# build.ps1 - compile IceyCoop assemblies with the game's old-Mono references.
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\build.ps1 -Configuration Debug
# Build never deploys or starts the game. Stage with tools\stage-test.ps1.
param(
    [string]$Configuration = 'Debug',
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\ICEY'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc not found: $csc" }

$managed = Join-Path $GameRoot 'ICEY_Data\Managed'
$bepCore = Join-Path $ProjectRoot 'installer\payload\BepInEx\core'

$mscorlib   = Join-Path $managed 'mscorlib.dll'
$system     = Join-Path $managed 'System.dll'
$systemCore = Join-Path $managed 'System.Core.dll'
$unity      = Join-Path $managed 'UnityEngine.dll'
$gameDll    = Join-Path $managed 'Assembly-CSharp.dll'
$bepin      = Join-Path $bepCore 'BepInEx.dll'
$harmony    = Join-Path $bepCore '0Harmony.dll'

foreach ($p in @($mscorlib, $system, $systemCore, $unity, $gameDll, $bepin, $harmony)) {
    if (-not (Test-Path $p)) { throw "reference missing: $p" }
}

$srcRoot = Join-Path $ProjectRoot 'src'
$outDir  = Join-Path $ProjectRoot ('artifacts\bin\' + $Configuration)
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Invoke-Csc {
    param([string]$Name, [string]$OutFile, [string[]]$Sources, [string[]]$Refs)
    $outPath = Join-Path $outDir $OutFile
    $cscArgs = @('/nologo', '/noconfig', '/nostdlib', '/target:library', '/langversion:5', '/utf8output', "/out:$outPath")
    foreach ($r in $Refs)    { $cscArgs += "/reference:$r" }
    foreach ($s in $Sources) { $cscArgs += $s }
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed for $Name (exit $LASTEXITCODE)" }
    Write-Host "compiled $OutFile"
}

function Find-Bytes {
    # Ordinal binary search for a byte sequence inside a byte array.
    # Returns the 0-based offset, or -1 when not found. Culture-insensitive by
    # construction: we compare raw bytes, never text.
    param([byte[]]$Haystack, [byte[]]$Needle)
    if ($Needle.Length -eq 0) { return 0 }
    $last = $Haystack.Length - $Needle.Length
    for ($i = 0; $i -le $last; $i++) {
        $match = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($Haystack[$i + $j] -ne $Needle[$j]) { $match = $false; break }
        }
        if ($match) { return $i }
    }
    return -1
}

$baseRefs = @($mscorlib, $system, $systemCore)

$bridgeSrc = @((Join-Path $srcRoot 'Bridge\BridgeContract.cs'), (Join-Path $srcRoot 'Bridge\LobbyContract.cs'))
Invoke-Csc 'Bridge' 'IceyCoop.Bridge.dll' $bridgeSrc $baseRefs

$coreSrc = @(Get-ChildItem (Join-Path $srcRoot 'Core') -Filter *.cs | Select-Object -ExpandProperty FullName)
Invoke-Csc 'Core' 'IceyCoop.Core.dll' $coreSrc $baseRefs

$bridgeDll = Join-Path $outDir 'IceyCoop.Bridge.dll'
$coreDll   = Join-Path $outDir 'IceyCoop.Core.dll'

$protocolSrc = @(Get-ChildItem (Join-Path $srcRoot 'Protocol') -Filter *.cs | Select-Object -ExpandProperty FullName)
$protocolRefs = @($mscorlib, $system, $systemCore, $coreDll)
Invoke-Csc 'Protocol' 'IceyCoop.Protocol.dll' $protocolSrc $protocolRefs

$protocolDll = Join-Path $outDir 'IceyCoop.Protocol.dll'

$transportSrc = @(Get-ChildItem (Join-Path $srcRoot 'Transport') -Filter *.cs | Select-Object -ExpandProperty FullName)
$transportRefs = @($mscorlib, $system, $systemCore, $coreDll, $protocolDll)
Invoke-Csc 'Transport' 'IceyCoop.Transport.dll' $transportSrc $transportRefs

$transportDll = Join-Path $outDir 'IceyCoop.Transport.dll'

$gameBridgeRefs = @($mscorlib, $system, $systemCore, $unity, $gameDll, $harmony, $bridgeDll, $coreDll, $protocolDll, $transportDll)
$gameBridgeSrc = @(
    (Join-Path $srcRoot 'GameBridge\EntryPoint.cs'),
    (Join-Path $srcRoot 'GameBridge\ProbeBridge.cs'),
    (Join-Path $srcRoot 'GameBridge\Lobby\ProbeBridge.Lobby.cs'),
    (Join-Path $srcRoot 'GameBridge\Lobby\LobbyRuntimeCleanup.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\CoopLobbyScreen.cs'),
    (Join-Path $srcRoot 'GameBridge\BindingManifest.cs'),
(Join-Path $srcRoot 'GameBridge\BuildIdentity.cs'),
    (Join-Path $srcRoot 'GameBridge\PatchInstaller.cs'),
    (Join-Path $srcRoot 'GameBridge\Diagnostics\TraceWriter.cs'),
    (Join-Path $srcRoot 'GameBridge\Diagnostics\RuntimeHealthMonitor.cs'),
    (Join-Path $srcRoot 'GameBridge\Progression\NativeSaveGuard.cs'),
    (Join-Path $srcRoot 'GameBridge\BridgeContext.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\ActorRegistry.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\T03ProbePatches.cs'),
(Join-Path $srcRoot 'GameBridge\Actors\ClientIsolationPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\T04MovePatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\T06SkillPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\ActorFactory.cs'),
(Join-Path $srcRoot 'GameBridge\Actors\P2CollisionSanitize.cs'),
(Join-Path $srcRoot 'GameBridge\Actors\P2Anchor.cs'),
(Join-Path $srcRoot 'GameBridge\Progression\CoopSaveWriter.cs'),
(Join-Path $srcRoot 'GameBridge\Progression\CoopRestore.cs'),
(Join-Path $srcRoot 'GameBridge\Progression\PartyCheckpointRestore.cs'),
(Join-Path $srcRoot 'GameBridge\Progression\PartyRevive.cs'),
(Join-Path $srcRoot 'GameBridge\Progression\HostStartupResume.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\NativePlayerBinding.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\NativeActorBinding.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\NativeOwnerReadPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\NativeChargingPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\T05BindingPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\NativeDeepCopy.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\T07HurtPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\T08WeaponAnimPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\CombatRouter.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\EntityAdapter.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\EncounterAdapter.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\T07AttackPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\TDamageCalcPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\T08LedgerPatches.cs'),
(Join-Path $srcRoot 'GameBridge\Combat\SoftCollisionOwnerPatch.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\HurtOutcomeCapture.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\ExecutionRouter.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\ExecutionCandidatePatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\TargetDirector.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\T11TargetPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\RewardRouter.cs'),
    (Join-Path $srcRoot 'GameBridge\Combat\T11RewardPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Network\InputAdapter.cs'),
    (Join-Path $srcRoot 'GameBridge\Network\InputSampler.cs'),
    (Join-Path $srcRoot 'GameBridge\Network\SessionOrchestrator.cs'),
    (Join-Path $srcRoot 'GameBridge\Network\SessionTermination.cs'),
    (Join-Path $srcRoot 'GameBridge\Network\ReconnectCredentialStore.cs'),
    (Join-Path $srcRoot 'GameBridge\Network\PendingJoinAdmission.cs'),
    (Join-Path $srcRoot 'GameBridge\Network\JoinTransaction.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\G25DeathPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Actors\RoomEndRequest.cs'),
    (Join-Path $srcRoot 'GameBridge\World\ReplicaFactory.cs'),
(Join-Path $srcRoot 'GameBridge\World\ClientIsolation.cs'),
(Join-Path $srcRoot 'GameBridge\World\SceneLoadGate.cs'),
(Join-Path $srcRoot 'GameBridge\World\PartyAssemblyGate.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\ActorPresenter.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\EnemyPresenter.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\SpineBodies.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\EffectCueCapture.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\EffectTargetPath.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\ReplicaAnimGuard.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\ClientBossHud.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\CameraHudAdapter.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\T16HudCameraPatches.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\EncounterPresenter.cs'),
    (Join-Path $srcRoot 'GameBridge\Story\StoryCapture.cs'),
    (Join-Path $srcRoot 'GameBridge\Story\CameraCueCapture.cs'),
    (Join-Path $srcRoot 'GameBridge\Story\ShiSync.cs'),
    (Join-Path $srcRoot 'GameBridge\World\InteractionBridge.cs'),
    (Join-Path $srcRoot 'GameBridge\World\PromptMirror.cs'),
    (Join-Path $srcRoot 'GameBridge\Presentation\ClientUiGuard.cs')
)
Invoke-Csc 'GameBridge' 'IceyCoop.GameBridge.dll' $gameBridgeSrc $gameBridgeRefs

$bootstrapRefs = @($mscorlib, $system, $systemCore, $unity, $bridgeDll, $bepin)
$bootstrapSrc = @(
    (Join-Path $srcRoot 'Bootstrap\Plugin.cs'),
    (Join-Path $srcRoot 'Bootstrap\BootstrapPathResolver.cs'),
    (Join-Path $srcRoot 'Bootstrap\PrivateAssemblyLoader.cs'),
    (Join-Path $srcRoot 'Bootstrap\Marker.cs')
)
Invoke-Csc 'Bootstrap' 'IceyCoop.Bootstrap.dll' $bootstrapSrc $bootstrapRefs

# T23: the relay node is a standalone console exe so hosting a room does not need the game.
# It is compiled from source against the *desktop* framework, not against the game's mscorlib
# 2.0.5.0 profile: the game-side assemblies cannot be loaded by a plain .NET Framework process
# (they demand System, Version=2.0.5.0, which only Unity ships). Only the three transport files
# the relay actually needs are pulled in, so the exe stays self-contained.
$relaySrc = @(
    (Join-Path $srcRoot 'Transport\RoomRegistry.cs'),
    (Join-Path $srcRoot 'Transport\RelayTarget.cs'),
    (Join-Path $srcRoot 'Transport\RelayServer.cs'),
    (Join-Path $ProjectRoot 'tools\relay-host\RelayHost.cs')
)
$relayOut = Join-Path $outDir 'IceyCoop.RelayHost.exe'
$relayArgs = @('/nologo', '/target:exe', '/langversion:5', '/utf8output', "/out:$relayOut")
foreach ($f in $relaySrc)   { $relayArgs += $f }
& $csc @relayArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed for RelayHost (exit $LASTEXITCODE)" }
Write-Host 'compiled IceyCoop.RelayHost.exe'

# Static self-checks (mirror the two silent-failure guards):
# 1) BepInPlugin attribute marker must exist, otherwise BepInEx loads 0 plugins.
# 2) Bootstrap metadata must not leak Assembly-CSharp, otherwise chainloader drops the dll.
$bootDll  = Join-Path $outDir 'IceyCoop.Bootstrap.dll'
$bootBytes = [System.IO.File]::ReadAllBytes($bootDll)
$bepinNeedle = [System.Text.Encoding]::UTF8.GetBytes('BepInPlugin')
$acNeedle    = [System.Text.Encoding]::UTF8.GetBytes('Assembly-CSharp')
if ((Find-Bytes $bootBytes $bepinNeedle) -lt 0) {
    throw 'self-check failed: IceyCoop.Bootstrap.dll has no BepInPlugin marker'
}
if ((Find-Bytes $bootBytes $acNeedle) -ge 0) {
    throw 'self-check failed: IceyCoop.Bootstrap.dll metadata leaks Assembly-CSharp'
}
Write-Host 'self-check ok: BepInPlugin present, no Assembly-CSharp leak'

$outputs = @('IceyCoop.Bridge.dll', 'IceyCoop.Core.dll', 'IceyCoop.Protocol.dll', 'IceyCoop.Transport.dll', 'IceyCoop.GameBridge.dll', 'IceyCoop.Bootstrap.dll')
$outputsSha256 = @{}
foreach ($f in $outputs) {
    $fp = Join-Path $outDir $f
    $outputsSha256[$f] = (Get-FileHash -Path $fp -Algorithm SHA256).Hash
}

$info = @{
    configuration = $Configuration
    builtAt       = (Get-Date).ToString('o')
    csc           = $csc
    outputs       = $outputs
    outputsSha256 = $outputsSha256
}
$infoPath = Join-Path $outDir 'build-info.json'
$info | ConvertTo-Json | Set-Content -Path $infoPath -Encoding UTF8
Write-Host "build info: $infoPath"

