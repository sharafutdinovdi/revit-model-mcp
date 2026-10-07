# Tool manifest

[`tool-manifest.json`](tool-manifest.json) lists every tool, resource and prompt of the server with a declaration of what each tool can do.
It helps reviewers and administrators check access before they approve the server.
Do not edit it by hand: a generator writes it from the live tool registry, the same source as `bundle/manifest.json`.

```sh
cd server && uv run python ../build/tool_manifest.py
cd server && uv run python ../build/tool_manifest.py --check
```

`--check` fails when the committed file is stale.
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
| `access` | `read_only` or `modifying`, from the `readOnlyHint` annotation |
| `annotations` | `readOnlyHint`, `destructiveHint` and `idempotentHint` as registered |
| `irreversible` | `true` when `revit_undo_last` does not cover the effect (save, synchronize, close, link removal, file export, C# execution) |
| `confirmation` | `required` is `never`, `conditional` or `always`; the mechanism is the single-use `confirm_token`, and `condition` says when it applies |
| `fileSystem` | `access` is `none`, `read`, `write` or `read_write`, with a `scope` text |
| `networkAccess` | `none`, or `unrestricted_by_confirmed_code` for tools that run user-confirmed C# |

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

`build/validate_bundle.py` checks the archive without Windows or Revit:

```sh
python build/validate_bundle.py output/RevitModelMcp.bundle.zip --years 2022-2027
```

It checks the `PackageContents.xml` attributes (numeric `AppVersion`, braced `ProductCode` and `UpgradeCode`, company details), one `Components` entry per year with `SeriesMin` and `SeriesMax` set to `R<year>`, that every `ModuleName` and every `.addin` assembly path exists in the archive, and that no Revit API assemblies are packed.
CI builds the bundle after all six Revit years compile, validates it and uploads it as the `revit-model-mcp-bundle` artifact.
`ProductCode` is derived from the upgrade code and the version, so rebuilding the same version gives the same code.
