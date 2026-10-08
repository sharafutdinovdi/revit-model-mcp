from __future__ import annotations

import asyncio

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp import server as revit_server


def _call(name, arguments):
    return asyncio.run(revit_server.mcp.call_tool(name, arguments))


def _tool_names():
    return [tool.name for tool in asyncio.run(revit_server.mcp.list_tools())]


@pytest.mark.parametrize("name", _tool_names())
def test_every_tool_rejects_unknown_argument(name):
    with pytest.raises(ToolError, match="bogus_argument"):
        _call(name, {"bogus_argument": 1})


def test_registry_is_not_empty():
    assert len(_tool_names()) > 40


@pytest.mark.parametrize(
    ("name", "old", "new"),
    [
        ("revit_export", "folder", "output_dir"),
        ("revit_execute_code", "response_timeout_s", "timeout_seconds"),
        ("revit_jobs", "wait_s", "wait_seconds"),
        ("revit_jobs", "cancel_job_id", "revit_cancel_job"),
        ("revit_export_view", "save_to", "output_path"),
        ("revit_batch_fetch", "dest_dir", "output_dir"),
    ],
)
def test_renamed_argument_names_the_replacement(name, old, new):
    with pytest.raises(ToolError) as error:
        _call(name, {old: "x"})
    message = str(error.value)
    assert f"'{old}'" in message
    assert new in message


def test_folder_is_not_hinted_for_other_tools():
    with pytest.raises(ToolError) as error:
        _call("revit_move", {"folder": "x"})
    assert "output_dir" not in str(error.value)


def test_documented_camel_case_aliases_still_pass_the_check():
    tool = revit_server.mcp._tool_manager.get_tool("revit_model_health")
    accepted = revit_server._accepted_argument_names(tool)
    assert {"timeout_seconds", "timeoutSeconds", "process_id", "processId"} <= accepted
