[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Release')][string]$Tag,
    [Parameter(Mandatory, ParameterSetName = 'Build')][string]$BuildDirectory,
    [ValidateRange(2022, 2027)][int[]]$Years,
    [Parameter(Mandatory)][string]$ModelDirectory,
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'RevitModelMcp\release-checks'),
    [ValidateRange(30, 900)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
$results = New-Object 'Collections.Generic.List[object]'
$runDirectory = Join-Path $OutputDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $runDirectory | Out-Null
function Result([int]$Year, [string]$Check, [bool]$Passed, [string]$Detail) {
    $results.Add([pscustomobject]@{ Year = $Year; Check = $Check; Result = $(if ($Passed) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
}
function Wait-Capacity {
    $deadline = (Get-Date).AddMinutes(60)
    while (@(Get-Process Revit -ErrorAction SilentlyContinue).Count -ge 2) {
        if ((Get-Date) -ge $deadline) { throw 'Two or more Revit processes remained running for 60 minutes.' }
        Start-Sleep -Seconds 120
    }
}
$root = Split-Path $PSScriptRoot -Parent
$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
$settings = Join-Path $env:LOCALAPPDATA 'RevitModelMcp\settings.json'
$lock = Join-Path $env:LOCALAPPDATA 'RevitModelMcp\read-only'
$hadSettings = Test-Path $settings
$settingsChanged = $false
$backedUp = @()
$launchSource = @'
param([string]$Model, [string]$PidFile, [string]$Executable)
$ErrorActionPreference = 'Stop'
$process = Start-Process $Executable -ArgumentList "`"$Model`"" -PassThru
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
$driverSource = @'
import asyncio
import json
import sys
import urllib.request
from pathlib import Path
from revit_model_mcp.pipe_host import LocalPipeHost, PipeJobHost
from revit_model_mcp.revit_channel import Job, RevitChannel, ReadOnlyRefusedError

pid, folder, port = int(sys.argv[1]), Path(sys.argv[2]), int(sys.argv[3])
rows = []
def record(check, passed, detail):
    rows.append(dict(Check=check, Result="PASS" if passed else "FAIL", Detail=detail))

class PipeOnlyHost(LocalPipeHost):
    async def select_job(self, job):
        selected, addressed = await super().select_job(job)
        assert isinstance(selected, PipeJobHost), "Named pipe required"
        return selected, addressed

async def main():
    host = PipeOnlyHost()
    channel = RevitChannel(host)
    locked = False
    async def send(command, **payload):
        result = await channel.execute(Job(command, dict(command=command, targetProcessId=pid, **payload)), timeout_seconds=90)
        print(json.dumps(result, ensure_ascii=True), flush=True)
        assert result.get("success") is True, result
        return result.get("data")
    async def unlock():
        preview = await send("set-read-only", enabled=False)
        assert preview.get("confirmToken"), preview
        result = await send("set-read-only", enabled=False, confirmToken=preview["confirmToken"])
        return result
    try:
        assert await send("ping") == "pong"
        record("Ping", True, "pong over pipe/1")
        info = await send("document-info")
        level = info["levels"][0]["name"]
        fixture = await send("create-wall", startMm=[100000,100000], endMm=[101000,100000], level=level, heightMm=1000)
        element = fixture["id"]
        before = await send("element-details", id=element)
        moved = await send("move", elementIds=[element], dxMm=25, dyMm=0, dzMm=0)
        assert moved["count"] == 1, moved
        after = await send("element-details", id=element)
        assert before != after, "Move did not change element details"
        record("Move", True, "25 mm; one element changed")
        await send("undo-last")
        for _ in range(30):
            restored = await send("element-details", id=element)
            if restored == before:
                break
            await asyncio.sleep(1)
        assert restored == before, "Undo did not restore element details"
        record("Undo", True, "Element details restored")
        image = folder / "view.png"
        views = await send("list-views", viewType="FloorPlan")
        assert views["views"], "No floor plan view for capture"
        job = Job.export_view(str(views["views"][0]["id"]), pixel_size=800, output_path=str(image)).for_process(pid)
        result = await channel.execute(job, timeout_seconds=90)
        print(json.dumps(result, ensure_ascii=True), flush=True)
        assert result.get("success") is True, result
        assert image.is_file() and image.stat().st_size > 0, "No view image"
        assert image.read_bytes()[:8] == b"\x89PNG\r\n\x1a\n", "Invalid PNG signature"
        record("Capture", True, "Nonempty PNG with valid signature")
        await send("set-read-only", enabled=True)
        locked = True
        refused = False
        try:
            result = await channel.execute(Job("move", dict(command="move", targetProcessId=pid, elementIds=[element], dxMm=25, dyMm=0, dzMm=0)), timeout_seconds=30)
            refused = result.get("errorCode") == "read_only"
        except ReadOnlyRefusedError:
            refused = True
        assert refused, "Locked action was not refused as read_only"
        await unlock()
        locked = False
        assert await send("element-details", id=element) == before, "Refused action changed the element"
        record("Read-only", True, "Action refused; token unlock; element unchanged")
        with urllib.request.urlopen("http://127.0.0.1:%d/health" % port, timeout=15) as response:
            health = json.load(response)
            assert response.status == 200 and health.get("commands"), health
        record("HTTP", True, "HTTP 200; nonempty commands on allocated loopback port")
    except Exception as error:
        record("Live checks", False, str(error))
        raise
    finally:
        try:
            if locked:
                await unlock()
        finally:
            await host.aclose()
            (folder / "checks.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")

asyncio.run(main())

'@
try {
    if (!$Years) { $Years = @(2022..2027 | Where-Object { Test-Path "C:\Program Files\Autodesk\Revit $_\Revit.exe" }) }
    $Years = @($Years | Sort-Object -Unique)
    if (!$Years.Count) { throw 'No supported Revit installation found.' }
    # Settings and the lock are shared by all years of this Windows user.
    if (Test-Path $lock) { throw 'Read-only is already enabled. Unlock explicitly before the release check.' }
    if ($hadSettings) { Copy-Item $settings (Join-Path $runDirectory 'settings.json') }
    if ($Tag) {
        $assets = Join-Path $runDirectory 'assets'
        New-Item -ItemType Directory $assets | Out-Null
        & gh release download $Tag --repo sharafutdinovdi/revit-model-mcp --dir $assets --pattern 'SHA256SUMS.txt' --pattern '*SingleUser*.msi'
        if ($LASTEXITCODE -ne 0) { throw 'Release asset download failed.' }
        $packages = @(Get-ChildItem $assets -Filter '*SingleUser*.msi')
        if ($packages.Count -ne 1) { throw 'Expected one SingleUser MSI.' }
        $package = $packages[0]
        $lines = @(Get-Content (Join-Path $assets 'SHA256SUMS.txt') | Where-Object { $_ -match ('^[0-9a-fA-F]{64}  ' + [regex]::Escape($package.Name) + '$') })
        if ($lines.Count -ne 1 -or (Get-FileHash $package.FullName -Algorithm SHA256).Hash -ne $lines[0].Substring(0, 64)) { throw 'Release checksum verification failed.' }
        & gh attestation verify $package.FullName --repo sharafutdinovdi/revit-model-mcp
        if ($LASTEXITCODE -ne 0) { throw 'Release attestation verification failed.' }
        Result 0 'Assets' $true 'SHA256 and attestation verified'
        if (@(Get-Process Revit -ErrorAction SilentlyContinue).Count) { throw 'Close Revit before installing an MSI.' }
        $existing = @(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName -eq 'Model MCP')
        if ($existing.Count) { throw 'MSI smoke requires a disposable Windows account without an existing Model MCP MSI.' }
        foreach ($installedYear in @(2022..2027 | Where-Object { Test-Path "C:\Program Files\Autodesk\Revit $_\Revit.exe" })) {
            $target = Join-Path $addinRoot $installedYear
            $backup = Join-Path $runDirectory "backup\$installedYear"
            New-Item -ItemType Directory -Force $backup | Out-Null
            foreach ($name in 'RevitModelMcp.addin', 'RevitModelMcp') {
                if (Test-Path (Join-Path $target $name)) { Copy-Item (Join-Path $target $name) $backup -Recurse }
            }
            $backedUp += $installedYear
        }
        $installationAttempted = $true
        $msi = Start-Process msiexec.exe -ArgumentList "/i `"$($package.FullName)`" /qn /norestart ALLUSERS=2 MSIINSTALLPERUSER=1 /l*v `"$(Join-Path $runDirectory 'install.log')`"" -PassThru -Wait
        if ($msi.ExitCode -notin 0, 3010) { throw "MSI install failed: $($msi.ExitCode)" }
    }
    else {
        $BuildDirectory = (Resolve-Path $BuildDirectory).Path
        Wait-Capacity
        foreach ($running in @(Get-CimInstance Win32_Process | Where-Object Name -eq 'Revit.exe')) {
            foreach ($year in $Years) {
                if ($running.ExecutablePath -like "*\Revit $year\Revit.exe") { throw "Revit $year is already running. Its installation will not be changed." }
            }
        }
        foreach ($year in $Years) {
            $target = Join-Path $addinRoot $year
            $backup = Join-Path $runDirectory "backup\$year"
            New-Item -ItemType Directory -Force $backup | Out-Null
            foreach ($name in 'RevitModelMcp.addin', 'RevitModelMcp') {
                if (Test-Path (Join-Path $target $name)) { Copy-Item (Join-Path $target $name) $backup -Recurse }
            }
            $backedUp += $year
        }
        $previousReuse = $env:MSBUILDDISABLENODEREUSE
        $previousCompilation = $env:UseSharedCompilation
        try {
            $env:MSBUILDDISABLENODEREUSE = '1'
            $env:UseSharedCompilation = 'false'
            & (Join-Path $BuildDirectory 'install.ps1') -Source Build -Year @($Years | ForEach-Object { [string]$_ }) -Force
            if ($LASTEXITCODE -ne 0) { throw 'Build installation failed.' }
        }
        finally {
            $env:MSBUILDDISABLENODEREUSE = $previousReuse
            $env:UseSharedCompilation = $previousCompilation
        }
    }
    foreach ($year in $Years) {
        $ownedId = 0
        $reservedUrl = $null
        $taskName = 'release-check-' + [guid]::NewGuid().ToString('N')
        $yearDirectory = Join-Path $runDirectory $year
        New-Item -ItemType Directory $yearDirectory | Out-Null
        try {
            $executable = "C:\Program Files\Autodesk\Revit $year\Revit.exe"
            if (!(Test-Path $executable)) { throw "Revit $year is not installed." }
            $models = @(Get-ChildItem (Join-Path $ModelDirectory $year) -Filter '*.rvt' -File)
            if ($models.Count -ne 1) { throw "Supply exactly one disposable .rvt in the $year model directory (links belong in subdirectories)." }
            $model = $models[0].FullName
            $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            $url = "http://127.0.0.1:$port/"
            & netsh http show urlacl "url=$url" | Out-File (Join-Path $yearDirectory 'urlacl-before.txt')
            Write-Host "HTTP reservation rollback: netsh http delete urlacl url=$url"
            & netsh http add urlacl "url=$url" "user=$([Security.Principal.WindowsIdentity]::GetCurrent().Name)" | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'HTTP URL reservation failed. Run the release check from elevated PowerShell.' }
            $reservedUrl = $url
            @{ httpEnabled = $true; httpBind = '127.0.0.1'; httpPort = $port; token = [guid]::NewGuid().ToString('N'); showActivityPaneOnAction = $false } | ConvertTo-Json | Set-Content $settings -Encoding UTF8
            $settingsChanged = $true
            $launcher = Join-Path $yearDirectory 'launch.ps1'
            $launchSource | Set-Content $launcher -Encoding UTF8
            $pidFile = Join-Path $yearDirectory 'revit.pid'
            $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$launcher`" -Model `"$model`" -PidFile `"$pidFile`" -Executable `"$executable`""
            $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive
            Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal | Out-Null
            Wait-Capacity
            Start-ScheduledTask $taskName
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while (!(Test-Path $pidFile) -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
            if (!(Test-Path $pidFile)) { throw 'Interactive launch did not report a PID.' }
            $ownedId = [int](Get-Content $pidFile -Raw)
            $heartbeatPath = Join-Path $env:LOCALAPPDATA "RevitModelMcp\instance_$ownedId.json"
            $ready = $false
            while ((Get-Date) -lt $deadline) {
                if (Test-Path $heartbeatPath) {
                    $heartbeat = Get-Content $heartbeatPath -Raw | ConvertFrom-Json
                    if ($heartbeat.documentPath -eq $model -and (Get-Date $heartbeat.updatedUtc) -gt (Get-Date).AddSeconds(-60)) { $ready = $true; break }
                }
                Start-Sleep 2
            }
            if (!$ready) { throw 'Timed out waiting for the test model heartbeat.' }
            Result $year 'Install/start' $true 'Fresh test model heartbeat'
            $driver = Join-Path $yearDirectory 'checks.py'
            $driverSource | Set-Content $driver -Encoding UTF8
            $server = if ($BuildDirectory) { Join-Path $BuildDirectory 'server' } else { Join-Path $root 'server' }
            $previousPythonPath = $env:PYTHONPATH
            $previousErrorPreference = $ErrorActionPreference
            try {
                $env:PYTHONPATH = $server
                # Windows PowerShell treats native stderr as ErrorRecord objects, even on exit 0.
                $ErrorActionPreference = 'Continue'
                & uv run --project $server python $driver $ownedId $yearDirectory $port 2>&1 | Tee-Object (Join-Path $yearDirectory 'checks.log') | Out-Host
                $driverExit = $LASTEXITCODE
            }
            finally {
                $env:PYTHONPATH = $previousPythonPath
                $ErrorActionPreference = $previousErrorPreference
            }
            $report = Join-Path $yearDirectory 'checks.json'
            if (Test-Path $report) {
                foreach ($row in (Get-Content $report -Raw | ConvertFrom-Json)) { Result $year $row.Check ($row.Result -eq 'PASS') $row.Detail }
            }
            if ($driverExit -ne 0) { throw 'Live checks failed; see checks.log.' }
        }
        catch { Result $year 'Smoke' $false $_.Exception.Message }
        finally {
            if (!$ownedId -and (Test-Path (Join-Path $yearDirectory 'revit.pid'))) { $ownedId = [int](Get-Content (Join-Path $yearDirectory 'revit.pid') -Raw) }
            try {
                if ($settingsChanged -and (Test-Path $lock)) { Remove-Item $lock -Force }
                if ($ownedId) {
                    $process = Get-Process -Id $ownedId -ErrorAction SilentlyContinue
                    if ($process -and $process.ProcessName -eq 'Revit') {
                        $null = $process.CloseMainWindow()
                        if (!$process.WaitForExit(20000)) { Stop-Process -Id $ownedId -Force; $process.WaitForExit() }
                    }
                }
                if ($reservedUrl) {
                    & netsh http delete urlacl "url=$reservedUrl" | Out-Host
                    if ($LASTEXITCODE -ne 0) { throw 'HTTP URL reservation cleanup failed.' }
                }
                Result $year 'Cleanup' (!(Test-Path $lock)) 'Read-only off; owned Revit closed without saving'
            }
            catch { Result $year 'Cleanup' $false $_.Exception.Message }
            Stop-ScheduledTask $taskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask $taskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    }
}
catch { Result 0 'Setup' $false $_.Exception.Message }
finally {
    if ($installationAttempted) {
        try {
            $remove = Start-Process msiexec.exe -ArgumentList "/x `"$($package.FullName)`" /qn /norestart" -PassThru -Wait
            Result 0 'Uninstall' ($remove.ExitCode -in 0, 3010, 1605) "Exit $($remove.ExitCode)"
        }
        catch { Result 0 'Uninstall' $false $_.Exception.Message }
    }
    try {
        if ($settingsChanged) {
            if ($hadSettings) { Copy-Item (Join-Path $runDirectory 'settings.json') $settings -Force }
            else { Remove-Item $settings -Force -ErrorAction SilentlyContinue }
        }
        foreach ($year in $backedUp) {
            $target = Join-Path $addinRoot $year
            foreach ($name in 'RevitModelMcp.addin', 'RevitModelMcp') {
                $destination = Join-Path $target $name
                if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
                $saved = Join-Path $runDirectory "backup\$year\$name"
                if (Test-Path $saved) { Copy-Item $saved $target -Recurse }
            }
        }
        Result 0 'Restore' $true 'Settings and selected add-in folders restored'
    }
    catch { Result 0 'Restore' $false $_.Exception.Message }
    $results | Export-Csv (Join-Path $runDirectory 'summary.csv') -NoTypeInformation -Encoding UTF8
    $results | Format-Table Year, Check, Result, Detail -AutoSize | Out-String -Width 240 | Tee-Object (Join-Path $runDirectory 'summary.txt') | Write-Host
}
if ($results.Result -contains 'FAIL') { exit 1 }
