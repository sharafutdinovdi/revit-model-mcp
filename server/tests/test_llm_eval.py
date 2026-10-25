"""Offline tests. No provider credentials or workstation connection are used."""

import asyncio
import importlib.util
import json
import os
import time
from contextlib import asynccontextmanager
from pathlib import Path
from types import SimpleNamespace

import jsonschema
import pytest
from mcp import types

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("llm_eval", ROOT / "build/llm-eval/run.py")
eval_run = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(eval_run)
SCENARIOS = eval_run.load_scenarios(ROOT / "build/llm-eval/scenarios.yaml")
GOLDEN = json.loads((Path(__file__).parent / "golden/tools.schema.json").read_text())


def test_scenario_schema_and_server_contracts():
    assert len(SCENARIOS) == 12
    assert len({s["id"] for s in SCENARIOS}) == 12
    for scenario in SCENARIOS:
        for call in scenario["truth"]:
            jsonschema.validate(call["args"], GOLDEN[call["tool"]])
        for sequence in scenario["sequences"]:
            for call in sequence:
                for option in call.get("any_of", [call]):
                    assert option["tool"] in GOLDEN
                    assert set(option["args"]) <= set(GOLDEN[option["tool"]]["properties"])
        assert set(scenario["actions"]) <= GOLDEN.keys()


@pytest.mark.parametrize("change", ["duplicate", "missing", "type"])
def test_scenario_schema_rejects_invalid_files(tmp_path, change):
    data = json.loads(json.dumps({"scenarios": SCENARIOS}))
    if change == "duplicate":
        data["scenarios"][1]["id"] = data["scenarios"][0]["id"]
    elif change == "missing":
        del data["scenarios"][0]["truth"]
    else:
        data["scenarios"][0]["actions"] = "revit_move"
    path = tmp_path / "scenarios.yaml"
    path.write_text(json.dumps(data))
    with pytest.raises((ValueError, jsonschema.ValidationError)):
        eval_run.load_scenarios(path)


def test_scoring_order_arguments_alternatives_and_errors():
    scenario = next(s for s in SCENARIOS if s["id"] == "move_five_undo")
    calls = [
        {"tool": "revit_list_catalog", "args": {}, "error": False},
        {"tool": "revit_query_elements", "args": {"categories": ["Furniture"]}, "error": False},
        {"tool": "revit_move", "args": {"dx_mm": 500, "dy_mm": 0}, "error": False},
        {"tool": "revit_undo_last", "args": {}, "error": False},
    ]
    assert eval_run.score(calls, scenario) == {
        "sequence_match": True,
        "calls": 4,
        "tool_errors": 0,
        "unexpected_errors": 0,
    }
    assert not eval_run.score_sequence(calls[::-1], scenario["sequences"])
    calls[2]["args"]["dx_mm"] = 50
    assert not eval_run.score_sequence(calls, scenario["sequences"])
    calls[2].update(error=True, refused=True)
    assert eval_run.score(calls, scenario)["tool_errors"] == 1
    assert eval_run.score(calls, scenario)["unexpected_errors"] == 0
    assert eval_run.score_sequence(calls, [[{"tool": "missing", "args": {}}], []])


TOOLS = [
    {"name": name, "description": name, "schema": GOLDEN[name], "read_only": read_only}
    for name, read_only in [("revit_query_elements", True), ("revit_move", False)]
]


class FakeSession:
    def __init__(self, error=False, payload=None):
        self.calls = []
        self.error = error
        self.payload = payload if payload is not None else {"success": True, "data": {"total": 5}}

    async def call_tool(self, name, args, *, read_timeout_seconds):
        assert read_timeout_seconds == 900.0
        self.calls.append((name, args))
        return types.CallToolResult(
            content=[], structured_content=self.payload, is_error=self.error
        )


class FakeProvider:
    family = "openai"
    model = "fake"
    results = eval_run.Provider.results

    def __init__(self, turns):
        self.turns = iter(turns)
        self.histories = []

    async def turn(self, history, tools):
        self.histories.append(list(history))
        return next(self.turns)


def turn(calls=None, text="Five items.", complete=True):
    return {
        "messages": [{"role": "assistant", "content": text}],
        "calls": calls or [],
        "text": text,
        "complete": complete,
        "input_tokens": 10,
        "output_tokens": 2,
    }


def request(name="revit_query_elements", args=None, id="a"):
    return {"id": id, "tool": name, "args": {} if args is None else args}


def run_agent(provider, session, scenario=None, max_calls=40):
    return asyncio.run(
        eval_run.agent_loop(
            provider,
            session,
            TOOLS,
            "Count items",
            scenario or {"actions": []},
            "Snowdon",
            max_calls,
        )
    )


def test_agent_loop_multiple_calls_usage_history_and_document_binding():
    provider = FakeProvider(
        [
            turn([request(args='{"document":"wrong"}'), request(id="b")]),
            turn(),
        ]
    )
    session = FakeSession()
    result = run_agent(provider, session)
    assert result["answer"] == "Five items."
    assert result["failure"] is None
    assert result["input_tokens"] == 20 and result["output_tokens"] == 4
    assert len(session.calls) == 2
    assert all(args["document"] == "Snowdon" for _, args in session.calls)
    assert [item["call_id"] for item in provider.histories[1][-2:]] == ["a", "b"]
    assert result["seconds"] >= 0


@pytest.mark.parametrize("kind", ["mcp", "envelope", "unknown", "json", "schema"])
def test_agent_loop_tool_errors_reach_model(kind):
    call = request()
    session = FakeSession(error=kind == "mcp", payload={"success": kind != "envelope"})
    if kind == "unknown":
        call["tool"] = "missing"
    if kind == "json":
        call["args"] = "{"
    if kind == "schema":
        call["args"] = {"limit": 0}
    provider = FakeProvider([turn([call]), turn(text="Tool failed.")])
    result = run_agent(provider, session)
    assert result["trace"][0]["error"]
    assert result["answer"] == "Tool failed."
    assert result["failure"] is None
    assert len(session.calls) == (1 if kind in {"mcp", "envelope"} else 0)


def test_read_only_refusal_never_reaches_mcp():
    session = FakeSession()
    provider = FakeProvider(
        [
            turn([request("revit_move", {"element_ids": [1], "dx_mm": 500, "dy_mm": 0})]),
            turn(text="Read-only mode refused this move. No change was made."),
        ]
    )
    result = run_agent(provider, session, {"actions": [], "read_only": True})
    assert not session.calls
    assert result["trace"][0]["refused"]
    assert "read_only_refusal" in provider.histories[1][-1]["output"]


def test_action_scope_and_authorized_action():
    move = request("revit_move", {"element_ids": [1], "dx_mm": 500, "dy_mm": 0})
    for allowed in (False, True):
        session = FakeSession()
        result = run_agent(
            FakeProvider([turn([move]), turn()]),
            session,
            {"actions": ["revit_move"] if allowed else []},
        )
        assert len(session.calls) == int(allowed)
        assert result["trace"][0]["error"] is not allowed


@pytest.mark.parametrize("incomplete", [False, True])
def test_agent_loop_limits_and_incomplete_responses(incomplete):
    provider = FakeProvider([turn([request()], complete=not incomplete), turn([request()])])
    result = run_agent(provider, FakeSession(), max_calls=1)
    assert result["failure"] == "ValueError"
    assert result["answer"] == ""
    assert len(result["trace"]) == (0 if incomplete else 1)


def test_ground_truth_is_direct_paginated_and_not_model_generated():
    class PagedSession(FakeSession):
        async def call_tool(self, name, args, **kwargs):
            result = await super().call_tool(name, args, **kwargs)
            result.structured_content = {
                "data": {"elements": [{"id": len(self.calls)}], "hasMore": len(self.calls) == 1}
            }
            return result

    session = PagedSession()
    scenario = {"truth": [{"tool": "revit_query_elements", "args": {"limit": 1}}]}
    truth = asyncio.run(eval_run.ground_truth(session, TOOLS, scenario, "Snowdon"))
    assert len(truth) == 2
    assert session.calls[1][1] == {"limit": 1, "offset": 1, "document": "Snowdon"}
    assert "offset" not in truth[0]["args"]
    with pytest.raises(ValueError, match="read-only"):
        asyncio.run(
            eval_run.ground_truth(session, TOOLS, {"truth": [request("revit_move")]}, "Snowdon")
        )


def test_judge_uses_evidence_and_validates_verdict():
    provider = FakeProvider([turn(text='{"pass":true,"reason":"Counts match"}')])
    scenario = SCENARIOS[0]
    truth = [{"direct_count": 5}]
    verdict = asyncio.run(
        eval_run.judge_answer(provider, scenario, truth, {"answer": "5", "trace": []})
    )
    assert verdict["pass"] is True
    assert '"direct_count": 5' in provider.histories[0][0]["content"]
    invalid = FakeProvider([turn(text='{"pass":"yes","reason":"wrong type"}')])
    with pytest.raises(ValueError):
        asyncio.run(eval_run.judge_answer(invalid, scenario, truth, {"answer": "5", "trace": []}))


def test_output_pair_and_default_command(tmp_path):
    run = {
        "scenario": "doors",
        "model": "fake",
        "sequence_match": True,
        "calls": 2,
        "tool_errors": 0,
        "judge": {"pass": True},
        "input_tokens": 20,
        "output_tokens": 4,
        "seconds": 1.25,
    }
    eval_run.write_outputs({"runs": [run]}, tmp_path)
    eval_run.write_outputs({"runs": [run]}, tmp_path)
    assert len(list(tmp_path.glob("*.json"))) == 2
    assert len(list(tmp_path.glob("*.md"))) == 2
    assert (
        "| doors | fake | True | 2 | 0 | True | n/a | 20/4 | 1.25 |"
        in next(tmp_path.glob("*.md")).read_text()
    )
    assert eval_run.parse_args(["--server-command", "fake-server"]).model is None


def test_server_command_has_no_default(monkeypatch, capsys):
    monkeypatch.delenv("RMM_EVAL_SERVER_COMMAND", raising=False)
    with pytest.raises(SystemExit) as exit_info:
        eval_run.parse_args([])
    assert exit_info.value.code == 2
    assert "RMM_EVAL_SERVER_COMMAND" in capsys.readouterr().err


def test_server_command_env_var_and_flag_precedence(monkeypatch):
    monkeypatch.setenv("RMM_EVAL_SERVER_COMMAND", "env-server")
    assert eval_run.parse_args([]).server_command == "env-server"
    assert eval_run.parse_args(["--server-command", "flag-server"]).server_command == "flag-server"


def block(**kwargs):
    return SimpleNamespace(model_dump=lambda **_: kwargs)


@pytest.mark.parametrize("family", ["openai", "anthropic"])
def test_sdk_adapters_round_trip_native_tool_blocks(family):
    sent = []

    async def create(**kwargs):
        sent.append(kwargs)
        usage = SimpleNamespace(input_tokens=11, output_tokens=3)
        if family == "openai":
            return SimpleNamespace(
                output=[
                    block(type="reasoning", id="r"),
                    block(
                        type="function_call",
                        call_id="c",
                        name="revit_query_elements",
                        arguments="{}",
                    ),
                ],
                output_text="",
                usage=usage,
                status="completed",
            )
        return SimpleNamespace(
            content=[block(type="tool_use", id="c", name="revit_query_elements", input={})],
            usage=usage,
            stop_reason="tool_use",
        )

    client = SimpleNamespace(
        responses=SimpleNamespace(create=create), messages=SimpleNamespace(create=create)
    )
    provider = eval_run.Provider(family, client=client)
    reply = asyncio.run(provider.turn([{"role": "user", "content": "Count"}], TOOLS))
    assert reply["calls"][0]["tool"] == "revit_query_elements"
    assert reply["input_tokens"] == 11 and reply["output_tokens"] == 3
    assert sent[0]["tools"][0]["name"] == "revit_query_elements"
    results = provider.results([{**reply["calls"][0], "result": {"total": 5}, "error": True}])
    if family == "openai":
        assert results[0]["call_id"] == "c"
        assert reply["messages"][0]["type"] == "reasoning"
        assert sent[0]["store"] is False
    else:
        assert results[0]["content"][0]["tool_use_id"] == "c"
        assert results[0]["content"][0]["is_error"] is True


def test_default_openai_falls_back_only_for_unavailable_model():
    class ModelError(Exception):
        code = "model_not_found"

    models = []

    async def create(**kwargs):
        models.append(kwargs["model"])
        if len(models) == 1:
            raise ModelError()
        return SimpleNamespace(
            output=[],
            output_text="ok",
            status="completed",
            usage=SimpleNamespace(input_tokens=1, output_tokens=1),
        )

    client = SimpleNamespace(responses=SimpleNamespace(create=create))
    provider = eval_run.Provider("openai", client=client)
    asyncio.run(provider.turn([], []))
    assert models == ["gpt-6.1-sol", "gpt-5.6-sol"]
    models.clear()
    with pytest.raises(ModelError):
        asyncio.run(eval_run.Provider("openai", "explicit", client).turn([], []))
    assert models == ["explicit"]


@pytest.mark.parametrize("scenario_id", ["sheet_listing", "daily_qa"])
def test_main_wires_stdio_discovery_prompt_and_judge(monkeypatch, scenario_id):
    from contextlib import asynccontextmanager

    import mcp
    import mcp.client.stdio

    created = []
    scenario = next(s for s in SCENARIOS if s["id"] == scenario_id)
    requested = [step.get("any_of", [step])[0] for step in scenario["sequences"][0]]
    names = {t["name"] for t in TOOLS} | {c["tool"] for c in scenario["truth"]}

    class Session(FakeSession):
        async def initialize(self):
            return types.InitializeResult(
                protocol_version="2025-11-25",
                capabilities=types.ServerCapabilities(),
                server_info=types.Implementation(name="fake", version="1"),
            )

        async def list_tools(self, *, params=None):
            assert params is None
            return types.ListToolsResult(
                tools=[
                    types.Tool(
                        name=name,
                        input_schema=GOLDEN[name],
                        annotations=types.ToolAnnotations(read_only_hint=name != "revit_move"),
                    )
                    for name in names
                ]
            )

        async def call_tool(self, name, args, **kwargs):
            if name == "revit_document_info":
                return types.CallToolResult(
                    content=[],
                    structured_content={
                        "success": True,
                        "responder": {
                            "documentName": "Snowdon Architectural",
                            "revitVersion": "2027",
                        },
                    },
                )
            return await super().call_tool(name, args, **kwargs)

        async def get_prompt(self, name, arguments):
            assert name == "daily_qa"
            assert arguments == {"required_parameters": "Doors: Mark"}
            return SimpleNamespace(
                messages=[SimpleNamespace(content=SimpleNamespace(text="Rendered daily QA"))]
            )

        async def read_resource(self, uri):
            assert uri == "revit://guides/coordinator"
            return SimpleNamespace(contents=[SimpleNamespace(text="Coordinator guide")])

    session = Session()

    @asynccontextmanager
    async def stdio(parameters):
        assert parameters.command == "fake-server"
        yield None, None

    @asynccontextmanager
    async def client(read, write):
        yield session

    def provider(family, model):
        instance = FakeProvider(
            [
                turn([request(c["tool"], c["args"], id=str(i)) for i, c in enumerate(requested)]),
                turn(text="Five sheets."),
            ]
            if not created
            else [turn(text='{"pass":true,"reason":"Matches"}')]
        )
        created.append(instance)
        return instance

    monkeypatch.setattr(mcp.client.stdio, "stdio_client", stdio)
    monkeypatch.setattr(mcp, "ClientSession", client)
    monkeypatch.setattr(eval_run, "Provider", provider)
    args = eval_run.parse_args(["--server-command", "fake-server", "--scenarios", scenario_id])
    result = asyncio.run(eval_run.main_async(args))
    assert result["runs"][0]["passed"]
    assert result["runs"][0]["calls"] == len(requested)
    assert len(session.calls) == (2 if scenario_id == "sheet_listing" else 12)
    assert "ground_truth" not in created[0].histories[0][0]["content"]
    assert "Coordinator guide" in created[0].histories[0][0]["content"]


def test_families_missing_parameter_truth_is_bounded():
    scenario = next(s for s in SCENARIOS if s["id"] == "families_missing_parameter")
    assert [c["tool"] for c in scenario["truth"]] == ["revit_parameter_fill_check"]
    assert "'*'" not in json.dumps(scenario) and '"*"' not in json.dumps(scenario)


def test_daily_qa_truth_family_audit_is_bounded():
    scenario = next(s for s in SCENARIOS if s["id"] == "daily_qa")
    audit = next(c for c in scenario["truth"] if c["tool"] == "revit_family_audit")
    assert "*" not in audit["args"]["families"]
    assert 0 < len(audit["args"]["families"]) <= 15


def test_sheet_and_api_scenarios_expect_harness_cleanup_and_auto_transaction():
    sheet = next(s for s in SCENARIOS if s["id"] == "create_sheet_plan")
    assert "revit_undo_last" not in sheet["actions"]
    assert all(c["tool"] != "revit_undo_last" for c in sheet["sequences"][0])
    api = next(s for s in SCENARIOS if s["id"] == "api_lookup_execute")
    code = [c["args"] for c in api["sequences"][0] if c["tool"] == "revit_execute_code"]
    assert code == [
        {"transaction": "auto", "dry_run": True},
        {"transaction": "auto"},
    ]
    assert "transaction none" in api["prompt"] and "dry_run" in api["prompt"]


@pytest.mark.parametrize("transaction,allowed", [("auto", True), ("none", False)])
def test_execute_code_gate_requires_auto_transaction(transaction, allowed):
    tools = [
        {
            "name": "revit_execute_code",
            "description": "",
            "schema": {"type": "object"},
            "read_only": False,
        }
    ]
    session = FakeSession()
    entry = asyncio.run(
        eval_run.execute_call(
            session,
            {t["name"]: t for t in tools},
            {"tool": "revit_execute_code", "args": {"transaction": transaction}},
            {"actions": ["revit_execute_code"]},
            "Snowdon",
        )
    )
    assert entry["error"] is not allowed
    assert len(session.calls) == int(allowed)


def test_created_ids_collects_successful_action_results_only():
    trace = [
        {
            "tool": "revit_create_sheet",
            "error": False,
            "result": {
                "data": {"dryRun": False, "sheetId": 7, "createdElementIds": [8, 8, -1, "x"]}
            },
        },
        {
            "tool": "revit_place_views_on_sheet",
            "error": False,
            "result": {"data": {"dryRun": False, "viewportIds": [9]}},
        },
        {"tool": "revit_create_sheet", "error": True, "result": {"data": {"sheetId": 99}}},
        {"tool": "revit_view_info", "error": False, "result": {"data": {"sheetId": 100}}},
    ]
    assert eval_run.created_ids(trace) == [7, 8, 9]


@pytest.mark.parametrize(
    "flags",
    [
        {"dryRun": True},
        {"rolledBack": True},
        {"committed": False},
        {"success": False},
    ],
)
def test_created_ids_excludes_uncommitted_results_at_any_depth(flags):
    trace = [
        {
            "tool": "revit_create_sheet",
            "error": False,
            "result": {
                "data": {"dryRun": False, **flags, "sheetId": 7, "data": {"createdElementIds": [8]}}
            },
        }
    ]
    assert eval_run.created_ids(trace) == []


def test_created_ids_excludes_dry_run_arguments_and_keeps_committed_batch_steps():
    trace = [
        {
            "tool": "revit_place_views_on_sheet",
            "args": {"dry_run": True},
            "error": False,
            "result": {"data": {"dryRun": False, "viewportIds": [3327631]}},
        },
        {
            "tool": "revit_batch",
            "error": False,
            "result": {
                "data": {
                    "committed": True,
                    "steps": [
                        {"data": {"createdElementIds": [3327617, 3327632]}},
                        {"rolledBack": True, "data": {"createdElementIds": [99]}},
                    ],
                }
            },
        },
    ]
    assert eval_run.created_ids(trace) == [3327617, 3327632]


class CleanupSession:
    """Health counts change until the harness deletes the created IDs."""

    def __init__(self, delete_works=True):
        self.calls = []
        self.dirty = True
        self.delete_works = delete_works

    async def call_tool(self, name, args, *, read_timeout_seconds):
        self.calls.append((name, args))
        if name == "revit_delete":
            self.dirty = not self.delete_works
        counts = {"sheets": 68 if self.dirty else 67}
        return types.CallToolResult(
            content=[], structured_content={"success": True, "data": {"counts": counts}}
        )


@pytest.mark.parametrize("delete_works", [True, False])
def test_harness_cleanup_deletes_created_ids_and_verifies_counts(delete_works):
    session = CleanupSession(delete_works)
    trace = [
        {
            "tool": "revit_create_sheet",
            "error": False,
            "result": {"data": {"dryRun": False, "sheetId": 7}},
        }
    ]
    report = asyncio.run(eval_run.cleanup_scenario(session, "Snowdon", {"sheets": 67}, trace))
    assert report["status"] == ("cleaned" if delete_works else "failed")
    assert report["deleted_ids"] == [7]
    assert report["counts_equal"] is delete_works
    assert ("revit_delete", {"document": "Snowdon", "element_ids": [7]}) in session.calls


def test_harness_cleanup_skips_delete_when_counts_already_match():
    session = CleanupSession()
    session.dirty = False
    trace = [
        {
            "tool": "revit_create_sheet",
            "error": False,
            "result": {"data": {"dryRun": False, "sheetId": 7}},
        }
    ]
    report = asyncio.run(eval_run.cleanup_scenario(session, "Snowdon", {"sheets": 67}, trace))
    assert report["status"] == "clean"
    assert all(name != "revit_delete" for name, _ in session.calls)
    missing = asyncio.run(eval_run.cleanup_scenario(session, "Snowdon", None, trace))
    assert missing["status"] == "no_baseline"


def test_scenario_timeout_default_and_validation():
    base = ["--server-command", "fake-server"]
    assert eval_run.parse_args(base).scenario_timeout == 300
    assert eval_run.parse_args([*base, "--scenario-timeout", "5"]).scenario_timeout == 5
    with pytest.raises(SystemExit):
        eval_run.parse_args([*base, "--scenario-timeout", "0"])


def test_agent_loop_timeout_preserves_partial_trace():
    class SlowSession(FakeSession):
        async def call_tool(self, name, args, **kwargs):
            await asyncio.sleep(10)

    result = asyncio.run(
        eval_run.agent_loop(
            FakeProvider([turn([request()])]),
            SlowSession(),
            TOOLS,
            "Count items",
            {"actions": []},
            "Snowdon",
            40,
            0.05,
        )
    )
    assert result["failure"] == "TimeoutError"


def test_series_continues_after_ground_truth_timeout(monkeypatch):
    from contextlib import asynccontextmanager

    import mcp
    import mcp.client.stdio

    ids = ["sheet_listing", "daily_qa"]
    names = {c["tool"] for s in SCENARIOS if s["id"] in ids for c in s["truth"]}
    names |= {"revit_ping"}

    class Session(FakeSession):
        async def initialize(self):
            return types.InitializeResult(
                protocol_version="2025-11-25",
                capabilities=types.ServerCapabilities(),
                server_info=types.Implementation(name="fake", version="1"),
            )

        async def list_tools(self, *, params=None):
            return types.ListToolsResult(
                tools=[
                    types.Tool(
                        name=name,
                        input_schema=GOLDEN[name],
                        annotations=types.ToolAnnotations(read_only_hint=True),
                    )
                    for name in names
                ]
            )

        async def call_tool(self, name, args, **kwargs):
            if name == "revit_document_info":
                return types.CallToolResult(
                    content=[],
                    structured_content={
                        "responder": {
                            "documentName": "Snowdon Architectural",
                            "revitVersion": "2027",
                        }
                    },
                )
            if name != "revit_ping" and len(self.calls) < 1:
                self.calls.append((name, args))
                await asyncio.sleep(10)
            return await super().call_tool(name, args, **kwargs)

        async def read_resource(self, uri):
            return SimpleNamespace(contents=[SimpleNamespace(text="Guide")])

        async def get_prompt(self, name, arguments):
            return SimpleNamespace(messages=[])

    session = Session()

    @asynccontextmanager
    async def stdio(parameters):
        yield None, None

    @asynccontextmanager
    async def client(read, write):
        yield session

    monkeypatch.setattr(mcp.client.stdio, "stdio_client", stdio)
    monkeypatch.setattr(mcp, "ClientSession", client)
    monkeypatch.setattr(eval_run, "Provider", lambda family, model: FakeProvider([]))
    args = eval_run.parse_args(
        [
            "--server-command",
            "fake-server",
            "--scenarios",
            ",".join(ids),
            "--scenario-timeout",
            "0.05",
        ]
    )
    result = asyncio.run(eval_run.main_async(args))
    assert result["runs"][0]["failure"] == "TimeoutError"
    assert not result["runs"][0]["passed"]
    assert len(result["runs"]) == 2
    assert "stopped" not in result
    assert any(name == "revit_ping" for name, _ in session.calls)


def test_series_stops_when_session_does_not_answer_ping():
    class Dead(FakeSession):
        async def call_tool(self, name, args, **kwargs):
            raise RuntimeError("closed")

    assert not asyncio.run(eval_run.session_alive(Dead(), "Snowdon"))
    assert asyncio.run(eval_run.session_alive(FakeSession(), "Snowdon"))


def test_provider_failure_preserves_partial_trace():
    class FailingProvider(FakeProvider):
        async def turn(self, history, tools):
            if self.histories:
                raise RuntimeError("sensitive exception text")
            return await super().turn(history, tools)

    result = run_agent(FailingProvider([turn([request()])]), FakeSession())
    assert result["failure"] == "RuntimeError"
    assert len(result["trace"]) == 1
    assert "sensitive exception text" not in json.dumps(result)


def cli_events(family, *, judge=False):
    answer = '{"pass":true,"reason":"Counts match"}' if judge else "Five items."
    if family == "claude-cli":
        return (
            []
            if judge
            else [
                {
                    "type": "assistant",
                    "message": {
                        "content": [
                            {
                                "type": "tool_use",
                                "id": "c",
                                "name": "mcp__revit__revit_query_elements",
                                "input": {"document": "wrong"},
                            },
                        ]
                    },
                },
                {
                    "type": "user",
                    "message": {
                        "content": [
                            {
                                "type": "tool_result",
                                "tool_use_id": "c",
                                "content": [{"type": "text", "text": '{"success":true,"total":5}'}],
                            },
                        ]
                    },
                },
            ]
        ) + [
            {
                "type": "result",
                "subtype": "success",
                "is_error": False,
                "result": answer,
                "usage": {"input_tokens": 7, "cache_read_input_tokens": 3, "output_tokens": 2},
            }
        ]
    item = {
        "id": "c",
        "type": "mcp_tool_call",
        "server": "revit",
        "tool": "revit_query_elements",
        "arguments": {"document": "wrong"},
    }
    return (
        []
        if judge
        else [
            {"type": "item.started", "item": {**item, "status": "in_progress"}},
            {
                "type": "item.completed",
                "item": {
                    **item,
                    "status": "completed",
                    "result": {"content": [], "structured_content": {"success": True, "total": 5}},
                },
            },
        ]
    ) + [
        {"type": "item.completed", "item": {"id": "a", "type": "agent_message", "text": answer}},
        {"type": "turn.completed", "usage": {"input_tokens": 10, "output_tokens": 2}},
    ]


def fake_cli(
    monkeypatch, events, *, returncode=0, record_proxy=False, malformed=False, stall=False
):
    commands = []

    async def spawn(*command, **kwargs):
        commands.append(command)
        prompt = kwargs["stdin"].read().decode("utf-8")
        assert prompt
        spawn.prompts.append(prompt)
        assert kwargs["stderr"] == asyncio.subprocess.DEVNULL
        assert kwargs["start_new_session"] is True
        if record_proxy:
            directory = Path(kwargs["cwd"])
            config = json.loads((directory / "proxy.json").read_text())
            Path(config["trace"]).write_text(
                json.dumps(
                    {
                        "tool": "revit_query_elements",
                        "args": {"document": config["document"]},
                        "error": False,
                        "refused": False,
                        "result": {"success": True, "total": 5},
                        "seconds": 0.01,
                    }
                )
                + "\n"
            )
        stdout = asyncio.StreamReader()
        stdout.feed_data(("\n".join(json.dumps(e) for e in events) + "\n").encode())
        if malformed:
            stdout.feed_data(b"invalid json\n")
        if not stall:
            stdout.feed_eof()

        async def wait():
            process.returncode = returncode
            return returncode

        def kill():
            process.returncode = -9

        process = SimpleNamespace(stdout=stdout, returncode=None, wait=wait, kill=kill, pid=424242)
        return process

    spawn.prompts = []
    monkeypatch.setattr(asyncio, "create_subprocess_exec", spawn)
    monkeypatch.setattr(eval_run.os, "killpg", lambda pid, sig: None)
    return commands


@pytest.mark.parametrize("family", ["claude-cli", "codex-cli"])
def test_cli_subprocess_parser_and_guarded_configuration(monkeypatch, family):
    events = cli_events(family)
    commands = fake_cli(monkeypatch, events, record_proxy=True)
    provider = eval_run.CliProvider(family)
    run = asyncio.run(
        provider.run(
            "Count items",
            'fake-server "argument with spaces"',
            TOOLS,
            {"actions": []},
            "Snowdon",
            3,
        )
    )
    assert run["failure"] is None
    assert run["answer"] == "Five items."
    assert run["input_tokens"] == 10 and run["output_tokens"] == 2
    assert run["trace"][0]["args"] == {"document": "Snowdon"}
    assert run["transcript"] == events
    parsed = eval_run.parse_cli_events(family, events)
    assert len(parsed["calls"]) == 1
    assert parsed["calls"][0]["tool"] == "revit_query_elements"
    assert parsed["calls"][0]["result"]["total"] == 5
    assert not parsed["calls"][0]["error"]
    command = commands[0]
    assert "Count items" not in command
    if family == "codex-cli":
        assert command[-2:] == ("--", "-")
    assert provider.model == eval_run.DEFAULT_MODELS[family]
    if family == "claude-cli":
        assert command[command.index("--output-format") + 1] == "stream-json"
        assert "--verbose" in command and "--dangerously-skip-permissions" in command
        assert command[command.index("--allowedTools") + 1] == "mcp__revit__*"
        server = json.loads(command[command.index("--mcp-config") + 1])["mcpServers"]["revit"]
        assert server["type"] == "stdio"
        assert server["args"][-2] == "--stdio-proxy"
    else:
        assert command[:3] == ("codex", "exec", "--json")
        overrides = [command[i + 1] for i, value in enumerate(command) if value == "-c"]
        assert any(o.startswith("mcp_servers.revit.command=") for o in overrides)
        args = next(
            o.split("=", 1)[1] for o in overrides if o.startswith("mcp_servers.revit.args=")
        )
        assert json.loads(args)[-2] == "--stdio-proxy"
        assert "--ignore-user-config" in command
        assert 'mcp_servers.revit.default_tools_approval_mode="approve"' in overrides


@pytest.mark.parametrize("family", ["claude-cli", "codex-cli"])
def test_cli_judge_uses_existing_verdict_contract_without_mcp(monkeypatch, family):
    commands = fake_cli(monkeypatch, cli_events(family, judge=True))
    verdict = asyncio.run(
        eval_run.judge_answer(
            eval_run.CliProvider(family, "explicit-model"),
            SCENARIOS[0],
            [{"count": 5}],
            {"answer": "5", "trace": []},
        )
    )
    assert verdict["pass"] is True and verdict["model"] == "explicit-model"
    command = commands[0]
    assert "explicit-model" in command
    if family == "claude-cli":
        assert json.loads(command[command.index("--mcp-config") + 1]) == {"mcpServers": {}}
    else:
        assert not any("mcp_servers.revit" in value for value in command)
    assert '"count": 5' in asyncio.create_subprocess_exec.prompts[0]


@pytest.mark.parametrize("family", ["claude-cli", "codex-cli"])
@pytest.mark.parametrize("failure", ["exit", "missing_final", "malformed", "trace_mismatch"])
def test_cli_failures_preserve_partial_trace(monkeypatch, family, failure):
    events = cli_events(family)
    if failure == "missing_final":
        events = events[:-1]
    fake_cli(
        monkeypatch,
        events,
        returncode=1 if failure == "exit" else 0,
        record_proxy=failure != "trace_mismatch",
        malformed=failure == "malformed",
    )
    result = asyncio.run(
        eval_run.CliProvider(family).run(
            "Count",
            "fake-server",
            TOOLS,
            {"actions": []},
            "Snowdon",
            40,
        )
    )
    assert result["failure"] is not None
    assert len(result["trace"]) == (0 if failure == "trace_mismatch" else 1)


@pytest.mark.parametrize("family", ["claude-cli", "codex-cli"])
def test_cli_tool_errors_and_terminal_failure(family):
    events = cli_events(family)
    if family == "claude-cli":
        block = events[1]["message"]["content"][0]
        block["content"] = '{"success":false}'
        events[-1]["is_error"] = True
    else:
        events[1]["item"]["result"]["structured_content"] = {"success": False}
        events.append({"type": "turn.failed", "error": {"message": "failure"}})
    parsed = eval_run.parse_cli_events(family, events)
    assert parsed["calls"][0]["error"]
    assert not parsed["complete"]


def test_stdio_proxy_enforces_gate_document_and_call_limit(monkeypatch, tmp_path):
    from contextlib import asynccontextmanager

    import mcp
    import mcp.client.stdio
    import mcp.server.lowlevel
    import mcp.server.stdio

    session = FakeSession()
    results = []

    class Server(mcp.server.lowlevel.Server):
        async def run(self, *args):
            list_tools = self.get_request_handler("tools/list").handler
            call_tool = self.get_request_handler("tools/call").handler
            assert len((await list_tools(None, None)).tools) == len(TOOLS)
            for name, arguments in [
                ("revit_move", {"element_ids": [1], "dx_mm": 500, "dy_mm": 0}),
                ("revit_query_elements", {"document": "wrong"}),
                ("revit_query_elements", {}),
            ]:
                results.append(
                    await call_tool(
                        None,
                        types.CallToolRequestParams(
                            name=name,
                            arguments=arguments,
                        ),
                    )
                )

    @asynccontextmanager
    async def transport(*args):
        yield None, None

    @asynccontextmanager
    async def client(*args):
        yield session

    async def initialize():
        return None

    session.initialize = initialize
    monkeypatch.setattr(mcp.client.stdio, "stdio_client", transport)
    monkeypatch.setattr(mcp, "ClientSession", client)
    monkeypatch.setattr(mcp.server.lowlevel, "Server", Server)
    monkeypatch.setattr(mcp.server.stdio, "stdio_server", transport)
    config = tmp_path / "proxy.json"
    trace = tmp_path / "trace.jsonl"
    config.write_text(
        json.dumps(
            {
                "server_command": "fake-server",
                "tools": TOOLS,
                "scenario": {"actions": [], "read_only": True},
                "document": "Snowdon",
                "max_calls": 2,
                "trace": str(trace),
            }
        )
    )
    asyncio.run(eval_run.stdio_proxy(config))
    assert [result.is_error for result in results] == [True, False, True]
    assert session.calls == [("revit_query_elements", {"document": "Snowdon"})]
    calls = [json.loads(line) for line in trace.read_text().splitlines()]
    assert calls[0]["refused"]
    assert calls[2]["result"]["error"] == "Tool call limit reached"


def test_stdio_proxy_round_trip_without_models(tmp_path):
    import shlex
    import sys

    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client

    upstream = """
import asyncio
from mcp import types
from mcp.server.lowlevel import Server
from mcp.server.stdio import stdio_server
async def call(context, params):
    return types.CallToolResult(content=[types.TextContent(
        type="text", text='{"success": true, "total": 5}')])
async def list_tools(context, params):
    return types.ListToolsResult(tools=[types.Tool(
        name="revit_query_elements", input_schema={"type": "object"})])
async def main():
    server = Server("fake", on_call_tool=call, on_list_tools=list_tools)
    async with stdio_server() as (read, write):
        await server.run(read, write, server.create_initialization_options())
asyncio.run(main())
"""
    config_path = tmp_path / "proxy.json"
    trace_path = tmp_path / "trace.jsonl"
    config_path.write_text(
        json.dumps(
            {
                "server_command": shlex.join([sys.executable, "-c", upstream]),
                "tools": TOOLS,
                "scenario": {"actions": [], "read_only": True},
                "document": "Snowdon",
                "max_calls": 2,
                "trace": str(trace_path),
            }
        )
    )

    async def check():
        parameters = StdioServerParameters(
            command=sys.executable,
            args=[
                str(ROOT / "build/llm-eval/run.py"),
                "--stdio-proxy",
                str(config_path),
            ],
        )
        async with stdio_client(parameters) as (read, write), ClientSession(read, write) as session:
            await session.initialize()
            assert len((await session.list_tools()).tools) == len(TOOLS)
            result = await session.call_tool("revit_query_elements", {"document": "wrong"})
            assert not result.is_error
            assert json.loads(result.content[0].text)["total"] == 5
            refusal = await session.call_tool(
                "revit_move",
                {
                    "element_ids": [1],
                    "dx_mm": 500,
                    "dy_mm": 0,
                },
            )
            assert refusal.is_error
            assert "read_only_refusal" in refusal.content[0].text

    asyncio.run(check())
    calls = [json.loads(line) for line in trace_path.read_text().splitlines()]
    assert calls[0]["args"]["document"] == "Snowdon"
    assert calls[1]["refused"]


@pytest.mark.parametrize("family", ["claude-cli", "codex-cli"])
def test_cli_timeout_preserves_proxy_evidence(monkeypatch, family):
    real_timeout = asyncio.timeout
    fake_cli(monkeypatch, cli_events(family)[:1], record_proxy=True, stall=True)
    monkeypatch.setattr(asyncio, "timeout", lambda _: real_timeout(0.01))
    result = asyncio.run(
        eval_run.CliProvider(family).run(
            "Count",
            "fake-server",
            TOOLS,
            {"actions": []},
            "Snowdon",
            40,
        )
    )
    assert result["failure"] == "TimeoutError"
    assert len(result["trace"]) == 1


def test_cli_timeout_kills_grandchild_process_group(monkeypatch, tmp_path):
    marker = tmp_path / "grandchild.pid"
    script = tmp_path / "bin" / "codex"
    script.parent.mkdir()
    script.write_text(f"#!/bin/sh\nsleep 60 &\necho $! > '{marker}'\nsleep 60\n")
    script.chmod(0o755)
    monkeypatch.setenv("PATH", f"{script.parent}{os.pathsep}{os.environ['PATH']}")
    real_timeout = asyncio.timeout
    process_ids = {}

    @asynccontextmanager
    async def timeout_after_grandchild_ready(_):
        async with real_timeout(5.0):
            while not marker.exists() or not marker.read_text().strip():
                await asyncio.sleep(0.01)
        process_ids["pid"] = int(marker.read_text())
        process_ids["pgid"] = os.getpgid(process_ids["pid"])
        async with real_timeout(0.01):
            yield

    monkeypatch.setattr(asyncio, "timeout", timeout_after_grandchild_ready)
    result = asyncio.run(eval_run.CliProvider("codex-cli").invoke("Count", tmp_path, None))
    assert result["failure"] == "TimeoutError"
    assert process_ids, "grandchild did not write its PID within five seconds"
    deadline = time.monotonic() + 5.0
    while True:
        try:
            os.kill(process_ids["pid"], 0)
        except ProcessLookupError:
            try:
                os.killpg(process_ids["pgid"], 0)
            except ProcessLookupError:
                break
        if time.monotonic() >= deadline:
            os.killpg(process_ids["pgid"], 9)
            pytest.fail("grandchild or its process group survived the timeout")
        time.sleep(0.01)


@pytest.mark.parametrize(
    ("scenario_id", "tool", "args"),
    [
        ("sheet_listing", "revit_list_sheets", {}),
        ("colour_thick_walls", "revit_view_elements", {"categories": ["Walls"]}),
        ("colour_thick_walls", "revit_view_summary", {}),
        ("model_health_audit", "revit_model_audit", {"compare_with": "none", "save": False}),
    ],
)
def test_scenario_accepts_equivalent_evidence(scenario_id, tool, args):
    scenario = next(s for s in SCENARIOS if s["id"] == scenario_id)
    assert eval_run.score_sequence([{"tool": tool, "args": args}], scenario["sequences"])


def test_model_health_sequence_rejects_another_rule_pack():
    scenario = next(s for s in SCENARIOS if s["id"] == "model_health_audit")
    assert not eval_run.score_sequence(
        [
            {
                "tool": "revit_model_audit",
                "args": {"rules": "other-pack", "compare_with": "none", "save": False},
            }
        ],
        scenario["sequences"],
    )


@pytest.mark.parametrize("restored", [True, False])
def test_cleanup_missing_elements_are_clean_but_counts_must_match(restored):
    class Session(CleanupSession):
        async def call_tool(self, name, args, **kwargs):
            if name == "revit_delete" and args["element_ids"] == [8]:
                self.dirty = not restored
                return types.CallToolResult(
                    content=[],
                    is_error=True,
                    structured_content={"success": False, "error": "Element 8 was not found."},
                )
            return await super().call_tool(name, args, **kwargs)

    session = Session()
    trace = [
        {
            "tool": "revit_create_sheet",
            "error": False,
            "result": {"data": {"dryRun": False, "sheetId": 7, "viewportIds": [8]}},
        }
    ]
    report = asyncio.run(eval_run.cleanup_scenario(session, "Snowdon", {"sheets": 67}, trace))
    assert report["already_clean_ids"] == [8]
    assert not report["delete_error"]
    assert report["counts_equal"] is restored
    assert report["status"] == ("cleaned" if restored else "failed")
    assert session.calls[-1][0] == "revit_model_health"


@pytest.mark.parametrize("family", ["claude-cli", "codex-cli"])
def test_cli_native_resource_evidence_and_mismatch_note(monkeypatch, family):
    events = cli_events(family)
    if family == "codex-cli":
        events.insert(
            2,
            {
                "type": "item.completed",
                "item": {
                    "id": "resource",
                    "type": "mcp_tool_call",
                    "server": "revit",
                    "tool": "read_resource",
                    "arguments": {"uri": "revit://guides/qa"},
                    "status": "failed",
                    "error": {"message": "Method not found"},
                },
            },
        )
    else:
        events[0]["message"]["content"].append(
            {
                "type": "tool_use",
                "id": "resource",
                "name": "mcp__revit__read_resource",
                "input": {"uri": "revit://guides/qa"},
            }
        )
        events[1]["message"]["content"].append(
            {
                "type": "tool_result",
                "tool_use_id": "resource",
                "is_error": True,
                "content": "Method not found",
            }
        )
    fake_cli(monkeypatch, events, record_proxy=True)
    scenario = {"actions": [], "sequences": [[{"tool": "revit_query_elements", "args": {}}]]}
    run = asyncio.run(
        eval_run.CliProvider(family).run(
            "Count",
            "fake-server",
            TOOLS,
            scenario,
            "Snowdon",
            40,
        )
    )
    assert run["failure"] is None
    assert run["notes"] == [{"kind": "TraceMismatch", "native_calls": 2, "proxy_calls": 1}]
    judge = FakeProvider([turn(text='{"pass":true,"reason":"Resource error supported"}')])
    asyncio.run(eval_run.judge_answer(judge, SCENARIOS[0], [], run))
    evidence = json.loads(judge.histories[0][0]["content"].split("\n", 1)[1])
    assert evidence["native_evidence"] == events
    assert "Method not found" in json.dumps(evidence["native_evidence"])
    assert len(evidence["trace"]) == 1


def test_cli_trace_mismatch_still_fails_when_required_tool_is_missing(monkeypatch):
    events = cli_events("codex-cli")
    events.insert(
        2,
        {
            "type": "item.started",
            "item": {
                "id": "missing",
                "type": "mcp_tool_call",
                "server": "revit",
                "tool": "revit_list_sheets",
                "arguments": {},
            },
        },
    )
    fake_cli(monkeypatch, events, record_proxy=True)
    run = asyncio.run(
        eval_run.CliProvider("codex-cli").run(
            "Count",
            "fake-server",
            TOOLS,
            {"actions": [], "sequences": [[{"tool": "revit_list_sheets", "args": {}}]]},
            "Snowdon",
            40,
        )
    )
    assert run["failure"] == "TraceMismatch"
    assert run["notes"][0]["kind"] == "TraceMismatch"


@pytest.mark.parametrize("family", ["claude-cli", "codex-cli"])
def test_cli_large_prompt_uses_stdin(monkeypatch, tmp_path, family):
    prompt = "large prompt " * 100_000
    captured = {}
    original = asyncio.create_subprocess_exec

    async def spawn(*command, **kwargs):
        captured["command"] = command
        captured["prompt"] = kwargs["stdin"].read().decode("utf-8")
        return await original("/usr/bin/true", **kwargs)

    monkeypatch.setattr(asyncio, "create_subprocess_exec", spawn)
    asyncio.run(eval_run.CliProvider(family).invoke(prompt, tmp_path, None))
    assert captured["prompt"] == prompt
    assert prompt not in captured["command"]


def test_daily_qa_accepts_all_milestone_orders_but_requires_every_check():
    scenario = next(s for s in SCENARIOS if s["id"] == "daily_qa")
    calls = [{**step, "error": False} for step in scenario["sequences"][0]]
    assert eval_run.score(calls[::-1], scenario)["sequence_match"]
    assert not eval_run.score(calls[:-1], scenario)["sequence_match"]
    calls[-2]["args"] = {"categories": ["Walls"], "parameters": ["Mark"]}
    assert not eval_run.score(calls, scenario)["sequence_match"]
