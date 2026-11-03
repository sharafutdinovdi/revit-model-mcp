# Install with an AI agent

Model MCP installs per user on Windows 10 or 11 with Revit 2022-2027.
The release MSI includes the server executable and add-in files.
Installation requires Windows PowerShell 5.1 and .NET Framework 4.8, which Revit requires, with no checkout, Python, .NET SDK, or administrator rights.
Close Revit before installation.

## For agents

- Do not clone the repository or build from source for installation.
- Use the release command below and select the current client.
- Keep SHA256 verification enabled. The installer verifies the MSI against the release's `SHA256SUMS.txt` before running it.
- Read the complete JSON result, then run `doctor --json` after the human steps.
- Report every `humanSteps` entry and every doctor problem. Never claim a live connection before a successful ping.
- Never unlock the workstation read-only lock without explicit user confirmation.

## One command per client

Run the matching command in Windows PowerShell.
The script resolves the latest release, verifies the MSI, installs silently, registers the client through the same Core configurator as Connect AI app, and runs doctor.

### Claude Code

```powershell
& ([scriptblock]::Create((irm https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest/download/install.ps1))) -Client claude-code -Json
```

If `claude` is on PATH, the script runs `claude mcp add` with the installed executable and local, redacted transport settings.
Otherwise `humanSteps` contains the exact command to run after Claude Code installation.
The command uses Claude Code's default local scope for the current folder, stored in `.claude.json`; `CLAUDE_CONFIG_DIR` overrides its home folder.
See [Claude Code installation scopes](https://code.claude.com/docs/en/mcp#mcp-installation-scopes).

### Claude Desktop

```powershell
& ([scriptblock]::Create((irm https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest/download/install.ps1))) -Client claude-desktop -Json
```

### Cursor

```powershell
& ([scriptblock]::Create((irm https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest/download/install.ps1))) -Client cursor -Json
```

### VS Code

```powershell
& ([scriptblock]::Create((irm https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest/download/install.ps1))) -Client vscode -Json
```

### Codex

```powershell
& ([scriptblock]::Create((irm https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest/download/install.ps1))) -Client codex -Json
```

`-Client all-detected` selects apps whose configuration folders exist, plus Claude Code when its command exists.
Explicit client selection can create its configuration folder before the app's first start.
Multiple clients use `-Client cursor,codex`.
Existing configuration files receive timestamped backups before changes.
Other servers and settings remain in place.
An unchanged registration creates no backup.
`-DryRun -Json` returns the plan without network access or changes.
`-Version 2.0.0` selects an exact release instead of `latest`; it must contain `install.ps1` and the SingleUser MSI.

## Result and human steps

`-Json` writes exactly one JSON object to stdout.
Errors go to stderr, and `log` points at the verbose MSI log.
Exit code 0 means installation and registration completed, even when human steps remain.
Exit code 1 means a failure; `error` explains it.
Doctor has its own `ok` result and returns exit code 1 when problems exist.
An installation can succeed while doctor reports that Revit has not started or is not installed.
No exit code 2 is used.

Illustrative result, with paths shortened:

```json
{
  "version": "2.0.0",
  "msi": "C:\\...\\RevitModelMcp-2.0.0-SingleUser.msi",
  "installedYears": [2022, 2023, 2024, 2025, 2026, 2027],
  "clients": [{"id": "cursor", "configPath": "C:\\...\\.cursor\\mcp.json", "changed": true, "backupPath": null}],
  "doctor": {"ok": true, "problems": [], "humanSteps": ["Start Revit, choose Always Load on first start, and open a model."]},
  "humanSteps": ["Start Revit and choose Always Load for Model MCP on first start.", "Restart the registered AI apps."],
  "nextPrompt": "What model is open in Revit?",
  "ok": true,
  "log": "C:\\...\\revit-model-mcp-msi.log"
}
```

1. Start Revit and choose **Always Load** for Model MCP on first start.
2. Open a model.
3. Restart the registered AI apps.
4. Complete any extra `humanSteps`, such as a missing Claude Code command or a required Windows restart.

The MSI installs files for every supported year; `installedYears` lists add-in manifests, while doctor's `revitYears` lists installed Revit applications.

## Verify

```powershell
& "$env:LOCALAPPDATA\Programs\Model MCP\server\revit-model-mcp.exe" doctor --json
```

Doctor reads installed Revit years, add-in manifests and dependencies, version agreement, channel folder state, client configurations, and running Revit instances.
It performs a read-only ping for running Revit instances and includes channel ACL refusal reasons from the add-in log with repair instructions.
It never edits a configuration or repairs permissions.
After doctor reports a responding instance, ask the AI app: **What model is open in Revit?**
`uvx revit-model-mcp doctor --json` provides the same diagnostics for package installations.
On macOS and Linux it explains that Revit runs on Windows and requires the [SSH transport](transport.md).

## Uninstall

Close Revit, then run:

```powershell
& ([scriptblock]::Create((irm https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest/download/install.ps1))) -Uninstall -Version 2.0.0 -Json
```

Select the installed release version for uninstall, or use Windows **Installed apps > Model MCP > Uninstall**.
Client configurations and backups remain; remove the `revit-model-mcp` entry through Connect AI app before uninstalling or through the client afterward.

## Offline and managed installations

Download `install.ps1`, the SingleUser MSI, and `SHA256SUMS.txt` from the same release on a connected computer.
Transfer them together into a local folder and run its downloaded script:

```powershell
.\install.ps1 -ReleaseDir C:\Packages\ModelMcp -Client cursor -Json
```

`-ReleaseDir` verifies the checksum and uses the current `APPDATA` and `LOCALAPPDATA` folders explicitly, which also supports isolated CI verification.
Managed deployments can use the MultiUser MSI and [deployment guide](admin/deployment.md).
Machine policy and the workstation read-only lock remain authoritative.
The checkout-oriented `-Source Build`, explicit `-Year`, and per-year `-Uninstall` modes remain available for contributors in [requirements and installation](install.md).
