# Prompts and coordinator guide

The server exposes four MCP prompts for reviewing one open Revit model.
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

The guide is also the source for the generated Claude skill at `skills/revit-model-coordinator/SKILL.md`.
Run `python3 build/generate_coordinator_skill.py` after changing the guide.
The generated skill includes the guide verbatim after its frontmatter and usage note.

For Claude Code, copy the `skills/revit-model-coordinator` folder into `~/.claude/skills/`.
For Claude Desktop, zip that skill folder and upload it as a skill.
