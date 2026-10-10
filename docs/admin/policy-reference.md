# Policy reference

This page lists every setting that controls Model MCP, where it lives, and which one wins when two disagree.

Machine policy settings are listed first. Settings that a user can change are listed under [user settings](#user-settings-and-environment).

## Machine policy

A machine policy is set by an administrator and cannot be changed by a standard user. It is read by the add-in inside Revit. Two stores exist:

| Store | Location | Notes |
| --- | --- | --- |
| Registry | `HKLM\Software\Policies\RevitModelMcp` | Preferred. Works with Group Policy and Intune. |
| File | `%ProgramData%\RevitModelMcp\policy.json` | Give Administrators write access and Users read access only. |

If both exist, the registry wins for every value it sets. Values not set in the registry are taken from the file. An unreadable or malformed source counts as not set.
The add-in rereads both stores about every ten seconds, so a change applies without restarting Revit. `TrustedNetworkRoots`, `RedactPaths` and `HttpEnabled` apply at the next Revit start.

| Value | Registry type | `policy.json` type | Meaning |
| --- | --- | --- | --- |
| `ReadOnly` | DWORD 0 or 1 | boolean | `1` refuses every action tool. Read tools are not affected. |
| `ToolDeny` | REG_MULTI_SZ | array of strings | Tool names that are refused. |
| `ToolAllow` | REG_MULTI_SZ | array of strings | Tool names that are allowed. Empty or absent means all tools. |
| `ExecuteCode` | REG_SZ `off` or `confirm` | string | `off` refuses `revit_execute_code` and code in `revit_process_models`. `confirm` keeps the confirmation token flow. |
| `UpdateMode` | REG_SZ `off`, `notify` or `auto` | string | `off` makes no update request. `notify` only shows a notice. `auto` installs a per-user update. An unknown value counts as `off`. See [automatic updates](../updates.md#modes). |
| `TrustedNetworkRoots` | REG_MULTI_SZ | array of strings | UNC roots (`\\server\share`) that export, save and open may use. A configured list replaces the per-user list, including an empty list. |
| `LogDir` | REG_SZ | string | Folder for the add-in log and `activity.log`. |
| `LogRetentionDays` | DWORD 1 to 3650 | number | Add-in log files older than this are deleted at start and on rotation. |
| `RedactPaths` | DWORD 0 or 1 | boolean | `1` redacts paths in add-in logs and audit files. |
| `HttpEnabled` | DWORD 0 or 1 | boolean | `0` switches the HTTP listener off and the user cannot switch it on. |

Rules:

- Names are tool names such as `revit_delete` or command names such as `execute-code`. They are not case sensitive: `revit_delete`, `delete` and `Revit-Delete` mean the same tool. The full list is in the [tool manifest](../tool-manifest.md) and each tool is marked `read_only` or `modifying`.
- A tool in `ToolDeny` is refused even when it is in `ToolAllow`.
- When `ToolAllow` is not empty, every tool that is not listed is refused.
- `revit_ping` and `revit_jobs` are never blocked, so a client can always read the effective policy.
- `revit_tag_elements` and `revit_create_schedule` are modifying tools: `ReadOnly`, `ToolDeny` and `ToolAllow` apply to them like to every other action tool.
- A refusal is a normal tool result with `success: false`. Its message starts with `Blocked by organization policy:`. The server adds `errorCode: "policy_blocked"`; `ReadOnly` keeps `errorCode: "read_only"`. A blocked call is refused before the command runs, also inside `revit_batch` steps.
- Policy applies to the pipe, the file channel and the HTTP listener alike, because it is checked in the add-in.
- Read tools that are not denied keep working in read-only mode.

### Example: `policy.json`

```json
{
  "ReadOnly": false,
  "ToolDeny": ["revit_execute_code", "revit_delete", "revit_sync_document"],
  "ExecuteCode": "off",
  "UpdateMode": "notify",
  "TrustedNetworkRoots": ["\\\\fileserver\\bim"],
  "LogRetentionDays": 30,
  "RedactPaths": true,
  "HttpEnabled": false
}
```

### Example: registry import

```reg
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\Software\Policies\RevitModelMcp]
"ReadOnly"=dword:00000001
"ExecuteCode"="off"
"UpdateMode"="off"
"HttpEnabled"=dword:00000000
"ToolDeny"=hex(7):72,00,65,00,76,00,69,00,74,00,5f,00,64,00,65,00,6c,00,65,00,74,00,65,00,00,00,00,00
```

The MultiUser MSI properties `READONLY` and `EXECUTECODE` write the matching registry values; see the [deployment guide](deployment.md#silent-install). `ToolDeny` and `ToolAllow` cannot be set through the MSI. The MSI property `UPDATEMODE` does not write the policy store: it writes `updateMode` to `settings.json`, below the machine policy `UpdateMode` in precedence.

### Reading the effective policy

`revit_ping` returns a `policy` object with the effective values. The pipe handshake behind `revit_list_instances` and the HTTP `/health` response carry the same object. Values are strings, lists are comma-separated, and network roots appear only as a count. The object contains no paths and no secrets.

### ADMX templates

`build/policy/RevitModelMcp.admx` and `build/policy/en-US/RevitModelMcp.adml` in the source repository define every value above. They are not installed by the MSI. Copy them to `%SystemRoot%\PolicyDefinitions` (or your central store); the policies appear under Computer Configuration, Administrative Templates, Model MCP for Revit. Without the templates, deploy the registry values with a Group Policy Registry preference item or an Intune custom OMA-URI setting that targets `HKLM\Software\Policies\RevitModelMcp`.

### Logging and Documents redirection

The add-in log is in `%USERPROFILE%\Documents\RevitModelMcp\Logs` and `activity.log` is in `%LOCALAPPDATA%\RevitModelMcp`. Documents is often redirected to OneDrive: set `LogDir` to a local folder if logs must not sync. `RedactPaths=1` removes model paths from responses, logs and the stored code files.

## Precedence

For each setting, the first source that has a value wins.

1. Machine policy, registry first, then `policy.json`.
2. Add-in environment variable in the Revit process.
3. User settings file `%LOCALAPPDATA%\RevitModelMcp\settings.json`.
4. Built-in default.

Two exceptions keep the safer value:

- Read-only is on if the machine policy, the workstation file `%LOCALAPPDATA%\RevitModelMcp\read-only` or the server variable `REVIT_MCP_READ_ONLY=1` says so. No lower layer can turn policy read-only off.
- An update check is off if any layer turns it off (`UpdateMode=off`, `updateCheck: false`, `REVIT_MCP_NO_UPDATE_CHECK=1`). Per-machine installs and `dev` builds are capped at `notify`, and the `store` and `managed` channels never check. See [automatic updates](../updates.md#modes).

## User settings and environment

These exist without a machine policy. They are per user or per process, so a user can change them.

### Settings file

`%LOCALAPPDATA%\RevitModelMcp\settings.json` is created at the first Revit start, also when HTTP is off. A per-machine install can also write `%ProgramData%\RevitModelMcp\settings.json` for `updateCheck`; a false value there wins.

| Key | Default | Meaning |
| --- | --- | --- |
| `httpEnabled` | `false` | Start the HTTP listener. |
| `httpBind` | `127.0.0.1` | Address to bind. |
| `httpPort` | `53110` | Port to bind. |
| `token` | generated | 32-byte bearer token, created at first run. |
| `showActivityPaneOnAction` | `true` | Show the activity pane when an action runs. |
| `trustedNetworkRoots` | empty | UNC roots allowed for export, save and open. Restart Revit after a change. |
| `updateCheck` | `true` | Check for add-in updates. |
| `maxStoredResponses`, `maxStoredBytes` | built in | Limits for stored HTTP job responses. |
| `requestReadTimeoutSeconds`, `shutdownDrainSeconds` | built in | HTTP request and shutdown timing. |

### Add-in environment (Revit process)

| Variable | Meaning |
| --- | --- |
| `REVIT_MCP_HTTP_ENABLED`, `REVIT_MCP_HTTP_BIND`, `REVIT_MCP_HTTP_PORT` | Override the HTTP settings. |
| `REVIT_MCP_TOKEN` | Override the HTTP token. |
| `REVIT_MCP_CHANNEL_DIR` | Move the file channel folder. |
| `REVIT_MCP_REDACT_PATHS` | Redact paths in add-in logs and audit files. Set it in the Revit environment: setting it only in the MCP client affects the Python responses, not these files. |
| `REVIT_MCP_NO_UPDATE_CHECK` | `1` turns the add-in update check off. |
| `REVIT_MCP_UPDATE_FEED` | HTTPS base URL of an internal release feed. |
| `REVIT_MCP_RSN_REST_BASE` | Revit Server REST base for multi-model runs. |
| `REVIT_MCP_REVIT_EXE_<year>` | Path to `Revit.exe` for multi-model runs. |

### Server environment (MCP client)

| Variable | Meaning |
| --- | --- |
| `REVIT_MCP_HOST` | `local`, an `http(s)://` workstation or an SSH alias. |
| `REVIT_MCP_READ_ONLY` | `1` refuses actions in this server process only. |
| `REVIT_MCP_REDACT_PATHS` | Redact paths in outgoing responses. |
| `REVIT_MCP_TOKEN` | Bearer token for an HTTP workstation. |
| `REVIT_MCP_CHANNEL_DIR` | Channel folder on the workstation. |
| `REVIT_MCP_NO_UPDATE_CHECK` | `1` turns the daily PyPI check off. |
| `REVIT_MCP_TOOL_BUDGET_S` | Time budget per tool call in seconds. |
| `REVIT_MCP_ACTIVATE_TASK` | Windows task used to bring the Revit window forward. |
| `REVIT_MCP_SSH_MUX`, `REVIT_MCP_SSH_OPTIONS` | SSH connection sharing and extra options. |

Boolean variables accept `1/0`, `true/false`, `yes/no` and `on/off`.
