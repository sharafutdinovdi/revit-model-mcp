--8<-- "server/README.md"

## Updates and compatibility

The server contains no update check, package refresh process or update-state cache.
The store build includes a self-contained server executable and receives updates through the Autodesk App Store.
Other bundle launchers use `uvx` and its package cache; package downloads follow the launcher's configured version.

`revit_ping` reports `serverVersion` and `addinVersion`, not `latestKnownVersion` or `updateCheck`.
When a tool needs a newer add-in, the server returns the required and installed versions before sending the job.
Update the store build through the Autodesk App Store.
For other builds, install the current add-in from the [releases page](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest).
