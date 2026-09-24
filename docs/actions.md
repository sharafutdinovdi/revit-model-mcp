# Actions (enabled by default)

Actions run unless read-only mode is active: `REVIT_MCP_READ_ONLY=1` in the Python server environment, or the
workstation `%LOCALAPPDATA%\RevitModelMcp\read-only` file. Action tools stay listed either way; a refused call
returns `success:false` and `error:"read-only mode"` instead of running. See [Read-only mode](#read-only-mode).
Transaction warnings are dismissed and reported in `warningsDismissed` (omitted when empty); errors that cannot be safely resolved roll back the action.

Every action result carries a `summary`: one human sentence describing what changed, how many elements, and in
which document, alongside `verification`. A committed mutation assimilates its transaction into a single named
Revit undo entry, `MCP (<clientName>): <short summary>` (at most 60 characters), visible in Revit's Undo list and
in the add-in's "MCP activity" dockable pane (ribbon: RevitModelMcp tab, Activity panel, "MCP Activity" button).
`select` and `show` make no document change and get no undo entry, but still appear in the activity pane.
See [Undo the last action](#undo-the-last-action) for `revit_undo_last`.

The action tools listed below accept `document`. `revit_export_nwc`, `revit_edit_families` and `revit_align_link_datums` also accept `response_timeout_s` from 30 to 3600 seconds.
Revit remains busy for the whole action duration.
Other listed tools use the default response timeout of 120 seconds. All actions use a pickup timeout of 300 seconds and require exactly one instance returned by the transport.
HTTP addresses one endpoint; the file transports discover workstation instances.
All IDs are unitless Revit element IDs.
Revit 2022–2023 accept IDs up to 2,147,483,647 only; larger IDs fail on those years.

Action jobs with `targetDocument` resolve that reference when the add-in executes the job.
The reference must match exactly one open document by a case-insensitive substring of its title or file name.
The resolved document is bound by its title and full path for all mutations and verification, even if another document is active.
An unknown or closed target returns `The addressed document '<TargetDocument>' is not open.`
An ambiguous target returns `The document reference '<TargetDocument>' is ambiguous (N open documents match); use a more specific substring.`
Resolution failure aborts the whole batch before any step runs; an addressed job never falls back to the active document.
The target is resolved once before the batch loop, and a later document or transaction failure is reported as a step error.
`select`, `show` and `isolate` (including `reset=true`) require the resolved document to be active.
Otherwise, they return `Cannot run '<command>' on '<title>' because it is not the active document; activate it in Revit first.`
Jobs without `targetDocument` retain the active-document behavior.
`activeView` always reports the actual active view, even when a mutation targets another document.

| Tool | Arguments | Action and units |
| --- | --- | --- |
| `revit_select` | `element_ids` | Select IDs; `[]` clears selection. Return `count`, the current selection size after the call. |
| `revit_show` | `element_ids`, `select=true` | Show nonempty IDs; return `activeView`, `viewOpened` and `count`, the current selection size after the call. With `select=false`, `count` reports the previous selection. |
| `revit_isolate` | `element_ids`, `reset=false` | Temporarily isolate IDs; `element_ids=[]` with `reset=true` clears hide/isolate. |
| `revit_move` | `element_ids`, `dx_mm`, `dy_mm`, `dz_mm=0` | Move by model-axis offsets in mm. |
| `revit_place_family` | `family`, `type_name`, `x_mm`, `y_mm`, `level`, `rotation_deg=0` | Place a loaded family at model XY in mm on a named level; rotate about Z in degrees. |
| `revit_create_wall` | `start_mm`, `end_mm`, `level`, `wall_type`, `height_mm=3000` | Create a straight wall; endpoints are `[x,y]` in model mm. |
| `revit_set_parameter` | `element_id`, `parameter`, `value` | Set a string value by parameter name; lengths use mm, areas m2, other doubles internal units. |
| `revit_delete` | `element_ids` | Delete nonempty IDs and their dependents. |
| `revit_batch` | `steps`, `dry_run=false` | Execute 1–50 actions in one `MCP (<clientName>): ...` undo entry. |
| `revit_export_nwc` | `path`, exporter options, `overwrite=false`, `dry_run=false`, `response_timeout_s=1800` | Export NWC to an absolute workstation path. Requires the matching Navisworks NWC Export Utility. |
| `revit_edit_families` | `operations`, `families=null`, `overwrite_parameter_values=false`, `stop_on_error=true`, `dry_run=false`, `response_timeout_s=1800` | Edit open family or named project families; one load cycle per family. |
| `revit_align_link_datums` | All `revit_compare_link_datums` arguments, `create_missing=true`, `level_type=null`, `grid_type=null`, `include_pinned=false`, `create_plan_views=false`, `plan_view_type=null`, `dry_run=false`, `response_timeout_s=600` | Move same-name grids and levels to a linked model; optionally create missing datums and floor plans. Cannot be used in a batch. |
| `revit_undo_last` | `document` | Undo the last MCP action through Revit's own undo command; refused unless it is still Revit's last undo entry. |

Alignment uses one host-document transaction, with `dry_run` rolling it back after prospective results are read. Pinned and other-user-owned datums are skipped. Existing datums are never renamed or deleted, and grid extents and scope boxes are never changed. Moving levels also moves elements hosted on them; moved-level results include `dependentCount`. Created datums report their ID and workset. Geometric alignment does not create a monitor relationship or later Coordination Review warnings.

### NWC export options

The API export uses only the options sent to `revit_export_nwc`; settings saved by the Navisworks exporter dialog are not used. The target must be a project document. The NWC file stays on the Revit workstation; no artifact is transferred to the client. This action cannot run inside `revit_batch`.

| Argument | Default | Revit API property |
| --- | --- | --- |
| `path` | required | `Document.Export` folder and name without `.nwc` |
| `scope` | `model` | `ExportScope`: `model`, `view`, `selection` |
| `view` | `null` | `ViewId`; non-template 3D view name or ID, required for `view` scope |
| `element_ids` | `null` | `SetSelectedElementIds`; nonempty for `selection` scope |
| `coordinates` | `shared` | `Coordinates`: `shared`, `internal` |
| `parameters` | `all` | `Parameters`: `all`, `elements`, `none` |
| `export_element_ids` | `true` | `ExportElementIds` |
| `convert_element_properties` | `false` | `ConvertElementProperties` |
| `export_parts` | `false` | `ExportParts` |
| `export_room_as_attribute` | `true` | `ExportRoomAsAttribute` |
| `export_room_geometry` | `true` | `ExportRoomGeometry` |
| `convert_lights` | `false` | `ConvertLights` |
| `convert_linked_cad_formats` | `true` | `ConvertLinkedCADFormats` |
| `export_links` | `false` | `ExportLinks` |
| `export_urls` | `true` | `ExportUrls` |
| `divide_file_into_levels` | `true` | `DivideFileIntoLevels` |
| `find_missing_materials` | `true` | `FindMissingMaterials` |
| `faceting_factor` | `1.0` | `FacetingFactor`, greater than 0 and at most 100 |
| `overwrite` | `false` | Replace an existing NWC only after successful export |
| `dry_run` | `false` | Validate without exporting |
| `response_timeout_s` | `1800` | Channel response timeout, 30–3600 seconds |

`path` must be an absolute drive or UNC path ending in `.nwc`, with an existing parent directory. Relative paths, `..` segments, device paths, invalid file names and existing files without `overwrite=true` are rejected. A dry run checks the path, exporter and resolved view or selection, then returns effective options without writing. `scope="view"` exports the specified 3D view with its section box. The RVT file reader's Embed textures, Strict sectioning and view conversion settings are outside this exporter API.

### Family edits

In an open `.rfa`, omit `families`. The add-in edits it in place and leaves saving to the user. In a project, pass 1–200 exact family names or `["*"]`; in-place, non-editable, missing and other-user-owned families are skipped with reasons. The add-in opens each family, applies operations in order inside one family transaction, then loads it into the project with one project undo entry. A dry run re-reads the prospective family and rolls back without loading. The command is excluded from `revit_batch`.

Operations use snake_case `op` values:

```json
{"operations":[
  {"op":"add_shared_parameters","parameters":[{"name":"AssetId","guid":null,"group":"Data","instance":true}],"replace_family_parameter":false,"shared_parameter_file":null},
  {"op":"remove_parameters","names":["Old"],"include_shared":false},
  {"op":"purge"},
  {"op":"set_shared","shared":true}
]}
```

`add_shared_parameters` reads definitions from the named absolute workstation file or Revit's current shared parameter file. A GUID identifies a definition; name lookup must be unique across groups. `group` is a `GroupTypeId` property name. Existing GUIDs are unchanged; same-name conflicts need `replace_family_parameter=true`. `remove_parameters` removes only unused parameters. Formula references, associations and labels keep a parameter; shared parameters also need `include_shared=true`. Built-in parameters remain. `purge` repeats up to five passes and reports deletion counts. Revit 2022–2023 covers only unused families and types; Revit 2024 and later uses full purge candidates. `set_shared` reports unsupported families and verifies the loaded project state. Shared nested families keep the project version during load. `overwrite_parameter_values=true` replaces existing project type parameter values; instance values remain.

With `stop_on_error=true`, the first failed family rolls back the whole project group and reports `failedFamily`. With `false`, that family's load is rolled back and later families continue. A shared-to-non-shared overwrite may fail verification; delete and reload that family manually if Revit keeps its previous shared state.

`type_name` and `wall_type` are required arguments that accept `null`.

`revit_move`, `revit_place_family`, `revit_create_wall`, `revit_set_parameter` and `revit_delete` accept a final `dry_run=false` argument.
A dry run executes the mutation, reads its prospective result, and rolls back the transaction.
A successful dry run includes `data.dryRun:true`, `data.rolledBack:true` and the same `verification` shape as a real write.
An action that throws returns an error without a verification block; a missing family also returns `closestFamilies` on the single-action tool.
`revit_isolate` has no `dry_run` argument; it uses temporary isolation only.
Created IDs in a dry run are provisional and do not identify persisted elements.

Successful real writes return `data.dryRun:false` and re-read the affected elements after commit.
`verification.before` is captured before the change; `verification.after` is re-read after commit or before rollback on a dry run.
`verification.error` reports a failed post-commit re-read; the change is committed.
Single-action responses include `failedStep:null`.
The `verification` block contains model facts: bounding boxes for moves, parameter values and ownership for parameter edits, element metadata for creation, and deleted/dependent IDs with a survival check for deletion.
Bounding boxes use model XYZ in mm rounded to one decimal; unavailable bounding boxes are omitted.
For example, setting Comments on element 123 returns:

```json
{
  "dryRun": false,
  "verification": {
    "before": {"id": 123, "parameter": "Comments", "value": "", "storageType": "String", "owner": "instance"},
    "after": {"id": 123, "parameter": "Comments", "value": "Reviewed", "storageType": "String", "owner": "instance"},
    "changed": [123]
  }
}
```

`revit_batch` takes action names and their normal snake_case arguments:

```json
{
  "steps": [
    {"action": "move", "args": {"element_ids": [123], "dx_mm": 100, "dy_mm": 0}},
    {"action": "set_parameter", "args": {"element_id": 123, "parameter": "Comments", "value": "Reviewed"}}
  ],
  "dry_run": false
}
```

A successful batch assimilates its transactions into one undo entry named `MCP (<clientName>): <short summary>`; a dry run assimilates nothing and `undoName` is absent.
The first failed step rolls back the entire batch; every attempted step, including the failing one, carries `rolledBack:true`.
An `Assimilate` failure is reported on the last step with `failedStep` pointing at it.
All steps are validated before execution; an invalid later step rejects the whole batch without executing anything and without `failedStep`.
Results include zero-based `index`, `command`, `success` and `data` or `error` per attempted step, plus `undoName`, `committed` and `failedStep` (null on success).
A batch dry run executes every step against preceding steps' changes, then rolls back the group and restores the original selection.
A per-step `dry_run:true` inside a real batch is accepted and previews only that step.
Verification describes each step's immediate result; subsequent steps may change those elements again.
Batches accept 1–50 steps; `select` and `isolate` are allowed, while `show`, nested batches and unknown argument keys are rejected.

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
For the single-action `revit_place_family` tool, missing families return up to five similar names with their family categories in `closestFamilies`; unrelated names are omitted.
Inside `revit_batch`, a missing family surfaces only as `steps[].error` text; `closestFamilies` is unavailable.
For `Family: Type`, `type_name=null` uses the embedded type; a conflicting `type_name` is rejected.
For a family name alone, `type_name=null` selects the first loaded type.

### Read-only mode

Actions run by default. Either gate below independently switches Revit into read-only mode, with the action
tools still listed and returning `success:false`, `error:"read-only mode"` instead of running:

1. Set `REVIT_MCP_READ_ONLY=1` in the Python server process environment and restart the server.
   Every action call short-circuits before it reaches Revit, including `revit_undo_last`.
2. Create `%LOCALAPPDATA%\RevitModelMcp\read-only` on the Revit workstation:

   ```powershell
   New-Item -ItemType Directory -Force "$env:LOCALAPPDATA\RevitModelMcp" | Out-Null
   New-Item -ItemType File -Force "$env:LOCALAPPDATA\RevitModelMcp\read-only" | Out-Null
   ```

The add-in checks the gate file for every action, including selection and navigation, and shows a "Read-only"
badge in the MCP activity pane while it is present.
Removing the file re-enables actions immediately; restarting Revit is unnecessary.
The gate stays in the default local application data directory even if the transport uses `REVIT_MCP_CHANNEL_DIR`.
Direct HTTP action jobs are refused the same way, with HTTP status 403.

Actions address the process ID reported by the transport.
Coordinates use model axes and the named level's project elevation.
Pass `null` for `type_name` to choose the family's first type, or for `wall_type` to choose the first basic wall type.
Family placement uses the level-based, nonstructural overload; hosted, face-based and adaptive families may require another placement API and return an error.
The single-action family placement tool returns up to five closest loaded names for an unloaded family.
Parameter values use invariant numeric notation; other Double parameters use Revit internal units.
Type parameter edits affect all instances of that type and return `parameterScope:"type"`.
ElementId and read-only parameters cannot be set.

Responses from the action executor include `activeView`, including action errors.
Transport rejection and target-mismatch responses may omit action metadata.
A committed single action or batch runs inside a `TransactionGroup` assimilated into one undo entry named
`MCP (<clientName>): <short summary>`, truncated to 60 characters; `clientName` comes from the calling MCP
client's `initialize` handshake, or `unknown`. `select`, `show`, `export-nwc` and `revit_compare_link_datums`
make no document change and open no such group.
Warnings at commit are dismissed and reported on successful actions.
Errors permit one `FixElements` or `SetValue` resolution when Revit allows it; unresolved or repeated errors roll back the transaction.
Selection and navigation use UI calls without model transactions.
The tools do not save the model.
After a timeout, inspect the model before retrying an action; the previous call may have executed.

### Undo the last action

`revit_undo_last` undoes the most recent MCP action through Revit's own Undo command
(`PostableCommand.Undo`), never by reversing the mutation programmatically. It is allowed only when all of
the following hold, tracked from Revit's `DocumentChanged` event:

- the addressed document is the active document;
- no command is currently pending in Revit;
- the name Revit reports for its last undo entry still equals the name recorded for the most recent
  activity entry.

Otherwise it refuses with a clear reason, for example `the last change in Revit is not ours: Move Elements`
when the user made an unrelated edit since the last MCP action, or `there is no recorded MCP action to
undo` when nothing has run yet. Only the single most recent action is covered; there is no redo and no
undo of an older entry. The same button appears on the newest row of the activity pane, disabled once it no
longer applies.

### MCP activity pane

The add-in keeps an in-memory ring buffer of the last 500 finished action jobs, also appended as JSON lines
to `%LOCALAPPDATA%\RevitModelMcp\activity.log`: time, client, command, document, state (`done`, `failed` or
`dry_run`), `summary`, the changed, created and deleted element IDs with category and name, the undo entry
name, and whether it was later undone. Toggle the "MCP activity" dockable pane from the RevitModelMcp ribbon
tab. It shows, newest first: a status icon, time, client badge, command and summary per row; an expandable
detail listing changed/created/deleted elements (deleted elements are not clickable; others select and zoom
on click); a "Show all" button per row; an "Undo" button on the newest eligible row; and, above the log, a
live queue section for jobs still waiting on this Revit instance, each with a "Cancel" button.
`Document` is always `Document.Title`, a file name, never a directory, so nothing in the log needs path
redaction.
