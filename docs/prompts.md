# Prompts and coordinator guide

The server exposes MCP prompts for reviewing one open Revit model or auditing a batch of models.
Each prompt is a read-only workflow.
It does not call action tools.
The prompts refer to the guide resource at `revit://guides/coordinator`.
Read that resource for the full review sequence, interpretation rules, and reporting format.

| Prompt | Arguments | Purpose | Requested output |
| --- | --- | --- | --- |
| `model_overview` | None | Review document identity, health, warnings, links, coordinates, and families. | Findings table with priority, evidence, element IDs, completeness, and next step. |
| `pre_issue_check` | None | Run a read-only hand-over checklist with relevant checks and element analysis. | Pass, fail, or not assessed for each applicable condition, with evidence. |
| `warnings_triage` | None | Group and prioritize model warnings after checking model health. | Prioritized findings with affected element IDs and review or fix next steps. |
| `parameter_fill_report` | `categories` and `parameters`, required comma-separated strings | Check localized category and parameter names, then inspect fill counts. | Per-category and per-parameter filled, empty, and missing counts with sample element IDs. |
| `batch_audit` | Required `output_path`; optional `folder`, `paths`, `parameter_rules`, and `previous_dir` | Collect persistent read-only snapshots across models, inspect them, and build a report. Supply exactly one of `folder` or `paths`; interpret `paths` and `parameter_rules` strings as tool lists. | `.xlsx` report with findings and a short project-manager summary for each model, including failures and incomplete results. |

The guide is also the source for the generated Claude skill at `skills/revit-model-coordinator/SKILL.md`.
Run `python3 build/generate_coordinator_skill.py` after changing the guide.
The generated skill includes the guide verbatim after its frontmatter and usage note.

For Claude Code, copy the `skills/revit-model-coordinator` folder into `~/.claude/skills/`.
For Claude Desktop, zip that skill folder and upload it as a skill.

## ISO 19650 issue register skill

The hand-written [ISO 19650 issue register skill](../skills/iso19650-issue-register/SKILL.md) checks document requirements against the open model and calls `revit_issue_register` with evidence and element snapshots.
For Claude Code, copy `skills/iso19650-issue-register` into `~/.claude/skills/`.
For Claude Desktop, zip that skill folder and upload it as a skill.
