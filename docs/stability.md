# Stability of 1.x

From 1.0.0 I follow [semantic versioning](https://semver.org/) for the surface below.
A breaking change to anything in the first list needs a major release.

## Covered

- MCP tool names, and the names, types, defaults and required status of their input parameters.
- The meaning of documented parameters and of documented enum values, for inputs and outputs.
- Documented response fields: names, types and units. `success`, `error`, `partial`, `status`, `jobId`, `needsConfirmation`, `confirmationText`, `confirmToken`, `skipped` and `skippedCount` are covered. The literal `error` value `read-only mode` is covered.
- The server command line (`--host`, `--redact-paths`, `--token`) and the `REVIT_MCP_*` environment variables listed in the [server documentation](server.md#configuration).
- The keys, types and meaning of `settings.json`, described in [HTTP configuration](transport.md#http-configuration), and the `read-only` gate file.
- The `pipe/1` protocol, file channel version 2, the HTTP routes, headers and status codes, and the snapshot `schemaVersion` 1. See [transport](transport.md).
- The names of the PyPI package, the console script, the MCP Registry entry and the bundle.
- The `ScriptContext` members available to `revit_execute_code`: `UiApplication`, `Application`, `UiDocument`, `Document`, `Log`, `ToMm` and `FromMm`.

## Allowed in minor releases

- New tools, new optional parameters whose default keeps the old behavior, new response fields, new settings keys, new error codes, and new values of output enums. Clients must ignore unknown fields and unknown enum values.
- Tighter validation that rejects input that never worked.

## Not covered

- The wording of error messages, tool descriptions, titles and prompts.
- The order of fields and of list items unless the documentation states an order.
- Python module internals (`revit_model_mcp.*` imports) and the .NET assemblies and namespaces of the add-in.
- Channel commands that no tool exposes, and anything under the channel directory except the documented heartbeat, job and response files.
- Log files, the activity pane, and the layout of generated Excel workbooks. Sheet and column names can change in a minor release.
- Numeric results that depend on the Revit version and the model language.

## Compatibility

- The server and add-in of the same major version work together. An older add-in gets a clear error that names the version a tool needs.
- Supported Revit years can be added in a minor release. I announce a year's removal one minor release ahead.
- Security fixes can break the rules above. The changelog says so.

## How it is enforced

A test snapshots the schemas of every registered tool: names, parameters, types, defaults and required status.
A change to a schema shows up as a diff in that snapshot, so a breaking change cannot slip into a minor release unnoticed.
Documented response fields and enum values are checked in review against this page.
