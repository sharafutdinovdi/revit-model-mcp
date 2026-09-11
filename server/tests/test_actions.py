import json
import math
import os
from unittest.mock import AsyncMock, patch

import pytest
from mcp import Client, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.server import MCPServer

from revit_model_mcp.actions import millimeters_to_feet, register_actions
from revit_model_mcp.revit_channel import parse_response

ACTION_TOOLS = {
    "revit_select", "revit_show", "revit_isolate", "revit_move",
    "revit_place_family", "revit_create_wall", "revit_set_parameter", "revit_delete",
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
        assert ACTION_TOOLS.intersection(tools) == (ACTION_TOOLS if flag == "1" else set())
        for name in ACTION_TOOLS.intersection(tools):
            assert not tools[name].annotations.read_only_hint
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


@pytest.mark.parametrize("name,arguments,payload", [
    ("revit_select", {"element_ids": []}, {"elementIds": []}),
    ("revit_show", {"element_ids": [1]}, {"elementIds": [1], "select": True}),
    ("revit_isolate", {"element_ids": [], "reset": True}, {"elementIds": [], "reset": True}),
    ("revit_move", {"element_ids": [1], "dx_mm": 304.8, "dy_mm": -50}, {"elementIds": [1], "dxMm": 304.8, "dyMm": -50.0, "dzMm": 0}),
    ("revit_place_family", {"family": "Desk", "type_name": None, "x_mm": 100, "y_mm": 200, "level": "Level 1"}, {"family": "Desk", "typeName": None, "xMm": 100.0, "yMm": 200.0, "level": "Level 1", "rotationDeg": 0}),
    ("revit_create_wall", {"start_mm": [0, 0], "end_mm": [2000, 0], "level": "Level 1", "wall_type": None}, {"startMm": [0.0, 0.0], "endMm": [2000.0, 0.0], "level": "Level 1", "wallType": None, "heightMm": 3000}),
    ("revit_set_parameter", {"element_id": 1, "parameter": "Comments", "value": ""}, {"elementId": 1, "parameter": "Comments", "value": ""}),
    ("revit_delete", {"element_ids": [1, 2]}, {"elementIds": [1, 2]}),
])
def test_action_arguments_reach_channel_in_millimeters(name, arguments, payload):
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(server.call_tool(name, arguments))
    execute.assert_awaited_once()
    job = execute.await_args.args[0]
    command = name.removeprefix("revit_").replace("_", "-")
    assert job.command == command
    assert job.payload == {"command": command, **payload, "targetProcessId": 42}


@pytest.mark.parametrize("name,arguments", [
    ("revit_select", {"element_ids": [0]}),
    ("revit_select", {"element_ids": [True]}),
    ("revit_select", {"element_ids": [1.5]}),
    ("revit_select", {"element_ids": [2**63]}),
    ("revit_show", {"element_ids": []}),
    ("revit_delete", {"element_ids": []}),
    ("revit_isolate", {"element_ids": []}),
    ("revit_move", {"element_ids": [1], "dx_mm": math.inf, "dy_mm": 0}),
    ("revit_move", {"element_ids": [1], "dx_mm": 0}),
    ("revit_create_wall", {"start_mm": [0], "end_mm": [1, 2], "level": "Level 1", "wall_type": None}),
    ("revit_create_wall", {"start_mm": [0, 0], "end_mm": [0, 0], "level": "Level 1", "wall_type": None}),
    ("revit_create_wall", {"start_mm": [0, 0], "end_mm": [1, 2], "level": "Level 1", "wall_type": None, "height_mm": -1}),
    ("revit_place_family", {"family": " ", "type_name": None, "x_mm": 0, "y_mm": 0, "level": "Level 1"}),
    ("revit_set_parameter", {"element_id": 1, "parameter": " ", "value": "x"}),
])
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


def test_action_failure_preserves_gate_message_view_and_suggestions():
    response = {"command": "place-family", "success": False, "error": "Family not loaded",
                "activeView": "Level 1", "data": {"closestFamilies": ["Office Desk (Furniture)"]}}
    assert parse_response(json.dumps(response), "place-family") == response
    response = {"command": "move", "success": False,
                "error": "actions disabled on the workstation", "activeView": "Level 1"}
    assert parse_response(json.dumps(response), "move") == response


@pytest.mark.parametrize("view_opened", [True, False])
@pytest.mark.parametrize("success", [True, False])
def test_show_response_preserves_view_opened_and_dialogs(view_opened, success):
    response = {"command": "show", "success": success, "activeView": "Level 5 Plan",
                "viewOpened": view_opened, "dialogsSuppressed": ["Continue?"], "data": {"count": 1}}
    if not success:
        response["error"] = "Show failed after opening view"
    assert parse_response(json.dumps(response), "show") == response
