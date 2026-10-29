param(
    [Parameter(Mandatory = $true)][string]$EvidenceName,
    [int]$Port = 57820,
    [string]$Connect = '',
    [int]$TimeoutSeconds = 90,
    [switch]$Manual,
    [switch]$PortableBoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if ($EvidenceName -notmatch '^[A-Za-z0-9_-]+$') { throw 'EvidenceName must contain only letters, numbers, underscore or hyphen' }
if ($Port -lt 1024 -or $Port -gt 65534) { throw 'Port must be between 1024 and 65534 so an adjacent refused test port is available' }
if ($TimeoutSeconds -lt 60 -or $TimeoutSeconds -gt 900) { throw 'TimeoutSeconds must be between 60 and 900' }
if ($Manual -and $TimeoutSeconds -lt 180) { throw 'Manual mode needs at least 180 seconds for human GUI actions' }

$work = $PSScriptRoot
$hostRoot = [IO.Path]::GetFullPath('D:\ICEYCoop\test-game').TrimEnd('\')
$clientRoot = [IO.Path]::GetFullPath('D:\ICEYCoop\test-game-client').TrimEnd('\')
if (-not [string]::Equals($hostRoot, 'D:\ICEYCoop\test-game', [StringComparison]::OrdinalIgnoreCase)) { throw ('Unexpected host test path: ' + $hostRoot) }
if (-not [string]::Equals($clientRoot, 'D:\ICEYCoop\test-game-client', [StringComparison]::OrdinalIgnoreCase)) { throw ('Unexpected client test path: ' + $clientRoot) }
$testRoots = @($hostRoot, $clientRoot)

if ([string]::IsNullOrWhiteSpace($Connect)) { $Connect = '127.0.0.1:' + $Port }
if ($Connect -notmatch '^[A-Za-z0-9.-]+:\d{1,5}$') { throw 'Connect must be a direct IPv4/domain endpoint in host:port form' }
$connectPort = [int]($Connect.Substring($Connect.LastIndexOf(':') + 1))
if ($connectPort -lt 1 -or $connectPort -gt 65535) { throw 'Connect port is outside 1..65535' }

$saveGuardHash = 'ADAD3D1EF38F1050741B1BCE9B471034E58A54F58F0486E08A2DADE135E1A9AF'
$refusedPort = 0
$probeFileName = 'IceyCoop.NativeLobbyProbe.dll'
$probeSource = Join-Path ([IO.Path]::GetTempPath()) ('IceyCoop.NativeLobbyProbe.' + [Guid]::NewGuid().ToString('N') + '.dll')
$probeHash = $null
$probeSourceOwned = $false
$source = Join-Path $work 'NativeLobbyProbe.cs'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$evidenceRoot = Join-Path $work ('evidence\' + $EvidenceName)
$signalsRoot = Join-Path $evidenceRoot 'signals'
$hostReport = Join-Path $evidenceRoot 'host.txt'
$clientReport = Join-Path $evidenceRoot 'client.txt'
$hostLog = Join-Path $evidenceRoot 'host-game.log'
$clientLog = Join-Path $evidenceRoot 'client-game.log'
$hostProbeTarget = Join-Path $hostRoot ('BepInEx\plugins\' + $probeFileName)
$clientProbeTarget = Join-Path $clientRoot ('BepInEx\plugins\' + $probeFileName)
$outputPaths = @(
    $evidenceRoot,
    $hostReport, $clientReport, $hostLog, $clientLog,
    (Join-Path $evidenceRoot 'host-main-menu.png'), (Join-Path $evidenceRoot 'host-lobby-open.png'),
    (Join-Path $evidenceRoot 'host-listening.png'), (Join-Path $evidenceRoot 'client-main-menu.png'),
    (Join-Path $evidenceRoot 'client-lobby-open.png'), (Join-Path $evidenceRoot 'client-joined.png')
)
foreach ($outputPath in $outputPaths) {
    if (Test-Path -LiteralPath $outputPath) { throw ('Evidence already exists; refusing overwrite: ' + $outputPath) }
}
if (-not (Test-Path -LiteralPath $source)) { throw ('Probe source missing: ' + $source) }
if (-not (Test-Path -LiteralPath $compiler)) { throw ('Framework C# compiler missing: ' + $compiler) }

foreach ($root in $testRoots) {
    $exe = Join-Path $root 'ICEY.exe'
    $plugins = Join-Path $root 'BepInEx\plugins'
    $patcher = Join-Path $root 'BepInEx\patchers\IceyCoop.TestSaveIsolation.dll'
    if (-not (Test-Path -LiteralPath $exe)) { throw ('Test-copy executable missing: ' + $exe) }
    if (-not (Test-Path -LiteralPath $plugins)) { throw ('Test-copy plugins folder missing: ' + $plugins) }
    if (-not (Test-Path -LiteralPath $patcher)) { throw ('Test save isolation patcher missing: ' + $patcher) }
    if ((Get-FileHash -LiteralPath $patcher -Algorithm SHA256).Hash -ne $saveGuardHash) { throw ('Test save isolation patcher hash mismatch: ' + $patcher) }
    $leftoverProbes = @(Get-ChildItem -LiteralPath $plugins -Filter '*Probe*.dll' -File -ErrorAction SilentlyContinue)
    if ($leftoverProbes.Count -gt 0) { throw ('Existing probe DLL in test copy; refusing to proceed: ' + $leftoverProbes[0].FullName) }
    if (-not (Test-Path -LiteralPath (Join-Path $root 'BepInEx\plugins\IceyCoop.Bootstrap.dll'))) { throw ('Staged ICEYCoop Bootstrap missing: ' + $root) }
    $bridgeLib = Join-Path $root 'BepInEx\icey-coop\lib\IceyCoop.GameBridge.dll'
    $contractLib = Join-Path $root 'BepInEx\icey-coop\lib\IceyCoop.Bridge.dll'
    if (-not (Test-Path -LiteralPath $bridgeLib) -or -not (Test-Path -LiteralPath $contractLib)) { throw ('Staged lobby bridge files missing: ' + $root) }
}
if (Test-Path -LiteralPath $hostProbeTarget) { throw ('Probe target already exists; refusing to overwrite: ' + $hostProbeTarget) }
if (Test-Path -LiteralPath $clientProbeTarget) { throw ('Probe target already exists; refusing to overwrite: ' + $clientProbeTarget) }

$bridgeFiles = @('BepInEx\plugins\IceyCoop.Bootstrap.dll', 'BepInEx\icey-coop\lib\IceyCoop.GameBridge.dll', 'BepInEx\icey-coop\lib\IceyCoop.Bridge.dll')
foreach ($relative in $bridgeFiles) {
    $hostHash = (Get-FileHash -LiteralPath (Join-Path $hostRoot $relative) -Algorithm SHA256).Hash
    $clientHash = (Get-FileHash -LiteralPath (Join-Path $clientRoot $relative) -Algorithm SHA256).Hash
    if ($hostHash -ne $clientHash) { throw ('Host/client staged bridge mismatch: ' + $relative) }
}

$runningGames = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ICEY.exe'" -ErrorAction Stop)
if ($runningGames.Count -gt 0) {
    $details = @($runningGames | ForEach-Object { 'PID ' + $_.ProcessId + ' path=' + $_.ExecutablePath }) -join '; '
    throw ('Existing ICEY process found; refusing to launch or disturb any game: ' + $details)
}

$portPreflight = New-Object System.Net.Sockets.TcpListener -ArgumentList @([System.Net.IPAddress]::Any, $Port)
try { $portPreflight.Start() }
catch { throw ('Requested host port is already occupied before the probe: ' + $Port + ' (' + $_.Exception.Message + ')') }
finally { if ($null -ne $portPreflight) { $portPreflight.Stop() } }

$managed = Join-Path $hostRoot 'ICEY_Data\Managed'
$bepCore = Join-Path $hostRoot 'BepInEx\core'
$references = @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $bepCore 'BepInEx.dll')
)
foreach ($reference in $references) { if (-not (Test-Path -LiteralPath $reference)) { throw ('Compiler reference missing: ' + $reference) } }
$compilerArgs = @('/nologo', '/noconfig', '/nostdlib', '/target:library', '/langversion:5', ('/out:' + $probeSource))
foreach ($reference in $references) { $compilerArgs += ('/reference:' + $reference) }
$compilerArgs += $source
$compilerArgs += (Join-Path $work 'RuntimePlaybackProbe.cs')
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $probeSource)) { throw 'Native lobby probe compilation failed' }
$probeSourceOwned = $true
$probeHash = (Get-FileHash -LiteralPath $probeSource -Algorithm SHA256).Hash

$saveRecords = @()
foreach ($root in $testRoots) {
    $saveBase = Join-Path $root 'BepInEx\icey-coop\coop-save.txt'
    foreach ($savePath in @($saveBase, ($saveBase + '.bak'))) {
        $existed = Test-Path -LiteralPath $savePath
        $bytes = if ($existed) { [IO.File]::ReadAllBytes($savePath) } else { $null }
        $saveRecords += [pscustomobject]@{ Path = $savePath; Existed = $existed; Bytes = $bytes }
    }
}

$savedEnvironment = @{}
foreach ($environmentKey in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
    $keyText = [string]$environmentKey
    if ($keyText.StartsWith('ICEYCOOP_', [StringComparison]::OrdinalIgnoreCase)) {
        $savedEnvironment[$keyText] = [Environment]::GetEnvironmentVariable($keyText, 'Process')
    }
}

$occupiedPortListener = $null
$hostProcess = $null
$hostIdentity = $null
$clientProcess = $null
$clientIdentity = $null
$hostInstallMayBeRemoved = $false
$clientInstallMayBeRemoved = $false
$failure = $null
$timedOut = $false
$cleanupBlocked = $false
$cleanupMessages = @()

function Set-ProbeEnvironment([string]$Role, [string]$OutputPath, [string]$LogPath) {
    foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
        $keyText = [string]$key
        if ($keyText.StartsWith('ICEYCOOP_', [StringComparison]::OrdinalIgnoreCase)) {
            [Environment]::SetEnvironmentVariable($keyText, $null, 'Process')
        }
    }
    $env:ICEYCOOP_ROLE = 'offline'
    if ($PortableBoot) {
        $env:ICEYCOOP_INSTALLER_PROBE = '1'
    }
    else {
        $env:ICEYCOOP_PROJECT_ROOT = $evidenceRoot
    }
    $env:ICEYCOOP_LOBBY_PROBE_ROLE = $Role
    $env:ICEYCOOP_LOBBY_PROBE_PORT = [string]$Port
    $env:ICEYCOOP_LOBBY_PROBE_CONNECT = $Connect
    $env:ICEYCOOP_LOBBY_PROBE_OUTPUT = $OutputPath
    $env:ICEYCOOP_LOBBY_PROBE_SIGNALS = $signalsRoot
    $env:ICEYCOOP_LOBBY_PROBE_REFUSED_PORT = [string]$script:refusedPort
    $env:ICEYCOOP_LOBBY_PROBE_TIMEOUT = [string]$TimeoutSeconds
    $env:ICEYCOOP_LOBBY_PROBE_MANUAL = if ($Manual) { '1' } else { '0' }
    $env:ICEYCOOP_TCP_ONLY = '0'
    $script:currentLogPath = $LogPath
}

function Get-ProcessIdentity([System.Diagnostics.Process]$Process, [string]$Root) {
    return [pscustomobject]@{
        Id = $Process.Id
        StartTicks = $Process.StartTime.ToUniversalTime().Ticks
        Path = [IO.Path]::GetFullPath((Join-Path $Root 'ICEY.exe'))
    }
}

function Wait-ForSignal([string]$Name, [int]$Seconds, [System.Diagnostics.Process[]]$Processes) {
    $signalPath = Join-Path $signalsRoot ($Name + '.txt')
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $signalPath) { return $true }
        foreach ($proc in $Processes) {
            if ($null -ne $proc) {
                $proc.Refresh()
                if ($proc.HasExited) { throw ('Owned ICEY process exited before signal ' + $Name + '; PID ' + $proc.Id + ' exit=' + $proc.ExitCode) }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Start-TestGame([string]$Role, [string]$Root, [string]$ReportPath, [string]$LogPath) {
    Set-ProbeEnvironment $Role $ReportPath $LogPath
    $style = if ($Manual) { 'Normal' } else { 'Hidden' }
    $proc = Start-Process -FilePath (Join-Path $Root 'ICEY.exe') -WorkingDirectory $Root `
        -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720', '-logFile', $LogPath) `
        -WindowStyle $style -PassThru
    $identity = Get-ProcessIdentity $proc $Root
    Write-Host ('OWNED_' + $Role.ToUpperInvariant() + '_ID=' + $identity.Id)
    return [pscustomobject]@{ Process = $proc; Identity = $identity }
}

function Get-Result([string]$Path, [string]$Role) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { $content = [IO.File]::ReadAllText($Path) }
    catch [IO.IOException] {
        # The probe briefly opens its report exclusively while appending. Retry
        # on the next poll; final RESULT assertions and the deadline still apply.
        $win32Error = $_.Exception.HResult -band 0xffff
        if ($win32Error -eq 32 -or $win32Error -eq 33) { return $null }
        throw
    }
    $match = [regex]::Match($content, '(?m)^.*RESULT role=' + [regex]::Escape($Role) + ' assertions=\d+ failures=\d+.*$')
    if ($match.Success) { return $match.Value }
    return $null
}

function Stop-OwnedProcess($Identity) {
    if ($null -eq $Identity) { return }
    $currentCim = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $Identity.Id) -ErrorAction SilentlyContinue
    $currentProcess = Get-Process -Id $Identity.Id -ErrorAction SilentlyContinue
    $owned = $currentCim -and $currentProcess -and -not [string]::IsNullOrEmpty($currentCim.ExecutablePath) -and
        [string]::Equals([IO.Path]::GetFullPath($currentCim.ExecutablePath), $Identity.Path, [StringComparison]::OrdinalIgnoreCase) -and
        $currentProcess.StartTime.ToUniversalTime().Ticks -eq $Identity.StartTicks
    if (-not $owned) { return }
    [void]$currentProcess.CloseMainWindow()
    if (-not $currentProcess.WaitForExit(5000)) {
        $currentCim = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $Identity.Id) -ErrorAction SilentlyContinue
        $currentProcess = Get-Process -Id $Identity.Id -ErrorAction SilentlyContinue
        $owned = $currentCim -and $currentProcess -and -not [string]::IsNullOrEmpty($currentCim.ExecutablePath) -and
            [string]::Equals([IO.Path]::GetFullPath($currentCim.ExecutablePath), $Identity.Path, [StringComparison]::OrdinalIgnoreCase) -and
            $currentProcess.StartTime.ToUniversalTime().Ticks -eq $Identity.StartTicks
        if ($owned) {
            Stop-Process -Id $Identity.Id -Force
            Start-Sleep -Seconds 1
        }
    }
    $currentCim = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $Identity.Id) -ErrorAction SilentlyContinue
    if ($currentCim -and -not [string]::IsNullOrEmpty($currentCim.ExecutablePath) -and
        [string]::Equals([IO.Path]::GetFullPath($currentCim.ExecutablePath), $Identity.Path, [StringComparison]::OrdinalIgnoreCase)) {
        $script:cleanupBlocked = $true
        $script:cleanupMessages += ('Owned ICEY process still active: PID ' + $Identity.Id)
    }
}

try {
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $signalsRoot -Force | Out-Null
    $gamesBeforeInstall = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ICEY.exe'" -ErrorAction Stop)
    if ($gamesBeforeInstall.Count -gt 0) { throw 'An ICEY process appeared after preflight; refusing probe deployment' }
    Copy-Item -LiteralPath $probeSource -Destination $hostProbeTarget
    if ((Get-FileHash -LiteralPath $hostProbeTarget -Algorithm SHA256).Hash -ne $probeHash) { throw 'Host probe install hash mismatch' }
    $hostInstallMayBeRemoved = $true
    Copy-Item -LiteralPath $probeSource -Destination $clientProbeTarget
    if ((Get-FileHash -LiteralPath $clientProbeTarget -Algorithm SHA256).Hash -ne $probeHash) { throw 'Client probe install hash mismatch' }
    $clientInstallMayBeRemoved = $true

    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $refusedPort = $listener.LocalEndpoint.Port
    $listener.Stop()
    if ($refusedPort -eq $Port -or $refusedPort -eq $connectPort) { throw 'Ephemeral refused-test port unexpectedly collides with room endpoint' }

    if ($Manual) {
        $startedHost = Start-TestGame 'host' $hostRoot $hostReport $hostLog
        $hostProcess = $startedHost.Process
        $hostIdentity = $startedHost.Identity
        if (-not (Wait-ForSignal 'host-ui-ready' $TimeoutSeconds @($hostProcess))) { throw 'Manual host did not expose host-ui-ready before timeout' }
        Write-Output 'MANUAL_ACTION: Click the visible host lobby button “开始监听”; the native start action should enter the game.'
        if (-not (Wait-ForSignal 'host-ready' $TimeoutSeconds @($hostProcess))) { throw 'Manual host did not reach host-ready before timeout' }
        $startedClient = Start-TestGame 'client' $clientRoot $clientReport $clientLog
        $clientProcess = $startedClient.Process
        $clientIdentity = $startedClient.Identity
        if (-not (Wait-ForSignal 'client-ui-ready' $TimeoutSeconds @($hostProcess, $clientProcess))) { throw 'Manual client did not expose client-ui-ready before timeout' }
        Write-Output 'MANUAL_ACTION: Click the visible client lobby button “连接房间”; the endpoint is prefilled from -Connect.'
    }
    else {
        $startedHost = Start-TestGame 'host' $hostRoot $hostReport $hostLog
        $hostProcess = $startedHost.Process
        $hostIdentity = $startedHost.Identity
        $startedClient = Start-TestGame 'client' $clientRoot $clientReport $clientLog
        $clientProcess = $startedClient.Process
        $clientIdentity = $startedClient.Identity
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $hostResult = $null
    $clientResult = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $hostResult = Get-Result $hostReport 'host'
        $clientResult = Get-Result $clientReport 'client'
        if ($hostResult -and $hostResult -notmatch 'failures=0') { throw ('Host probe failed: ' + $hostResult) }
        if ($clientResult -and $clientResult -notmatch 'failures=0') { throw ('Client probe failed: ' + $clientResult) }
        foreach ($entry in @(@{ Process = $hostProcess; Role = 'host' }, @{ Process = $clientProcess; Role = 'client' })) {
            if ($null -eq $entry.Process) { continue }
            $entry.Process.Refresh()
            if ($entry.Process.HasExited -and ((($entry.Role -eq 'host') -and -not $hostResult) -or (($entry.Role -eq 'client') -and -not $clientResult))) {
                throw ($entry.Role + ' ICEY exited before writing its final RESULT (exit ' + $entry.Process.ExitCode + ')')
            }
        }
        if ($hostResult -and $clientResult) { break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $hostResult -or -not $clientResult) {
        $timedOut = $true
        throw ('Probe pair did not finish within ' + $TimeoutSeconds + ' seconds')
    }
    if ($hostResult -notmatch 'failures=0') { throw ('Host probe failed: ' + $hostResult) }
    if ($clientResult -notmatch 'failures=0') { throw ('Client probe failed: ' + $clientResult) }

    $requiredScreenshots = @('host-main-menu.png', 'host-lobby-open.png', 'host-listening.png', 'client-main-menu.png', 'client-lobby-open.png', 'client-joined.png')
    foreach ($image in $requiredScreenshots) {
        $imagePath = Join-Path $evidenceRoot $image
        $imageDeadline = [DateTime]::UtcNow.AddSeconds(8)
        while (-not (Test-Path -LiteralPath $imagePath) -and [DateTime]::UtcNow -lt $imageDeadline) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $imagePath)) { throw ('Required UI screenshot missing: ' + $imagePath) }
    }
}
catch {
    $failure = $_.Exception.Message
}
finally {
    try { Stop-OwnedProcess $clientIdentity }
    catch { $cleanupBlocked = $true; $cleanupMessages += ('Client process cleanup failed: ' + $_.Exception.Message) }
    try { Stop-OwnedProcess $hostIdentity }
    catch { $cleanupBlocked = $true; $cleanupMessages += ('Host process cleanup failed: ' + $_.Exception.Message) }

    try {
        $remainingTestGames = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ICEY.exe'" -ErrorAction Stop | Where-Object {
            if ([string]::IsNullOrEmpty($_.ExecutablePath)) { $true; return }
            $path = [IO.Path]::GetFullPath($_.ExecutablePath)
            [string]::Equals($path, (Join-Path $hostRoot 'ICEY.exe'), [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals($path, (Join-Path $clientRoot 'ICEY.exe'), [StringComparison]::OrdinalIgnoreCase)
        })
        foreach ($remaining in $remainingTestGames) {
            $cleanupBlocked = $true
            $cleanupMessages += ('Test-copy ICEY still active; refusing save restore/probe removal: PID ' + $remaining.ProcessId)
        }
    }
    catch {
        $cleanupBlocked = $true
        $cleanupMessages += ('Could not verify test-copy process cleanup: ' + $_.Exception.Message)
    }

    foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
        $keyText = [string]$key
        if ($keyText.StartsWith('ICEYCOOP_', [StringComparison]::OrdinalIgnoreCase)) { [Environment]::SetEnvironmentVariable($keyText, $null, 'Process') }
    }
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process') }

    if (-not $cleanupBlocked) {
        foreach ($saveRecord in $saveRecords) {
            if ($saveRecord.Existed) { [IO.File]::WriteAllBytes($saveRecord.Path, $saveRecord.Bytes) }
            elseif (Test-Path -LiteralPath $saveRecord.Path) { Remove-Item -LiteralPath $saveRecord.Path -Force }
        }
        foreach ($entry in @(@{ Target = $clientProbeTarget; MayRemove = $clientInstallMayBeRemoved }, @{ Target = $hostProbeTarget; MayRemove = $hostInstallMayBeRemoved })) {
            if ($entry.MayRemove -and (Test-Path -LiteralPath $entry.Target)) {
                if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -eq $probeHash) {
                    Remove-Item -LiteralPath $entry.Target -Force
                    Write-Output ('OWN_PROBE_REMOVED_BY_HASH=' + $entry.Target)
                }
                else { Write-Output ('OWN_PROBE_LEFT_IN_PLACE_HASH_CHANGED=' + $entry.Target) }
            }
        }
    }
    else {
        foreach ($message in $cleanupMessages) { Write-Output ('CLEANUP_SAFETY_BLOCKED=' + $message) }
        Write-Output 'CLEANUP_SAFETY_BLOCKED: coop saves and installed probes were left untouched because a test-copy process may still be active.'
        if ($null -ne $failure) { $failure += '; cleanup safety blocked' } else { $failure = 'cleanup safety blocked; test-copy process may still be active' }
    }

    if ($probeSourceOwned -and (Test-Path -LiteralPath $probeSource)) {
        if ((Get-FileHash -LiteralPath $probeSource -Algorithm SHA256).Hash -eq $probeHash) { Remove-Item -LiteralPath $probeSource -Force }
    }
}

foreach ($reportPath in @($hostReport, $clientReport, $hostLog, $clientLog)) {
    if (Test-Path -LiteralPath $reportPath) {
        Write-Output ('--- ' + $reportPath + ' ---')
        Write-Output ([IO.File]::ReadAllText($reportPath))
    }
}
if ($failure) { throw ('FAIL: ' + $failure) }
Write-Output ('NATIVE_LOBBY_PROBE_PASSED evidence=' + $evidenceRoot)
