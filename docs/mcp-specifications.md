# MCP specifications

This page answers the structured questions that Autodesk Marketplace asks about an MCP app. The machine-readable source is the [tool manifest](tool-manifest.md); the numbers below come from `tool-manifest.json` at the time of writing, and a CI check keeps the manifest in step with the server.

## App model

Model A: the server runs on the user's machine. It is a Python process that the user's MCP client starts over stdio. It talks to the add-in inside Revit through a named pipe or file channel on the same PC, a loopback HTTP listener, or a remote workstation that the user configures over SSH or HTTP(S). The project operates no hosted server.

| Field | Value |
| --- | --- |
| Server name | `revit-model-mcp` |
| Transport | `stdio` |
| MCP specification version | `2025-11-25` |
| Tools | 77 (34 read-only, 43 modifying) |
| Resources | 1 (`revit://guides/coordinator`) |
| Prompts | 5 |

## Data handling

- The add-in reads only the Revit documents that are open on the workstation, or that a tool opens at the user's request. Nothing is collected in the background.
- Tool results go to the MCP client that the user chose. The project does not receive, store or relay them.
- No analytics, telemetry or crash reporting.
- Files are stored on the workstation: channel folder, logs and code audit files. Locations and retention are in the [privacy policy](privacy.md#data-retention) and the [security whitepaper](admin/security-whitepaper.md#storage-and-retention).
- Privacy policy: [privacy](privacy.md).
- Path redaction and read-only mode are available as settings; an administrator can enforce them with a [machine policy](admin/policy-reference.md).

## External endpoints

All requests are HTTPS, carry no model data, and can be turned off. The Autodesk Marketplace build uses the update channel `store`: updates come from the store, and the add-in does not check GitHub or download an installer. <!-- depends: night/updates -->

| Domain | Component | Purpose | Present in the store build |
| --- | --- | --- | --- |
| `api.github.com` | Add-in | Latest stable release check, at most every 24 hours | No |
| `github.com`, `release-assets.githubusercontent.com` | Add-in | Release MSI and checksum download for per-user installs | No |
| `pypi.org` | Python server | Optional check for a newer server release | Yes, unless `REVIT_MCP_NO_UPDATE_CHECK=1` |
| `pypi.org`, `files.pythonhosted.org` | uv launcher | Package download when the server starts for the first time | Yes, unless the server is installed from a wheelhouse |

No other domain is contacted. The committed manifest lists the add-in domains because other builds use them; the managed manifest (`--managed`) has none for the add-in. See the [full table](admin/security-whitepaper.md#outbound-network).

## Autodesk data access

The add-in uses the Revit API on the Revit API thread and no Autodesk cloud service. It does not use Autodesk Platform Services, Autodesk Construction Cloud or Forma.

| Use | Revit API area |
| --- | --- |
| Reads | Open document through ExternalEvent |
| Changes | Elements, views, sheets, links and families, each in one named transaction |
| Documents | Open, save, synchronize and close |
| Export | PDF, DWG, IFC and schedule CSV |
| Optional | Navisworks NWC Export Utility, only for `revit_export_nwc` |

No Autodesk binaries are redistributed.

## Network transparency

- Local use opens no port. The named pipe is restricted to the Windows user who runs Revit.
- HTTP is off by default. When on, it binds to loopback and needs a bearer token.
- A remote workstation is reached only through an SSH or HTTP(S) host that the user configures. Plain HTTP to a remote host is the user's choice; use a tunnel or a TLS proxy.
- The activity pane lists every request and action.
- No AI provider is declared: neither the add-in nor the server calls one, and no API key is shipped. The MCP client may send results to its own provider under the user's agreement with it.

## Tool justification

Every tool is listed in the [tool reference](tools.md) with its access class, file system access, network access, whether it is irreversible and whether it needs a confirmation token.

| Group | Tools | Why it is needed |
| --- | --- | --- |
| Model reading | `ping`, `document_info`, `documents`, `ui_state`, `list_instances`, `list_catalog`, `aggregate_elements`, `query_elements`, `element_details`, `list_relations`, `shared_coordinates` | Let the assistant find out what is in the model before it answers or acts. |
| Views and schedules | `list_views`, `view_info`, `view_summary`, `view_elements`, `schedule_data`, `capture_elements`, `export_view` | Read what a view shows and return an image as evidence. |
| Quality checks | `model_health`, `parameter_fill_check`, `list_warnings`, `view_warnings`, `family_audit`, `links_status`, `compare_link_datums`, `model_snapshot` | Coordinator checks without changing the model. |
| Multi-model audit | `batch_start`, `batch_status`, `batch_fetch`, `batch_cancel`, `build_report`, `issue_register`, `jobs`, `nwc_settings_check` | Read-only review of many models and Excel reports. |
| Navigation | `select`, `show`, `isolate`, `activate_view`, `activate_document`, `close_views`, `override_graphics`, `set_view_visibility` | Show the user what the assistant is talking about. |
| Editing | `move`, `rotate`, `copy`, `mirror`, `set_parameter`, `update_parameters`, `change_type`, `delete`, `run_actions`, `undo_last` | Make named, undoable changes the user asked for. |
| Creation | `create_wall`, `create_mep_run`, `place_family`, `place_families`, `load_family`, `create_view`, `duplicate_view`, `apply_view_template`, `create_sheet`, `place_views_on_sheet`, `walls_from_cad`, `link_cad`, `edit_families` | Create the elements and documentation the user asked for. |
| Documents and files | `open_document`, `new_document`, `close_document`, `save_document`, `sync_document`, `remove_links`, `align_link_datums`, `export`, `export_nwc`, `process_models`, `cancel_job` | Document lifecycle and hand-over files. Irreversible or file-overwriting calls need a confirmation token. |
| Code execution | `execute_code` | Covers tasks that no fixed tool handles. Unsandboxed, token-gated, refused in read-only mode, and removable by policy. |

All modifying tools are refused in read-only mode. Tool names and descriptions do not change after installation; the server registers a fixed list.
