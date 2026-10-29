[CmdletBinding()]
param([string]$Configuration='RuntimeHealth',[string]$Version='2026.10.29-health.1')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bin=Join-Path $root ('artifacts\bin\'+$Configuration)
$packageRoot=Join-Path $root 'installer'
$manifestPath=Join-Path $packageRoot 'manifest.json'
$manifest=[IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
$info=[IO.File]::ReadAllText((Join-Path $bin 'build-info.json')) | ConvertFrom-Json
$allowed=@('BepInEx\plugins\IceyCoop.Bootstrap.dll','BepInEx\plugins\IceyCoop.Bridge.dll',
 'BepInEx\icey-coop\lib\IceyCoop.GameBridge.dll','BepInEx\icey-coop\lib\IceyCoop.Bridge.dll',
 'BepInEx\icey-coop\lib\IceyCoop.Core.dll','BepInEx\icey-coop\lib\IceyCoop.Protocol.dll',
 'BepInEx\icey-coop\lib\IceyCoop.Transport.dll')
$mods=@($manifest.files | Where-Object {$_.kind -eq 'mod'})
if($mods.Count -ne $allowed.Count){throw 'Unexpected mod placement count'}
foreach($file in $mods){
 if($allowed -notcontains $file.path){throw ('Unexpected mod placement: '+$file.path)}
 $source=Join-Path $bin ([IO.Path]::GetFileName($file.path))
 $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
 $expected=$info.outputsSha256.([IO.Path]::GetFileName($file.path))
 if($hash -ne $expected){throw ('Build-info mismatch: '+$source)}
}
foreach($file in $mods){
 $source=Join-Path $bin ([IO.Path]::GetFileName($file.path))
 $target=Join-Path (Join-Path $packageRoot 'payload') $file.path
 Copy-Item -LiteralPath $source -Destination $target
 $file.sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
 $file.bytes=(Get-Item -LiteralPath $target).Length
}
$manifest.buildId=([string]$info.outputsSha256.'IceyCoop.GameBridge.dll').Substring(0,12).ToLowerInvariant()
$manifest.version=$Version
[IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $bin 'build-info.json') -Destination (Join-Path $packageRoot 'build-info.json')
foreach($file in $manifest.files){
 $path=Join-Path (Join-Path $packageRoot 'payload') $file.path
 if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256){throw ('Final payload mismatch: '+$file.path)}
}
Write-Output ('PAYLOAD_VERIFIED build='+$manifest.buildId+' files='+@($manifest.files).Count)
