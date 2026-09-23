# Revit Model MCP server

The Python package exposes Revit tools over MCP stdio, read-only by default.
Optional actions require `REVIT_MCP_ALLOW_WRITE=1` and a workstation `allow-write` gate.
It requires Python 3.11 or later and the matching add-in loaded in Revit on Windows.

## Install and run

After the first PyPI release, run the published package with uv:

```sh
uvx revit-model-mcp
```

Register the local Windows server with Claude Code:

```sh
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -- uvx revit-model-mcp
```

For a client on macOS or Linux:

```sh
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=ssh:revit-host -e REVIT_MCP_REDACT_PATHS=1 -- uvx revit-model-mcp
```

Replace `revit-host` with an alias from the client's SSH configuration.
The Windows SSH session must use the same account as Revit or an explicitly shared channel directory.

Claude Desktop uses this entry in `claude_desktop_config.json` on Windows:

```json
{
  "mcpServers": {
    "revit-model-mcp": {
      "command": "uvx",
      "args": ["revit-model-mcp"],
      "env": {
        "REVIT_MCP_HOST": "local",
        "REVIT_MCP_REDACT_PATHS": "1"
      }
    }
  }
}
```

`uvx` must be available on the client's PATH; an absolute executable path is also supported.
A remote Desktop client uses `REVIT_MCP_HOST=ssh:revit-host`.

For development or before the first PyPI publication, run from the repository root:

```sh
uv run --directory server revit-model-mcp
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -- uv run --directory /absolute/path/to/revit-model-mcp/server revit-model-mcp
```

mcp-name: io.github.sharafutdinovdi/revit-model-mcp

## Configuration

| Variable | Default | Behavior |
|---|---|---|
| `REVIT_MCP_HOST` | `local` | Local PowerShell, `ssh:<alias>` or an `http://` / `https://` add-in endpoint. `--host` overrides it. |
| `REVIT_MCP_ALLOW_WRITE` | Unset | Only `1` registers the ten action tools, including `revit_batch` and `revit_export_nwc` at server startup; the workstation gate is also required. |
| `REVIT_MCP_TOKEN` | Unset | HTTP bearer token from workstation settings. `--token` overrides it. |
| `REVIT_MCP_SSH_MUX` | Enabled | `0` disables OpenSSH connection multiplexing. Local mode ignores SSH settings. |
| `REVIT_MCP_SSH_OPTIONS` | Unset | Extra SSH arguments, parsed with shell quoting and appended after built-in options, before the host. Example: `-o ServerAliveInterval=30 -p 2222`. |
| `REVIT_MCP_ACTIVATE_TASK` | Unset | Optional existing Windows scheduled task. Runs once after 60 seconds if the trigger remains pending. The task must activate the interactive Revit window. No task is created by the server. |
| `REVIT_MCP_CHANNEL_DIR` | `%LOCALAPPDATA%\RevitModelMcp` on Windows | Absolute Windows channel path. Set the same value in the Python server environment and in Revit's environment before starting Revit. In SSH mode this path belongs to the remote host. |
| `REVIT_MCP_REDACT_PATHS` | Unset | `1` replaces every response `documentPath` and nested `path` value with its file name. `--redact-paths` enables the same behavior. |

SSH mode passes `ControlMaster=auto`, `ControlPath=<dir>/mux-%C` and `ControlPersist=600` on every invocation.
The socket directory is `$XDG_RUNTIME_DIR` when nonempty, otherwise `/tmp/revit-model-mcp-<uid>/`.
The directory is created or restricted to mode `0700` on macOS and Linux.
Keep its absolute path short for Unix socket limits; `%C` hashes the connection identity.
The master connection remains available for 600 seconds after its last client disconnects.
Extra options follow OpenSSH's first-value-wins behavior.
To supply a custom multiplexing path or lifetime, set `REVIT_MCP_SSH_MUX=0` and provide all three `Control*` options through `REVIT_MCP_SSH_OPTIONS`.
Clients whose OpenSSH lacks multiplexing support, such as native Windows OpenSSH, use `REVIT_MCP_SSH_MUX=0`.

`uv run --directory server revit-model-mcp --help` prints the environment host mode, Windows channel directory and path redaction flag without contacting Revit.

The server reads activation configuration at process startup.
A configured task may restore and focus the Revit window.
Without a task the server only polls for pickup.

## Responses and privacy

Model paths occur in responder metadata and instance listings.
Redaction covers `documentPath` and all nested `path` fields in successful MCP results, including RVT/CAD/image link paths from `revit_links_status`.
It preserves exported image `localPath` values for clients that open the downloaded file.
It does not redact names, parameter values, add-in error text or files stored in the channel.
Revit model data and errors can retain their original language.
Python tool descriptions and server-generated messages are English.

## Request behavior

For HTTP setup and remote access commands, see [transport](../docs/transport.md#http-configuration).
HTTP submits once and polls by job ID within `timeout_seconds`; pickup timeout applies only to file transports.
HTTP exports download PNG directly without remote PowerShell.
Each HTTP endpoint represents one Revit process.

The default file pickup timeout is 300 seconds.
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

From the repository root:

```sh
cd server
uv run --with pytest pytest -q
```

The tests use mocked host operations and exercise MCP stdio without Revit.

See the [tool arguments](../README.md#tools), [action arguments](../README.md#actions-opt-in) and [response contract](../docs/feed-format.md#command-responses).

## License

[MIT](LICENSE), copyright (c) 2026 Dinar Sharafutdinov.
