import json
import math
import os
from unittest.mock import AsyncMock, patch

import pytest
from mcp import Client, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.server import MCPServer

from revit_model_mcp.actions import _send_action, millimeters_to_feet, register_actions
from revit_model_mcp.revit_channel import (
    JobPickupStatus,
    ReadJob,
    RevitReadChannel,
    parse_response,
)

ACTION_TOOLS = {
    "revit_select",
    "revit_show",
    "revit_isolate",
    "revit_move",
    "revit_place_family",
    "revit_create_wall",
    "revit_set_parameter",
    "revit_delete",
    "revit_batch",
    "revit_align_link_datums",
}


@pytest.mark.parametrize("flag", [None, "0", "true", "1"])
def test_stdio_action_gate(flag):
    import asyncio

    async def check():
        env = os.environ.copy()
        env.pop("REVIT_MCP_ALLOW_WRITE", None)
        if flag is not None:
            env["REVIT_MCP_ALLOW_WRITE"] = flag
        parameters = StdioServerParameters(command="revit-model-mcp", args=[], env=env)
        async with Client(stdio_client(parameters), read_timeout_seconds=10) as client:
            result = await client.list_tools()
        tools = {tool.name: tool for tool in result.tools}
        assert ACTION_TOOLS.intersection(tools) == (
            ACTION_TOOLS if flag in {"1", "true"} else set()
        )
        for name in ACTION_TOOLS.intersection(tools):
            tool = tools[name]
            if name != "revit_align_link_datums":
                assert "response_timeout_s" not in tool.input_schema["properties"]
            assert tool.annotations.read_only_hint is False
            assert tool.title and len(tool.title) <= 40
            assert tool.annotations.title == tool.title
            assert tool.annotations.destructive_hint is (
                name not in {"revit_select", "revit_show", "revit_isolate"}
            )
        return tools

    asyncio.run(check())


def action_server():
    server = MCPServer("actions-test")
    execute = AsyncMock(return_value={"success": True, "data": {}, "activeView": "Level 1"})
    host = AsyncMock()
    host.list_revit_instances.return_value = [{"processId": 42}]
    with patch.dict(os.environ, {"REVIT_MCP_ALLOW_WRITE": "1"}):
        register_actions(server, execute, lambda: host)
    return server, execute, host


def test_align_link_datums_payload_and_timeout():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_align_link_datums",
            {
                "link": "AR.rvt : 1",
                "kinds": ["grids"],
                "name_map": {"A": "A1"},
                "level_offset_mm": 150,
                "dry_run": True,
                "response_timeout_s": 600,
            },
        )
    )
    job = execute.await_args.args[0]
    assert job.command == "align-link-datums"
    assert job.payload["nameMap"] == {"A": "A1"}
    assert job.payload["dryRun"] is True
    assert execute.await_args.args[1] == 600


def test_align_link_datums_rejects_invalid_timeout():
    import asyncio

    server, execute, _ = action_server()
    with pytest.raises(Exception):
        asyncio.run(
            server.call_tool("revit_align_link_datums", {"link": "AR.rvt", "response_timeout_s": 0})
        )
    execute.assert_not_awaited()


def test_compare_link_datums_is_read_only():
    import asyncio

    from revit_model_mcp import server as revit_server

    channel = AsyncMock(return_value={"success": True})
    with patch.object(revit_server, "channel") as mock_channel:
        mock_channel.execute = channel
        asyncio.run(revit_server.revit_compare_link_datums("AR.rvt", name_map={"A": "A1"}))
    job = channel.await_args.args[0]
    assert job.command == "compare-link-datums"
    assert job.payload["nameMap"] == {"A": "A1"}


@pytest.mark.parametrize("response_timeout_s", [None, 900])
def test_action_response_timeout_reaches_channel(response_timeout_s):
    import asyncio

    _, execute, host = action_server()
    timeout = {} if response_timeout_s is None else {"response_timeout_s": response_timeout_s}
    asyncio.run(_send_action(execute, lambda: host, "select", elementIds=[1], **timeout))
    execute.assert_awaited_once()
    assert execute.await_args.args[1:] == (response_timeout_s or 120, 300, None)


@pytest.mark.parametrize(
    "name,arguments,payload",
    [
        ("revit_select", {"element_ids": []}, {"elementIds": []}),
        ("revit_show", {"element_ids": [1]}, {"elementIds": [1], "select": True}),
        ("revit_isolate", {"element_ids": [], "reset": True}, {"elementIds": [], "reset": True}),
        (
            "revit_move",
            {"element_ids": [1], "dx_mm": 304.8, "dy_mm": -50},
            {"elementIds": [1], "dxMm": 304.8, "dyMm": -50.0, "dzMm": 0},
        ),
        (
            "revit_place_family",
            {"family": "Desk", "type_name": None, "x_mm": 100, "y_mm": 200, "level": "Level 1"},
            {
                "family": "Desk",
                "typeName": None,
                "xMm": 100.0,
                "yMm": 200.0,
                "level": "Level 1",
                "rotationDeg": 0,
            },
        ),
        (
            "revit_create_wall",
            {"start_mm": [0, 0], "end_mm": [2000, 0], "level": "Level 1", "wall_type": None},
            {
                "startMm": [0.0, 0.0],
                "endMm": [2000.0, 0.0],
                "level": "Level 1",
                "wallType": None,
                "heightMm": 3000,
            },
        ),
        (
            "revit_set_parameter",
            {"element_id": 1, "parameter": "Comments", "value": ""},
            {"elementId": 1, "parameter": "Comments", "value": ""},
        ),
        ("revit_delete", {"element_ids": [1, 2]}, {"elementIds": [1, 2]}),
    ],
)
@pytest.mark.parametrize("dry_run", [False, True])
@pytest.mark.parametrize(
    "document_arguments",
    [{}, {"document": None}, {"document": "Tower"}, {"targetDocument": "Tower"}],
)
def test_action_arguments_reach_channel_in_millimeters(
    name, arguments, payload, dry_run, document_arguments
):
    import asyncio

    server, execute, _ = action_server()
    if name not in {"revit_select", "revit_show", "revit_isolate"}:
        if dry_run:
            arguments = {**arguments, "dry_run": True}
        payload = {**payload, "dryRun": dry_run}
    asyncio.run(server.call_tool(name, {**arguments, **document_arguments}))
    execute.assert_awaited_once()
    job = execute.await_args.args[0]
    command = name.removeprefix("revit_").replace("_", "-")
    assert job.command == command
    if "Tower" in document_arguments.values():
        payload = {**payload, "targetDocument": "Tower"}
    else:
        assert "targetDocument" not in job.payload
    assert job.payload == {"command": command, **payload, "targetProcessId": 42}


@pytest.mark.parametrize(
    "name,arguments",
    [
        ("revit_select", {"element_ids": [0]}),
        ("revit_select", {"element_ids": [True]}),
        ("revit_select", {"element_ids": [1.5]}),
        ("revit_select", {"element_ids": [2**63]}),
        ("revit_show", {"element_ids": []}),
        ("revit_delete", {"element_ids": []}),
        ("revit_isolate", {"element_ids": []}),
        ("revit_move", {"element_ids": [1], "dx_mm": math.inf, "dy_mm": 0}),
        ("revit_move", {"element_ids": [1], "dx_mm": 0}),
        (
            "revit_create_wall",
            {"start_mm": [0], "end_mm": [1, 2], "level": "Level 1", "wall_type": None},
        ),
        (
            "revit_create_wall",
            {"start_mm": [0, 0], "end_mm": [0, 0], "level": "Level 1", "wall_type": None},
        ),
        (
            "revit_create_wall",
            {
                "start_mm": [0, 0],
                "end_mm": [1, 2],
                "level": "Level 1",
                "wall_type": None,
                "height_mm": -1,
            },
        ),
        (
            "revit_place_family",
            {"family": " ", "type_name": None, "x_mm": 0, "y_mm": 0, "level": "Level 1"},
        ),
        ("revit_set_parameter", {"element_id": 1, "parameter": " ", "value": "x"}),
    ],
)
def test_invalid_arguments_never_reach_channel(name, arguments):
    import asyncio

    server, execute, host = action_server()
    with pytest.raises(Exception):
        asyncio.run(server.call_tool(name, arguments))
    execute.assert_not_awaited()
    host.list_revit_instances.assert_not_awaited()


@pytest.mark.parametrize("instances", [[], [{"processId": 1}, {"processId": 2}]])
def test_actions_require_one_revit_instance(instances):
    import asyncio

    server, execute, host = action_server()
    host.list_revit_instances.return_value = instances
    with pytest.raises(Exception, match="exactly one"):
        asyncio.run(server.call_tool("revit_select", {"element_ids": [1]}))
    execute.assert_not_awaited()


@pytest.mark.parametrize("value,expected", [(0, 0), (304.8, 1), (-609.6, -2), (1, 1 / 304.8)])
def test_mm_conversion(value, expected):
    assert millimeters_to_feet(value) == pytest.approx(expected)


@pytest.mark.parametrize("value", [math.nan, math.inf, -math.inf])
def test_mm_conversion_rejects_non_finite(value):
    with pytest.raises(ValueError):
        millimeters_to_feet(value)


@pytest.mark.parametrize(
    "payload",
    [
        {"command": "move", "elementIds": [1], "dxMm": 10, "dyMm": 0},
        {"command": "delete", "elementIds": [1]},
        {"command": "select", "elementIds": [1]},
        {"command": "isolate", "elementIds": [], "reset": True},
        {"command": "batch", "steps": [{"command": "delete", "elementIds": [1]}]},
    ],
)
@pytest.mark.parametrize(
    "document,error",
    [
        ("Model A", None),
        ("Missing", "The addressed document 'Missing' is not open."),
        (
            "Model",
            "The document reference 'Model' is ambiguous (2 open documents match); "
            "use a more specific substring.",
        ),
    ],
)
def test_addressed_action_channel_preserves_target_and_response(payload, document, error):
    import asyncio

    command = payload["command"]
    job = ReadJob(command, {**payload, "targetProcessId": 42}).for_document(document)
    response = {"command": command, "success": error is None, "activeView": "Model B Plan"}
    if error is None:
        response["data"] = {}
    else:
        response["error"] = error
    host = AsyncMock()
    host.requires_identity = False
    host.select_job.return_value = (host, job)
    host.prepare_job.return_value = set()
    host.wait_until_trigger_is_gone.return_value = JobPickupStatus(True, 0, False, 0)
    host.wait_for_new_response.return_value = "response_action.json"
    host.finish_job.return_value = (json.dumps(response), None)

    result = asyncio.run(RevitReadChannel(host).execute(job))

    sent = json.loads(host.prepare_job.await_args.args[1])
    correlation_id = sent.pop("correlationId")
    assert len(correlation_id) == 32
    assert host.wait_for_new_response.await_args.args[3] == correlation_id
    assert sent == {
        **payload,
        "targetProcessId": 42,
        "targetDocument": document,
    }
    assert result == response


def test_action_failure_preserves_gate_message_view_and_suggestions():
    response = {
        "command": "place-family",
        "success": False,
        "error": "Family not loaded",
        "activeView": "Level 1",
        "data": {"closestFamilies": ["Office Desk (Furniture)"]},
    }
    assert parse_response(json.dumps(response), "place-family") == response
    response = {
        "command": "move",
        "success": False,
        "error": "actions disabled on the workstation",
        "activeView": "Level 1",
    }
    assert parse_response(json.dumps(response), "move") == response


@pytest.mark.parametrize("view_opened", [True, False])
@pytest.mark.parametrize("success", [True, False])
def test_show_response_preserves_view_opened_and_dialogs(view_opened, success):
    response = {
        "command": "show",
        "success": success,
        "activeView": "Level 5 Plan",
        "viewOpened": view_opened,
        "dialogsSuppressed": ["Continue?"],
        "data": {"count": 1},
    }
    if not success:
        response["error"] = "Show failed after opening view"
    assert parse_response(json.dumps(response), "show") == response


@pytest.mark.parametrize(
    "steps",
    [
        [],
        [{"action": "select", "args": {"element_ids": []}}] * 51,
        [{"action": "unknown", "args": {}}],
        [{"action": "show", "args": {"element_ids": [1]}}],
        [{"action": "batch", "args": {"steps": []}}],
        [{"action": "move", "args": {"element_ids": [1], "dx_mm": 1, "dy_mm": 0, "typo": 2}}],
        [{"action": "move", "args": {"element_ids": [True], "dx_mm": 1, "dy_mm": 0}}],
        [{"action": "move", "args": {"element_ids": [1], "dx_mm": math.inf, "dy_mm": 0}}],
        [{"action": "move", "args": {"element_ids": [1], "dx_mm": 1}}],
        [{"action": "isolate", "args": {"element_ids": []}}],
        [
            {
                "action": "create_wall",
                "args": {"start_mm": [0, 0], "end_mm": [0, 0], "level": "01", "wall_type": None},
            }
        ],
    ],
)
def test_batch_invalid_steps_never_reach_channel(steps):
    import asyncio

    server, execute, host = action_server()
    with pytest.raises(Exception):
        asyncio.run(server.call_tool("revit_batch", {"steps": steps}))
    execute.assert_not_awaited()
    host.list_revit_instances.assert_not_awaited()


@pytest.mark.parametrize("dry_run", [False, True])
@pytest.mark.parametrize(
    "document_arguments",
    [{}, {"document": None}, {"document": "Tower"}, {"targetDocument": "Tower"}],
)
def test_batch_payload_and_annotations(dry_run, document_arguments):
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_batch",
            {
                "steps": [
                    {"action": "move", "args": {"element_ids": [1], "dx_mm": 10, "dy_mm": 0}},
                    {
                        "action": "set_parameter",
                        "args": {"element_id": 1, "parameter": "Comments", "value": "Reviewed"},
                    },
                ],
                "dry_run": dry_run,
                **document_arguments,
            },
        )
    )
    payload = execute.await_args.args[0].payload
    if "Tower" in document_arguments.values():
        document_payload = {"targetDocument": "Tower"}
    else:
        document_payload = {}
        assert "targetDocument" not in payload
    assert payload == {
        "command": "batch",
        "targetProcessId": 42,
        **document_payload,
        "dryRun": dry_run,
        "steps": [
            {
                "command": "move",
                "elementIds": [1],
                "dxMm": 10,
                "dyMm": 0,
                "dzMm": 0,
                "dryRun": False,
            },
            {
                "command": "set-parameter",
                "elementId": 1,
                "parameter": "Comments",
                "value": "Reviewed",
                "dryRun": False,
            },
        ],
    }
    tools = asyncio.run(server.list_tools())
    tool = next(tool for tool in tools if tool.name == "revit_batch")
    assert tool.annotations.read_only_hint is False
    assert tool.annotations.destructive_hint is True
    assert tool.annotations.idempotent_hint is False


def test_in_process_action_titles_with_true(monkeypatch):
    import asyncio

    monkeypatch.setenv("REVIT_MCP_ALLOW_WRITE", "true")
    server = MCPServer("action-titles")
    register_actions(server, AsyncMock(), lambda: AsyncMock())
    tools = asyncio.run(server.list_tools())
    assert {tool.name for tool in tools} == ACTION_TOOLS
    for tool in tools:
        assert tool.title and len(tool.title) <= 40
        assert tool.annotations.title == tool.title
        assert tool.annotations.read_only_hint is False
        assert tool.annotations.destructive_hint is (
            tool.name not in {"revit_select", "revit_show", "revit_isolate"}
        )
