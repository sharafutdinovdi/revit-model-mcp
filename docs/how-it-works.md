# How it works

The Python server runs beside the MCP client and communicates over stdio.
The add-in runs inside Revit on Windows and reads the active document through an ExternalEvent.
A remote client can use HTTP with a bearer token or the SSH file channel.

## First call

1. Revit loads `RevitModelMcp.addin` and starts a file watcher, heartbeat and optional HTTP listener.
2. The MCP client calls `revit_ping`; the server constructs `{"command":"ping"}`.
3. HTTP queues the job in memory; local or SSH mode publishes it as `trigger.txt` in the Windows channel directory.
4. An ExternalEvent invokes the shared command handler on Revit's API thread.
5. The handler returns `success:true` with `data:"pong"`; the server delivers the JSON result over MCP.

The channel directory defaults to `%LOCALAPPDATA%\RevitModelMcp`, not `%LOCALAPPDATA%\RevitDevLoader`.
The add-in loads directly through its manifest and does not require a separate loader.
The [feed format](feed-format.md) documents paths and response fields.

## Read-only and actions

The default tools read model data or export a view to PNG.
Exports and diagnostics write files outside the Revit model.
Actions appear in MCP only when the Python process starts with `REVIT_MCP_ALLOW_WRITE=1`.
The add-in also requires `%LOCALAPPDATA%\RevitModelMcp\allow-write` for every action.
Deleting that gate file disables action execution without restarting Revit.
The MCP environment gate controls tool registration; direct HTTP callers are checked against the token and workstation gate.

Selection and navigation use UI calls.
Model changes and temporary isolation run in individual transactions.
Warnings are dismissed and reported on success; unresolved errors roll back the action.
Action handling attempts TaskDialog overrides and reports their messages.
The tools do not save the model.

## Routing and waiting

HTTP defaults to `127.0.0.1:53110`; the bearer token is stored in the per-user `settings.json`.
Each HTTP endpoint belongs to one Revit process.
A tunnel can connect a remote client while the listener stays on loopback.
See [transport configuration](transport.md) for LAN and Tailscale routes.

Local and SSH modes run Windows PowerShell under the Revit account.
They locate responses by command and filename; they have no request correlation ID.
Use one server process per file channel directory and a distinctive `document` filter for multiple Revit instances.
HTTP polls by job ID and retains completed results for ten minutes.
Timeouts do not cancel accepted jobs, especially actions.

The [architecture](architecture.md) describes scheduling and failure behavior.
The [server reference](../server/README.md) lists environment settings.
