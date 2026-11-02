from __future__ import annotations

import asyncio
import importlib.util
import json
import sys
from contextlib import asynccontextmanager
from pathlib import Path

import pytest
from mcp import types

BENCH_PATH = Path(__file__).resolve().parents[2] / "build" / "bench" / "bench.py"
SPEC = importlib.util.spec_from_file_location("bench", BENCH_PATH)
bench = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = bench
SPEC.loader.exec_module(bench)

SCHEMAS = json.loads(
    (Path(__file__).parent / "golden" / "tools.schema.json").read_text(encoding="utf-8")
)
TOOL_PROPERTIES = {name: set(schema["properties"]) for name, schema in SCHEMAS.items()}


class FakeClientSession:
    async def initialize(self):
        return types.InitializeResult(
            protocol_version="2025-11-25",
            capabilities=types.ServerCapabilities(),
            server_info=types.Implementation(name="fake-server", version="1.2.3"),
        )

    async def list_tools(self):
        return types.ListToolsResult(
            tools=[types.Tool(name=name, input_schema=schema) for name, schema in SCHEMAS.items()]
        )

    async def call_tool(self, name, args, *, read_timeout_seconds):
        # MCP 2.3.0 takes seconds as a float; the 1.x SDK required a timedelta.
        assert isinstance(read_timeout_seconds, float)
        assert read_timeout_seconds == 900.0
        payload, _ = await FakeSession().call_tool(name, args)
        return types.CallToolResult(content=[], structured_content=payload, is_error=False)


def test_main_uses_sdk_types_and_timeout(monkeypatch):
    import mcp
    import mcp.client.stdio

    @asynccontextmanager
    async def fake_stdio_client(parameters):
        yield None, None

    @asynccontextmanager
    async def fake_client_session(read, write):
        yield FakeClientSession()

    monkeypatch.setattr(mcp.client.stdio, "stdio_client", fake_stdio_client)
    monkeypatch.setattr(mcp, "ClientSession", fake_client_session)
    args = bench.parse_args(["--scenarios", "ping,cold_start", "--runs", "1"])
    result = asyncio.run(bench.main_async(args))
    assert result["serverVersion"] == "1.2.3"
    ping, cold_start = result["scenarios"]
    assert ping["runs"] == 1 and ping["error"] is None
    assert cold_start["runs"] == bench.COLD_START_RUNS and cold_start["error"] is None


def test_default_server_cwd_is_independent_of_working_directory(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    assert Path(bench.parse_args([]).cwd) == BENCH_PATH.parents[2] / "server"
    assert bench.parse_args(["--cwd", "."]).cwd == "."


class FakeSession:
    def __init__(self, failing_tool=None):
        self.calls = []
        self.failing_tool = failing_tool

    async def call_tool(self, name, args):
        self.calls.append((name, dict(args)))
        schema = SCHEMAS[name]
        assert set(args) <= set(schema["properties"])
        assert set(schema.get("required", [])) <= set(args)
        if name == self.failing_tool:
            raise bench.BenchError("fake workstation unavailable")

        data = {
            "revit_ping": "pong",
            "revit_aggregate_elements": {"matchedElements": 0, "groups": []},
            "revit_element_details": {
                "element": {"id": 101},
                "parameters": [{"name": "Comments", "storageType": "String", "hasValue": False}],
            },
            "revit_list_warnings": {"totalWarnings": 0, "groups": []},
            "revit_model_health": {"revitVersion": "2026", "counts": {}},
            "revit_parameter_fill_check": {"scope": {"categories": ["Walls"]}, "parameters": []},
            "revit_family_audit": {"mode": "summary", "families": []},
            "revit_model_snapshot": {"schemaVersion": 1, "skippedCount": 0},
            "revit_list_views": {"views": [{"id": 201, "name": "Level 1", "type": "FloorPlan"}]},
            "revit_export_view": {"fileName": "view.png", "sizeBytes": 128},
            "revit_run_actions": {"summary": "Updated Comments", "steps": []},
            "revit_undo_last": {"summary": "Undid Updated Comments"},
        }
        if name == "revit_list_catalog":
            items = {
                "categories": [
                    {"id": -2000945, "name": "Analytical Surfaces"},
                    {"id": -2000011, "name": "Walls"},
                ],
                "parameters": [{"name": "Comments"}],
                "family-types": [{"name": "Single", "family": "Door"}],
            }[args["section"]]
            payload_data = {"section": args["section"], "items": items, "total": len(items)}
        elif name == "revit_query_elements":
            payload_data = {
                "fields": ["category", "family", "type", "name", "level"],
                "elements": [{"id": 101, "values": {}}],
                "total": 1,
            }
        else:
            payload_data = data[name]
        payload = {
            "command": name.removeprefix("revit_").replace("_", "-"),
            "success": True,
            "partial": False,
            "data": payload_data,
            "elapsedMs": 1,
            "responder": {"documentName": "Test model", "revitVersion": "2026"},
        }
        return payload, len(json.dumps(payload).encode("utf-8"))


@pytest.mark.parametrize("scenario,size", [("query_100", 100), ("query_1000", 1000)])
@pytest.mark.parametrize("field_count", [3, 12])
def test_query_uses_category_fields_with_at_most_ten(scenario, size, field_count):
    fields = [f"Wall parameter {index}" for index in range(field_count)]

    class CategorySession(FakeSession):
        async def call_tool(self, name, args):
            payload, _ = await super().call_tool(name, args)
            if name == "revit_list_catalog" and args["section"] == "categories":
                payload["data"]["items"] = [{"id": -2000011, "name": "Walls"}]
            if name == "revit_list_catalog" and args["section"] == "parameters":
                payload["data"]["items"] = [{"name": "Above Counter Visibility"}]
            if name == "revit_query_elements":
                assert args["categories"] == ["Walls"]
                if "fields" in args:
                    assert args["fields"] == fields[:10]
                    assert args["limit"] == size
                else:
                    assert args["limit"] == 1
                payload["data"]["fields"] = fields
            return payload, len(json.dumps(payload).encode("utf-8"))

    session = CategorySession()
    result = asyncio.run(bench.run_scenario(bench.Context(session, 2), scenario))
    assert result.error is None and result.runs == 2
    queries = [args for name, args in session.calls if name == "revit_query_elements"]
    assert queries == [
        {"categories": ["Walls"], "limit": 1},
        *[{"categories": ["Walls"], "fields": fields[:10], "limit": size}] * 2,
    ]


def test_export_view_uses_string_id_for_duplicate_view_names():
    class DuplicateViewSession(FakeSession):
        async def call_tool(self, name, args):
            payload, size = await super().call_tool(name, args)
            if name == "revit_list_views":
                payload["data"]["views"] = [
                    {"id": view_id, "name": "L1", "type": view_type}
                    for view_id, view_type in [
                        (853402, "AreaPlan"),
                        (853585, "AreaPlan"),
                        (1350581, "FloorPlan"),
                        (1566634, "CeilingPlan"),
                    ]
                ]
            return payload, size

    session = DuplicateViewSession()
    result = asyncio.run(bench.run_scenario(bench.Context(session, 2), "export_view"))
    assert result.error is None and result.runs == 2
    exports = [args for name, args in session.calls if name == "revit_export_view"]
    assert len(exports) == 2
    assert SCHEMAS["revit_export_view"]["properties"]["view"]["type"] == "string"
    assert all(args["view"] == "853402" for args in exports)


def test_markdown_revit_year_comes_from_ping_responder():
    class Revit2027Session(FakeSession):
        async def call_tool(self, name, args):
            payload, size = await super().call_tool(name, args)
            if name == "revit_ping":
                payload["responder"]["revitVersion"] = "2027"
            return payload, size

    result = asyncio.run(bench.run_bench(bench.Context(Revit2027Session(), 1), [], None, None))
    assert result["revitYear"] == "2027"
    assert "- Revit year: 2027\n" in bench.render_markdown(result)


def test_scenarios_subset_runs_in_requested_order():
    args = bench.parse_args(["--scenarios", " catalog_limited, ping ", "--runs", "2"])
    assert args.names == ["catalog_limited", "ping"]
    session = FakeSession()
    result = asyncio.run(
        bench.run_bench(bench.Context(session, args.runs), args.names, "subset", "1.0.0")
    )
    assert [item["name"] for item in result["scenarios"]] == args.names
    assert all(item["error"] is None and item["runs"] == 2 for item in result["scenarios"])
    assert session.calls == [
        ("revit_ping", {}),
        ("revit_list_catalog", {"section": "categories", "limit": 50}),
        ("revit_list_catalog", {"section": "categories", "limit": 50}),
        ("revit_ping", {}),
        ("revit_ping", {}),
    ]


def test_unknown_scenario_is_an_argument_error(capsys):
    with pytest.raises(SystemExit) as error:
        bench.parse_args(["--scenarios", "ping,missing"])
    assert error.value.code == 2
    assert "Unknown scenarios: missing. Known: ping" in capsys.readouterr().err


def test_statistics_on_known_measurements():
    result = bench.summarize("ping", bench.Measurement(runs_ms=[10, 20, 30, 40, 50]), None)
    assert result.runs == 5
    assert result.median_ms == 30
    assert result.p95_ms == 50
    assert result.min_ms == 10
    assert result.max_ms == 50


def test_markdown_header_and_scenario_rows():
    scenarios = [
        bench.summarize(name, bench.Measurement(runs_ms=[10, 20, 30, 40, 50], bytes=128), None)
        for name in ["ping", "catalog"]
    ]
    result = {
        "label": "baseline",
        "timestamp": "2026-10-20T12:00:00Z",
        "serverVersion": "1.0.0",
        "addinVersion": "1.2.3",
        "revitYear": "2026",
        "scenarios": [item.__dict__ for item in scenarios],
    }
    markdown = bench.render_markdown(result)
    assert markdown.startswith("# Bench result\n")
    for field, value in [
        ("Label", "baseline"),
        ("Timestamp", "2026-10-20T12:00:00Z"),
        ("Server version", "1.0.0"),
        ("Add-in version", "1.2.3"),
        ("Revit year", "2026"),
    ]:
        assert f"- {field}: {value}\n" in markdown
    assert "| Scenario | runs | median ms | p95 ms | bytes | notes |\n" in markdown
    rows = [line for line in markdown.splitlines() if line.startswith("| ")][2:]
    assert rows == [f"| {name} | 5 | 30.0 | 50.0 | 128 |  |" for name in ["ping", "catalog"]]


def test_failed_scenario_records_error_and_continues():
    session = FakeSession(failing_tool="revit_list_catalog")
    result = asyncio.run(
        bench.run_bench(bench.Context(session, 2), ["ping", "catalog", "warnings"], None, None)
    )
    before, failed, after = result["scenarios"]
    assert before["name"] == "ping" and before["runs"] == 2 and before["error"] is None
    assert failed["name"] == "catalog"
    assert failed["error"] == "BenchError: fake workstation unavailable"
    assert failed["runs"] == 0
    assert failed["median_ms"] is None and failed["p95_ms"] is None
    assert after["name"] == "warnings" and after["runs"] == 2 and after["error"] is None
    assert "FAILED: BenchError: fake workstation unavailable" in bench.render_markdown(result)


def test_document_is_passed_to_every_tool_that_accepts_it():
    names = [name for name in bench.SCENARIOS if name != "cold_start"]
    args = bench.parse_args(["--document", "Test model", "--scenarios", ",".join(names)])
    session = FakeSession()
    ctx = bench.Context(session, 1, args.document, TOOL_PROPERTIES)
    result = asyncio.run(bench.run_bench(ctx, args.names, None, None))
    assert result["document"] == "Test model"
    assert [item["name"] for item in result["scenarios"]] == names
    assert all(item["error"] is None and item["runs"] == 1 for item in result["scenarios"])
    assert {name for name, _ in session.calls} == {
        "revit_ping",
        "revit_list_catalog",
        "revit_aggregate_elements",
        "revit_query_elements",
        "revit_element_details",
        "revit_list_warnings",
        "revit_model_health",
        "revit_parameter_fill_check",
        "revit_family_audit",
        "revit_model_snapshot",
        "revit_list_views",
        "revit_export_view",
        "revit_run_actions",
        "revit_undo_last",
    }
    for name, arguments in session.calls:
        if "document" in TOOL_PROPERTIES[name]:
            assert arguments["document"] == args.document
        else:
            assert "document" not in arguments


def test_actions_50_queries_walls_and_sets_verified_parameter():
    session = FakeSession()
    result = asyncio.run(bench.run_scenario(bench.Context(session, 2), "actions_50"))
    assert result.error is None and result.runs == 2
    queries = [args for name, args in session.calls if name == "revit_query_elements"]
    assert queries == [{"categories": ["Walls"], "limit": 100}]
    details = [args for name, args in session.calls if name == "revit_element_details"]
    assert details == [{"element_id": 101}]
    actions = [args for name, args in session.calls if name == "revit_run_actions"]
    assert actions[0]["dry_run"] is True
    assert len(actions) == 3
    for index, args in enumerate(actions[1:]):
        assert args == {
            "steps": [
                {
                    "action": "set_parameter",
                    "args": {"element_id": 101, "parameter": "Comments", "value": f"bench {index}"},
                }
            ]
        }
    assert sum(name == "revit_undo_last" for name, _ in session.calls) == 2


def test_actions_50_rejects_failed_payload_without_mcp_error():
    class FailedBatchClient(FakeClientSession):
        async def call_tool(self, name, args, *, read_timeout_seconds):
            result = await super().call_tool(name, args, read_timeout_seconds=read_timeout_seconds)
            if name == "revit_undo_last":
                pytest.fail("a failed batch must not be undone")
            if name == "revit_run_actions" and not args.get("dry_run"):
                result.structured_content["success"] = False
                result.structured_content["data"] = {
                    "summary": "Batch failed",
                    "steps": [{"success": False, "error": "Comments is read-only"}],
                }
                assert result.is_error is False
            return result

    session = bench.McpSession(FailedBatchClient())
    result = asyncio.run(bench.run_scenario(bench.Context(session, 2), "actions_50"))
    assert result.runs == 0
    assert result.median_ms is None and result.p95_ms is None
    assert "Batch failed" in result.error
    assert "Comments is read-only" in result.error


def test_actions_50_requires_walls_category():
    class NoWallsSession(FakeSession):
        async def call_tool(self, name, args):
            payload, size = await super().call_tool(name, args)
            if name == "revit_list_catalog":
                payload["data"]["items"] = payload["data"]["items"][:1]
            return payload, size

    with pytest.raises(bench.BenchError, match="the catalog has no Walls category"):
        asyncio.run(bench.actions_50(bench.Context(NoWallsSession(), 1)))


@pytest.mark.parametrize("usable", [True, False])
def test_actions_50_tries_writable_text_fallback(usable):
    class ParameterSession(FakeSession):
        async def call_tool(self, name, args):
            payload, size = await super().call_tool(name, args)
            if name == "revit_element_details":
                payload["data"]["parameters"] += [
                    {"name": "Mark", "storageType": "String", "hasValue": False},
                    {"name": "Height", "storageType": "Double", "hasValue": True},
                ]
            if name == "revit_run_actions":
                parameter = args["steps"][0]["args"]["parameter"]
                if parameter == "Comments" or not usable:
                    payload["success"] = False
                    payload["data"] = {"summary": "Parameter is read-only"}
            return payload, size

    session = ParameterSession()
    result = asyncio.run(bench.run_scenario(bench.Context(session, 1), "actions_50"))
    actions = [args for name, args in session.calls if name == "revit_run_actions"]
    assert [args["steps"][0]["args"]["parameter"] for args in actions[:2]] == ["Comments", "Mark"]
    if usable:
        assert result.error is None and result.runs == 1
        assert actions[2]["steps"][0]["args"]["parameter"] == "Mark"
    else:
        assert "no writable text parameter" in result.error
        assert result.runs == 0
        assert not any(name == "revit_undo_last" for name, _ in session.calls)
