<#
.SYNOPSIS
    ICEY 联机安装器 · 现代网页界面（本地单页 + 本地回环服务）

.DESCRIPTION
    双击 Setup.cmd 后：
      1. 本脚本在 127.0.0.1 上起一个随机端口的最小 HTTP 服务（只用 TcpListener，不需要 URL ACL / 管理员）；
      2. 用默认浏览器打开 Setup.ui.html（Edge/Chrome 渲染，真正的现代 CSS）；
      3. 页面上选目录、点按钮，前端通过 /api/* 与本脚本通信；
      4. 安装逻辑仍然只调 Installer.Core.ps1，本文件不复制任何安装逻辑。

    为什么不是 exe：不编译任何可执行文件，包里只有 .cmd / .ps1 / .html 纯文本。
    为什么不是 HTA：HTA 用的是老 IE 内核，做不出现代界面。

.PARAMETER SelfTest
    只做自检（引擎可加载、清单可读、服务可起、页面可取），不打开浏览器。
#>
[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string]$PackageRoot,
    [int]$Port = 0,
    [switch]$NoBrowser
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = $PSScriptRoot }
$corePath = Join-Path $PSScriptRoot 'Installer.Core.ps1'
$uiPath = Join-Path $PSScriptRoot 'Setup.ui.html'
if (-not (Test-Path -LiteralPath $corePath -PathType Leaf)) { Write-Output "找不到 Installer.Core.ps1：$corePath"; exit 900 }
if (-not (Test-Path -LiteralPath $uiPath -PathType Leaf)) { Write-Output "找不到 Setup.ui.html：$uiPath（请完整解压压缩包）"; exit 900 }

. $corePath

$script:uiHtml = [System.IO.File]::ReadAllText($uiPath, [System.Text.Encoding]::UTF8)
$script:buildId = ''
try {
    $manifest = Read-CoopJsonFile (Join-Path $PackageRoot 'manifest.json') 'manifest.json' 2
    $script:buildId = [string](Get-CoopProperty $manifest 'buildId')
    $script:totalFiles = @(Get-CoopProperty $manifest 'files').Count
} catch {
    $script:totalFiles = 23
}
if ($script:totalFiles -le 0) { $script:totalFiles = 23 }

# ------------------------------------------------------------------ 状态
$token = [guid]::NewGuid().ToString('N')
$script:state = [pscustomobject]@{
    running = $false
    finished = $false
    ok = $false
    action = ''
    status = ''
    exitCode = 0
    written = 0
    total = $script:totalFiles
    lines = New-Object System.Collections.ArrayList
    reported = $false
    closeRequested = $false
    lastHeartbeat = 0
    sawHeartbeat = $false
}
$script:logQueue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
$script:progressQueue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
$script:detectCache = $null
$script:detectTime = Get-Date
$script:ps = $null
$script:rs = $null
$script:handle = $null

function Add-UiLog {
    param([string]$Text, [string]$Class = 'i')
    [void]$script:state.lines.Add([pscustomobject]@{ text = $Text; cls = $Class })
}

function Get-CoopWebDetect {
    if ($null -ne $script:detectCache -and ((Get-Date) - $script:detectTime).TotalSeconds -lt 5) { return $script:detectCache }
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
                foreach ($m in [regex]::Matches($text, '"path"\s*"([^"]+)"')) { $libs.Add($m.Groups[1].Value.Replace('\\', '\')) }
            } catch { }
        }
    }
    foreach ($guess in @('C:\Program Files (x86)\Steam', 'C:\Program Files\Steam', 'D:\SteamLibrary', 'E:\SteamLibrary', 'C:\SteamLibrary')) { $libs.Add($guess) }

    $result = New-Object System.Collections.ArrayList
    $seen = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    foreach ($lib in $libs) {
        if ([string]::IsNullOrWhiteSpace($lib)) { continue }
        $candidate = Join-Path $lib 'steamapps\common\ICEY'
        if (-not (Test-Path -LiteralPath (Join-Path $candidate 'ICEY.exe') -PathType Leaf)) { continue }
        if (-not $seen.Add($candidate)) { continue }
        $installed = Test-Path -LiteralPath (Join-Path $candidate 'BepInEx\icey-coop\installer-state.json') -PathType Leaf
        $label = Split-Path (Split-Path (Split-Path $candidate -Parent) -Parent) -Parent
        if ([string]::IsNullOrWhiteSpace($label)) { $label = $lib }
        [void]$result.Add([pscustomobject]@{ label = [string]$label; path = $candidate; installed = [bool]$installed })
    }
    $script:detectCache = @($result)
    $script:detectTime = Get-Date
    return $script:detectCache
}

$script:runnerScript = @'
param($CorePath, $Action, $GameRoot, $PackageRoot, $LogQueue, $ProgressQueue)
try {
    . $CorePath
    $callback = { param($count) [void]$ProgressQueue.Enqueue([string]$count) }
    $r = Invoke-CoopAction -Action $Action -GameRoot $GameRoot -PackageRoot $PackageRoot -AfterWrite $callback
    $files = @()
    foreach ($f in @($r.files)) { $files += [pscustomobject]@{ path = [string]$f.path; status = [string]$f.status } }
    $payload = [pscustomobject]@{
        ok = $true; action = [string]$r.action; status = [string]$r.status
        buildId = [string]$r.buildId; gameRoot = [string]$r.gameRoot
        backup = [string]$r.backup; files = $files
    }
} catch {
    $code = 4; $diag = ''
    if ($null -ne $_.Exception.Data -and $_.Exception.Data.Contains('ExitCode')) { $code = [int]$_.Exception.Data['ExitCode'] }
    if ($null -ne $_.Exception.Data -and $_.Exception.Data.Contains('Diagnostic')) { $diag = [string]$_.Exception.Data['Diagnostic'] }
    $payload = [pscustomobject]@{ ok = $false; exitCode = $code; message = [string]$_.Exception.Message; diagnostic = $diag }
}
$payload | ConvertTo-Json -Depth 5 -Compress
'@

function Start-CoOpWebAction {
    param([string]$Action, [string]$Root)
    $drain = ''
    while ($script:logQueue.TryDequeue([ref]$drain)) { }
    $script:state.running = $true
    $script:state.finished = $false
    $script:state.ok = $false
    $script:state.reported = $false
    $script:state.action = $Action
    $script:state.status = ''
    $script:state.exitCode = 0
    $script:state.written = 0
    $script:state.lines.Clear()

    $script:progressQueue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
    $rs = [runspacefactory]::CreateRunspace()
    $rs.Open()
    $ps = [powershell]::Create()
    $ps.Runspace = $rs
    [void]$ps.AddScript($script:runnerScript)
    [void]$ps.AddParameter('CorePath', $corePath)
    [void]$ps.AddParameter('Action', $Action)
    [void]$ps.AddParameter('GameRoot', $Root)
    [void]$ps.AddParameter('PackageRoot', $PackageRoot)
    [void]$ps.AddParameter('LogQueue', $script:logQueue)
    [void]$ps.AddParameter('ProgressQueue', $script:progressQueue)
    $script:rs = $rs
    $script:ps = $ps
    $script:handle = $ps.BeginInvoke()

    $verb = @{ Install = '安装'; Verify = '校验'; Uninstall = '卸载' }[$Action]
    Add-UiLog ("开始{0}：{1}" -f $verb, $Root) 'sys'
}

function Update-CoOpWebProgress {
    if (-not $script:state.running) { return }
    $value = ''
    while ($script:progressQueue.TryDequeue([ref]$value)) {
        $n = 0
        if ([int]::TryParse($value, [ref]$n)) {
            $script:state.written = $n
            if ($script:state.action -ne 'Verify') { Add-UiLog ("写入第 {0} 个文件" -f $n) 'dim' }
        }
    }
    if ($null -ne $script:handle -and $script:handle.IsCompleted) {
        $raw = $null
        try { $raw = $script:ps.EndInvoke($script:handle) } catch { $raw = $null }
        try { $script:ps.Dispose() } catch { }
        try { $script:rs.Close(); $script:rs.Dispose() } catch { }
        $script:ps = $null; $script:rs = $null; $script:handle = $null
        $script:state.running = $false
        $script:state.finished = $true
        $json = $null
        foreach ($line in @($raw)) {
            $text = [string]$line
            if ($text.TrimStart().StartsWith('{')) { $json = $text }
        }
        if ([string]::IsNullOrWhiteSpace($json)) {
            $script:state.ok = $false
            $script:state.exitCode = 4
            Add-UiLog '安装器没有返回结果（内部错误）' 'err'
            return
        }
        try { $res = $json | ConvertFrom-Json } catch {
            $script:state.ok = $false
            $script:state.exitCode = 4
            Add-UiLog ('无法解析安装器输出：' + $_.Exception.Message) 'err'
            return
        }
        if ($res.ok) {
            $script:state.ok = $true
            $script:state.status = [string]$res.status
            # 校验不写文件，AfterWrite 永远不回调；否则界面会停在 "0 / 23" 却显示"校验通过"。
            $checkedCount = @($res.files).Count
            if ($checkedCount -gt 0) {
                $script:state.total = $checkedCount
                if ($res.action -eq 'Verify' -or $script:state.written -eq 0) { $script:state.written = $checkedCount }
            }
            Add-UiLog ('结果：{0} / {1}' -f $res.action, $res.status) 'ok'
            if ($res.buildId) { Add-UiLog ('构建：' + $res.buildId) 'dim' }
            if ($res.backup) { Add-UiLog ('备份：' + $res.backup) 'dim' }
            $counts = @{}
            foreach ($f in @($res.files)) {
                $key = [string]$f.status
                if (-not $counts.ContainsKey($key)) { $counts[$key] = 0 }
                $counts[$key] = $counts[$key] + 1
            }
            foreach ($key in ($counts.Keys | Sort-Object)) { Add-UiLog ('  ' + $key + '：' + $counts[$key] + ' 个文件') 'dim' }
            if ($res.action -eq 'Verify') { Add-UiLog '文件校验完成；这不代表已验证联机可玩。' 'warn' }
            if ($res.action -eq 'Uninstall') { Add-UiLog '卸载完成。合作存档与日志仍保留在 BepInEx\icey-coop。' 'i' }
        } else {
            $script:state.ok = $false
            $script:state.exitCode = [int]$res.exitCode
            Add-UiLog ('失败（退出码 {0}）：{1}' -f $res.exitCode, $res.message) 'err'
            if ($res.diagnostic) { Add-UiLog ([string]$res.diagnostic) 'err' }
            Add-UiLog '提示：确认游戏未在运行、目录里能直接看到 ICEY.exe、且当前用户对该目录有写权限。' 'warn'
        }
    }
}

# ------------------------------------------------------------------ HTTP
function Send-Http {
    param($Stream, [string]$Status, [string]$ContentType, [string]$Body)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $head = "HTTP/1.1 $Status`r`nContent-Type: $ContentType; charset=utf-8`r`nContent-Length: $($bytes.Length)`r`nCache-Control: no-store`r`nX-Content-Type-Options: nosniff`r`nConnection: close`r`n`r`n"
    $hb = [System.Text.Encoding]::ASCII.GetBytes($head)
    $Stream.Write($hb, 0, $hb.Length)
    if ($bytes.Length -gt 0) { $Stream.Write($bytes, 0, $bytes.Length) }
    $Stream.Flush()
}
function Send-Json { param($Stream, $Object, [string]$Status = '200 OK'); Send-Http $Stream $Status 'application/json' ($Object | ConvertTo-Json -Depth 6 -Compress) }

function Read-HttpLine {
    param([System.IO.Stream]$Stream, [int]$MaxBytes = 8192)
    $bytes = New-Object 'System.Collections.Generic.List[byte]'
    while ($bytes.Count -le $MaxBytes) {
        $value = $Stream.ReadByte()
        if ($value -lt 0) {
            if ($bytes.Count -eq 0) { return $null }
            throw (New-Object System.IO.InvalidDataException 'incomplete HTTP line')
        }
        if ($value -eq 10) {
            if ($bytes.Count -gt 0 -and $bytes[$bytes.Count - 1] -eq 13) { $bytes.RemoveAt($bytes.Count - 1) }
            return [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
        }
        $bytes.Add([byte]$value)
    }
    throw (New-Object System.IO.InvalidDataException 'HTTP line too long')
}

function Read-HttpRequest {
    param([System.IO.Stream]$Stream)
    $requestLine = Read-HttpLine $Stream 4096
    if ([string]::IsNullOrWhiteSpace($requestLine)) { throw (New-Object System.IO.InvalidDataException 'missing request line') }
    $headerBytes = 0
    $headers = @{}
    while ($true) {
        $line = Read-HttpLine $Stream 8192
        if ($null -eq $line) { throw (New-Object System.IO.InvalidDataException 'incomplete HTTP headers') }
        $headerBytes += $line.Length + 2
        if ($headerBytes -gt 16384) { throw (New-Object System.IO.InvalidDataException 'HTTP headers too large') }
        if ($line.Length -eq 0) { break }
        $colon = $line.IndexOf(':')
        if ($colon -le 0) { throw (New-Object System.IO.InvalidDataException 'malformed HTTP header') }
        $name = $line.Substring(0, $colon).Trim().ToLowerInvariant()
        $value = $line.Substring($colon + 1).Trim()
        if ($headers.ContainsKey($name)) { throw (New-Object System.IO.InvalidDataException 'duplicate HTTP header') }
        $headers[$name] = $value
    }
    if ($headers.ContainsKey('transfer-encoding') -and $headers['transfer-encoding'] -ne 'identity') {
        throw (New-Object System.IO.InvalidDataException 'transfer encoding is unsupported')
    }
    $contentLength = 0
    if ($headers.ContainsKey('content-length')) {
        $parsedLength = [long]0
        if ($headers['content-length'] -notmatch '^\d+$' -or
            -not [long]::TryParse($headers['content-length'], [ref]$parsedLength) -or $parsedLength -gt 1048576) {
            throw (New-Object System.IO.InvalidDataException 'invalid or oversized content length')
        }
        $contentLength = [int]$parsedLength
    }
    $bodyBytes = New-Object byte[] $contentLength
    $read = 0
    while ($read -lt $contentLength) {
        $n = $Stream.Read($bodyBytes, $read, $contentLength - $read)
        if ($n -le 0) { throw (New-Object System.IO.InvalidDataException 'incomplete HTTP body') }
        $read += $n
    }
    $encoding = New-Object System.Text.UTF8Encoding($false, $true)
    $body = $encoding.GetString($bodyBytes)
    $parts = $requestLine.Split(' ')
    if ($parts.Length -ne 3 -or $parts[2] -notmatch '^HTTP/1\.[01]$') { throw (New-Object System.IO.InvalidDataException 'malformed HTTP request line') }
    return [pscustomobject]@{ Method = $parts[0].ToUpperInvariant(); RawPath = $parts[1]; Headers = $headers; Body = $body }
}

$listener = $null
for ($attempt = 0; $attempt -lt 12; $attempt++) {
    $tryPort = if ($Port -gt 0) { $Port } else { Get-Random -Minimum 39000 -Maximum 49000 }
    try {
        $candidate = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $tryPort)
        $candidate.Start()
        $listener = $candidate
        $actualPort = $tryPort
        break
    } catch { $listener = $null }
}
if ($null -eq $listener) { Write-Output '无法在 127.0.0.1 上绑定本地端口（可能被安全软件拦截），改用备用界面'; exit 900 }

$url = "http://127.0.0.1:$actualPort/?t=$token"
$script:logQueue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
$script:progressQueue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'

if ($SelfTest) {
    $checks = @()
    $checks += [pscustomobject]@{ name = '引擎可加载'; ok = (@(Get-Command -Name Invoke-CoopAction -ErrorAction SilentlyContinue).Count -eq 1) }
    $checks += [pscustomobject]@{ name = '页面存在'; ok = ($script:uiHtml.Length -gt 4000) }
    # 断"能力"而不是断"我的上一版怎么写"：样式形态交给设计，页面必须能驱动后端契约。
    $styleOk = ($script:uiHtml -match '<style') -and ($script:uiHtml -match '<script')
    $controlsOk = ($script:uiHtml -match 'id="gamePath"') -and ($script:uiHtml -match 'id="install"') -and
                  ($script:uiHtml -match 'id="verify"') -and ($script:uiHtml -match 'id="uninstall"') -and
                  ($script:uiHtml -match 'id="logs"')
    $apiOk = ($script:uiHtml -match '/api/state') -and ($script:uiHtml -match '/api/action')
    $offlineOk = -not ($script:uiHtml -match 'https?://(?!127\.0\.0\.1|localhost)') -and -not ($script:uiHtml -match '@import')
    $checks += [pscustomobject]@{ name = '样式与脚本内联'; ok = $styleOk }
    $checks += [pscustomobject]@{ name = '必需控件齐全'; ok = $controlsOk }
    $checks += [pscustomobject]@{ name = '接口契约存在'; ok = $apiOk }
    $checks += [pscustomobject]@{ name = '无外部请求'; ok = $offlineOk }
    $checks += [pscustomobject]@{ name = '清单构建号'; ok = ($script:buildId.Length -ge 8) }
    $checks += [pscustomobject]@{ name = 'payload 文件数'; ok = ($script:totalFiles -ge 20) }
    $checks += [pscustomobject]@{ name = '服务已绑定'; ok = ($null -ne $listener) }
    $checks += [pscustomobject]@{ name = 'Steam 探测可运行'; ok = ($null -ne (Get-CoopWebDetect)) }
    foreach ($c in $checks) { Write-Output ("  [{0}] {1}" -f $(if ($c.ok) { 'PASS' } else { 'FAIL' }), $c.name) }
    $failed = @($checks | Where-Object { -not $_.ok })
    if ($failed.Count -eq 0) { Write-Output 'SELFTEST_OK'; $listener.Stop(); exit 0 }
    Write-Output ('SELFTEST_FAIL ' + (($failed | ForEach-Object { $_.name }) -join ','))
    $listener.Stop()
    exit 5
}

# 优先用「应用窗口」模式：没有地址栏、没有标签栏，看起来就是一个独立程序，
# 而不是"装个东西还要开网页"。Edge 在 Win10/11 上一定存在，Chrome 作备选；
# 都不可用才退回默认浏览器（会有地址栏，但至少能用）。
function Open-CoOpUiWindow {
    param([string]$Address)
    $candidates = @(
        'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files\Google\Chrome\Application\chrome.exe',
        'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe'
    )
    foreach ($exe in $candidates) {
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { continue }
        try {
            Start-Process -FilePath $exe -ArgumentList @(
                ('--app=' + $Address),
                '--window-size=1120,800',
                '--window-position=120,80',
                '--no-first-run',
                '--no-default-browser-check',
                '--disable-features=Translate,msEdgeTranslate',
                '--user-data-dir=' + (Join-Path $env:LOCALAPPDATA 'ICEYCoopSetup\window')
            ) | Out-Null
            Write-Output ('界面窗口：' + (Split-Path $exe -Leaf) + '（应用窗口模式）')
            return $true
        } catch {
            continue
        }
    }
    return $false
}

if (-not $NoBrowser) {
    $opened = $false
    try { $opened = Open-CoOpUiWindow $url } catch { $opened = $false }
    if (-not $opened) {
        try { Start-Process $url | Out-Null; Write-Output '界面窗口：默认浏览器（普通标签页）' } catch {
            Write-Output "无法打开浏览器：$($_.Exception.Message)"
            Write-Output "请手动访问：$url"
        }
    }
}
Write-Output "ICEY 联机安装器已启动：$url"
Write-Output '关闭浏览器页面即退出（或按 Esc）。'

$idleLimit = 25
$bootLimit = 120
$startedAt = Get-Date
try {
    while ($true) {
        if ($listener.Pending()) {
            $client = $listener.AcceptTcpClient()
            $client.NoDelay = $true
            $client.ReceiveTimeout = 3000
            $client.SendTimeout = 3000
            $stream = $client.GetStream()
            $stream.ReadTimeout = 3000
            $stream.WriteTimeout = 3000
            try {
                $request = Read-HttpRequest $stream
                $method = $request.Method
                $rawPath = $request.RawPath
                $body = $request.Body
                $pathOnly = ($rawPath -split '\?')[0]
                $queryString = if ($rawPath.Contains('?')) { $rawPath.Substring($rawPath.IndexOf('?') + 1) } else { '' }
                $headerToken = if ($request.Headers.ContainsKey('x-coop-token')) { $request.Headers['x-coop-token'] } else { '' }
                $authorized = $headerToken -eq $token

                switch -Wildcard ($pathOnly) {
                    '/' { Send-Http $stream '200 OK' 'text/html' $script:uiHtml; break }
                    '/index.html' { Send-Http $stream '200 OK' 'text/html' $script:uiHtml; break }
                    '/favicon.ico' { Send-Http $stream '204 No Content' 'text/plain' ''; break }
                    '/api/state' {
                        if (-not $authorized) { Send-Json $stream @{ ok = $false; error = 'unauthorized' } '403 Forbidden'; break }
                        $script:state.sawHeartbeat = $true
                        $script:state.lastHeartbeat = (Get-Date)
                        Send-Json $stream ([pscustomobject]@{
                            ok = $true
                            buildId = $script:buildId
                            running = $script:state.running
                            finished = $script:state.finished
                            reported = $script:state.reported
                            resultOk = $script:state.ok
                            action = $script:state.action
                            status = $script:state.status
                            exitCode = $script:state.exitCode
                            written = $script:state.written
                            total = $script:state.total
                            detect = @(Get-CoopWebDetect)
                            installedPath = ''
                            lines = @($script:state.lines)
                        })
                        $script:state.lines.Clear()
                        break
                    }
                    '/api/heartbeat' {
                        if ($authorized) { $script:state.sawHeartbeat = $true; $script:state.lastHeartbeat = (Get-Date) }
                        Send-Json $stream @{ ok = $true }
                        break
                    }
                    '/api/ack' {
                        if (-not $authorized) { Send-Json $stream @{ ok = $false; error = 'unauthorized' } '403 Forbidden'; break }
                        if ($method -ne 'POST') { Send-Json $stream @{ ok = $false; error = 'method not allowed' } '405 Method Not Allowed'; break }
                        $script:state.reported = $true
                        Send-Json $stream @{ ok = $true }
                        break
                    }
                    '/api/close' {
                        if (-not $authorized) { Send-Json $stream @{ ok = $false; error = 'unauthorized' } '403 Forbidden'; break }
                        if ($method -ne 'POST') { Send-Json $stream @{ ok = $false; error = 'method not allowed' } '405 Method Not Allowed'; break }
                        $script:state.closeRequested = $true
                        Send-Json $stream @{ ok = $true }
                        break
                    }
                    '/api/check' {
                        if (-not $authorized) { Send-Json $stream @{ ok = $false; error = 'unauthorized' } '403 Forbidden'; break }
                        $p = ''
                        if ($queryString -match '(?:^|&)path=([^&]+)') { $p = [System.Uri]::UnescapeDataString($matches[1]) }
                        if ([string]::IsNullOrWhiteSpace($p)) { Send-Json $stream @{ ok = $false; error = '路径为空' }; break }
                        $exe = Join-Path $p 'ICEY.exe'
                        if (Test-Path -LiteralPath $exe -PathType Leaf) { Send-Json $stream @{ ok = $true } }
                        else { Send-Json $stream @{ ok = $false; error = '这个目录里没有 ICEY.exe' } }
                        break
                    }
                    '/api/browse' {
                        if (-not $authorized) { Send-Json $stream @{ ok = $false; error = 'unauthorized' } '403 Forbidden'; break }
                        $picked = ''
                        try {
                            Add-Type -AssemblyName System.Windows.Forms
                            $owner = New-Object System.Windows.Forms.Form
                            $owner.TopMost = $true
                            $owner.ShowInTaskbar = $false
                            $owner.FormBorderStyle = 'None'
                            $owner.Size = New-Object System.Drawing.Size(1, 1)
                            $owner.StartPosition = 'CenterScreen'
                            $owner.Show()
                            $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
                            $dlg.Description = '选择 ICEY 游戏目录（里面能直接看到 ICEY.exe）'
                            $dlg.ShowNewFolderButton = $false
                            if ($dlg.ShowDialog($owner) -eq 'OK') { $picked = $dlg.SelectedPath }
                            $owner.Close()
                            $owner.Dispose()
                        } catch { $picked = '' }
                        if ([string]::IsNullOrWhiteSpace($picked)) { Send-Json $stream @{ ok = $false; error = '已取消' } }
                        else { Send-Json $stream @{ ok = $true; path = $picked } }
                        break
                    }
                    '/api/action' {
                        if (-not $authorized) { Send-Json $stream @{ ok = $false; error = 'unauthorized' } '403 Forbidden'; break }
                        if ($method -ne 'POST') { Send-Json $stream @{ ok = $false; error = 'method not allowed' } '405 Method Not Allowed'; break }
                        $action = ''; $target = ''
                        try {
                            if (-not [string]::IsNullOrWhiteSpace($body)) {
                                $req = $body | ConvertFrom-Json
                                $action = [string]$req.action
                                $target = [string]$req.path
                            }
                        } catch { }
                        if ($script:state.running) { Send-Json $stream @{ ok = $false; error = '上一个操作还在进行' }; break }
                        if ($action -notin @('Install', 'Verify', 'Uninstall')) { Send-Json $stream @{ ok = $false; error = '未知操作' }; break }
                        if ([string]::IsNullOrWhiteSpace($target)) { Send-Json $stream @{ ok = $false; error = '缺少游戏目录' }; break }
                        try {
                            Start-CoOpWebAction $action $target
                            Send-Json $stream @{ ok = $true }
                        } catch {
                            Add-UiLog ('无法启动安装器：' + $_.Exception.Message) 'err'
                            Send-Json $stream @{ ok = $false; error = $_.Exception.Message }
                        }
                        break
                    }
                    default {
                        Send-Json $stream @{ ok = $false; error = 'not found' } '404 Not Found'
                        break
                    }
                }
            } catch {
                $status = '500 Internal Server Error'
                $errorRecord = $_.Exception
                $inner = $errorRecord
                $timedOut = $false
                $malformed = $false
                while ($null -ne $inner) {
                    if ($inner -is [System.Net.Sockets.SocketException] -and
                        $inner.SocketErrorCode -eq [System.Net.Sockets.SocketError]::TimedOut) { $timedOut = $true; break }
                    if ($inner -is [System.IO.InvalidDataException] -or
                        $inner -is [System.Text.DecoderFallbackException]) { $malformed = $true }
                    $inner = $inner.InnerException
                }
                if ($timedOut) {
                    $status = '408 Request Timeout'
                } elseif ($malformed) { $status = '400 Bad Request' }
                try { Send-Json $stream @{ ok = $false; error = $_.Exception.Message } $status } catch { }
            } finally {
                try { $stream.Close() } catch { }
                try { $client.Close() } catch { }
            }
            continue
        }

        Start-Sleep -Milliseconds 50
        Update-CoOpWebProgress

        if ($script:state.closeRequested -and -not $script:state.running) { break }
        $now = Get-Date
        if ($script:state.sawHeartbeat) {
            if (($now - $script:state.lastHeartbeat).TotalSeconds -gt $idleLimit) { $script:state.closeRequested = $true }
        } elseif (($now - $startedAt).TotalSeconds -gt $bootLimit) {
            $script:state.closeRequested = $true
        }
    }
} finally {
    if ($script:state.running -and $null -ne $script:handle) {
        $script:state.closeRequested = $true
        while ($script:state.running -and $null -ne $script:handle -and -not $script:handle.IsCompleted) {
            Start-Sleep -Milliseconds 100
            Update-CoOpWebProgress
        }
        Update-CoOpWebProgress
    }
    if ($null -ne $script:ps) { try { $script:ps.Dispose() } catch { } }
    if ($null -ne $script:rs) { try { $script:rs.Close(); $script:rs.Dispose() } catch { } }
    try { $listener.Stop() } catch { }
}
exit 0
