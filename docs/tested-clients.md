---
description: "Recorded Model MCP client validation, configuration support, untested clients, and live Revit evidence limits."
---

# Tested MCP clients

This page states which MCP clients were checked against Model MCP, which were not, and what that means. A client counts as tested only when someone ran it against a live Revit model. Last update: 2026-10-10.

| Client | Status | Evidence |
| --- | --- | --- |
| Claude Desktop on macOS with a remote workstation | Tested | Recorded run against Revit 2026 in the [README](https://github.com/sharafutdinovdi/revit-model-mcp#readme): Claude Desktop on a Mac, Revit on a Windows workstation over SSH. |
| Claude Desktop with the `.mcpb` bundle | Manifest and server start checked; import into Claude Desktop not verified | [Validation](validation.md) |
| Bundled server executable (MSI) | `revit-model-mcp.exe --version` and an MCP `initialize` plus `tools/list` over stdio pass in CI on Windows without uv or Python on the PATH; the expected tool count comes from the manifest. The registration written by **Connect AI app** for it is covered by unit tests, not by a recorded client run | [Validation](validation.md) |
| Claude Code | Configuration documented in [install](install.md); no recorded validation run | |
| Cursor | Not tested | Any client that starts a stdio server should work with the same JSON configuration. |
| Codex CLI | Not tested | |
| VS Code with GitHub Copilot | Not tested | |
| LM Studio, Jan and other local clients | Not tested | |
| Claude Desktop chat (cloud) | Not supported | Cloud chat cannot start a local stdio server. |

## What "not tested" means

The server supports MCP `2026-07-28` per-request envelopes and legacy handshake versions through `2025-11-25` over stdio.
See [protocol negotiation and client identity](mcp-specifications.md#protocol-negotiation-and-client-identity) for the supported versions and identity fields.
Automated in-memory transport tests cover negotiation, client name and version from modern request metadata, and client names forwarded to Revit.
These checks do not add a live client validation entry to the table above.
Differences in tool-list size limits, schema handling and confirmation behavior between clients are not covered.
The server returns a confirmation token for risky calls, and the client has to show the preview to the user.
A client that does not do this weakens that safeguard.
Check this before using a client for work on a real model.

If you try another client, report the result in [Discussions](https://github.com/sharafutdinovdi/revit-model-mcp/discussions).
