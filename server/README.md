# Revit Model MCP server

The Python package exposes Revit read tools over MCP stdio.
It requires Python 3.11 or later and the matching add-in loaded in Revit on Windows.

## Install and run

Run from the repository root:

```sh
uv run --directory server revit-model-mcp
```

Register a local Windows server with Claude Code:

```sh
claude mcp add revit-model-mcp -- uv run --directory /absolute/path/to/revit-model-mcp/server revit-model-mcp
```

For a client on macOS or Linux:

```sh
claude mcp add revit-model-mcp -- uv run --directory /absolute/path/to/revit-model-mcp/server revit-model-mcp --host ssh:revit-host
```

Replace the directory with the checkout path on the MCP client.
Replace `revit-host` with an alias from the client's SSH configuration.
The Windows SSH session must use the same account as Revit or an explicitly shared channel directory.

## Configuration

| Variable | Default | Behavior |
|---|---|---|
| `REVIT_MCP_HOST` | `local` | Runs Windows PowerShell locally. `ssh:<alias>` runs it through the local SSH client. `--host` overrides this variable. |
| `REVIT_MCP_ACTIVATE_TASK` | Unset | Optional existing Windows scheduled task. Runs once after 60 seconds if the trigger remains pending. The task must activate the interactive Revit window. No task is created by the server. |
| `REVIT_MCP_CHANNEL_DIR` | `%LOCALAPPDATA%\RevitModelMcp` on Windows | Absolute Windows channel path. Set the same value in the Python server environment and in Revit's environment before starting Revit. In SSH mode this path belongs to the remote host. |
| `REVIT_MCP_REDACT_PATHS` | Unset | `1` replaces every response `documentPath` value with its file name. `--redact-paths` enables the same behavior. |

The server reads activation configuration at process startup.
A configured task may restore and focus the Revit window.
Without a task the server only polls for pickup.

## Responses and privacy

Model paths occur in responder metadata and instance listings.
Redaction covers nested `documentPath` fields in successful MCP results.
It preserves exported image `localPath` values for clients that open the downloaded file.
It does not redact names, parameter values, add-in error text or files stored in the channel.
Revit model data and errors can retain their original language.
Python tool descriptions and server-generated messages are English.

## Request behavior

The default pickup timeout is 300 seconds.
The response timeout is 120 seconds after pickup.
Most tools accept `pickup_timeout_seconds` and `timeout_seconds`.
`revit_export_view` uses the defaults.
Supply `document` when multiple Revit instances run on the host.
Use a distinctive document title or file name.
Matching is case-insensitive and accepts substrings.
A pending job remains in the channel after pickup timeout and may execute later.
Use one MCP server process per channel directory.

See [transport](../docs/transport.md) for file handling and SSH behavior.

## Tests

```sh
uv run --with pytest pytest -q
```

The tests use mocked host operations and exercise MCP stdio without Revit.
