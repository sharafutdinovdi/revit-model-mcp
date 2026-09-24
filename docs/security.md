# Security

See the [privacy policy](privacy.md) for data handling, retention and contact information.

The model API is read-only by default.
Action tools are absent unless `REVIT_MCP_ALLOW_WRITE=1`; action execution also requires the workstation gate file described in [actions](actions.md).
`revit_export_nwc` may write to any valid absolute workstation path only when both action gates are enabled. It never transfers the NWC file to the client; logging omits the export path.
The default surface covers ping, document and instance information, catalogs, element queries and aggregates, views and their elements, element parameters, warnings, relations, PNG view export and the four coordinator tools for model health, links, shared coordinates and parameter fill.
The [command executor](../src/RevitModelMcp.Addin/Control/ReadCommandExecutor.cs) and readers open no Revit transactions and expose no element creation, deletion, parameter setters or model save operations.
View export calls `Document.ExportImage` and writes an image file.
Channel jobs, responses, heartbeats and diagnostic logs also write files outside the model.

`REVIT_MCP_REDACT_PATHS=1` or `--redact-paths` reduces response `documentPath` and every `path` field, including link and image paths, to file names.
This covers nested results and instance listings.
Model names, parameter values, error text, channel files and exported image `localPath` values remain visible.

HTTP binds to `127.0.0.1:53110` by default.
A per-user 32-byte random bearer token is generated in `settings.json`; its protected NTFS ACL grants access only to the current user.
The token is never logged.
All HTTP routes except `/health` require it; health exposes the active document name and process information.
There is no built-in TLS: put remote access behind a tunnel or a TLS proxy.
Set `REVIT_MCP_HTTP_ENABLED=0` in Revit's environment or `httpEnabled=false` in settings to disable the listener entirely.
MCP action calls require both gates over every transport.
Direct HTTP action jobs require the bearer token and workstation gate; the Python registration flag does not apply to direct callers.

The named pipe `\\.\pipe\RevitModelMcp.<pid>` has a protected ACL that grants access only to the Windows user running Revit.
Other local users and services running under other accounts cannot open it unless an administrator changes that ACL.
The add-in also drops any pipe client that connects from another computer.
The pipe needs no token: holding the Revit user's credentials already grants access to the file channel and the model.
Pipe requests are limited to 1 MiB, and action jobs still require the workstation gate.

SSH mode stores no credentials.
Authentication and routing use the local OpenSSH configuration and agent.
Running the server on the workstation over SSH stdio needs the same account as Revit and opens no additional port.
The default multiplexing socket directory has mode `0700` on macOS and Linux.
The Windows file channel relies on the account's filesystem permissions.
See [transport](transport.md) and [security reporting](../SECURITY.md).

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
The attestation binds the downloaded file's digest to this repository's build workflow and a source commit.
It does not certify that the program is safe or cover packages downloaded later by uv.
The MSI and executable files are not Authenticode-signed; Windows may still show an unknown publisher warning.
The bundle has no MCPB certificate signature.
Attestations are available for releases built after provenance was enabled; older releases are not retroactively attested.
