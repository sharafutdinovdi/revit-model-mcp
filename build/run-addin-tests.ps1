[CmdletBinding()]
param(
    [ValidateRange(2022, 2027)][int]$Year = 2027,
    [string]$Model = $env:RMM_TEST_MODEL,
    [string]$OutputDirectory = 'C:\Projects\rmm-test\out\tests'
)
$ErrorActionPreference = 'Stop'
if (Get-CimInstance Win32_Process -Filter "Name='Revit.exe'") {
    throw 'A Revit process is already running. Close it before starting this isolated test run.'
}
if (!$Model) { $Model = 'C:\Projects\rmm-test\2027\Snowdon Towers Sample Architectural.rvt' }
if (!(Test-Path -LiteralPath $Model -PathType Leaf)) { throw "Test model not found: $Model" }
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository 'tests\RevitModelMcp.Addin.Tests\RevitModelMcp.Addin.Tests.csproj'
$configuration = 'Release.R' + ($Year - 2000)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$previousModel = $env:RMM_TEST_MODEL
try {
    $report = Join-Path $OutputDirectory "addin-$Year.trx"
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
    $env:RMM_TEST_MODEL = (Resolve-Path -LiteralPath $Model).Path
    & dotnet run --project $project -c $configuration -p:RunRevitTests=true `
        -p:DeployAddin=false -p:PublishAddin=false -p:IsRepackable=false -p:LaunchRevit=false `
        -- --report-trx --report-trx-filename "addin-$Year.trx" --results-directory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Revit $Year tests failed (exit $LASTEXITCODE). See $OutputDirectory." }
    if (!(Test-Path $report)) { throw "No TRX report was written: $report" }
    [xml]$trx = Get-Content -Raw $report
    $counters = $trx.TestRun.ResultSummary.Counters
    $counters | Format-List
    if ([int]$counters.passed -lt 15 -or [int]$counters.failed -ne 0) {
        throw 'At least 15 passing tests and no failures are required; skipped tests do not satisfy the gate.'
    }
}
finally { $env:RMM_TEST_MODEL = $previousModel }
