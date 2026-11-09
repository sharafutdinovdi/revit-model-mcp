---
description: "Model MCP read-only model audit checks, result interpretation, and evidence for coordination reviews."
---

# Model audit

`revit_model_audit` evaluates one live model with a stated rule file. It reads the model and writes local results. It never changes Revit elements. `revit_audit_report` builds reports later without Revit.

```json
{"rules": "pack:model-health", "compare_with": "previous", "save": true}
```

Paths refer to the machine running the Python server. Use `rules_inline` instead of `rules` to send a rule object. The default is `pack:model-health`. Invalid rules fail before any model read.

## Rule files

YAML and JSON use the same fields. Up to 200 resolved rules are allowed. Inheritance resolves builtin pack names. Repeating an inherited id can change only `severity`, `threshold` and `enabled`. Repeated descriptive fields must match the inherited rule exactly.

```yaml
schema: 1
name: Tower A review
extends: [model-health]
references:
  BEP: Tower A execution plan, revision C
rules:
  - id: HEALTH-warnings
    threshold: {max_count: 100}
  - id: BEP-mark
    title: Walls carry a Mark
    severity: major
    scope:
      categories: [{builtin: OST_Walls}]
    check:
      type: parameter_filled
      parameter: {names: [Mark, Марка]}
    threshold: {min_percent: 100}
    reference: {source: BEP, clause: "4.2", text: Fill Mark on walls.}
    recommendation: Fill Mark on the listed elements.
```

Categories and parameters resolve through `revit_list_catalog`. Use `builtin`, shared parameter `guid`, or localized `names` fallbacks. Unresolved identifiers are `not_assessed`. Builtin catalog metadata depends on the connected add-in version.

| Pack | Review defaults |
| --- | --- |
| `model-health` | Warning budget of 200, in-place families, imported CAD, unloaded RVT links, closed worksets. |
| `coordination` | Shared coordinates and link offsets with a 10 mm tolerance. Presence is not assessed in this slice. |
| `sheets-and-views` | Missing title blocks, unplaced views, default view names. |
| `iso19650-information` | Required description, Mark and classification code on walls and doors. These are review defaults. Replace them with project EIR or BEP requirements. They do not prove ISO 19650 compliance. |

Checks include `parameter_filled`, `parameter_in`, `parameter_matches`, `name_matches`, `count`, `model_health`, `coordinates`, `sheets` and `worksets`.
The worksets check supports metric `closed` through the paged user-workset catalog.
Non-workshared models and older add-ins without open-state metadata return `not_assessed`.
Closed worksets and bounded count scans produce incomplete evidence and prevent a full-population pass.
Thresholds accept one of `max_count`, `max_percent`, or `min_percent`.
Omitted thresholds allow zero findings.
For parameter fill, use `min_percent`.
For `coordinates` metric `link_offset_mm`, `max_count` is the tolerance in millimetres.

Threshold units are validated after inheritance, before any model read.
`count`, `coordinates` and `worksets` accept only `max_count`.
Percentage thresholds require a supported population conversion, as provided by name, parameter-value, model-health and sheet checks.

Scopes accept categories, level, workset, view, phase, family and type_name. Parameter fill supports categories, level, workset and view only. Regex patterns are limited to 200 characters. Nested quantifiers are rejected. Each match has a two second limit.

`warnings_total` has no scoped reader.
A warning count rule with a nonempty scope returns `not_assessed` with a reason and no measured value.

## Evidence and statuses

| Status | Meaning |
| --- | --- |
| `passed` | Available evidence satisfies the threshold. |
| `failed` | Available evidence violates the threshold. |
| `partial` | A paging, time or skipped-read limit left incomplete evidence. Do not treat this as a pass. |
| `not_assessed` | Disabled rule, unsupported check, unresolved reference or unavailable reader. The reason is included. |

Each rule records measured value, threshold, population, read names, skipped entries and up to 500 evidence element ids. `element_ids_truncated` identifies limited samples. Identical reads are cached for one run. Queries cover at most 20 pages of 500 elements per rule. A run has a ten minute budget.

Live audits capture the responding process and document identity at the initial snapshot read.
Every subsequent read targets that process and the resolved document path, or its title for an unsaved document.
Responses with missing or changed identity return `not_assessed`; their measurements are not used.
Identity validation precedes path redaction.

The Python `SnapshotReadSource` adapter accepts a loaded snapshot v1 dictionary.
`revit_model_audit` evaluates a local directory through `snapshots_dir` without contacting Revit.
Snapshot counts, warnings and matching parameterFill rows can be assessed.
V1 has no element rows, sheets, links or stable catalog identifier mappings.
Those checks are `not_assessed`.

View `name_matches` rules scoped to `categories: [{builtin: OST_Views}]`, including `VIEWS-names`, use `revit_list_views` instead of the category catalog. They check view names across all pages. Current add-ins return all non-template views in one response. View templates are excluded by default; an applied template name does not exclude a view. Set `scope.include_templates: true` to request templates. The current live reader cannot include them and returns `not_assessed` for that request. Snapshot v1 has no view list. An unavailable view reader produces `not_assessed`.

Sheets use the `revit_list_sheets` contract. An add-in that returns `unknown command` produces `not_assessed`. No sheets tool is added in this slice.

## History and deltas

Set `REVIT_MCP_AUDIT_DIR` to choose a history directory. Otherwise audits share the existing server state root, next to `update.json`:

- macOS: `~/Library/Caches/revit-model-mcp/audits`.
- Linux: `$XDG_CACHE_HOME/revit-model-mcp/audits`, default `~/.cache/revit-model-mcp/audits`.
- Windows: `%LOCALAPPDATA%/revit-model-mcp/audits`.

Files are `<model key>/<UTC timestamp>.json`. The key hashes central path or source path together with title. `REVIT_MCP_REDACT_PATHS` redacts stored model paths and returned paths. Files are created once and never overwritten. There is no automatic pruning.

`compare_with="previous"` selects the latest strictly earlier `collectedAtUtc` for the same key and versionGuid family.
`evaluatedAtUtc` records evaluation time, not evidence freshness.
Offline results retain the snapshot collection time.
Live results retain the initial snapshot collection time, or the evaluation time when that reader provides no collection time.
Missing, equal or later collection times cannot establish a baseline; deltas remain unassessed.
Older history files without collection timestamps are not eligible baselines.
A result path selects an explicit baseline with the same collection-time requirement.
`"none"` disables comparison.
Missing or different version identity prevents deltas.
Failed evidence is matched by rule id and element id; model-level findings use an empty element id.
Deltas are `new`, `fixed` or `unchanged`.
Missing, truncated or incomplete evidence cannot prove a fix.
`not_assessed` never yields a fixed item.
`summary.rulesChanged` flags a changed rules hash.

## Batch snapshot audits

Collect a folder with `revit_batch_start`, wait for a terminal result with `revit_batch_status`, then download its snapshots with `revit_batch_fetch`.
Pass the fetch response's complete `models` array as `batch_models`.
Each inventory item uses `path`, `status` and `localPath`, with optional `error` or `reason`.
Relative `localPath` values resolve inside `snapshots_dir`.
Snapshot files must be direct directory members, not symlinks.

```json
{
  "snapshots_dir": "/reports/current",
  "rules": "pack:model-health",
  "batch_models": [
    {"path": "Tower A.rvt", "status": "completed", "localPath": "snapshot_0000.json"},
    {"path": "Tower B.rvt", "status": "failed", "error": "Model could not be opened"}
  ],
  "compare_with": "previous",
  "save": true
}
```

Without `batch_models`, every direct JSON file in the directory represents a model.
Directory discovery cannot identify models that failed before producing a snapshot.
An inventory retains failed, cancelled and missing models as report rows with reasons.
Invalid snapshots remain failed rows.
An empty directory is accepted only with a nonempty inventory.

Each assessed model uses the same rule evaluator, immutable history key and comparison as a live audit.
Fetched snapshots contain a `historyKey` computed from the original model identity before path redaction.
The offline audit uses this key for both model separation and history lookup.
A snapshot without `historyKey` requires a model path with a directory.
A basename-only identity without a key produces a failed model row with an explanatory reason.
Batch comparison accepts `previous` or `none`.
The previous result is selected independently for each model, not by model title.
No baseline or unavailable version identity leaves deltas unassessed.
Absent and incomplete evidence cannot prove a fix.
Rule status totals separate `not_assessed` from `passed`, `failed` and `partial`.
Models without usable snapshots count every rule as `not_assessed`.
Collection failures and reader omissions remain in `skipped` and `skippedCount`.

With `save=true`, each model audit is stored in its normal history directory.
The combined result is stored under `batches` in the audit history root, and `savedTo` identifies its JSON file.
`save=false` returns the combined result without writing history or a combined file.
Pass the combined `savedTo` to `revit_audit_report` for XLSX and HTML.
The Models section contains one row per model, rule status counts, new, fixed and unchanged findings, comparison availability, failure reasons and the per-model history path.
Findings and Deltas include the model identifier.
Rules and Skipped retain the single-model report contracts.
Offline batch audits do not capture live images. Reports retain any stored per-model evidence and keep missing evidence unassessed.
Batch results accept `xlsx`, `html` and `both`. `bcf` and `all` are refused before any output is written; export each model's saved audit separately for BCF.

## Reports and issue registers

```json
{"audit": "/reports/audit.json", "output": "/reports/review", "format": "both"}
```

`format` accepts `xlsx`, `html`, `both`, `bcf` or `all`.
`both` writes XLSX and HTML; `all` also writes BCF 2.1.
The output stem gets the selected extensions.
Parent directories must exist.
Existing outputs and evidence PNG files are refused.
HTML contains all content in one file and loads no external assets.

| Sheet or section | Content |
| --- | --- |
| Summary | Model, evaluation time, rule provenance and status totals. |
| Findings | Rule, severity, status, measured, threshold, population, element ids, recommendation and reference. |
| Deltas | Rule, element id, delta status and severity, when a comparison exists. |
| Rules | Resolved definitions, including check, scope and enabled state. |
| Skipped | Reader omissions and total skipped count. |

`audit.to_register_issues(result)` maps failed audit findings to `revit_issue_register` issues. It maps title directly, category from the pack, finding from measured value and threshold, severity directly, and requirement_source and requirement from reference. Review the project requirements before creating a register. The register's existing limits still apply.

## Evidence images and BCF

Set `capture_evidence=true` on `revit_model_audit` to capture images for non-passing findings with element IDs.
Capture uses the existing `capture-elements` path over local or SSH transport and the audit's pinned process and document.
Temporary Revit views roll back.
Evaluation remains read-only.
Capture runs after evaluation with a separate 210-second budget and at most 25 images, ordered by severity.
Each capture uses at most 500 element IDs.
The saved finding's `evidence` contains the base64 PNG, camera, component metadata, status and skipped diagnostics.
Capture requires `save=true`; `capture_evidence=true` with `save=false` is refused before any model read.
The MCP response omits PNG bytes and returns `evidence.image` with the capture status and diagnostics.
Report generation reads full images from the audit JSON identified by `savedTo`.
Count limits, timeouts, transport failures, missing elements and skipped reads remain in that finding's evidence and the result's `skipped` and `skippedCount`.
Capture failure does not discard a finding or change its assessment into a pass.
Without capture, report generation remains entirely offline.

HTML embeds PNG data URLs.
XLSX links to new sibling `<output>.evidence-N.png` files; distribute these with the workbook.
BCF embeds snapshots in topic folders with `markup.bcf` and `viewpoint.bcfv`.
The BCF archive uses the `.bcfzip` extension.
All non-passing findings become topics with title, description, severity priority and rule, severity and assessment labels.
Missing snapshots and cameras are explicit in topic descriptions.
No camera is fabricated for historical audits without camera evidence.
Reports retain incomplete and not-assessed checks, not just failed rules.

BCF 2.1 targets the common file exchange format.
[BIMcollab Zoom explicitly supports reading and writing 2.1](https://helpcenter.bimcollab.com/en/articles/347383-why-can-t-others-read-the-bcf-files-i-create-with-bimcollab-zoom-application).
[Revizto documents BCF file exchange](https://help.revizto.com/hc/en-us/articles/360001575975-Working-with-BCF-files), and [Solibri documents BCF collaboration](https://help.solibri.com/hc/en-us/sections/1500000775082-Collaboration-with-BCF).
BCF 3.0 is not required for this file exchange.

Components include `OriginatingSystem` and `AuthoringToolId` with the Revit element ID.
The add-in reads the existing `BuiltInParameter.IFC_GUID` without setting or generating values.
Valid compressed IFC GUIDs also appear as `IfcGuid`.
Absent GUIDs retain Revit identity; IFC-only viewers cannot match those components.
The GUID must match the IFC loaded in the viewer.
Camera positions and orthogonal scale use metres.
`camera_coordinate_base="shared"` is the default for `revit_model_audit` and `revit_capture_elements`.
It matches `revit_export(format="ifc")`, which leaves `SitePlacement` unset and uses the Autodesk IFC exporter's shared-coordinate default in Revit 2022-2027.
The year-specific exporter sources initialize `SiteTransformation` to `Shared`: [2022](https://github.com/Autodesk/revit-ifc/blob/Release_22.x.x/Source/Revit.IFC.Export/Utility/ExportOptionsCache.cs), [2023](https://github.com/Autodesk/revit-ifc/blob/Release_23.x.x/Source/Revit.IFC.Export/Utility/ExportOptionsCache.cs), [2024](https://github.com/Autodesk/revit-ifc/blob/Release_24.x.x/Source/Revit.IFC.Export/Utility/ExportOptionsCache.cs), [2025](https://github.com/Autodesk/revit-ifc/blob/Release_25.x.x/Source/Revit.IFC.Export/Utility/ExportOptionsCache.cs), [2026](https://github.com/Autodesk/revit-ifc/blob/Release_26.x.x/Source/Revit.IFC.Export/Utility/ExportOptionsCache.cs) and [2027](https://github.com/Autodesk/revit-ifc/blob/Release_27.x.x/Source/Revit.IFC.Export/Utility/ExportOptionsCache.cs).
Shared cameras apply the inverse active project location total transform to the internal position, direction and up vector.
Translation applies only to the position; direction and up retain unit length.
The orthogonal camera frames the captured bounds with the capture view's direction and up vector.
No clipping planes are emitted.
Set `camera_coordinate_base="internal"` when pairing evidence with an IFC exported separately using internal origin and project north.
`revit_export` does not forward arbitrary `SitePlacement` options; passing `options={"SitePlacement":3}` does not select internal origin.
Use the same active project location and coordinate base for capture and IFC export.
Existing saved audits retain their original cameras; recapture evidence after changing the coordinate base or project location.
Model-level findings without element IDs have no component viewpoint.

Official buildingSMART 2.1 XSD fixtures are vendored unchanged for offline validation tests only.
The [fixture NOTICE](https://github.com/sharafutdinovdi/revit-model-mcp/blob/main/server/tests/fixtures/bcf-2.1/NOTICE) records source commit, SHA-256 values, attribution and CC BY-ND 4.0 terms.
Rewriting hooks and formatters exclude these fixtures.
They are excluded from runtime distributions.
Only the tests use an XML schema validator; the BCF writer uses standard-library ZIP and XML.

The `pre_issue_check`, `weekly_coordinator_report` and `subcontractor_model_acceptance` prompts cover the roadmap scenarios.
They require agreed project rules, preserve incomplete results and generate reports with evidence.
