param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $root 'artifacts') -Force | Out-Null
$bridge = Get-Content (Join-Path $root 'src/GameBridge/BridgeContext.cs') -Raw
$match = [regex]::Match($bridge, 'public\s+static\s+void\s+SetRole\s*\(')
if (-not $match.Success) { throw 'BridgeContext.SetRole not found' }
$open = $bridge.IndexOf('{', $match.Index); $depth=0; $end=-1
for ($i=$open; $i -lt $bridge.Length; $i++) { if ($bridge[$i] -eq '{') {$depth++} elseif ($bridge[$i] -eq '}') {$depth--; if ($depth -eq 0) {$end=$i;break}} }
if ($end -lt 0) { throw 'SetRole braces are unbalanced' }
$signature=$bridge.Substring($match.Index,$open-$match.Index).Trim()
$body=$bridge.Substring($open,$end-$open+1)
$generated="namespace IceyCoop.GameBridge { public static partial class BridgeContext { $signature $body } }"
$artifact=Join-Path $root 'artifacts/VisibilitySetRole.g.cs'; Set-Content $artifact $generated
$project=Join-Path $PSScriptRoot '../tests/Visibility.Specs/Visibility.Specs.csproj'
$projectDir=Split-Path -Parent $project
$config=Join-Path $projectDir 'NuGet.Config'
Set-Content $config '<configuration><packageSources><clear /></packageSources></configuration>'
dotnet restore $project --configfile $config
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
dotnet run --project $project --no-restore
if ($LASTEXITCODE -ne 0) { throw "visibility regression failed with exit code $LASTEXITCODE" }
