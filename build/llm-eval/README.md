# LLM scenario evaluation

Run a small tool-calling agent against the live Model MCP stdio server.
OpenAI uses the Responses API through `openai`.
Anthropic uses the Messages API through `anthropic`.
The API providers forward MCP `tools/list` schemas as function tools and execute calls locally.
Claude Code and Codex CLI providers use existing subscription logins and require no API keys.
It does not use a remote MCP connector.

## Run

Use Python 3.11 or newer and uv. Run from the repository root.
The script declares its dependencies inline. No package metadata changes are needed.

```sh
export OPENAI_API_KEY="$(~/Projects/compass-brain/scripts/secret.sh get OPENAI_API_KEY)"
uv run build/llm-eval/run.py --provider openai --judge openai --out work/ws/v2/llm-eval
```

The default OpenAI model is `gpt-6.1-sol`.
Only a model access rejection with code `model_not_found` or `model_not_available` triggers fallback to `gpt-5.6-sol`.
An explicit `--model` never falls back. Authentication and rate errors never trigger fallback.
The actual model is recorded for each scenario.

```sh
export ANTHROPIC_API_KEY="$(~/Projects/compass-brain/scripts/secret.sh get ANTHROPIC_API_KEY)"
uv run build/llm-eval/run.py --provider anthropic --model claude-sonnet-5-5 \
  --judge anthropic --judge-model claude-sonnet-5-5 --out work/ws/v2/llm-eval
```

Keys come from the environment. Do not print them or enable shell tracing.
`--judge` selects the judge provider family. It defaults to `--provider`.
`--judge-model` selects its exact model. Otherwise the family default applies.
Each API provider requires its own key when selected for the agent or judge.
CLI providers require an installed executable in `PATH` and an existing subscription login.

## Subscription CLI providers

```sh
uv run build/llm-eval/run.py --provider claude-cli --judge claude-cli
uv run build/llm-eval/run.py --provider codex-cli --judge codex-cli
uv run build/llm-eval/run.py --provider claude-cli --judge codex-cli --scenarios sheet_listing
```

`claude-cli` defaults to `claude-sonnet-5-5`; `codex-cli` defaults to `gpt-6.1-sol`.
`--model` and `--judge-model` override these defaults without fallback.
CLI authentication uses the existing Claude Code or Codex subscription login.
The harness does not obtain credentials or edit user configuration.
Codex must support `exec --json` and `--ignore-user-config`.

Claude runs headlessly with `-p --output-format stream-json --verbose`, a generated `--mcp-config`, `--allowedTools "mcp__revit__*"`, and `--dangerously-skip-permissions`.
The parser collects `assistant.message.content` blocks with `type=tool_use`, correlates `user` tool results by `tool_use_id`, and reads the final `result` event.
Codex runs `exec --json -m <model>` with `-c mcp_servers.revit.command=...` and `-c mcp_servers.revit.args=[...]` overrides.
It also sets `mcp_servers.revit.default_tools_approval_mode="approve"`, because the proxy already enforces the scenario's tools and a non-interactive run cannot answer approval prompts.
Its parser tracks `mcp_tool_call` items by ID across `item.started` and `item.completed`, takes the final `agent_message`, and requires `turn.completed`.
Both commands pass the prompt after `--` without a shell.

Each CLI registers the stdio server as `revit` through a temporary harness proxy in this script.
The proxy launches `--server-command` and enforces the same document binding, schema validation, action scope, read-only refusal, and call limit as the API loop.
It serializes tool calls and records enforced arguments and results before replying to the CLI.
The CLI event trace is retained in the transcript; scoring uses the proxy trace.
A trace count mismatch fails the scenario.
The judge runs without a configured MCP server and uses the existing verdict contract.

CLI runs use temporary working directories and exclude user MCP configuration.
Claude disables built-in tools and project settings; Codex retains its built-in agent tools and default sandbox.
The CLI process has a 1,800 second timeout per scenario or judge invocation.
Each CLI starts in its own session, and the whole process group is killed on timeout or exit so grandchildren cannot hold stdout open.
A nonzero exit, malformed JSONL, missing final event, or timeout fails the run and preserves available tool evidence.
Temporary proxy configuration and trace files are removed after each invocation.

## Workstation connection

Open Snowdon Towers Sample Architectural in Revit 2027 before a live run.
The harness checks the responder model name and Revit year before scenarios start.
Use an isolated test model and a quiet workstation. Action scenarios change it temporarily.
The harness never starts Revit and never saves the model.

The harness has no default server command.
Pass `--server-command` or set `RMM_EVAL_SERVER_COMMAND`; the flag wins, and the harness stops with an error when neither is set.
For a remote workstation, for example:

```text
ssh <workstation> "cd /d <checkout>\server && set REVIT_MCP_HOST=local&& uv run -q revit-model-mcp"
```

The command is one quoted string.
The command is split into an executable and arguments, without a local shell.
For a local server, for example:

```sh
uv run build/llm-eval/run.py \
  --server-command 'uv run --directory server revit-model-mcp' \
  --document 'Snowdon Towers Sample Architectural' \
  --scenarios doors_per_level,sheet_listing --out work/ws/v2/llm-eval
```

`--document` is enforced on every tool that accepts it.
`--scenarios` selects comma-separated IDs from `scenarios.yaml`.
Every invocation writes its own evidence files, so a series that stopped or timed out can be
continued without repeating finished scenarios: list only the remaining IDs, after the
workstation is healthy again.

```sh
uv run build/llm-eval/run.py --scenarios create_sheet_plan,snapshot_compare,move_five_undo,daily_qa
```

Combine the result files afterwards. Reopen the unchanged on-disk model first if a session was lost.
`--max-calls` bounds model tool calls per scenario, including invalid calls and retries.
Calls are executed sequentially, even when a model returns several calls in one turn.
MCP calls have a 900 second timeout.
API provider requests have a 180 second timeout and no SDK retries.

## Evidence and scoring

Each invocation writes a new timestamped JSON file and a Markdown table to `--out`.
Existing files are never overwritten. The CLI prints only the summary table.
JSON includes native conversation history, tool arguments and results, final answers,
direct ground truth, actual models, sequence scores, judge verdicts, tokens and elapsed time.
Keep these sensitive artifacts under `work/ws/v2/llm-eval/`. Do not commit them.
Respect the server's path redaction setting. Model evidence is sent to the configured providers.

Ground truth is collected through direct MCP calls before each scenario.
Query ground truth follows all pages, with a bound of 1,000 pages.
Other tools retain their own coverage limits and skipped-item diagnostics.
The agent does not receive ground truth. The snapshot comparison receives only its previous snapshot.
The judge receives the task, rubric, direct evidence, final answer and complete tool trace.
It returns a Boolean verdict and a short reason. Invalid judge output fails the run.

Sequence matching checks ordered milestones with required argument fields.
Lists and scalar arguments must match exactly. Extra discovery calls and retries are allowed and counted.
Alternatives are explicit in YAML. The judge also checks action semantics and cleanup.
A sequence score alone does not establish correctness.
The colour scenario permits a query-only sequence for a zero-match result.
The judge must verify that no walls qualify before accepting that alternative.

`calls` and `tool_errors` count model calls only, including local validation errors.
A blocked action in the refusal scenario counts as a tool error but not an unexpected error.
Ground truth, discovery and judge calls are excluded from those counts.
Agent token totals sum provider usage across turns. Judge usage and time are recorded separately.
`seconds` measures the agent loop, including its MCP calls. It excludes setup, ground truth and judging.
A run passes only with a matching sequence, no unexpected tool errors, a positive judge verdict and no execution failure.
The CLI exits with status 1 for a failed or stopped evaluation.

## Scenarios and limitations

The twelve scenarios cover door counts, family parameters, temporary wall colours,
overlap warnings, sheet placement and undo, snapshot comparison, movement and undo,
`daily_qa`, `pack:model-health`, API lookup before code, sheet listing and read-only refusal.
`daily_qa` is fetched through MCP `prompts/get` and included in the model task.
The coordinator resource is also provided to the model.
Sheets use `revit_query_elements` because this server has no dedicated sheet-list tool.
Snapshot comparison uses two direct snapshots as reference evidence and a model-side comparison.
No snapshot comparison tool is invented.

The refusal scenario exercises a local harness gate. It does not verify the workstation gate.
The model sees the full tool list. Attempted actions are refused before they reach MCP.
Other scenarios allow only the action tool names declared in YAML.
The API lookup scenario permits code execution only with `transaction=auto`, because the server
policy refuses `transaction=none`. The expected flow is a `dry_run` first, then the confirmation
flow. The harness gate does not sandbox the code.
Use only trusted evaluation inputs on the test workstation.

The model must reset colour overrides and undo a move once. The sheet scenario does not rely on undo,
because sheet creation and placement are two separate actions and only the newest one can be undone.
After every changing scenario the harness cleans up itself. It reads `revit_model_health` counts before
the scenario and, if they differ afterwards, deletes the element IDs reported by action results
(`createdElementIds`, plus `sheetId` and `viewportIds` of the sheet tools) with `revit_delete`
through its own session, not the agent gate.
It then compares the health counts with the baseline.
Each run records `cleanup` with `status` (`clean`, `cleaned`, `failed` or `no_baseline`), the deleted IDs and
whether the counts are equal. Anything but `clean` or `cleaned` fails the scenario.
A failed action scenario stops the series after writing its evidence.
Inspect the workstation and restore its state before another run.

No live runs are part of the offline checks. Results remain empty until an authorized live evaluation.
See [Evaluation](../../docs/evaluation.md) for the published results table.

## Offline checks

```sh
cd server
uv run --with pytest pytest -q tests/test_llm_eval.py
cd ..
uvx ruff==0.16.10 check build/llm-eval server/tests/test_llm_eval.py
uvx ruff==0.16.10 format --check build/llm-eval server/tests/test_llm_eval.py
uv run --with-requirements docs/requirements.txt mkdocs build --strict
```

Tests validate the YAML schema and server tool contracts, scoring, pagination, output,
SDK message adapters, CLI JSONL parsers and subprocess commands, model fallback, and the agent loop with fake providers and MCP sessions.
Proxy tests verify document binding, read-only refusal, and the call limit.
They do not connect to providers, SSH or Revit.
The API adapters follow the official
[OpenAI function calling guide](https://developers.openai.com/api/docs/guides/function-calling)
and [Anthropic tool call guide](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls).

CLI event formats follow the [Claude Agent SDK message types](https://github.com/anthropics/claude-agent-sdk-python/blob/main/src/claude_agent_sdk/types.py) and [Codex non-interactive mode](https://developers.openai.com/codex/noninteractive/).

## Family profile

`--profile family` selects the Russian A..E prompts in `family-scenarios.yaml`.
The profile uses independent Revit API probes in `family_verifier.py`, without an LLM judge or sequence score.
The default limits are 120 agent calls and 1,800 seconds per scenario.
The agent receives explicit family and project document identities; the proxy does not replace every target with the project.
Document-free discovery permits `revit_documents`, `revit_ping`, `revit_list_instances`, `revit_ui_state`, and `revit_list_catalog`.
An explicit document must still belong to the run.
Harness refusals return a fixed explanation to the agent without exposing exception details.
A CLI proxy shares the organizer's single upstream process through an authenticated loopback bridge.
`--server-command` selects the agent server, including an installed bimwright rvt-mcp gateway.
`--verifier-command` selects a separate Model MCP server for setup, probes, and cleanup when the agent uses another server.
The verifier command defaults to the agent command for Model MCP runs.

`--family-fixtures` reads a local JSON manifest with this structure:

```json
{
  "output_root": "C:\\RMM-Eval\\runs",
  "scenarios": {
    "A": {"template": "C:\\Templates\\Generic Model.rft", "project": "C:\\RMM-Eval\\fixture.rvt"},
    "B": {"template": "C:\\Templates\\Furniture.rft", "project": "C:\\RMM-Eval\\fixture.rvt"},
    "C": {"template": "C:\\Templates\\Window.rft", "project": "C:\\RMM-Eval\\fixture.rvt"},
    "D": {"template": "C:\\Templates\\Mechanical Equipment.rft", "project": "C:\\RMM-Eval\\fixture.rvt"},
    "E": {"template": "C:\\Templates\\Generic Model.rft", "project": "C:\\RMM-Eval\\fixture.rvt"}
  }
}
```

Paths resolve on the workstation.
Templates must match the selected Revit year.
The clean project fixture contains Level 0 at Z=0, the unjoined 6,000 by 3,000 mm wall, wall types at 150 and 300 mm, a round duct type, and a Supply Air system type.
`--document` identifies a separate open organizer document for filesystem setup and cleanup transactions.
Each scenario receives a unique output folder and copied project.
The organizer creates a fresh family from the template before timing the agent.
For E, a fixed C# probe generates the non-compliant RFA and its defect manifest at run start; no binary fixture is committed.

```sh
uv run build/llm-eval/run.py --profile family --provider codex-cli \
  --server-command 'ssh workstation model-mcp-command' \
  --family-fixtures work/family-fixtures.json --document Organizer \
  --scenarios A --out work/ws/v2/llm-eval
```

Native server challenges prompt the operator for an explicit `yes`.
The journal records decisions, timestamps, code hashes, and wait time; tokens are redacted from family evidence.
EOF denies a challenge.
The harness never unlocks the workstation or changes machine policy.
CLI permission bypass flags do not count as human approval.

The verifier runs after failed or timed-out agent responses and before cleanup.
It checks family metadata, nominal types, three flex value sets, solids, labels, locked reference associations, visibility, host cut volume, reciprocal MEP connections, system membership, and the E report against seeded defect identities.
Saved RFA copies reopen for a second family probe.
Each check records status, expected and actual values, document identity, object IDs, and reason.
Missing, malformed, or failed evidence yields `fail` with a reason for every expected check.
A probe can report `not_applicable` with a reason; calibration requires every reference check to pass.
The artifact score counts passed checks; time, calls, tool errors, and approvals remain separate metrics.
Probe and cleanup calls do not enter the agent call count.

The dry-run contract rolls back the outer transaction.
Commit-time failure processing remains unverified and is excluded from the scored checks.
The result records this limitation explicitly; artifact pass means all implemented dry-run checks pass.
Constraints with unresolved or equivalent sketch references require workstation calibration before comparative scoring.
The current probes do not establish D connection stability after a fresh reload and placement, the exact window frame strip, or every G1..G5 isolation invariant in the design.
Offline tests validate the harness contract, not those Revit behaviors.
These limits must remain visible in any pilot report.

Cleanup closes run documents without saving and deletes temporary RFA/RVT files and their Revit backups only under the unique eval folder.
JSON evidence remains in `--out`.
A denied close, unknown document inventory, failed deletion, or timeout stops the series for workstation recovery.
A timed-out action may have committed; the operator must resolve pending jobs before cleanup approval.

`--toolbaker` requests training A and a paired A-prime control/treatment on fresh documents.
The catalog exposes only a unique run-owned tool name; personal tools remain inaccessible through list/get/run/save.
Training must save a new procedure and pass all artifact checks.
The control sees an empty index and cannot invoke saved tools.
The treatment sees only the training procedure and cannot modify it.
The organizer deletes the run-owned procedure through native confirmation after the experiment.
Servers without the required isolation and deletion surface report `isolation_unavailable`.
Three repeated pairs, adaptive suggestion acceptance, and amortization remain workstation protocol steps.
Speedup is the control time divided by treatment time only when both artifacts and cleanup pass.

The common prompt requires artifact behavior and preserves native confirmations and warnings.
Undo names, activity entries, and server summaries remain diagnostic evidence outside artifact scoring.
D retains the design's explicit single family type; its static body uses a bounds check instead of flex.
E reports `before_violations` and `after_violations` with rule IDs and affected Revit UniqueIds, or the legacy parameter name `w`.
All affected R1 and R2 identities must be covered, including when a rule has several report rows.
Defect labels and row counts do not affect scoring.
Project copies receive unique wall Type Marks before agent execution; the source fixture remains unchanged.

## Family verifier calibration

`--calibrate` executes the fixed C# solutions in [reference/](reference/) without an LLM provider or credentials.
Each selected scenario runs twice on separate fresh family/project copies: the reference, then the reference with its `.broken.cs` mutation.
The shared [project.cs](reference/project.cs) places each family and connects the D duct through the connector-based `Duct.Create` overload.
The same probes, saved-copy verification, native confirmations, and cleanup apply to both variants and agent runs.
`--toolbaker` cannot be combined with calibration.
When the selected server exposes `revit_send_code_to_revit`, calibration submits the fixed authoring steps as `McpDynamicScript` with explicit document selection and native transactions.
The independent Model MCP `--verifier-command` handles fixture setup, save/load, probes, and cleanup.
The C reference retains an 800 mm local sill offset; project rays derive the opening baseline from the required 50 mm frame.

```sh
uv run build/llm-eval/run.py --profile family --calibrate \
  --server-command 'ssh workstation model-mcp-command' \
  --family-fixtures work/family-fixtures.json --document Organizer \
  --scenarios A,B,C,D,E --out work/ws/v2/llm-eval-calibration
```

The CLI exits successfully only when every reference check passes, every mutation produces an explicit failure of its targeted check, and every run cleans up.
Missing probe evidence cannot satisfy a negative control.
The JSON records `calibration_variant`, `calibration_target`, `passed`, lifecycle calls, and raw probe responses.
A low check score on a broken variant is expected; `passed` reports calibration success.

| Scenario | Deliberate defect | Targeted check |
| --- | --- | --- |
| A | Left face alignment unlocked | `alignments` |
| B | LegOffset formula removed | `formula` |
| C | Small Width changed to 1000 mm | `types` |
| D | Equipment disconnected from the duct | `connector_connected` |
| E | Defines Origin disabled on ORG_Center_X | `rule_origin` |

D interprets the design's `Out` requirement as the authored connector setting: `AssignedFlowDirection` must be `Out`.
The final `Direction` is read after connection and regeneration and may be `Out` or `Bidirectional` in this open-ended teaching system.
Autodesk defines [Direction](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/2b1dc55a-6597-7143-4af9-6e92e4c4d024.htm) as instantaneous and system-calculated; [AssignedFlowDirection](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/17222532-ea11-5ce8-9529-be2a17202693.htm) records the assigned setting.
An assigned `In` or `Bidirectional`, or a final calculated `In`, fails.
The reference uses connector-based duct creation; all authoring paths must satisfy the same final predicate.
System membership checks the equipment in `MEPSystem.Elements` and the connected duct in `MechanicalSystem.DuctNetwork`.
The reference fixtures calibrate the implemented oracle; the design coverage limitations above still apply.

A and E pin only the central planes and Bottom.
The outer planes remain movable under the labeled dimensions and locked face alignments.
Pinning all outer planes prevents the required flex and is not part of the design's plane contract.
