---
description: "Run daily Revit QA, warnings triage, parameter checks, issue registers, sheet sets, link datum checks, and batch audits with Model MCP."
---

# AI model coordination in Revit

Model MCP supports AI-assisted coordination of live Revit models through read tools, repeatable prompts, and evidence-based reports.
Daily QA can inspect warnings, parameter completeness, links, coordinates, and families before any edit.
Results retain element IDs and incomplete-check diagnostics for review.

## Run a coordination review

1. Establish the target with `revit_list_instances`, `revit_documents`, and `revit_document_info`.
2. Enable the shared read-only lock for an inspection-only session.
3. Choose a workflow below and provide the required categories, parameter names, or model paths.
4. Report counts, element IDs, `skipped`, and `skippedCount`; mark incomplete checks as incomplete.
5. Review a concrete change plan before any edit, then read the result back.

| Task | Tools | Prompt or public skill |
| --- | --- | --- |
| Daily QA | `revit_model_health`, `revit_links_status`, `revit_shared_coordinates`, `revit_family_audit` | `daily_qa`; [daily QA skill](../../skills/revit-daily-qa/SKILL.md) |
| Warnings triage | `revit_list_warnings`, `revit_element_details`, `revit_capture_elements` | `warnings_triage` |
| Parameter fill | `revit_list_catalog`, `revit_parameter_fill_check`; approved edits with `revit_update_parameters` | `parameter_fill_report` |
| Issue register | `revit_issue_register` with findings, references, and snapshots | [ISO 19650 issue register skill](../../skills/iso19650-issue-register/SKILL.md) |
| Sheet sets | `revit_list_sheets`, `revit_create_sheet`, `revit_place_views_on_sheet` | `sheet_set`; [sheet set skill](../../skills/revit-sheet-set/SKILL.md) |
| Link datum checks | `revit_compare_link_datums`; approved corrections with `revit_align_link_datums` | [Coordinator skill](../../skills/revit-model-coordinator/SKILL.md) |
| Batch audits | `revit_batch_start`, `revit_batch_status`, `revit_batch_fetch`, `revit_build_report` | `batch_audit` |

## Example requests

- **Run daily QA. Compare with the previous snapshot and report incomplete checks.**
- **Triage warnings by impact. Include affected element IDs and the next review action.**
- **Check door Fire Rating fill. Separate empty parameters from missing parameters. Propose changes only.**
- **Compare host grids and levels with the structural link. Report differences before alignment.**
- **Audit the agreed folder of models and create a new Excel report. Retain failures for each model.**

An issue register records findings; it is not an automatic certification of ISO 19650 compliance.
Sheet creation and view placement use separate undo entries under current batching limits.
Batch reports depend on collected snapshots and must retain collection failures.

## Further reading

- [Prompts and arguments](../prompts.md)
- [Tool reference](../tools.md)
- [Batch collection](../batch.md)
- [Read-only, Undo, and privacy](safety.md)
