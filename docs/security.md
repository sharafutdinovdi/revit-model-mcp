# Security

The model API is read-only by default.
Action tools are absent unless `REVIT_MCP_ALLOW_WRITE=1`; action execution also requires the workstation gate file described in [actions](actions.md).
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

SSH mode stores no credentials.
Authentication and routing use the local OpenSSH configuration and agent.
The default multiplexing socket directory has mode `0700` on macOS and Linux.
The Windows file channel relies on the account's filesystem permissions.
See [transport](transport.md) and [security reporting](../SECURITY.md).
