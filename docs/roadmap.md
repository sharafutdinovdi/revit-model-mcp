# Roadmap

Review date: 2026-10-18.

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

### 1.1.0 (pending)

These changes are merged on `main` and await a release.

- Document-switch recovery and bounded catalog results ([#298](https://github.com/sharafutdinovdi/revit-model-mcp/pull/298)).
- Read-path caches and deferred report imports ([#297](https://github.com/sharafutdinovdi/revit-model-mcp/pull/297)).
- Compact family audit summaries by default ([#301](https://github.com/sharafutdinovdi/revit-model-mcp/pull/301)).
- Retryable `transport_unavailable` responses for transport loss ([#302](https://github.com/sharafutdinovdi/revit-model-mcp/pull/302)).
- Revit SDK and Polyfill dependency updates ([#304](https://github.com/sharafutdinovdi/revit-model-mcp/pull/304)).

## Current direction

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
- [#209](https://github.com/sharafutdinovdi/revit-model-mcp/issues/209): add a Windows release smoke script for repeatable live checks under [#220](https://github.com/sharafutdinovdi/revit-model-mcp/issues/220) (`help wanted`).
- [#294](https://github.com/sharafutdinovdi/revit-model-mcp/issues/294): run add-in tests inside Revit to cover API behavior.
- [#290](https://github.com/sharafutdinovdi/revit-model-mcp/issues/290): report progress during long actions to keep clients informed.
- [#291](https://github.com/sharafutdinovdi/revit-model-mcp/issues/291): evaluate Roslyn 5 for C# execution against the supported Revit runtimes.
- [#236](https://github.com/sharafutdinovdi/revit-model-mcp/issues/236): design optional workstation confirmation for irreversible actions (`needs-design`).
- [#237](https://github.com/sharafutdinovdi/revit-model-mcp/issues/237): add per-action-class workstation policy for irreversible actions and C# scripts.
- [#295](https://github.com/sharafutdinovdi/revit-model-mcp/issues/295): design a Revit API lookup tool to support C# execution (`needs-design`).
- [#292](https://github.com/sharafutdinovdi/revit-model-mcp/issues/292): align MCP documentation with specification 2026-07-28 and verify client names from request metadata.
- [#219](https://github.com/sharafutdinovdi/revit-model-mcp/issues/219): sign release builds to remove the unsigned add-in prompt (`help wanted`).

## Model audit

I want `revit_model_audit` to run a stated rule set against one or many models and return findings with evidence ([#98](https://github.com/sharafutdinovdi/revit-model-mcp/issues/98)).
The [model audit design](design/model-audit.md) covers rule files, snapshots, and reports; its open questions block implementation.

- [#212](https://github.com/sharafutdinovdi/revit-model-mcp/issues/212): sheets, title blocks, revisions, and unplaced views (`P3`, `roadmap`, `enhancement`, `addin`, `server`).
- [#213](https://github.com/sharafutdinovdi/revit-model-mcp/issues/213): worksets and element ownership (`P3`, `roadmap`, `enhancement`, `addin`).
- [#214](https://github.com/sharafutdinovdi/revit-model-mcp/issues/214): single-model rule evaluation with evidence (`P3`, `roadmap`, `enhancement`, `server`).
- [#215](https://github.com/sharafutdinovdi/revit-model-mcp/issues/215): multi-model audits and changes since the previous audit (`P3`, `roadmap`, `enhancement`, `server`).
- [#216](https://github.com/sharafutdinovdi/revit-model-mcp/issues/216): HTML or XLSX reports with view images and BCF export (`P3`, `roadmap`, `enhancement`, `server`).

## Known gaps

- View readers still need a resource disposal audit under live Revit ([#201](https://github.com/sharafutdinovdi/revit-model-mcp/issues/201)).
- NWC export needs the Navisworks exporter. The Revit 2023 and 2025 installations on the validation workstation did not have it; see [validation evidence](validation.md).
- Claude Desktop `.mcpb` import and the default HTTP port still need validation ([#195](https://github.com/sharafutdinovdi/revit-model-mcp/issues/195)).
- Batch undo: the Revit undo menu label (`MCP (<clientName>): <short summary>`) cannot be verified through the API.
- Revit 2022 reports an assembly version conflict and starts slowly ([#207](https://github.com/sharafutdinovdi/revit-model-mcp/issues/207)).
- `revit_capture_elements` ignores the plan crop when framing ([#276](https://github.com/sharafutdinovdi/revit-model-mcp/issues/276)).
- `revit_walls_from_cad` stops joining after six preflight rounds ([#277](https://github.com/sharafutdinovdi/revit-model-mcp/issues/277)).

## How an item gets here

File an [issue](https://github.com/sharafutdinovdi/revit-model-mcp/issues/new/choose).
I assign a priority label from `P0` to `P3`; [CONTRIBUTING.md](contributing.md#labels-and-milestones) defines the levels.
Items marked `needs-design` need an agreed design before code.
`help wanted` marks items where outside contributions are welcome.
