from __future__ import annotations

import asyncio
import json
import sys
from unittest.mock import patch

import pytest

from revit_model_mcp import server as revit_server
from revit_model_mcp.toolsets import (
    TOOLSETS,
    apply_toolsets,
    parse_toolsets,
    select_tools,
)

TOOLS_LIST_BUDGET_BYTES = 105_000


def _registered() -> set[str]:
    return {tool.name for tool in asyncio.run(revit_server.mcp.list_tools())}


def test_every_tool_belongs_to_exactly_one_toolset():
    members = [tool for group in TOOLSETS.values() for tool in group]
    assert len(members) == len(set(members))
    assert set(members) == _registered()


def test_default_and_all_keep_every_tool():
    for value in (None, "", "all", "core,ALL"):
        assert parse_toolsets(value) == []
        assert select_tools(value) == _registered()


def test_selection_always_includes_core():
    assert parse_toolsets("read, batch") == ["core", "read", "batch"]
    assert select_tools("code") == {*TOOLSETS["core"], *TOOLSETS["code"]}
    assert TOOLSETS["code"] == ("revit_execute_code", "revit_api_lookup")


def test_unknown_toolset_is_rejected_with_the_valid_names():
    with pytest.raises(ValueError, match="Unknown toolset nope.*core.*cad-mep"):
        parse_toolsets("read,nope")


def test_apply_toolsets_limits_tools_list():
    server = revit_server.RevitMCPServer("toolsets-test")
    for name in ("revit_ping", "revit_move", "revit_execute_code", "revit_query_elements"):
        server.add_tool(lambda: None, name=name)
    removed = apply_toolsets(server, "read")
    assert removed == ["revit_move", "revit_execute_code"]
    assert {tool.name for tool in asyncio.run(server.list_tools())} == {
        "revit_ping",
        "revit_query_elements",
    }
    assert apply_toolsets(server, "all") == []


def test_main_applies_environment_selection(monkeypatch):
    applied = []
    monkeypatch.setattr(sys, "argv", ["revit-model-mcp"])
    monkeypatch.setenv("REVIT_MCP_TOOLSETS", "core,read")
    with (
        patch.object(revit_server, "apply_toolsets", lambda _server, value: applied.append(value)),
        patch.object(revit_server, "check_for_updates"),
        patch.object(revit_server.mcp, "run"),
    ):
        revit_server.main()
        monkeypatch.setattr(sys, "argv", ["revit-model-mcp", "--toolsets", "code"])
        revit_server.main()
    assert applied == ["core,read", "code"]


def test_main_rejects_unknown_toolset(monkeypatch, capsys):
    monkeypatch.setattr(sys, "argv", ["revit-model-mcp", "--toolsets", "nope"])
    with pytest.raises(SystemExit) as stop:
        revit_server.main()
    assert stop.value.code == 2
    assert "Unknown toolset nope" in capsys.readouterr().err


def test_tools_list_stays_within_size_budget():
    tools = asyncio.run(revit_server.mcp.list_tools())
    payload = [tool.model_dump(mode="json", by_alias=True, exclude_none=True) for tool in tools]
    size = len(json.dumps(payload, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))
    assert size < TOOLS_LIST_BUDGET_BYTES, f"tools/list is {size} bytes"


def test_schemas_carry_no_generated_titles():
    for tool in asyncio.run(revit_server.mcp.list_tools()):
        for name, schema in tool.input_schema.get("properties", {}).items():
            assert not isinstance(schema.get("title"), str) or name == "title", (tool.name, name)
