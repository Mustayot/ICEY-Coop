param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $PSScriptRoot 'LobbyTcpForwarder.cs'
if ([string]::IsNullOrEmpty($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot 'LobbyTcpForwarder.exe'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    (Join-Path $framework 'mscorlib.dll'),
    (Join-Path $framework 'System.dll'),
    (Join-Path $framework 'System.Core.dll')
)
foreach ($path in @($compiler, $source) + $references) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required file missing: $path" }
}

$outputDirectory = [IO.Path]::GetDirectoryName($OutputPath)
if (-not [string]::IsNullOrEmpty($outputDirectory) -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$args = @('/nologo', '/noconfig', '/nostdlib', '/target:exe', '/langversion:5', ('/out:' + $OutputPath))
foreach ($reference in $references) { $args += ('/reference:' + $reference) }
$args += $source
& $compiler @args
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath)) {
    throw "LobbyTcpForwarder compilation failed (csc exit $LASTEXITCODE)"
}
Write-Output ('COMPILED=' + $OutputPath)

