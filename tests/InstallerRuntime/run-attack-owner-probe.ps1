# run-attack-owner-probe.ps1 - T07 player->enemy attack attribution.
# Host runs the probe (real wave spawn + P2 attack through the production InputAdapter);
# the client is a plain peer so the host actually creates P2.
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Scene = 'C2L1S7',
    [int]$TimeoutSeconds = 240,
    [double]$GrowthMinutes = 0,
    [string]$GrowthSwitchTo = "",
    [double]$GrowthSwitchAt = 0,
    [switch]$WipeTest,
    [switch]$FallTest,
    [string]$TcpPort = '57590',
    [string]$UdpPort = '57591'
)
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[A-Za-z0-9_-]+$') { throw 'Invalid evidence name' }
if ($TimeoutSeconds -lt 120 -or $TimeoutSeconds -gt 2400) { throw 'TimeoutSeconds must be between 120 and 2400' }

$work = $PSScriptRoot
$hostRoot = [IO.Path]::GetFullPath('D:\ICEYCoop\test-game').TrimEnd('\')
$clientRoot = [IO.Path]::GetFullPath('D:\ICEYCoop\test-game-client').TrimEnd('\')
$guardHash = 'ADAD3D1EF38F1050741B1BCE9B471034E58A54F58F0486E08A2DADE135E1A9AF'
$probeName = 'IceyCoop.AttackOwnerProbe.dll'
$probeSource = Join-Path $work $probeName
$build = Join-Path $work 'build-attack-owner-probe.ps1'

$hostReport = Join-Path $work ($Name + '-host.txt')
$hostLog = Join-Path $work ($Name + '-host-player.log')
$clientLog = Join-Path $work ($Name + '-client-player.log')
$hostIdentity = $null
$clientIdentity = $null
$cleanupBlocked = $false
$cleanupMessages = @()
$probeInstalled = $false

function Get-TargetGameProcesses {
    $matches = @()
    try { $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'ICEY.exe'" -ErrorAction Stop) }
    catch { throw ('Could not verify ICEY process identities: ' + $_.Exception.Message) }
    $targetPaths = @(
        [IO.Path]::GetFullPath((Join-Path $hostRoot 'ICEY.exe')),
        [IO.Path]::GetFullPath((Join-Path $clientRoot 'ICEY.exe'))
    )
    foreach ($process in $processes) {
        $path = [string]$process.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($path)) { throw ('ICEY process identity is unknown; refusing to continue (PID ' + $process.ProcessId + ')') }
        try { $path = [IO.Path]::GetFullPath($path) }
        catch { throw ('ICEY process path cannot be resolved; refusing to continue (PID ' + $process.ProcessId + ')') }
        foreach ($targetPath in $targetPaths) {
            if ([string]::Equals($path, $targetPath, [StringComparison]::OrdinalIgnoreCase)) { $matches += $process; break }
        }
    }
    return @($matches)
}

function Assert-NoTargetGameProcesses {
    $matches = @(Get-TargetGameProcesses)
    if ($matches.Count -gt 0) {
        $details = @($matches | ForEach-Object { 'PID ' + $_.ProcessId + ' path=' + $_.ExecutablePath }) -join '; '
        throw ('A target test copy is active; refusing to continue: ' + $details)
    }
}

function Assert-NoGameAtRoot {
    param([string]$Root)
    $expectedPath = [IO.Path]::GetFullPath((Join-Path $Root 'ICEY.exe'))
    foreach ($process in @(Get-TargetGameProcesses)) {
        if ([string]::Equals([IO.Path]::GetFullPath([string]$process.ExecutablePath), $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('Target test copy is already active: PID ' + $process.ProcessId + ' path=' + $expectedPath)
        }
    }
}

function Get-OwnedProcessIdentity {
    param([System.Diagnostics.Process]$Process,[string]$Root)
    if ($null -eq $Process) { throw 'Cannot capture identity for a missing process object' }
    $expectedPath = [IO.Path]::GetFullPath((Join-Path $Root 'ICEY.exe'))
    $row = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $Process.Id) -ErrorAction SilentlyContinue
    if ($null -eq $row -or [string]::IsNullOrWhiteSpace([string]$row.ExecutablePath)) { throw ('Started ICEY process identity is unknown; refusing cleanup by name (PID ' + $Process.Id + ')') }
    $actualPath = [IO.Path]::GetFullPath([string]$row.ExecutablePath)
    if (-not [string]::Equals($actualPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) { throw ('Started ICEY path does not match requested test copy: PID ' + $Process.Id + ' path=' + $actualPath) }
    $Process.Refresh()
    if ($Process.HasExited) { throw ('Started ICEY exited before identity capture: PID ' + $Process.Id) }
    return [pscustomobject]@{ Id = $Process.Id; StartTicks = $Process.StartTime.ToUniversalTime().Ticks; Path = $expectedPath }
}

function Get-OwnedProcessIfSame {
    param($Identity)
    if ($null -eq $Identity) { return $null }
    $row = Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId = ' + $Identity.Id) -ErrorAction SilentlyContinue
    $process = Get-Process -Id $Identity.Id -ErrorAction SilentlyContinue
    if ($null -eq $row -and $null -eq $process) { return $null }
    if ($null -eq $row -or $null -eq $process -or [string]::IsNullOrWhiteSpace([string]$row.ExecutablePath)) {
        throw ('ICEY process identity is incomplete; refusing to stop or restore (PID ' + $Identity.Id + ')')
    }
    $currentPath = [IO.Path]::GetFullPath([string]$row.ExecutablePath)
    $pathMatches = [string]::Equals($currentPath, $Identity.Path, [StringComparison]::OrdinalIgnoreCase)
    $startMatches = $process.StartTime.ToUniversalTime().Ticks -eq $Identity.StartTicks
    if ((-not $pathMatches) -or (-not $startMatches)) {
        throw ('ICEY PID was reused or changed identity; refusing to stop or restore (PID ' + $Identity.Id + ')')
    }
    return $process
}

function Stop-OwnedProcess {
    param($Identity)
    if ($null -eq $Identity) { return }
    $process = Get-OwnedProcessIfSame $Identity
    if ($null -eq $process) { return }
    [void]$process.CloseMainWindow()
    if (-not $process.WaitForExit(5000)) {
        $process = Get-OwnedProcessIfSame $Identity
        if ($null -ne $process) {
            Stop-Process -Id $Identity.Id -Force -ErrorAction Stop
            if (-not $process.WaitForExit(5000)) { throw ('Owned ICEY did not exit after stop request (PID ' + $Identity.Id + ')') }
        }
    }
    if ($null -ne (Get-OwnedProcessIfSame $Identity)) { throw ('Owned ICEY remains active after cleanup (PID ' + $Identity.Id + ')') }
}

Assert-NoTargetGameProcesses
foreach ($existing in @($hostReport, $hostLog, $clientLog)) {
    if (Test-Path -LiteralPath $existing) { throw ('Evidence already exists: ' + $existing) }
}

foreach ($root in @($hostRoot, $clientRoot)) {
    $patcher = Join-Path $root 'BepInEx\patchers\IceyCoop.TestSaveIsolation.dll'
    if (-not (Test-Path -LiteralPath $patcher)) { throw ('Isolation patcher missing in ' + $root) }
    if ((Get-FileHash -LiteralPath $patcher).Hash -ne $guardHash) { throw ('Isolation patcher mismatch in ' + $root) }
    $leftover = @(Get-ChildItem -LiteralPath (Join-Path $root 'BepInEx\plugins') -Filter '*Probe*.dll' -ErrorAction SilentlyContinue)
    if ($leftover.Count -gt 0) { throw ('A probe DLL is already installed in ' + $root) }
}

# Probe source changes must be present in the DLL used by this run.
& $build -GameRoot $hostRoot
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed; refusing to run an older DLL' }
if (-not (Test-Path -LiteralPath $probeSource)) { throw 'Probe build did not produce its DLL' }
$probeHash = (Get-FileHash -LiteralPath $probeSource -Algorithm SHA256).Hash

# --- coop-save guard: keep both copies' save files exactly as found
$saveRecords = @()
foreach ($root in @($hostRoot, $clientRoot)) {
    $save = Join-Path $root 'BepInEx\icey-coop\coop-save.txt'
    $bak = $save + '.bak'
    foreach ($file in @($save, $bak)) {
        $existed = Test-Path -LiteralPath $file
        $bytes = if ($existed) { [IO.File]::ReadAllBytes($file) } else { $null }
        $saveRecords += [pscustomobject]@{ Path = $file; Existed = $existed; Bytes = $bytes }
    }
}

$hostProc = $null
$clientProc = $null
$failure = $null
$savedEnv = @{}
foreach ($key in [Environment]::GetEnvironmentVariables('Process').Keys) {
    $name = [string]$key
    if ($name.StartsWith('ICEYCOOP_', [StringComparison]::OrdinalIgnoreCase)) {
        $savedEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
}

try {
    Assert-NoTargetGameProcesses
    Copy-Item -LiteralPath $probeSource -Destination (Join-Path $hostRoot ('BepInEx\plugins\' + $probeName))
    $probeInstalled = $true

    foreach ($key in [Environment]::GetEnvironmentVariables('Process').Keys) {
        $name = [string]$key
        if ($name.StartsWith('ICEYCOOP_', [StringComparison]::OrdinalIgnoreCase)) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    }

    $env:ICEYCOOP_TCP_PORT = $TcpPort
    $env:ICEYCOOP_UDP_PORT = $UdpPort
    $env:ICEYCOOP_DEMO_INPUT = '0'
    $env:ICEYCOOP_PROBE_SCENE = $Scene
    $env:ICEYCOOP_ROLE = 'host'
    $env:ICEYCOOP_CONNECT = ('127.0.0.1:' + $TcpPort)
    if ($GrowthMinutes -gt 0) { $env:ICEYCOOP_GROWTH_MINUTES = $GrowthMinutes.ToString([System.Globalization.CultureInfo]::InvariantCulture) }
    if ($WipeTest) { $env:ICEYCOOP_WIPE_TEST = '1' }
    if ($FallTest) { $env:ICEYCOOP_FALL_TEST = '1' }
    if ($GrowthSwitchTo) { $env:ICEYCOOP_GROWTH_SWITCH_TO = $GrowthSwitchTo }
    if ($GrowthSwitchAt -gt 0) { $env:ICEYCOOP_GROWTH_SWITCH_AT = $GrowthSwitchAt.ToString([System.Globalization.CultureInfo]::InvariantCulture) }
    $env:ICEYCOOP_PROBE_OUTPUT = $hostReport
    $env:ICEYCOOP_ISOLATION_REPORT = Join-Path $work ($Name + '-host-isolation.txt')
    Assert-NoTargetGameProcesses
    $hostProc = Start-Process -FilePath (Join-Path $hostRoot 'ICEY.exe') -WorkingDirectory $hostRoot `
        -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '540', '-logFile', $hostLog) `
        -WindowStyle Hidden -PassThru
    $hostIdentity = Get-OwnedProcessIdentity $hostProc $hostRoot
    Write-Output ('OWNED_HOST_PID=' + $hostIdentity.Id + ' START_TICKS=' + $hostIdentity.StartTicks + ' PATH=' + $hostIdentity.Path)

    Start-Sleep -Seconds 12

    $env:ICEYCOOP_ROLE = 'client'
    # The probe drives P2 itself; a demo-input client would race it on the same input
    # channel (its frames restart at seq 1 and get interleaved with the injected ones).
    $env:ICEYCOOP_DEMO_INPUT = '0'
    $env:ICEYCOOP_PROBE_OUTPUT = ''
    $env:ICEYCOOP_ISOLATION_REPORT = Join-Path $work ($Name + '-client-isolation.txt')
    Assert-NoGameAtRoot $clientRoot
    if ($null -eq (Get-OwnedProcessIfSame $hostIdentity)) { throw 'Owned host exited before the client copy could start' }
    $clientProc = Start-Process -FilePath (Join-Path $clientRoot 'ICEY.exe') -WorkingDirectory $clientRoot `
        -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '540', '-logFile', $clientLog) `
        -WindowStyle Hidden -PassThru
    $clientIdentity = Get-OwnedProcessIdentity $clientProc $clientRoot
    Write-Output ('OWNED_CLIENT_PID=' + $clientIdentity.Id + ' START_TICKS=' + $clientIdentity.StartTicks + ' PATH=' + $clientIdentity.Path)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-Path -LiteralPath $hostReport) {
            $text = Get-Content -LiteralPath $hostReport -Raw -ErrorAction SilentlyContinue
            if ($text -match '(?m)^RESULT assertions=\d+ failures=\d+\s*$') { break }
        }
        $hostProc.Refresh()
        if ($hostProc.HasExited) { throw ('Host exited before writing its result (exit ' + $hostProc.ExitCode + ')') }
        $clientProc.Refresh()
        if ($clientProc.HasExited) { throw ('Client exited before host probe result (exit ' + $clientProc.ExitCode + ')') }
    }

    if (-not (Test-Path -LiteralPath $hostReport)) { throw 'Probe wrote no report before the deadline' }
    $reportText = Get-Content -LiteralPath $hostReport -Raw
    Write-Output $reportText
    if ($reportText -notmatch '(?m)^RESULT assertions=\d+ failures=0\s*$') {
        $failure = 'Attack owner probe reported failures. See ' + $hostReport
    }
}
catch {
    $failure = $_.Exception.Message
}
finally {
    foreach ($identity in @($clientIdentity, $hostIdentity)) {
        if ($null -ne $identity) {
            try { Stop-OwnedProcess $identity }
            catch { $cleanupBlocked = $true; $cleanupMessages += $_.Exception.Message }
        }
    }
    Start-Sleep -Seconds 2
    try { Assert-NoTargetGameProcesses }
    catch { $cleanupBlocked = $true; $cleanupMessages += $_.Exception.Message }

    if (-not $cleanupBlocked) {
        $target = Join-Path $hostRoot ('BepInEx\plugins\' + $probeName)
        if ($probeInstalled -and (Test-Path -LiteralPath $target)) {
            try {
                Assert-NoTargetGameProcesses
                if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $probeHash) {
                    throw 'Installed probe hash changed; refusing probe removal and coop-save restore'
                }
                Remove-Item -LiteralPath $target -Force
                Write-Output 'OWN_PROBE_REMOVED_BY_HASH'
            }
            catch { $cleanupBlocked = $true; $cleanupMessages += $_.Exception.Message }
        }
    }

    if (-not $cleanupBlocked) {
        try {
            Assert-NoTargetGameProcesses
            foreach ($record in $saveRecords) {
                if ($record.Existed) { [IO.File]::WriteAllBytes($record.Path, $record.Bytes) }
                elseif (Test-Path -LiteralPath $record.Path) { Remove-Item -LiteralPath $record.Path -Force }
            }
            Write-Output 'COOP_SAVE_SNAPSHOT_RESTORED'
        }
        catch { $cleanupBlocked = $true; $cleanupMessages += $_.Exception.Message }
    }

    foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
        $name = [string]$key
        if ($name.StartsWith('ICEYCOOP_', [StringComparison]::OrdinalIgnoreCase)) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    }
    foreach ($name in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnv[$name], 'Process') }
    if ($cleanupBlocked) {
        foreach ($message in $cleanupMessages) { Write-Output ('CLEANUP_SAFETY_BLOCKED=' + $message) }
        Write-Output 'CLEANUP_SAFETY_BLOCKED: active or unidentified ICEY process; probe removal and coop-save restore were skipped.'
    }
}

if ($cleanupBlocked) { throw ('CLEANUP SAFETY BLOCKED: ' + ($cleanupMessages -join '; ')) }
if ($failure) { throw $failure }
Write-Output 'ATTACK_OWNER_PROBE_PASSED'
