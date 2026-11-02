"""Bounded properties for untrusted parser inputs."""

import asyncio
import copy
import json
import os
import tempfile
import time
from pathlib import Path, PureWindowsPath
from unittest.mock import AsyncMock, patch

import pytest
import yaml
from hypothesis import example, given, settings
from hypothesis import strategies as st
from jsonschema import ValidationError
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp import server
from revit_model_mcp.actions import redact_model_paths
from revit_model_mcp.audit.evaluate import Evaluator, NotAssessed, compile_pattern
from revit_model_mcp.audit.result import redact_path
from revit_model_mcp.audit.rules import load_rules

BOUNDED = settings(max_examples=200, deadline=500)
TEXT = st.text(max_size=80)
SCALAR = st.none() | st.booleans() | st.integers() | st.floats(allow_nan=False) | TEXT
JSON = st.recursive(
    SCALAR,
    lambda children: st.lists(children, max_size=5) | st.dictionaries(TEXT, children, max_size=5),
    max_leaves=15,
)
RULE_DICTS = st.dictionaries(
    st.sampled_from(["schema", "name", "rules", "extends", "references", "unknown"]),
    JSON,
    max_size=6,
)


@BOUNDED
@given(st.one_of(TEXT, RULE_DICTS.map(yaml.safe_dump)))
@example("schema: 1\nname: Test\nrules: []\n")
@example("[unterminated")
@example("&loop [*loop]")
def test_yaml_like_rule_files_only_raise_validation_errors(content):
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "rules.yaml"
        path.write_text(content, encoding="utf-8")
        try:
            load_rules(str(path))
        except (ValueError, ValidationError):
            pass


@st.composite
def rule_files(draw):
    identifiers = draw(st.lists(st.text(min_size=1, max_size=12), max_size=4, unique=True))
    rules = [
        {
            "id": identifier,
            "title": draw(st.text(min_size=1, max_size=20)),
            "severity": draw(st.sampled_from(["critical", "major", "minor", "info"])),
            "check": {"type": "name_matches", "pattern": draw(TEXT)},
            "enabled": draw(st.booleans()),
            "threshold": {"max_count": draw(st.integers(min_value=0, max_value=100))},
        }
        for identifier in identifiers
    ]
    return {"schema": 1, "name": draw(st.text(min_size=1, max_size=20)), "rules": rules}


@BOUNDED
@given(
    st.one_of(
        RULE_DICTS,
        st.tuples(
            rule_files(),
            st.sampled_from(["schema", "name", "rules", "extends", "references"]),
            JSON,
        ).map(lambda item: {**item[0], item[1]: item[2]}),
    )
)
def test_arbitrary_rule_dicts_only_raise_validation_errors(value):
    original = copy.deepcopy(value)
    try:
        load_rules(inline=value)
    except (ValueError, ValidationError):
        pass
    assert value == original


@BOUNDED
@given(rule_files())
def test_valid_rule_files_round_trip(value):
    expected = load_rules(inline=value)
    assert expected["rules"] == [{**rule, "pack": None} for rule in value["rules"]]
    with tempfile.TemporaryDirectory() as directory:
        for suffix, content in [("json", json.dumps(value)), ("yaml", yaml.safe_dump(value))]:
            path = Path(directory) / f"rules.{suffix}"
            path.write_text(content, encoding="utf-8")
            actual = load_rules(str(path))
            assert {k: v for k, v in actual.items() if k != "file"} == {
                k: v for k, v in expected.items() if k != "file"
            }


@BOUNDED
@given(
    st.one_of(st.text(max_size=220), st.text(alphabet=r"ab()[]{}?*+|\\0129,-^$", max_size=80)),
    TEXT,
)
@example("(a+)+$", "a" * 1000 + "!")
@example("(a|aa)+$", "a" * 30 + "!")
def test_regex_guard_is_bounded(pattern, text):
    started = time.monotonic()
    try:
        compiled = compile_pattern(pattern)
        evaluator = Evaluator(None, budget_seconds=0.05)
        evaluator.matches(compiled, text)
    except (NotAssessed, TimeoutError):
        pass
    assert time.monotonic() - started < 2


@BOUNDED
@given(JSON)
def test_redaction_is_pure_and_idempotent(value):
    original = copy.deepcopy(value)
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
        redacted = redact_model_paths(value)
        assert redact_model_paths(redacted) == redacted
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "0"}):
        assert redact_model_paths(value) == value
    assert value == original


@BOUNDED
@given(st.lists(st.text(alphabet="abcXYZ012_-", min_size=1, max_size=12), min_size=1, max_size=4))
def test_redaction_removes_directories(parts):
    filename = parts[-1] + ".rvt"
    for prefix in ["C:\\private\\", "\\\\host\\share\\", "/private/"]:
        path = prefix + "\\".join([*parts[:-1], filename])
        with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
            assert redact_path(path) == filename
            assert redact_model_paths({"nested": [{"path": path}]}) == {
                "nested": [{"path": filename}]
            }
            if prefix != "/private/":
                assert redact_model_paths({"message": f"Opened {path}"}) == {
                    "message": f"Opened {filename}"
                }
        with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "0"}):
            assert redact_path(path) == path


@BOUNDED
@given(TEXT)
def test_arbitrary_path_fields_keep_only_the_basename(path):
    with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}):
        expected = PureWindowsPath(path).name
        assert redact_path(path) == expected
        assert redact_model_paths({"path": path}) == {"path": expected}


@pytest.mark.parametrize("name", ["revit_document_info", "revit_model_health"])
@BOUNDED
@given(st.none() | TEXT)
def test_document_strings_reach_read_tools(name, document):
    execute = AsyncMock(return_value={"success": True, "data": {}})
    with patch.object(server, "_execute", execute):
        asyncio.run(server.mcp.call_tool(name, {"document": document}))
    execute.assert_awaited_once()
    # MCP pre-parses the JSON literal null for optional arguments.
    expected = None if isinstance(document, str) and document.strip() == "null" else document
    assert execute.await_args.args[2] == expected


@pytest.mark.parametrize("name", ["revit_document_info", "revit_model_health"])
@BOUNDED
@given(
    st.booleans()
    | st.integers()
    | st.lists(TEXT, max_size=4)
    | st.dictionaries(TEXT, TEXT, max_size=4)
)
def test_invalid_documents_do_not_reach_read_tools(name, document):
    execute = AsyncMock()
    with patch.object(server, "_execute", execute), pytest.raises(ToolError):
        asyncio.run(server.mcp.call_tool(name, {"document": document}))
    execute.assert_not_awaited()


@pytest.mark.parametrize("name", ["revit_select", "revit_show", "revit_move"])
@BOUNDED
@given(st.lists(SCALAR, max_size=8))
@example([True])
@example([0])
@example([2**63])
def test_invalid_ids_do_not_reach_action_tools(name, ids):
    valid = all(type(item) is int and 0 < item < 2**63 for item in ids)
    if valid and (ids or name == "revit_select"):
        return
    arguments = {"element_ids": ids}
    if name == "revit_move":
        arguments.update(dx_mm=1, dy_mm=0)
    host = AsyncMock()
    with patch.object(server, "host", host), pytest.raises(ToolError):
        asyncio.run(server.mcp.call_tool(name, arguments))
    host.list_revit_instances.assert_not_awaited()


def test_json_rule_file_preserves_non_bmp_text(tmp_path):
    value = {
        "schema": 1,
        "name": "Test",
        "rules": [
            {"id": "\U00010000", "title": "Test", "severity": "info", "check": {"type": "count"}}
        ],
    }
    path = tmp_path / "rules.json"
    path.write_text(json.dumps(value), encoding="utf-8")
    actual = load_rules(str(path))
    expected = load_rules(inline=value)
    assert actual["rules"] == expected["rules"]
    assert actual["sha256"] == expected["sha256"]


@pytest.mark.parametrize("references", ["{2001-01-01: source}", "{1: source, a: source}"])
def test_yaml_rule_file_rejects_non_json_reference_keys(tmp_path, references):
    path = tmp_path / "rules.yaml"
    path.write_text(
        f"schema: 1\nname: Test\nrules: []\nreferences: {references}\n", encoding="utf-8"
    )
    with pytest.raises(ValueError, match="JSON-compatible"):
        load_rules(str(path))


@pytest.mark.parametrize("name", ["revit_select", "revit_show", "revit_move"])
@BOUNDED
@given(st.lists(st.integers(min_value=1, max_value=2**63 - 1), min_size=1, max_size=8))
@example([1, 2**63 - 1, 1])
def test_valid_ids_reach_action_tools(name, ids):
    host = AsyncMock()
    host.list_revit_instances.return_value = [{"processId": 42}]
    execute = AsyncMock(return_value={"success": True, "data": {}})
    arguments = {"element_ids": ids}
    if name == "revit_move":
        arguments.update(dx_mm=1, dy_mm=0)
    with patch.object(server, "host", host), patch.object(server.channel, "execute", execute):
        asyncio.run(server.mcp.call_tool(name, arguments))
    execute.assert_awaited_once()
    assert execute.await_args.args[0].payload["elementIds"] == ids
