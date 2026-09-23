# Revit Model MCP — Manual End-to-End Test Plan

Full manual validation checklist. Goal: exercise **every tool** and **every install/transport path**, from a clean install through live model reads and gated actions, on each supported Revit version.

Run when scheduled. Tick each `[ ]`. For every checked item record: **result** (pass/fail), **evidence** (journal line, screenshot, tool JSON, elapsed), and **notes**. File a GitHub issue for each fail with the repro.

> Reference model for expected values: **Snowdon Towers Sample Architectural.rvt** (ships with Revit). Known values captured 2026-09-15 on Revit 2024.3 — use as the expected baseline where cited. If testing another model, capture its own baseline first with `revit_document_info` + `revit_aggregate_elements`.

---

## 0. Test environments (matrix)

Ideally test on **two machines**:

- **M1 — clean-ish Windows workstation**: fresh or minimally-used Windows, target Revit version installed, Claude Desktop installed and logged in. This is the "does a real user get it working from the installers" run.
- **M2 — remote client (macOS/Linux)**: Claude Desktop / Claude Code on a Mac, reaching a Windows workstation's Revit over the remote (SSH tunnel) transport.

| Field | M1 | M2 |
|---|---|---|
| OS / build | | |
| Revit version(s) | | |
| Python + uv version | | |
| Claude Desktop version | | |
| Claude Code (`claude --version`) | | |
| Release under test (add-in / server / mcpb) | | |

Record the exact release tag being tested (e.g. `v0.5.0`) and confirm all three artifacts are the same version: the `.msi`/add-in zip, the PyPI `revit-model-mcp`, and the `.mcpb`.

---

## 1. Add-in installation (fresh, per Revit version)

Do this with **Revit closed**. Repeat the whole section for each Revit year present (2022 / 2023 / 2024 / 2025 / 2026 / 2027).

- [ ] **1.1 MSI — SingleUser**: run `RevitModelMcp-<ver>-SingleUser.msi`. Installer completes without error.
- [ ] **1.2 MSI — MultiUser**: on a separate profile/machine, run `RevitModelMcp-<ver>-MultiUser.msi`. Completes; add-in visible for all users.
- [ ] **1.3 Clone script**: `./install.ps1 -Source Release` from a clone. Completes.
- [ ] **1.4 Manifest present**: `%APPDATA%\Autodesk\Revit\Addins\<year>\RevitModelMcp.addin` exists and points to a `RevitModelMcp.dll` that exists.
- [ ] **1.5 DLL version**: the installed `RevitModelMcp.dll` matches the release version.
- [ ] **1.6 Provenance**: verify the release asset's build-provenance attestation (per docs `security/#verify-downloads`) before install.
- [ ] **1.7 Load in Revit**: start Revit, accept the "unsigned add-in" trust dialog (**Always Load**), open a model. Journal (`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <year>\Journals\journal.*.txt`) shows `API_SUCCESS ... Starting External Application: Revit Model MCP ... Assembly Version: <ver>` and event registrations. No fatal `API_ERROR` for the MCP DLL (benign "assembly version conflict" warnings, where the reference is *lower* than the installed Revit build, are OK).
- [ ] **1.8 Uninstall**: MSI uninstall removes the manifest cleanly (no orphan files, no load error next Revit start).

**Regression to watch (found before):** the add-in must reference `RevitAPI/RevitAPIUI` pinned to `<year>.0.0.0` (≤ installed build) and `System.Diagnostics.DiagnosticSource 8.x`, or it fails to load. Confirm per version.

---

## 2. Server + client wiring (every documented method)

### 2.1 Local server via `uvx` (Windows workstation)
- [ ] `uvx revit-model-mcp` resolves and installs the package from PyPI on first run (record package count + time).
- [ ] Server starts and reports `serverInfo: { name: "Revit Model Reader", version: "<ver>" }` on `initialize`.
- [ ] `tools/list` returns **19 read tools** (enumerate — see §3).

### 2.2 Claude Code registration (`claude mcp add`)
- [ ] `claude mcp add revit-model-mcp -s user -e REVIT_MCP_HOST=local -e REVIT_MCP_REDACT_PATHS=1 -- uvx revit-model-mcp` writes to `~/.claude.json`.
- [ ] `claude mcp list` shows `revit-model-mcp: ... ✓ Connected`.
- [ ] `claude -p "what model is open in Revit?"` (headless) returns real model data (capture stdout to a file — headless output can be lost over a raw pipe).
- [ ] **App local agent**: in Claude Desktop's Code surface with the **"Local"** chip, ask a Revit question → agent calls `mcp__revit-model-mcp__*` and answers with real data.

### 2.3 Claude Desktop `.mcpb` one-click install  ⚠️ **known-broken, re-verify**
- [ ] Double-clicking `revit-model-mcp-<ver>.mcpb` on Windows opens it **in Claude Desktop** (file association). *2026-09-15: FAILED — Windows showed "choose an app", Claude not registered as the `.mcpb` handler. Confirm whether fixed / document the correct install entry point (Settings → Extensions/Connectors → install from file).*
- [ ] Install via Claude Desktop **Settings → Extensions/Connectors → install `.mcpb`**; the config form sets `REVIT_MCP_HOST` (local/remote), redaction, actions, HTTP token without editing JSON.
- [ ] After install the connector appears and its tools are usable **in a surface that can reach a local stdio server**.

### 2.4 Manual `claude_desktop_config.json`  ⚠️ **surface limitation, re-verify**
- [ ] Write the documented `mcpServers` block, restart Claude Desktop.
- [ ] Determine which surface picks it up. *2026-09-15: the app's **cloud chat** could NOT reach the local stdio MCP ("doesn't reach this cloud session"). Document clearly: local stdio MCP works only in the **local** agent, not the cloud chat.*

### 2.5 Remote workstation (M2 → M1)
- [ ] From a Mac/Linux client, configure the remote workstation (SSH tunnel to the workstation loopback endpoint) per docs `transport/`.
- [ ] Client `revit_ping` succeeds against the remote Revit; a read tool returns real data.

---

## 3. Read tools — full coverage (19)

Model open on the workstation. Call **`revit_list_catalog` first, `revit_aggregate_elements` second, `revit_query_elements` only when rows are needed** (per server guidance). For each tool: record the JSON, confirm `success: true`, spot-check values.

| # | Tool | How to invoke (natural prompt or direct call) | Expected / spot-check | [ ] |
|---|---|---|---|---|
| 1 | `revit_ping` | "is Revit reachable?" | `pong`; responder documentName + revitVersion + processId | [ ] |
| 2 | `revit_document_info` | "what model is open, its levels and rooms?" | file name; levels with elevation + roomCount; area schemes (Snowdon: Gross Building 14, Rentable 87); `isWorkshared` | [ ] |
| 3 | `revit_model_health` | "give me a model health summary" | health metrics (warning count, etc.) return without error | [ ] |
| 4 | `revit_links_status` | "list linked models and their status" | linked model list (empty is a valid result — confirm shape) | [ ] |
| 5 | `revit_shared_coordinates` | "what are the shared coordinates / survey point?" | survey/base point + coordinate system | [ ] |
| 6 | `revit_parameter_fill_check` | "check parameter completeness" | fill/missing report for the requested parameters/category | [ ] |
| 7 | `revit_list_catalog` | "list the element categories/types in the model" | catalog of categories and counts | [ ] |
| 8 | `revit_aggregate_elements` | "how many doors and windows?" | counts by category (Snowdon: Doors 142, Windows 106) | [ ] |
| 9 | `revit_query_elements` | "find the largest room: name, level, area" | rows with real values (Snowdon: Parking Garage, level Parking, 957.6 m²) | [ ] |
| 10 | `revit_list_views` | "list the views" | views enumerated (browser has floor/ceiling/3D/elevation/section) | [ ] |
| 11 | `revit_view_summary` | "summarize the active view / view X" | view type, scale, element count summary | [ ] |
| 12 | `revit_export_view` | "export the {3D / a floor plan} view to an image" | image produced; `localPath` returned (visible even with redaction) — open it, confirm it renders | [ ] |
| 13 | `revit_view_elements` | "what elements are in view X?" | element list for that view | [ ] |
| 14 | `revit_element_details` | "details of element <id>" (use an id from #9) | full parameter details for that element | [ ] |
| 15 | `revit_view_warnings` | "warnings in view X" | warnings scoped to the view | [ ] |
| 16 | `revit_list_warnings` | "list all model warnings" | model-wide warnings list | [ ] |
| 17 | `revit_list_relations` | "show hosting/group relations for element <id>" | relation graph (host, hosted, group membership) | [ ] |
| 18 | `revit_list_instances` | "list instances of family/type <X>" | instances of the named type | [ ] |
| 19 | `revit_family_audit` | "audit family <X>" | parameter use, shared flag and purge coverage; no project change | [ ] |

- [ ] **3.20 Redaction on**: with `REVIT_MCP_REDACT_PATHS=1`, path fields are redacted but names, parameter values, errors, channel files and export `localPath` remain visible.
- [ ] **3.21 Redaction off**: `REVIT_MCP_REDACT_PATHS=0` shows full paths.

---

## 4. Action tools — gated writes (11)

### Family audit and edits

- [ ] Open an `.rfa`. Run `revit_family_audit` without `families`; inspect shared status, parameter use and purge counts. Run `revit_edit_families` with an added shared parameter, then save manually.
- [ ] In a project, audit three editable families. Run the same edit with `dry_run=true`; verify the project is unchanged and no family was loaded.
- [ ] Run the edit for real; verify one `revit_edit_families` undo entry. Undo and verify that the original family state returns.
- [ ] Run `set_shared` and confirm the flag in Family Category and Parameters. Check the reported loaded state.
- [ ] Run `purge` on Revit 2023 and verify coverage `families-and-types`; on Revit 2026 verify `full`.
- [ ] Verify a used parameter is kept with `usedBy`, an unused shared parameter requires `include_shared=true`, and a missing shared parameter file fails before any edit.
- [ ] With `replace_family_parameter=true`, give a valid GUID and a different parameter name. Verify the edit fails and the original parameter and its values remain intact.
- [ ] Cause the first family edit to fail with `stop_on_error=true`. Verify `success:false`, `committed:false`, `failedFamily`, and `rolledBack:true`; confirm the project is unchanged.
- [ ] Audit enough families for the call to take over 60 seconds. Verify the complete audit returns `success:true` within the configured response timeout.

Actions require **both gates**: `REVIT_MCP_ALLOW_WRITE=1` **and** the workstation allow-write file. Direct HTTP callers also need the bearer token.

### NWC export

- [ ] Remove or disable the year-matched Navisworks NWC exporter. `revit_export_nwc` reports `Navisworks exporter is not installed for Revit <year> on this workstation.`
- [ ] With the exporter installed, run `dry_run=true` with a new absolute `.nwc` path. Check all effective options and `exporterAvailable:true`; confirm no file is created.
- [ ] Export the full model with `coordinates="shared"`. Open it in Navisworks and verify alignment with an NWC of a linked model exported the same way.
- [ ] Export a non-template 3D view with `scope="view"` and an enabled section box. Verify the NWC respects the section box.
- [ ] Export a 3D view whose exact name is numeric. Verify name lookup works when that number is not a view ID.
- [ ] Export once with `parameters="all"` and once with `parameters="none"`. Verify element properties disappear in the second NWC.
- [ ] Export to an existing file without `overwrite`. Verify the error and confirm the original file bytes are unchanged.

- [ ] **4.0 Enumerate**: start the server with write enabled and run `tools/list` — record the exact action tool names and count. Known action set from the demo recording (README "In action"): open a view, select element(s), isolate, place a family instance, move an element, and cleanup/undo. Fill the table with the real names.

| Action tool (fill from tools/list) | Test | Verify | Cleanup | [ ] |
|---|---|---|---|---|
| open view | open a specific view by name | view is active in Revit UI | — | [ ] |
| select element(s) | select the largest room / an element by id | selection highlighted in Revit | clear selection | [ ] |
| isolate | isolate the selection in the view | temp isolate active | reset temporary hide/isolate | [ ] |
| place family instance | place e.g. a chair at a point | instance exists at location | delete it | [ ] |
| move element | move the placed instance | new location correct | delete it | [ ] |
| delete / cleanup | delete the test instance | element gone | model back to baseline | [ ] |
| _(others)_ | | | | [ ] |

- [ ] **4.1 Gate negative test**: with only `REVIT_MCP_ALLOW_WRITE=1` but **no** allow-write file (or vice-versa), an action is **refused**. Read tools still work.
- [ ] **4.2 HTTP token gate**: a direct HTTP action without the bearer token is refused; `/health` works without token.
- [ ] **4.3 Transaction safety**: every action wraps a Revit transaction; a failed action leaves the model unchanged (no partial edits).
- [ ] **4.4 Model restored**: after the action suite, the model matches its pre-test baseline (re-run `revit_aggregate_elements`).

---

## 5. Transports

- [ ] **5.1 Local** (`REVIT_MCP_HOST=local`) on the workstation — all of §3 passes.
- [ ] **5.2 SSH tunnel** (remote client → workstation loopback) — `revit_ping` + a read tool pass from M2.
- [ ] **5.3 HTTP** (loopback + bearer token) — `/health` (no token) responds; an authenticated read works. ⚠️ **Re-verify issue #44**: the HTTP listener (port 53110) not binding. Confirm fixed or still open.
- [ ] **5.4 Concurrency / channel contention** ⚠️ **found 2026-09-15**: fire several tool calls in quick succession (as the agent does when reasoning). Earlier run showed *4 of 7* parallel calls failing (single file-channel "busy: trigger.txt exists"). Test rapid/parallel calls and record the failure rate; if the channel serializes poorly, file an issue.

---

## 6. Multi-version Revit

Repeat the **§3 read smoke set** (at minimum: ping, document_info, list_catalog, aggregate_elements, list_warnings, export_view) with a model open in each installed version:

- [ ] 6.1 Revit 2022 (net48)
- [ ] 6.2 Revit 2023 (net48)
- [ ] 6.3 Revit 2024 (net48)
- [ ] 6.4 Revit 2025 (net8)
- [ ] 6.5 Revit 2026 (net8)
- [ ] 6.6 Revit 2027 (net10) — if available
- [ ] 6.7 **Two instances open**: with 2+ Revit versions running, confirm the `document` parameter is required and correctly routes to the intended instance (`revit_ping` with/without `document`).

---

## 7. Client-surface matrix (which Claude surface actually works)

Record pass/fail per surface — this is where the confusion was:

| Surface | Reaches local stdio MCP? | Reaches remote MCP? | [ ] |
|---|---|---|---|
| Claude Desktop — **cloud chat** (Chat/Cowork) | expected **NO** (cloud) | via connector? | [ ] |
| Claude Desktop — **local agent** (Code + "Local" chip) | expected **YES** | n/a | [ ] |
| Claude Code CLI (`claude -p`, `claude`) local | expected **YES** | n/a | [ ] |
| Claude Desktop with **remote workstation** config (M2) | n/a | expected **YES** | [ ] |

- [ ] Document the *recommended* setup for each user type (Windows-local vs Mac-remote) based on what actually passed.

---

## 8. Known-issue regression (must re-test)

Explicit re-tests for things that broke or surprised us:

- [ ] **8.1** `.mcpb` file association / open-in-Claude (see §2.3).
- [ ] **8.2** Local stdio MCP unavailable in the cloud chat (see §2.4).
- [ ] **8.3** HTTP listener 53110 bind — issue #44 (see §5.3).
- [ ] **8.4** Channel contention on parallel tool calls (see §5.4).
- [ ] **8.5** Add-in load: benign vs fatal `API_ERROR` (assembly version conflict) per version (see §1.7).
- [ ] **8.6** `uvx`/`claude` not on the non-interactive shell PATH (use full paths) — a setup-doc note, not a product bug.

---

## 9. Reporting

For the run, produce a short results table: environment, release tag, per-section pass/fail counts, and a list of filed issues. Optionally record a **clean demo video** — but arrange the UI first (see the preferences note): open a real 3D/model view in Revit (not just the project browser), size Claude and Revit panels so both are legible, then start the screen recording. Ask the owner to set the layout before recording.

**Definition of done for the release:** §1–§7 all pass on at least Revit 2024 + 2026 (local) and one remote (M2) run; all §8 regressions either pass or have a tracked issue; the client-surface matrix (§7) is documented in the README/docs.
