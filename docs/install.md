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

If your organization delivers add-in updates itself, build the add-in with `ManagedInstall=true` to turn off its own update check; see [automatic updates](updates.md#managed-installs-and-mirrors). The server still looks for a newer package on PyPI once a day; set `REVIT_MCP_NO_UPDATE_CHECK=1` in the client's server settings to turn that off too.

## Install from the MSI or WinGet

The installers published on the [releases page](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest) install the same add-in with their own update path.
`RevitModelMcp-<version>-SingleUser.msi` installs for the current user, and `RevitModelMcp-<version>-MultiUser.msi` installs for all users. Install only one.
Both support automatic updates described in [automatic updates](updates.md).
WinGet manifests for the package `Sharafutdinov.RevitModelMcp` are generated weekly for the latest stable release; see [CONTRIBUTING](contributing.md) for the status of the submission.
The README has the full steps for the [add-in](https://github.com/sharafutdinovdi/revit-model-mcp#on-the-revit-workstation), the [MCP client](https://github.com/sharafutdinovdi/revit-model-mcp#on-the-machine-with-the-mcp-client) and the Claude Desktop bundle.

## Uninstall

Close Revit, then remove the add-in from **Apps > Installed apps** (or delete the bundle folder). Remove the server by deleting its entry from your MCP client. Settings and logs remain until you delete them; the [privacy policy](privacy.md#data-retention) lists the folders.

## Get help

Report problems in [GitHub issues](https://github.com/sharafutdinovdi/revit-model-mcp/issues/new/choose) and ask usage questions in [Discussions](https://github.com/sharafutdinovdi/revit-model-mcp/discussions). Remove model names and paths from logs and screenshots first.
