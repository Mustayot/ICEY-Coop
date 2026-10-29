Set-StrictMode -Version 2

$script:CoopModPaths = @('BepInEx\plugins\IceyCoop.Bootstrap.dll','BepInEx\plugins\IceyCoop.Bridge.dll','BepInEx\icey-coop\lib\IceyCoop.GameBridge.dll','BepInEx\icey-coop\lib\IceyCoop.Core.dll','BepInEx\icey-coop\lib\IceyCoop.Bridge.dll','BepInEx\icey-coop\lib\IceyCoop.Protocol.dll','BepInEx\icey-coop\lib\IceyCoop.Transport.dll')
$script:CoopLoaderPaths = @('winhttp.dll','.doorstop_version','doorstop_config.ini','BepInEx\core\0Harmony.dll','BepInEx\core\0Harmony20.dll','BepInEx\core\BepInEx.dll','BepInEx\core\BepInEx.Harmony.dll','BepInEx\core\BepInEx.Preloader.dll','BepInEx\core\HarmonyXInterop.dll','BepInEx\core\Mono.Cecil.dll','BepInEx\core\Mono.Cecil.Mdb.dll','BepInEx\core\Mono.Cecil.Pdb.dll','BepInEx\core\Mono.Cecil.Rocks.dll','BepInEx\core\MonoMod.RuntimeDetour.dll','BepInEx\core\MonoMod.Utils.dll')
$script:CoopConfigPath = 'BepInEx\config\BepInEx.cfg'
$script:CoopStatePath = 'BepInEx\icey-coop\installer-state.json'
$script:CoopProduct = 'ICEYCoop'

function New-CoopInstallerException {
    param([string]$Message,[int]$ExitCode,[Exception]$Inner)
    if ($null -eq $Inner) { $e = New-Object System.InvalidOperationException($Message) } else { $e = New-Object System.InvalidOperationException($Message,$Inner) }
    $e.Data['ExitCode'] = $ExitCode
    if ($null -ne $Inner) { $e.Data['Diagnostic'] = $Inner.ToString() }
    return $e
}
function Stop-CoopInstaller { param([string]$Message,[int]$ExitCode,[Exception]$Inner); throw (New-CoopInstallerException $Message $ExitCode $Inner) }
function Get-CoopProperty {
    param($Object,[string]$Name,[bool]$Required = $true)
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) { if ($Required) { Stop-CoopInstaller "清单或状态缺少字段：$Name" 2 $null }; return $null }
    return $Object.$Name
}
function Get-CoopCanonicalDirectory {
    param([string]$Path,[string]$Label)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)) { Stop-CoopInstaller "$Label 必须是已存在的绝对目录路径" 2 $null }
    try {
        $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
        $item = Get-Item -LiteralPath $resolved.ProviderPath -Force -ErrorAction Stop
        if (-not $item.PSIsContainer) { Stop-CoopInstaller "$Label 不是目录：$Path" 2 $null }
        $canonical = [IO.Path]::GetFullPath($item.FullName).TrimEnd('\')
        if ($canonical.Length -eq 2 -and $canonical[1] -eq ':') { $canonical += '\' }
        return $canonical
    } catch { if ($_.Exception.Data.Contains('ExitCode')) { throw }; Stop-CoopInstaller "$Label 无法解析：$Path" 2 $_.Exception }
}
function ConvertTo-CoopRelativePath {
    param([string]$Path,[string]$Label = '文件路径',[int]$ExitCode = 2)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '^[\\/]' -or $Path.Contains(':')) { Stop-CoopInstaller "$Label 必须是严格相对路径：$Path" $ExitCode $null }
    $normalized = $Path.Replace('/','\'); $segments = $normalized.Split('\')
    foreach ($segment in $segments) {
        if ([string]::IsNullOrEmpty($segment) -or $segment -eq '.' -or $segment -eq '..') { Stop-CoopInstaller "$Label 含有空段或路径穿越：$Path" $ExitCode $null }
        if ($segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { Stop-CoopInstaller "$Label 含有无效字符：$Path" $ExitCode $null }
        if (($segment.Split('.')[0]).ToUpperInvariant() -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') { Stop-CoopInstaller "$Label 使用保留设备名：$Path" $ExitCode $null }
    }
    return ($segments -join '\')
}
function Test-CoopPathAllowed {
    param([string]$Path,[string]$Kind)
    if ($Kind -eq 'mod') { return ($script:CoopModPaths -contains $Path) }
    if ($Kind -eq 'loader') { return ($script:CoopLoaderPaths -contains $Path) }
    if ($Kind -eq 'config') { return ($Path -ieq $script:CoopConfigPath) }
    return $false
}
function Test-CoopOwnedPathAllowed { param([string]$Path); return (($script:CoopModPaths -contains $Path) -or ($script:CoopLoaderPaths -contains $Path) -or ($Path -ieq $script:CoopConfigPath)) }
function Get-CoopFullChildPath {
    param([string]$Root,[string]$RelativePath)
    $rel = ConvertTo-CoopRelativePath $RelativePath
    $full = [IO.Path]::GetFullPath((Join-Path $Root $rel)); $prefix = $Root.TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { Stop-CoopInstaller "目标路径越出根目录：$RelativePath" 2 $null }
    return $full
}
function Assert-CoopNoReparsePath {
    param([string]$Root,[string]$TargetPath)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\'); $targetFull = [IO.Path]::GetFullPath($TargetPath); $prefix = $rootFull + '\'
    if (-not $targetFull.Equals($rootFull,[StringComparison]::OrdinalIgnoreCase) -and -not $targetFull.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { Stop-CoopInstaller "目标路径越出游戏目录：$TargetPath" 2 $null }
    $rootItem = Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-CoopInstaller "游戏目录是重解析点，已拒绝：$rootFull" 3 $null }
    $relative = if ($targetFull.Equals($rootFull,[StringComparison]::OrdinalIgnoreCase)) { '' } else { $targetFull.Substring($prefix.Length) }
    $parts = @(); if ($relative.Length -gt 0) { $parts = $relative.Split('\') }; $current = $rootFull
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $current = Join-Path $current $parts[$i]
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-CoopInstaller "目标路径包含重解析点，已拒绝：$current" 3 $null }
            if ($i -lt ($parts.Count - 1) -and -not $item.PSIsContainer) { Stop-CoopInstaller "目标路径祖先不是目录：$current" 3 $null }
        }
    }
}
function Assert-CoopPackageNoReparse {
    param([string]$PackageRoot,[string]$TargetPath)
    $rootFull = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\'); $targetFull = [IO.Path]::GetFullPath($TargetPath); $prefix = $rootFull + '\'
    if (-not $targetFull.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { Stop-CoopInstaller '包文件路径越出 PackageRoot' 2 $null }
    $rootItem = Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-CoopInstaller 'PackageRoot 是重解析点，已拒绝' 2 $null }
    $parts = $targetFull.Substring($prefix.Length).Split('\'); $current = $rootFull
    for ($i = 0; $i -lt $parts.Count; $i++) { $current = Join-Path $current $parts[$i]; if (Test-Path -LiteralPath $current) { $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop; if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-CoopInstaller "包路径包含重解析点：$current" 2 $null } } }
}
function Get-CoopSha256Bytes { param([byte[]]$Bytes); $sha = [Security.Cryptography.SHA256]::Create(); try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() } }
function Get-CoopSha256File { param([string]$Path); try { return (Get-CoopSha256Bytes ([IO.File]::ReadAllBytes($Path))) } catch { Stop-CoopInstaller "无法读取或计算文件哈希：$Path" 2 $_.Exception } }
function Assert-CoopHashText { param([string]$Hash,[string]$Label,[int]$ExitCode = 2); if ($Hash -notmatch '^[0-9a-fA-F]{64}$') { Stop-CoopInstaller "$Label 不是 64 位 SHA-256" $ExitCode $null } }
function Read-CoopJsonFile {
    param([string]$Path,[string]$Label,[int]$ExitCode)
    try { return ([IO.File]::ReadAllText($Path,[Text.Encoding]::UTF8) | ConvertFrom-Json -ErrorAction Stop) }
    catch { if ($_.Exception.Data.Contains('ExitCode')) { throw }; Stop-CoopInstaller "$Label JSON 无法读取：$Path" $ExitCode $_.Exception }
}
function Get-CoopPackage {
    param([string]$PackageRoot,[bool]$CheckPayload = $true)
    $root = Get-CoopCanonicalDirectory $PackageRoot 'PackageRoot'; $manifestPath = Join-Path $root 'manifest.json'
    Assert-CoopPackageNoReparse $root $manifestPath
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { Stop-CoopInstaller "缺少包清单：$manifestPath" 2 $null }
    $manifest = Read-CoopJsonFile $manifestPath '包清单' 2
    if ([int](Get-CoopProperty $manifest 'schemaVersion') -ne 1 -or (Get-CoopProperty $manifest 'product') -cne $script:CoopProduct) { Stop-CoopInstaller '包格式或产品标识不受支持' 2 $null }
    $buildId = [string](Get-CoopProperty $manifest 'buildId')
    if ([string]::IsNullOrWhiteSpace($buildId) -or $buildId -notmatch '^[A-Za-z0-9._-]{1,128}$') { Stop-CoopInstaller 'buildId 缺失或格式无效' 2 $null }

    $gameFiles = @(); $gameSeen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @(Get-CoopProperty $manifest 'gameFiles')) {
        $path = ConvertTo-CoopRelativePath ([string](Get-CoopProperty $entry 'path')) 'gameFiles.path'; $hash = [string](Get-CoopProperty $entry 'sha256')
        Assert-CoopHashText $hash "gameFiles $path"
        if (-not $gameSeen.Add($path)) { Stop-CoopInstaller "gameFiles 路径重复：$path" 2 $null }
        $gameFiles += [pscustomobject]@{ path = $path; sha256 = $hash.ToLowerInvariant() }
    }
    if ($gameFiles.Count -eq 0 -or -not ($gameFiles | Where-Object { $_.path -ieq 'ICEY.exe' }) -or -not ($gameFiles | Where-Object { $_.path -ieq 'ICEY_Data\Managed\Assembly-CSharp.dll' })) {
        Stop-CoopInstaller 'gameFiles 必须包含 ICEY.exe 与 Assembly-CSharp.dll 指纹' 2 $null
    }

    $files = @(); $fileSeen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @(Get-CoopProperty $manifest 'files')) {
        $path = ConvertTo-CoopRelativePath ([string](Get-CoopProperty $entry 'path')) 'files.path'
        $hash = [string](Get-CoopProperty $entry 'sha256'); $kind = [string](Get-CoopProperty $entry 'kind')
        if (-not $fileSeen.Add($path)) { Stop-CoopInstaller "包文件路径重复：$path" 2 $null }
        Assert-CoopHashText $hash "payload $path"
        if ($kind -notin @('mod','loader','config') -or -not (Test-CoopPathAllowed $path $kind)) { Stop-CoopInstaller "包文件路径或类型不在允许清单：$kind $path" 2 $null }
        $length = -1L
        if ($CheckPayload) {
            $bytesValue = Get-CoopProperty $entry 'bytes'
            try { $length = [long]$bytesValue } catch { Stop-CoopInstaller "payload 长度无效：$path" 2 $null }
            if ($length -lt 0) { Stop-CoopInstaller "payload 长度无效：$path" 2 $null }
        }
        $payloadRoot = Join-Path $root 'payload'; $sourcePath = Get-CoopFullChildPath $payloadRoot $path
        Assert-CoopPackageNoReparse $root $sourcePath
        $content = $null
        if ($CheckPayload) {
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { Stop-CoopInstaller "包中缺少 payload：$path" 2 $null }
            $info = Get-Item -LiteralPath $sourcePath -Force
            if ($info.Length -ne $length) { Stop-CoopInstaller "payload 长度校验失败：$path" 5 $null }
            $actual = Get-CoopSha256File $sourcePath
            if ($actual -ine $hash) { Stop-CoopInstaller "payload SHA-256 校验失败：$path" 5 $null }
            $content = [IO.File]::ReadAllBytes($sourcePath)
        }
        $files += [pscustomobject]@{ path = $path; sha256 = $hash.ToLowerInvariant(); bytes = $length; kind = $kind; sourcePath = $sourcePath; content = $content }
    }
    $modEntries = @($files | Where-Object { $_.kind -eq 'mod' })
    if ($modEntries.Count -ne $script:CoopModPaths.Count) { Stop-CoopInstaller 'payload 必须包含完整的 7 个 ICEYCoop DLL' 2 $null }
    foreach ($requiredMod in $script:CoopModPaths) { if (-not ($modEntries | Where-Object { $_.path -ieq $requiredMod })) { Stop-CoopInstaller "payload 缺少 Mod 文件：$requiredMod" 2 $null } }
    if ($files.Count -ne 23 -or @($files | Where-Object { $_.kind -eq 'loader' }).Count -ne $script:CoopLoaderPaths.Count) { Stop-CoopInstaller 'payload 必须包含全部 15 个 loader、7 个 mod 和 1 个 config 文件' 2 $null }
    foreach ($requiredLoader in $script:CoopLoaderPaths) { if (-not ($files | Where-Object { $_.path -ieq $requiredLoader -and $_.kind -eq 'loader' })) { Stop-CoopInstaller "payload 缺少 loader 文件：$requiredLoader" 2 $null } }
    $gameBridge = $files | Where-Object { $_.path -ieq 'BepInEx\icey-coop\lib\IceyCoop.GameBridge.dll' } | Select-Object -First 1
    if ($null -eq $gameBridge -or $buildId -ine $gameBridge.sha256.Substring(0,12)) { Stop-CoopInstaller 'buildId 必须等于 GameBridge SHA-256 的前 12 位' 2 $null }
    if (-not ($files | Where-Object { $_.kind -eq 'config' -and $_.path -ieq $script:CoopConfigPath })) { Stop-CoopInstaller 'payload 缺少必需的 BepInEx config' 2 $null }
    return [pscustomobject]@{ root = $root; manifest = $manifest; buildId = $buildId; gameFiles = $gameFiles; files = $files }
}
function Assert-CoopGameCompatible {
    param([string]$GameRoot,$Package)
    $exe = Join-Path $GameRoot 'ICEY.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { Stop-CoopInstaller "所选目录不是 ICEY 游戏目录：缺少 $exe" 2 $null }
    Assert-CoopNoReparsePath $GameRoot $exe
    foreach ($entry in $Package.gameFiles) {
        $path = Get-CoopFullChildPath $GameRoot $entry.path; Assert-CoopNoReparsePath $GameRoot $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Stop-CoopInstaller "游戏文件不完整：$($entry.path)" 5 $null }
        if ((Get-CoopSha256File $path) -ine $entry.sha256) { Stop-CoopInstaller "游戏版本不匹配：$($entry.path) SHA-256 不符" 5 $null }
    }
}
function Assert-CoopGameNotRunning {
    param([string]$GameRoot)
    try { $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ICEY.exe'" -ErrorAction Stop) }
    catch { Stop-CoopInstaller '无法通过 CIM 确认 ICEY.exe 是否正在运行；请关闭安装器后重试' 3 $_.Exception }
    $targetExe = [IO.Path]::GetFullPath((Join-Path $GameRoot 'ICEY.exe'))
    foreach ($process in $processes) {
        $exePath = [string]$process.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($exePath)) { Stop-CoopInstaller '发现 ICEY.exe 进程但 CIM 无法确认其路径；为避免误操作已拒绝' 3 $null }
        try { $exePath = [IO.Path]::GetFullPath($exePath) } catch { Stop-CoopInstaller '发现 ICEY.exe 进程但无法解析其路径；为避免误操作已拒绝' 3 $_.Exception }
        if ($exePath.Equals($targetExe,[StringComparison]::OrdinalIgnoreCase)) { Stop-CoopInstaller '目标 ICEY 正在运行；请先正常退出游戏后重试。安装器不会关闭游戏进程' 3 $null }
    }
}
function Test-CoopRequiredConfig {
    param([string]$Text)
    $required = @{ 'Preloader.Entrypoint' = @{ Assembly='UnityEngine.dll'; Type='MonoBehaviour'; Method='.cctor' }; 'Caching' = @{ EnableAssemblyCache='false' } }
    $sections = @{}; $current = ''
    foreach ($line in ($Text -split "
|`n|`r")) {
        if ($line -match '^\s*\[([^\]]+)\]\s*(?:[;#].*)?$') { $current = $Matches[1].Trim(); if (-not $sections.ContainsKey($current)) { $sections[$current] = @{} } }
        elseif ($current -and $line -match '^\s*([^=;#]+?)\s*=\s*(.*?)\s*(?:[;#].*)?$') { $sections[$current][$Matches[1].Trim()] = $Matches[2].Trim() }
    }
    foreach ($section in $required.Keys) {
        if (-not $sections.ContainsKey($section)) { return $false }
        foreach ($key in $required[$section].Keys) { if (-not $sections[$section].ContainsKey($key) -or $sections[$section][$key] -cne $required[$section][$key]) { return $false } }
    }
    return $true
}
function Set-CoopIniValue {
    param([System.Collections.Generic.List[string]]$Lines,[string]$Section,[string]$Key,[string]$Value)
    $pattern = '^\s*\[' + [regex]::Escape($Section) + '\]\s*(?:[;#].*)?$'; $starts = New-Object 'System.Collections.Generic.List[int]'
    for ($i=0; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -match $pattern) { $starts.Add($i) } }
    if ($starts.Count -eq 0) { if ($Lines.Count -gt 0 -and $Lines[$Lines.Count-1] -ne '') { $Lines.Add('') }; $Lines.Add('['+$Section+']'); $Lines.Add($Key+'='+$Value); return }
    for ($n=$starts.Count-1; $n -ge 0; $n--) {
        $start = $starts[$n]; $end = if ($n+1 -lt $starts.Count) { $starts[$n+1] } else { $Lines.Count }
        $found = $false
        for ($i=$start+1; $i -lt $end; $i++) { if ($Lines[$i] -match ('^\s*'+[regex]::Escape($Key)+'\s*=')) { $Lines[$i] = $Key+'='+$Value; $found = $true } }
        if (-not $found) { $Lines.Insert($end,$Key+'='+$Value) }
    }
}
function Merge-CoopConfig {
    param([byte[]]$SourceBytes,[string]$ExistingPath)
    $encoding = New-Object System.Text.UTF8Encoding($false,$true)
    try {
        $sourceText = $encoding.GetString($SourceBytes).TrimStart([char]0xFEFF)
        if (Test-Path -LiteralPath $ExistingPath -PathType Leaf) { $text = $encoding.GetString([IO.File]::ReadAllBytes($ExistingPath)).TrimStart([char]0xFEFF) } else { $text = $sourceText }
    } catch { Stop-CoopInstaller 'BepInEx.cfg 不是有效 UTF-8，无法安全合并' 3 $_.Exception }
    $newline = "
"; if ($text.Contains("`n") -and -not $text.Contains("
")) { $newline = "`n" } elseif ($text.Contains("`r") -and -not $text.Contains("`n")) { $newline = "`r" }
    $lines = New-Object 'System.Collections.Generic.List[string]'; foreach ($line in ($text -split "
|`n|`r")) { $lines.Add($line) }
    Set-CoopIniValue $lines 'Preloader.Entrypoint' 'Assembly' 'UnityEngine.dll'; Set-CoopIniValue $lines 'Preloader.Entrypoint' 'Type' 'MonoBehaviour'; Set-CoopIniValue $lines 'Preloader.Entrypoint' 'Method' '.cctor'; Set-CoopIniValue $lines 'Caching' 'EnableAssemblyCache' 'false'
    $merged = [string]::Join($newline,$lines.ToArray()); if ($text.EndsWith("`n") -or $text.EndsWith("`r")) { $merged += $newline }
    if (-not (Test-CoopRequiredConfig $merged)) { Stop-CoopInstaller '无法生成必要的 BepInEx 入口配置' 4 $null }
    return (New-Object System.Text.UTF8Encoding($false)).GetBytes($merged)
}
function Get-CoopState {
    param([string]$GameRoot)
    $path = Get-CoopFullChildPath $GameRoot $script:CoopStatePath; Assert-CoopNoReparsePath $GameRoot $path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $state = Read-CoopJsonFile $path '安装状态' 3
    if ([int](Get-CoopProperty $state 'schemaVersion') -ne 1 -or (Get-CoopProperty $state 'product') -cne $script:CoopProduct) { Stop-CoopInstaller '安装状态格式或产品标识无效，拒绝继续' 3 $null }
    $status = [string](Get-CoopProperty $state 'status'); if ($status -notin @('Installed','Uninstalled')) { Stop-CoopInstaller "未知安装状态：$status" 3 $null }
    $buildId = [string](Get-CoopProperty $state 'buildId'); if ([string]::IsNullOrWhiteSpace($buildId)) { Stop-CoopInstaller '安装状态缺少 buildId' 3 $null }
    $files = @(); $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @(Get-CoopProperty $state 'files')) {
        $rel = ConvertTo-CoopRelativePath ([string](Get-CoopProperty $entry 'path')) 'installer-state.path' -ExitCode 3
        if (-not (Test-CoopOwnedPathAllowed $rel) -or -not $seen.Add($rel)) { Stop-CoopInstaller "状态路径无效或重复：$rel" 3 $null }
        $kind = [string](Get-CoopProperty $entry 'kind')
        if (-not (Test-CoopPathAllowed $rel $kind)) { Stop-CoopInstaller "状态 kind/path 不匹配：$kind $rel" 3 $null }
        $installedHash = [string](Get-CoopProperty $entry 'installedSha256'); Assert-CoopHashText $installedHash "状态 installedSha256 $rel" 3
        $originalExisted = [bool](Get-CoopProperty $entry 'originalExisted'); $originalHash = Get-CoopProperty $entry 'originalSha256' $false; $backupRel = Get-CoopProperty $entry 'originalBackup' $false
        if ($originalExisted) {
            Assert-CoopHashText ([string]$originalHash) "状态 originalSha256 $rel" 3
            $backupRel = ConvertTo-CoopRelativePath ([string]$backupRel) 'installer-state.originalBackup' -ExitCode 3
            if (-not $backupRel.StartsWith('BepInEx\icey-coop\install-backups\',[StringComparison]::OrdinalIgnoreCase) -or -not $backupRel.EndsWith('\before\'+$rel,[StringComparison]::OrdinalIgnoreCase)) { Stop-CoopInstaller "状态备份路径不在受控备份目录或与目标不匹配：$backupRel" 3 $null }
        } elseif ($null -ne $originalHash -or $null -ne $backupRel) { Stop-CoopInstaller "原文件不存在但状态含原始备份：$rel" 3 $null }
        $preserved = [bool](Get-CoopProperty $entry 'preservedOnUninstall' $false)
        $preservedExists = Get-CoopProperty $entry 'preservedExists' $false
        $preservedHash = Get-CoopProperty $entry 'preservedSha256' $false
        if ($preserved -and $kind -notin @('config','loader')) { Stop-CoopInstaller "不允许 preserve 此类型：$rel" 3 $null }
        if ($preserved -and $preservedExists) { Assert-CoopHashText ([string]$preservedHash) "状态 preservedSha256 $rel" 3 }
        if ($preserved -and -not $preservedExists -and $null -ne $preservedHash) { Stop-CoopInstaller "不存在的 preserve 文件不能带哈希：$rel" 3 $null }
        $files += [pscustomobject]@{ path=$rel; kind=$kind; installedSha256=$installedHash.ToLowerInvariant(); originalExisted=$originalExisted; originalBackup=$backupRel; originalSha256=if($originalExisted){([string]$originalHash).ToLowerInvariant()}else{$null}; preservedOnUninstall=$preserved; preservedExists=[bool]$preservedExists; preservedSha256=$preservedHash }
    }
    if ($files.Count -eq 0) { Stop-CoopInstaller '安装状态 files 不能为空' 3 $null }
    $expectedPaths=@($script:CoopModPaths)+@($script:CoopLoaderPaths)+@($script:CoopConfigPath)
    if ($files.Count -ne $expectedPaths.Count) { Stop-CoopInstaller '安装状态文件清单不完整或包含额外目标' 3 $null }
    foreach ($expectedPath in $expectedPaths) { if (-not ($files | Where-Object { $_.path -ieq $expectedPath })) { Stop-CoopInstaller ('安装状态缺少受管目标：'+$expectedPath) 3 $null } }
    return [pscustomobject]@{ schemaVersion=1; product=$script:CoopProduct; status=$status; buildId=$buildId; files=$files }
}
function Assert-CoopSameFileSet {
    param($State,$Package)
    $expected = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase); foreach ($file in $Package.files) { [void]$expected.Add($file.path) }
    $actual = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase); foreach ($file in $State.files) { [void]$actual.Add($file.path) }
    if ($expected.Count -ne $actual.Count) { Stop-CoopInstaller '已安装文件布局与此包不同；请先卸载后再安装，避免丢失原始备份' 3 $null }
    foreach ($path in $expected) { if (-not $actual.Contains($path)) { Stop-CoopInstaller '已安装文件布局与此包不同；为保护原始备份已拒绝升级' 3 $null } }
}
function Get-CoopStateFile { param($State,[string]$Path); foreach ($file in $State.files) { if ($file.path -ieq $Path) { return $file } }; return $null }
function Assert-CoopOwnedFilesUnmodified {
    param([string]$GameRoot,$State)
    foreach ($entry in $State.files) {
        $target = Get-CoopFullChildPath $GameRoot $entry.path; Assert-CoopNoReparsePath $GameRoot $target
        if ($entry.kind -eq 'config') { continue }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { Stop-CoopInstaller "受管理文件缺失，未作任何更改：$($entry.path)" 3 $null }
        if ((Get-CoopSha256File $target) -ine $entry.installedSha256) { Stop-CoopInstaller "受管理文件已被修改，未作任何更改：$($entry.path)" 3 $null }
    }
}
function New-CoopLock {
    param([string]$GameRoot)
    $hash = Get-CoopSha256Bytes ([Text.Encoding]::UTF8.GetBytes($GameRoot.ToLowerInvariant()))
    $mutex = New-Object System.Threading.Mutex($false,('Global\ICEYCoopInstaller_'+$hash.Substring(0,32))); $taken = $false
    try { $taken = $mutex.WaitOne(0,$false) }
    catch [Threading.AbandonedMutexException] { $taken = $true; $mutex.ReleaseMutex(); $mutex.Dispose(); Stop-CoopInstaller '检测到上次安装进程异常中断；请检查状态和备份后重试' 4 $null }
    if (-not $taken) { $mutex.Dispose(); Stop-CoopInstaller '另一个安装器正在操作此游戏目录' 3 $null }
    return $mutex
}
function Write-CoopAtomic {
    param([string]$GameRoot,[string]$TargetPath,[byte[]]$Bytes)
    Assert-CoopNoReparsePath $GameRoot $TargetPath; $directory = Split-Path -Parent $TargetPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { Assert-CoopNoReparsePath $GameRoot $directory; New-Item -ItemType Directory -Force -Path $directory | Out-Null; Assert-CoopNoReparsePath $GameRoot $TargetPath }
    $temp = Join-Path $directory ('.iceycoop-'+[Guid]::NewGuid().ToString('N')+'.tmp'); $replaceBackup=$temp+'.previous'
    try {
        $stream = New-Object IO.FileStream($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($Bytes,0,$Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        Assert-CoopNoReparsePath $GameRoot $TargetPath
        if (Test-Path -LiteralPath $TargetPath -PathType Leaf) { [IO.File]::Replace($temp,$TargetPath,$replaceBackup) } else { [IO.File]::Move($temp,$TargetPath) }
        $written = [IO.File]::ReadAllBytes($TargetPath)
        $sameBytes = $written.Length -eq $Bytes.Length
        if ($sameBytes) { for ($i=0; $i -lt $Bytes.Length; $i++) { if ($written[$i] -ne $Bytes[$i]) { $sameBytes=$false; break } } }
        if (-not $sameBytes -or (Get-CoopSha256Bytes $written) -ine (Get-CoopSha256Bytes $Bytes)) { Stop-CoopInstaller "原子写入后目标字节/SHA-256校验失败：$TargetPath" 4 $null }
    } finally { foreach($cleanupPath in @($temp,$replaceBackup)){if(Test-Path -LiteralPath $cleanupPath){Remove-Item -LiteralPath $cleanupPath -Force -ErrorAction SilentlyContinue}} }
}function Write-CoopUtf8JsonAtomic {
    param([string]$GameRoot,[string]$TargetPath,$Object)
    $json = ConvertTo-Json -InputObject $Object -Depth 8; $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json+"
"); Write-CoopAtomic $GameRoot $TargetPath $bytes
}
function New-CoopTransaction {
    param([string]$GameRoot,[object[]]$Actions)
    $backupRel = 'BepInEx\icey-coop\install-backups\'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+[Guid]::NewGuid().ToString('N')
    $backupRoot = Get-CoopFullChildPath $GameRoot $backupRel; Assert-CoopNoReparsePath $GameRoot $backupRoot
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $beforeRoot = Join-Path $backupRoot 'before'; New-Item -ItemType Directory -Force -Path $beforeRoot | Out-Null
    $statePath = Get-CoopFullChildPath $GameRoot $script:CoopStatePath; Assert-CoopNoReparsePath $GameRoot $statePath
    $stateExisted = Test-Path -LiteralPath $statePath -PathType Leaf; $stateBackup = $null
    if ($stateExisted) { Copy-Item -LiteralPath $statePath -Destination (Join-Path $beforeRoot 'installer-state.json') -Force; $stateBackup = $backupRel+'\before\installer-state.json' }
    $journal = [ordered]@{ schemaVersion=1; product=$script:CoopProduct; startedAt=[DateTime]::UtcNow.ToString('o'); status='Prepared'; files=@(); stateExisted=$stateExisted; stateBackup=$stateBackup; rollbackErrors=@() }
    foreach ($action in $Actions) {
        $target = Get-CoopFullChildPath $GameRoot $action.path; Assert-CoopNoReparsePath $GameRoot $target
        $exists = Test-Path -LiteralPath $target -PathType Leaf; $beforeRel=$null; $beforeHash=$null
        if ($exists) {
            $beforeRel = $backupRel+'\before\'+$action.path; $beforePath=Get-CoopFullChildPath $GameRoot $beforeRel; $beforeDir=Split-Path -Parent $beforePath
            Assert-CoopNoReparsePath $GameRoot $beforeDir; New-Item -ItemType Directory -Force -Path $beforeDir | Out-Null
            Copy-Item -LiteralPath $target -Destination $beforePath -Force; $beforeHash=Get-CoopSha256File $beforePath
        }
        $journal.files += [ordered]@{ path=$action.path; beforeExisted=$exists; beforeBackup=$beforeRel; beforeSha256=$beforeHash; operation=$action.operation }
    }
    $journalPath = Join-Path $backupRoot 'rollback-journal.json'; Write-CoopUtf8JsonAtomic $GameRoot $journalPath $journal
    return [pscustomobject]@{ backup=$backupRoot; backupRelative=$backupRel; journalPath=$journalPath; journal=$journal; stateExisted=$stateExisted; stateBackup=$stateBackup }
}
function Save-CoopJournal { param([string]$GameRoot,$Transaction); Write-CoopUtf8JsonAtomic $GameRoot $Transaction.journalPath $Transaction.journal }
function Invoke-CoopRollback {
    param([string]$GameRoot,$Transaction)
    $errors = New-Object 'System.Collections.Generic.List[string]'
    $journalFiles = @($Transaction.journal.files)
    for ($i=$journalFiles.Count-1; $i -ge 0; $i--) {
        $entry = $journalFiles[$i]
        try {
            $target=Get-CoopFullChildPath $GameRoot $entry.path; Assert-CoopNoReparsePath $GameRoot $target
            if ($entry.beforeExisted) {
                $backup=Get-CoopFullChildPath $GameRoot $entry.beforeBackup; Assert-CoopNoReparsePath $GameRoot $backup; $bytes=[IO.File]::ReadAllBytes($backup)
                if ((Get-CoopSha256Bytes $bytes) -ine $entry.beforeSha256) { throw 'rollback snapshot hash mismatch' }
                Write-CoopAtomic $GameRoot $target $bytes
            } elseif (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
        } catch { $errors.Add(($entry.path+': '+$_.Exception.Message)) }
    }
    try {
        $statePath=Get-CoopFullChildPath $GameRoot $script:CoopStatePath; Assert-CoopNoReparsePath $GameRoot $statePath
        if ($Transaction.stateExisted) { $old=Get-CoopFullChildPath $GameRoot $Transaction.stateBackup; $bytes=[IO.File]::ReadAllBytes($old); Write-CoopAtomic $GameRoot $statePath $bytes }
        elseif (Test-Path -LiteralPath $statePath -PathType Leaf) { Remove-Item -LiteralPath $statePath -Force }
    } catch { $errors.Add(('installer-state.json: '+$_.Exception.Message)) }
    if ($errors.Count -eq 0) { $Transaction.journal.status='RolledBack' } else { $Transaction.journal.status='RollbackFailed'; $Transaction.journal.rollbackErrors=@($errors.ToArray()) }
    try { Save-CoopJournal $GameRoot $Transaction } catch { $errors.Add(('rollback-journal.json: '+$_.Exception.Message)) }
    return @($errors.ToArray())
}
function New-CoopStateObject { param([string]$BuildId,[string]$Status,[object[]]$Files); return [ordered]@{ schemaVersion=1; product=$script:CoopProduct; status=$Status; buildId=$BuildId; files=@($Files) } }
function Assert-CoopOriginalBackups {
    param([string]$GameRoot,$State)
    foreach($entry in $State.files){
        if(-not $entry.originalExisted){continue}
        $backup=Get-CoopFullChildPath $GameRoot $entry.originalBackup; Assert-CoopNoReparsePath $GameRoot $backup
        if(-not (Test-Path -LiteralPath $backup -PathType Leaf)){Stop-CoopInstaller "原始备份缺失，拒绝卸载：$($entry.path)" 3 $null}
        if((Get-CoopSha256File $backup) -ine $entry.originalSha256){Stop-CoopInstaller "原始备份哈希不匹配，拒绝卸载：$($entry.path)" 3 $null}
        $markerRel=$entry.originalBackup.Substring(0,$entry.originalBackup.LastIndexOf('\before\',[StringComparison]::OrdinalIgnoreCase))+'\rollback-journal.json'
        $marker=Get-CoopFullChildPath $GameRoot $markerRel; Assert-CoopNoReparsePath $GameRoot $marker
        if(-not (Test-Path -LiteralPath $marker -PathType Leaf)){Stop-CoopInstaller "原始备份事务记录缺失：$($entry.path)" 3 $null}
        $journal=Read-CoopJsonFile $marker '原始备份事务记录' 3
        if((Get-CoopProperty $journal 'product') -cne $script:CoopProduct){Stop-CoopInstaller "原始备份事务记录产品不匹配：$($entry.path)" 3 $null}
        $match=$false
        foreach($saved in @(Get-CoopProperty $journal 'files')){if($saved.path -ieq $entry.path -and $saved.beforeExisted -and $saved.beforeBackup -ieq $entry.originalBackup -and $saved.beforeSha256 -ieq $entry.originalSha256){$match=$true;break}}
        if(-not $match){Stop-CoopInstaller "原始备份不属于对应目标的事务快照：$($entry.path)" 3 $null}
    }
}
function Test-CoopOtherPluginsPresent {
    param([string]$GameRoot)
    foreach($relative in @('BepInEx\plugins','BepInEx\patchers')){
        $directory=Get-CoopFullChildPath $GameRoot $relative; Assert-CoopNoReparsePath $GameRoot $directory
        if(Test-Path -LiteralPath $directory -PathType Container){
            foreach($item in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)){
                if($relative -ieq 'BepInEx\plugins' -and $item.Name -ieq 'IceyCoop.Bootstrap.dll'){continue}
                if($relative -ieq 'BepInEx\plugins' -and $item.Name -ieq 'IceyCoop.Bridge.dll'){continue}
                return $true
            }
        }
    }
    return $false
}
function New-CoopInstallPlan {
    param([string]$GameRoot,$Package,$State)
    $actions=@();$stateFiles=@()
    foreach($file in $Package.files){
        $target=Get-CoopFullChildPath $GameRoot $file.path;Assert-CoopNoReparsePath $GameRoot $target;$exists=Test-Path -LiteralPath $target -PathType Leaf
        $old=if($null -ne $State){Get-CoopStateFile $State $file.path}else{$null}
        if($null -ne $State -and $null -eq $old){Stop-CoopInstaller "状态缺少受管文件：$($file.path)" 3 $null}
        $targetHash=if($exists){Get-CoopSha256File $target}else{$null}
        if($null -ne $State -and $State.status -eq 'Installed'){
            if($file.kind -ne 'config' -and (-not $exists -or $targetHash -ine $old.installedSha256)){Stop-CoopInstaller "受管文件已缺失或修改，拒绝升级：$($file.path)" 3 $null}
        }elseif($null -ne $State -and $State.status -eq 'Uninstalled'){
            if($old.preservedOnUninstall){
                if($old.preservedExists -and (-not $exists -or $targetHash -ine $old.preservedSha256)){Stop-CoopInstaller "保留文件已变化，拒绝覆盖：$($file.path)" 3 $null}
                if(-not $old.preservedExists -and $exists){Stop-CoopInstaller "卸载后出现同名文件，拒绝覆盖：$($file.path)" 3 $null}
            }elseif($old.originalExisted){
                if(-not $exists -or $targetHash -ine $old.originalSha256){Stop-CoopInstaller "卸载后原文件已变化，拒绝覆盖：$($file.path)" 3 $null}
            }elseif($exists){Stop-CoopInstaller "卸载后出现同名文件，拒绝覆盖：$($file.path)" 3 $null}
            if($file.kind -eq 'loader' -and $exists -and $targetHash -ine $file.sha256 -and -not $old.preservedOnUninstall){Stop-CoopInstaller "发现版本未知的 loader 同名文件，拒绝覆盖：$($file.path)" 3 $null}
        }elseif($file.kind -eq 'loader' -and $exists -and $targetHash -ine $file.sha256){Stop-CoopInstaller "发现版本未知的 loader 同名文件，拒绝覆盖：$($file.path)" 3 $null}
        $bytes=$file.content;if($file.kind -eq 'config'){$bytes=Merge-CoopConfig $file.content $target}
        $installedHash=Get-CoopSha256Bytes $bytes;$actions += [pscustomobject]@{path=$file.path;operation='Write';bytes=$bytes;kind=$file.kind}
        $recapture=$false
        if($null -ne $old){$origExists=$old.originalExisted;$origBackup=$old.originalBackup;$origHash=$old.originalSha256
            if($State.status -eq 'Uninstalled' -and $old.preservedOnUninstall -and $file.kind -eq 'config'){$recapture=$true;$origExists=$exists;$origBackup=$null;$origHash=$targetHash}
        }else{$origExists=$exists;$origBackup=$null;$origHash=$targetHash}
        $stateFiles += [pscustomobject]@{path=$file.path;kind=$file.kind;installedSha256=$installedHash;originalExisted=$origExists;originalBackup=$origBackup;originalSha256=$origHash;preservedOnUninstall=$false;preservedExists=$false;preservedSha256=$null;recaptureOriginal=$recapture}
    }
    return [pscustomobject]@{actions=$actions;files=$stateFiles}
}
function New-CoopResultFileList { param([object[]]$Entries); return @($Entries | ForEach-Object { [pscustomobject]@{path=$_.path;status=$_.status} }) }
function Get-CoopBackupDisplay { param([string]$GameRoot,$State); foreach($entry in $State.files){if($entry.originalBackup){return (Get-CoopFullChildPath $GameRoot $entry.originalBackup)}};return $null }
function Invoke-CoopInstall {
    param([string]$GameRoot,$Package,[scriptblock]$AfterWrite)
    $state=Get-CoopState $GameRoot
    if($null -ne $state){Assert-CoopSameFileSet $state $Package
        if($state.status -eq 'Installed'){
            Assert-CoopOwnedFilesUnmodified $GameRoot $state
            $configPath=Get-CoopFullChildPath $GameRoot $script:CoopConfigPath
            if(-not (Test-Path -LiteralPath $configPath -PathType Leaf) -or -not (Test-CoopRequiredConfig ([IO.File]::ReadAllText($configPath,[Text.Encoding]::UTF8)))){Stop-CoopInstaller 'BepInEx 必要入口配置缺失或错误' 3 $null}
            $samePayload=$true;foreach($manifestFile in $Package.files){$owned=Get-CoopStateFile $state $manifestFile.path;if($manifestFile.kind -ne 'config' -and $owned.installedSha256 -ine $manifestFile.sha256){$samePayload=$false;break}}
            if($state.buildId -ceq $Package.buildId -and $samePayload){$entries=@($state.files|ForEach-Object{[pscustomobject]@{path=$_.path;status='AlreadyInstalled'}});return [pscustomobject]@{action='Install';status='AlreadyInstalled';buildId=$Package.buildId;gameRoot=$GameRoot;backup=(Get-CoopBackupDisplay $GameRoot $state);files=(New-CoopResultFileList $entries)}}
        }
    }
    $plan=New-CoopInstallPlan $GameRoot $Package $state;$transaction=$null;$count=0
    try{
        $transaction=New-CoopTransaction $GameRoot $plan.actions
        foreach($entry in $plan.files){if($entry.originalExisted -and ($null -eq $entry.originalBackup -or $entry.recaptureOriginal)){$entry.originalBackup=$transaction.backupRelative+'\before\'+$entry.path}}
        $newState=New-CoopStateObject $Package.buildId 'Installed' $plan.files
        foreach($action in $plan.actions){$target=Get-CoopFullChildPath $GameRoot $action.path;Write-CoopAtomic $GameRoot $target $action.bytes;$count++;if($null -ne $AfterWrite){& $AfterWrite $count}}
        foreach($writtenEntry in $plan.files){
            $writtenPath=Get-CoopFullChildPath $GameRoot $writtenEntry.path;Assert-CoopNoReparsePath $GameRoot $writtenPath
            if(-not (Test-Path -LiteralPath $writtenPath -PathType Leaf) -or (Get-CoopSha256File $writtenPath) -ine $writtenEntry.installedSha256){Stop-CoopInstaller "提交前写入复核失败：$($writtenEntry.path)" 4 $null}
        }        $statePath=Get-CoopFullChildPath $GameRoot $script:CoopStatePath;Write-CoopUtf8JsonAtomic $GameRoot $statePath $newState
        $transaction.journal.status='Committed';$transaction.journal.committedAt=[DateTime]::UtcNow.ToString('o');Save-CoopJournal $GameRoot $transaction
        $entries=@($plan.files|ForEach-Object{[pscustomobject]@{path=$_.path;status='Installed'}})
        return [pscustomobject]@{action='Install';status='Installed';buildId=$Package.buildId;gameRoot=$GameRoot;backup=$transaction.backup;files=(New-CoopResultFileList $entries)}
    }catch{
        $failure=$_.Exception
        if($null -ne $transaction){$rollback=@(Invoke-CoopRollback $GameRoot $transaction);if(@($rollback).Count -gt 0){Stop-CoopInstaller ('安装失败且回滚不完整：'+($rollback -join '; ')) 4 $failure}}
        if($failure.Data.Contains('ExitCode')){throw $failure};Stop-CoopInstaller ('安装事务失败，已尝试回滚所有文件：'+$failure.ToString()) 4 $failure
    }
}
function Invoke-CoopVerify {
    param([string]$GameRoot,$Package)
    $state=Get-CoopState $GameRoot
    if($null -eq $state -or $state.status -ne 'Installed'){Stop-CoopInstaller '没有已安装的 ICEYCoop 状态' 5 $null}
    if($state.buildId -cne $Package.buildId){Stop-CoopInstaller '已安装 buildId 与当前离线包不一致' 5 $null}
    try{Assert-CoopSameFileSet $state $Package}catch{Stop-CoopInstaller '已安装文件清单与当前包不一致' 5 $_.Exception}
    foreach($entry in $state.files){
        $path=Get-CoopFullChildPath $GameRoot $entry.path;Assert-CoopNoReparsePath $GameRoot $path
        if($entry.kind -eq 'config'){
            if(-not (Test-Path -LiteralPath $path -PathType Leaf)){Stop-CoopInstaller 'BepInEx.cfg 缺失' 5 $null}
            try{$configText=[IO.File]::ReadAllText($path,[Text.Encoding]::UTF8)}catch{Stop-CoopInstaller 'BepInEx.cfg 无法读取' 5 $_.Exception}
            if(-not (Test-CoopRequiredConfig $configText)){Stop-CoopInstaller 'BepInEx 入口配置校验失败' 5 $null}
            continue
        }
        $packagedEntry=$Package.files|Where-Object{$_.path -ieq $entry.path}|Select-Object -First 1
        if($null -eq $packagedEntry -or $entry.installedSha256 -ine $packagedEntry.sha256){Stop-CoopInstaller "已安装状态与当前包SHA不符：$($entry.path)" 5 $null}
        if(-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-CoopSha256File $path) -ine $packagedEntry.sha256){Stop-CoopInstaller "已安装文件校验失败：$($entry.path)" 5 $null}
    }
    $entries=@($state.files|ForEach-Object{[pscustomobject]@{path=$_.path;status='Verified'}})
    return [pscustomobject]@{action='Verify';status='Verified';buildId=$Package.buildId;gameRoot=$GameRoot;backup=(Get-CoopBackupDisplay $GameRoot $state);files=(New-CoopResultFileList $entries)}
}
function Invoke-CoopUninstall {
    param([string]$GameRoot,[string]$PackageBuildId,[scriptblock]$AfterWrite)
    $state=Get-CoopState $GameRoot
    if($null -eq $state -or $state.status -eq 'Uninstalled'){return [pscustomobject]@{action='Uninstall';status='AlreadyUninstalled';buildId=if($null -ne $state){$state.buildId}else{$PackageBuildId};gameRoot=$GameRoot;backup=$null;files=@()}}
    Assert-CoopOwnedFilesUnmodified $GameRoot $state
    Assert-CoopOriginalBackups $GameRoot $state
    $sharedLoader=Test-CoopOtherPluginsPresent $GameRoot
    $actions=@();$newFiles=@();$resultEntries=@()
    foreach($entry in $state.files){
        $target=Get-CoopFullChildPath $GameRoot $entry.path;Assert-CoopNoReparsePath $GameRoot $target
        $exists=Test-Path -LiteralPath $target -PathType Leaf;$currentHash=if($exists){Get-CoopSha256File $target}else{$null}
        $preserve=$false;$fileStatus=''
        if($entry.kind -eq 'config' -and (-not $exists -or $currentHash -ine $entry.installedSha256 -or ($sharedLoader -and -not $entry.originalExisted))){$preserve=$true;$fileStatus='PreservedConfig'}
        elseif($entry.kind -eq 'loader' -and $sharedLoader -and -not $entry.originalExisted){$preserve=$true;$fileStatus='PreservedLoader'}
        if($preserve){
            $saved=[pscustomobject]@{path=$entry.path;kind=$entry.kind;installedSha256=$entry.installedSha256;originalExisted=$entry.originalExisted;originalBackup=$entry.originalBackup;originalSha256=$entry.originalSha256;preservedOnUninstall=$true;preservedExists=$exists;preservedSha256=$currentHash}
            $newFiles += $saved;$resultEntries += [pscustomobject]@{path=$entry.path;status=$fileStatus};continue
        }
        if($entry.originalExisted){
            $backup=Get-CoopFullChildPath $GameRoot $entry.originalBackup;$bytes=[IO.File]::ReadAllBytes($backup);$actions += [pscustomobject]@{path=$entry.path;operation='Restore';bytes=$bytes};$fileStatus='Restored'
        }else{$actions += [pscustomobject]@{path=$entry.path;operation='Delete';bytes=$null};$fileStatus='Removed'}
        $newFiles += [pscustomobject]@{path=$entry.path;kind=$entry.kind;installedSha256=$entry.installedSha256;originalExisted=$entry.originalExisted;originalBackup=$entry.originalBackup;originalSha256=$entry.originalSha256;preservedOnUninstall=$false;preservedExists=$false;preservedSha256=$null}
        $resultEntries += [pscustomobject]@{path=$entry.path;status=$fileStatus}
    }
    $transaction=$null;$count=0
    try{
        $transaction=New-CoopTransaction $GameRoot $actions
        foreach($action in $actions){
            $target=Get-CoopFullChildPath $GameRoot $action.path
            if($action.operation -eq 'Restore'){Write-CoopAtomic $GameRoot $target $action.bytes}else{Assert-CoopNoReparsePath $GameRoot $target;if(Test-Path -LiteralPath $target -PathType Leaf){Remove-Item -LiteralPath $target -Force}}
            $count++;if($null -ne $AfterWrite){& $AfterWrite $count}
        }
        $newState=New-CoopStateObject $state.buildId 'Uninstalled' $newFiles;$statePath=Get-CoopFullChildPath $GameRoot $script:CoopStatePath
        Write-CoopUtf8JsonAtomic $GameRoot $statePath $newState
        $transaction.journal.status='Committed';$transaction.journal.committedAt=[DateTime]::UtcNow.ToString('o');Save-CoopJournal $GameRoot $transaction
        return [pscustomobject]@{action='Uninstall';status='Uninstalled';buildId=$state.buildId;gameRoot=$GameRoot;backup=$transaction.backup;files=(New-CoopResultFileList $resultEntries)}
    }catch{
        $failure=$_.Exception
        if($null -ne $transaction){$rollback=@(Invoke-CoopRollback $GameRoot $transaction);if(@($rollback).Count -gt 0){Stop-CoopInstaller ('卸载失败且回滚不完整：'+($rollback -join '; ')) 4 $failure}}
        if($failure.Data.Contains('ExitCode')){throw $failure};Stop-CoopInstaller ('卸载事务失败，已尝试回滚所有文件：'+$failure.ToString()) 4 $failure
    }
}
function Invoke-CoopAction {
    [CmdletBinding()]
    param([string]$Action,[string]$GameRoot,[string]$PackageRoot,[scriptblock]$AfterWrite)
    if($Action -notin @('Install','Verify','Uninstall')){Stop-CoopInstaller "不支持的操作：$Action" 2 $null}
    if([string]::IsNullOrWhiteSpace($GameRoot) -or [string]::IsNullOrWhiteSpace($PackageRoot)){Stop-CoopInstaller 'GameRoot 与 PackageRoot 都必须提供' 2 $null}
    $mutex=$null
    try {
        $game=Get-CoopCanonicalDirectory $GameRoot 'GameRoot';$checkPayload=$Action -ne 'Uninstall';$package=Get-CoopPackage $PackageRoot $checkPayload
        $mutex=New-CoopLock $game
        try{
            if($Action -eq 'Uninstall'){
                $gameExe=Join-Path $game 'ICEY.exe'
                if(-not (Test-Path -LiteralPath $gameExe -PathType Leaf)){Stop-CoopInstaller "所选目录不是 ICEY 游戏目录：缺少 $gameExe" 2 $null}
                Assert-CoopNoReparsePath $game $gameExe
            }else{Assert-CoopGameCompatible $game $package}
            Assert-CoopGameNotRunning $game
            if($Action -eq 'Install'){return Invoke-CoopInstall $game $package $AfterWrite}
            if($Action -eq 'Verify'){return Invoke-CoopVerify $game $package}
            return Invoke-CoopUninstall $game $package.buildId $AfterWrite
        }finally{if($null -ne $mutex){try{$mutex.ReleaseMutex()}catch{};$mutex.Dispose()}}
    } catch {
        if($_.Exception.Data.Contains('ExitCode')){throw}
        if($Action -eq 'Verify'){Stop-CoopInstaller '校验操作失败' 5 $_.Exception}
        Stop-CoopInstaller '安装器前置或事务操作失败' 2 $_.Exception
    }
}