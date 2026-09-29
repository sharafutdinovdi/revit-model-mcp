# Automatic updates

The add-in checks the latest stable release after Revit starts. The check runs in the background at most once every 24 hours. It uses the system proxy and has a 10-second request timeout. Drafts and prereleases are ignored.

For a per-user install, the add-in downloads the new SingleUser MSI and `SHA256SUMS.txt` to `%LOCALAPPDATA%\RevitModelMcp\updates\<version>`. It verifies the MSI checksum before starting the updater. The updater waits until all Revit processes in the current Windows session close, verifies the MSI again, and installs it silently without elevation. It keeps the verified MSI open until installation ends. It waits for up to seven days. If the updater stops without a result, the next Revit start retries the check and installation. The next Revit start after an installation shows the result in the activity pane, with a link to the release notes. The SingleUser MSI does not manage HTTP URL reservations; see [Windows URL reservation](transport.md#windows-url-reservation).

For a per-machine install, the activity pane shows an update notice once per version. The add-in does not download or install the MSI. An administrator must update the installation.

To disable checks, install the MSI with `UPDATECHECK=0`. A per-machine MSI writes `{"updateCheck":false}` to `%ProgramData%\RevitModelMcp\settings.json`. A per-user MSI writes the same field to `%LOCALAPPDATA%\RevitModelMcp\settings.json`. You can also set that field yourself in either file. A false machine setting takes precedence. If any setting disables checks, the add-in makes no update request. Updates do not change the read-only gate.

Revit shows its unsigned add-in security dialog again when the add-in DLL content changes or it loads from a new path. The decision is stored for that path and content only after a graceful Revit exit. Until release builds are signed, expect one security prompt at the next Revit start after each automatic update. The updater does not answer the prompt or write to the CodeSigning registry. See the [code signing policy](code-signing.md).
