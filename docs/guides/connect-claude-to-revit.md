---
description: "Connect Claude Desktop or Claude Code to Revit 2022-2027 with a verified release MSI, first-start steps, and connection diagnostics."
---

# Connect Claude to Revit

Model MCP connects Claude Desktop and Claude Code to a live Revit model through MCP.
The add-in runs in Revit on Windows, and the bundled server exposes model queries and actions to Claude.
Revit 2022-2027 is supported; actions are enabled by default, with a shared read-only switch.

## Install and connect

1. Close Revit on Windows 10 or 11.
2. Give a local installation agent this prompt, naming Claude Desktop or Claude Code as the target:

    ```text
    Install Model MCP for Revit from https://github.com/sharafutdinovdi/revit-model-mcp. Follow docs/agent-install.md exactly. Configure Claude Desktop only.
    ```

3. Follow [MSI download and verification](../agent-install.md#download-and-verify).
   The MSI includes the add-in and server executable.
4. Start Revit, choose **Always Load** for Model MCP at the first-start prompt, and open a model.
5. Use **Model MCP > Model MCP > Connect AI app** to register Claude Desktop or obtain the Claude Code registration command, then restart Claude completely.
6. Follow [connection verification](../agent-install.md#verify), then ask Claude: **What model is open in Revit?**
   A successful `revit_ping` establishes a live connection.

Claude Code registration uses the command provided by **Connect AI app**.
The [one-command installer](../agent-install.md#next-release-one-command-installer) arrives with the next release.

## First review

Ask: **Run a read-only model overview. Report warnings, unloaded links, and incomplete checks with element IDs.**
The `model_overview` prompt follows the coordinator guide.
The ribbon read-only switch locks changes across Revit instances; chat unlock requires explicit confirmation.

## Further reading

- [Install with an AI agent](../agent-install.md)
- [Tested clients and validation limits](../tested-clients.md)
- [macOS or a remote Windows workstation over SSH](../transport.md)
- [Read-only, Undo, and privacy](safety.md)
