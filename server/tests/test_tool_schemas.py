from __future__ import annotations

import asyncio
import json
import os
from pathlib import Path

from revit_model_mcp import server as revit_server

GOLDEN = Path(__file__).parent / "golden" / "tools.schema.json"
UPDATE_VARIABLE = "UPDATE_TOOL_SCHEMAS"


def _normalize(schema):
    """Keep names, types, defaults, enums and required; drop prose."""
    if isinstance(schema, list):
        return [_normalize(item) for item in schema]
    if not isinstance(schema, dict):
        return schema
    normalized = {}
    for key, value in schema.items():
        if key in {"description", "title"} and isinstance(value, str):
            continue
        if key in {"properties", "$defs"} and isinstance(value, dict):
            normalized[key] = {name: _normalize(item) for name, item in value.items()}
        elif key == "required" and isinstance(value, list):
            normalized[key] = sorted(value)
        else:
            normalized[key] = _normalize(value)
    return normalized


def _render() -> str:
    tools = asyncio.run(revit_server.mcp.list_tools())
    schemas = {tool.name: _normalize(tool.input_schema) for tool in tools}
    return json.dumps(schemas, indent=2, sort_keys=True, ensure_ascii=False) + "\n"


def test_tool_schemas_match_the_committed_snapshot():
    rendered = _render()
    if os.environ.get(UPDATE_VARIABLE) == "1":
        GOLDEN.parent.mkdir(parents=True, exist_ok=True)
        GOLDEN.write_text(rendered, encoding="utf-8", newline="\n")
    expected = GOLDEN.read_text(encoding="utf-8")
    current = json.loads(rendered)
    committed = json.loads(expected)
    assert sorted(current) == sorted(committed), (
        "The set of tools changed. If this is deliberate, run "
        f"`cd server && {UPDATE_VARIABLE}=1 uv run --with pytest pytest tests/test_tool_schemas.py` "
        "and commit the updated tests/golden/tools.schema.json."
    )
    changed = [name for name in committed if current[name] != committed[name]]
    assert not changed, (
        f"The input schema changed for: {', '.join(changed)}. If this is deliberate, run "
        f"`cd server && {UPDATE_VARIABLE}=1 uv run --with pytest pytest tests/test_tool_schemas.py` "
        "and commit the updated tests/golden/tools.schema.json."
    )
    assert rendered == expected


def test_snapshot_covers_names_types_defaults_and_required():
    committed = json.loads(GOLDEN.read_text(encoding="utf-8"))
    assert len(committed) >= 70
    for name, schema in committed.items():
        assert name.startswith("revit_")
        assert schema.get("type") == "object"
        assert "description" not in schema
        for parameter in schema.get("properties", {}).values():
            assert "description" not in parameter
    wait = committed["revit_jobs"]["properties"]["wait_seconds"]
    assert wait["default"] == 40
    assert wait["maximum"] == 50
    assert committed["revit_move"]["required"] == sorted(committed["revit_move"]["required"])
