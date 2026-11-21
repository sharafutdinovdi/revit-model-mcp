---
description: "Connect Claude Desktop or Claude Code to Revit 2022-2027 with a verified release MSI, first-start steps, and connection diagnostics."
---

# Connect Claude to Revit

Model MCP connects Claude Desktop and Claude Code to a live Revit model through MCP.
The add-in runs in Revit on Windows, and the bundled server exposes model queries and actions to Claude.
Revit 2022-2027 is supported; actions are enabled by default, with a shared read-only switch.

## Install and connect

Follow [Install in five minutes](../install.md#install-in-five-minutes) on the Windows Revit computer.
The MSI includes the add-in and server; no Python or uv installation is needed.
In **Connect AI app**, click **Add** for Claude Desktop.
For Claude Code, click **Copy command** and run it in a terminal.
Fully restart Claude, then ask: **Call `revit_ping`, then tell me which model is open.**

For installation through a local agent, use [Install with an AI agent](../agent-install.md) and explicitly select Claude Desktop or Claude Code.
The manual quick path explains the separate Windows and Revit trust prompts and optional download verification.

## First review

Ask: **Run a read-only model overview. Report warnings, unloaded links, and incomplete checks with element IDs.**
The `model_overview` prompt follows the coordinator guide.
The ribbon read-only switch locks changes across Revit instances; chat unlock requires explicit confirmation.

## Further reading

- [Install with an AI agent](../agent-install.md)
- [Tested clients and validation limits](../tested-clients.md)
- [macOS or a remote Windows workstation over SSH](../transport.md)
- [Read-only, Undo, and privacy](safety.md)
