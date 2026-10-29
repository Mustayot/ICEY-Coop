[CmdletBinding()]
param(
    [ValidateSet('Install','Verify','Uninstall')][string]$Action,
    [string]$GameRoot,
    [string]$PackageRoot
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Installer.Core.ps1')

function Read-CoopPath {
    param([string]$Prompt)
    $value = Read-Host $Prompt
    if ($null -eq $value) { return '' }
    $value = $value.Trim()
    if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[$value.Length-1] -eq '"') -or ($value[0] -eq "'" -and $value[$value.Length-1] -eq "'"))) {
        $value = $value.Substring(1,$value.Length-2).Trim()
    }
    return $value
}

if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($Action)) {
    Write-Host ''
    Write-Host 'ICEYCoop 离线安装器'
    Write-Host '1. 安装 / 更新'
    Write-Host '2. 校验安装文件'
    Write-Host '3. 卸载并恢复安装前文件'
    $choice = Read-Host '请选择 1、2 或 3'
    switch ($choice.Trim()) {
        '1' { $Action = 'Install' }
        '2' { $Action = 'Verify' }
        '3' { $Action = 'Uninstall' }
        default { Write-Host '未选择有效操作。'; exit 2 }
    }
}
if ([string]::IsNullOrWhiteSpace($GameRoot)) { $GameRoot = Read-CoopPath '请输入 ICEY 游戏目录（包含 ICEY.exe）' }
if ([string]::IsNullOrWhiteSpace($GameRoot)) { Write-Host '游戏目录不能为空。'; exit 2 }

try {
    $result = Invoke-CoopAction -Action $Action -GameRoot $GameRoot -PackageRoot $PackageRoot
    Write-Host ''
    Write-Host ('操作：{0}    结果：{1}' -f $result.action,$result.status)
    Write-Host ('构建：{0}' -f $result.buildId)
    Write-Host ('游戏目录：{0}' -f $result.gameRoot)
    if ($result.backup) { Write-Host ('备份：{0}' -f $result.backup) }
    foreach ($file in $result.files) { Write-Host ('[{0}] {1}' -f $file.status,$file.path) }
    if ($result.status -eq 'Verified') { Write-Host '文件校验完成；这不代表已验证联机可玩。' }
    exit 0
} catch {
    $code = 4
    if ($_.Exception.Data -and $_.Exception.Data.Contains('ExitCode')) { $code = [int]$_.Exception.Data['ExitCode'] }
    Write-Host ('失败（退出码 {0}）：{1}' -f $code,$_.Exception.Message) -ForegroundColor Red
    if ($_.Exception.Data -and $_.Exception.Data.Contains('Diagnostic')) { Write-Host ([string]$_.Exception.Data['Diagnostic']) -ForegroundColor DarkRed }
    exit $code
}