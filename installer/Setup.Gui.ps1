<#
.SYNOPSIS
    ICEY 联机安装器 · 图形界面（双击 Setup.cmd 打开）

.DESCRIPTION
    只做三件事：收集游戏目录、展示进度、把动作交给 Installer.Core.ps1。
    安装/校验/卸载的全部事务逻辑（备份、原子写、回滚、哈希校验、独占锁、进程检查）
    仍然只有 Installer.Core.ps1 一份实现，本文件不复制任何安装逻辑。

    为什么不是 exe：不编译任何可执行文件，杀软看到的就是本目录里现有的
    纯文本 .cmd / .ps1，签名与文件集合与终端版完全一致，不新增误报面。

.PARAMETER SelfTest
    只构造窗口并自检控件（不显示、不安装），用于打包前的自动化冒烟测试。

.PARAMETER HeadlessAction
    走与界面完全相同的后台执行体，但不显示窗口；用于自动化验证"界面 → 引擎"这一段接线。

.PARAMETER GameRoot
    预填游戏目录（跳过自动探测）。

.PARAMETER NoAutoDetect
    不自动探测 Steam 库。
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [ValidateSet('Install','Verify','Uninstall')][string]$HeadlessAction,
    [string]$GameRoot,
    [string]$PackageRoot,
    [switch]$NoAutoDetect
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = $PSScriptRoot }
$corePath = Join-Path $PSScriptRoot 'Installer.Core.ps1'
if (-not (Test-Path -LiteralPath $corePath -PathType Leaf)) {
    throw "找不到 Installer.Core.ps1：$corePath（请完整解压压缩包后再运行）"
}

# 900 = 界面环境不可用，由 Setup.cmd 回退到终端模式
$GuiUnavailableCode = 900

try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
} catch {
    if ($SelfTest -or $HeadlessAction) { Write-Output "GUIUNAVAILABLE: $($_.Exception.Message)"; exit $GuiUnavailableCode }
    # 界面环境不可用（如受限会话）：自己拉起终端模式，别让用户双击后什么也看不到。
    try {
        $terminal = Join-Path $PSScriptRoot 'Setup.ps1'
        Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $terminal + '"'))
    } catch { }
    exit $GuiUnavailableCode
}

. $corePath

function Get-CoopGuiBuildId {
    param([string]$Root)
    try {
        $manifest = Read-CoopJsonFile (Join-Path $Root 'manifest.json') 'manifest.json' 2
        return [string](Get-CoopProperty $manifest 'buildId')
    } catch {
        return ''
    }
}

<#
    自动找游戏目录：Steam 注册表 → libraryfolders.vdf → 几个常见库路径。
    这里只做"猜一个候选填进输入框"，最终是否合法仍由引擎校验（缺 ICEY.exe 就直接拒绝）。
#>
function Find-CoopIceyRoots {
    $libs = New-Object System.Collections.Generic.List[string]
    $steam = $null
    foreach ($probe in @(
        @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
        @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' }
    )) {
        if ($null -ne $steam) { break }
        try {
            $value = (Get-ItemProperty -Path $probe.Path -Name $probe.Name -ErrorAction Stop).($probe.Name)
            if (-not [string]::IsNullOrWhiteSpace($value)) { $steam = [string]$value }
        } catch { }
    }
    if (-not [string]::IsNullOrWhiteSpace($steam)) {
        $libs.Add($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf -PathType Leaf) {
            try {
                $text = Get-Content -LiteralPath $vdf -Raw -Encoding UTF8
                foreach ($m in [regex]::Matches($text, '"path"\s*"([^"]+)"')) {
                    $libs.Add($m.Groups[1].Value.Replace('\\', '\'))
                }
            } catch { }
        }
    }
    foreach ($guess in @('C:\Program Files (x86)\Steam', 'C:\Program Files\Steam', 'D:\SteamLibrary', 'E:\SteamLibrary', 'C:\SteamLibrary')) {
        $libs.Add($guess)
    }
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($lib in $libs) {
        if ([string]::IsNullOrWhiteSpace($lib)) { continue }
        $candidate = Join-Path $lib 'steamapps\common\ICEY'
        if (Test-Path -LiteralPath (Join-Path $candidate 'ICEY.exe') -PathType Leaf) {
            $exists = $false
            foreach ($item in $found) { if ($item -ieq $candidate) { $exists = $true } }
            if (-not $exists) { $found.Add($candidate) }
        }
    }
    return $found
}

<#
    后台执行体：和界面按钮走完全相同的路径。
    之所以放进独立 runspace，是因为安装会连续写二十多个文件，
    在主线程同步调用会把窗口冻住（假死），用户会以为程序没反应。
    AfterWrite 回调把"已写完第 N 个"丢进队列，界面据此显示进度。
#>
$runnerScript = @'
param($CorePath, $Action, $GameRoot, $PackageRoot, $Queue)
try {
    . $CorePath
    $callback = { param($count) [void]$Queue.Enqueue([string]$count) }
    $r = Invoke-CoopAction -Action $Action -GameRoot $GameRoot -PackageRoot $PackageRoot -AfterWrite $callback
    $files = @()
    foreach ($f in @($r.files)) {
        $files += [pscustomobject]@{ path = [string]$f.path; status = [string]$f.status }
    }
    $payload = [pscustomobject]@{
        ok = $true
        action = [string]$r.action
        status = [string]$r.status
        buildId = [string]$r.buildId
        gameRoot = [string]$r.gameRoot
        backup = [string]$r.backup
        files = $files
    }
} catch {
    $code = 4
    $diag = ''
    if ($null -ne $_.Exception.Data -and $_.Exception.Data.Contains('ExitCode')) { $code = [int]$_.Exception.Data['ExitCode'] }
    if ($null -ne $_.Exception.Data -and $_.Exception.Data.Contains('Diagnostic')) { $diag = [string]$_.Exception.Data['Diagnostic'] }
    $payload = [pscustomobject]@{
        ok = $false
        exitCode = $code
        message = [string]$_.Exception.Message
        diagnostic = $diag
    }
}
$payload | ConvertTo-Json -Depth 5 -Compress
'@

$script:guiQueue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
$script:guiPowerShell = $null
$script:guiHandle = $null
$script:guiRunspace = $null
$script:guiAction = ''

function Start-CoopGuiRun {
    param([string]$Action, [string]$Root)
    $script:guiAction = $Action
    $scratch = ''
    while ($script:guiQueue.TryDequeue([ref]$scratch)) { }
    $rs = [runspacefactory]::CreateRunspace()
    $rs.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $rs
    [void]$ps.AddScript($runnerScript)
    [void]$ps.AddParameter('CorePath', $corePath)
    [void]$ps.AddParameter('Action', $Action)
    [void]$ps.AddParameter('GameRoot', $Root)
    [void]$ps.AddParameter('PackageRoot', $PackageRoot)
    [void]$ps.AddParameter('Queue', $script:guiQueue)
    $script:guiRunspace = $rs
    $script:guiPowerShell = $ps
    $script:guiHandle = $ps.BeginInvoke()
}

function Stop-CoopGuiRun {
    if ($null -ne $script:guiPowerShell) { try { $script:guiPowerShell.Dispose() } catch { } }
    if ($null -ne $script:guiRunspace) { try { $script:guiRunspace.Close(); $script:guiRunspace.Dispose() } catch { } }
    $script:guiPowerShell = $null
    $script:guiRunspace = $null
    $script:guiHandle = $null
}

function Read-CoopGuiResult {
    $raw = $script:guiPowerShell.EndInvoke($script:guiHandle)
    $json = $null
    foreach ($line in @($raw)) {
        $text = [string]$line
        if ($text.TrimStart().StartsWith('{')) { $json = $text }
    }
    Stop-CoopGuiRun
    if ([string]::IsNullOrWhiteSpace($json)) {
        return [pscustomobject]@{ ok = $false; exitCode = 4; message = '安装器没有返回结果（内部错误）'; diagnostic = '' }
    }
    try { return ($json | ConvertFrom-Json) } catch { return [pscustomobject]@{ ok = $false; exitCode = 4; message = "无法解析安装器输出：$($_.Exception.Message)"; diagnostic = $json } }
}

function Show-CoopGuiResult {
    param($Result)
    if ($Result.ok) {
        Add-CoopGuiLog ''
        Add-CoopGuiLog ("结果：{0} / {1}" -f $Result.action, $Result.status)
        if (-not [string]::IsNullOrWhiteSpace($Result.buildId)) { Add-CoopGuiLog ("构建：{0}" -f $Result.buildId) }
        if (-not [string]::IsNullOrWhiteSpace($Result.backup)) { Add-CoopGuiLog ("备份：{0}" -f $Result.backup) }
        $statuses = @{}
        foreach ($f in @($Result.files)) {
            $key = [string]$f.status
            if (-not $statuses.ContainsKey($key)) { $statuses[$key] = 0 }
            $statuses[$key] = $statuses[$key] + 1
        }
        foreach ($key in ($statuses.Keys | Sort-Object)) {
            Add-CoopGuiLog ("  {0}：{1} 个文件" -f $key, $statuses[$key])
        }
        if ($Result.action -eq 'Verify') { Add-CoopGuiLog '文件校验完成；这不代表已验证联机可玩。' }
        if ($Result.action -eq 'Install') { Add-CoopGuiLog '安装完成。请从 Steam 正常启动游戏，在主菜单右下角点击"进入联机"。' }
        if ($Result.action -eq 'Uninstall') { Add-CoopGuiLog '卸载完成。合作存档与日志仍保留在游戏目录 BepInEx\icey-coop 下。' }
    } else {
        Add-CoopGuiLog ''
        Add-CoopGuiLog ("失败（退出码 {0}）：{1}" -f $Result.exitCode, $Result.message) $true
        if (-not [string]::IsNullOrWhiteSpace($Result.diagnostic)) { Add-CoopGuiLog ([string]$Result.diagnostic) $true }
        Add-CoopGuiLog '提示：确认游戏未在运行、目录里能直接看到 ICEY.exe、且当前用户对游戏目录有写权限。'
    }
}

# ---------------------------------------------------------------- 无界面模式
if ($HeadlessAction) {
    $root = $GameRoot
    if ([string]::IsNullOrWhiteSpace($root)) { Write-Output 'HEADLESS_FAIL: 需要 -GameRoot'; exit 2 }
    Start-CoopGuiRun $HeadlessAction $root
    $script:guiHandle.AsyncWaitHandle.WaitOne() | Out-Null
    $result = Read-CoopGuiResult
    if ($result.ok) { Write-Output ("HEADLESS_OK {0}/{1} files={2}" -f $result.action, $result.status, @($result.files).Count); exit 0 }
    Write-Output ("HEADLESS_FAIL {0}: {1}" -f $result.exitCode, $result.message)
    exit 3
}

# ---------------------------------------------------------------- 界面
[System.Windows.Forms.Application]::EnableVisualStyles()
$uiFont = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
$logFont = New-Object System.Drawing.Font('Consolas', 8.5)

$form = New-Object System.Windows.Forms.Form
$form.Text = 'ICEY 双人联机 · 安装器'
$form.ClientSize = New-Object System.Drawing.Size(660, 470)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false
$form.Font = $uiFont

$labelPath = New-Object System.Windows.Forms.Label
$labelPath.Text = '游戏目录（这个文件夹里应该能直接看到 ICEY.exe）'
$labelPath.Location = New-Object System.Drawing.Point(14, 12)
$labelPath.Size = New-Object System.Drawing.Size(520, 20)
$form.Controls.Add($labelPath)

$txtPath = New-Object System.Windows.Forms.TextBox
$txtPath.Location = New-Object System.Drawing.Point(14, 34)
$txtPath.Size = New-Object System.Drawing.Size(520, 24)
$form.Controls.Add($txtPath)

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Text = '浏览…'
$btnBrowse.Location = New-Object System.Drawing.Point(542, 33)
$btnBrowse.Size = New-Object System.Drawing.Size(100, 26)
$form.Controls.Add($btnBrowse)

$group = New-Object System.Windows.Forms.GroupBox
$group.Text = '操作'
$group.Location = New-Object System.Drawing.Point(14, 70)
$group.Size = New-Object System.Drawing.Size(628, 66)
$form.Controls.Add($group)

$btnInstall = New-Object System.Windows.Forms.Button
$btnInstall.Text = '安装 / 更新'
$btnInstall.Location = New-Object System.Drawing.Point(14, 24)
$btnInstall.Size = New-Object System.Drawing.Size(140, 30)
$group.Controls.Add($btnInstall)

$btnVerify = New-Object System.Windows.Forms.Button
$btnVerify.Text = '校验文件'
$btnVerify.Location = New-Object System.Drawing.Point(166, 24)
$btnVerify.Size = New-Object System.Drawing.Size(140, 30)
$group.Controls.Add($btnVerify)

$btnUninstall = New-Object System.Windows.Forms.Button
$btnUninstall.Text = '卸载'
$btnUninstall.Location = New-Object System.Drawing.Point(318, 24)
$btnUninstall.Size = New-Object System.Drawing.Size(140, 30)
$group.Controls.Add($btnUninstall)

$labelBuild = New-Object System.Windows.Forms.Label
$labelBuild.Location = New-Object System.Drawing.Point(470, 30)
$labelBuild.Size = New-Object System.Drawing.Size(150, 18)
$labelBuild.ForeColor = [System.Drawing.Color]::DimGray
$group.Controls.Add($labelBuild)

$txtLog = New-Object System.Windows.Forms.TextBox
$txtLog.Location = New-Object System.Drawing.Point(14, 146)
$txtLog.Size = New-Object System.Drawing.Size(628, 268)
$txtLog.Multiline = $true
$txtLog.ReadOnly = $true
$txtLog.ScrollBars = 'Vertical'
$txtLog.WordWrap = $true
$txtLog.BackColor = [System.Drawing.Color]::White
$txtLog.Font = $logFont
$form.Controls.Add($txtLog)

$labelStatus = New-Object System.Windows.Forms.Label
$labelStatus.Location = New-Object System.Drawing.Point(14, 420)
$labelStatus.Size = New-Object System.Drawing.Size(470, 18)
$labelStatus.Text = '就绪'
$labelStatus.ForeColor = [System.Drawing.Color]::DimGray
$form.Controls.Add($labelStatus)

$progress = New-Object System.Windows.Forms.ProgressBar
$progress.Location = New-Object System.Drawing.Point(14, 440)
$progress.Size = New-Object System.Drawing.Size(628, 14)
$progress.Minimum = 0
$progress.Maximum = 100
$form.Controls.Add($progress)

function Add-CoopGuiLog {
    param([string]$Text, [bool]$IsError = $false)
    if ($null -eq $Text) { $Text = '' }
    if ($IsError) {
        $txtLog.SelectionStart = $txtLog.TextLength
        $txtLog.SelectionLength = 0
        $txtLog.SelectionColor = [System.Drawing.Color]::Firebrick
        $txtLog.AppendText($Text + [Environment]::NewLine)
        $txtLog.SelectionColor = $txtLog.ForeColor
    } else {
        $txtLog.AppendText($Text + [Environment]::NewLine)
    }
    $txtLog.SelectionStart = $txtLog.TextLength
    $txtLog.ScrollToCaret()
}

function Set-CoopGuiBusy {
    param([bool]$Busy)
    $btnInstall.Enabled = -not $Busy
    $btnVerify.Enabled = -not $Busy
    $btnUninstall.Enabled = -not $Busy
    $btnBrowse.Enabled = -not $Busy
    $txtPath.Enabled = -not $Busy
    if ($Busy) { $progress.Style = 'Marquee' } else { $progress.Style = 'Blocks'; $progress.Value = 0 }
}

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 200
$timer.Add_Tick({
    $pending = 0
    $last = ''
    $value = ''
    while ($script:guiQueue.TryDequeue([ref]$value)) { $pending = $pending + 1; $last = $value }
    if ($pending -gt 0) {
        $labelStatus.Text = ('正在写入第 {0} 个文件…' -f $last)
    }
    if ($null -ne $script:guiHandle -and $script:guiHandle.IsCompleted) {
        $timer.Stop()
        $result = Read-CoopGuiResult
        Show-CoopGuiResult $result
        Set-CoopGuiBusy $false
        if ($result.ok) { $labelStatus.Text = ('完成：{0} / {1}' -f $result.action, $result.status) }
        else { $labelStatus.Text = ('失败（退出码 {0}）' -f $result.exitCode) }
    }
})

function Invoke-CoopGuiAction {
    param([string]$Action, [string]$Label)
    $root = $txtPath.Text.Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($root)) {
        [System.Windows.Forms.MessageBox]::Show('请先选择 ICEY 游戏目录（里面能直接看到 ICEY.exe）。', '缺少游戏目录', 'OK', 'Warning') | Out-Null
        return
    }
    if ($Action -eq 'Uninstall') {
        $answer = [System.Windows.Forms.MessageBox]::Show(
            "将从下面这个目录移除联机模组，并还原安装前的原始文件：`r`n`r`n$root`r`n`r`n合作存档、日志、最近地址会保留。继续吗？",
            '确认卸载', 'YesNo', 'Question')
        if ($answer -ne 'Yes') { return }
    }
    Add-CoopGuiLog ''
    Add-CoopGuiLog ('开始 {0}：{1}' -f $Label, $root)
    Set-CoopGuiBusy $true
    $labelStatus.Text = ('{0}中…' -f $Label)
    try {
        Start-CoopGuiRun $Action $root
        $timer.Start()
    } catch {
        Set-CoopGuiBusy $false
        Add-CoopGuiLog ('无法启动安装器：{0}' -f $_.Exception.Message) $true
        $labelStatus.Text = '启动失败'
    }
}

$btnBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = '选择 ICEY 游戏目录（里面能直接看到 ICEY.exe）'
    $dialog.ShowNewFolderButton = $false
    if (-not [string]::IsNullOrWhiteSpace($txtPath.Text) -and (Test-Path -LiteralPath $txtPath.Text.Trim('"'))) {
        $dialog.SelectedPath = $txtPath.Text.Trim('"')
    }
    if ($dialog.ShowDialog() -eq 'OK') { $txtPath.Text = $dialog.SelectedPath }
})

$btnInstall.Add_Click({ Invoke-CoopGuiAction 'Install' '安装' })
$btnVerify.Add_Click({ Invoke-CoopGuiAction 'Verify' '校验' })
$btnUninstall.Add_Click({ Invoke-CoopGuiAction 'Uninstall' '卸载' })

$form.Add_FormClosing({
    if ($null -ne $script:guiHandle -and -not $script:guiHandle.IsCompleted) {
        $answer = [System.Windows.Forms.MessageBox]::Show('安装操作还在进行中，现在关闭可能留下未完成的事务。确定关闭吗？', '操作进行中', 'YesNo', 'Warning')
        if ($answer -ne 'Yes') { $_.Cancel = $true }
    }
})

# 初始化内容
$buildId = Get-CoopGuiBuildId $PackageRoot
if ([string]::IsNullOrWhiteSpace($buildId)) { $labelBuild.Text = '构建：未知' } else { $labelBuild.Text = ('构建：{0}' -f $buildId) }

Add-CoopGuiLog 'ICEY 双人联机 · 安装器'
Add-CoopGuiLog '1. 先正常退出游戏；2. 选择游戏目录；3. 点"安装 / 更新"；4. 从 Steam 启动游戏，在主菜单右下角进入联机。'
Add-CoopGuiLog '安装器不会启动或关闭游戏，也不会修改 Steam、防火墙或联机软件的设置。'
Add-CoopGuiLog ''

if (-not [string]::IsNullOrWhiteSpace($GameRoot)) {
    $txtPath.Text = $GameRoot
} elseif (-not $NoAutoDetect) {
    $roots = Find-CoopIceyRoots
    if (@($roots).Count -ge 1) {
        $txtPath.Text = [string]$roots[0]
        if (@($roots).Count -gt 1) {
            Add-CoopGuiLog ('检测到多个 ICEY 安装：{0}（已填第一个，若不对请点"浏览…"）' -f (@($roots) -join '；'))
        } else {
            Add-CoopGuiLog ('已自动找到 ICEY：{0}' -f $txtPath.Text)
        }
    } else {
        Add-CoopGuiLog '没有自动找到 ICEY 目录，请点"浏览…"手动选择（别选错成 Steam 根目录）。'
    }
}

if ($SelfTest) {
    $checks = @(
        @{ Name = '窗口标题'; Ok = ($form.Text.Length -gt 0) },
        @{ Name = '路径输入框'; Ok = ($txtPath -ne $null -and $txtPath.Width -gt 100) },
        @{ Name = '浏览按钮'; Ok = ($btnBrowse -ne $null) },
        @{ Name = '安装按钮'; Ok = ($btnInstall -ne $null) },
        @{ Name = '校验按钮'; Ok = ($btnVerify -ne $null) },
        @{ Name = '卸载按钮'; Ok = ($btnUninstall -ne $null) },
        @{ Name = '日志框'; Ok = ($txtLog.Multiline -and $txtLog.ReadOnly) },
        @{ Name = '进度条'; Ok = ($progress -ne $null) },
        @{ Name = '引擎已加载'; Ok = (@(Get-Command -Name 'Invoke-CoopAction' -ErrorAction SilentlyContinue).Count -eq 1) },
        @{ Name = '构建号'; Ok = ((Get-CoopGuiBuildId $PackageRoot).Length -ge 8) },
        @{ Name = 'Steam 探测可运行'; Ok = ($null -ne (Find-CoopIceyRoots)) }
    )
    $failed = @($checks | Where-Object { -not $_.Ok })
    foreach ($c in $checks) { Write-Output ("  [{0}] {1}" -f $(if ($c.Ok) { 'PASS' } else { 'FAIL' }), $c.Name) }
    if ($failed.Count -eq 0) { Write-Output 'SELFTEST_OK'; $form.Dispose(); exit 0 }
    Write-Output ("SELFTEST_FAIL {0}" -f ($failed.Name -join ','))
    $form.Dispose()
    exit 5
}

try {
    $form.ShowIcon = $false
    [void]$form.ShowDialog()
} finally {
    $timer.Stop()
    $timer.Dispose()
    Stop-CoopGuiRun
    $form.Dispose()
}
exit 0
