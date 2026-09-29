# Security policy

Private vulnerability reporting is enabled for this repository.
Report vulnerabilities through [GitHub's private report form](https://github.com/sharafutdinovdi/revit-model-mcp/security/advisories/new) or email [sharafutdinov.di.dev@outlook.com](mailto:sharafutdinov.di.dev@outlook.com).
Include the affected version and steps to reproduce.
Do not include credentials or confidential model data.
Keep undisclosed vulnerabilities out of public issues and Discussions.

The add-in keeps the channel directory private. If another account can write to the override directory, the add-in uses the private default directory instead. Set `REVIT_MCP_CHANNEL_DIR` on the server to that directory or remove the server override.
Responses can contain model paths and parameter values.
See [server privacy settings](server/README.md#responses-and-privacy).

HTTP binds to loopback by default and authenticates every route except `/health` with a per-user token.
Health reveals document name, Revit version, process ID and workstation read-only state.
Protect `%LOCALAPPDATA%\RevitModelMcp\settings.json` and use an encrypted tunnel for remote access.
The listener has no built-in TLS.
Actions are enabled by default. Set `REVIT_MCP_READ_ONLY=1` in the Python server or create the workstation `read-only` file to disable them.
See [transport](docs/transport.md) for bind settings and [actions](README.md#actions-opt-in) for read-only controls.

0.x releases are previews; report against the latest release or main.
