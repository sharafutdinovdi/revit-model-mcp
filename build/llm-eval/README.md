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
