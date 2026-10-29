# Tool manifest

[`tool-manifest.json`](tool-manifest.json) lists every tool, resource and prompt of the server with a declaration of what each tool can do.
It helps reviewers and administrators check access before they approve the server.
Do not edit it by hand: a generator writes it from the live tool registry, the same source as `bundle/manifest.json`.

```sh
cd server && uv run python ../build/tool_manifest.py
cd server && uv run python ../build/tool_manifest.py --check
```

`--check` fails when the committed file is stale.

`--managed` writes the manifest for the managed install variant to `output/tool-manifest-managed.json`, or to `--output`. That variant has no external endpoints, because the add-in makes no update check and the server runs with `REVIT_MCP_NO_UPDATE_CHECK=1`. The committed file describes the regular build.
`--export-snake-case PATH` writes the Autodesk MCP manifest fields (`mcp_manifest_version`, `app_model`, `mcp_spec_version`, `server`, `tools`, `resources`, `prompts`, `external_endpoints`, `autodesk_apis_used`, `ai_llm_providers`) to PATH. The release workflow runs it with `--managed` for the store build, which excludes update code and declares optional user-configured workstation transports, and attaches `revit-model-mcp-<version>-store.mcp-manifest.json`.
Each tool in the store export includes `inputSchema` and annotations as the server advertises them.
The export declares stdio and named pipe defaults, optional loopback HTTP, and user-configured SSH.
`confirm_token` is a two-step handshake with the AI app.
The AI app is expected to show the action to you; Model MCP does not show its own dialog.
The server tests run the same comparison, so a PR that changes a tool without regenerating the manifest fails CI.
Tool names and descriptions are identical to `bundle/manifest.json`.

## Top-level fields

| Field | Value |
| --- | --- |
| `schemaVersion` | `1.0` |
| `mcpSpecVersion` | MCP specification version, `2025-11-25` |
| `server` | `name`, `transport` (`stdio`) and `location` (`local`: the server runs on the user's machine) |
| `tools` | Every registered tool, see below |
| `resources`, `prompts` | Every registered resource and prompt with a description |
| `externalEndpoints` | Every external domain the add-in or server can contact, HTTPS only |
| `apisUsed` | Revit and related APIs the product calls |
| `aiServices` | AI services the product sends data to; empty because the server calls no AI service |
| `dataHandling` | Notes on AI clients, read-only mode, local channels and telemetry |

## Per-tool fields

| Field | Meaning |
| --- | --- |
| `name`, `description` | As registered |
| `toolset` | The toolset the tool belongs to, see [Toolsets](tools.md#toolsets) |
| `access` | `read_only` or `modifying`, from the `readOnlyHint` annotation |
| `annotations` | `readOnlyHint`, `destructiveHint` and `idempotentHint` as registered |
| `irreversible` | `true` when `revit_undo_last` does not cover the effect (save, synchronize, close, link removal, file export, C# execution) |
| `confirmation` | `required` is `never`, `conditional` or `always`; the mechanism is the single-use `confirm_token`, and `condition` says when it applies |
| `fileSystem` | `access` is `none`, `read`, `write` or `read_write`, with a `scope` text |
| `networkAccess` | `none`, or `unrestricted_by_confirmed_code` for tools that run enabled C# through the client token handshake |

The policy tables behind `irreversible`, `confirmation` and `fileSystem` live in `build/tool_manifest.py`.
A test fails when a tool that takes `confirm_token` is missing from the confirmation table or a table names an unknown tool.
When you add a tool that changes files, asks for confirmation or cannot be undone, update the tables and regenerate the manifest.

Each `externalEndpoints` entry has `domain`, `protocol`, `component`, `purpose`, `dataSent`, `optional` and `disable`.
Loopback traffic (named pipe, file channel, `127.0.0.1` HTTP listener) is described in `dataHandling.localChannels`, not as an external endpoint.
See [Security](security.md), [Privacy](privacy.md) and [Automatic updates](updates.md) for the behavior behind these fields.

## Revit bundle

`dotnet run --project build -- bundle` packs the existing `src/RevitModelMcp.Addin/bin/Release.R*/publish` output into `output/RevitModelMcp.bundle.zip`.
The archive contains one `RevitModelMcp.bundle/` folder with `PackageContents.xml` and a `Contents/<year>/` folder per Revit year.
`pack` builds the same archive when it runs without `--no-build`.

The managed bundle is named `revit-model-mcp-<version>-managed.bundle.zip`. Build it with `Bundle__Managed=true` after compiling the add-in with `-p:ManagedInstall=true`.

`build/validate_bundle.py` checks the archive without Windows or Revit:

```sh
python build/validate_bundle.py output/RevitModelMcp.bundle.zip --years 2022-2027
python build/validate_bundle.py output/revit-model-mcp-<version>-managed.bundle.zip --years 2022-2027 --managed-version <version>
```

With `--managed-version`, the check also requires a `README.txt` that pins `revit-model-mcp==<version>`.

It checks the `PackageContents.xml` attributes (numeric `AppVersion`, braced `ProductCode` and `UpgradeCode`, company details), one `Components` entry per year with `SeriesMin` and `SeriesMax` set to `R<year>`, that every `ModuleName` and every `.addin` assembly path exists in the archive, and that no Revit API assemblies are packed.
CI builds the bundle after all six Revit years compile, validates it and uploads it as the `revit-model-mcp-bundle` artifact.
`ProductCode` is derived from the upgrade code and the version, so rebuilding the same version gives the same code.

## Store validation

```sh
python build/server-exe/build.py --channel store
cd server && uv run python ../build/tool_manifest.py --managed --export-snake-case ../output/store.mcp-manifest.json
```

On Windows, run `output/server-exe/venv/Scripts/python.exe build/store_check.py output/revit-model-mcp-1.0.0-store.bundle.zip --manifest output/store.mcp-manifest.json` from the repository root.
The check reads compressed Python code, scans binary strings, starts the bundled server to compare its live registry, and checks settings and UI source rules.
For a macOS add-in-only publish check, pass `--registry-source` and the publish directory instead of a ZIP.
That mode does not verify the bundled server.
The store workflow builds all supported years and uploads the ZIP and manifest without publishing them.
