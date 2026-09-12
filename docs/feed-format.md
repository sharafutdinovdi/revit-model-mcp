# Feed format

The v0.1.0 protocol uses UTF-8 JSON and case-sensitive field names.
It has no `schemaVersion` field; the package version identifies the documented contract.
The standalone add-in does not write a feed under `%LOCALAPPDATA%\RevitDevLoader`.
Its default channel is `%LOCALAPPDATA%\RevitModelMcp`.

## Files and directories

| Location | Contents |
|---|---|
| Channel directory | `trigger.txt`, `mcp_<uuid>.tmp`, `response_<timestamp>_<command>.json`, `view_<timestamp>_<id>.png`, `instance_<processId>.json` and heartbeat `.tmp` files |
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
`targetDocument` matches a case-insensitive substring of the active document title or path basename in the add-in.
An HTTP endpoint also rejects jobs addressed to another process.

| MCP arguments | JSON fields |
|---|---|
| `document` | `targetDocument` |
| `element_id` | `id` for `element-details`; `elementId` for `set-parameter` |
| `element_ids` | `elementIds` |
| `view_type`, `name_contains` | `viewType`, `nameContains` |
| `pixel_size` | `pixelSize`; the server also sets `zoomToFit:true` |
| `group_by`, `sum_field` | `groupBy`, `numericField` |
| `type_name`, `area_scheme`, `parameter_filters` | `type`, `areaScheme`, `parameterFilters` for query filters; placement uses `typeName` |
| `sort_field`, `sort_direction` | `sort:{field,direction}` |
| `include_geometry`, `include_elements`, `warning_text` | `includeGeometry`, `includeElements`, `warningText` |
| `source_id`, `source_name` | `sourceId`, `sourceName` |
| `dx_mm`, `dy_mm`, `dz_mm`, `x_mm`, `y_mm` | `dxMm`, `dyMm`, `dzMm`, `xMm`, `yMm` |
| `start_mm`, `end_mm`, `wall_type`, `height_mm`, `rotation_deg` | `startMm`, `endMm`, `wallType`, `heightMm`, `rotationDeg` |

`save_to` and timeouts are client options, not job fields.
`parameterFilters` entries contain `parameter`, `operator` and an optional `value`.
Numeric filter values use mm for lengths, m2 for areas and m3 for volumes.
Other measurable filter values use the document's display units; unmeasurable doubles use internal values.
Returned query fields include `value`, optional `numericValue`, `unit`, `hasValue` and `source`.
Aggregation uses these numeric values; inspect the returned unit before interpreting a sum.
See the [tool tables](../README.md#tools) for argument defaults and units.
The [job builders](../server/revit_model_mcp/universal_jobs.py) and [parser](../src/RevitModelMcp.Core/Control/ControlJobParser.cs) define the request contract.

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
| `data.closestFamilies` | Similar loaded family names with categories when a family is missing |
| `data.parameterScope` | `instance` or `type` after `set-parameter`; type edits affect every instance using that type |

Transport errors and target mismatches can occur before the action executor and omit these fields.
See [response models](../src/RevitModelMcp.Core/Models/ReadCommandModels.cs) and [action models](../src/RevitModelMcp.Core/Control/ActionJobParser.cs).

## Geometry and image exports

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
