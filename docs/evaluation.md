# LLM scenario evaluation

The evaluation harness measures tool use against Snowdon Architectural in Revit 2027.
It supports OpenAI Responses and Anthropic Messages through a local MCP stdio agent loop.
The `claude-cli` and `codex-cli` providers use installed CLIs with existing subscription logins and require no API keys.
It records tool sequence matching, call counts, tool errors, judge verdicts, tokens and time.
Read scenario ground truth comes from direct MCP calls, independently of the model.

## Run the evaluation

Use Python 3.11 or newer and uv. Run from the repository root.
Open the test model on the workstation first. Action scenarios require an isolated test session.

```sh
export OPENAI_API_KEY="$(~/Projects/compass-brain/scripts/secret.sh get OPENAI_API_KEY)"
uv run build/llm-eval/run.py --provider openai --judge openai --out work/ws/v2/llm-eval
```

```sh
export ANTHROPIC_API_KEY="$(~/Projects/compass-brain/scripts/secret.sh get ANTHROPIC_API_KEY)"
uv run build/llm-eval/run.py --provider anthropic --model claude-sonnet-5-5 \
  --judge anthropic --out work/ws/v2/llm-eval
```

```sh
uv run build/llm-eval/run.py --provider claude-cli --judge claude-cli
uv run build/llm-eval/run.py --provider codex-cli --judge codex-cli
```

The CLI defaults are `claude-sonnet-5-5` for Claude Code and `gpt-6.1-sol` for Codex.
Each executable must be installed in `PATH` and authenticated with its subscription login.
`--judge` accepts either CLI provider, including a different provider from the agent.
CLI judges receive the same evidence and return the same verdict JSON as API judges.

Claude uses `stream-json` tool-use blocks and its final result event.
Codex uses JSONL MCP tool-call items and its final agent message.
Both register the stdio server as `revit` through a temporary harness proxy that preserves document binding, action scope, read-only mode, and the call limit.
Codex uses `-c` MCP overrides and `--ignore-user-config`; user configuration files remain unchanged.
CLI invocations have a 1,800 second timeout and preserve available proxy evidence on failure.
The CLI executable, its version, and subscription access require live validation on the evaluation machine.

Do not print API keys or enable shell tracing.
There is no default server command. Pass `--server-command` or set `RMM_EVAL_SERVER_COMMAND`; the flag wins.
For a remote workstation: `ssh <workstation> "cd /d <checkout>\server && set REVIT_MCP_HOST=local&& uv run -q revit-model-mcp"`. Use `--document` to select the document explicitly.
Use `--scenarios doors_per_level,sheet_listing` for a small read-only subset.
The default OpenAI model is `gpt-6.1-sol`, with `gpt-5.6-sol` fallback only on a model access rejection.
An explicit `--model` never falls back. `--judge` selects the judge provider family.

The harness writes a timestamped JSON transcript and a Markdown table to `--out`.
Keep raw evidence outside version control under `work/ws/v2/llm-eval/`.
The model and judge receive model evidence. Server path redaction still applies.
A failed action scenario stops the series. Inspect the workstation before continuing.
A timeout or failed verification can follow a committed change.

The twelve scenarios cover door counts, missing family parameters, temporary wall colours,
overlap warning IDs, sheet placement and undo, snapshot comparison, movement and undo,
the MCP `daily_qa` prompt, `pack:model-health`, API lookup before code, sheet listing,
and a local read-only refusal.
The refusal tests the harness gate. It does not establish workstation gate behavior.
Tool traces provide cleanup evidence. Independent geometry restoration is not measured.

For full CLI options, scoring rules and offline checks, read `build/llm-eval/README.md` in the repository.
The harness follows the
[OpenAI function calling guide](https://developers.openai.com/api/docs/guides/function-calling)
and [Anthropic tool call guide](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls).

CLI event formats follow the [Claude Agent SDK message types](https://github.com/anthropics/claude-agent-sdk-python/blob/main/src/claude_agent_sdk/types.py) and [Codex non-interactive mode](https://developers.openai.com/codex/noninteractive/).

## Results

No live evaluation has been run for this harness yet. The table is intentionally empty.
Do not infer model accuracy from the offline tests.
Publish measured results only after complete runs on the same model and workstation.
Record the actual model ID, including any fallback, and judge model.
Each model needs twelve scenario rows. Keep counts and tokens separate from judge overhead.

| Model | Judge model | Scenario | Sequence match | Calls | Tool errors | Judge pass | Input tokens | Output tokens | Agent seconds | Evidence |
| --- | --- | --- | --- | ---: | ---: | --- | ---: | ---: | ---: | --- |

## Family authoring evaluation

The family profile compares authoring in clean family documents against fixed Russian tasks A..E.
The tasks cover a constrained box, a table with a formula and visibility, a hosted window opening, connected air equipment, and company-rule repairs.
The source prompts and acceptance descriptions are in [family-scenarios.yaml](../build/llm-eval/family-scenarios.yaml).
The [family authoring skill](../skills/revit-family-authoring/SKILL.md) defines the authoring workflow.

Each run uses a compatible workstation template, a copied test project, a unique run folder, and explicit document targets.
The E fixture is generated in Revit with known defects and object identities before the agent starts.
The same prompts run against Model MCP or another command selected by `--server-command`.
A separate Model MCP connection selected by `--verifier-command` supplies the independent probes for another server.
Agent calls finish before the organizer probes the live result and a reopened saved RFA.
Cleanup closes disposable documents without saving and deletes run-owned temporary files.
Cleanup failure stops the series.

The score is checks passed over checks attempted, with `not_verified` counted as incomplete.
The report also records agent time, calls, tool errors, approval challenges, explicit operator decisions, and approval wait time.
Setup, probe, and cleanup times remain separate.
The verifier reports pass or fail only from API evidence, without an LLM judge.
The dry-run probe cannot establish commit-time failure handling and records that limit outside the scored checks.
An artifact pass covers the implemented dry-run checks; it does not certify the complete design.
The complete geometry, reload, isolation, and hosted variant checks still require workstation calibration and extensions listed in the [harness instructions](../build/llm-eval/README.md#family-profile).

The optional ToolBaker flag schedules A and A-prime with training and transfer evidence kept separate.
It requires successful training and an accepted tool index; it does not approve saved code or bake suggestions automatically.
Comparative transfer measurement additionally requires isolated catalogs and paired runs without and with the same accepted tool.

No live family evaluation has run for this change.
No accuracy, speed, server ranking, or ToolBaker gain is reported.
The planned matrix uses Codex CLI and Claude Code with exact model IDs against both servers on the same Revit build.
Run instructions, fixture requirements, probe limits, and recovery rules are in the [family profile documentation](../build/llm-eval/README.md#family-profile).
