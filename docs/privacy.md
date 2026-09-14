# Privacy policy

Effective date: September 14, 2026.
Changes to this policy are recorded in the repository history.

## Data collection

Revit Model MCP reads the model open in Revit on the configured workstation and returns the information requested by the MCP client.
Requested data can include model names, paths, element parameters, geometry, warnings and exported view images.
Optional actions can change the model when both write gates are enabled.
The project includes no analytics, telemetry or crash reporting.
Runtime network connections serve the configured Revit workstation through HTTP or SSH and any user-configured proxy or tunnel.
The bundle launcher uses uvx to download the package and dependencies from PyPI and its package hosting service during installation or updates.
Prerelease bundles download the package wheel from GitHub Releases.
These package downloads do not send Revit model data.

## Usage and storage

Responses are returned to the MCP client for the requested operation.
The local and SSH file channels write requests, responses and exported PNG files under `%LOCALAPPDATA%\RevitModelMcp` on the Windows workstation.
`REVIT_MCP_CHANNEL_DIR` overrides the channel directory.
Settings and the workstation write gate remain in the default directory.
HTTP keeps completed job responses in memory until expiry; exported images also use the workstation channel directory.
Downloaded PNG files are written to the client path specified by `save_to`, or a new `revit-view-*` directory in the client's temporary directory.

`REVIT_MCP_REDACT_PATHS=1` removes directories from response `documentPath` and nested `path` fields.
The bundle enables this setting by default.
Model names, parameter values, errors and exported image `localPath` values remain visible.
Redaction applies to outgoing Python responses, not workstation files or add-in logs.
Boolean settings also accept `true/false`, `yes/no` and `on/off`, without regard to case or surrounding whitespace; `1/0` remains the canonical form.

## Third-party sharing

The project does not send model data to its maintainer or an analytics service.
The selected MCP client receives the requested responses and may process or retain them under its own policy.
Claude Desktop is governed by [Anthropic's privacy policy](https://www.anthropic.com/legal/privacy).
Other MCP clients have their own privacy policies.
User-configured remote hosts, proxies and tunnels are part of the selected transport route.

## Data retention

The [transport contract](transport.md) specifies that completed HTTP results expire after ten minutes.
The add-in checks expiry once per minute and attempts to delete associated HTTP image artifacts.
In-memory results also disappear when the Revit process exits.
The file channel consumes the trigger during processing and attempts to remove response files and source PNG exports during retrieval.
Pending files and files left after interrupted operations or failed cleanup can remain on disk; there is no age-based expiry for these files.
Downloaded client PNG files have no project-managed expiry.

Add-in logs are stored in the Windows Documents folder under `RevitModelMcp\Logs`, with `%TEMP%\RevitModelMcp\Logs` as a fallback.
Logs rotate at 10 MiB, and startup cleanup keeps the 14 most recently modified log files.
This is a file-count limit, not a retention period in days.
Logs can contain model names, paths and exception details even when Python response redaction is enabled.
Python diagnostics go to the MCP client's logging stream; client retention is controlled by that client.

To remove project data, close Revit and the MCP server, uninstall the add-in and desktop extension, and remove `%LOCALAPPDATA%\RevitModelMcp`.
Also remove any overridden channel directory, the log directories above and downloaded PNG files on the client.
Uninstalling alone leaves local settings intact.
Client conversations, client logs and package caches require separate deletion through the client or package manager.

## Contact

Privacy questions can be sent to [sharafutdinov.di.dev@outlook.com](mailto:sharafutdinov.di.dev@outlook.com).
Security-sensitive reports can use the [private GitHub security advisory form](https://github.com/sharafutdinovdi/revit-model-mcp/security/advisories/new).
