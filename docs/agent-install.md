---
description: "Install Model MCP for Revit from a published MSI, verify its checksum, register a client, and check the connection."
---

# Install with an AI agent

Model MCP installs per user on Windows 10 or 11 with Revit 2022-2027.
The published v2.0.0 release provides MSI installers but does not contain `install.ps1`.
The one-command installer arrives with the next release.
Until then, use the published MSI and client registration below.
Close Revit before installation.

## For agents

- Do not clone the repository or build from source for installation.
- Download the MSI and checksum manifest from the same release.
- Verify SHA256 before running the MSI.
- Report required human steps and every diagnostic problem.
- Never claim a live connection before a successful ping.
- Never unlock the workstation read-only lock without explicit user confirmation.

## Download and verify

The [v2.0.0 release](https://github.com/sharafutdinovdi/revit-model-mcp/releases/tag/v2.0.0) contains the current-user installer.
Run these commands in Windows PowerShell from a writable download folder:

```powershell
$base = 'https://github.com/sharafutdinovdi/revit-model-mcp/releases/download/v2.0.0'
$msi = 'RevitModelMcp-2.0.0-SingleUser.msi'
Invoke-WebRequest "$base/$msi" -OutFile $msi
Invoke-WebRequest "$base/SHA256SUMS.txt" -OutFile SHA256SUMS.txt
$entry = @(Get-Content SHA256SUMS.txt | Where-Object { $_ -match "^[0-9a-fA-F]{64}  $([regex]::Escape($msi))$" })
if ($entry.Count -ne 1) { throw 'Expected exactly one MSI checksum' }
$expected = ($entry[0] -split '  ', 2)[0]
if ((Get-FileHash $msi -Algorithm SHA256).Hash -ne $expected) { throw 'MSI checksum mismatch' }
$process = Start-Process msiexec.exe -ArgumentList @('/i', "`"$((Resolve-Path $msi).Path)`"", '/passive', '/norestart') -Wait -PassThru
if ($process.ExitCode -notin 0, 3010) { throw "MSI failed: $($process.ExitCode)" }
if ($process.ExitCode -eq 3010) { Write-Output 'Restart Windows to complete installation.' }
```

The SingleUser MSI requires no administrator rights.
For checksum provenance verification, see [Verify downloads](security.md#verify-downloads).

## Register the server

Start Revit, choose **Always Load** for Model MCP on first start, and open a model.
In Revit, **Model MCP > Model MCP > Connect AI app** registers the bundled executable in the selected client.
Claude Code receives a copy-paste registration command.
Restart the client after registration.
The bundled executable needs no separate Python or uv installation.

Alternatively, install [uv](https://docs.astral.sh/uv/getting-started/installation/) and register the PyPI server.
For Claude Code on the same Windows machine:

```powershell
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- uvx revit-model-mcp
```

For clients with a JSON MCP configuration:

```json
{
  "mcpServers": {
    "revit-model-mcp": {
      "command": "uvx",
      "args": ["revit-model-mcp"],
      "env": {
        "REVIT_MCP_HOST": "local",
        "REVIT_MCP_REDACT_PATHS": "1"
      }
    }
  }
}
```

Client-specific instructions are in [requirements and installation](install.md).
On macOS and Linux, use the [SSH transport](transport.md) instead of `REVIT_MCP_HOST=local`.

### Task tool selection

The default connection exposes all tools.
Set the server environment variable `REVIT_MCP_TOOLSETS` in the client's MCP configuration to a [task preset](tools.md#toolsets), for example:

```json
{
  "env": {
    "REVIT_MCP_TOOLSETS": "preset:families"
  }
}
```

Merge this property into the existing server entry and retain its executable, arguments and other environment settings.
The server also accepts `--toolsets preset:families`; this argument overrides the environment variable.
Discovery instructions recommend `preset:audit` for reading and auditing, `preset:edit` for existing-element changes, `preset:families` for family work, `preset:sheets` for sheets and exports, and `preset:batch` for collection and processing.
Restart the server connection after changing the selection.
Comma-separated presets and toolsets combine; `all` restores every tool.
Hidden tools require a new selection before use.

## Verify

For the PyPI server, run:

```powershell
uvx revit-model-mcp doctor --json
```

In the client, ask: **Call `revit_ping`, then tell me which model is open.**
A successful ping confirms connectivity to Revit.

## Uninstall

Close Revit, then use Windows **Installed apps > Model MCP > Uninstall**.
Client configurations remain; remove the `revit-model-mcp` entry through Connect AI app before uninstalling or through the client afterward.

## Offline and managed installations

Download the MSI and `SHA256SUMS.txt` from the same release on a connected computer.
Transfer both files into a local folder, then run the verification and installation commands above without the two `Invoke-WebRequest` commands.
The bundled server supports client registration without a PyPI download.
Managed deployments can use the MultiUser MSI and [deployment guide](admin/deployment.md).
Machine policy and the workstation read-only lock remain authoritative.

## Next-release one-command installer

The next release includes `install.ps1` with checksum verification, silent per-user installation, client registration, JSON results, diagnostics, and offline installation support.
The current published release does not support downloading that script as a release asset.
Use the MSI procedure above until a release containing the script is published.
