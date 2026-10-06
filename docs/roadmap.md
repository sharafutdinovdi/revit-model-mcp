# Roadmap

Review date: 2026-10-06.

## How work ships

Releases go out weekly on Monday (Europe/Moscow).
Each Monday release has a milestone named by its date, and unscheduled work lives in `Backlog`.
An urgent fix can ship on another day with the `hotfix-release` label on the release pull request.
The roadmap states direction, not versions: what lands in a release is what is merged by then.

## Shipped

The project works with the Revit session as a whole, not only with the open document ([#160](https://github.com/sharafutdinovdi/revit-model-mcp/issues/160)). A client drives Revit the way a coordinator does, on Revit 2022-2027.

- Session control: tabs, views, selection, open settings and new documents ([#154](https://github.com/sharafutdinovdi/revit-model-mcp/issues/154)).
- C# scripts as an ordinary action, refused in read-only mode ([#153](https://github.com/sharafutdinovdi/revit-model-mcp/issues/153)).
- Multi-model processing in the interactive session: open, change, export or save, close ([#159](https://github.com/sharafutdinovdi/revit-model-mcp/issues/159)). The batch collector stays read-only.
- Deliverables: PDF, DWG, IFC and schedule CSV ([#155](https://github.com/sharafutdinovdi/revit-model-mcp/issues/155)), views and sheets ([#158](https://github.com/sharafutdinovdi/revit-model-mcp/issues/158)).
- Model edits beyond single elements: bulk family placement ([#156](https://github.com/sharafutdinovdi/revit-model-mcp/issues/156)), rotate, copy, mirror, type change and bulk parameter update ([#157](https://github.com/sharafutdinovdi/revit-model-mcp/issues/157)).
- Background jobs, element highlight, MEP runs, walls from CAD and the issue register.
- Coordinator checks with evidence: model health, warnings, links, shared coordinates, link datums, parameter fill, family audit, the report workbook and the issue register. Every check names the rule, the elements and the view it was evaluated on.
- Security hardening, delivery through PyPI, the MCP registry, Smithery and WinGet, per-user and per-machine installers, and updates for the server and the add-in.
- Live validation on Revit 2022 to 2027 with the release artifacts, recorded in the [validation evidence](validation.md).

Every action keeps the existing rules: refused in read-only mode, one named undo entry per model change, listed in the activity pane and reported in `summary`.

## Current direction

- Weekly Monday releases from `main`, with the release pull request gated to the release window.
- Hardening of what exists: HTTP host lifecycle ([#25](https://github.com/sharafutdinovdi/revit-model-mcp/issues/25)), a stated policy for unattended runs ([#26](https://github.com/sharafutdinovdi/revit-model-mcp/issues/26)), confirmation before irreversible actions ([#71](https://github.com/sharafutdinovdi/revit-model-mcp/issues/71)), dry-run checks for every bulk action ([#191](https://github.com/sharafutdinovdi/revit-model-mcp/issues/191)) and the robustness umbrella ([#58](https://github.com/sharafutdinovdi/revit-model-mcp/issues/58)).
- Code signing: a certificate for open source builds, so updates do not trigger Revit's unsigned add-in prompt. Until builds are signed, Revit asks once after each add-in update.
- Installer: atomic updates per Revit year ([#31](https://github.com/sharafutdinovdi/revit-model-mcp/issues/31)).
- Model audit is next, see below.

## Next: model audit for coordinators and project managers

One tool, `revit_model_audit`, runs a stated rule set against one or many models and returns one report ([#98](https://github.com/sharafutdinovdi/revit-model-mcp/issues/98)).
Each finding names the rule, the elements and the view it was evaluated on, so a coordinator can hand the report to a client or a subcontractor without rerunning it.

- Rule sets as files: naming conventions, required parameters per category, allowed values, worksets, view and sheet conventions, taken from the project's BIM execution plan.
- Existing checks become audit sections: model health, warnings, links, shared coordinates, link datums, parameter fill and family audit.
- New sections: schedules read as tables, sheets with title block status and revisions, views not placed on sheets, workset and element ownership.
- Change over time: each audit is stored as a snapshot, and the next audit reports what changed since the previous one.
- Many models: audit a folder of models opened in the background and return one summary with a row per model.
- Output a person can open: an HTML or XLSX report with view images, and BCF for findings that go back to the model author.
- Ready scenarios as MCP prompts: check before issue, weekly coordinator report, acceptance of a subcontractor model.

## Known gaps

- Public-source cleanup: legacy snapshot readers, contracts and fixtures still contain organization-specific family and parameter identifiers; removing those fields changes the legacy feed contract.
- Python naming: `ReadJob` and `RevitReadChannel` also carry actions, and the MCP display name still says Reader; rename with an explicit compatibility policy.
- Revit resources: reader and legacy snapshot paths still need a collector/filter disposal audit under live Revit.
- Compatibility: NWC export needs the Navisworks exporter, which Revit 2023 and 2025 builds on the validation workstation did not have; the server refuses with a clear message there. Importing the `.mcpb` bundle into Claude Desktop is not validated yet.
- Batch undo: the Revit undo menu label (`revit_batch`) cannot be verified through the API.
