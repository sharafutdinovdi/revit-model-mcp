# Policy reference

This page lists every setting that controls Model MCP, where it lives, and which one wins when two disagree.

<!-- depends: night/enterprise -->
The machine policy layer below is added with the enterprise policy change. The keys and the refusal text follow one fixed contract. Settings that exist today are listed separately under [user settings](#user-settings-and-environment).

## Machine policy

A machine policy is set by an administrator and cannot be changed by a standard user. It is read by the add-in inside Revit. Two stores exist:

| Store | Location | Notes |
| --- | --- | --- |
| Registry | `HKLM\Software\Policies\RevitModelMcp` | Preferred. Works with Group Policy and Intune. |
| File | `%ProgramData%\RevitModelMcp\policy.json` | Give Administrators write access and Users read access only. |

If both exist, the registry wins for every value it sets. Values not set in the registry are taken from the file.

| Value | Registry type | `policy.json` type | Meaning |
| --- | --- | --- | --- |
| `ReadOnly` | DWORD 0 or 1 | boolean | `1` refuses every action tool. Read tools are not affected. |
| `ToolDeny` | REG_MULTI_SZ | array of strings | Tool names that are refused. |
| `ToolAllow` | REG_MULTI_SZ | array of strings | Tool names that are allowed. Empty or absent means all tools. |
| `ExecuteCode` | REG_SZ `off` or `confirm` | string | `off` refuses `revit_execute_code` and code in `revit_process_models`. `confirm` keeps the confirmation token flow. |
| `UpdateMode` | REG_SZ `off`, `notify` or `auto` | string | `off` makes no update request. `notify` only shows a notice. `auto` installs a per-user update. |
| `TrustedNetworkRoots` | REG_MULTI_SZ | array of strings | UNC roots (`\\server\share`) that export, save and open may use. |
| `LogDir` | REG_SZ | string | Folder for add-in logs. |
| `LogRetentionDays` | DWORD | number | Age limit for log files in days. |
| `RedactPaths` | DWORD 0 or 1 | boolean | `1` redacts paths in add-in logs and audit files. |
| `HttpEnabled` | DWORD 0 or 1 | boolean | `0` switches the HTTP listener off and the user cannot switch it on. |

Rules:

- Names are tool names such as `revit_delete`; the full list is in the [tool manifest](../tool-manifest.md) and each tool is marked `read_only` or `modifying`.
- A tool in `ToolDeny` is refused even when it is in `ToolAllow`.
- When `ToolAllow` is not empty, every tool that is not listed is refused.
- A refusal is a normal tool result with `success: false`. Its message starts with `Blocked by organization policy:`.
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

The MSI public properties `READONLY`, `EXECUTECODE` and `UPDATEMODE` write the matching values; see the [deployment guide](deployment.md#silent-install).

### ADMX templates

Group Policy administrative templates for these values are not part of the current release. Until they ship, deploy the registry values with a Group Policy Registry preference item or an Intune custom OMA-URI setting that targets `HKLM\Software\Policies\RevitModelMcp`.

## Precedence

For each setting, the first source that has a value wins.

1. Machine policy, registry first, then `policy.json`.
2. Add-in environment variable in the Revit process.
3. User settings file `%LOCALAPPDATA%\RevitModelMcp\settings.json`.
4. Built-in default.

Two exceptions keep the safer value:

- Read-only is on if the machine policy, the workstation file `%LOCALAPPDATA%\RevitModelMcp\read-only` or the server variable `REVIT_MCP_READ_ONLY=1` says so. No lower layer can turn policy read-only off.
- An update check is off if any layer turns it off. No lower layer can raise `notify` to `auto`.

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
