# Tested MCP clients

This page states which MCP clients were checked against Model MCP, which were not, and what that means. A client counts as tested only when someone ran it against a live Revit model. Last update: 2026-10-10.

| Client | Status | Evidence |
| --- | --- | --- |
| Claude Desktop on macOS with a remote workstation | Tested | Recorded run against Revit 2026 in the [README](https://github.com/sharafutdinovdi/revit-model-mcp#readme): Claude Desktop on a Mac, Revit on a Windows workstation over SSH. |
| Claude Desktop with the `.mcpb` bundle | Manifest and server start checked; import into Claude Desktop not verified | [Validation](validation.md) |
| Bundled server executable (MSI) | `revit-model-mcp.exe --version` and an MCP `initialize` plus `tools/list` over stdio (79 tools) pass in CI on Windows without uv or Python on the PATH; the registration written by **Connect AI app** for it is covered by unit tests, not by a recorded client run | [Validation](validation.md) |
| Claude Code | Configuration documented in [install](install.md); no recorded validation run | |
| Cursor | Not tested | Any client that starts a stdio server should work with the same JSON configuration. |
| Codex CLI | Not tested | |
| VS Code with GitHub Copilot | Not tested | |
| LM Studio, Jan and other local clients | Not tested | |
| Claude Desktop chat (cloud) | Not supported | Cloud chat cannot start a local stdio server. |

## What "not tested" means

The server follows MCP specification `2025-11-25` over stdio. A compliant client can use it. Differences in tool-list size limits, schema handling and confirmation behavior between clients are not covered. The server returns a confirmation token for risky calls, and the client has to show the preview to the user; a client that does not do this weakens that safeguard. Check this before you use a client for work on a real model.

If you try another client, report the result in [Discussions](https://github.com/sharafutdinovdi/revit-model-mcp/discussions).
