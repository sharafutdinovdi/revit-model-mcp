# Transport

## File channel

The default directory is `%LOCALAPPDATA%\RevitModelMcp` on the Windows account running Revit.
Set `REVIT_MCP_CHANNEL_DIR` to an absolute Windows path to override it.
The server and Revit must use the same directory.
The Revit environment must contain the override before Revit starts.

| File | Role |
|---|---|
| `mcp_<uuid>.tmp` | JSON job before publication |
| `trigger.txt` | Published job awaiting pickup |
| `response_<timestamp>_<command>.json` | Add-in response |
| `view_<timestamp>_<id>.png` | Exported view before download |
| `instance_<processId>.json` | Instance heartbeat |

The transport uses files and starts no network listener inside Revit.
Revit API work runs through ExternalEvent.
Pending files remain on disk if the server stops.
There is no automatic cancellation of a published job.
The channel relies on Windows file permissions.

## Local host

`REVIT_MCP_HOST=local` is the default.
The server invokes `powershell.exe -NoProfile -NonInteractive -EncodedCommand` on Windows.
The process checks Revit, publishes jobs and reads responses under the current Windows account.
This mode requires PowerShell and a running Revit instance with the add-in loaded.
macOS and Linux clients use SSH mode to reach Windows.

## SSH host

`REVIT_MCP_HOST=ssh:<alias>` selects a host in the client's SSH configuration.
`--host ssh:<alias>` provides the same setting on the command line.
The client invokes `ssh` with batch mode and a 45-second connection timeout.
The remote command runs Windows PowerShell with a UTF-16LE base64-encoded script.
Host aliases are validated and PowerShell path literals escape single quotes.
SSH credentials and routing belong to the user's SSH configuration.

Responses and exported PNG files are transferred as base64 in the PowerShell result.
The server decodes the image into `save_to` or a new temporary directory.
An existing destination file produces an error.
The MCP result contains the image path and metadata without base64.

SSH starts share a limit of five connections per rolling 30 seconds within one host object.
Polling waits ten seconds between attempts.
Job preparation and final response retrieval each use one command.
Transient failures during polling can be retried within the remaining timeout.
Local mode uses the same file operations and polling without the SSH connection limiter.

## Optional activation

Set `REVIT_MCP_ACTIVATE_TASK` to the name of an existing Windows scheduled task that activates Revit.
After 60 seconds without pickup the next check can invoke that task once.
Activation only occurs if `trigger.txt` still exists.
The server checks the task result and reports activation failure separately.
The task is optional and is never created by the server.
The default pickup timeout is 300 seconds, followed by a separate 120-second response timeout.

## Path redaction

`REVIT_MCP_REDACT_PATHS=1` or `--redact-paths` strips directories from response `documentPath` fields.
This applies to responder metadata and instance listings.
Image `localPath` remains available to the MCP client.
The option does not sanitize channel files or arbitrary strings in model data and errors.
See [server configuration](../server/README.md#configuration).
