# What Model MCP deliberately does not do

A short list of limits, so that you know what to expect before you install.

- **It does not run an AI model.** There is no model, no API key and no chat window. You bring an MCP client and its model provider.
- **It does not send data to the maintainer.** No telemetry, analytics or crash reports. The only automatic requests are update and package checks that you can turn off.
- **It does not host anything.** There is no cloud service, account or license server.
- **It does not sandbox C# execution.** `revit_execute_code` runs with your Windows rights. A confirmation token makes you look first. If that is too much risk, turn the tool off by policy.
- **It does not stop an MCP client that confirms for itself.** The client receives each confirmation token. Use read-only mode or policy for unattended runs.
- **It does not provide TLS or single sign-on.** The HTTP listener is optional, loopback by default and uses one bearer token. Use a tunnel or TLS proxy for remote access.
- **It does not authenticate individual callers.** All HTTP callers share the token.
- **It does not encrypt its files.** Use disk encryption.
- **It does not write to the Windows Event Log or forward logs.** Logs are local files.
- **It does not offer full family authoring or manage ACC or Forma models.** Family work is limited to the tools in the [tool reference](tools.md). Revit Server paths are for multi-model runs.
- **It does not run in Revit 2021 or older.** Supported Revit versions are 2022 to 2027.
- **It does not change tools or descriptions after install.** The tool list is fixed per release.
- **It does not install silently over a signed channel yet.** Release binaries are unsigned; see [code signing](code-signing.md).
- **It does not replace Revit's own checks.** Results report what the Revit API returns. A non-empty `skipped` list means a result is incomplete.

See the [security whitepaper](admin/security-whitepaper.md) for the full list of controls and residual risks.
