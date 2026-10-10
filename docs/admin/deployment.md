# Deployment guide for administrators

This guide covers rolling out Model MCP to many Windows workstations. It has two parts: the add-in inside Revit and the Python server next to the MCP client.
For a single machine, use [requirements and installation](../install.md).

Machine policy, the policy installer properties and the policy templates are described in the [policy reference](policy-reference.md).

## Choose an install scope

| | SingleUser MSI | MultiUser MSI |
| --- | --- | --- |
| File | `RevitModelMcp-<version>-SingleUser.msi` | `RevitModelMcp-<version>-MultiUser.msi` |
| Scope | Current user | All users on the machine |
| Administrator rights | Not needed | Needed |
| Add-in folder | `%APPDATA%\Autodesk\Revit\Addins\<year>` | `%ProgramData%\Autodesk\Revit\Addins\<year>` for Revit 2022 to 2026, `%ProgramFiles%\Autodesk\Revit\Addins\2027` for Revit 2027 |
| Settings written by `UPDATECHECK=0` | `%LOCALAPPDATA%\RevitModelMcp\settings.json` | `%ProgramData%\RevitModelMcp\settings.json` |
| Automatic update | Downloads and installs the SingleUser MSI after Revit closes | Shows a notice only; an administrator updates the installation |
| Machine policy | Honored | Honored |

The two installers are alternatives. Each refuses to install while the other scope is present, so uninstall one before installing the other.
Use the MultiUser MSI for managed fleets: it needs no user rights at run time and it never self-updates.

The release builds are not Authenticode-signed yet. Revit shows its unsigned add-in prompt once per user, path and file content. See [code signing](../code-signing.md) and [updates](../updates.md).
A managed rollout therefore has to plan for that prompt, for example by signing the installed DLLs with your own organization certificate (`install.ps1 -SignThumbprint` does this for a developer install) and by using Revit's own trusted publisher mechanism.
[INFERENCE] Where Revit stores the "Always Load" decision, and any administrator pre-trust, is not verified for this project.

## Silent install

Run the installer with Revit closed. Use an elevated prompt for the MultiUser MSI.

```bat
msiexec /i RevitModelMcp-<version>-MultiUser.msi /qn /norestart /l*v "%TEMP%\rmm-install.log"
```

Switches and properties:

| Item | Meaning |
| --- | --- |
| `/qn /norestart` | No user interface, no restart. |
| `/l*v <file>` | Verbose log. Attach it to a support request. |
| `UPDATECHECK=0` | Turn off the add-in update check. Writes `{"updateCheck":false}` to the settings file of the scope. Both MSIs. |
| `HTTP_ENABLED=1` | Reserve an HTTP URL for the optional loopback listener. MultiUser MSI only. Default `0`. |
| `HTTP_URL_PREFIX=<url>` | URL to reserve, default `http://127.0.0.1:53110/`. MultiUser MSI only. |
| `UPDATEMODE=off\|notify\|auto` | Update mode. Writes `updateMode` to the settings file of the scope, so it is below a machine policy `UpdateMode`. Both MSIs; `UPDATECHECK=0` is the legacy alias for `off`. See [automatic updates](../updates.md#modes). |
| `READONLY=1` | Refuse every action tool on this machine. Writes `ReadOnly=1` (DWORD) under `HKLM\Software\Policies\RevitModelMcp`. MultiUser MSI only. |
| `EXECUTECODE=off\|confirm` | `off` refuses `revit_execute_code` and code in `revit_process_models`. Writes `ExecuteCode` to the same key. MultiUser MSI only. |

See [policy properties and Group Policy](#policy-properties-and-group-policy) for how `READONLY` and `EXECUTECODE` behave on upgrade, repair and uninstall. `ToolDeny`, `ToolAllow` and the other policy values cannot be set through the MSI: use Group Policy, Intune or `policy.json`.

The MultiUser MSI also creates `%ProgramData%\RevitModelMcp` as SYSTEM with a protected ACL (Administrators and SYSTEM full control, Users read and execute) on every install, because that folder holds `policy.json` and `settings.json`. If the folder already exists as a link or is owned by someone other than Administrators or SYSTEM, the install fails and the log says why. Remove the folder and install again. See [the policy folder](policy-reference.md#the-policy-folder).

Example for a locked-down fleet:

```bat
msiexec /i RevitModelMcp-<version>-MultiUser.msi /qn /norestart ^
  UPDATEMODE=off READONLY=1 EXECUTECODE=off /l*v "%TEMP%\rmm-install.log"
```

The properties are public and secure, so a transform or a software distribution tool can set them.

Exit codes follow Windows Installer:

| Code | Meaning |
| --- | --- |
| 0 | Success. |
| 3010 | Success, restart requested (not expected). |
| 1603 | Fatal error. Read the verbose log. |
| 1638 | A newer version is already installed. Downgrades are refused. |

Upgrades within the same scope run in place, also for the same version. Repair with `msiexec /fa <msi>`. Uninstall with `msiexec /x <msi> /qn`.
Uninstall leaves settings, logs and the channel folder; see [data retention](../privacy.md#data-retention) for the folders.

### Policy properties and Group Policy

The installer treats `HKLM\Software\Policies\RevitModelMcp` as owned by Group Policy or Intune. It follows three rules:

- It writes `ReadOnly` or `ExecuteCode` only when you set `READONLY=1` or `EXECUTECODE=off|confirm` on the command line, or when an earlier run of the installer wrote that value. `ReadOnly` is always written as `1`, so a repair can never write `0`.
- It remembers what it wrote in `HKLM\Software\RevitModelMcp\MsiPolicy`, not by reading the policy key. An upgrade, a repair (`msiexec /fa`) and Modify therefore keep the values without passing the properties again, and a value that Group Policy set is never rewritten, reset or deleted by the installer. A value on the command line always wins over the remembered one.
- Uninstall removes only the values the installer wrote. To take a value back on an upgrade, pass `READONLY=0` or `EXECUTECODE=confirm`.

If Group Policy and the installer both write the same value, Group Policy refresh restores its own value. A broken or unreadable policy is never the result of an installer run: it fails closed, see [unreadable policy](policy-reference.md#unreadable-policy-fails-closed).

## Software distribution

### SCCM or Configuration Manager

1. Create an application from the MultiUser MSI.
2. Install command: the `msiexec` line above. Uninstall command: `msiexec /x <msi> /qn /norestart`.
3. Detection rule: Windows Installer product code of the MSI, or the file `RevitModelMcp.dll` in the folder from the table above with a version equal to the package version.
4. Install behavior: system, whether or not a user is logged on. Add a requirement or a pre-install check that no `Revit.exe` is running, or accept that Revit loads the new files at its next start.
5. Return codes: treat 0 as success and 1638 as success if you deploy a version that is already newer.

### Intune Win32 app

1. Wrap the MultiUser MSI with the Win32 Content Prep Tool into an `.intunewin` file.
2. Install and uninstall commands as above. Install behavior: system.
3. Detection rule: MSI product code, or the file rule on `RevitModelMcp.dll`.
4. Return codes: add 1638 as success if you stage newer versions first.
5. Assign to a device group, not a user group, because the MSI installs for all users.

### Group Policy

Assign the MultiUser MSI as a computer software installation package in a GPO. Use a transform (`.mst`) to set the public properties.
Deliver the machine policy values with a Registry preference item, see the [policy reference](policy-reference.md).
Environment variables for the Revit process, such as `REVIT_MCP_NO_UPDATE_CHECK`, can be set by a startup script, but registry or file policy is easier to audit.

### WinGet

The package `Sharafutdinov.RevitModelMcp` is generated weekly for the latest stable release. For a managed fleet, prefer the MSI from your own software share so that you control the version.

## Server rollout

The server is a Python package started by the MCP client through uv. The client machine needs Python 3.11 or newer, uv, and a way to fetch the package.

For a controlled fleet, pin the version and turn off the daily check:

```sh
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_NO_UPDATE_CHECK=1 -- uvx revit-model-mcp==<version>
```

Use the same `==<version>` pin in a JSON client configuration. The managed add-in bundle `revit-model-mcp-<version>-managed.bundle.zip` is built to never check for updates; see [managed installs](../install.md#managed-installs).

### Offline or air-gapped install

The server does not need internet access once its packages are on the machine. Build a wheelhouse on a connected machine with the same Python version and platform, copy it to the offline site, and install from it:

```sh
# connected machine
python -m pip download revit-model-mcp==<version> -d wheelhouse

# offline machine
uv tool install --offline --no-index --find-links wheelhouse revit-model-mcp==<version>
```

Then register the installed command instead of `uvx`:

```sh
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_NO_UPDATE_CHECK=1 -- revit-model-mcp
```

The repository also keeps a hash-pinned dependency list for the container image, `server/requirements.docker.txt`. You can use it as the seed for `pip download --require-hashes`.
[INFERENCE] The exact uv flags above follow the uv documentation and have not been exercised on an air-gapped machine for this project. Test them in your environment first. The add-in needs no wheelhouse.
To check an internal release mirror instead of GitHub, set `REVIT_MCP_UPDATE_FEED`; see [updates](../updates.md#channels).

### Proxy and custom certificates

| Component | Proxy behavior |
| --- | --- |
| Add-in update check | Uses the system proxy and the default Windows credentials. |
| Python server | Standard library proxy variables: `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`. |
| uv package download | Follows uv settings. Typical variables are `HTTPS_PROXY`, `SSL_CERT_FILE`, `UV_NATIVE_TLS` and `UV_DEFAULT_INDEX` (see the uv documentation). |

If you set `HTTP_PROXY` or `HTTPS_PROXY` for the server, also set `NO_PROXY=127.0.0.1,localhost` so that the loopback HTTP workstation host is not sent to the proxy. [INFERENCE] This follows Python's default proxy handling and is not covered by a project test.
The complete list of outbound hosts is in the [security whitepaper](security-whitepaper.md#outbound-network).

## Multi-user and VDI hosts

- The named pipe `\\.\pipe\RevitModelMcp.<pid>` is owned by the Windows user who runs Revit. A second user cannot open it.
- Settings, the HTTP token and the channel folder live under `%LOCALAPPDATA%\RevitModelMcp` of each user. On non-persistent desktops they disappear with the profile unless the profile is kept.
- The default HTTP port 53110 collides when several sessions run Revit on one host. Leave HTTP off, or give each session its own `REVIT_MCP_HTTP_PORT`.
- Logs are written under `Documents\RevitModelMcp\Logs`. If your Documents folder is redirected to a cloud-synced location, logs with model names and paths sync with it. Redirect or exclude the folder, and see the `LogDir` policy value.

## Verify the rollout

On a pilot machine, start Revit, open a model, and ask the client to call `revit_ping`. A `success: true` result means the add-in and the server are connected.
Then check the effective settings: with the machine policy present, an action tool that policy blocks must return a result that starts with `Blocked by organization policy:`.
