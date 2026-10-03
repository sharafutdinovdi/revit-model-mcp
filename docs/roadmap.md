# Roadmap

Review date: 2026-10-04.

## Direction

The project works with the Revit session as a whole, not only with the open document ([#160](https://github.com/sharafutdinovdi/revit-model-mcp/issues/160)). A client drives Revit the way a coordinator does: it switches between open models and views, opens models with the right settings, processes many models in one run, places families in bulk, exports deliverables, and runs a C# script when no curated tool fits. It stays useful on Revit 2022-2026, where the Autodesk Revit MCP is not available.

- Session control: tabs, views, selection, open settings and new documents ([#154](https://github.com/sharafutdinovdi/revit-model-mcp/issues/154)).
- C# scripts as an ordinary action, refused in read-only mode ([#153](https://github.com/sharafutdinovdi/revit-model-mcp/issues/153)).
- Multi-model processing in the interactive session: open, change, export or save, close ([#159](https://github.com/sharafutdinovdi/revit-model-mcp/issues/159)). The batch collector stays read-only.
- Deliverables: PDF, DWG, IFC and schedule CSV ([#155](https://github.com/sharafutdinovdi/revit-model-mcp/issues/155)), views and sheets ([#158](https://github.com/sharafutdinovdi/revit-model-mcp/issues/158)).
- Model edits beyond single elements: bulk family placement ([#156](https://github.com/sharafutdinovdi/revit-model-mcp/issues/156)), rotate, copy, mirror, type change and bulk parameter update ([#157](https://github.com/sharafutdinovdi/revit-model-mcp/issues/157)).
- Coordinator-grade checks with evidence remain: every check names the rule, the elements and the view it was evaluated on.
- Live validation on every supported Revit year, recorded in the validation table.

Every action keeps the existing rules: refused in read-only mode, one named undo entry per model change, listed in the activity pane and reported in `summary`.

## Now: 0.7, security and delivery

Milestone [v0.7: Security & delivery](https://github.com/sharafutdinovdi/revit-model-mcp/milestone/2).

- Security hardening from the September review: SSH invocation and artifact downloads, private channel directory, network path allowlist, named pipe ownership, HTTP endpoint proof, path redaction, bounded stores, pinned release tooling and verified installer downloads.
- Delivery: a complete bundle manifest published to Smithery on release ([#87](https://github.com/sharafutdinovdi/revit-model-mcp/issues/87)), a server that updates itself and checks the add-in version ([#88](https://github.com/sharafutdinovdi/revit-model-mcp/issues/88)), and an add-in that updates itself after Revit closes ([#89](https://github.com/sharafutdinovdi/revit-model-mcp/issues/89)).
- Code signing: a certificate for open source builds, so updates do not trigger Revit's unsigned add-in prompt. Until builds are signed, Revit asks once after each add-in update.
- Installer: per-user MSI without elevation ([#76](https://github.com/sharafutdinovdi/revit-model-mcp/issues/76)), rollback across years ([#31](https://github.com/sharafutdinovdi/revit-model-mcp/issues/31)), reproducible restores ([#30](https://github.com/sharafutdinovdi/revit-model-mcp/issues/30)).

## Next: 0.8, reliability and action safety

Milestone [v0.8: Reliability & action safety](https://github.com/sharafutdinovdi/revit-model-mcp/milestone/6).

- A confirmation shown in Revit before irreversible or bulk actions ([#71](https://github.com/sharafutdinovdi/revit-model-mcp/issues/71)), and a stated policy for unattended runs ([#26](https://github.com/sharafutdinovdi/revit-model-mcp/issues/26)).
- Channel robustness: terminal response handling, committed results kept on cleanup failure, parameter filters and non-ASCII model names ([#53](https://github.com/sharafutdinovdi/revit-model-mcp/issues/53) to [#56](https://github.com/sharafutdinovdi/revit-model-mcp/issues/56)).
- HTTP host lifecycle: graceful shutdown, response budget and read deadline ([#25](https://github.com/sharafutdinovdi/revit-model-mcp/issues/25)).

## Later: model audit for coordinators and project managers

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
- Capture diagnostics: some best-effort readers still swallow parameter and geometry failures without reporting skipped fields ([#28](https://github.com/sharafutdinovdi/revit-model-mcp/issues/28)).
- Revit resources: reader and legacy snapshot paths still need a collector/filter disposal audit under live Revit.
- Parameter edits: duplicate parameter names and implicit type fallback need explicit disambiguation before expanding the action API ([#27](https://github.com/sharafutdinovdi/revit-model-mcp/issues/27)).
- Compatibility: reads and actions are validated live on Revit 2023-2027; Revit 2022 and NWC export on 2022-2025 are not ([#23](https://github.com/sharafutdinovdi/revit-model-mcp/issues/23)).
- Batch undo: the Revit undo menu label (`revit_batch`) cannot be verified through the API.
