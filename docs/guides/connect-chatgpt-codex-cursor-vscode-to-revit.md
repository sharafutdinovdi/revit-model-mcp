---
description: "Connect local Codex, Cursor, or VS Code to Revit, and understand the boundary between ChatGPT cloud chat and local MCP access."
---

# Connect ChatGPT, Codex, Cursor or VS Code to Revit

Model MCP provides local stdio registration for Codex, Cursor, and VS Code with GitHub Copilot.
ChatGPT cloud chat cannot start this local stdio server; the documented OpenAI route is local Codex registration.
The add-in runs on Windows with Revit 2022-2027, while a remote MCP client can use SSH.

## Install a local client

1. Close Revit and select the target client.
2. Follow [one-command installation](../agent-install.md#one-command-per-client) with `-Client codex`, `-Client cursor`, or `-Client vscode`.
   The release MSI includes the server executable and requires no Python or SDK installation.
3. Start Revit, choose **Always Load**, and open a model.
4. Restart the selected client and finish the installer's `humanSteps`.
5. Run [doctor](../agent-install.md#verify) and request `revit_ping` before asking for model work.

The same installation can target several clients with `-Client cursor,codex`.
Existing configuration files receive timestamped backups before changes.
The Model MCP ribbon's **Connect AI app** provides registration after installation.

## ChatGPT and remote access

Codex uses the local `.codex/config.toml` registration described in [installation](../install.md#connect-an-ai-app).
A ChatGPT conversation alone does not create that registration or reach the Windows add-in.
Model MCP does not provide a hosted ChatGPT connector.
Its direct HTTP API uses bearer authentication and is distinct from a hosted MCP connector.

For Codex or another MCP client on macOS, keep Revit on Windows and configure the [SSH transport](../transport.md).
Configuration support does not imply a recorded live test for every client.
Consult the [tested clients matrix](../tested-clients.md) before deployment.

## Further reading

- [Install with an AI agent](../agent-install.md)
- [Toolsets and context budget](../tools.md#toolsets)
- [Security and transport authentication](../security.md)
- [FAQ](faq.md)
