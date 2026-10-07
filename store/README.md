# Autodesk Marketplace files

This folder holds files for a listing in the Autodesk Design and Make Marketplace.
They are generated or checked by CI; do not edit them by hand.

## MCP tool manifest

`autodesk-mcp-manifest.json` is the MCP Tool Manifest that Autodesk requires for MCP servers.
It follows the format in the [MCP publisher guide](https://aps.autodesk.com/marketplace/mcp-publisher-guide) and the [submission FAQ](https://damassets.autodesk.net/content/dam/autodesk/www/pdfs/mcp-faq.pdf).
The generator reads the live tool registry, the same source as `bundle/manifest.json`:

```sh
cd server && uv run python ../build/autodesk_mcp_manifest.py
cd server && uv run python ../build/autodesk_mcp_manifest.py --check
```

`--check` fails when the committed file is stale.
The server tests run the same comparison, so a PR that changes a tool without regenerating the manifest fails CI.
Tool names and descriptions are identical to `bundle/manifest.json`.

### Fields required by Autodesk

| Field | Value |
| --- | --- |
| `mcp_manifest_version` | `1.0` |
| `app_model` | `A`: the server runs on the user's machine (`B` embedded, `C` publisher-hosted) |
| `mcp_spec_version` | MCP specification version, `2025-11-25` |
| `server` | `name` and `transport` (`stdio`) |
| `tools` | `name` and `description` for every registered tool |
| `resources`, `prompts` | Every registered resource and prompt |
| `external_endpoints` | Every external domain, HTTPS only; `[]` when there are none |
| `autodesk_apis_used` | Autodesk APIs the product calls |
| `ai_llm_providers` | AI providers the product sends data to; empty because the server calls no AI service |

### Fields added by this project

Autodesk specifies only `name` and `description` per tool.
The extra fields below describe behavior that a reviewer asks about; a consumer that does not know them can ignore them.

| Field | Meaning |
| --- | --- |
| `tools[].access` | `read_only` or `modifying`, from the `readOnlyHint` annotation |
| `tools[].annotations` | `readOnlyHint`, `destructiveHint` and `idempotentHint` as registered |
| `tools[].irreversible` | `true` when `revit_undo_last` does not cover the effect (save, synchronize, close, link removal, file export, C# execution) |
| `tools[].confirmation` | `required` is `never`, `conditional` or `always`; the mechanism is the single-use `confirm_token`, and `condition` says when it applies |
| `tools[].file_system` | `access` is `none`, `read`, `write` or `read_write`, with a `scope` text |
| `tools[].network_access` | `none`, or `unrestricted_by_confirmed_code` for tools that run user-confirmed C# |
| `external_endpoints[]` | `domain`, `protocol`, `component`, `purpose`, `data_sent`, `optional`, `disable` |
| `data_handling` | Notes on AI clients, read-only mode, local channels and telemetry |

The policy tables behind `irreversible`, `confirmation` and `file_system` live in `build/autodesk_mcp_manifest.py`.
A test fails when a tool that takes `confirm_token` is missing from the confirmation table or a table names an unknown tool.
When you add a tool that changes files, asks for confirmation or cannot be undone, update the tables and regenerate the manifest.

Loopback traffic (named pipe, file channel, `127.0.0.1` HTTP listener) is described in `data_handling.local_channels`, not as an external endpoint.

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
