# Administrator guide

This page is for IT and security teams that deploy Model MCP on managed workstations.
It covers the machine policy, the per-machine installer properties and the policy templates.

## Machine policy

Machine policy is a set of settings that only an administrator can change.
The add-in reads them in the Revit process, and the Python server reads the same values when it runs on the workstation.
A policy value always wins over a user setting.

Two sources exist.
The registry key `HKLM\Software\Policies\RevitModelMcp` wins over the file `%ProgramData%\RevitModelMcp\policy.json`.
The add-in rereads both about every ten seconds, so a change applies without restarting Revit, except where the table says otherwise.
An unreadable or malformed source counts as not set.

| Value | Registry type | Meaning |
|---|---|---|
| `ReadOnly` | DWORD 0 or 1 | `1` refuses every action tool. The user's `read-only` file cannot lift it. |
| `ToolDeny` | REG_MULTI_SZ | Tool names to refuse, for example `revit_delete` or `execute-code`. |
| `ToolAllow` | REG_MULTI_SZ | When not empty, only these tools run. `ToolDeny` wins over `ToolAllow`. |
| `ExecuteCode` | REG_SZ | `off` refuses `revit_execute_code` and code steps in `revit_process_models`. `confirm` keeps the normal confirmation. |
| `UpdateMode` | REG_SZ | `off`, `notify` or `auto`. An unknown value is treated as `off`. |
| `TrustedNetworkRoots` | REG_MULTI_SZ | UNC share roots such as `\\server\share`. A configured list replaces the per-user list, including an empty list. Applies at Revit start. |
| `LogDir` | REG_SZ | Folder for the add-in log and `activity.log`. |
| `LogRetentionDays` | DWORD 1 to 3650 | Add-in log files older than this are deleted at start and on rotation. |
| `RedactPaths` | DWORD 0 or 1 | Forces path redaction on or off for the Revit process and its child processes. Applies at Revit start. |
| `HttpEnabled` | DWORD 0 or 1 | Overrides the user's `httpEnabled` setting. Applies at Revit start. |

Tool names are not case sensitive.
`revit_delete`, `delete` and `Revit-Delete` mean the same tool.
`ping` and `jobs` are never blocked, so a client can always read the effective policy.

`policy.json` uses the same names:

```json
{
  "ReadOnly": true,
  "ToolDeny": ["revit_delete", "execute-code"],
  "ExecuteCode": "off",
  "UpdateMode": "notify",
  "TrustedNetworkRoots": ["\\\\fileserver\\bim"],
  "LogRetentionDays": 30
}
```

Give `%ProgramData%\RevitModelMcp\policy.json` an ACL where only administrators can write.
Prefer the registry or Group Policy where you can, because the registry key under `Policies` is writable only by administrators by default.

### What a blocked call looks like

A refused call returns `success:false` and an error that starts with `Blocked by organization policy:`.
The server adds `errorCode:"policy_blocked"`.
`ReadOnly` uses the same text and keeps `errorCode:"read_only"`.
A blocked call never reaches Revit's API; it is refused before the command runs, also inside `revit_batch` steps.

### Reading the effective policy

`revit_ping` returns a `policy` object with the effective values.
The pipe handshake behind `revit_list_instances` and the HTTP `/health` response carry the same object.
Values are strings. Lists are comma-separated, and network roots appear only as a count.
The object contains no paths and no secrets.

## Group Policy templates

`build/policy/RevitModelMcp.admx` and `build/policy/en-US/RevitModelMcp.adml` define every value above.
Copy them to `%SystemRoot%\PolicyDefinitions` (or your central store).
The policies appear under Computer Configuration, Administrative Templates, Model MCP for Revit.

## Per-machine installer properties

The MultiUser MSI accepts these public properties:

| Property | Effect |
|---|---|
| `READONLY=1` | Writes `ReadOnly=1` under `HKLM\Software\Policies\RevitModelMcp`. |
| `EXECUTECODE=off` | Writes `ExecuteCode=off` (or `confirm`) to the same key. |
| `UPDATECHECK=0` | Turns update checks off. |
| `HTTP_ENABLED=1` | Enables the HTTP listener and registers its URL ACL. |

```powershell
msiexec /i RevitModelMcp-0.9.2-MultiUser.msi /qn /norestart READONLY=1 EXECUTECODE=off
```

Pass the properties again on every upgrade.
An install without a property leaves the matching registry value out, and an uninstall removes the values the MSI wrote.
For fleets, set the registry values with Group Policy or Intune instead, so they do not depend on the installer.

## Logging

The add-in log is in `%USERPROFILE%\Documents\RevitModelMcp\Logs` and `activity.log` is in `%LOCALAPPDATA%\RevitModelMcp`.
Documents is often redirected to OneDrive. Set `LogDir` to a local folder if logs must not sync.
`RedactPaths=1` removes model paths from responses, logs and the stored code files.
