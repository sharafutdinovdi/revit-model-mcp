[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'run-addin-tests.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
$previousProgramFiles = $env:ProgramFiles
$previousModel = $env:RMM_TEST_MODEL
$previousExitCode = $global:LASTEXITCODE
$state = @{ Passed = 19; Failed = 0; ExitCode = 0; Arguments = @(); Model = $null }

function Get-CimInstance { return $null }
function dotnet {
    $state.Arguments = @($args)
    $state.Model = $env:RMM_TEST_MODEL
    $directory = $args[[Array]::IndexOf($args, '--results-directory') + 1]
    $filename = $args[[Array]::IndexOf($args, '--report-trx-filename') + 1]
    $executed = $state.Passed + $state.Failed
    Set-Content -LiteralPath (Join-Path $directory $filename) -Value "<TestRun><ResultSummary><Counters total='$executed' executed='$executed' passed='$($state.Passed)' failed='$($state.Failed)' /></ResultSummary></TestRun>"
    $global:LASTEXITCODE = $state.ExitCode
}

try {
    $env:ProgramFiles = Join-Path $temporary 'ProgramFiles'
    $installation = Join-Path $env:ProgramFiles 'Autodesk\Revit 2026'
    New-Item -ItemType Directory -Path $installation -Force | Out-Null
    $model = Join-Path $temporary 'sample.rvt'
    Set-Content -LiteralPath $model -Value ''
    $env:RMM_TEST_MODEL = $model
    $output = Join-Path $temporary 'results'

    foreach ($version in @('8.0.0', '10.0.0')) {
        Set-Content -LiteralPath (Join-Path $installation 'RevitAPI.runtimeconfig.json') -Value "{`"runtimeOptions`":{`"frameworks`":[{`"name`":`"Microsoft.NETCore.App`",`"version`":`"$version`"}]}}"
        & $runner -Year 2026 -OutputDirectory $output | Out-Null
        if ($state.Arguments -notcontains "-p:RevitTestRuntimeVersion=$version") { throw "Runtime $version was not selected." }
        if ($state.Model -ne $model) { throw 'The model environment was not passed to the test host.' }
        Write-Output "PASS: installed .NET $version selects the matching test host runtime."
    }

    $state.Passed = 0
    try {
        & $runner -Year 2026 -OutputDirectory $output | Out-Null
        throw 'A zero-test run unexpectedly succeeded.'
    }
    catch {
        if ($_.Exception.Message -notlike 'At least 15 passing tests*') { throw }
        Write-Output 'PASS: zero executed tests fail the runner.'
    }

    $state.Passed = 18
    $state.Failed = 1
    $state.ExitCode = 1
    try {
        & $runner -Year 2026 -OutputDirectory $output | Out-Null
        throw 'A failing test run unexpectedly succeeded.'
    }
    catch {
        if ($_.Exception.Message -notlike 'Revit 2026 tests failed (exit 1).*') { throw }
        Write-Output 'PASS: a failing test host fails the runner.'
    }
    if ($env:RMM_TEST_MODEL -ne $model) { throw 'The runner did not restore the model environment.' }
}
finally {
    $env:ProgramFiles = $previousProgramFiles
    $env:RMM_TEST_MODEL = $previousModel
    Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
    $global:LASTEXITCODE = $previousExitCode
}
