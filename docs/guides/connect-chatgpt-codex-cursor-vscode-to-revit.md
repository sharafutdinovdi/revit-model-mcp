---
description: "Connect local Codex, Cursor, or VS Code to Revit, and understand the boundary between ChatGPT cloud chat and local MCP access."
---

# Connect ChatGPT, Codex, Cursor or VS Code to Revit

Model MCP provides local stdio registration for Codex, Cursor, and VS Code with GitHub Copilot.
ChatGPT cloud chat cannot start this local stdio server; the documented OpenAI route is local Codex registration.
There is no separate ChatGPT Desktop configuration target or recorded ChatGPT Desktop validation.
The add-in runs on Windows with Revit 2022-2027, while a remote MCP client can use SSH.

## Install a local client

1. Follow [Install in five minutes](../install.md#install-in-five-minutes) on the Windows Revit computer.
   The MSI includes the server executable and needs no Python, uv or SDK installation.
2. In **Model MCP > Model MCP > Connect AI app**, click **Add** for Codex, Cursor or VS Code.
   Restart the selected client completely.
3. Ask: **Call `revit_ping`, then tell me which model is open.**
   A successful ping confirms Revit connectivity; the model response confirms project access.

The same installation supports registration in several clients through **Connect AI app**.
Each registration requires an explicit selection.
For installation through a local agent, use [Install with an AI agent](../agent-install.md).

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
