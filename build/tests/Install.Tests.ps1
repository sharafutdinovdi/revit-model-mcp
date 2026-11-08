Describe 'Per-year checkout installation' {
    BeforeAll {
        $installer = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'install.ps1'
        $oldDll = [object].Assembly.Location
        $newDll = [psobject].Assembly.Location
        $oldVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($oldDll).FileVersion
        $newVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($newDll).FileVersion
    }

    It 'preserves independent year results: <Fault>, existing=<Existing>' -TestCases @(
        @{ Fault = 'None'; Existing = $true }
        @{ Fault = 'Copy'; Existing = $true }
        @{ Fault = 'Promote'; Existing = $true }
        @{ Fault = 'Build'; Existing = $true }
        @{ Fault = 'None'; Existing = $false }
        @{ Fault = 'Promote'; Existing = $false }
    ) {
        param($Fault, $Existing)
        $caseRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $checkout = Join-Path $caseRoot 'checkout'
        $root = Join-Path $caseRoot 'addins'
        $temporary = Join-Path $caseRoot 'temp'
        New-Item -ItemType Directory -Path $checkout, $root, $temporary -Force | Out-Null
        Copy-Item -LiteralPath $installer -Destination (Join-Path $checkout 'install.ps1')
        $oldHashes = @{}
        foreach ($year in '2024', '2025', '2026') {
            $bin = Join-Path $checkout "src\RevitModelMcp.Addin\bin\Release.R$($year.Substring(2))"
            New-Item -ItemType Directory -Path (Join-Path $bin 'publish') -Force | Out-Null
            Copy-Item -LiteralPath $newDll -Destination (Join-Path $bin 'RevitModelMcp.dll')
            Set-Content -LiteralPath (Join-Path $bin 'new.txt') -Value "new-$year"
            Set-Content -LiteralPath (Join-Path $bin 'publish\RevitModelMcp.addin') -Value '<RevitAddIns><ManifestSettings><UseRevitContext>True</UseRevitContext></ManifestSettings><AddIn Type="Application"><Assembly>old-path</Assembly></AddIn></RevitAddIns>'
            $yearRoot = Join-Path $root $year
            New-Item -ItemType Directory -Path $yearRoot -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $yearRoot 'unrelated.addin') -Value 'not owned'
            if ($Existing) {
                $folder = Join-Path $yearRoot 'RevitModelMcp'
                New-Item -ItemType Directory -Path $folder | Out-Null
                Copy-Item -LiteralPath $oldDll -Destination (Join-Path $folder 'RevitModelMcp.dll')
                Set-Content -LiteralPath (Join-Path $folder 'stale.txt') -Value "old-$year"
                Set-Content -LiteralPath (Join-Path $yearRoot 'RevitModelMcp.addin') -Value "old manifest $year"
                $oldHashes[$year] = (Get-FileHash (Join-Path $folder 'RevitModelMcp.dll')).Hash
            }
        }
        $driver = Join-Path $caseRoot 'run.ps1'
        @'
param($Checkout, $Root, $Temporary, $Fault)
$env:TEMP = $Temporary
$env:TMP = $Temporary
$env:APPDATA = Join-Path $Temporary 'appdata'
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null
function dotnet {
    $global:LASTEXITCODE = 0
    if ($Fault -eq 'Build' -and $args -contains 'Release.R25') { $global:LASTEXITCODE = 1 }
}
function Copy-Item {
    param([Parameter(ValueFromPipeline = $true, Position = 0)]$Path, $LiteralPath,
        [Parameter(Position = 1)]$Destination, [switch]$Recurse, [switch]$Force)
    process {
        if ($Fault -eq 'Copy' -and $Destination -like '*2025*.RevitModelMcp-stage-*') {
            New-Item -ItemType Directory -Path $Destination -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $Destination 'partial.txt') -Value 'incomplete copy'
            throw 'Injected copy failure'
        }
        if ($Path -is [IO.FileSystemInfo]) { $PSBoundParameters.Path = $Path.FullName }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
}
$script:promotionFailed = $false
function Move-Item {
    param($LiteralPath, $Destination)
    if ($Fault -eq 'Promote' -and !$script:promotionFailed -and
        $LiteralPath -like '*.RevitModelMcp-stage-*' -and
        $Destination -eq (Join-Path $Root '2025\RevitModelMcp.addin')) {
        $script:promotionFailed = $true
        throw 'Injected manifest promotion failure'
    }
    Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
}
& (Join-Path $Checkout 'install.ps1') -Year 2024,2025,2026 -Source Build -Force -InstallRoot $Root
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $driver -Encoding ASCII
        $stdout = Join-Path $caseRoot 'stdout.txt'
        $stderr = Join-Path $caseRoot 'stderr.txt'
        $arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File') +
            @($driver, $checkout, $root, $temporary, $Fault | ForEach-Object { '"{0}"' -f $_ })
        $process = Start-Process powershell.exe -ArgumentList $arguments -Wait -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        $exitCode = $process.ExitCode
        $text = (Get-Content -LiteralPath $stdout, $stderr -Raw) -join "`n"
        Write-Host $text
        $text | Should -Match 'Year\s+Previous version\s+New version\s+Result'
        $expectedExit = 0
        if ($Fault -ne 'None') { $expectedExit = 1 }
        $exitCode | Should -Be $expectedExit
        foreach ($year in '2024', '2025', '2026') {
            $yearRoot = Join-Path $root $year
            $folder = Join-Path $yearRoot 'RevitModelMcp'
            $manifest = Join-Path $yearRoot 'RevitModelMcp.addin'
            $previous = 'Not installed'
            if ($Existing) { $previous = $oldVersion }
            $reportedNew = $newVersion
            if ($Fault -eq 'Build' -and $year -eq '2025') { $reportedNew = 'Unavailable' }
            $status = 'Installed'
            if ($Fault -ne 'None' -and $year -eq '2025') { $status = 'Failed' }
            $text | Should -Match ("$year\s+" + [regex]::Escape($previous) + '\s+' + [regex]::Escape($reportedNew) + '\s+' + $status)
            (Get-Content (Join-Path $yearRoot 'unrelated.addin')) | Should -Be 'not owned'
            @(Get-ChildItem -Force $yearRoot -Filter '.RevitModelMcp-*').Count | Should -Be 0
            if ($status -eq 'Failed') {
                if ($Existing) {
                    (Get-FileHash (Join-Path $folder 'RevitModelMcp.dll')).Hash | Should -Be $oldHashes[$year]
                    (Get-Content (Join-Path $folder 'stale.txt')) | Should -Be "old-$year"
                    (Get-Content $manifest) | Should -Be "old manifest $year"
                    Test-Path (Join-Path $folder 'new.txt') | Should -BeFalse
                    Test-Path (Join-Path $folder 'partial.txt') | Should -BeFalse
                }
                else {
                    Test-Path $folder | Should -BeFalse
                    Test-Path $manifest | Should -BeFalse
                }
            }
            else {
                (Get-FileHash (Join-Path $folder 'RevitModelMcp.dll')).Hash | Should -Be (Get-FileHash $newDll).Hash
                Test-Path (Join-Path $folder 'stale.txt') | Should -BeFalse
                (Get-Content (Join-Path $folder 'new.txt')) | Should -Be "new-$year"
                [xml]$xml = Get-Content $manifest
                $xml.RevitAddIns.AddIn.Assembly | Should -Be (Join-Path $folder 'RevitModelMcp.dll')
                if ([int]$year -lt 2026) { $xml.SelectSingleNode('/RevitAddIns/ManifestSettings') | Should -BeNullOrEmpty }
                else { $xml.SelectSingleNode('/RevitAddIns/ManifestSettings') | Should -Not -BeNullOrEmpty }
            }
        }
        @(Get-ChildItem $temporary -Filter 'revit-model-mcp-*').Count | Should -Be 0
    }
}
