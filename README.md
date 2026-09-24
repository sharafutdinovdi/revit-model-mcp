<p align="center">
  <img src="docs/assets/logo.svg" alt="Revit Model MCP mark" width="96" height="96">
</p>

# Revit Model MCP

For people reviewing or automating Revit models with an AI client: read a live Revit model through MCP, read-only by default, and act in it only when two explicit gates are on.

[![CI](https://img.shields.io/github/actions/workflow/status/sharafutdinovdi/revit-model-mcp/ci.yml?style=flat-square)](https://github.com/sharafutdinovdi/revit-model-mcp/actions/workflows/ci.yml)
[![CodeQL](https://img.shields.io/github/actions/workflow/status/sharafutdinovdi/revit-model-mcp/codeql.yml?label=CodeQL&style=flat-square)](https://github.com/sharafutdinovdi/revit-model-mcp/actions/workflows/codeql.yml)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/sharafutdinovdi/revit-model-mcp/badge)](https://scorecard.dev/viewer/?uri=github.com/sharafutdinovdi/revit-model-mcp)
[![Latest release](https://img.shields.io/github/v/release/sharafutdinovdi/revit-model-mcp?style=flat-square)](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest)
[![PyPI](https://img.shields.io/pypi/v/revit-model-mcp?style=flat-square)](https://pypi.org/project/revit-model-mcp/)
[![Downloads](https://img.shields.io/github/downloads/sharafutdinovdi/revit-model-mcp/total?style=flat-square)](https://github.com/sharafutdinovdi/revit-model-mcp/releases)
![Revit 2022-2027](https://img.shields.io/badge/Revit-2022--2027-005FB8?style=flat-square)
![Python 3.11+](https://img.shields.io/badge/Python-3.11%2B-3776AB?style=flat-square)
[![MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)

## Privacy policy

Revit Model MCP returns requested model data to the selected MCP client.
The bundle enables response path redaction by default.
The [privacy policy](https://sharafutdinovdi.github.io/revit-model-mcp/privacy/) covers collection, storage, sharing, retention and contact information.

## Install

### Claude Desktop bundle

Install [uv](https://docs.astral.sh/uv/getting-started/installation/) on the client's PATH, download `revit-model-mcp-<version>.mcpb` from the [latest release](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest), and open it in Claude Desktop.
The settings form configures the workstation host, path redaction, optional actions and the HTTP bearer token without editing JSON.
Use `local` on the Windows Revit workstation, or [configure a remote workstation](#remote-workstations) for macOS and Linux clients.
Path redaction starts enabled and actions start disabled.
The Windows workstation still needs the add-in below.
See the [bundle guide](bundle/README.md) for build details and prerequisites.

**Verify downloads.** Release assets include GitHub build provenance attestations; follow [download verification](https://sharafutdinovdi.github.io/revit-model-mcp/security/#verify-downloads) before installing.

### On the Revit workstation

Download `RevitModelMcp-<version>-SingleUser.msi` (current user) or `RevitModelMcp-<version>-MultiUser.msi` (all users) from the [latest release](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest).
Run it with Revit closed, then start Revit and open a model.
Alternatively, run from a clone in PowerShell:

```powershell
.\install.ps1 -Source Release
```

### On the machine with the MCP client

Install [uv](https://docs.astral.sh/uv/getting-started/installation/) and use Python 3.11+.
For Claude Code on the same Windows workstation:

```sh
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- uvx revit-model-mcp
```

For manual Claude Desktop registration, add to its MCP configuration:

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

From a clone, `uv run --directory server revit-model-mcp` runs the same server without installing the package.
With `REVIT_MCP_HOST=local` the server reaches each Revit through its own named pipe, restricted to the Windows user running Revit; no port or URL reservation is needed.
For macOS or Linux clients, configure a [remote workstation](#remote-workstations).

### Check

Call `revit_ping` in the MCP client and expect `success: true`.

## In action

Claude Desktop runs on a Mac and connects to Revit 2026 on a Windows workstation.
Both action gates are enabled in this recording.

<img alt="Claude Desktop conversation on the left, Revit 2026 on the right: Claude reads the open model, finds the largest room, opens its plan and selects it, isolates it, places a chair and moves it, then cleans up" src="docs/screenshots/revit-model-mcp_claude-desktop.gif" width="100%">

What happens in the recording, in order:

1. "What model is open in Revit right now?" The client reads the document, levels and room counts.
2. "Which level has the most room area? Find the largest room and show it to me." The client aggregates room areas by level and queries the largest room.
   `revit_show` opens a matching plan and selects the room.
3. "Isolate that room, place a Chair-Breuer at its centre and move it 800 mm along X." `revit_isolate`, then `revit_place_family` at the room's `roomCenterMm`, then `revit_move`. Each mutation is its own Revit transaction.
4. Cleanup afterwards is one more sentence: reset the view, delete the chair.

The picture below is the PNG saved by `revit_export_view` during an earlier session against Revit 2023 over SSH, untouched:

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/revit-model-mcp_export-view_dark.png">
  <img alt="View exported by revit_export_view from the Revit sample project" src="docs/screenshots/revit-model-mcp_export-view_light.png" width="100%">
</picture>

## What you get

<a id="tools"></a>

| Read tools | Names |
| --- | --- |
| Document and catalog | `revit_ping`, `revit_document_info`, `revit_list_catalog`, `revit_list_instances` |
| Elements and parameters | `revit_query_elements`, `revit_aggregate_elements`, `revit_element_details`, `revit_list_relations`, `revit_list_warnings` |
| Views and export | `revit_list_views`, `revit_view_summary`, `revit_view_elements`, `revit_view_warnings`, `revit_export_view` |
| Coordinator checks | `revit_model_health`, `revit_links_status`, `revit_shared_coordinates`, `revit_parameter_fill_check` |

See the [full tool reference](https://sharafutdinovdi.github.io/revit-model-mcp/tools/) for arguments, units and limits.

<a id="actions-opt-in"></a>

Actions are enabled by default: opt out with `REVIT_MCP_READ_ONLY=1` in the server, or the workstation `read-only` file.
Model mutations support `dry_run` previews and return `verification` and a human-readable `summary`; a committed action assimilates into one named Revit undo entry, visible in the add-in's "MCP activity" pane; `revit_undo_last` undoes it while it is still Revit's last change.
See [actions](https://sharafutdinovdi.github.io/revit-model-mcp/actions/) for gates, exceptions and verification failures.

## Remote workstations

Local Windows clients use `REVIT_MCP_HOST=local` under the Revit user's account.
Remote clients run the whole server on the workstation over SSH, as the same Windows user that runs Revit.
Install it there once with `uv tool install revit-model-mcp`, then register `ssh` as the command:

```json
{
  "mcpServers": {
    "revit-model-mcp": {
      "command": "ssh",
      "args": ["windows-fi", "revit-model-mcp", "--redact-paths"]
    }
  }
}
```

Replace `windows-fi` with the workstation's SSH host alias.
MCP stdio flows through the SSH session and the remote server uses the named pipe, so no port opens.
`REVIT_MCP_HOST=ssh:<alias>` (file channel over SSH) and HTTP through an SSH tunnel remain available; HTTP requires a bearer token except for `/health` and binds to loopback by default.
See [transport setup](https://sharafutdinovdi.github.io/revit-model-mcp/transport/).

## Security

The default tools read the model without model-changing transactions; exports and channel operations write files outside it.
MCP actions are refused in read-only mode, while direct HTTP callers require the bearer token and are refused while the workstation read-only file is present.
`REVIT_MCP_REDACT_PATHS=1` hides directories in response path fields, but names, parameter values, errors, channel files and exported image `localPath` values remain visible.
See [security details](https://sharafutdinovdi.github.io/revit-model-mcp/security/) for authentication and privacy boundaries, and [SECURITY.md](SECURITY.md) to report a vulnerability.

## Compatibility

| Revit year | Add-in target framework | Validation status |
| --- | --- | --- |
| 2022 | .NET Framework 4.8 | Build evidence |
| 2023 | .NET Framework 4.8 | Build evidence |
| 2024 | .NET Framework 4.8 | Builds and install script |
| 2025 | .NET 8 | Build evidence |
| 2026 | .NET 8 | Builds, live reads/actions and install script |
| 2027 | .NET 10 | Build evidence |

See [validation evidence](https://sharafutdinovdi.github.io/revit-model-mcp/validation/) for dates and limits, and [known gaps](https://sharafutdinovdi.github.io/revit-model-mcp/roadmap/#known-gaps).

## Contributing and support

[Documentation](https://sharafutdinovdi.github.io/revit-model-mcp/) covers setup, tools and transport.

Start with [CONTRIBUTING.md](CONTRIBUTING.md), ask questions in [Discussions](https://github.com/sharafutdinovdi/revit-model-mcp/discussions), or report bugs and request features through the [issue forms](https://github.com/sharafutdinovdi/revit-model-mcp/issues/new/choose).
CI runs the C# and Python test suites and builds the supported Revit configurations.

## Contributors

[![Contributors](https://contrib.rocks/image?repo=sharafutdinovdi/revit-model-mcp)](https://github.com/sharafutdinovdi/revit-model-mcp/graphs/contributors)

## License

[MIT](LICENSE), maintained by Dinar Sharafutdinov.
See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency licenses.
