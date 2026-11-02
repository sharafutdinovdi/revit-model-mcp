[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Msi,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string]$PreviousMsi,
    [string]$Model = 'C:\Projects\rmm-test\2027\Snowdon Towers Sample Architectural.rvt',
    [string]$ServerDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'server'),
    [string]$OutputDirectory = 'C:\Projects\rmm-test\out\tests',
    [ValidateRange(30, 900)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
$results = [Collections.Generic.List[object]]::new()
function Result([string]$Check, [bool]$Passed, [string]$Detail) {
    $results.Add([pscustomobject]@{ Check = $Check; Result = $(if ($Passed) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
}
function Assert-NoForeignRevit([int]$OwnedId = 0) {
    $foreign = @(Get-CimInstance Win32_Process -Filter "Name='Revit.exe'" | Where-Object ProcessId -ne $OwnedId)
    if ($foreign.Count) { throw 'A foreign Revit.exe is running. Smoke aborted; no foreign process will be closed.' }
}
function Run-Msi([string]$Operation, [string]$Log, [string]$Package = $Msi, [string]$Scope = 'ALLUSERS=2 MSIINSTALLPERUSER=1') {
    $process = Start-Process msiexec.exe -ArgumentList "$Operation `"$Package`" /qn /norestart $Scope /l*v `"$Log`"" -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010) { throw "msiexec $Operation failed: $($process.ExitCode); $Log" }
}
Assert-NoForeignRevit
$Msi = (Resolve-Path -LiteralPath $Msi).Path
if ([IO.Path]::GetFileName($Msi) -notmatch 'SingleUser.*\.msi$') { throw 'A SingleUser MSI is required.' }
if (!(Test-Path -LiteralPath $Model -PathType Leaf)) { throw "Model not found: $Model" }
$Model = (Resolve-Path -LiteralPath $Model).Path
$ServerDirectory = (Resolve-Path -LiteralPath $ServerDirectory).Path
$installed = @(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -eq 'Model MCP' -and $_.WindowsInstaller -eq 1 })
$previousScope = '0'
if ($installed.Count) {
    if ($installed.Count -ne 1 -or !$PreviousMsi) { throw 'Supply -PreviousMsi for the single existing Model MCP installation.' }
    $PreviousMsi = (Resolve-Path -LiteralPath $PreviousMsi).Path
    if ([IO.Path]::GetFileName($PreviousMsi) -notmatch 'SingleUser.*\.msi$') { throw 'Only a previous SingleUser package can be restored.' }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase($PreviousMsi, 0)
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductCode'")
    $view.Execute()
    if ($view.Fetch().StringData(1) -ne $installed[0].PSChildName) { throw 'PreviousMsi does not match the installed ProductCode.' }
    $previousScope = $installer.ProductInfo($installed[0].PSChildName, 'AssignmentType')
}
$years = @(2022..2027 | Where-Object { Test-Path "C:\Program Files\Autodesk\Revit $_\Revit.exe" })
if (2027 -notin $years) { throw 'Revit 2027 must be installed for the interactive smoke.' }
$runDirectory = Join-Path $OutputDirectory ('smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $runDirectory | Out-Null
$backup = Join-Path $runDirectory 'backup'
New-Item -ItemType Directory $backup | Out-Null
$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
$backedUp = [Collections.Generic.List[int]]::new()
$taskName = 'rmm-smoke-' + [guid]::NewGuid().ToString('N')
$ownedId = 0
$installationAttempted = $false
$previousRemoved = $false
$settings = Join-Path $env:LOCALAPPDATA 'RevitModelMcp\settings.json'
$hadSettings = Test-Path -LiteralPath $settings
$taskRegistered = $false
try {
    if ($hadSettings) { Copy-Item -LiteralPath $settings -Destination (Join-Path $backup 'settings.json') }
    if ($installed.Count) { Copy-Item -LiteralPath $PreviousMsi -Destination (Join-Path $backup 'previous.msi') }
    foreach ($year in $years) {
        $target = Join-Path $addinRoot $year
        $saved = Join-Path $backup $year
        New-Item -ItemType Directory -Force $saved | Out-Null
        foreach ($name in 'RevitModelMcp.addin', 'RevitModelMcp') {
            $source = Join-Path $target $name
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $saved -Recurse }
        }
        $backedUp.Add($year)
    }
    Assert-NoForeignRevit
    if ($installed.Count) {
        Run-Msi '/x' (Join-Path $runDirectory 'remove-previous.log') (Join-Path $backup 'previous.msi')
        $previousRemoved = $true
        Result 'Remove previous MSI' $true $installed[0].DisplayVersion
    }
    $installationAttempted = $true
    Run-Msi '/i' (Join-Path $runDirectory 'install.log')
    Result 'Install per user' $true ([IO.Path]::GetFileName($Msi))
    foreach ($year in $years) {
        try {
            $manifestPath = Join-Path $addinRoot "$year\RevitModelMcp.addin"
            [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
            $entry = @($manifest.RevitAddIns.AddIn | Where-Object FullClassName -eq 'RevitModelMcp.Application')
            if ($entry.Count -ne 1) { throw "Invalid manifest for Revit $year" }
            $assembly = [string]$entry[0].Assembly
            if (![IO.Path]::IsPathRooted($assembly)) { $assembly = Join-Path (Split-Path $manifestPath) $assembly }
            $version = (Get-Item -LiteralPath $assembly).VersionInfo.ProductVersion
            $versionMatches = ($version -split '\+')[0] -eq $ExpectedVersion
            Result "Manifest/DLL $year" $versionMatches $version
        }
        catch { Result "Manifest/DLL $year" $false $_.Exception.Message }
    }
    if ($results.Where({ $_.Result -eq 'FAIL' }).Count) { throw 'Installed add-in validation failed.' }
    $launcher = Join-Path $runDirectory 'launch.ps1'
    $launchSource = @'
param([string]$Model, [string]$PidFile)
$ErrorActionPreference = 'Stop'
$process = Start-Process 'C:\Program Files\Autodesk\Revit 2027\Revit.exe' -ArgumentList "`"$Model`"" -PassThru
[IO.File]::WriteAllText($PidFile, [string]$process.Id)
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class RmmSmokeDialogs {
    public delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr word, IntPtr value);
    public static void Dismiss(int owned) {
        EnumWindows((window, state) => {
            uint process; GetWindowThreadProcessId(window, out process);
            if (process != owned) return true;
            var kind = new StringBuilder(256); GetClassName(window, kind, kind.Capacity);
            var title = new StringBuilder(512); GetWindowText(window, title, title.Capacity);
            if (kind.ToString() != "#32770") return true;
            if (title.ToString().Contains("Безопасность") || title.ToString().Contains("Security"))
                PostMessage(window, 0x466, (IntPtr)1001, IntPtr.Zero);
            if (title.ToString().Contains("Необработанные ссылки") || title.ToString().Contains("Unresolved References"))
                PostMessage(window, 0x466, (IntPtr)2, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
}
"@
while (!$process.HasExited) {
    [RmmSmokeDialogs]::Dismiss($process.Id)
    Start-Sleep -Seconds 2
    $process.Refresh()
}
'@
    $launchSource | Set-Content -LiteralPath $launcher -Encoding UTF8
    $pidFile = Join-Path $runDirectory 'revit.pid'
    $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$launcher`" -Model `"$Model`" -PidFile `"$pidFile`""
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive
    Assert-NoForeignRevit
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
    $taskRegistered = $true
    Start-ScheduledTask -TaskName $taskName
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (!(Test-Path $pidFile) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
    if (!(Test-Path $pidFile)) { throw 'The interactive task did not report a Revit PID.' }
    $ownedId = [int](Get-Content -LiteralPath $pidFile -Raw)

    $heartbeatPath = Join-Path $env:LOCALAPPDATA "RevitModelMcp\instance_$ownedId.json"
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        Assert-NoForeignRevit $ownedId
        if (!(Get-Process -Id $ownedId -ErrorAction SilentlyContinue)) { throw 'Revit exited before heartbeat.' }
        if (Test-Path $heartbeatPath) {
            $heartbeat = Get-Content -Raw $heartbeatPath | ConvertFrom-Json
            if ($heartbeat.processId -eq $ownedId -and $heartbeat.documentPath -eq $Model -and
                (Get-Date $heartbeat.updatedUtc) -gt (Get-Date).AddSeconds(-60)) { $ready = $true; break }
        }
        Start-Sleep -Seconds 2
    }
    if (!$ready) { throw 'Timed out waiting for a fresh heartbeat for the test model.' }
    Result 'Heartbeat 2027' $true "PID $ownedId"
    $ping = Join-Path $runDirectory 'ping.py'
    @'
import asyncio
import json
import sys
from revit_model_mcp.pipe_host import LocalPipeHost, PipeJobHost
from revit_model_mcp.revit_channel import Job, RevitChannel

class PipeOnlyHost(LocalPipeHost):
    async def select_job(self, job):
        selected, addressed = await super().select_job(job)
        assert isinstance(selected, PipeJobHost), 'Named pipe required; file fallback refused'
        return selected, addressed

async def main():
    host = PipeOnlyHost()
    try:
        instance = next(item for item in host.heartbeats() if item['processId'] == int(sys.argv[1]))
        connection = await host.connection(instance)
        assert connection.hello['protocol'] == 'pipe/1', connection.hello
        job = Job.ping()
        job.payload['targetProcessId'] = int(sys.argv[1])
        result = await RevitChannel(host).execute(job, timeout_seconds=60)
        print(json.dumps(result, ensure_ascii=True))
        assert result.get('success') is True and result.get('data') == 'pong', result
    finally:
        await host.aclose()

asyncio.run(main())
'@ | Set-Content -LiteralPath $ping -Encoding UTF8
    Push-Location $ServerDirectory
    try {
        $previousPythonPath = $env:PYTHONPATH
        $env:PYTHONPATH = $ServerDirectory
        & uv run --project $ServerDirectory python $ping $ownedId 2>&1 | Tee-Object -FilePath (Join-Path $runDirectory 'ping.log')
        if ($LASTEXITCODE -ne 0) { throw 'revit_ping over named pipe failed.' }
    }
    finally { $env:PYTHONPATH = $previousPythonPath; Pop-Location }
    Result 'revit_ping (pipe/1)' $true 'pong'
}
catch { Result 'Smoke' $false $_.Exception.Message }
finally {
    if (!$ownedId -and (Test-Path (Join-Path $runDirectory 'revit.pid'))) {
        $ownedId = [int](Get-Content (Join-Path $runDirectory 'revit.pid') -Raw)
    }
    if ($ownedId) {
        try {
            $process = Get-Process -Id $ownedId -ErrorAction SilentlyContinue
            if ($process -and $process.ProcessName -eq 'Revit') {
                $null = $process.CloseMainWindow()
                if (!$process.WaitForExit(20000)) {
                    Stop-Process -Id $ownedId -Force
                    if (!$process.WaitForExit(10000)) { throw 'The owned Revit process did not exit.' }
                }
            }
            Result 'Close owned Revit' $true "PID $ownedId"
        }
        catch { Result 'Close owned Revit' $false $_.Exception.Message }
    }
    if ($taskRegistered) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
    if ($installationAttempted) {
        try { Assert-NoForeignRevit; Run-Msi '/x' (Join-Path $runDirectory 'uninstall.log'); Result 'Uninstall' $true 'SingleUser MSI removed' }
        catch { Result 'Uninstall' $false $_.Exception.Message }
    }
    if ($previousRemoved) {
        try {
            Assert-NoForeignRevit
            $scope = if ($previousScope -eq '1') { 'ALLUSERS=1' } else { 'ALLUSERS=2 MSIINSTALLPERUSER=1' }
            Run-Msi '/i' (Join-Path $runDirectory 'restore-previous.log') (Join-Path $backup 'previous.msi') $scope
            Result 'Restore previous MSI' $true $installed[0].DisplayVersion
        }
        catch { Result 'Restore previous MSI' $false $_.Exception.Message }
    }
    foreach ($year in $backedUp) {
        try {
            Assert-NoForeignRevit
            $target = Join-Path $addinRoot $year
            foreach ($name in 'RevitModelMcp.addin', 'RevitModelMcp') {
                $destination = Join-Path $target $name
                if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
                $saved = Join-Path $backup "$year\$name"
                if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $target -Recurse }
            }
            $dll = Join-Path $target 'RevitModelMcp\RevitModelMcp.dll'
            $savedDll = Join-Path $backup "$year\RevitModelMcp\RevitModelMcp.dll"
            $same = (!(Test-Path $savedDll)) -or ((Get-FileHash $dll).Hash -eq (Get-FileHash $savedDll).Hash)
            $version = if (Test-Path $dll) { (Get-Item $dll).VersionInfo.ProductVersion } else { 'No previous add-in' }
            Result "Restore $year" $same $version
        }
        catch { Result "Restore $year" $false $_.Exception.Message }
    }
    try {
        Assert-NoForeignRevit
        if ($hadSettings -and (Test-Path (Join-Path $backup 'settings.json'))) {
            Copy-Item -LiteralPath (Join-Path $backup 'settings.json') -Destination $settings -Force
        }
        if (!$hadSettings -and (Test-Path -LiteralPath $settings)) { Remove-Item -LiteralPath $settings -Force }
    }
    catch { Result 'Restore settings' $false $_.Exception.Message }
    $results | Format-Table -AutoSize | Out-String -Width 240 | Tee-Object -FilePath (Join-Path $runDirectory 'summary.txt') | Write-Host
    $results | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $runDirectory 'summary.csv')
    Write-Host "Evidence and backup: $runDirectory"
}
if ($results.Result -contains 'FAIL') { exit 1 }
