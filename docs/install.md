# Requirements and installation

Revit Model MCP has two parts. The add-in runs inside Revit on Windows. The server runs next to your MCP client and talks to the add-in. You need both.

## Requirements

| Part | Requirement |
| --- | --- |
| Revit | Revit 2022, 2023, 2024, 2025, 2026 or 2027 on Windows 10 or 11. |
| Add-in | Installed on the Windows machine where Revit runs. No administrator rights are needed for the per-user installation. |
| Python and uv | Python 3.11 or newer, started through [uv](https://docs.astral.sh/uv/getting-started/installation/). uv downloads the server package and, if needed, a matching Python. |
| MCP client | An MCP client such as Claude Desktop or Claude Code. It can run on the same Windows machine, or on macOS or Linux with a [remote workstation](transport.md). |
| Network | Local use needs no open ports. Update checks and the first server download need internet access; see the [privacy policy](privacy.md). |

The add-in changes nothing in a model on its own. The MCP client asks for data or actions through the server. Actions are enabled by default and every one is a named undo entry. Switch to read-only mode if you only want to inspect models; see [actions](actions.md).

## Install the add-in bundle

1. Get the add-in package from your BIM manager or IT team, or build it from the repository. It is an Autodesk `.bundle` folder with a `PackageContents.xml` file.
2. Close Revit and copy the folder to `%APPDATA%\Autodesk\ApplicationPlugins` (current user) or `%ProgramData%\Autodesk\ApplicationPlugins` (all users).
3. Start Revit and open a model. The first time, Revit asks whether to load the add-in. Choose **Always Load** after you check the publisher and path. Release builds are not code-signed yet; see [code signing](code-signing.md).
4. Find the **MCP** panel on the **Add-Ins** tab. Its **Activity** button opens the activity pane, which lists every request and action.
5. Install uv on the machine with your MCP client, following the [uv installation guide](https://docs.astral.sh/uv/getting-started/installation/).
6. Register the server in your MCP client.

   For Claude Code on the same Windows machine:

   ```sh
   claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- uvx revit-model-mcp
   ```

   For Claude Desktop, add this to its MCP configuration file, or use the `.mcpb` bundle from the [latest release](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest):

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

7. Restart the client. With a model open in Revit, ask it to call `revit_ping`. A reply with `success: true` means both parts are connected.

### The MCP ribbon panel

The **MCP** panel on the **Add-Ins** tab has one button, **Activity**. It shows or hides the activity pane. Press F1 while the pointer is on the button to open this page.

### Managed installs

Managed installs do not check for updates. If your organization delivers add-in updates itself, use `revit-model-mcp-<version>-managed.bundle.zip` from the [latest release](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest). It is the same bundle, built with `ManagedInstall=true`; see [automatic updates](updates.md#managed-installs-and-mirrors). You can also build it yourself with that property.

The bundle does not contain the server. Pin the server launcher to the exact release version and turn off its daily check for a newer package on PyPI:

```sh
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_NO_UPDATE_CHECK=1 -- uvx revit-model-mcp==<version>
```

In a JSON configuration, use `"args": ["revit-model-mcp==<version>"]` and set `REVIT_MCP_NO_UPDATE_CHECK` to `1` in `env`. A `README.txt` in the bundle repeats these commands for its version. With both settings, neither part makes an update request.

## Install from the MSI or WinGet

The installers published on the [releases page](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest) install the same add-in with their own update path.
`RevitModelMcp-<version>-SingleUser.msi` installs for the current user, and `RevitModelMcp-<version>-MultiUser.msi` installs for all users.
The two installers are alternatives: install only one.
Each refuses to install while the other is present, so uninstall the other first (**Apps > Installed apps**), then install.
Upgrade within the same scope works in place.
Run the installer with Revit closed, then start Revit and open a model.
The installer replaces the add-in files and the `RevitModelMcp.addin` manifest it ships, even when an older developer build left a higher file version or a manifest with absolute paths. It leaves other add-ins in the same folder alone.
Both support automatic updates; see [automatic updates](updates.md) for behavior and opt-out settings.
From a clone, run `.\install.ps1 -Source Release` in PowerShell instead.
WinGet manifests for the package `Sharafutdinov.RevitModelMcp` are generated weekly for the latest stable release; see [CONTRIBUTING](contributing.md) for how they are published.

Then register the server as in step 6 of [Install the add-in bundle](#install-the-add-in-bundle), or use the Claude Desktop bundle below.

## Claude Desktop bundle

Install [uv](https://docs.astral.sh/uv/getting-started/installation/) on the client's PATH, download `revit-model-mcp-<version>.mcpb` from the [latest release](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest), and open it in Claude Desktop.
The bundle starts from the local uv cache. The server checks for a newer stable release in the background once per day.
The next client start uses the refreshed version. Set `REVIT_MCP_NO_UPDATE_CHECK=1` to opt out.
The settings form configures the workstation host, path redaction, read-only mode and the HTTP bearer token without editing JSON.
Use `local` on the Windows Revit workstation, or configure a [remote workstation](transport.md) for macOS and Linux clients.
Path redaction starts enabled and actions start enabled. Check "Read-only mode" to refuse actions.
The Windows workstation still needs the add-in.
See the [bundle guide](https://github.com/sharafutdinovdi/revit-model-mcp/blob/main/bundle/README.md) for build details and prerequisites.
Release assets include GitHub build provenance attestations. Follow [download verification](security.md#verify-downloads) before installing.

## Uninstall

Close Revit, then remove the add-in from **Apps > Installed apps** (or delete the bundle folder). Remove the server by deleting its entry from your MCP client. Settings and logs remain until you delete them; the [privacy policy](privacy.md#data-retention) lists the folders.

## Get help

Report problems in [GitHub issues](https://github.com/sharafutdinovdi/revit-model-mcp/issues/new/choose) and ask usage questions in [Discussions](https://github.com/sharafutdinovdi/revit-model-mcp/discussions). Remove model names and paths from logs and screenshots first.
