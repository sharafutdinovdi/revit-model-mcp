# Model audit: rule format and snapshot storage

This page records the design decisions for `revit_model_audit` ([#98](https://github.com/sharafutdinovdi/revit-model-mcp/issues/98)).
It answers the three open questions of the epic and blocks the implementation issues [#212](https://github.com/sharafutdinovdi/revit-model-mcp/issues/212) to [#216](https://github.com/sharafutdinovdi/revit-model-mcp/issues/216).
Nothing here is implemented yet. Tool names and limits below are the contract the first slice builds to.

## Goals and non-goals

Goals:

- One tool runs a stated rule set against one model or a folder of models and returns findings.
- Every finding names the rule, the elements and the view or read it was evaluated on, so the reader does not rerun the check.
- The rule set is a file taken from the BIM execution plan, not code.
- Each audit is kept, and the next one reports what is new, fixed and unchanged.
- The audit never changes a model.

Non-goals:

- No geometric clash detection. That stays in Navisworks or Solibri.
- No rule scripting language. A rule picks a check type and gives thresholds.
- No automatic fixing. A finding can feed the existing bulk update actions, but the audit does not call them.
- No claim of ISO 19650 compliance from model data alone, as in the [issue register skill](https://github.com/sharafutdinovdi/revit-model-mcp/blob/main/skills/iso19650-issue-register/SKILL.md).

## Users

| User | Needs |
| --- | --- |
| BIM coordinator | A weekly report across the project models, what changed since last week, and a finding list to send to model authors (BCF). |
| Project manager | A short summary per model: severity totals and trend. A check before a data drop. An acceptance check of a subcontractor model against the EIR. |

Both read the same report. The coordinator also edits the rule file.

## Rule file

A rule file is YAML. JSON with the same keys is accepted. The file is validated against a JSON Schema shipped with the server, and an invalid file fails before any model is read.

```yaml
schema: 1
name: Tower A BEP checks
extends: [model-health]          # built-in packs, see below
references:
  BEP: "Tower A BIM execution plan, rev C"
rules:
  - id: BEP-4.2-mark
    ...
```

Each rule has these keys.

| Key | Meaning |
| --- | --- |
| `id` | Unique in the file. Stable, because deltas match on it. |
| `title` | One line shown in the report. |
| `severity` | `critical`, `major`, `minor` or `info`, the same values as the issue register. |
| `scope` | Selectors that limit the population: `categories`, `level`, `workset`, `view`, `phase`, `family`, `type_name`. They map to the filters of `revit_query_elements`. |
| `check` | The check type and its parameters, see below. |
| `threshold` | Optional. `max_count`, `max_percent` or `min_percent` that the measured value is compared with. Default is zero findings. |
| `reference` | Optional. `{source, clause, text}`. Copied into `requirement_source` and `requirement` of the register. |
| `recommendation` | Optional text for the model author. |
| `enabled` | Optional, default `true`. Lets a project switch off an inherited rule. |

### Check types

The first slice supports a closed set. Adding a type is a code change, not a file change.

| Type | Reads | Result |
| --- | --- | --- |
| `parameter_filled` | parameter fill | Elements with an empty or missing value. |
| `parameter_in` | element query | Elements whose value is not in `values`. |
| `parameter_matches` | element query | Elements whose value does not match a regular expression. |
| `name_matches` | element query, views, sheets | Levels, grids, views or sheets whose name does not match a pattern. |
| `count` | aggregate | A category or warning count outside the threshold. |
| `model_health` | health, warnings, family audit | Warning, in-place family and import counts against the threshold. |
| `coordinates` | shared coordinates, link datums | Offsets from an expected value, in millimetres. |
| `sheets` | sheets reader ([#212](https://github.com/sharafutdinovdi/revit-model-mcp/issues/212)) | Sheets without title block, unplaced views, revision gaps. |
| `worksets` | worksets reader ([#213](https://github.com/sharafutdinovdi/revit-model-mcp/issues/213)) | Elements on default workset, unexpected owners. |

### Naming categories and parameters without the UI language

Revit readers today take names in the model's language, for example `Walls` or `Стены`. A rule file that works for an English and a Russian model must not contain either.

Decision: a rule names a category by `builtin` id and a parameter by `builtin` id, shared `guid`, or a list of `names` as the last resort.

```yaml
scope:
  categories: [{builtin: OST_Walls}]
check:
  type: parameter_filled
  parameter: {builtin: ALL_MODEL_MARK}
```

```yaml
parameter: {guid: "a6a7b4c2-0000-0000-0000-000000000000"}   # a shared parameter
parameter: {names: [Classification Code, Код классификации]}   # project parameter, no stable id
```

The server resolves ids to the model's names with `revit_list_catalog` before the first read, and caches the mapping per model. The catalog rows gain a `builtin` field for this, which is part of [#214](https://github.com/sharafutdinovdi/revit-model-mcp/issues/214). A rule that cannot be resolved is reported as `not assessed` with the reason, never as passed.

Rejected: plain UI names only (breaks on mixed-language projects), and importing Revit Model Checker files (their format ties checks to localized names too; revisit as an import path, see open questions).

## Example rules

Required mark on doors and walls, with a threshold, from clause 4.2 of the BEP:

```yaml
- id: BEP-4.2-mark
  title: Doors and walls carry a Mark
  severity: major
  scope: {categories: [{builtin: OST_Doors}, {builtin: OST_Walls}]}
  check: {type: parameter_filled, parameter: {builtin: ALL_MODEL_MARK}}
  threshold: {min_percent: 100}
  reference: {source: BEP, clause: "4.2", text: "All doors and walls shall be marked."}
  recommendation: Fill Mark on every listed element.
```

Level naming, from the file naming section of the EIR:

```yaml
- id: EIR-6.1-level-names
  title: Level names follow the project convention
  severity: minor
  scope: {categories: [{builtin: OST_Levels}]}
  check:
    type: name_matches
    pattern: '^L\d{2} - .+$'
  reference: {source: EIR, clause: "6.1"}
```

Warnings budget, as a model health rule:

```yaml
- id: HEALTH-warnings
  title: Warnings stay under the agreed limit
  severity: major
  check: {type: model_health, metric: warnings_total}
  threshold: {max_count: 200}
  recommendation: Resolve the largest warning groups first.
```

The three rules form one complete file that the first audit slice runs as is:

```yaml
schema: 1
name: Tower A checks
extends: [model-health]
references:
  BEP: "Tower A BIM execution plan, rev C"
  EIR: "Tower A employer's information requirements"
rules:
  - id: BEP-4.2-mark
    title: Doors and walls carry a Mark
    severity: major
    scope: {categories: [{builtin: OST_Doors}, {builtin: OST_Walls}]}
    check: {type: parameter_filled, parameter: {builtin: ALL_MODEL_MARK}}
    threshold: {min_percent: 100}
    reference: {source: BEP, clause: "4.2"}
  - id: EIR-6.1-level-names
    title: Level names follow the project convention
    severity: minor
    scope: {categories: [{builtin: OST_Levels}]}
    check: {type: name_matches, pattern: '^L\d{2} - .+$'}
    reference: {source: EIR, clause: "6.1"}
  - id: HEALTH-warnings
    title: Warnings stay under the agreed limit
    severity: major
    check: {type: model_health, metric: warnings_total}
    threshold: {max_count: 200}
```

## Built-in rule packs

Packs are rule files shipped inside the server package and referenced by name in `extends`. A project file can override `severity`, `threshold` and `enabled` of an inherited rule by repeating its `id`. It cannot change the `check`.

| Pack | Content |
| --- | --- |
| `model-health` | Warning count, in-place families, imported CAD, closed worksets, links not loaded. |
| `coordination` | Shared coordinates present, link datum offsets within tolerance. |
| `sheets-and-views` | Sheets without title block, views not on sheets, unnamed or default-named views. |
| `iso19650-information` | Level of information need as required parameters, naming fields, classification codes. Every rule is marked as a review default until a project overrides it with its own EIR or BEP clause. |

## Running over a live document and over snapshots

Two evaluators share one rule engine. The engine turns a rule into a list of reads, evaluates it over their results and produces findings.

- **Live document.** The audit calls the existing reader tools on the active or a named document. Each rule runs on the view or population in its `scope`, and the finding records that view and the paging that was covered.
- **Stored snapshots.** The audit evaluates the rule over a saved snapshot, with no Revit running. Only rules whose reads the snapshot contains can run. The rest are reported as `not assessed`, never passed.

For many models, `revit_batch_start` collects snapshots in the background worker. The ruleset is turned into `parameter_rules` and the other section requests of the snapshot, so a batch audit reads only what the rules need. A model is evaluated when its snapshot arrives.

Evidence is the same in both modes: `rule`, `severity`, `model`, `element_ids` (up to 500), measured value, threshold, population count and the read that supplied it. `skipped` from any read makes the rule `partial`, and the finding says so.

## Snapshot storage and versioning

### Where snapshots live

Decision: JSON files on the machine that runs the Python server, in a directory set by `REVIT_MCP_AUDIT_DIR`, default `~/.revit-model-mcp/audits` (`%LOCALAPPDATA%\RevitModelMcp\audits` on Windows).

```text
<audit dir>/<project>/<model key>/<UTC timestamp>.json
```

- `<project>` comes from the folder or from a `project:` key of the rule file.
- `<model key>` is a short hash of `passport.centralPath`, or of the normalized `source.path` when the model is not workshared, plus the sanitized title for readability. A renamed file keeps its history if the central path is unchanged.
- Files are written once and never overwritten, like `revit_batch_fetch`. A new audit adds a new file.
- Retention: the last 26 snapshots per model, about half a year of weekly audits. The first snapshot is kept as a baseline. Older files are removed only by an explicit `revit_model_audit(prune=true)` call, never silently.

Rejected: a database (one more dependency and a binary format for data that is read whole), storing results inside the Revit model through extensible storage (it would modify the model and break the read-only guarantee), and storing on the Revit workstation (the server and the report run on the client).

### Schema evolution from version 1

`revit_model_snapshot` returns `schemaVersion: 1` with `source`, `passport`, `warnings`, `families`, `parameterFill` and `skipped`. `revit_build_report` reads it today.

Decision: version 2 is additive. It keeps every v1 field and adds optional sections:

- `sheets` and `unplacedViews` from #212.
- `worksets` with counts and owners from #213, replacing the short list in `passport.worksets`.
- `audit`: the rule file name and SHA-256, the pack versions, the evaluation time and the findings with their evidence.

Rules for readers:

- A reader ignores unknown fields and refuses a `schemaVersion` it does not know.
- `revit_build_report` accepts v1 and v2 files in the same folder.
- A missing section means the rule is `not assessed`, not passed, so old v1 history stays usable for the sections it has.
- A change that removes or renames a field raises the major version and gets a migration note in `docs/tools.md`. A breaking change needs its own issue.

### Deltas for #215

Findings are matched between the previous and the current audit of one model by the key `(rule id, element id)`. A model-level finding without elements uses `(rule id, "")`.

| Class | Meaning |
| --- | --- |
| new | The key is in the current audit only. |
| fixed | The key was in the previous audit and is gone, and the rule ran and was assessed this time. |
| unchanged | The key is in both. |

A rule that was `not assessed` in either run produces no `fixed` entries, so a skipped read never looks like an improvement. A changed rule file hash is shown in the report header, because a new threshold changes results without a model change. Element ids are stable inside one model but not across a model rebuild; when `passport.versionGuid` history shows a different central model, the delta is not computed and the report says so.

The delta fits the existing `Changes` sheet of `revit_build_report` and adds counts per class and per severity.

## Outputs

- **XLSX.** The finding fields match the `findings` input of `revit_build_report` (`model`, `severity`, `rule`, `element_ids`, `recommendation`), so the Summary and Findings sheets are reused. The audit adds columns for threshold and measured value in the same sheet.
- **Issue register.** A finding maps to an `issues` item of `revit_issue_register`: `title`, `category` from the rule pack, `finding` from the evidence, `severity`, `requirement_source` and `requirement` from `reference`. The register keeps its 60 issue and 25 snapshot limits.
- **HTML.** A single self-contained file with a summary per model, findings grouped by severity and the view images the capture reads provide ([#216](https://github.com/sharafutdinovdi/revit-model-mcp/issues/216)).
- **BCF.** BCF 2.1 export for findings that go back to the model author: one topic per finding with title, description, priority from severity, the Revit element ids as components and the captured image as the viewpoint snapshot. A camera is added only when the evaluation view is a 3D view.

## Performance limits

- Reads run in the existing serialized channel. The audit groups rules by read, so a parameter fill is called once per category set and not once per rule.
- Existing caps stay: 1-20 categories and 1-30 parameters per fill check, 200 element ids per warning group, paged queries.
- A rule file has at most 200 rules.
- A single model audit has a 10 minute budget; a rule that exceeds its share is reported as `partial`. Batch deadlines from [batch collection](../batch.md) apply per model.
- Image capture follows the register limits and never blocks the findings.
- A very large model is audited over snapshots: collection is one worker pass, and rules are evaluated on the client without more Revit calls.

## Read-only guarantee

- The audit uses read tools and the worker-only snapshot command. It never starts a transaction, saves or synchronizes.
- It does not call action tools, so it works in read-only mode and for unattended runs ([#26](https://github.com/sharafutdinovdi/revit-model-mcp/issues/26)).
- Its only writes are new files on the client: snapshots, reports, BCF. Existing files are never overwritten.
- Stored snapshots contain model paths and element ids. They follow `REVIT_MCP_REDACT_PATHS` in the same way as fetched batch snapshots.

## Open questions

- Whether to offer an import of Revit Model Checker files as a rule source, and how much of that format maps to the closed set of check types.
- Whether `builtin` ids should be accepted directly by the existing readers, which would remove the catalog resolution step.
- Whether finding keys should use `UniqueId` instead of element id, so deltas survive a model rebuild. It needs an additive field in the readers.
- How long a project wants snapshots kept. The 26 file default is a guess to be checked with the first users.
- Whether BCF needs a camera for plan views, or the image alone is enough for the viewers in use.
