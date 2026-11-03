"""Read-only installation and live channel diagnostics."""

from __future__ import annotations

import asyncio
import json
import sys
from typing import Any

from revit_model_mcp.pipe_host import LocalPipeHost
from revit_model_mcp.ssh_host import CHANNEL_REPAIR_HINT, SshPowerShellHost

# Config inspection uses the same .NET Framework Core assembly as the installer and GUI.
INVENTORY_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
$server = Join-Path $env:LOCALAPPDATA 'Programs\Model MCP\server\revit-model-mcp.exe'
if (!(Test-Path $server)) { $server = Join-Path $env:ProgramFiles 'Model MCP\server\revit-model-mcp.exe' }
$years = @(2022..2027 | Where-Object { Test-Path "$env:ProgramFiles\Autodesk\Revit $_\Revit.exe" })
$addins = @(foreach ($year in 2022..2027) {
    $roots = @((Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"),
        (Join-Path $env:ProgramData "Autodesk\Revit\Addins\$year"))
    if ($year -ge 2027) { $roots += Join-Path $env:ProgramFiles "Autodesk\Revit\Addins\$year" }
    foreach ($root in $roots) {
        $manifest = Join-Path $root 'RevitModelMcp.addin'
        if (Test-Path $manifest) {
            [xml]$xml = Get-Content -LiteralPath $manifest
            $assembly = [string]$xml.RevitAddIns.AddIn.Assembly
            if (![IO.Path]::IsPathRooted($assembly)) { $assembly = Join-Path $root $assembly }
            $files = @($assembly, (Join-Path (Split-Path $assembly) 'RevitModelMcp.Core.dll'))
            $missing = @($files | Where-Object { !(Test-Path -LiteralPath $_ -PathType Leaf) })
            $version = if (Test-Path -LiteralPath $assembly) { (Get-Item -LiteralPath $assembly).VersionInfo.ProductVersion } else { '' }
            [ordered]@{ year = $year; manifest = $manifest; version = $version; missing = $missing }
        }
    }
})
$core = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2022\RevitModelMcp\RevitModelMcp.Core.dll'
if (!(Test-Path $core)) { $core = Join-Path $env:ProgramData 'Autodesk\Revit\Addins\2022\RevitModelMcp\RevitModelMcp.Core.dll' }
$clients = @()
if (Test-Path $core) {
    Add-Type -Path $core
    $spec = if (Test-Path -LiteralPath $server) {
        [RevitModelMcp.Core.Connect.McpServerSpec]::ForExecutable($server)
    } else { [RevitModelMcp.Core.Connect.McpServerSpec]::Uvx }
    $editor = [RevitModelMcp.Core.Connect.McpClientConfigurator]::new($null, $spec)
    $clients = @([RevitModelMcp.Core.Connect.McpClientLocator]::Discover($env:APPDATA, $env:LOCALAPPDATA, $env:USERPROFILE) | ForEach-Object {
        $status = $editor.Inspect($_)
        [ordered]@{ id = $_.Id; configPath = $_.ConfigPath; state = $status.State.ToString(); detail = $status.Detail }
    })
}
if (Get-Command claude -ErrorAction SilentlyContinue) {
    $ErrorActionPreference = 'Continue'
    $registration = & claude mcp get revit-model-mcp 2>$null
    $registered = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if ($registered) {
        $state = if (($registration -join "`n").Contains($server)) { 'Configured' } else { 'ConfiguredDifferentCommand' }
        $clients += [ordered]@{ id = 'claude-code'; configPath = 'Managed by claude mcp'; state = $state; detail = $null }
    }
}
$channel = if ($env:REVIT_MCP_CHANNEL_DIR) { $env:REVIT_MCP_CHANNEL_DIR } else { Join-Path $env:LOCALAPPDATA 'RevitModelMcp' }
[ordered]@{ revitYears = $years; addins = $addins; clients = $clients;
    serverExists = (Test-Path -LiteralPath $server); channelExists = (Test-Path -LiteralPath $channel) } | ConvertTo-Json -Depth 8 -Compress
"""


def assess_inventory(
    inventory: dict[str, Any], version: str, *, require_bundled_server: bool = True
) -> dict[str, Any]:
    """Assess installation facts without writing files or changing client settings."""
    problems: list[str] = []
    human_steps: list[str] = []
    years = inventory["revitYears"]
    addins = inventory["addins"]
    if not years:
        problems.append("No Revit 2022-2027 installed. Install Revit on this Windows PC.")
    if require_bundled_server and not inventory["serverExists"]:
        problems.append("Bundled server is missing. Run the SingleUser MSI installer again.")
    for year in years:
        if not any(item["year"] == year for item in addins):
            problems.append(f"Revit {year} add-in manifest is missing. Run the installer again.")
    for item in addins:
        if item["missing"]:
            problems.append(
                f"Revit {item['year']} add-in files are missing. Repair the MSI installation."
            )
        if ".".join(item["version"].split("+")[0].split(".")[:3]) != ".".join(
            version.split("+")[0].split(".")[:3]
        ):
            problems.append(
                f"Revit {item['year']} add-in and server versions differ. Install the same release for both."
            )
    for client in inventory["clients"]:
        if client["state"] in ("Unreadable", "ConfiguredDifferentCommand"):
            client_id = "claude-desktop" if client["id"] == "claude-desktop-store" else client["id"]
            problems.append(
                f"{client['id']} registration is {client['state']}. Back up its config and rerun install.ps1 -Client {client_id}."
            )
    if not inventory["channelExists"]:
        human_steps.append("Start Revit to create the Model MCP channel folder.")
    return {
        "ok": not problems,
        "version": version,
        "installedYears": sorted({item["year"] for item in addins}),
        "revitYears": years,
        "clients": inventory["clients"],
        "channelExists": inventory["channelExists"],
        "problems": problems,
        "humanSteps": human_steps,
    }


async def diagnose(version: str) -> dict[str, Any]:
    if sys.platform != "win32":
        return {
            "ok": False,
            "version": version,
            "installedYears": [],
            "clients": [],
            "problems": [
                "Revit runs on Windows; use the SSH transport. Set REVIT_MCP_HOST=ssh:<alias> and install the add-in on Windows."
            ],
            "humanSteps": [],
        }
    host = SshPowerShellHost(local=True)
    try:
        inventory = json.loads(await host._run(INVENTORY_SCRIPT))
        result = assess_inventory(
            inventory, version, require_bundled_server=bool(getattr(sys, "frozen", False))
        )
        instances = await LocalPipeHost(host).list_revit_instances()
        # Report only channel facts. Model titles and paths stay out of doctor output.
        result["instances"] = [
            {key: instance.get(key) for key in ("processId", "pluginResponding", "channelLog")}
            for instance in instances
        ]
        for instance in instances:
            if not instance.get("pluginResponding"):
                reason = (
                    instance.get("channelLog")
                    or "No confirmed response. Choose Always Load on first start, then open a model and retry."
                )
                result["problems"].append(
                    f"Revit PID {instance['processId']}: {reason} {CHANNEL_REPAIR_HINT}"
                )
        if not instances:
            result["humanSteps"].append(
                "Start Revit, choose Always Load on first start, and open a model."
            )
        result["ok"] = not result["problems"]
        return result
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        return {
            "ok": False,
            "version": version,
            "installedYears": [],
            "clients": [],
            "problems": [f"Diagnostics failed: {error}. Repair the installation and retry doctor."],
            "humanSteps": [],
        }


def main(version: str, *, json_output: bool) -> None:
    result = asyncio.run(diagnose(version))
    print(json.dumps(result, indent=None if json_output else 2))
    if not result["ok"]:
        raise SystemExit(1)
