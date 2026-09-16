<#
Installs or removes Revit Model MCP for selected or detected Revit 2022-2027 years.
Release installs check available assets and report missing Revit year packages.
Keep this script ASCII-only for BOM-less Windows PowerShell 5.1 compatibility.
Examples (run on Windows from a repository checkout):
  .\install.ps1
  .\install.ps1 -Year 2024,2026 -Source Build -RegisterClaude
  .\install.ps1 -Year 2027 -Version 0.2.0 -SignThumbprint ABC123
  .\install.ps1 -Year 2026 -Uninstall
Payload writes use TEMP and APPDATA Addins. -EnableHttp explicitly requests a
URL ACL for -HttpBind and -HttpPort; the Revit listener is enabled separately. Explicit Build and
RegisterClaude commands also write their normal build/cache and Claude settings.
#>
[CmdletBinding()]
param(
    [string[]] $Year,
    [ValidateSet('Build', 'Release')] [string] $Source = 'Release',
    [string] $Version = 'latest',
    [string] $SignThumbprint,
    [switch] $RegisterClaude,
    [switch] $EnableHttp,
    [ValidatePattern('^(?:\d{1,3}\.){3}\d{1,3}$')] [string] $HttpBind = '127.0.0.1',
    [ValidateRange(1, 65535)] [int] $HttpPort = 53110,
    [switch] $Uninstall,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$tempRoot = $null
$script:releaseAssets = @()
$headers = @{ 'User-Agent' = 'revit-model-mcp-install' }
if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" }

function Get-InstalledRevitYears {
    foreach ($candidate in 2022..2027) {
        if (Test-Path "C:\Program Files\Autodesk\Revit $candidate\Revit.exe" -PathType Leaf) {
            [string]$candidate
        }
    }
}

function Get-Payload([string] $SelectedYear) {
    $yy = $SelectedYear.Substring(2)
    $stage = Join-Path $tempRoot $SelectedYear
    New-Item -ItemType Directory -Path $stage | Out-Null
    if ($Source -eq 'Build') {
        Push-Location $repo
        try {
            & dotnet build src/RevitModelMcp.Addin -c "Release.R$yy" -p:DeployAddin=false | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "R$yy build failed (exit $LASTEXITCODE)." }
        }
        finally { Pop-Location }
        $folder = Join-Path $stage 'RevitModelMcp'
        New-Item -ItemType Directory -Path $folder | Out-Null
        Copy-Item (Join-Path $repo "src\RevitModelMcp.Addin\bin\Release.R$yy\*") $folder -Recurse -Force
        Copy-Item (Join-Path $repo 'src\RevitModelMcp.Addin\RevitModelMcp.addin') $stage
    }
    else {
        $asset = "revit-model-mcp-addin-$releaseVersion-R$yy.zip"
        if ($asset -notin $script:releaseAssets) {
            throw "Release $releaseTag has no add-in package for Revit $SelectedYear (expected $asset). Install from a local build with -Source Build, or choose a release that includes R$yy."
        }
        $url = "https://github.com/sharafutdinovdi/revit-model-mcp/releases/download/$releaseTag/$asset"
        $zip = Join-Path $tempRoot $asset
        Invoke-WebRequest -Uri $url -Headers $headers -OutFile $zip -UseBasicParsing
        Expand-Archive -LiteralPath $zip -DestinationPath $stage
    }
    if (Get-ChildItem $stage -Recurse -Filter 'RevitAPI*.dll' -File) {
        throw "Revit API binaries must not be installed: $SelectedYear payload."
    }
    if (!(Test-Path (Join-Path $stage 'RevitModelMcp\RevitModelMcp.dll') -PathType Leaf)) {
        throw "Missing RevitModelMcp.dll in $SelectedYear payload."
    }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $stage 'RevitModelMcp.addin')
    if (!$manifest.SelectSingleNode('/RevitAddIns/AddIn/Assembly')) {
        throw "Missing Assembly node in $SelectedYear manifest."
    }
    return $stage
}

function Sign-Folder([string] $Folder) {
    if (!$SignThumbprint) { return }
    foreach ($dll in Get-ChildItem -LiteralPath $Folder -Recurse -Filter '*.dll' -File) {
        $signature = Set-AuthenticodeSignature -LiteralPath $dll.FullName -Certificate $certificate
        Write-Host "signature $($dll.Name): $($signature.Status)"
        if ($signature.Status -ne 'Valid') { throw "Signing failed for $($dll.FullName): $($signature.Status)" }
    }
}

function Install-Year([string] $SelectedYear, [string] $Payload) {
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$SelectedYear"
    $install = Join-Path $addins 'RevitModelMcp'
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $Payload 'RevitModelMcp.addin')
    $dll = [IO.Path]::GetFullPath((Join-Path $install 'RevitModelMcp.dll'))
    $manifest.SelectSingleNode('/RevitAddIns/AddIn/Assembly').InnerText = $dll
    # Replace the owned folder to remove stale dependencies from earlier versions.
    if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    Copy-Item (Join-Path $Payload 'RevitModelMcp\*') $install -Recurse -Force
    if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Installed DLL is missing: $dll" }
    Sign-Folder $install
    $manifest.Save((Join-Path $addins 'RevitModelMcp.addin'))
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
    Write-Host "installed $SelectedYear -> $install (version $fileVersion)"
}

function Uninstall-Year([string] $SelectedYear) {
    $addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$SelectedYear"
    foreach ($name in 'RevitModelMcp.addin', 'RevitModelMcp') {
        $path = Join-Path $addins $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    Write-Host "uninstalled $SelectedYear -> $addins"
}

function Get-HttpIdentity {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $account = $identity.Name
    }
    finally { $identity.Dispose() }
    return @{ Elevated = $elevated; Account = $account }
}

function Update-HttpUrlAcl {
    $ownershipFile = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\RevitModelMcp-http-urlacl.txt'
    if (!$Uninstall -and !$EnableHttp) { return }
    if ($Uninstall -and !(Test-Path -LiteralPath $ownershipFile)) { return }
    $address = [Net.IPAddress]::Parse($HttpBind)
    $bind = $address.ToString()
    if ($bind -eq '0.0.0.0') { $bind = '+' }
    $prefix = "http://${bind}:${HttpPort}/"
    if ($Uninstall) { $prefix = (Get-Content -LiteralPath $ownershipFile -Raw).Trim() }
    elseif (Test-Path -LiteralPath $ownershipFile) {
        $ownedPrefix = (Get-Content -LiteralPath $ownershipFile -Raw).Trim()
        if ($ownedPrefix -ne $prefix) {
            throw "This script already owns $ownedPrefix. Remove that reservation and $ownershipFile before changing the install prefix."
        }
    }
    if ($Uninstall) {
        foreach ($candidate in 2022..2027) {
            if (Test-Path (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$candidate\RevitModelMcp.addin")) {
                return
            }
        }
    }
    $identity = Get-HttpIdentity
    $elevated = $identity.Elevated
    $account = $identity.Account
    $arguments = @('http', 'add', 'urlacl', "url=$prefix", "user=$account")
    $command = 'netsh http add urlacl url={0} user="{1}"' -f $prefix, $account
    if ($Uninstall) {
        $arguments = @('http', 'delete', 'urlacl', "url=$prefix")
        $command = "netsh http delete urlacl url=$prefix"
    }
    if (!$elevated) {
        Write-Warning "HTTP URL ACL requires administrator rights. Run once from an elevated command prompt: $command"
        return
    }
    $netsh = Join-Path $env:SystemRoot 'System32\netsh.exe'
    & $netsh http show urlacl "url=$prefix" *> $null
    $exists = $LASTEXITCODE -eq 0
    if (!$Uninstall -and $exists) { return }
    if ($Uninstall -and !$exists) {
        Remove-Item -LiteralPath $ownershipFile
        return
    }
    & $netsh @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "HTTP URL ACL update failed (exit $LASTEXITCODE). Run once from an elevated command prompt: $command"
        return
    }
    if ($Uninstall) { Remove-Item -LiteralPath $ownershipFile }
    else { Set-Content -LiteralPath $ownershipFile -Value $prefix -Encoding ASCII }
}

function Register-Claude {
    if (!(Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Warning 'Claude Code is not on PATH; skipping registration.'
        return
    }
    if (!(Get-Command uv -ErrorAction SilentlyContinue)) {
        Write-Warning 'uv is not on PATH; skipping registration. Install with: winget install --id astral-sh.uv -e (or: pip install uv).'
        return
    }
    $server = Join-Path $repo 'server'
    if (!(Test-Path -LiteralPath $server -PathType Container)) { throw "Server checkout missing: $server" }
    & claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- uv run --directory "$server" revit-model-mcp | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Claude registration failed (exit $LASTEXITCODE)." }
}

try {
    if ($env:OS -ne 'Windows_NT') { throw 'Run this script on Windows.' }
    if (!$env:APPDATA -or !$env:TEMP) { throw 'APPDATA and TEMP must be set.' }
    if (!$Year) { $Year = @(Get-InstalledRevitYears) }
    $Year = @($Year | Select-Object -Unique)
    if (!$Year.Count) { throw 'No Revit 2022-2027 installation detected. Specify -Year explicitly.' }
    foreach ($selected in $Year) {
        if ($selected -notmatch '^202[2-7]$') { throw "Unsupported Revit year: $selected. Use 2022-2027." }
    }
    $running = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
    if ($running.Count -and !$Force) {
        throw "Close Revit before installing or uninstalling. Running PIDs: $($running.Id -join ', '). Use -Force to override."
    }
    if (!$Uninstall) {
        if ($SignThumbprint) {
            $thumbprint = $SignThumbprint.Replace(' ', '')
            $certificate = $null
            foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
                $certificate = Get-ChildItem $store | Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1
                if ($certificate) { break }
            }
            if (!$certificate) { throw "Certificate not found: $SignThumbprint" }
            if (!$certificate.HasPrivateKey) { throw 'Signing certificate has no private key.' }
        }
        if ($Source -eq 'Release') {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $releaseTag = "v$Version"
            if ($Version -eq 'latest') {
                $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/sharafutdinovdi/revit-model-mcp/releases/latest' -Headers $headers
                $releaseTag = [string]$release.tag_name
            }
            if ($releaseTag -notmatch '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
                throw 'Expected a release version without v, such as 0.1.0, or latest.'
            }
            if ($Version -ne 'latest') {
                $release = Invoke-RestMethod -Uri "https://api.github.com/repos/sharafutdinovdi/revit-model-mcp/releases/tags/$releaseTag" -Headers $headers
            }
            $script:releaseAssets = @($release.assets | ForEach-Object { $_.name })
            $releaseVersion = $releaseTag.Substring(1)
        }
        $tempRoot = Join-Path $env:TEMP ("revit-model-mcp-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tempRoot | Out-Null
    }
    foreach ($selected in $Year) {
        if ($Uninstall) { Uninstall-Year $selected }
        else {
            $payload = Get-Payload $selected
            Install-Year $selected $payload
        }
    }
    if ($EnableHttp -or $Uninstall) { Update-HttpUrlAcl }
    if ($RegisterClaude -and !$Uninstall) { Register-Claude }
    $action = 'Installed'
    if ($Uninstall) { $action = 'Uninstalled' }
    Write-Host "$action $($Year.Count) Revit year(s): $($Year -join ', ')."
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
finally {
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
