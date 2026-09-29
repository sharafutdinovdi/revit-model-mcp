# Roadmap

Review date: 2026-09-29.

## Direction

Announced on the Autodesk Revit API forum on 2026-09-15. The project does not compete with the Autodesk Revit MCP on generic CRUD; it stays useful on Revit 2022-2026, where the Autodesk server is not available, and moves in five directions:

- Coordinator-grade checks with evidence: every check names the rule, the elements and the view it was evaluated on, so the result can be handed to a client without rerunning it.
- Checks across linked models: link status, shared coordinates and clash-adjacent questions that need more than one document open.
- Execution policy for unattended runs: confirmation tokens for actions and a stated policy for what an unattended client may do.
- Live validation on every supported Revit year, recorded in the validation table with the contributor's name when offered.
- Design and Make Marketplace listing in the Local (stdio) model: manifest, tool inventory, declaration form.

New tools follow from the first direction; a check that a coordinator scripts by hand every week is the next candidate.

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

## Known gaps

- Public-source cleanup: legacy snapshot readers, contracts and fixtures still contain organization-specific family and parameter identifiers; removing those fields changes the legacy feed contract.
- Python naming: `ReadJob` and `RevitReadChannel` also carry actions, and the MCP display name still says Reader; rename with an explicit compatibility policy.
- Capture diagnostics: some best-effort readers still swallow parameter and geometry failures without reporting skipped fields ([#28](https://github.com/sharafutdinovdi/revit-model-mcp/issues/28)).
- Revit resources: reader and legacy snapshot paths still need a collector/filter disposal audit under live Revit.
- Parameter edits: duplicate parameter names and implicit type fallback need explicit disambiguation before expanding the action API ([#27](https://github.com/sharafutdinovdi/revit-model-mcp/issues/27)).
- Compatibility: reads and actions are validated live on Revit 2023-2027; Revit 2022 and NWC export on 2022-2025 are not ([#23](https://github.com/sharafutdinovdi/revit-model-mcp/issues/23)).
- Batch undo: the Revit undo menu label (`revit_batch`) cannot be verified through the API.
