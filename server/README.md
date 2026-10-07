# Revit Model MCP server

The Python package exposes Revit tools over MCP stdio; actions run by default.
Set `REVIT_MCP_READ_ONLY=1` or create the workstation `read-only` file to disable them without hiding the action tools.
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

Set server variables in the MCP client's `env` block. Set add-in variables in the environment of the Revit process before it starts, and restart Revit after a change.
Boolean server variables accept `1`, `true`, `yes`, `on` and `0`, `false`, `no`, `off`; any other value is an error. Add-in variables that say `1` accept only `1`.

| Variable | Read by | Default | Behavior |
|---|---|---|---|
| `REVIT_MCP_HOST` | Server | `local` | Local PowerShell or pipe, `ssh:<alias>` or an `http://` / `https://` add-in endpoint. `--host` overrides it. |
| `REVIT_MCP_READ_ONLY` | Server | Off | `1` refuses action calls, including `revit_batch`, `revit_export_nwc`, `revit_edit_families`, `revit_align_link_datums` and `revit_undo_last`, with `read-only mode` instead of running them; the tools stay listed. The workstation `read-only` file is checked independently. |
| `REVIT_MCP_REDACT_PATHS` | Server and add-in | Off | `1` removes directories from response `documentPath`, `path` and `centralPath` fields and Windows paths in message fields. `--redact-paths` enables the same behavior in the server. In the add-in it also redacts paths in the activity pane, stored code and logs. |
| `REVIT_MCP_TOKEN` | Server and add-in | Unset | HTTP bearer token. The server sends it; `--token` overrides it. In Revit it overrides the `token` in `settings.json`. |
| `REVIT_MCP_CHANNEL_DIR` | Server and add-in | `%LOCALAPPDATA%\RevitModelMcp` on Windows | Absolute Windows channel path. Set the same value in the server environment and in Revit's environment before starting Revit. In SSH mode this path belongs to the remote host. `settings.json` and the `read-only` file stay in the default directory. |
| `REVIT_MCP_SSH_MUX` | Server | Enabled | `0` disables OpenSSH connection multiplexing. Local mode ignores SSH settings. |
| `REVIT_MCP_SSH_OPTIONS` | Server | Unset | Extra SSH arguments, parsed with shell quoting and appended after built-in options, before the host. Example: `-o ServerAliveInterval=30 -p 2222`. |
| `REVIT_MCP_ACTIVATE_TASK` | Server | Unset | Optional existing Windows scheduled task. Runs once after 60 seconds if the trigger remains pending. The task must activate the interactive Revit window. No task is created by the server. |
| `REVIT_MCP_TOOL_BUDGET_S` | Server | `40` | Seconds the first response of a long action waits before it returns `status:"running"`. Accepts 10 through 200. |
| `REVIT_MCP_NO_UPDATE_CHECK` | Server | Off | `1` disables the background package refresh and the PyPI version lookup. |
| `REVIT_MCP_HTTP_ENABLED` | Add-in | `httpEnabled` from `settings.json` | `0` or `1` turns the HTTP listener off or on. Other values disable HTTP. |
| `REVIT_MCP_HTTP_BIND` | Add-in | `httpBind` from `settings.json` | IPv4 interface address for the listener. |
| `REVIT_MCP_HTTP_PORT` | Add-in | `httpPort` from `settings.json` | Listener port, 1-65535. Give each HTTP-enabled Revit process its own port. |
| `REVIT_MCP_UPDATE_FEED` | Add-in | GitHub API | Release feed base URL for update checks. Must be HTTPS, or HTTP on loopback. |
| `REVIT_MCP_REVIT_EXE_<year>` | Add-in | `Program Files\Autodesk\Revit <year>\Revit.exe` | Path to `Revit.exe` for the batch collector, for each year from 2022 to 2027. |
| `REVIT_MCP_RSN_REST_BASE` | Add-in (batch supervisor) | Unset | `AdminRESTService.svc/` URL of the Revit Server host. Required to read RSN model metadata before a batch run. |

The add-in also reads `REVIT_MCP_BATCH_WORKER`; the batch supervisor sets it on worker processes, so do not set it yourself.
See the [settings reference](../docs/transport.md#settings-reference) for the `settings.json` keys.

`revit_family_audit` is a read tool. It inspects an open family when `families` is omitted, or exact family names / `["*"]` in a project. `revit_edit_families` applies ordered shared-parameter, removal, purge and shared-flag operations. Project edits use one family load per family and one undo entry; `dry_run=true` rolls back. The edit tool is refused in read-only mode. Family audit defaults to a 600-second response budget; family edits default to 1800 seconds.

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
Redaction covers `documentPath`, `path` and `centralPath` fields in MCP results, including RVT/CAD/image link paths from `revit_links_status`.
It also reduces Windows drive and UNC paths in `confirmationText`, `summary`, `error`, `message`, `warning` and `warnings` strings at any depth to file names, including tool errors.
It preserves exported image `localPath` values for clients that open the downloaded file.
Names, parameter values and remaining message text stay visible. Redaction applies to outgoing Python responses, not files stored in the channel or add-in logs.
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
