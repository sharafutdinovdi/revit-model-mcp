# Security

See the [privacy policy](privacy.md) for data handling, retention and contact information.

The model read API is always available.
Action tools are enabled by default; either `REVIT_MCP_READ_ONLY=1` in the Python server environment or the
workstation `%LOCALAPPDATA%\RevitModelMcp\read-only` file switches them to read-only mode without hiding them,
described in [actions](actions.md).
`revit_execute_code` runs arbitrary C# with the Revit user's rights. It can read or write files and call network APIs. The read-only gates refuse this action. Every call except `dry_run` needs a five-minute, single-use confirmation token bound to the code hash, the transaction mode and the target document, and `transaction="none"`, which can change the model without a single undo entry, is refused by default. The token cannot make the code safe: it only forces a pause so that the user sees what will run. Submitted source is saved in the local audit directory and must be treated as sensitive.
`revit_export_nwc` may write to a drive path or a UNC path on a trusted share unless read-only mode is active. It never transfers the NWC file to the client; logging omits the export path.
UNC shares are denied by default for export, save, open, shared parameter files and NWC settings XML. Add approved `\\server\share` roots to the `trustedNetworkRoots` string array in `%LOCALAPPDATA%\RevitModelMcp\settings.json` and restart Revit; mapped drives remain allowed as drive paths.
`RSN://` paths use Revit Server and are not restricted by `trustedNetworkRoots`.
The default surface covers ping, active document information, the open document list, instance information, catalogs, element queries and aggregates, views and their elements, element parameters, warnings, relations, PNG view export and the four coordinator tools for model health, links, shared coordinates and parameter fill.
The [command executor](../src/RevitModelMcp.Addin/Control/ReadCommandExecutor.cs) and readers open no Revit transactions and expose no element creation, deletion, parameter setters or model save operations.
View export calls `Document.ExportImage` and writes an image file.
Channel jobs, responses, heartbeats and diagnostic logs also write files outside the model.

`REVIT_MCP_REDACT_PATHS=1` or `--redact-paths` removes directories from response `documentPath`, `path` and `centralPath` fields, including nested results and instance listings.
It also reduces Windows drive and UNC paths in `confirmationText`, `summary`, `error`, `message`, `warning` and `warnings` strings at any depth to file names, including tool errors.
Model names, parameter values, remaining message text and exported image `localPath` values remain visible.
Redaction applies to outgoing Python responses, not channel files or add-in logs.

HTTP binds to `127.0.0.1:53110` by default.
A per-user 32-byte random bearer token is generated in `settings.json`; its protected NTFS ACL grants access only to the current user.
The token is never logged.
All HTTP routes except `/health` require it; health exposes the active document name and process information.
Before sending that token, the Python HTTP client verifies a nonce proof from the add-in on the existing health request.
There is no built-in TLS: put remote access behind a tunnel or a TLS proxy.
Set `REVIT_MCP_HTTP_ENABLED=0` in Revit's environment or `httpEnabled=false` in settings to disable the listener entirely.
MCP action calls are refused in read-only mode over every transport.
Direct HTTP action jobs require the bearer token and are refused with HTTP 403 while the workstation read-only file is present; the Python server's `REVIT_MCP_READ_ONLY` setting does not apply to direct callers.

The named pipe `\\.\pipe\RevitModelMcp.<pid>` has a protected ACL that grants access only to the Windows user running Revit.
The add-in creates the first instance with that ACL and disables the pipe if the name is already in use.
The add-in also drops any pipe client that connects from another computer.
The pipe needs no token: holding the Revit user's credentials already grants access to the file channel and the model.
Pipe requests are limited to 1 MiB, and action jobs are refused while the workstation `read-only` file exists.

SSH mode stores no credentials.
Authentication and routing use the local OpenSSH configuration and agent.
Running the server on the workstation over SSH stdio needs the same account as Revit and opens no additional port.
The workstation `read-only` file refuses action jobs only. It does not restrict a person who holds an SSH key for the Revit account.
For SSH stdio, prefix a dedicated public key entry in `authorized_keys` with `restrict,command="revit-model-mcp --redact-paths"`.
Windows OpenSSH administrators place this entry in `%ProgramData%\ssh\administrators_authorized_keys`.
The `ssh:<alias>` file channel requires a full shell key and cannot use this forced command.
On macOS and Linux, the default multiplexing socket directory is verified as user-owned and set to mode `0700`; multiplexing is disabled if this fails.
The Windows file channel relies on the account's filesystem permissions.
See [transport](transport.md) and [security reporting](../SECURITY.md).

## Document lifecycle safety

Open, close, save and synchronize are refused in read-only mode, same as every other action. A central model opened through MCP defaults to detached mode. `local_copy` creates a new local file and leaves the central unchanged until an explicitly confirmed synchronization. Saving an open central model or saving under a known central path is refused.

Save, sync and any close that can discard changes require a five-minute, single-use confirmation token. So do `revit_remove_links` without `dry_run`, `revit_export` and `revit_export_nwc` with `overwrite=true` when a target file exists, `revit_execute_code` without `dry_run`, and `revit_process_models` with `code`. These tokens guard against mistakes and against instructions injected into model data or tool output. They do not guard against an MCP client that confirms by itself, because the client receives the token. Use read-only mode for unattended runs. The first call only describes the operation. The client must show `confirmationText` and must not send the token back until the user explicitly agrees in chat. A failed verification or timeout can occur after Revit commits a change; inspect document state before a retry. Neither the token nor the action gate is a substitute for model backups and Revit permissions.

## Verify downloads

Release assets carry GitHub build provenance attestations.
After downloading an asset, verify it with the GitHub CLI:

```sh
gh attestation verify RevitModelMcp-<version>-SingleUser.msi --owner sharafutdinovdi
```

A successful command exits with code 0 and reports a verified attestation.
Check that the repository is `sharafutdinovdi/revit-model-mcp`, the signer workflow is `.github/workflows/release.yml`, and the source commit matches the intended release tag.
For an explicit repository and workflow constraint:

```sh
gh attestation verify RevitModelMcp-<version>-SingleUser.msi \
  --repo sharafutdinovdi/revit-model-mcp \
  --signer-workflow sharafutdinovdi/revit-model-mcp/.github/workflows/release.yml
```

The same command accepts a per-year ZIP, wheel, source distribution, `.mcpb` or `SHA256SUMS.txt` in place of the MSI filename.
In a non-interactive shell, add `--format json` or the command prints nothing.
The attestation binds the downloaded file's digest to this repository's build workflow and a source commit.
It does not certify that the program is safe or cover packages downloaded later by uv.
The MSI and executable files are not Authenticode-signed; Windows may still show an unknown publisher warning.
The bundle has no MCPB certificate signature.
Attestations are available for releases built after provenance was enabled; older releases are not retroactively attested.
