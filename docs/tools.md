# Read tool reference

MEP routing, graphic overrides, view and sheet creation, CAD linking and wall construction actions are listed in [Action tools](actions.md).

Revit-backed tools support local, SSH and HTTP transports. `revit_build_report` runs on the MCP client machine from snapshot files and needs no Revit transport or running instance.
`revit_capture_elements` downloads a highlighted element PNG over local or SSH transport; HTTP is unavailable.
`revit_export_view` downloads PNG through `/views/{name}/image` in HTTP mode.
`revit_list_instances` reports the connected Revit process in HTTP mode.

Every addressed Revit-backed read tool accepts optional `process_id` (alias `processId`), a strict positive integer. It selects an exact Revit process and must agree with `document` when both are given. See [batch collection](batch.md#explicit-process-addressing).

Every Revit-backed read tool except `revit_ui_state`, `revit_export_view`, `revit_capture_elements`, `revit_issue_register`, `revit_list_instances` and `revit_family_audit` accepts `timeout_seconds=120`, `pickup_timeout_seconds=300` and `document=null`. `revit_ui_state` accepts only `process_id`. Family audit accepts `response_timeout_s=600` and `document=null`.
Timeouts are seconds; pickup timeout applies only to local and SSH transports.
Arguments without defaults in these tables are required.
The query filters shared by aggregation and queries are `categories`, `family`, `type_name`, `level`, `view`, `workset`, `phase`, `area_scheme` and `parameter_filters`; each defaults to `null`.

Every successful Revit-backed read result returns top-level `skipped` and `skippedCount`. Each skipped entry has `what` and `reason`. An empty list means no fields were omitted. A non-empty list means the answer is incomplete. The list keeps the first 100 entries; `skippedCount` reports the uncapped total. `revit_list_instances` returns `instances` with empty diagnostics. Model health uses this common shape instead of a nested metric/error list. Links status also keeps each link's `error` and mirrors it in `skipped`.

| Tool | Arguments beyond the common read options | Purpose |
| --- | --- | --- |
| `revit_ping` | None | Check connectivity; returns `data:"pong"`. |
| `revit_jobs` | `job_id=null`, `wait_s=40`, `cancel_job_id=null` | List jobs, or wait up to 50 seconds for an action job (0 through 50, default 40; poll again for longer jobs). Returns progress or the original final action response. |
| `revit_nwc_settings_check` | `settings_xml` | Parse exporter XML on the Revit workstation without exporting; return values, mapping, notApplied and ignored. |
| `revit_document_info` | None | Read document, levels, area schemes and worksets. |
| `revit_documents` | `include_linked=false` | List every open document in one Revit process, including background documents; linked documents are excluded by default. |
| `revit_ui_state` | None | Read the active document and view, open UI views, up to 500 selected elements, and all open documents. |
| `revit_list_catalog` | `section` | Discover valid category, family, view and parameter names. |
| `revit_aggregate_elements` | `group_by`, `sum_field=null`, shared query filters | Group by one or two fields; return counts and optional sum/average. |
| `revit_query_elements` | Shared query filters, `fields=null`, `offset=0`, `limit=100`, `sort_field="id"`, `sort_direction="asc"`, `include_geometry=false` | Read a page of matching elements. |
| `revit_list_views` | `view_type=null`, `name_contains=null` | Find views in the active document. |
| `revit_view_summary` | `view` | Read view metadata and category counts. |
| `revit_view_info` | `view` (name or decimal ID) | Inspect view template controls, display settings, hidden categories, worksets, filters, links and temporary modes. |
| `revit_capture_elements` | `element_ids`, `pixel_size=1600`, `padding_mm=1500`, `mode="3d"`, `save_to=null`, `document=null`; no timeout arguments | Capture 1-500 model elements highlighted red in a temporary 3D or plan view. Returns PNG image content and local path; the view is rolled back. Local/SSH only. |
| `revit_export_view` | `view`, `pixel_size=1600`, `save_to=null`, `document=null`; no timeout arguments | Download a PNG; `pixel_size` is 1-4000 pixels on the fitted image dimension. |
| `revit_schedule_data` | `schedule`, `max_rows=500`, `offset=0` | Read visible schedule columns and data rows with `totalRows` and `truncated`. Paging excludes heading rows. Rejects non-schedules. |
| `revit_view_elements` | `view`, `categories=null`, `offset=0`, `limit=100` | Read a page of elements in a view. |
| `revit_element_details` | `element_id` | Read instance/type parameters and geometry by unitless Revit ID. |
| `revit_view_warnings` | `view` | Read warnings involving elements in a view. |
| `revit_list_warnings` | `warning_text=null`, `include_elements=false` | Group warnings or inspect a specific warning group. |
| `revit_list_relations` | `relation`, `source_id=null`, `source_name=null` | Read membership or dependencies. |
| `revit_list_instances` | `document=null`; no timeout arguments | Return endpoint or heartbeat information in `instances`. |
| `revit_model_health` | `save_to` (optional) | Read model quality counts and top warnings; save an Excel health report on the MCP server machine. |
| `revit_links_status` | None | Read RVT, CAD and image status, paths and instance counts. |
| `revit_shared_coordinates` | None | Read base/survey points, sites and link transforms in mm and degrees. Refused for family documents. |
| `revit_family_audit` | `families=null`, `response_timeout_s=600` | Inspect family parameters, use, shared status and purge candidates. |
| `revit_parameter_fill_check` | `categories`, `parameters`, `level=null`, `workset=null`, `view=null`, `sample_limit=20`, `include_types=true` | Count filled, empty and missing values; sample unitless element IDs. |
| `revit_model_snapshot` | `parameter_rules=null`, `document=null`, `process_id=null` | Read a schema version 1 project snapshot for batch audits. |
| `revit_batch_start` | `paths=null`, `folder=null`, `recursive=false`, `parameter_rules=null`, `years=null`, `open_timeout_minutes=null` | Start persistent read-only collection; see [batch collection](batch.md). |
| `revit_batch_status` | `run_id` | Read persisted run and model status. |
| `revit_batch_cancel` | `run_id` | Persist cancellation and stop unstarted models. |
| `revit_batch_fetch` | `run_id`, `dest_dir` | Copy completed JSON snapshots to new local files. |
| `revit_compare_link_datums` | `link`, `kinds=["grids","levels"]`, `name_map={}`, `prefix=""`, `suffix=""`, `level_offset_mm=0`, `reuse_matching=true`, `tolerance_mm=0.5` | Compare link grids and levels with host datums without modifying the model. |
| `revit_issue_register` | `output_path`, `project`, `issues`, `pixel_size=900`, `document=null`; no timeout arguments | Write a new local `.xlsx` review register with documents, severity totals, category chart, element rows and snapshots. |
| `revit_build_report` | `snapshots_dir`, `output_path`, `previous_dir=null`, `findings=null`; no Revit document or timeout arguments | Build a local `.xlsx` report from schema-v1 snapshots. |
### Snapshot report

`snapshots_dir` contains regular `*.json` files directly in that directory. Files are read in filename order. `output_path` is a client-machine `.xlsx` path; missing parent directories are created and an existing file is never overwritten. `previous_dir`, when supplied, follows the same input rules and matches models by `passport.title`. Duplicate titles are rejected. `findings` is an optional list with `model`, `severity`, `rule`, `element_ids` (or `elementIds`) and `recommendation`; IDs are integers.

The sheets appear in this order: `Summary`, `Warnings`, `Families`, `Parameters`, `Skipped`, optional `Changes`, `Findings`. Summary has one row per current snapshot and uses `passport.fileLastWriteUtc` for **File system time**: this is the OS-observed file timestamp, not a save or sync timestamp. Revit Server change and user cells are blank when that data is absent. Warning and parameter sample IDs are comma-separated; truncated warning IDs have a marker. Parameters and Changes percentages are numeric values. Changes subtracts previous values from current values, with weighted model fill percent computed as total filled divided by total elements across parameter rows. Models without a previous match have blank deltas. Findings is header-only when omitted. Every sheet freezes the header and has an auto-filter.

| Sheet | Columns |
| --- | --- |
| Summary | Model, Saved-in year, Runtime year, Upgraded, Workshared, Number of saves, File system time, Revit Server last change, Revit Server last user, Elements, Views, Sheets, Families, Family types, Links, Warnings total, Families total, In-place families, Family signals, Skipped |
| Warnings | Model, Warning, Count, Element IDs |
| Families | Model, Family, Category, In-place, Editable, Type count, Instance count, Warning count, Signals |
| Parameters | Model, Category, Parameter, Total, Filled, Empty, Missing, Fill percent, Sample IDs |
| Skipped | Model, What, Reason |
| Changes | Model, Warnings delta, Families delta, Fill percent delta, Number of saves delta |
| Findings | Model, Severity, Rule, Element IDs, Recommendation |

`revit_model_snapshot` accepts an ordered list of `{"category": "Walls", "parameter": "Mark"}` rules. Each rule pairs one category and one parameter. No rules return empty `parameterFill.rows`. Warning groups contain at most 200 distinct affected element IDs, with `elementIdsTruncated` showing whether more exist. Closed user worksets add a skipped entry because element, family instance, warning attribution, and parameter fill results can be incomplete. For an ordinary open document, `source.path`, saved-in year, and file metadata come from the document's source file. A detached interactive document has no usable source file; its snapshot contains one `source file` skipped entry with reason `detached document has no source file`, and file-derived fields remain empty. `fileLastWriteUtc` is an operating system file observation, not a Revit save or sync time. `passport.revitServer` is always null and reserved for the later batch runner.

The `settings_xml` path rejects device paths, `..` segments and UNC shares absent from `trustedNetworkRoots`; files over 1 MiB are refused before reading.

`link` accepts one linked instance ID or a case-insensitive substring of its instance or type name; ambiguous and unloaded links fail. Comparison transforms link geometry into host coordinates and reports `aligned`, `differs`, `missing_in_host`, `host_only` or `unsupported`. A same-name host datum matches first; a coincident differently named datum may match by geometry. Distances are millimetres and angles are degrees.
Geometric alignment does not create a monitor relationship or later Coordination Review warnings.

`revit_view_info` returns `id`, `name`, `type`, `isTemplate`, `template` with controlled parameter names, detail level, display style, discipline, phase and phase filter, scale, crop and 3D section box in millimetres. It also returns the background type and colours where supported, category class toggles, individually hidden categories, filter visibility and enabled flags, Revit link visibility, and active temporary mode flags. `worksets` is null outside workshared documents. Link graphic overrides are inspected on Revit 2024 and later; earlier versions report instance and category hiding.

### Family audit

On an open `.rfa`, omit `families`; the audit reads that family without saving it. In a project, pass 1-200 exact family names (case-insensitive) or `["*"]` for every editable loadable family. The add-in opens each family with `EditFamily` and closes it without loading it back. In-place, non-editable and missing families have a `skipped` result with a reason. A project transaction must be closed before the call.

Each parameter reports scope, shared GUID, group type ID, formula, reporting status and use. Associations, formula substrings and dimension or array labels count as use. Formula matching is deliberately over-inclusive. An unused shared parameter has `dataCarrierRisk:true` because project schedules and tags can still depend on its values.

`purgeable` groups Revit's unused-element candidates by category. Coverage is `full` in Revit 2024 and later. In Revit 2022-2023, coverage is `families-and-types`; materials, patterns and styles are not included.

**Coordinator checks.** Call `revit_model_health` → `revit_links_status` → `revit_shared_coordinates` → `revit_parameter_fill_check(categories=["Walls","Doors"], parameters=["Mark","Comments"])` before an export or hand-over.
Category and parameter names use the model language; the fill check accepts 1-20 categories, 1-30 parameters and a sample limit of 1-100.
Coordinator location and link lists are capped at 100 without pagination; locations are sorted by name and links by ID.
`pinned` and `viewSpecific` are true when any instance of the reported type qualifies.
Parameter names resolve through `LookupParameter(name)`, which returns the first match by name; GUID and BuiltInParameter selection are unavailable.
Paged reads that exceed their 60-second add-in budget return `partial:true` regardless of the client timeout. Family audit uses its own response budget and reports each attempted family.
Image exports (`revit_export_view`, `revit_capture_elements`) are not cut at the 60-second budget: a written PNG produces a complete result (`success:true`) and `save_to` is honored; the client/response timeout still applies.

Offsets are zero-based row counts; limits are positive row counts. `revit_query_elements` and `revit_view_elements` clamp limits above 5000 to 5000.
Lengths use mm, areas m2 and volumes m3 where metric fields are provided.
Other numeric filter values follow document display units; returned query values carry a `unit` field when available.
See the [feed format](feed-format.md#jobs) for the distinction between filter inputs and numeric outputs.
Parameter names come from the model's language; use `revit_list_catalog(section="parameters")` before filtering.
`save_to` is a new file path on the MCP client's machine and never overwrites an existing file.
The path is resolved on the machine that runs the MCP server, which is the client's machine for local and SSH setups; a path from another machine is not translated.

`revit_element_details` returns geometry alongside parameters in `data`.
`revit_query_elements(include_geometry=True)` adds the same fields to each element in the returned page.
The query flag defaults to `False`; default queries omit geometry.
All coordinates use model axes in millimetres rounded to one decimal place.

| Field | Contents |
| --- | --- |
| `location` | Point: `type:"point"`, `xMm`, `yMm`, `zMm`. Curve: `type:"curve"`, `startMm`, `endMm`, `lengthMm`. |
| `boundingBox` | `minMm`, `maxMm`, `centerMm` as `[x,y,z]` arrays from the element's model bounding box. Rooms use their own bounding box. |
| `roomCenterMm` | `[x,y,z]` from a placed room's location. Use `roomCenterMm` when placing something inside a room. A bounding box centre may lie outside a nonrectangular room. |

Unavailable geometry is omitted.

### Action job polling

Long actions may return `status:"running"`, `jobId`, progress, and partial per-model results.
Call `revit_jobs(job_id=jobId, wait_s=40)` until the original final action response is returned.
The action may already have changed the model; do not resubmit it while it runs.
Results remain available for 24 hours across MCP server restarts.
See [long action jobs](actions.md#long-action-jobs) for cancellation and retention.

### Issue register

`output_path` requires a new `.xlsx` path on the MCP server machine.
Parent directories are created; existing files are refused before contacting Revit.
`project` accepts optional string fields `name`, `model`, `reviewer`, `client`, `stage`, `date` and a `documents` list of `{title, reference, revision}` objects.
`issues` requires 1-60 objects with `title`, `category`, `finding` and `severity` (`critical`, `major`, `minor`, `info`).
Optional fields are `id` (default `ISS-001` onwards), `requirement_source`, `requirement`, `recommendation`, `status` (default `Open`), `responsible`, `due`, `element_ids` (up to 500 positive integers) and `snapshot` (`3d`, `plan`, `none`).
Categories are free text; typical values are Information standard, Naming, Level of information need, Model health, Coordinates and levels, Classification, Spatial and Geometry.
Snapshots default to `3d` with element IDs and `none` otherwise.
The workbook contains `Cover`, `Summary`, `Register` and `Elements`.
The finding cell contains the issue title and finding; snapshots occupy the final column.
Captures run sequentially for the first 25 eligible issues in severity order, with stable input order for ties.
The remaining issues receive `Snapshot skipped: limit of 25 per register`.
A 210-second capture budget reserves time within the 240-second client limit for writing the workbook.
A capture failure receives `Snapshot unavailable: <reason>` and does not fail the register.
Result fields are `path`, `issueCount`, `snapshotCount`, `bySeverity` and `warnings`.

## Saved health report

`revit_model_health(save_to="C:\\Reports\\health.xlsx")` writes a new workbook on the machine running the MCP server.
`saveTo` is an alias for `save_to`.
The path must end in `.xlsx`, its parent directory must exist, and an existing file is refused.
Without `save_to`, the response is unchanged.
With `save_to`, the response adds `workbook` with `path`, `snapshotCount` and `warnings`.
The workbook contains Health, Warnings and Counts sheets.
All warning groups include affected element IDs; snapshots cover up to five groups with the highest counts, with at most 50 elements per capture.
The total capture budget is 60 seconds; failed or skipped captures become warnings.

The Health sheet uses the following default review thresholds.
These are review defaults, not project requirements.
Unavailable metrics receive Review status.

| Check | Pass threshold |
| --- | --- |
| Warnings | 0 |
| Views not on sheets | At most 20% of all views; 0% when there are no views |
| Unused family types | At most 300 |
| In-place families | 0 |
| CAD imports | 0 |
| Unplaced rooms | 0 |
| Rooms not enclosed | 0 |
| Design options | 0 at the stage under review |
| Images | At most 20 |
| Model groups | At most 50 |
