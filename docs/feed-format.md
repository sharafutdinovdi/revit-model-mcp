# Feed format

The v0.2.0 protocol uses UTF-8 JSON and case-sensitive field names.
It has no `schemaVersion` field; the package version identifies the documented contract.
The standalone add-in does not write a feed under `%LOCALAPPDATA%\RevitDevLoader`.
Its default channel is `%LOCALAPPDATA%\RevitModelMcp`.

## Files and directories

| Location | Contents |
|---|---|
| Channel directory | `job_<jobId>.json`, legacy `trigger.txt`, `mcp_<uuid>.tmp`, `response_<timestamp>_<command>.json`, `view_<timestamp>_<id>.png`, `instance_<processId>.json` and heartbeat `.tmp` files |
| Channel directory, legacy snapshots | `latest.json`, `latest.txt`, `snapshot_yyyyMMdd_HHmmss.json` |
| Channel directory, legacy view dumps | `views_dump_yyyyMMdd_HHmmss_fff.json` and matching `.txt`; a numeric suffix avoids existing names |
| `%LOCALAPPDATA%\RevitModelMcp\settings.json` | HTTP listener settings and persistent bearer token |
| `%LOCALAPPDATA%\RevitModelMcp\allow-write` | Workstation action gate; file existence enables actions |
| Windows Documents folder, `RevitModelMcp\Logs` | `RevitModelMcp-yyyyMMdd.log`, with numbered size rotations |
| `%TEMP%\RevitModelMcp\Logs` | Log fallback when Documents is unavailable |

`REVIT_MCP_CHANNEL_DIR` overrides the channel directory in both the server and Revit environments.
It does not relocate HTTP settings, the action gate or logs.
Response timestamps use local time with millisecond precision and optional collision suffixes.
HTTP stores completed response JSON in memory; exported PNGs still use the channel directory.

## Jobs

MCP tools translate snake_case arguments into channel JSON fields.
The request `{"command":"ping"}` checks connectivity without an active model.
Read jobs may contain `targetDocument`; actions add `targetProcessId` from instance discovery.
Every server job contains a GUID `jobId`, a per-process GUID `clientId`, and `clientName` from MCP initialize (or `unknown`).
The response envelope adds `client:{name,id}`, `jobId`, and `queuedMs`.
`family-audit` contains `families` only in project mode. `edit-families` contains `operations` and may contain `families`; the addressed Revit document determines the mode.
`targetDocument` matches a case-insensitive substring of the active document title or path basename in the add-in.
An HTTP endpoint also rejects jobs addressed to another process.

| MCP arguments | JSON fields |
|---|---|
| `document` | `targetDocument` |
| `cancel_job_id` | `cancelJobId` for the `jobs` read command |
| `element_id` | `id` for `element-details`; `elementId` for `set-parameter` |
| `element_ids` | `elementIds` |
| `dry_run` | `dryRun` (optional boolean, defaults to false) |
| `view_type`, `name_contains` | `viewType`, `nameContains` |
| `pixel_size` | `pixelSize`; the server also sets `zoomToFit:true` |
| `group_by`, `sum_field` | `groupBy`, `numericField` |
| `type_name`, `area_scheme`, `parameter_filters` | `type`, `areaScheme`, `parameterFilters` for query filters; placement uses `typeName` |
| `sort_field`, `sort_direction` | `sort:{field,direction}` |
| `include_geometry`, `include_elements`, `warning_text` | `includeGeometry`, `includeElements`, `warningText` |
| `source_id`, `source_name` | `sourceId`, `sourceName` |
| `dx_mm`, `dy_mm`, `dz_mm`, `x_mm`, `y_mm` | `dxMm`, `dyMm`, `dzMm`, `xMm`, `yMm` |
| `start_mm`, `end_mm`, `wall_type`, `height_mm`, `rotation_deg` | `startMm`, `endMm`, `wallType`, `heightMm`, `rotationDeg` |
| `name_map`, `level_offset_mm`, `reuse_matching`, `tolerance_mm` | `nameMap`, `levelOffsetMm`, `reuseMatching`, `toleranceMm` |
| `create_missing`, `level_type`, `grid_type`, `include_pinned`, `create_plan_views`, `plan_view_type` | `createMissing`, `levelType`, `gridType`, `includePinned`, `createPlanViews`, `planViewType` |

`compare-link-datums` is a read job; `align-link-datums` is an action job. Their `data.items` use `aligned`, `differs` or `moved`, `missing_in_host` or `created`, `host_only`, `unsupported`, and action-only `skipped`. Distances are rounded to 0.1 mm and angles to 0.001 degrees. `data.summary` counts aligned, moved, created, host-only, unsupported and skipped items.

`save_to` and timeouts are client options, not job fields.
`parameterFilters` entries contain `parameter`, `operator` and an optional `value`.
Numeric filter values use mm for lengths, m2 for areas and m3 for volumes.
Other measurable filter values use the document's display units; unmeasurable doubles use internal values.
Returned query fields include `value`, optional `numericValue`, `unit`, `hasValue` and `source`.
Aggregation uses these numeric values; inspect the returned unit before interpreting a sum.
See the [tool tables](../README.md#tools) for argument defaults and units.
The [job builders](../server/revit_model_mcp/universal_jobs.py) and [parser](../src/RevitModelMcp.Core/Control/ControlJobParser.cs) define the request contract.

### Coordinator checks

These jobs use the read response envelope and require no action gate.
All accept optional `targetDocument`; timeout options stay on the Python client.
The following examples show `data` independently of that envelope.

`model-health` job:

```json
{"command":"model-health"}
```

Response data shape:

```json
{
  "revitVersion":"2026", "revitBuild":"build", "fileName":"Model.rvt",
  "isWorkshared":false, "fileSizeBytes":null,
  "projectInfo":{"name":"Model","number":"01","client":"","address":"","buildingName":"","status":"","author":""},
  "counts":{"elements":0,"warnings":0,"warningGroups":0,"levels":0,"grids":0,"views":0,
    "viewsNotOnSheets":0,"viewTemplates":0,"sheets":0,"rooms":0,"roomsUnplaced":0,
    "roomsNotEnclosed":0,"families":0,"familiesInPlace":0,"familyTypesUnused":0,
    "groupsModel":0,"groupsDetail":0,"groupTypes":0,"designOptions":0,"worksets":0,
    "linksRvt":0,"linksCad":0,"cadImports":0,"images":0},
  "topWarnings":[{"text":"Warning description","count":1}],
  "units":{"length":"unit type id","area":"unit type id","volume":"unit type id"},
  "skipped":[]
}
```

`elements` counts all non-type elements, including views and sheets.
`familyTypesUnused` counts element types unreferenced by any non-type element's type ID.
`linksRvt` counts RVT types; `linksCad` counts CAD types with external file references; `cadImports` and `images` count instances.
`viewsNotOnSheets` excludes templates and includes plan, section, elevation, 3D, drafting and legend views absent from sheets.
Unplaced rooms have nonpositive area and no location; not-enclosed rooms have nonpositive area and a location.
`topWarnings` contains at most ten groups sorted by descending count.
A failed metric is null and adds `{"metric":"counts.rooms","error":"description"}` to `skipped`.
`fileSizeBytes` is null without a saved path; an inaccessible file also records a skipped metric.

`links-status` job:

```json
{"command":"links-status"}
```

Response data shape:

```json
{
  "rvtLinks":[{"name":"A.rvt","typeId":10,"status":"Loaded","pathType":"Absolute","path":"C:\\Models\\A.rvt","instances":1,"pinned":true,"nested":false}],
  "cadLinks":[{"name":"Plan.dwg","typeId":20,"isLinked":true,"status":"Loaded","path":"C:\\Models\\Plan.dwg","instances":1,"viewSpecific":true}],
  "images":[{"name":"Logo.png","typeId":30,"status":"Loaded","path":"C:\\Models\\Logo.png","instances":1}],
  "summary":{"rvt":1,"rvtLoaded":1,"cad":1,"cadImports":0,"images":1},
  "listLimit":100
}
```

Each list contains at most 100 types, ordered by type ID; summary counts cover all types, while `cadImports` counts imported instances.
RVT/CAD status is `Loaded`, `Unloaded`, `NotFound`, `LocallyUnloaded`, `InClosedWorkset` or `Other`.
Images preserve Revit's `ImageTypeStatus`: `Loaded`, `Unloaded`, `FailedToLoad`, `Imported`, `Generated` or `Unknown`.
Per-type read failures return `status:"Other"` and `error`; unavailable paths are omitted.
RVT `pathType` is `Absolute`, `Relative`, `Cloud`, `Server` or `Unknown`.
The add-in supplies paths; the Python server reduces nested `path` fields to file names when `REVIT_MCP_REDACT_PATHS=1`.

`shared-coordinates` job:

```json
{"command":"shared-coordinates"}
```

Response data shape:

```json
{
  "activeProjectLocation":"Internal", "projectLocations":["Internal"],
  "projectBasePoint":{"eastWestMm":0.0,"northSouthMm":0.0,"elevationMm":0.0,"angleToTrueNorthDeg":0.0,"clipped":false},
  "surveyPoint":{"eastWestMm":0.0,"northSouthMm":0.0,"elevationMm":0.0,"clipped":null},
  "internalOriginToBasePointMm":{"x":0.0,"y":0.0,"z":0.0},
  "trueNorthAngleDeg":0.0, "siteName":"Internal",
  "sharedSiteFromLinks":[{"linkName":"A.rvt","sharedSiteName":null,"hasOffset":true,"offsetMm":{"x":100.0,"y":0.0,"z":0.0},"rotationDeg":0.0}],
  "listLimit":100, "projectLocationsTotal":1, "linkInstancesTotal":1
}
```

Coordinates use mm and angles use degrees, rounded to one decimal place.
True north is the active location's project position angle at the internal origin.
`clipped` reports the project base point API value, or null when unavailable.
Link data describes the total transform; `hasOffset` compares that transform with identity before rounding, and `sharedSiteName` is null.
Locations and link instances are capped at 100; total fields report uncapped counts.

`parameter-fill-check` job:

```json
{"command":"parameter-fill-check","categories":["Walls","Doors"],"parameters":["Mark","Comments"],"level":"Level 1","workset":"Shell","view":"Plan","sampleLimit":20,"includeTypes":true}
```

`categories` requires 1–20 names and `parameters` requires 1–30 names.
Optional `level`, `workset` and `view` use the universal query filter semantics, including localized category resolution and unknown-name errors.
`sampleLimit` defaults to 20 and rejects values outside 1–100; `includeTypes` defaults to true.
Python arguments `sample_limit` and `include_types` map to `sampleLimit` and `includeTypes`.
Response data shape:

```json
{
  "scope":{"categories":["Walls","Doors"],"elements":3,"level":"Level 1","workset":"Shell","view":"Plan"},
  "parameters":[{"name":"Mark","elements":3,"filled":1,"empty":1,"missing":1,
    "storageTypes":{"String":2},"owner":{"instance":1,"type":1},
    "emptySampleIds":[101],"missingSampleIds":[102],
    "byCategory":[{"category":"Walls","elements":3,"filled":1,"empty":1,"missing":1}]}]
}
```

Scope counts cover all matching non-type elements without pagination.
Each parameter's `filled + empty + missing` equals `elements`; storage and owner counts include existing parameters only.
Type fallback occurs only when the instance has no parameter with that name.
A filled parameter has `HasValue`; strings also require non-whitespace text and element IDs must differ from `InvalidElementId`.
Double and integer zero values count as filled when `HasValue` is true.
Each sample list is capped independently at `sampleLimit`; `byCategory` contains only categories with matching elements.

## Command responses

```json
{
  "command": "ping",
  "success": true,
  "partial": false,
  "data": "pong",
  "elapsedMs": 0,
  "responder": {
    "documentName": "Sample model",
    "documentPath": "C:\\Models\\Sample model.rvt",
    "processId": 1234,
    "revitVersion": "2026"
  }
}
```

`data` depends on the command and is omitted when null.
`message` carries optional diagnostic text.
Read failures use `success:false` and `message`; the Python server converts them to MCP tool errors.
Partial reads use `success:false`, `partial:true` and any available `data`; the server also treats them as errors.
Action failures retain the response object and add `error`.
`revit_list_instances` returns a list of instance objects directly, outside this response envelope.

| Action response field | Contract |
|---|---|
| `activeView` | Active view name at response time, or an empty string without a document; supplied by the action executor |
| `viewOpened` | Present for `show`; whether its explicit view-opening step opened a previously closed view |
| `dialogsSuppressed` | Messages from successful TaskDialog overrides; an empty list is emitted for actions without overrides |
| `warningsDismissed` | Warning descriptions from a successful transaction; omitted when empty and on failed actions |
| `failedStep` | Present and null on single actions |
| `data.closestFamilies` | Similar loaded family names with categories when a family is missing in the single-action tool; batches return only `steps[].error` text |
| `data.parameterScope` | `instance` or `type` after `set-parameter`; type edits affect every instance using that type |

Transport errors and target mismatches can occur before the action executor and omit these fields.
See [response models](../src/RevitModelMcp.Core/Models/ReadCommandModels.cs) and [action models](../src/RevitModelMcp.Core/Control/ActionJobParser.cs).

Family responses have `data.mode` (`family` or `project`) and `data.families`. Audit entries include `parameters`, `purgeable`, `purgeableTotal` and `purgeCoverage`; skipped entries include a reason. Edit responses include `dryRun`, `committed`, `failedFamily`, per-family status and ordered operation results. `stopOnError=true` rolls back the project group and marks attempted families `rolledBack:true`. A timed-out response may follow a committed change; inspect the model before retrying.

## Action writes and batches

The file channel and HTTP accept `dryRun` on `move`, `place-family`, `create-wall`, `set-parameter`, `delete` and `batch`.
Successful mutations always return `data.dryRun`.
Successful dry runs return `data.rolledBack:true`; their prospective facts are read before rollback.
An action that throws returns an error without a verification block.
`verification.before` is captured before the change.
Real writes re-read `verification.after` after commit.
`verification.error` reports a failed post-commit re-read; the change itself is committed.
Unavailable bounding boxes are omitted; available bounds are XYZ arrays in model mm rounded to one decimal.
Parameter values are invariant strings with lengths in mm, areas in m2 and other doubles in internal units.
`owner` is `instance` or `type`.

| Command | `data.verification` shape |
|---|---|
| `move` | `{"before":{"elements":[{"id":1,"category":"Walls","boundingBoxMinMm":[0,0,0],"boundingBoxMaxMm":[100,100,3000]}]},"after":{"elements":[{"id":1,"category":"Walls","boundingBoxMinMm":[10,0,0],"boundingBoxMaxMm":[110,100,3000]}]},"changed":[1]}` |
| `set-parameter` | `{"before":{"id":1,"parameter":"Comments","value":"","storageType":"String","owner":"instance"},"after":{"id":1,"parameter":"Comments","value":"Reviewed","storageType":"String","owner":"instance"},"changed":[1]}` |
| `place-family`, `create-wall` | `{"after":{"id":2,"category":"Walls","family":"Basic Wall","type":"Generic","level":"01","boundingBoxMinMm":[0,0,0],"boundingBoxMaxMm":[1000,200,3000]}}`; dry runs add `"wouldCreate":true` inside `verification`. |
| `delete` | `{"before":{"requested":[1],"dependents":[2]},"after":{"stillPresent":[]},"changed":[1,2]}` |

`changed` contains IDs whose rounded bounds or parameter values differ, or all IDs returned by `Document.Delete`.
`dependents` excludes explicitly requested IDs.
Creation IDs from a dry run are provisional.
Creation metadata comes from the created element and its type and level.

A batch job contains a nonempty `steps` array of at most 50 command objects:

```json
{"command":"batch","dryRun":false,"steps":[
  {"command":"move","elementIds":[1],"dxMm":10,"dyMm":0},
  {"command":"set-parameter","elementId":1,"parameter":"Comments","value":"Reviewed"}
]}
```

Steps use each command's normal channel fields.
All steps are validated at parse time before execution; an invalid later step rejects the entire batch without executing any step and without `failedStep`.
Allowed commands are `move`, `place-family`, `create-wall`, `set-parameter`, `delete`, `select` and `isolate`.
Each model step uses its own transaction; the group is assimilated into the single undo entry `revit_batch`.
A batch dry run retains each step's changes for subsequent steps and rolls back the group at the end.
An individual channel step with `dryRun:true` (MCP `dry_run:true`) in a real batch is accepted and previews only that step.
Selection is restored on batch rollback.

`data.steps[]` contains `index` (zero-based), `command`, `success`, and `data` or `error`.
Each successful mutation's `data` carries the single-action verification shape.
The first failed step stops execution; all attempted steps, including the failing one, carry `rolledBack:true`, as does any retained step data.
An `Assimilate` failure is reported on the last step with `failedStep` pointing at that step.
`data.undoName` is `"revit_batch"`, `data.committed` reports group assimilation, and `data.failedStep` is the failed index or null.
Dry-run success has `committed:false`, `failedStep:null` and `rolledBack:true`.
Verification records each step immediately; later steps can supersede those facts.

## Geometry and image exports

`export-nwc` is a gated action job and is excluded from `batch`. It accepts `path`, `scope`, `view`, `elementIds`, `coordinates`, `parameters`, all exporter Boolean options, `facetingFactor`, `overwrite` and `dryRun`. The add-in assigns every `NavisworksExportOptions` property explicitly. The response `data` includes `path`, `bytes`, `sha256`, `elapsedMs`, `scope`, `view`, `elementCount`, `options` with snake_case keys, `dryRun` and `overwritten`. A dry run also reports `exporterAvailable` and `pathChecks` without writing a file. NWC bytes remain on the workstation.

`revit_element_details` returns `location`, `boundingBox` and `roomCenterMm` directly under `data` when available.
`revit_query_elements(include_geometry=true)` includes them on each returned element.
Point locations use `type:"point"`, `xMm`, `yMm`, `zMm`.
Curve locations use `type:"curve"`, `startMm`, `endMm`, `lengthMm`.
Bounding boxes use `minMm`, `maxMm`, `centerMm` arrays.
`roomCenterMm` is a placed room's location point; it is not a computed geometric centroid.
Coordinates use model axes in mm rounded to one decimal place.

Exports return `fileName`, `width`, `height`, `sizeBytes`, `viewName` and `viewType` in `data`.
The Python server adds `localPath` after downloading the PNG.
`width` and `height` are pixels; `sizeBytes` is the PNG size in bytes.
See [HTTP endpoints](transport.md#http-configuration) for direct image retrieval.

## Heartbeats and legacy reports

Heartbeat JSON contains `processId`, `revitVersion`, `documentTitle`, `documentPath` and `updatedUtc`.
`updatedUtc` is an ISO 8601 UTC timestamp.
The add-in writes every five seconds and the file client ignores records older than 60 seconds.
HTTP instance discovery uses `/health` instead of heartbeat files.

Legacy snapshots and `views-dump` jobs are accepted by the add-in but are not MCP tools.
Snapshots use the [Snapshot contract](../src/RevitModelMcp.Core/Models/Snapshot.cs), without the command response envelope.
View dumps use `command:"views-dump"`, `status`, timestamps, `responder`, progress counts and a `views` list from [ViewDumpReport](../src/RevitModelMcp.Core/Models/ViewDumpReport.cs).
They track opened/closed views and restoration of the original view.
Legacy formats have no schema version and should not be treated as a stable external API.

## Document action responses

The read command `documents` returns an array of open document states, including background documents. It does not require an active document or the action gate.

Document lifecycle commands use the normal command response envelope. `open-document` returns `title`, `path`, `isWorkshared`, `isDetached`, `isCentral`, `openedAs`, `active`, `worksetsOpen` and `elapsedMs` in `data`. Open responses also report any suppressed dialogs in the envelope's `dialogsSuppressed` field.

A save, sync or close operation requiring confirmation returns `success:true` and `data.needsConfirmation:true`, `data.confirmationText` and `data.confirmToken`. This response reports a pending operation; it does not mean the operation ran. The follow-up must repeat all arguments and add `confirmToken`. Invalid, consumed, expired or mismatched tokens return an error without making a change. The token expires five minutes after issuance.
