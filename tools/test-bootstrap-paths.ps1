$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = Split-Path -Parent $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw ('C# 5 compiler not found: ' + $compiler) }

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('icey-bootstrap-paths-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $tempRoot 'BootstrapPathResolverSpecs.exe'
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    $source = Join-Path $root 'tests\BootstrapPathResolverSpecs.cs'
    $resolver = Join-Path $root 'src\Bootstrap\BootstrapPathResolver.cs'
    & $compiler /nologo /noconfig /target:exe /langversion:5 (('/out:' + $output)) $resolver $source
    if ($LASTEXITCODE -ne 0) { throw ('Bootstrap path specs compile failed (exit ' + $LASTEXITCODE + ')') }

    & $output
    if ($LASTEXITCODE -ne 0) { throw ('Bootstrap path specs failed (exit ' + $LASTEXITCODE + ')') }
    Write-Output 'RESULT=Passed'
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
