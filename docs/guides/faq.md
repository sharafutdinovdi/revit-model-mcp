---
description: "Model MCP FAQ: Revit versions, pricing, MIT licence, offline use, macOS, installation, clients, tool counts, privacy, Undo, and uninstall."
---

# FAQ

Model MCP connects MCP clients to live Revit models on Windows.
These answers cover installation, compatibility, cost, and data handling; the linked references define the full contracts.

## Which Revit versions are supported?

Model MCP supports Revit 2022, 2023, 2024, 2025, 2026, and 2027 on Windows 10 or 11.

## Is Model MCP free?

Model MCP has no purchase price. Revit and the selected AI service may require separate paid licences or subscriptions.

## What is the licence?

Model MCP uses the MIT licence. Commercial use, modification, and redistribution are permitted under its terms.

## Can it work offline?

The release script supports offline installation with a local release folder. Runtime use can stay offline with a local MCP client and local model, cached dependencies, and update checks disabled. Cloud AI services still require a connection.

## Can it run on macOS or Linux?

The MCP server can run on macOS or Linux and connect over SSH to Revit on Windows. The Revit add-in requires Windows.

## Are administrator rights required?

The SingleUser MSI installs for the current Windows account without administrator rights. The MultiUser MSI is intended for administrator-managed deployment.

## Why does Revit ask to Always Load the add-in?

Current release builds are not code-signed. After verifying the release source and checksum, choose Always Load for Model MCP to permit loading at later starts. See [code signing](../code-signing.md).

## Which AI clients can connect?

The installer supports Claude Desktop, Claude Code, Cursor, VS Code, and Codex registration. Other stdio MCP clients can use manual configuration. Live validation differs by client; see [tested clients](../tested-clients.md).

## Can ChatGPT in the browser connect directly?

Model MCP does not provide a hosted ChatGPT connector. ChatGPT cloud chat cannot start the local stdio server. Use local Codex registration or another supported local MCP client.

## How many tools are available?

The documented default catalog has 89 tools. The core and read selection has 22. See the maintained [tool reference](../tools.md#toolsets) for the current release and toolset controls.

## Does model data leave the PC?

Model MCP does not send model data to its maintainer or an AI provider. It returns requested responses to the selected MCP client, which may send them to a cloud provider. SSH and configured remote routes also move responses between computers. See [privacy](../privacy.md).

## Is it read-only by default?

Actions are enabled by default. The shared workstation lock enables read-only mode across Revit versions and instances for the Windows account. Machine policy can enforce stronger restrictions.

## Can a change be undone?

Each committed transactional edit creates one named Revit Undo entry. The revit_undo_last tool works only while that action is still the most recent change. Undo does not reverse saved files, exports, or other external effects.

## Does installation require Python or the .NET SDK?

The release MSI includes the server executable and add-in files. The standard release installation needs neither a Python installation nor the .NET SDK.

## What should happen on first start?

Start Revit, choose Always Load for Model MCP, open a model, and restart the registered AI app. Complete the returned humanSteps, run doctor, and verify a successful revit_ping.

## What if a request times out?

A timeout or failed verification can follow a committed edit. Inspect the response, activity, and current model state before retrying. Preserve the warning in the report.

## How is Model MCP uninstalled?

Close Revit and use Windows Installed apps, or the release script uninstall command for the installed version. Client configuration and local settings remain. See [uninstall steps](../agent-install.md#uninstall) and [data removal](../privacy.md#data-retention).

## Which server is best for a particular workflow?

Choose by supported Revit year, required operations, deployment, and privacy requirements. The [comparison](revit-mcp-servers-compared.md) links each project's own documentation and identifies distinct strengths.

## Further reading

- [Install with an AI agent](../agent-install.md)
- [Read-only, Undo, and privacy](safety.md)
- [Tool reference](../tools.md)
