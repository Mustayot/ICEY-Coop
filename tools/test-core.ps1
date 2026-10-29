# test-core.ps1 - run the pure Core/Protocol specs (modern runtime, shared source).
param(
    [string]$ProjectRoot = 'D:\ICEYCoop'
)
$ErrorActionPreference = 'Stop'
$proj   = Join-Path $ProjectRoot 'tests\Core.Specs'
$report = Join-Path $ProjectRoot 'artifacts\core-tests.json'
Push-Location -LiteralPath $ProjectRoot
try {
    dotnet run --project $proj -- --suite All --report $report
    $testExit = $LASTEXITCODE
}
finally { Pop-Location }
exit $testExit
