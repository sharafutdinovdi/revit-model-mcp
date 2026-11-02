# Roadmap

Review date: 2026-10-23.

Model MCP is an add-in plus an MCP server that lets a coordinator's AI client drive a live Revit session.
I keep actions confirm-first for irreversible steps and undo-first for model changes.
See [what Model MCP does not do](not-doing.md) for the project boundaries.

## How work ships

Releases go out weekly on Monday (Europe/Moscow).
Each Monday release has a milestone named by its date, and unscheduled work lives in `Backlog`.
An urgent fix can ship on another day with the `hotfix-release` label on the release pull request.
The roadmap states direction, not versions: what lands in a release is what is merged by then.
release-please maintains the release pull request and version history.
See [CHANGELOG.md](changelog.md) for what shipped in each version.

## Shipped

### 0.9.x

- Revit session actions, including document control, exports, model edits, and multi-model processing ([#169](https://github.com/sharafutdinovdi/revit-model-mcp/pull/169)).
- Confirmation token and job polling fixes from live validation ([#184](https://github.com/sharafutdinovdi/revit-model-mcp/pull/184)).

### 0.10.0

- Confirmation tokens for link removal, overwriting exports, C# scripts, and deletions above 500 elements ([#242](https://github.com/sharafutdinovdi/revit-model-mcp/pull/242), [#253](https://github.com/sharafutdinovdi/revit-model-mcp/pull/253)).
- Batch start and cancel obey read-only mode, closing [#26](https://github.com/sharafutdinovdi/revit-model-mcp/issues/26) ([#241](https://github.com/sharafutdinovdi/revit-model-mcp/pull/241)).
- Bulk dry runs predict group failures, closing [#191](https://github.com/sharafutdinovdi/revit-model-mcp/issues/191) ([#228](https://github.com/sharafutdinovdi/revit-model-mcp/pull/228)).
- Toolsets and shorter tool descriptions reduce the client's tool context ([#275](https://github.com/sharafutdinovdi/revit-model-mcp/pull/275)).
- Machine policy and ADMX templates support managed deployments ([#275](https://github.com/sharafutdinovdi/revit-model-mcp/pull/275)).
- MSI installers bundle a server executable, and Connect AI app registers it ([#279](https://github.com/sharafutdinovdi/revit-model-mcp/pull/279)).
- `revit_batch` becomes `revit_run_actions` ([#257](https://github.com/sharafutdinovdi/revit-model-mcp/pull/257)).
- Tag and schedule creation tools extend model coordination ([#275](https://github.com/sharafutdinovdi/revit-model-mcp/pull/275)).
- The 1.x stability promise defines compatible tool and response changes ([#255](https://github.com/sharafutdinovdi/revit-model-mcp/pull/255)).

### 1.0.0

- Autodesk App Store build artifacts include the MCP manifest with tool input schemas ([#279](https://github.com/sharafutdinovdi/revit-model-mcp/pull/279), [#286](https://github.com/sharafutdinovdi/revit-model-mcp/pull/286)).

### 2.0.0

- A dedicated Model MCP ribbon tab provides client setup, Activity, product links, themed icons, and contextual tooltips.
- One workstation read-only lock covers all running Revit versions for the Windows user.
  The ribbon and `revit_set_read_only` control it.
  Chat unlock requires explicit confirmation, and machine policy remains enforced.
- `revit_model_audit` evaluates single-model rules with evidence, immutable history, and comparisons.
  `revit_audit_report` writes XLSX and HTML reports.
  See [model audit](model-audit.md).
- `revit_list_sheets` reads sheets, title blocks, revisions, placed views, and unplaced views.
- `revit_api_lookup` returns public types and members from the loaded Revit API.
- MCP progress notifications report queue state, Revit API availability, and batch step counters during long requests.
- Document tab borders identify active MCP work and restore the previous appearance after completion.
- Coordinator, daily QA, sheet set, and ISO 19650 issue register skills provide reusable workflows.
- Eligible text parameter batches share one transaction and regeneration.
  Other action batches retain their existing transaction boundaries.
- Document-switch recovery, bounded catalog results, read caches, deferred report imports, and compact family audit summaries improve reads.
- Transport loss returns retryable `transport_unavailable` responses.
- Revit SDK and Polyfill dependencies are updated.
  Benchmarks, an LLM evaluation harness, in-Revit add-in tests, and a Windows installer smoke script support validation.

## Next

I prioritize the open work by its priority label.
Children of an epic keep their own labels.

### P2

- Hardening epic [#220](https://github.com/sharafutdinovdi/revit-model-mcp/issues/220): finish validation and robustness work after 0.9.x; its open children are listed below.
- [#195](https://github.com/sharafutdinovdi/revit-model-mcp/issues/195): validate Claude Desktop bundle import and the default HTTP port before treating those paths as verified (`help wanted`).
- [#210](https://github.com/sharafutdinovdi/revit-model-mcp/issues/210): check port 53110 on a clean machine to verify the default HTTP setup (`help wanted`).
- [#278](https://github.com/sharafutdinovdi/revit-model-mcp/issues/278): finish workstation checks for machine policy, updates, and client setup before the next release (`help wanted`).

### P3

- [#201](https://github.com/sharafutdinovdi/revit-model-mcp/issues/201): dispose view-reader curve and collector wrappers to complete the resource audit under [#220](https://github.com/sharafutdinovdi/revit-model-mcp/issues/220).
- [#204](https://github.com/sharafutdinovdi/revit-model-mcp/issues/204): guard batch dialog handling against exceptions and slow regular expressions under [#220](https://github.com/sharafutdinovdi/revit-model-mcp/issues/220).
- [#207](https://github.com/sharafutdinovdi/revit-model-mcp/issues/207): investigate Revit 2022 assembly conflicts and slow startup under [#220](https://github.com/sharafutdinovdi/revit-model-mcp/issues/220).
- [#291](https://github.com/sharafutdinovdi/revit-model-mcp/issues/291): evaluate Roslyn 5 for C# execution against the supported Revit runtimes.
- [#237](https://github.com/sharafutdinovdi/revit-model-mcp/issues/237): add per-action-class workstation policy for irreversible actions and C# scripts.
- [#292](https://github.com/sharafutdinovdi/revit-model-mcp/issues/292): align MCP documentation with specification 2026-07-28 and verify client names from request metadata.
- [#219](https://github.com/sharafutdinovdi/revit-model-mcp/issues/219): sign release builds to remove the unsigned add-in prompt (`help wanted`).

### Planned capabilities

- E4: a live overlay for MCP activity in Revit.
- E6: optional Revit confirmation for irreversible actions. See [#236](https://github.com/sharafutdinovdi/revit-model-mcp/issues/236).
- Worksets and element ownership. See [#213](https://github.com/sharafutdinovdi/revit-model-mcp/issues/213).
- Multi-model audits. See [#215](https://github.com/sharafutdinovdi/revit-model-mcp/issues/215). Single-model history comparisons are available in 2.0.0.
- BCF export for audit findings. XLSX and HTML reports are available in 2.0.0. See [#216](https://github.com/sharafutdinovdi/revit-model-mcp/issues/216).
- Fuzzing for tool inputs and transport boundaries.

## Known gaps

- View readers still need a resource disposal audit under live Revit ([#201](https://github.com/sharafutdinovdi/revit-model-mcp/issues/201)).
- NWC export needs the Navisworks exporter. The Revit 2023 and 2025 installations on the validation workstation did not have it; see [validation evidence](validation.md).
- Claude Desktop `.mcpb` import and the default HTTP port still need validation ([#195](https://github.com/sharafutdinovdi/revit-model-mcp/issues/195)).
- Batch undo: the Revit undo menu label (`MCP (<clientName>): <short summary>`) cannot be verified through the API.
- Revit 2022 reports an assembly version conflict and starts slowly ([#207](https://github.com/sharafutdinovdi/revit-model-mcp/issues/207)).
- `revit_capture_elements` ignores the plan crop when framing ([#276](https://github.com/sharafutdinovdi/revit-model-mcp/issues/276)).
- `revit_walls_from_cad` stops joining after six preflight rounds ([#277](https://github.com/sharafutdinovdi/revit-model-mcp/issues/277)).
- Parameter batches are bounded by Revit regeneration: about 11 s for 50 wall edits on Snowdon.

## How an item gets here

File an [issue](https://github.com/sharafutdinovdi/revit-model-mcp/issues/new/choose).
I assign a priority label from `P0` to `P3`; [CONTRIBUTING.md](contributing.md#labels-and-milestones) defines the levels.
Items marked `needs-design` need an agreed design before code.
`help wanted` marks items where outside contributions are welcome.
