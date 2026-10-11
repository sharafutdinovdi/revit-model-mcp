# Automatic updates

The add-in checks the latest stable release after Revit starts. The check runs in the background at most once every 24 hours. It uses the system proxy and has a 10-second request timeout. Drafts and prereleases are ignored.

For a per-user install, the add-in downloads the new SingleUser MSI and `SHA256SUMS.txt` to `%LOCALAPPDATA%\RevitModelMcp\updates\<version>`. It verifies the MSI checksum before starting the updater. The updater waits until all Revit processes in the current Windows session close, verifies the MSI again, and installs it silently without elevation. It keeps the verified MSI open until installation ends. It waits for up to seven days. If the updater stops without a result, the next Revit start retries the check and installation. The next Revit start after an installation shows the result in the activity pane, with a link to the release notes. The SingleUser MSI does not manage HTTP URL reservations; see [Windows URL reservation](transport.md#windows-url-reservation).

For a per-machine install, the activity pane shows an update notice once per version. The add-in does not download or install the MSI. An administrator must update the installation.

## Modes

The mode is `off`, `notify` or `auto`. Unknown values count as `off`. The first source that sets a mode wins:

1. The `store` and `managed` channels: always `off`.
2. Machine policy `UpdateMode`. It is final, but `REVIT_MCP_NO_UPDATE_CHECK=1` and `updateCheck: false` can still turn updates off.
3. The MSI property `UPDATEMODE` (`off`, `notify` or `auto`), stored as `updateMode` in `settings.json`. `UPDATECHECK=0` is an alias for `off`. Machine settings cap user settings.
4. The channel default: `notify` for `release`, `off` for `dev`.

`notify` is the default for per-user release installs. The add-in shows one activity pane notice per version with a link to the release notes. The notice also names the setting that switches to `auto`: set `"updateMode": "auto"` in `%LOCALAPPDATA%\RevitModelMcp\settings.json`. Only `auto` downloads and installs. The download and installation described above apply only to `auto` on a per-user install.

To disable checks, install the MSI with `UPDATEMODE=off` or the legacy `UPDATECHECK=0`. A per-machine MSI writes the setting to `%ProgramData%\RevitModelMcp\settings.json`. A per-user MSI writes it to `%LOCALAPPDATA%\RevitModelMcp\settings.json`. You can also set `"updateMode": "off"` or `"updateCheck": false` yourself in either file. A machine setting caps the user setting. You can also set `REVIT_MCP_NO_UPDATE_CHECK=1` in the environment of the Revit process. If any setting disables checks, the add-in makes no update request. Updates do not change the read-only gate.

Revit shows its unsigned add-in security dialog again when the add-in DLL content changes or it loads from a new path. The decision is stored for that path and content only after a graceful Revit exit. Until release builds are signed, expect one security prompt at the next Revit start after each automatic update. The updater does not answer the prompt or write to the CodeSigning registry. See the [code signing policy](code-signing.md).

## Channels

Each build carries a channel, set at build time with the MSBuild property `UpdateChannel`. The default is `release`.

| Channel | Network access for updates | Updater | Use |
| --- | --- | --- | --- |
| `release` | Yes, subject to the mode | Included | MSI, WinGet and zip downloads from GitHub. |
| `managed` | Never | Not copied | Your deployment tool delivers updates. `-p:ManagedInstall=true` is an alias. |
| `store` | Never | Not copied | Autodesk App Store builds. Updates arrive through the Autodesk App Manager. The build also ships the bundled server, which never checks for updates, and keeps C# code execution off by default (see below). |
| `dev` | Only when policy or settings turn it on, and then notify only | Included | Builds whose version contains `-dev`. They are never installed over. |

Example: `dotnet build src/RevitModelMcp.Addin -c Release.R26 -p:UpdateChannel=managed`. A `managed` or `store` build ignores every mode setting and never contacts the network for updates. An unknown channel name is treated as `managed`.

### The store channel

The release workflow builds the store variant with `-p:UpdateChannel=store` and attaches `RevitModelMcp-<version>-SingleUser-store.msi` and `revit-model-mcp-<version>-store.bundle.zip`. Neither contains `RevitModelMcp.Updater.exe`; the workflow and `build/validate_bundle.py --store` fail the release if one does. The add-in makes no update request, and no setting, policy or environment variable changes that.

The store channel also turns C# code execution (`revit_execute_code` and code steps in `revit_process_models`) off until the user sets `"executeCode": "confirm"` in `%LOCALAPPDATA%\RevitModelMcp\settings.json`, or an administrator sets the `ExecuteCode` policy to `confirm`. The `ExecuteCode=off` policy always wins. Other channels are unchanged: code execution stays available with its confirmation.

## Dev and prerelease builds

A published prerelease such as `0.9.1-rc.1` is replaced by the stable release `0.9.1` or any newer stable release. A dev build (version label `-dev`, for example `0.9.1-dev+abc1234`) is never replaced by the same or an older `X.Y.Z`; only a strictly newer stable release counts as an update. Dev builds also default to mode `off`, and even with a policy or setting they are capped at `notify`, so they never install anything.

## Enterprise notes

- Set `UpdateMode` in the machine policy (`HKLM\Software\Policies\RevitModelMcp` or `%ProgramData%\RevitModelMcp\policy.json`) to lock the mode for every user. Users cannot raise it.
- Use the `managed` channel when your deployment tool owns updates. No update request leaves the machine.
- Per-machine installs only ever notify, whatever the mode.
- To check an internal mirror instead of GitHub, set `REVIT_MCP_UPDATE_FEED` to an HTTPS base URL (loopback HTTP is allowed for tests). The mirror must serve the same `/repos/sharafutdinovdi/revit-model-mcp/releases/latest` JSON as the GitHub API.

## Download safety

- Download URLs must use HTTPS and carry no credentials. The host must be the feed host. For the default GitHub feed, `github.com`, `api.github.com`, `objects.githubusercontent.com` and `release-assets.githubusercontent.com` are accepted. The final host after redirects is checked too.
- A version whose checksum does not match, or whose installation fails, is blocked for seven days. The add-in skips it and shows a notice with a link to the release.
- `SHA256SUMS.txt` comes from the same release as the MSI, so it detects corruption but not a compromised release. Signature verification is not part of this version. It needs a signing key for the checksum file or an Authenticode certificate for the MSI; see the [code signing policy](code-signing.md).
