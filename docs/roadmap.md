# Roadmap

Review date: 2026-09-11.

## Known gaps

- Public-source cleanup: legacy snapshot readers, contracts and fixtures still contain organization-specific family and parameter identifiers; removing those fields changes the legacy feed contract.
- English diagnostics: Capture, Core, view sessions and tests still contain Russian messages or fixtures; localized model parameter aliases must retain their lookup behavior during translation.
- Python naming: `ReadJob` and `RevitReadChannel` also carry actions, and the MCP display name still says Reader; rename with an explicit compatibility policy.
- File protocol: jobs lack correlation IDs and interprocess response ownership; use one server process per channel directory.
- File responses: writes are not atomic, and a polling client can observe incomplete JSON; atomic publication needs transport regression coverage.
- Capture diagnostics: best-effort readers swallow some parameter and geometry failures without reporting skipped fields or logging their cause.
- Revit resources: reader and legacy snapshot paths still need a collector/filter disposal audit under live Revit.
- HTTP shutdown: listener tasks are detached and the cancellation source is not disposed; drain in-flight handlers before disposing shared state.
- HTTP artifacts: exports that are never fetched are not registered for image cleanup; result expiry can also race with an image download.
- HTTP capacity: completed response storage has time-based expiry but no byte/count budget, and a request body has no read deadline.
- Action policy: generic TaskDialog overrides and automatic failure resolutions need live model coverage before unattended write use.
- Parameter edits: duplicate parameter names and implicit type fallback need explicit disambiguation before expanding the action API.
- Dependency reproducibility: floating NuGet and Python ranges can change restores; pin the resolved graph before promising reproducible binaries.
- Compatibility: CI compilation does not verify installation, HTTP ACL behavior or live actions in each Revit year.
- Release validation: the tag-only publishing job requires its first real tag run; it is not triggered by a main-branch push.
