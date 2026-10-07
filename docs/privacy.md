# Privacy policy

Effective date: October 7, 2026.
Changes to this policy are recorded in the repository history.

## Summary

- Your model data stays on your machines. The project has no server that receives it.
- There is no analytics, telemetry, crash reporting or advertising code in the add-in or the Python server.
- Revit model data leaves the workstation only as responses to the MCP client you chose. That client may send it to an AI model service under its own policy.
- The only automatic requests to the project's hosting are update checks to `api.github.com` (add-in) and `pypi.org` (server). They carry no model data and you can switch them off.

## Data collection

Revit Model MCP reads the model open in Revit on the configured workstation and returns the information requested by the MCP client.
Requested data can include model names, paths, element parameters, geometry, warnings and exported view images.
Actions change Revit data by default. Set `REVIT_MCP_READ_ONLY=1` or create the workstation `read-only` file to disable them.
The maintainer collects no personal data, accounts or usage statistics.
Runtime network connections serve the configured Revit workstation through HTTP or SSH and any user-configured proxy or tunnel.

## Update checks

The add-in contacts `api.github.com` at most once every 24 hours after Revit starts to check the latest stable release.
The request is a plain HTTPS `GET` for the latest release of this repository, sent through the system proxy with the user agent `RevitModelMcp updater`.
It contains no model data, document names, user names or license information.
GitHub sees the network address of the request, as it does for any web request, and handles it under [its own privacy statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
The `REVIT_MCP_UPDATE_FEED` environment variable redirects the check to another HTTPS (or loopback HTTP) release feed, for example an internal mirror.
Per-user installs download the SingleUser MSI and its SHA256 checksum from the release assets when an update is available. Per-machine installs only show a notice.

The Python server checks for a newer package once every 24 hours.
It looks up the package page on `pypi.org` and refreshes the cached package with `uvx`, which downloads from PyPI and its package hosting service.
These requests contain no model data.
The bundle launcher uses uvx to download the package and dependencies from PyPI and its package hosting service during installation or updates.
Prerelease bundles download the package wheel from GitHub Releases.

To turn the checks off:

| Check | How to disable |
| --- | --- |
| Add-in, one machine | Install the MSI with `UPDATECHECK=0`, or set `{"updateCheck":false}` in `%ProgramData%\RevitModelMcp\settings.json` (all users) or `%LOCALAPPDATA%\RevitModelMcp\settings.json` (current user). |
| Add-in, environment | Set `REVIT_MCP_NO_UPDATE_CHECK=1` in the environment of the Revit process. |
| Add-in, managed install | A build made with the MSBuild property `ManagedInstall=true` never checks for updates. Use it when another system delivers add-in updates. |
| Server | Set `REVIT_MCP_NO_UPDATE_CHECK=1` in the MCP client's server settings. |

Any one of these is enough. Without them the default behavior stays on.
See [automatic updates](updates.md) for details.

## AI client data flow

The add-in and the server do not send model data to the maintainer, to GitHub or to any AI provider.
The MCP client you choose (for example Claude Desktop or Claude Code) starts the server and receives its responses.
What that client does next is outside this project: it may pass the responses to a language model service in the cloud or to a local model, depending on its configuration.
Review the client's privacy policy and your project's confidentiality rules before you connect a client to a confidential model.
Use read-only mode and `REVIT_MCP_REDACT_PATHS=1` to reduce what a client can change or see.
No AI service is contacted by this project itself, and it ships no API key for one.

## Usage and storage

Responses are returned to the MCP client for the requested operation.
The local and SSH file channels write requests, responses and exported PNG files under `%LOCALAPPDATA%\RevitModelMcp` on the Windows workstation.
`REVIT_MCP_CHANNEL_DIR` overrides the channel directory.
Settings and the workstation `read-only` file remain in the default directory.
HTTP keeps completed job responses in memory until expiry; exported images also use the workstation channel directory.
Downloaded PNG files are written to the client path specified by `save_to`, or a new `revit-view-*` directory in the client's temporary directory.

`REVIT_MCP_REDACT_PATHS=1` removes directories from response `documentPath`, `path` and `centralPath` fields.
It also reduces Windows drive and UNC paths in `confirmationText`, `summary`, `error`, `message`, `warning` and `warnings` strings at any depth to file names.
The bundle enables this setting by default.
Document names, parameter values, remaining message text and exported image `localPath` values remain visible.
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
Logs stay on the workstation. The project never uploads them; they leave the machine only if you attach them to a support request, so review and sanitize them first.
Python diagnostics go to the MCP client's logging stream; client retention is controlled by that client.

To remove project data, close Revit and the MCP server, uninstall the add-in and desktop extension, and remove `%LOCALAPPDATA%\RevitModelMcp`.
Also remove any overridden channel directory, the log directories above and downloaded PNG files on the client.
Uninstalling alone leaves local settings intact.
Client conversations, client logs and package caches require separate deletion through the client or package manager.

## Contact

Privacy questions can be sent to [sharafutdinov.di.dev@outlook.com](mailto:sharafutdinov.di.dev@outlook.com).
Security-sensitive reports can use the [private GitHub security advisory form](https://github.com/sharafutdinovdi/revit-model-mcp/security/advisories/new).
