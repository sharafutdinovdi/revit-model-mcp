# Security policy

Private vulnerability reporting is enabled for this repository.
Report vulnerabilities through [GitHub's private report form](https://github.com/sharafutdinovdi/revit-model-mcp/security/advisories/new) or email [sharafutdinov.di.dev@outlook.com](mailto:sharafutdinov.di.dev@outlook.com).
Include the affected version and steps to reproduce.
Do not include credentials or confidential model data.
Keep undisclosed vulnerabilities out of public issues and Discussions.

The file channel uses operating system permissions.
Limit channel directory access to trusted users.
Responses can contain model paths and parameter values.
See [server privacy settings](server/README.md#responses-and-privacy).

HTTP binds to loopback by default and authenticates every route except `/health` with a per-user token.
Health reveals document name, Revit version, process ID and workstation read-only state.
Protect `%LOCALAPPDATA%\RevitModelMcp\settings.json` and use an encrypted tunnel for remote access.
The listener has no built-in TLS.
Actions require the workstation gate; the Python server also requires an explicit registration flag.
See [transport](docs/transport.md) for bind settings and [actions](README.md#actions-opt-in) for the gates.

The v0.1 series is a preview; report security issues against the current main branch or latest release.
