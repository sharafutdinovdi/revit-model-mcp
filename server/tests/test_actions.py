import json
import math
import os
import re
from pathlib import Path
from unittest.mock import AsyncMock, patch

import pytest
from mcp import Client, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.server import MCPServer
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp.actions import (
    UpdateFilters,
    _send_action,
    millimeters_to_feet,
    query_filter_payload,
    redact_model_paths,
    register_actions,
)
from revit_model_mcp.revit_channel import (
    Job,
    JobPickupStatus,
    RevitChannel,
    parse_response,
)

ACTION_TOOLS = {
    "revit_cancel_job",
    "revit_process_models",
    "revit_execute_code",
    "revit_select",
    "revit_show",
    "revit_isolate",
    "revit_override_graphics",
    "revit_move",
    "revit_rotate",
    "revit_copy",
    "revit_mirror",
    "revit_change_type",
    "revit_update_parameters",
    "revit_place_family",
    "revit_load_family",
    "revit_place_families",
    "revit_create_wall",
    "revit_create_mep_run",
    "revit_link_cad",
    "revit_walls_from_cad",
    "revit_create_view",
    "revit_duplicate_view",
    "revit_apply_view_template",
    "revit_create_sheet",
    "revit_place_views_on_sheet",
    "revit_set_parameter",
    "revit_delete",
    "revit_run_actions",
    "revit_export_nwc",
    "revit_export",
    "revit_edit_families",
    "revit_align_link_datums",
    "revit_open_document",
    "revit_activate_document",
    "revit_activate_view",
    "revit_close_views",
    "revit_new_document",
    "revit_close_document",
    "revit_save_document",
    "revit_sync_document",
    "revit_set_view_visibility",
    "revit_remove_links",
    "revit_undo_last",
}


@pytest.mark.parametrize("read_only", [None, "0", "1", "true"])
def test_stdio_action_tools_listed_regardless_of_read_only(read_only):
    import asyncio

    async def check():
        env = os.environ.copy()
        env.pop("REVIT_MCP_READ_ONLY", None)
        if read_only is not None:
            env["REVIT_MCP_READ_ONLY"] = read_only
        parameters = StdioServerParameters(command="revit-model-mcp", args=[], env=env)
        async with Client(stdio_client(parameters), read_timeout_seconds=10) as client:
            result = await client.list_tools()
        tools = {tool.name: tool for tool in result.tools}
        assert ACTION_TOOLS.issubset(tools)
        for name in ACTION_TOOLS:
            tool = tools[name]
            assert ("timeout_seconds" in tool.input_schema["properties"]) is (
                name
                in {
                    "revit_export_nwc",
                    "revit_edit_families",
                    "revit_align_link_datums",
                    "revit_execute_code",
                    "revit_export",
                    "revit_process_models",
                    "revit_load_family",
                    "revit_place_families",
                    "revit_link_cad",
                    "revit_walls_from_cad",
                }
            )
            assert tool.annotations.read_only_hint is False
            assert tool.title and len(tool.title) <= 40
            assert tool.annotations.title == tool.title
            assert tool.annotations.destructive_hint is (
                name not in {"revit_select", "revit_show", "revit_isolate"}
            )
        return tools

    asyncio.run(check())


def test_stdio_read_only_blocks_execution_without_hiding_tools():
    import asyncio

    async def check():
        env = os.environ.copy()
        env["REVIT_MCP_READ_ONLY"] = "1"
        parameters = StdioServerParameters(command="revit-model-mcp", args=[], env=env)
        async with Client(stdio_client(parameters), read_timeout_seconds=10) as client:
            listed = await client.list_tools()
            assert "revit_select" in {tool.name for tool in listed.tools}
            result = await client.call_tool("revit_select", {"element_ids": []})
        assert "read-only mode" in str(result)

    asyncio.run(check())


@pytest.mark.parametrize(
    "name,arguments",
    [
        ("revit_activate_document", {"document": "Tower"}),
        ("revit_activate_view", {"view": "Level 1"}),
        ("revit_close_views", {}),
        ("revit_new_document", {}),
    ],
)
def test_session_actions_refused_in_read_only_mode(name, arguments):
    import asyncio

    server, execute, _ = action_server(read_only=True)
    result = asyncio.run(server.call_tool(name, arguments))
    assert "read-only mode" in str(result)
    execute.assert_not_awaited()


def test_session_action_mappings():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(server.call_tool("revit_activate_document", {"document": "Tower"}))
    assert execute.await_args.args[0].payload["command"] == "activate-document"
    asyncio.run(
        server.call_tool(
            "revit_activate_view",
            {"view": "3D", "document": "Tower", "activate_document": True, "view_type": "ThreeD"},
        )
    )
    assert execute.await_args.args[0].payload["activateDocument"] is True
    assert execute.await_args.args[0].payload["viewType"] == "ThreeD"
    assert execute.await_args.args[0].payload["zoom"] == "fit"
    asyncio.run(server.call_tool("revit_close_views", {"views": ["3D"], "keep_active": False}))
    assert execute.await_args.args[0].payload["keepActive"] is False
    asyncio.run(
        server.call_tool(
            "revit_new_document", {"template": r"C:\\T.rft", "kind": "family", "name": "Door"}
        )
    )
    assert execute.await_args.args[0].payload["kind"] == "family"
    assert execute.await_args.args[0].payload["name"] == "Door"
    asyncio.run(
        server.call_tool(
            "revit_open_document",
            {"path": r"C:\\M.rvt", "audit": True, "worksets": {"close": ["*Link*"]}},
        )
    )
    assert execute.await_args.args[0].payload["worksetsClose"] == ["*Link*"]
    assert execute.await_args.args[0].payload["audit"] is True


def action_server(read_only=False):
    server = MCPServer("actions-test")
    execute = AsyncMock(return_value={"success": True, "data": {}, "activeView": "Level 1"})
    host = AsyncMock()
    host.list_revit_instances.return_value = [{"processId": 42}]
    with patch.dict(os.environ, {"REVIT_MCP_READ_ONLY": "1" if read_only else "0"}):
        register_actions(server, execute, lambda: host)
    return server, execute, host


def test_execute_code_maps_arguments_and_respects_read_only():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(server.call_tool("revit_execute_code", {"code": "return 42;"}))
    job = execute.await_args.args[0]
    assert job.command == "execute-code"
    assert job.payload["code"] == "return 42;"
    assert job.payload["transaction"] == "auto"
    assert job.payload["dryRun"] is False
    assert execute.await_args.args[1] == 600

    with pytest.raises(Exception):
        asyncio.run(
            server.call_tool(
                "revit_execute_code",
                {
                    "code": "return 42;",
                    "transaction": "none",
                    "dry_run": True,
                },
            )
        )
    blocked, blocked_execute, _ = action_server(read_only=True)
    result = asyncio.run(blocked.call_tool("revit_execute_code", {"code": "return 42;"}))
    assert "read-only mode" in str(result)
    blocked_execute.assert_not_awaited()


def test_process_models_maps_steps_code_exports_and_save():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_process_models",
            {
                "paths": [r"C:\Models\A.rvt"],
                "open": {"mode": "detached", "worksets": {"close": ["*Link*"]}, "audit": True},
                "steps": [
                    {
                        "action": "set_parameter",
                        "args": {"element_id": 1, "parameter": "Mark", "value": "done"},
                    }
                ],
                "code": {"code": "return 1;"},
                "exports": [{"format": "ifc", "output_dir": r"C:\Out\{model}"}],
                "save": {"mode": "output_dir", "output_dir": r"C:\Saved"},
            },
        )
    )
    job = execute.await_args.args[0]
    assert job.command == "process-models"
    assert execute.await_args.args[1] == 14400
    process = job.payload["process"]
    assert process["open"]["worksetsClose"] == ["*Link*"]
    assert process["open"]["audit"] is True
    assert process["steps"][0]["command"] == "set-parameter"
    assert process["code"] == {"code": "return 1;", "transaction": "auto"}
    assert process["exports"][0]["format"] == "ifc"
    assert process["save"] == {
        "mode": "output_dir",
        "outputDir": r"C:\Saved",
        "compact": True,
        "overwrite": False,
    }


def test_process_models_confirmation_flow_and_validation():
    import asyncio

    server, execute, _ = action_server()
    execute.side_effect = [
        {"success": True, "data": {"needsConfirmation": True, "confirmToken": "token"}},
        {"success": True, "data": {"models": []}},
    ]
    arguments = {"paths": [r"C:\Models\A.rvt"], "save": {"mode": "in_place"}}
    preview = asyncio.run(server.call_tool("revit_process_models", arguments))
    assert "needsConfirmation" in str(preview)
    asyncio.run(server.call_tool("revit_process_models", {**arguments, "confirm_token": "token"}))
    assert execute.await_count == 2
    assert execute.await_args.args[0].payload["process"]["confirmToken"] == "token"

    for invalid in (
        {},
        {"paths": ["relative.rvt"]},
        {"paths": [r"C:\Models\A.rvt"], "folder": r"C:\Models"},
        {
            "paths": [r"C:\Models\A.rvt"],
            "code": {"code": "return 1;", "transaction": "none"},
            "dry_run": True,
        },
        {"paths": [r"C:\Models\A.rvt"], "save": {"mode": "output_dir", "output_dir": r"C:\Models"}},
        {
            "paths": [r"C:\Models\A.rvt"],
            "open": {"mode": "local_copy"},
            "save": {"mode": "in_place"},
        },
    ):
        with pytest.raises(Exception):
            asyncio.run(server.call_tool("revit_process_models", invalid))
    assert execute.await_count == 2

    blocked, blocked_execute, _ = action_server(read_only=True)
    response = asyncio.run(blocked.call_tool("revit_process_models", arguments))
    assert "read-only mode" in str(response)
    blocked_execute.assert_not_awaited()


def test_process_models_redacts_nested_paths():
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
        result = redact_model_paths(
            {
                "data": {
                    "models": [
                        {
                            "path": r"C:\Models\A.rvt",
                            "saved": r"C:\Out\A.rvt",
                            "dialogsDismissed": [r"Opened C:\Models\A.rvt"],
                            "code": {"log": [r"Read C:\Models\A.rvt"]},
                        }
                    ]
                }
            }
        )
    model = result["data"]["models"][0]
    assert model["path"] == "A.rvt"
    assert model["saved"] == "A.rvt"
    assert "C:\\Models" not in str(model)


def test_document_action_mapping_and_confirmation_shape():
    import asyncio

    server, execute, _ = action_server()
    execute.return_value = {
        "success": True,
        "data": {
            "needsConfirmation": True,
            "confirmationText": "Synchronize Tower with central.",
            "confirmToken": "random-token",
        },
    }
    first = asyncio.run(
        server.call_tool("revit_sync_document", {"document": "Tower", "comment": "grids"})
    )
    assert "confirmationText" in str(first)
    payload = execute.await_args.args[0].payload
    assert payload["command"] == "sync-document"
    assert payload["targetDocument"] == "Tower"
    assert payload["comment"] == "grids"
    assert payload["confirmToken"] is None
    asyncio.run(
        server.call_tool(
            "revit_sync_document",
            {"document": "Tower", "comment": "grids", "confirm_token": "random-token"},
        )
    )
    assert execute.await_args.args[0].payload["confirmToken"] == "random-token"

    asyncio.run(
        server.call_tool(
            "revit_open_document",
            {"path": "RSN://srv/AR/House.rvt", "worksets": {"open": ["A"]}},
        )
    )
    assert execute.await_args.args[0].payload["worksets"] == "open"
    assert execute.await_args.args[0].payload["worksetsOpen"] == ["A"]


def test_view_visibility_and_link_removal_argument_mapping():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_set_view_visibility",
            {
                "view": "NWC 3D",
                "hide_categories_by_type": ["annotation"],
                "worksets": {"hide_mask": ["HVAC*"], "show_mask": []},
                "template_mode": "duplicate_view",
                "dry_run": True,
            },
        )
    )
    payload = execute.await_args.args[0].payload
    assert payload["command"] == "set-view-visibility"
    assert payload["hideCategoriesByType"] == ["annotation"]
    assert payload["worksets"] == {"hideMask": ["HVAC*"], "showMask": []}
    assert payload["templateMode"] == "duplicate_view"
    assert payload["dryRun"] is True

    asyncio.run(
        server.call_tool(
            "revit_remove_links",
            {
                "links": "*",
                "kinds": ["revit", "image"],
                "include_imported_cad": True,
            },
        )
    )
    payload = execute.await_args.args[0].payload
    assert payload["command"] == "remove-links"
    assert payload["links"] == ["*"]
    assert payload["kinds"] == ["revit", "image"]
    assert payload["includeImportedCad"] is True


def test_document_action_response_paths_are_redacted_when_enabled():
    import asyncio

    server, execute, _ = action_server()
    execute.return_value = {
        "success": True,
        "data": {
            "title": "Tower",
            "path": r"C:\Models\Tower_local.rvt",
            "centralPath": r"RSN://srv/AR/Tower.rvt",
            "confirmationText": r"Save to C:\Models\Tower_local.rvt now?",
        },
        "summary": r"Saved C:\Models\Tower_local.rvt",
    }
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
        result = asyncio.run(
            server.call_tool("revit_close_document", {"document": "Tower", "confirm_token": "tok"})
        )
    data = result.structured_content["data"]
    assert data["path"] == "Tower_local.rvt"
    assert data["centralPath"] == "Tower.rvt"
    assert data["confirmationText"] == "Save to Tower_local.rvt now?"
    assert result.structured_content["summary"] == "Saved Tower_local.rvt"


@pytest.mark.parametrize(
    ("message", "expected"),
    [
        ('Save "C:\\Models\\Tower.rvt" now.', 'Save "Tower.rvt" now.'),
        ("Saved C:\\Models\\Tower.rvt and closed it.", "Saved Tower.rvt and closed it."),
        (r"Open \\host\share\Tower.rvt next.", "Open Tower.rvt next."),
        ("Open //host/share/Tower.rvt next.", "Open Tower.rvt next."),
        (r'Open "\\host\my share\Tower.rvt" next.', 'Open "Tower.rvt" next.'),
        (
            "See https://example.com/a/b.html for help.",
            "See https://example.com/a/b.html for help.",
        ),
        ("Open RSN://srv/AR/Tower.rvt next.", "Open RSN://srv/AR/Tower.rvt next."),
        ("Open C:/Models/Tower.rvt next.", "Open Tower.rvt next."),
        (
            'Save "C:\\My Models\\Tower North.rvt" now.',
            'Save "Tower North.rvt" now.',
        ),
        ("Failed at C:\\Models\\Tower.rvt\nRetry now.", "Failed at Tower.rvt\nRetry now."),
        (
            r"Copy C:\Models\Tower.rvt to \\host\share\Copy.rvt today.",
            "Copy Tower.rvt to Copy.rvt today.",
        ),
        ("The document is ready.", "The document is ready."),
    ],
)
def test_response_message_paths_are_redacted_when_enabled(message, expected):
    response = {"data": {"confirmationText": message}}
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
        assert redact_model_paths(response)["data"]["confirmationText"] == expected


def test_response_message_fields_are_redacted_at_any_depth():
    response = {
        "summary": r"Saved C:\Models\Tower.rvt.",
        "data": {
            "error": r"Failed at C:\Models\Tower.rvt",
            "message": r"Read C:/Models/Tower.rvt",
            "warning": r"Check \\host\share\Tower.rvt",
            "warnings": [r"Missing C:\Models\Tower.rvt"],
            "parameterValue": r"C:\Models\Tower.rvt",
        },
    }
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
        result = redact_model_paths(response)
    assert result == {
        "summary": "Saved Tower.rvt.",
        "data": {
            "error": "Failed at Tower.rvt",
            "message": "Read Tower.rvt",
            "warning": "Check Tower.rvt",
            "warnings": ["Missing Tower.rvt"],
            "parameterValue": r"C:\Models\Tower.rvt",
        },
    }


def test_response_message_paths_are_unchanged_when_redaction_is_off():
    response = {"summary": r"Saved C:\Models\Tower.rvt"}
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "0"}):
        assert redact_model_paths(response) is response


def test_export_folder_is_redacted_when_enabled():
    response = {"data": {"folder": r"C:\Models\exports\2026", "files": [{"name": "Doors.csv"}]}}
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
        assert redact_model_paths(response) == {
            "data": {"folder": "2026", "files": [{"name": "Doors.csv"}]}
        }


def test_nwc_export_defaults_and_options_reach_channel():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(server.call_tool("revit_export_nwc", {"path": "C:\\x\\a.nwc"}))
    payload = execute.await_args.args[0].payload
    assert execute.await_args.args[1] == 1800
    assert payload == {
        "command": "export-nwc",
        "targetProcessId": 42,
        "path": "C:\\x\\a.nwc",
        "overwrite": False,
        "dryRun": False,
        "confirmToken": None,
    }


def test_cad_actions_map_arguments_and_timeouts():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_link_cad",
            {
                "path": r"C:\Plans\Floor.dwg",
                "view": 12,
                "link": False,
                "layers": ["Walls"],
                "dry_run": True,
            },
        )
    )
    assert execute.await_args.args[1] == 600
    assert execute.await_args.args[0].payload == {
        "command": "link-cad",
        "targetProcessId": 42,
        "path": r"C:\Plans\Floor.dwg",
        "view": "12",
        "level": None,
        "cadLink": False,
        "origin": "internal",
        "units": "auto",
        "layers": ["Walls"],
        "dryRun": True,
    }
    asyncio.run(
        server.call_tool(
            "revit_walls_from_cad",
            {"cad_id": 17, "layers": ["Walls"], "level": "Level 1", "min_length_mm": 500},
        )
    )
    assert execute.await_args.args[0].payload == {
        "command": "walls-from-cad",
        "targetProcessId": 42,
        "cadId": 17,
        "layers": ["Walls"],
        "level": "Level 1",
        "wallType": None,
        "heightMm": 3000,
        "minThicknessMm": 80,
        "maxThicknessMm": 700,
        "minLengthMm": 500,
        "maxGapMm": 3000,
        "join": True,
        "dryRun": False,
    }


@pytest.mark.parametrize("max_gap_mm", [-1, float("inf"), float("nan")])
def test_walls_from_cad_rejects_invalid_gap(max_gap_mm):
    import asyncio

    server, execute, _ = action_server()
    with pytest.raises((ToolError, ValueError)):
        asyncio.run(
            server.call_tool(
                "revit_walls_from_cad",
                {
                    "cad_id": 17,
                    "layers": ["Walls"],
                    "level": "L1",
                    "max_gap_mm": max_gap_mm,
                },
            )
        )
    execute.assert_not_awaited()


def test_file_export_maps_targets_options_and_timeout():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_export",
            {
                "format": "pdf",
                "sheets": ["A1"],
                "all_sheets": True,
                "output_dir": r"C:\Exports",
                "options": {"combine": False},
                "overwrite": True,
                "dry_run": True,
                "timeout_seconds": 600,
            },
        )
    )
    assert execute.await_args.args[1] == 600
    assert execute.await_args.args[0].payload == {
        "command": "export",
        "targetProcessId": 42,
        "format": "pdf",
        "views": None,
        "sheets": ["A1"],
        "sheetSet": None,
        "allSheets": True,
        "folder": r"C:\Exports",
        "options": {"combine": False},
        "overwrite": True,
        "dryRun": True,
        "confirmToken": None,
    }


def test_nwc_xml_and_explicit_false_reach_channel():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_export_nwc",
            {
                "path": "C:\\x\\a.nwc",
                "settings_xml": "C:\\x\\settings.xml",
                "export_links": False,
            },
        )
    )
    payload = execute.await_args.args[0].payload
    assert payload["settingsXml"] == "C:\\x\\settings.xml"
    assert payload["exportLinks"] is False
    assert "parameters" not in payload


def test_nwc_export_overrides_reach_channel():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_export_nwc",
            {
                "path": "C:\\x\\a.nwc",
                "scope": "selection",
                "element_ids": [1],
                "coordinates": "internal",
                "parameters": "none",
                "export_element_ids": False,
                "convert_element_properties": True,
                "export_parts": True,
                "export_room_as_attribute": False,
                "export_room_geometry": False,
                "convert_lights": True,
                "convert_linked_cad_formats": False,
                "export_links": True,
                "export_urls": False,
                "divide_file_into_levels": False,
                "find_missing_materials": False,
                "faceting_factor": 5,
                "overwrite": True,
                "dry_run": True,
                "timeout_seconds": 900,
            },
        )
    )
    payload = execute.await_args.args[0].payload
    assert execute.await_args.args[1] == 900
    assert payload["elementIds"] == [1]
    assert payload["coordinates"] == "internal"
    assert payload["parameters"] == "none"
    for key in (
        "exportElementIds",
        "exportRoomAsAttribute",
        "exportRoomGeometry",
        "convertLinkedCadFormats",
        "exportUrls",
        "divideFileIntoLevels",
        "findMissingMaterials",
    ):
        assert payload[key] is False
    for key in ("convertElementProperties", "exportParts", "convertLights", "exportLinks"):
        assert payload[key] is True
    assert payload["facetingFactor"] == 5
    assert payload["overwrite"] is True
    assert payload["dryRun"] is True


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
                "timeout_seconds": 600,
            },
        )
    )
    job = execute.await_args.args[0]
    assert job.command == "align-link-datums"
    assert job.payload["nameMap"] == {"A": "A1"}
    assert job.payload["dryRun"] is True
    assert execute.await_args.args[1] == 600


def test_undo_last_sends_command_without_extra_arguments():
    import asyncio

    server, execute, host = action_server()
    asyncio.run(server.call_tool("revit_undo_last", {"document": "Model"}))
    job = execute.await_args.args[0]
    assert job.command == "undo-last"
    assert job.payload["targetDocument"] == "Model"
    host.list_revit_instances.assert_awaited_once()


def test_undo_last_in_read_only_mode_never_reaches_channel():
    import asyncio

    server, execute, host = action_server(read_only=True)
    result = asyncio.run(server.call_tool("revit_undo_last", {}))
    assert "read-only mode" in str(result)
    execute.assert_not_awaited()
    host.list_revit_instances.assert_not_awaited()


def test_align_link_datums_rejects_invalid_timeout():
    import asyncio

    server, execute, _ = action_server()
    with pytest.raises(Exception):
        asyncio.run(
            server.call_tool("revit_align_link_datums", {"link": "AR.rvt", "timeout_seconds": 0})
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


@pytest.mark.parametrize("timeout_seconds", [None, 900])
def test_action_response_timeout_reaches_channel(timeout_seconds):
    import asyncio

    _, execute, host = action_server()
    timeout = {} if timeout_seconds is None else {"timeout_seconds": timeout_seconds}
    asyncio.run(_send_action(execute, lambda: host, "select", elementIds=[1], **timeout))
    execute.assert_awaited_once()
    assert execute.await_args.args[1:] == (timeout_seconds or 120, 300, None)


@pytest.mark.parametrize(
    "name,arguments,payload",
    [
        ("revit_select", {"element_ids": []}, {"elementIds": []}),
        ("revit_show", {"element_ids": [1]}, {"elementIds": [1], "select": True}),
        ("revit_isolate", {"element_ids": [], "reset": True}, {"elementIds": [], "reset": True}),
        (
            "revit_override_graphics",
            {"element_ids": [1]},
            {
                "elementIds": [1],
                "color": "#FF0000",
                "viewScope": "active",
                "views": None,
                "halftoneOthers": False,
                "lineWeight": None,
                "fill": True,
                "transparency": 0,
                "reset": False,
                "dryRun": False,
            },
        ),
        (
            "revit_override_graphics",
            {"element_ids": [1], "views": [23, "Section A"], "reset": True},
            {
                "elementIds": [1],
                "color": "#FF0000",
                "viewScope": "list",
                "views": ["23", "Section A"],
                "halftoneOthers": False,
                "lineWeight": None,
                "fill": True,
                "transparency": 0,
                "reset": True,
                "dryRun": False,
            },
        ),
        (
            "revit_move",
            {"element_ids": [1], "dx_mm": 304.8, "dy_mm": -50},
            {"elementIds": [1], "dxMm": 304.8, "dyMm": -50.0, "dzMm": 0},
        ),
        (
            "revit_rotate",
            {"element_ids": [1], "angle_deg": 45},
            {"elementIds": [1], "angleDeg": 45.0, "centerMm": None},
        ),
        (
            "revit_copy",
            {"element_ids": [1], "dx_mm": 100, "dy_mm": 0, "count": 2},
            {"elementIds": [1], "dxMm": 100.0, "dyMm": 0.0, "dzMm": 0, "count": 2},
        ),
        (
            "revit_mirror",
            {"element_ids": [1], "axis": "x", "point_mm": [0, 0]},
            {"elementIds": [1], "axis": "x", "pointMm": [0.0, 0.0], "copy": True},
        ),
        (
            "revit_change_type",
            {"element_ids": [1], "type_name": "Basic"},
            {"elementIds": [1], "typeName": "Basic", "family": None},
        ),
        (
            "revit_update_parameters",
            {
                "filters": {"categories": ["Walls"], "level": "L1"},
                "parameter": "Mark",
                "value": "A",
            },
            {
                "queryFilters": {"categories": ["Walls"], "level": "L1"},
                "parameter": "Mark",
                "value": "A",
                "parameterId": None,
                "maxElements": 5000,
                "includeTypeParameters": False,
            },
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
        (
            "revit_set_parameter",
            {"element_id": 1, "parameter": "Mark", "parameter_id": "ALL_MODEL_MARK", "value": 12},
            {"elementId": 1, "parameter": "Mark", "parameterId": "ALL_MODEL_MARK", "value": 12},
        ),
        (
            "revit_set_parameter",
            {"element_id": 1, "parameter": "Area", "parameter_id": "123", "value": 2.5},
            {"elementId": 1, "parameter": "Area", "parameterId": "123", "value": 2.5},
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
    "name,arguments,expected",
    [
        (
            "revit_load_family",
            {"paths": [r"C:\Families\Chair.rfa"]},
            {
                "paths": [r"C:\Families\Chair.rfa"],
                "overwrite": False,
                "overwriteParameterValues": False,
            },
        ),
        (
            "revit_place_families",
            {
                "placements": [
                    {"family": "Chair", "type_name": "A", "x_mm": 1, "y_mm": 2, "level": "L1"}
                ]
            },
            {
                "placements": [
                    {
                        "family": "Chair",
                        "typeName": "A",
                        "xMm": 1.0,
                        "yMm": 2.0,
                        "zMm": 0.0,
                        "level": "L1",
                        "rotationDeg": 0.0,
                    }
                ]
            },
        ),
        (
            "revit_place_families",
            {"at_rooms": {"family": "Chair", "type_name": "A", "level": "L1"}},
            {
                "atRooms": {
                    "family": "Chair",
                    "typeName": "A",
                    "level": "L1",
                    "zMm": 0.0,
                    "rotationDeg": 0.0,
                }
            },
        ),
    ],
)
def test_bulk_family_payloads(name, arguments, expected):
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(server.call_tool(name, arguments))
    payload = execute.await_args.args[0].payload
    for key, value in expected.items():
        assert payload[key] == value
    assert payload["dryRun"] is False


def test_bulk_placement_options_reach_channel():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_place_families",
            {
                "placements": [
                    {
                        "family": "Chair",
                        "type_name": "A",
                        "x_mm": 100,
                        "y_mm": 200,
                        "z_mm": 300,
                        "level": "L1",
                        "host_id": 42,
                        "parameters": {"Mark": "C1"},
                    }
                ],
                "load": [r"C:\Families\Chair.rfa"],
                "stop_on_error": False,
                "timeout_seconds": 600,
            },
        )
    )
    job = execute.await_args.args[0]
    assert job.payload["placements"][0]["hostId"] == 42
    assert job.payload["placements"][0]["zMm"] == 300.0
    assert job.payload["placements"][0]["parameters"] == {"Mark": "C1"}
    assert job.payload["load"] == [r"C:\Families\Chair.rfa"]
    assert job.payload["stopOnError"] is False
    assert execute.await_args.args[1] == 600


def test_batch_accepts_load_family_step():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_run_actions",
            {"steps": [{"action": "load_family", "args": {"paths": [r"C:\Families\Chair.rfa"]}}]},
        )
    )
    step = execute.await_args.args[0].payload["steps"][0]
    assert step["command"] == "load-family"
    assert step["paths"] == [r"C:\Families\Chair.rfa"]
    assert step["overwriteParameterValues"] is False


def test_batch_accepts_override_graphics_step():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_run_actions",
            {
                "steps": [
                    {"action": "override_graphics", "args": {"element_ids": [42], "views": [17]}}
                ]
            },
        )
    )
    step = execute.await_args.args[0].payload["steps"][0]
    assert step["command"] == "override-graphics"
    assert step["viewScope"] == "list"
    assert step["views"] == ["17"]


@pytest.mark.parametrize(
    "name,arguments",
    [
        ("revit_load_family", {"paths": []}),
        ("revit_load_family", {"paths": ["x"] * 101}),
        ("revit_place_families", {}),
        ("revit_place_families", {"placements": [], "at_rooms": {"family": "A", "type_name": "B"}}),
        (
            "revit_place_families",
            {
                "placements": [
                    {"family": "A", "type_name": "B", "x_mm": 0, "y_mm": 0, "level": "L"}
                ]
                * 2001
            },
        ),
        (
            "revit_place_families",
            {
                "placements": [
                    {"family": "A", "type_name": "B", "x_mm": math.inf, "y_mm": 0, "level": "L"}
                ]
            },
        ),
    ],
)
def test_bulk_family_limits(name, arguments):
    import asyncio

    server, execute, _ = action_server()
    with pytest.raises(Exception):
        asyncio.run(server.call_tool(name, arguments))
    execute.assert_not_awaited()


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
        ("revit_override_graphics", {"element_ids": [], "views": "all"}),
        ("revit_override_graphics", {"element_ids": [1], "color": "red"}),
        ("revit_override_graphics", {"element_ids": [1], "line_weight": 17}),
        ("revit_override_graphics", {"element_ids": [1], "transparency": 101}),
        ("revit_move", {"element_ids": [1], "dx_mm": math.inf, "dy_mm": 0}),
        ("revit_move", {"element_ids": [1], "dx_mm": 0}),
        ("revit_rotate", {"element_ids": [1], "angle_deg": math.inf}),
        ("revit_copy", {"element_ids": [1], "dx_mm": 1, "dy_mm": 0, "count": 101}),
        ("revit_mirror", {"element_ids": [1], "axis": "z", "point_mm": [0, 0]}),
        ("revit_change_type", {"element_ids": [1], "type_name": ""}),
        (
            "revit_update_parameters",
            {"filters": {}, "parameter": "Mark", "value": "A", "max_elements": 20001},
        ),
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
        (
            "revit_set_parameter",
            {"element_id": 1, "parameter": "Mark", "parameter_id": " ", "value": 1},
        ),
        (
            "revit_set_parameter",
            {"element_id": 1, "parameter": "Mark", "parameter_id": "0", "value": 1},
        ),
        (
            "revit_set_parameter",
            {"element_id": 1, "parameter": "Mark", "parameter_id": "invalid", "value": 1},
        ),
        ("revit_set_parameter", {"element_id": 1, "parameter": "Mark", "value": True}),
        ("revit_set_parameter", {"element_id": 1, "parameter": "Mark", "value": math.inf}),
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
    job = Job(command, {**payload, "targetProcessId": 42}).for_document(document)
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

    result = asyncio.run(RevitChannel(host).execute(job))

    sent = json.loads(host.prepare_job.await_args.args[1])
    correlation_id = sent.pop("correlationId")
    assert len(correlation_id) == 32
    assert len(sent.pop("jobId")) == 32
    assert len(sent.pop("clientId")) == 32
    assert sent.pop("clientName") == "unknown"
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
        [
            {
                "action": "set_parameter",
                "args": {"element_id": 1, "parameter": "Mark", "parameter_id": " ", "value": 1},
            }
        ],
        [
            {
                "action": "set_parameter",
                "args": {"element_id": 1, "parameter": "Mark", "parameter_id": "0", "value": 1},
            }
        ],
        [
            {
                "action": "set_parameter",
                "args": {"element_id": 1, "parameter": "Mark", "value": True},
            }
        ],
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
        asyncio.run(server.call_tool("revit_run_actions", {"steps": steps}))
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
            "revit_run_actions",
            {
                "steps": [
                    {"action": "move", "args": {"element_ids": [1], "dx_mm": 10, "dy_mm": 0}},
                    {
                        "action": "set_parameter",
                        "args": {
                            "element_id": 1,
                            "parameter": "Comments",
                            "parameter_id": "123",
                            "value": 7,
                        },
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
                "parameterId": "123",
                "value": 7,
                "dryRun": False,
            },
        ],
    }
    tools = asyncio.run(server.list_tools())
    tool = next(tool for tool in tools if tool.name == "revit_run_actions")
    assert tool.annotations.read_only_hint is False
    assert tool.annotations.destructive_hint is True
    assert tool.annotations.idempotent_hint is False


def test_update_parameters_batch_uses_query_filter_names():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_run_actions",
            {
                "steps": [
                    {
                        "action": "update_parameters",
                        "args": {
                            "filters": {
                                "categories": ["Walls"],
                                "level": "Level 1",
                                "type_name": "Basic",
                                "area_scheme": "Gross",
                                "parameter_filters": [
                                    {"parameter": "Mark", "operator": "not_empty"}
                                ],
                            },
                            "parameter": "Comments",
                            "value": "Reviewed",
                        },
                    }
                ]
            },
        )
    )
    step = execute.await_args.args[0].payload["steps"][0]
    assert step["command"] == "update-parameters"
    assert step["queryFilters"] == {
        "categories": ["Walls"],
        "level": "Level 1",
        "type": "Basic",
        "areaScheme": "Gross",
        "parameterFilters": [{"parameter": "Mark", "operator": "not_empty"}],
    }
    assert step["parameter"] == "Comments"
    assert step["value"] == "Reviewed"
    assert step["maxElements"] == 5000
    assert step["includeTypeParameters"] is False


def test_update_parameters_normalizes_filters_and_type_opt_in():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_update_parameters",
            {
                "filters": {
                    "categories": [" Walls ", "walls"],
                    "parameter_filters": [{"parameter": " Mark ", "operator": " NOT_EMPTY "}],
                },
                "parameter": "Comments",
                "value": "Reviewed",
                "include_type_parameters": True,
            },
        )
    )
    payload = execute.await_args.args[0].payload
    assert payload["queryFilters"] == {
        "categories": ["Walls"],
        "parameterFilters": [{"parameter": "Mark", "operator": "not_empty"}],
    }
    assert payload["includeTypeParameters"] is True


def test_update_parameters_rejects_invalid_parameter_filters():
    with pytest.raises(ValueError, match="requires parameter and operator"):
        query_filter_payload(UpdateFilters(parameter_filters=[{"parameter": "Mark"}]))


def test_in_process_action_titles():
    import asyncio

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


def test_edit_families_maps_discriminated_operations_and_defaults():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_edit_families",
            {
                "families": ["Door"],
                "operations": [
                    {
                        "op": "add_shared_parameters",
                        "parameters": [{"name": "Tag", "group": "Data"}],
                    },
                    {"op": "remove_parameters", "names": ["Old"]},
                    {"op": "purge"},
                    {"op": "set_shared", "shared": True},
                ],
            },
        )
    )
    job = execute.await_args.args[0]
    assert job.command == "edit-families"
    assert job.payload["overwriteParameterValues"] is False
    assert job.payload["stopOnError"] is True
    assert job.payload["dryRun"] is False
    assert job.payload["operations"][0]["replaceFamilyParameter"] is False
    assert job.payload["operations"][0]["parameters"][0]["instance"] is True
    assert job.payload["operations"][1]["includeShared"] is False
    assert execute.await_args.args[1] == 1800


def test_edit_families_rejects_unknown_op_and_empty_names():
    import asyncio

    server, execute, _ = action_server()
    for operations in ([{"op": "unknown"}], [{"op": "remove_parameters", "names": []}]):
        with pytest.raises(Exception):
            asyncio.run(server.call_tool("revit_edit_families", {"operations": operations}))
    execute.assert_not_awaited()


def test_document_lifecycle_explicit_pid_precedes_document_and_rejects_contradiction():
    import asyncio

    server, execute, host = action_server()
    host.list_revit_instances.return_value = [
        {"processId": 42, "documentTitle": "Tower"},
        {"processId": 84, "documentTitle": "Depot"},
    ]
    asyncio.run(
        server.call_tool("revit_open_document", {"path": r"C:\models\new.rvt", "processId": 84})
    )
    assert execute.await_args.args[0].payload["targetProcessId"] == 84
    execute.reset_mock()
    with pytest.raises(Exception, match="contradict"):
        asyncio.run(
            server.call_tool("revit_close_document", {"document": "Tower", "process_id": 84})
        )
    execute.assert_not_awaited()
    with pytest.raises(Exception):
        asyncio.run(
            server.call_tool("revit_open_document", {"path": r"C:\models\new.rvt", "process_id": 0})
        )
    execute.assert_not_awaited()


def test_view_and_sheet_action_mapping():
    import asyncio

    server, execute, _ = action_server()
    cases = [
        (
            "revit_create_view",
            {"kind": "section", "box": {"min_mm": [0, 0, 0], "max_mm": [100, 100, 100]}},
            "create-view",
            "box",
        ),
        (
            "revit_duplicate_view",
            {"view": "Level 1", "mode": "dependent"},
            "duplicate-view",
            "mode",
        ),
        (
            "revit_apply_view_template",
            {"views": ["Level 1"], "template": "Plan"},
            "apply-view-template",
            "views",
        ),
        ("revit_create_sheet", {"number": "A101", "name": "Plan"}, "create-sheet", "titleBlock"),
        (
            "revit_place_views_on_sheet",
            {"sheet": "A101", "views": [{"view": "Level 1", "x_mm": 10, "y_mm": 20}]},
            "place-views-on-sheet",
            "placements",
        ),
    ]
    for tool, arguments, command, key in cases:
        asyncio.run(server.call_tool(tool, arguments))
        payload = execute.await_args.args[0].payload
        assert payload["command"] == command
        assert key in payload
        if command == "create-view":
            assert payload["box"] == {"minMm": [0.0, 0.0, 0.0], "maxMm": [100.0, 100.0, 100.0]}
        if command == "place-views-on-sheet":
            assert payload["placements"] == [{"view": "Level 1", "xMm": 10.0, "yMm": 20.0}]


def test_create_view_batch_mapping_and_validation():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_run_actions",
            {
                "steps": [
                    {"action": "create_view", "args": {"kind": "floor_plan", "level": "Level 1"}}
                ]
            },
        )
    )
    assert execute.await_args.args[0].payload["steps"][0]["command"] == "create-view"
    with pytest.raises((ToolError, ValueError)):
        asyncio.run(server.call_tool("revit_create_view", {"kind": "section"}))


@pytest.mark.parametrize(
    "bounds", [{}, {"element_ids": [1]}, {"box": {"min_mm": [0, 0, 0], "max_mm": [100, 100, 100]}}]
)
def test_create_view_3d_defaults_and_optional_bounds(bounds):
    import asyncio

    from revit_model_mcp.actions import ActionStep

    server, execute, _ = action_server()
    asyncio.run(server.call_tool("revit_create_view", {"kind": "3d", **bounds}))
    payload = execute.await_args.args[0].payload
    assert payload["displayStyle"] == "shaded"
    assert payload["detailLevel"] == "fine"
    assert payload["elementIds"] == bounds.get("element_ids")
    assert (payload["box"] is None) == ("box" not in bounds)
    step = ActionStep(action="create_view", args={"kind": "3d", **bounds})
    assert step.payload()["displayStyle"] == "shaded"
    assert step.payload()["detailLevel"] == "fine"


def test_create_view_explicit_styles_and_plan_defaults():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(
        server.call_tool(
            "revit_create_view",
            {
                "kind": "3d",
                "display_style": "consistent_colors",
                "detail_level": "medium",
            },
        )
    )
    assert execute.await_args.args[0].payload["displayStyle"] == "consistent_colors"
    assert execute.await_args.args[0].payload["detailLevel"] == "medium"
    asyncio.run(server.call_tool("revit_create_view", {"kind": "floor_plan", "level": "L1"}))
    assert execute.await_args.args[0].payload["displayStyle"] is None
    assert execute.await_args.args[0].payload["detailLevel"] is None


@pytest.mark.parametrize(
    "arguments",
    [
        {"kind": "section"},
        {"kind": "floor_plan"},
        {"kind": "3d", "box": {"min_mm": [0, 0, 0], "max_mm": [1, 1, 1]}, "element_ids": [1]},
        {"kind": "drafting", "element_ids": [1]},
        {"kind": "3d", "display_style": "wireframe"},
        {"kind": "3d", "detail_level": "undefined"},
        {"kind": "3d", "element_ids": []},
        {"kind": "3d", "box": {"min_mm": [0, 0, 0], "max_mm": [0, 1, 1]}},
    ],
)
def test_create_view_validation_is_clean_tool_error(arguments):
    import asyncio

    server, execute, _ = action_server()
    with pytest.raises(ToolError) as error:
        asyncio.run(server.call_tool("revit_create_view", arguments))
    assert "Traceback" not in str(error.value)
    execute.assert_not_awaited()


def test_create_mep_run_maps_points_sizes_and_batch():
    import asyncio

    server, execute, _ = action_server()
    arguments = {
        "kind": "duct",
        "points_mm": [[0, 0], [1000, 0], [1000, 1000, 3000]],
        "level": "Level 1",
        "width_mm": 400,
        "height_mm": 200,
        "connect_to": 42,
        "dry_run": True,
    }
    asyncio.run(server.call_tool("revit_create_mep_run", arguments))
    payload = execute.await_args.args[0].payload
    assert payload["command"] == "create-mep-run"
    assert payload["pointsMm"] == arguments["points_mm"]
    assert payload["widthMm"] == 400
    assert payload["heightMm"] == 200
    assert payload["connectTo"] == 42
    assert payload["dryRun"] is True

    asyncio.run(
        server.call_tool(
            "revit_run_actions", {"steps": [{"action": "create_mep_run", "args": arguments}]}
        )
    )
    assert execute.await_args.args[0].payload["steps"][0]["command"] == "create-mep-run"


@pytest.mark.parametrize(
    "arguments",
    [
        {"kind": "duct", "points_mm": [[0, 0]], "level": "L1"},
        {"kind": "pipe", "points_mm": [[0, 0], [0, 0]], "level": "L1"},
        {"kind": "conduit", "points_mm": [[0, 0], [100, 0]], "level": "L1", "width_mm": 10},
        {"kind": "cable_tray", "points_mm": [[0, 0], [100, 0]], "level": "L1", "diameter_mm": 10},
        {
            "kind": "duct",
            "points_mm": [[0, 0], [100, 0]],
            "level": "L1",
            "width_mm": 10,
            "diameter_mm": 10,
        },
    ],
)
def test_create_mep_run_rejects_invalid_geometry_and_sizes(arguments):
    import asyncio

    server, execute, _ = action_server()
    with pytest.raises((ToolError, ValueError)):
        asyncio.run(server.call_tool("revit_create_mep_run", arguments))
    execute.assert_not_awaited()


def test_addin_schedule_not_found_precedes_non_schedule_error():
    source = (
        Path(__file__).resolve().parents[2]
        / "src/RevitModelMcp.Addin/Control/ReadCommandExecutor.cs"
    ).read_text()
    schedule = source.split("internal static ScheduleDataResult ReadSchedule(", 1)[1]
    assert re.search(
        r"if \(view is null\)\s+throw new ArgumentException\("
        r"\$\"Schedule '\{reference\}' was not found\.\"\);",
        schedule,
    )
    assert schedule.index("Schedule '{reference}' was not found.") < schedule.index(
        "'{reference}' is not a schedule."
    )
    assert "if (view is not ViewSchedule schedule || schedule.IsTemplate)" in schedule


def test_addin_change_type_empty_candidates_has_specific_error():
    source = (
        Path(__file__).resolve().parents[2] / "src/RevitModelMcp.Addin/Control/ActionMutations.cs"
    ).read_text()
    change_type = source.split("internal static ActionResultData ChangeType(", 1)[1].split(
        "internal static ActionResultData UpdateParameters(", 1
    )[0]
    assert re.search(
        r"if \(valid.Count == 0\)\s+throw new ArgumentException\("
        r"\$\"Element \{RevitValueReader.GetId\(id\)\} has no compatible types\.\"\);",
        change_type,
    )
    assert change_type.index("has no compatible types.") < change_type.index("Candidates:")


def test_batch_allowlists_and_process_models_schema_match():
    import asyncio

    from revit_model_mcp.actions import _BATCH_FIELDS, ActionStep

    parser = (
        Path(__file__).resolve().parents[2] / "src/RevitModelMcp.Core/Control/ActionJobParser.cs"
    ).read_text()
    allowlist = parser.split("Require(stepCommand is ", 1)[1].split('"Unknown batch step."', 1)[0]
    commands = set(re.findall(r'"([a-z-]+)"', allowlist))
    actions = set(_BATCH_FIELDS)
    assert commands == {action.replace("_", "-") for action in actions}
    assert set(ActionStep.model_json_schema()["properties"]["action"]["enum"]) == actions
    assert {"override_graphics", "create_mep_run"} <= actions
    server, _, _ = action_server()
    tools = asyncio.run(server.list_tools())
    for name in ("revit_run_actions", "revit_process_models"):
        schema = next(tool.input_schema for tool in tools if tool.name == name)
        assert set(schema["$defs"]["ActionStep"]["properties"]["action"]["enum"]) == actions


@pytest.mark.parametrize(
    "zoom,mode,ids",
    [("fit", "fit", None), ("none", "none", None), ([42, 43, 42], "elements", [42, 43])],
)
def test_activate_view_zoom_mapping(zoom, mode, ids):
    import asyncio

    server, execute, _ = action_server()
    result = asyncio.run(server.call_tool("revit_activate_view", {"view": "L1", "zoom": zoom}))
    assert not result.is_error
    payload = execute.await_args.args[0].payload
    assert payload["zoom"] == mode
    assert payload.get("zoomElementIds") == ids


@pytest.mark.parametrize("zoom", ["invalid", [], [0], [-1], [True], [1.5], ["42"]])
def test_activate_view_rejects_invalid_zoom(zoom):
    import asyncio

    server, execute, _ = action_server()
    with pytest.raises(ToolError):
        asyncio.run(server.call_tool("revit_activate_view", {"view": "L1", "zoom": zoom}))
    execute.assert_not_awaited()


def test_activate_view_element_zoom_refused_in_read_only_mode():
    import asyncio

    server, execute, _ = action_server(read_only=True)
    result = asyncio.run(server.call_tool("revit_activate_view", {"view": "L1", "zoom": [42]}))
    assert "read-only mode" in str(result)
    execute.assert_not_awaited()


def test_cancel_action_job_maps_polling_request_and_read_only_gate():
    import asyncio

    server, execute, _ = action_server()
    asyncio.run(server.call_tool("revit_cancel_job", {"job_id": "a" * 32, "process_id": 42}))
    job = execute.await_args.args[0]
    assert job.command == "jobs"
    assert job.payload["fetchJobId"] == "a" * 32
    assert job.payload["requestCancellation"] is True
    assert job.payload["targetProcessId"] == 42
    blocked, blocked_execute, _ = action_server(read_only=True)
    result = asyncio.run(blocked.call_tool("revit_cancel_job", {"job_id": "a" * 32}))
    assert "read-only mode" in str(result)
    blocked_execute.assert_not_awaited()


def test_process_progress_current_path_is_redacted():
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
        assert redact_model_paths({"progress": {"currentPath": r"C:\Private\Model.rvt"}}) == {
            "progress": {"currentPath": "Model.rvt"}
        }


@pytest.mark.parametrize(
    ("tool_name", "expected_text"),
    [
        ("revit_update_parameters", ["skipped.inGroup", "dry_run", "groups"]),
        ("revit_set_parameter", ["Group members", "clear message", "dry_run"]),
        ("revit_walls_from_cad", ["unjoinedEnds", "unjoinedReasons", "dry_run"]),
        *(
            (name, ["skipped.inGroup", "dry_run", "every element"])
            for name in (
                "revit_move",
                "revit_rotate",
                "revit_copy",
                "revit_mirror",
                "revit_change_type",
            )
        ),
    ],
)
def test_preflight_tool_descriptions(tool_name, expected_text):
    import asyncio

    server = MCPServer("preflight-descriptions")
    register_actions(server, AsyncMock(), lambda: AsyncMock())
    tools = asyncio.run(server.list_tools())
    description = next(tool.description for tool in tools if tool.name == tool_name)
    for text in expected_text:
        assert text in description


def test_mirror_batch_step_keeps_copy_arg():
    from revit_model_mcp.actions import ActionStep

    base = {"element_ids": [1], "axis": "x", "point_mm": [0, 0]}
    assert ActionStep(action="mirror", args=base).args["copy"] is True
    assert ActionStep(action="mirror", args={**base, "copy": False}).args["copy"] is False


def test_actions_import_emits_no_user_warning():
    import subprocess
    import sys

    result = subprocess.run(
        [sys.executable, "-W", "error::UserWarning", "-c", "import revit_model_mcp.actions"],
        check=False,
    )
    assert result.returncode == 0


CONFIRMED_TOOL_CALLS = {
    "revit_remove_links": {"links": ["Link A"]},
    "revit_execute_code": {"code": "return 42;"},
    "revit_export": {"format": "pdf", "all_sheets": True, "overwrite": True},
    "revit_export_nwc": {"path": "C:\\out\\model.nwc", "overwrite": True},
}


@pytest.mark.parametrize("tool", CONFIRMED_TOOL_CALLS)
def test_confirmation_token_is_mapped_into_payload(tool):
    import asyncio

    server, execute, _ = action_server()
    arguments = CONFIRMED_TOOL_CALLS[tool]
    asyncio.run(server.call_tool(tool, arguments))
    assert execute.await_args.args[0].payload["confirmToken"] is None
    asyncio.run(server.call_tool(tool, {**arguments, "confirm_token": "tok-1"}))
    assert execute.await_args.args[0].payload["confirmToken"] == "tok-1"


@pytest.mark.parametrize("tool", ["revit_remove_links", "revit_execute_code"])
def test_confirmation_two_step_flow_repeats_arguments(tool):
    import asyncio

    server, execute, _ = action_server()
    execute.side_effect = [
        {
            "success": True,
            "data": {
                "needsConfirmation": True,
                "confirmationText": "Confirm.",
                "confirmToken": "tok-2",
            },
        },
        {"success": True, "data": {}, "activeView": "Level 1"},
    ]
    arguments = CONFIRMED_TOOL_CALLS[tool]
    asyncio.run(server.call_tool(tool, arguments))
    asyncio.run(server.call_tool(tool, {**arguments, "confirm_token": "tok-2"}))
    first = execute.await_args_list[0].args[0].payload
    second = execute.await_args_list[1].args[0].payload
    assert first["confirmToken"] is None
    assert second["confirmToken"] == "tok-2"
    assert {k: v for k, v in first.items() if k != "confirmToken"} == {
        k: v for k, v in second.items() if k != "confirmToken"
    }


def test_confirmation_tools_describe_confirm_token():
    import asyncio

    server, _, _ = action_server()
    tools = {tool.name: tool.description for tool in asyncio.run(server.list_tools())}
    for name in CONFIRMED_TOOL_CALLS:
        assert "confirm_token" in tools[name], name
    assert "confirmation token" in tools["revit_process_models"]
    assert 'transaction="none" is refused' in tools["revit_execute_code"]
