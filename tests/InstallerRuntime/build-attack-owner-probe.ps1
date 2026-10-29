param(
    [string]$GameRoot = 'D:\SteamLibrary\steamapps\common\ICEY'
)
$ErrorActionPreference = 'Stop'
$GameRoot = [System.IO.Path]::GetFullPath($GameRoot)
$managed = Join-Path $GameRoot 'ICEY_Data\Managed'
$core = Join-Path $GameRoot 'BepInEx\core'
$references = @(
    (Join-Path $managed 'mscorlib.dll'),
    (Join-Path $managed 'System.dll'),
    (Join-Path $managed 'System.Core.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $core 'BepInEx.dll'),
    (Join-Path $core '0Harmony.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) { throw ('Missing compiler reference: ' + $reference) }
}
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Framework C# compiler was not found' }
$output = Join-Path $PSScriptRoot 'IceyCoop.AttackOwnerProbe.dll'
$source = Join-Path $PSScriptRoot 'AttackOwnerProbe.cs'
$refs = @($references | ForEach-Object { '/reference:' + $_ })
& $compiler /nologo /noconfig /nostdlib /target:library /langversion:5 ('/out:' + $output) @refs $source
if ($LASTEXITCODE -ne 0) { throw 'Attack owner probe compilation failed' }
Write-Output ('BUILT ' + $output)
