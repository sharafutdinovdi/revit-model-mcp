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

Checks include `parameter_filled`, `parameter_in`, `parameter_matches`, `name_matches`, `count`, `model_health`, `coordinates`, `sheets` and `worksets`. The last is validated but not assessed until #213 supplies a reader. Thresholds accept one of `max_count`, `max_percent`, or `min_percent`. Omitted thresholds allow zero findings. For parameter fill, use `min_percent`. For `coordinates` metric `link_offset_mm`, `max_count` is the tolerance in millimetres.

Scopes accept categories, level, workset, view, phase, family and type_name. Parameter fill supports categories, level, workset and view only. Regex patterns are limited to 200 characters. Nested quantifiers are rejected. Each match has a two second limit.

## Evidence and statuses

| Status | Meaning |
| --- | --- |
| `passed` | Available evidence satisfies the threshold. |
| `failed` | Available evidence violates the threshold. |
| `partial` | A paging, time or skipped-read limit left incomplete evidence. Do not treat this as a pass. |
| `not_assessed` | Disabled rule, unsupported check, unresolved reference or unavailable reader. The reason is included. |

Each rule records measured value, threshold, population, read names, skipped entries and up to 500 evidence element ids. `element_ids_truncated` identifies limited samples. Identical reads are cached for one run. Queries cover at most 20 pages of 500 elements per rule. A run has a ten minute budget.

The Python `SnapshotReadSource` adapter accepts a loaded snapshot v1 dictionary. Load snapshot directories with `snapshot_report.load_snapshots`. Snapshot counts, warnings and matching parameterFill rows can be assessed. V1 has no element rows, sheets, links or stable catalog identifier mappings. Those checks are `not_assessed`. The MCP audit tool addresses a live document; offline callers use the same evaluator directly.

View `name_matches` rules scoped to `categories: [{builtin: OST_Views}]`, including `VIEWS-names`, use `revit_list_views` instead of the category catalog. They check view names across all pages. Current add-ins return all non-template views in one response. View templates are excluded by default; an applied template name does not exclude a view. Set `scope.include_templates: true` to request templates. The current live reader cannot include them and returns `not_assessed` for that request. Snapshot v1 has no view list. An unavailable view reader produces `not_assessed`.

Sheets use the `revit_list_sheets` contract. An add-in that returns `unknown command` produces `not_assessed`. No sheets tool is added in this slice.

## History and deltas

Set `REVIT_MCP_AUDIT_DIR` to choose a history directory. Otherwise audits share the existing server state root, next to `update.json`:

- macOS: `~/Library/Caches/revit-model-mcp/audits`.
- Linux: `$XDG_CACHE_HOME/revit-model-mcp/audits`, default `~/.cache/revit-model-mcp/audits`.
- Windows: `%LOCALAPPDATA%/revit-model-mcp/audits`.

Files are `<model key>/<UTC timestamp>.json`. The key hashes central path or source path together with title. `REVIT_MCP_REDACT_PATHS` redacts stored model paths and returned paths. Files are created once and never overwritten. There is no automatic pruning.

`compare_with="previous"` finds the newest earlier result for the same key and versionGuid family. A result path selects an explicit baseline. `"none"` disables comparison. Missing or different version identity prevents deltas. Failed evidence is matched by rule id and element id; model-level findings use an empty element id. Deltas are `new`, `fixed` or `unchanged`. Missing, truncated or incomplete evidence cannot prove a fix. `not_assessed` never yields a fixed item. `summary.rulesChanged` flags a changed rules hash.

## Reports and issue registers

```json
{"audit": "/reports/audit.json", "output": "/reports/review", "format": "both"}
```

`format` accepts `xlsx`, `html` or `both`. The output stem gets the selected extensions. Parent directories must exist. Existing outputs are refused. HTML contains all content in one file and loads no external assets.

| Sheet or section | Content |
| --- | --- |
| Summary | Model, evaluation time, rule provenance and status totals. |
| Findings | Rule, severity, status, measured, threshold, population, element ids, recommendation and reference. |
| Deltas | Rule, element id, delta status and severity, when a comparison exists. |
| Rules | Resolved definitions, including check, scope and enabled state. |
| Skipped | Reader omissions and total skipped count. |

`audit.to_register_issues(result)` maps failed audit findings to `revit_issue_register` issues. It maps title directly, category from the pack, finding from measured value and threshold, severity directly, and requirement_source and requirement from reference. Review the project requirements before creating a register. The register's existing limits still apply.

Workset checks (#213), multi-model batch audits (#215), BCF and images are outside this slice.
