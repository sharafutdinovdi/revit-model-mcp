--8<-- "server/README.md"

## Automatic updates and compatibility

The bundle starts `uvx` offline from its cache. An empty cache falls back to an online start.
Once per 24 hours, the running server refreshes the cached stable package in a detached process.
The next client start uses the refreshed version. Startup does not wait for the check.
Set `REVIT_MCP_NO_UPDATE_CHECK=1` to disable both the refresh and the PyPI version lookup.
The check state and latest known stable version live in the user cache directory under `revit-model-mcp/update.json`.

`revit_ping` reports `serverVersion`, `addinVersion`, `latestKnownVersion` and `updateCheck`.
`updateCheck` is `disabled` or the last check time in UTC.
`latestKnownVersion` is never older than the running server version.
When a tool needs a newer add-in, the server returns the required and installed versions before sending the job.
Install the current add-in from the [releases page](https://github.com/sharafutdinovdi/revit-model-mcp/releases/latest).
