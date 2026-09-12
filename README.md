<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/revit-model-mcp_hero_dark.png">
  <img alt="revit-model-mcp: a terminal session where an MCP client reads a live Revit 2023 model" src="docs/screenshots/revit-model-mcp_hero_light.png" width="100%">
</picture>

![Status: preview](https://img.shields.io/badge/status-preview-grey?style=flat-square) [![CI](https://img.shields.io/github/actions/workflow/status/sharafutdinovdi/revit-model-mcp/ci.yml?style=flat-square)](https://github.com/sharafutdinovdi/revit-model-mcp/actions/workflows/ci.yml) [![Release](https://img.shields.io/github/v/release/sharafutdinovdi/revit-model-mcp?include_prereleases&style=flat-square)](https://github.com/sharafutdinovdi/revit-model-mcp/releases) ![Revit 2022-2026](https://img.shields.io/badge/Revit-2022--2026-005FB8?style=flat-square) [![MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)

## What it does

The server reads a live Autodesk Revit model by default: elements, views, parameters, warnings and PNG view exports.
Actions are opt-in and require two gates: `REVIT_MCP_ALLOW_WRITE=1` in the server and an `allow-write` file on the Revit workstation.
The client can run locally or reach a remote Windows workstation over LAN, Tailscale or an SSH tunnel using HTTP with a bearer token.

## Why

Model review needs quantities and geometry.
An MCP client can discover categories, aggregate element data and export views through one connection.
The add-in handles API access inside Revit while the Python server runs on the client's machine.

## In action

Claude Desktop runs on a Mac and connects to Revit 2026 on a Windows workstation.
Both action gates are enabled in this recording.

<img alt="Claude Desktop conversation on the left, Revit 2026 on the right: Claude reads the open model, finds the largest room, opens its plan and selects it, isolates it, places a chair and moves it, then cleans up" src="docs/screenshots/revit-model-mcp_claude-desktop.gif" width="100%">

What happens in the recording, in order:

1. "What model is open in Revit right now?" The client reads the document, levels and room counts.
2. "Which level has the most room area? Find the largest room and show it to me." The client aggregates room areas by level and queries the largest room.
   `revit_show` opens a matching plan and selects the room.
3. "Isolate that room, place a Chair-Breuer at its centre and move it 800 mm along X." `revit_isolate`, then `revit_place_family` at the room's `roomCenterMm`, then `revit_move`. Each mutation is its own Revit transaction.
4. Cleanup afterwards is one more sentence: reset the view, delete the chair.

The header image is the same server driven from a small terminal client against Revit 2023 over SSH. The export below is the PNG saved by `revit_export_view`:

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/screenshots/revit-model-mcp_export-view_dark.png">
  <img alt="View exported by revit_export_view from the Revit sample project" src="docs/screenshots/revit-model-mcp_export-view_light.png" width="100%">
</picture>

## Quick start

The add-in requires Windows and Revit 2022-2026.
Build with the .NET SDK selected by [`global.json`](global.json).
The server requires Python 3.11 or later, [uv](https://docs.astral.sh/uv/getting-started/installation/) and an MCP client.
Clone on each machine that will build or run a component:

```sh
git clone https://github.com/sharafutdinovdi/revit-model-mcp.git
cd revit-model-mcp
```

The commands below start from the repository root.
Installation uses inline PowerShell commands; this repository has no install `.ps1` script.

On Windows, build from the repository root and install the Revit 2026 output:

```powershell
$ErrorActionPreference = 'Stop'
dotnet build src/RevitModelMcp.Addin -c Release.R26 -p:DeployAddin=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
$install = Join-Path $addins 'RevitModelMcp'
New-Item -ItemType Directory -Force -Path $install | Out-Null
Copy-Item 'src\RevitModelMcp.Addin\bin\Release.R26\*' $install -Recurse -Force
[xml]$manifest = Get-Content 'src\RevitModelMcp.Addin\RevitModelMcp.addin'
$manifest.SelectSingleNode('/RevitAddIns/AddIn/Assembly').InnerText = [string](Join-Path $install 'RevitModelMcp.dll')
$manifest.Save((Join-Path $addins 'RevitModelMcp.addin'))
```

`DeployAddin=false` disables automatic deployment during the build.
The install uses only `RevitModelMcp\` and `RevitModelMcp.addin` under the Revit 2026 add-ins directory.
For another installed Revit year, change both `Release.R26` paths and the add-ins year.
Start Revit and open a model after installation, or restart it if it was already running.
The add-in creates `%LOCALAPPDATA%\RevitModelMcp\instance_<processId>.json` and updates it every five seconds.
It adds no ribbon tab or button.

Register the server with Claude Code on that Windows machine:

```powershell
$server = (Resolve-Path ./server).Path
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- uv run --directory "$server" revit-model-mcp
```

For remote clients, use the setup in [Transports and remote workstations](#transports-and-remote-workstations).
`uv run --directory server revit-model-mcp --help` checks package startup and prints configuration without contacting Revit.

Ask the MCP client to call `revit_ping`.
The expected successful response has `command: "ping"` and `success: true`.
Then call `revit_document_info` to inspect the active model.
For file transports, supply a distinctive `document` title when multiple Revit instances are running.

## How it works

```mermaid
flowchart LR
    Client[MCP client] <-->|stdio| Server[Python server]
    Server <-->|local PowerShell or SSH| Channel[Windows file channel]
    Channel <-->|ExternalEvent| Revit[Revit add-in]
    Server <-->|HTTP + bearer token| Endpoint[Add-in HTTP listener]
    Endpoint <-->|ExternalEvent| Revit
```

The server submits jobs over HTTP or writes them to the Windows file channel.
The add-in processes both through the same ExternalEvent and accepts one job at a time.
HTTP returns JSON and PNG directly; local and SSH modes keep their file-based responses.
A heartbeat identifies each Revit instance and its active document.
The default tools read model data and export images.
Opt-in actions use the same channel and execute in the Revit API context.
See [how it works](docs/how-it-works.md), [architecture](docs/architecture.md) and the [feed format](docs/feed-format.md).

## Tools

All tools support local, SSH and HTTP transports.
`revit_export_view` downloads PNG through `/views/{name}/image` in HTTP mode.
`revit_list_instances` reports the connected Revit process in HTTP mode.

Every read tool except `revit_export_view` and `revit_list_instances` accepts `timeout_seconds=120`, `pickup_timeout_seconds=300` and `document=null`.
Timeouts are seconds; pickup timeout applies only to local and SSH transports.
Arguments without defaults in these tables are required.
The query filters shared by aggregation and queries are `categories`, `family`, `type_name`, `level`, `view`, `workset`, `phase`, `area_scheme` and `parameter_filters`; each defaults to `null`.

| Tool | Arguments beyond the common read options | Purpose |
|---|---|---|
| `revit_ping` | None | Check connectivity; returns `data:"pong"`. |
| `revit_document_info` | None | Read document, levels, area schemes and worksets. |
| `revit_list_catalog` | `section` | Discover valid category, family, view and parameter names. |
| `revit_aggregate_elements` | `group_by`, `sum_field=null`, shared query filters | Group by one or two fields; return counts and optional sum/average. |
| `revit_query_elements` | Shared query filters, `fields=null`, `offset=0`, `limit=100`, `sort_field="id"`, `sort_direction="asc"`, `include_geometry=false` | Read a page of matching elements. |
| `revit_list_views` | `view_type=null`, `name_contains=null` | Find views in the active document. |
| `revit_view_summary` | `view` | Read view metadata and category counts. |
| `revit_export_view` | `view`, `pixel_size=1600`, `save_to=null`, `document=null`; no timeout arguments | Download a PNG; `pixel_size` is 1-4000 pixels on the fitted image dimension. |
| `revit_view_elements` | `view`, `categories=null`, `offset=0`, `limit=100` | Read a page of elements in a view. |
| `revit_element_details` | `element_id` | Read instance/type parameters and geometry by unitless Revit ID. |
| `revit_view_warnings` | `view` | Read warnings involving elements in a view. |
| `revit_list_warnings` | `warning_text=null`, `include_elements=false` | Group warnings or inspect a specific warning group. |
| `revit_list_relations` | `relation`, `source_id=null`, `source_name=null` | Read membership or dependencies. |
| `revit_list_instances` | `document=null`; no timeout arguments | List endpoint or heartbeat information. |

Offsets are zero-based row counts; limits are positive row counts.
Lengths use mm, areas m2 and volumes m3 where metric fields are provided.
Other numeric filter values follow document display units; returned query values carry a `unit` field when available.
See the [feed format](docs/feed-format.md#jobs) for the distinction between filter inputs and numeric outputs.
Parameter names come from the model's language; use `revit_list_catalog(section="parameters")` before filtering.
`save_to` is a new file path on the MCP client's machine and never overwrites an existing file.

`revit_element_details` returns geometry alongside parameters in `data`.
`revit_query_elements(include_geometry=True)` adds the same fields to each element in the returned page.
The query flag defaults to `False`; default queries omit geometry.
All coordinates use model axes in millimetres rounded to one decimal place.

| Field | Contents |
|---|---|
| `location` | Point: `type:"point"`, `xMm`, `yMm`, `zMm`. Curve: `type:"curve"`, `startMm`, `endMm`, `lengthMm`. |
| `boundingBox` | `minMm`, `maxMm`, `centerMm` as `[x,y,z]` arrays from the element's model bounding box. Rooms use their own bounding box. |
| `roomCenterMm` | `[x,y,z]` from a placed room's location. Use `roomCenterMm` when placing something inside a room. A bounding box centre may lie outside a nonrectangular room. |

Unavailable geometry is omitted.

## Actions (opt-in)

Read-only by default. Actions are a separate tool set you enable on purpose.
Transaction warnings are dismissed and reported in `warningsDismissed` (omitted when empty); errors that cannot be safely resolved roll back the action.

Action tools have no `document` or timeout arguments.
They use the default timeouts and require exactly one instance returned by the transport.
HTTP addresses one endpoint; the file transports discover workstation instances.
All IDs are unitless Revit element IDs.

| Tool | Arguments | Action and units |
|---|---|---|
| `revit_select` | `element_ids` | Select IDs; `[]` clears selection. |
| `revit_show` | `element_ids`, `select=true` | Show nonempty IDs; return `activeView` and `viewOpened`. |
| `revit_isolate` | `element_ids`, `reset=false` | Temporarily isolate IDs; `element_ids=[]` with `reset=true` clears hide/isolate. |
| `revit_move` | `element_ids`, `dx_mm`, `dy_mm`, `dz_mm=0` | Move by model-axis offsets in mm. |
| `revit_place_family` | `family`, `type_name`, `x_mm`, `y_mm`, `level`, `rotation_deg=0` | Place a loaded family at model XY in mm on a named level; rotate about Z in degrees. |
| `revit_create_wall` | `start_mm`, `end_mm`, `level`, `wall_type`, `height_mm=3000` | Create a straight wall; endpoints are `[x,y]` in model mm. |
| `revit_set_parameter` | `element_id`, `parameter`, `value` | Set a string value by parameter name; lengths use mm, areas m2, other doubles internal units. |
| `revit_delete` | `element_ids` | Delete nonempty IDs and their dependents. |

`type_name` and `wall_type` are required arguments that accept `null`.

`revit_show` checks the open UI views before calling `ShowElements`.
If none contains a requested element, it opens a non-template plan for an element's level.
Floor plans take priority, followed by names starting with the level name.
Without a matching plan it uses the first non-template 3D view.
The handler sets `UIDocument.ActiveView` synchronously inside its ExternalEvent without a transaction; `ShowElements` needs the view active immediately.
`RequestViewChange` defers the change until control returns to Revit.
The response includes `activeView` and `viewOpened`, which reports whether the handler opened a previously closed view.

During action execution, the handler attempts to dismiss TaskDialog prompts with OK and then Yes.
Messages from successful overrides appear in `dialogsSuppressed`.
The dialog handler is removed in `finally`, including on errors.
Missing families return up to five similar names with their family categories in `closestFamilies`; unrelated names are omitted.
For `Family: Type`, `type_name=null` uses the embedded type; a conflicting `type_name` is rejected.
For a family name alone, `type_name=null` selects the first loaded type.

Both gates must be enabled:

1. Set `REVIT_MCP_ALLOW_WRITE=1` in the Python server process environment and restart the server.
   With any other value or no value, MCP `list_tools` does not include the action tools.
2. Create `%LOCALAPPDATA%\RevitModelMcp\allow-write` on the Revit workstation:

   ```powershell
   New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\RevitModelMcp" | Out-Null
   New-Item -ItemType File -Force "$env:LOCALAPPDATA\RevitModelMcp\allow-write" | Out-Null
   ```

The add-in checks the gate file for every action, including selection and navigation.
Without it, the response contains `success:false` and `error:"actions disabled on the workstation"`.
Removing the file disables actions immediately; restarting Revit is unnecessary.
The gate stays in the default local application data directory even if the transport uses `REVIT_MCP_CHANNEL_DIR`.

Actions address the process ID reported by the transport.
Coordinates use model axes and the named level's project elevation.
Pass `null` for `type_name` to choose the family's first type, or for `wall_type` to choose the first basic wall type.
Family placement uses the level-based, nonstructural overload; hosted, face-based and adaptive families may require another placement API and return an error.
An unloaded family returns up to five closest loaded names.
Parameter values use invariant numeric notation; other Double parameters use Revit internal units.
Type parameter edits affect all instances of that type and return `parameterScope:"type"`.
ElementId and read-only parameters cannot be set.

Responses from the action executor include `activeView`, including action errors.
Transport rejection and target-mismatch responses may omit action metadata.
Model changes and temporary isolation use one transaction named after the tool.
Warnings at commit are dismissed and reported on successful actions.
Errors permit one `FixElements` or `SetValue` resolution when Revit allows it; unresolved or repeated errors roll back the transaction.
Selection and navigation use UI calls without model transactions.
The tools do not save the model.
After a timeout, inspect the model before retrying an action; the previous call may have executed.

## Transports and remote workstations

| Route | Server setting | Workstation setup |
|---|---|---|
| Local Windows | `REVIT_MCP_HOST=local` | Run PowerShell under the Revit user's account. |
| HTTP over LAN | `REVIT_MCP_HOST=http://revit-host:53110` | Explicit interface bind, URL ACL and restricted firewall rule. |
| HTTP over Tailscale | `REVIT_MCP_HOST=http://<tailscale-ip>:53110` | Bind to the permitted Tailscale interface. |
| HTTP over an SSH tunnel | `REVIT_MCP_HOST=http://127.0.0.1:53110` | Keep the default loopback bind. |
| SSH file channel | `REVIT_MCP_HOST=ssh:revit-host` | Existing SSH alias and Windows PowerShell access. |

Every HTTP route uses `REVIT_MCP_TOKEN` from `%LOCALAPPDATA%\RevitModelMcp\settings.json`, except unauthenticated `/health`.
Supply the token through the MCP client's secret store.
With existing SSH access, open a tunnel from the client:

```sh
ssh -N -L 53110:127.0.0.1:53110 user@host
```

Replace `user@host` with the permitted workstation account and host.
In another terminal at the clone root, register the endpoint with Claude Code:

```sh
claude mcp add revit-model-mcp -e REVIT_MCP_HOST=http://127.0.0.1:53110 -e REVIT_MCP_REDACT_PATHS=1 -- uv run --directory "$PWD/server" revit-model-mcp
```

The server process must inherit `REVIT_MCP_TOKEN` from the client's environment or secret configuration.
For the SSH file channel, use `-e REVIT_MCP_HOST=ssh:revit-host` instead; no HTTP token is needed.
See [remote setup](docs/transport.md#remote-setups) for LAN, Tailscale, URL ACL and firewall commands.
HTTP has no built-in TLS; use an encrypted tunnel for remote access.

## Security

The model API is read-only by default.
Action tools are absent unless `REVIT_MCP_ALLOW_WRITE=1`; action execution also requires the workstation gate file described above.
The default surface covers ping, document and instance information, catalogs, element queries and aggregates, views and their elements, element parameters, warnings, relations and PNG view export.
The [command executor](src/RevitModelMcp.Addin/Control/ReadCommandExecutor.cs) and readers open no Revit transactions and expose no element creation, deletion, parameter setters or model save operations.
View export calls `Document.ExportImage` and writes an image file.
Channel jobs, responses, heartbeats and diagnostic logs also write files outside the model.

`REVIT_MCP_REDACT_PATHS=1` or `--redact-paths` reduces response `documentPath` fields to file names.
This covers nested results and instance listings.
Model names, parameter values, error text, channel files and exported image `localPath` values remain visible.

HTTP binds to `127.0.0.1:53110` by default.
A per-user 32-byte random bearer token is generated in `settings.json`; its protected NTFS ACL grants access only to the current user.
The token is never logged.
All HTTP routes except `/health` require it; health exposes the active document name and process information.
There is no built-in TLS: put remote access behind a tunnel or a TLS proxy.
Set `REVIT_MCP_HTTP_ENABLED=0` in Revit's environment or `httpEnabled=false` in settings to disable the listener entirely.
MCP action calls require both gates over every transport.
Direct HTTP action jobs require the bearer token and workstation gate; the Python registration flag does not apply to direct callers.

SSH mode stores no credentials.
Authentication and routing use the local OpenSSH configuration and agent.
The default multiplexing socket directory has mode `0700` on macOS and Linux.
The Windows file channel relies on the account's filesystem permissions.
See [transport](docs/transport.md) and [security reporting](SECURITY.md).

## Testing

The Python tests cover job construction, transport failures, downloads, action validation and MCP stdio registration with both flag states.
A threaded fake HTTP server covers health, authentication, busy responses, job polling and PNG download.
Core tests cover parsing, serialization, formatting, units and query processing.
These tests do not require a live Revit model.

On Windows:

```powershell
dotnet build src/RevitModelMcp.Addin -c Release.R26 -p:DeployAddin=false
dotnet build src/RevitModelMcp.Addin -c Release.R22 -p:DeployAddin=false
dotnet test --project tests/RevitModelMcp.Core.Tests/RevitModelMcp.Core.Tests.csproj
```

On the MCP client:

```sh
cd server
uv run --with pytest pytest -q
```

CI builds Revit 2022 and 2026 on Windows and uploads both outputs as artifacts.
It runs the Core and server tests on every push and pull request: [latest run](https://github.com/sharafutdinovdi/revit-model-mcp/actions/workflows/ci.yml).
Release tags build all five Revit configurations and the Python wheel before publishing assets.
The recordings above show live sessions; automated tests do not validate live Revit behavior.

## Compatibility

| Component | Configured support |
|---|---|
| Revit add-in | Revit 2022-2026 on Windows |
| Add-in configurations | `Debug.R22` through `Debug.R26`, `Release.R22` through `Release.R26` |
| .NET targets | .NET Framework 4.8 for Revit 2022-2024, .NET 8 for Revit 2025-2026 |
| Build SDK | Selected by `global.json` |
| Python server | Python 3.11+, MCP Python SDK 2+ |
| Local transport | Windows PowerShell under the Revit user's account |
| HTTP transport | Standard-library HTTP/HTTPS client; Windows HttpListener on .NET 4.8 and 8 |
| SSH transport | SSH client on Windows, macOS or Linux; Windows SSH host with PowerShell |

The CI workflow targets Revit 2022 and 2026 builds on Windows.
The tag workflow packages Revit 2022-2026; installing each year still requires validation in that Revit version.
See [known gaps](docs/roadmap.md#known-gaps).

## Author and license

Author and maintainer: Dinar Sharafutdinov.
Licensed under [MIT](LICENSE).
See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency licenses.
