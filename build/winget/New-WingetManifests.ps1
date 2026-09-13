[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$ReleaseTag,
    [Parameter(Mandatory)]
    [string]$OutputDir,
    # Uses existing MSIs in the repository's output directory and computes real hashes.
    [switch]$SkipDownload
)

$ErrorActionPreference = 'Stop'
if ($ReleaseTag -ne "v$Version") { throw 'ReleaseTag must match Version' }
$repository = 'https://github.com/sharafutdinovdi/revit-model-mcp'
$installerDirectory = Join-Path $PSScriptRoot '../../output'
New-Item -ItemType Directory -Force $installerDirectory, $OutputDir | Out-Null
$values = @{ VERSION = $Version; RELEASE_TAG = $ReleaseTag }
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer

foreach ($scope in @(
    @{ Name = 'SingleUser'; Key = 'SINGLE_USER' },
    @{ Name = 'MultiUser'; Key = 'MULTI_USER' }
)) {
    $name = "RevitModelMcp-$Version-$($scope.Name).msi"
    $url = "$repository/releases/download/$ReleaseTag/$name"
    $path = Join-Path $installerDirectory $name
    if (!$SkipDownload) { Invoke-WebRequest -Uri $url -OutFile $path }
    if (!(Test-Path $path)) { throw "Missing installer: $path" }
    $path = (Resolve-Path $path).Path
    $values["$($scope.Key)_URL"] = $url
    $values["$($scope.Key)_SHA256"] = (Get-FileHash $path -Algorithm SHA256).Hash
    $database = $windowsInstaller.OpenDatabase($path, 0)
    $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')
    $view.Execute()
    $record = $view.Fetch()
    $values["$($scope.Key)_PRODUCT_CODE"] = $record.StringData(1)
    $view.Close()
}

foreach ($template in Get-ChildItem "$PSScriptRoot/*.yaml.template") {
    $content = Get-Content $template.FullName -Raw
    foreach ($key in $values.Keys) {
        $content = $content.Replace("{{$key}}", $values[$key])
    }
    if ($content -match '\{\{[^}]+\}\}') { throw "Unresolved placeholder in $($template.Name)" }
    $destination = Join-Path $OutputDir ($template.Name -replace '\.template$', '')
    Set-Content $destination $content.TrimEnd() -Encoding utf8
    Write-Host "Generated $destination"
}
