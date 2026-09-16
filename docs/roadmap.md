# Roadmap

Review date: 2026-09-15.

## Direction

Announced on the Autodesk Revit API forum on 2026-09-15. The project does not compete with the Autodesk Revit MCP on generic CRUD; it stays useful on Revit 2022–2026, where the Autodesk server is not available, and moves in five directions:

- Coordinator-grade checks with evidence: every check names the rule, the elements and the view it was evaluated on, so the result can be handed to a client without rerunning it.
- Checks across linked models: link status, shared coordinates and clash-adjacent questions that need more than one document open.
- Execution policy for unattended runs: confirmation tokens for actions and a stated policy for what an unattended client may do (see the action policy gap below).
- Live validation on every supported Revit year, recorded in the validation table with the contributor's name when offered.
- Design and Make Marketplace listing in the Local (stdio) model: manifest, tool inventory, declaration form.

New tools follow from the first direction; a check that a coordinator scripts by hand every week is the next candidate.

## Known gaps

- Public-source cleanup: legacy snapshot readers, contracts and fixtures still contain organization-specific family and parameter identifiers; removing those fields changes the legacy feed contract.
- English diagnostics: Capture, Core, view sessions and tests still contain Russian messages or fixtures; localized model parameter aliases must retain their lookup behavior during translation.
- Python naming: `ReadJob` and `RevitReadChannel` also carry actions, and the MCP display name still says Reader; rename with an explicit compatibility policy.
- File protocol: jobs lack correlation IDs and interprocess response ownership; use one server process per channel directory.
- File responses: writes are not atomic, and a polling client can observe incomplete JSON; atomic publication needs transport regression coverage.
- Capture diagnostics: `model-health` reports `skipped` and `links-status` reports per-link `error`; other best-effort readers still swallow some parameter and geometry failures without reporting skipped fields or logging their cause.
- Revit resources: reader and legacy snapshot paths still need a collector/filter disposal audit under live Revit.
- HTTP shutdown: listener tasks are detached and the cancellation source is not disposed; drain in-flight handlers before disposing shared state.
- HTTP artifacts: exports that are never fetched are not registered for image cleanup; result expiry can also race with an image download.
- HTTP capacity: completed response storage has time-based expiry but no byte/count budget, and a request body has no read deadline.
- Action policy: `dry_run` and `verification` exist; confirmation tokens and an unattended execution policy do not.
- Parameter edits: duplicate parameter names and implicit type fallback need explicit disambiguation before expanding the action API.
- Dependency reproducibility: floating NuGet and Python ranges can change restores; pin the resolved graph before promising reproducible binaries.
- Compatibility: no live validation of reads or actions on Revit 2022–2025 or 2027 in the 2026-09-12 validation pass; CI compilation does not verify HTTP ACL behavior or live execution.
- Publishing: v0.1.0 ships GitHub release assets; MCP Registry and PyPI publishing are unavailable.
- Batch undo: the Revit undo menu label (`revit_batch`) cannot be verified through the API.
- Installation: `install.ps1` has no rollback across years if a later year fails.
