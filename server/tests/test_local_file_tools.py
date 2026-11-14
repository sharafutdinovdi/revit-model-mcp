import asyncio
import json
from unittest.mock import AsyncMock, patch

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp import server


def audit_result():
    return {
        "schema": 1,
        "model": {"title": "Demo"},
        "evaluatedAtUtc": "2026-01-01T00:00:00Z",
        "rules": {},
        "summary": {},
        "findings": [],
        "skipped": [],
        "skippedCount": 0,
    }


@pytest.mark.parametrize("redact", [False, True])
@pytest.mark.parametrize("tool", ["audit", "register", "health", "rules"])
def test_local_file_errors(tmp_path, monkeypatch, redact, tool):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    target = tmp_path / "private file.xlsx"
    target.touch()
    with pytest.raises(ToolError) as caught:
        if tool == "audit":
            server.revit_audit_report(str(tmp_path / "missing.json"), str(target))
        elif tool == "register":
            asyncio.run(server.revit_issue_register(str(target), {}, []))
        elif tool == "health":
            asyncio.run(server.revit_model_health(output_path=str(target)))
        else:
            asyncio.run(server.revit_model_audit(rules=str(tmp_path / "missing.yaml")))
    message = str(caught.value)
    assert (str(tmp_path) in message) is not redact
    assert ("missing" if tool in ("audit", "rules") else target.name) in message


@pytest.mark.parametrize("redact", [False, True])
@pytest.mark.parametrize("tool", ["audit", "register", "health"])
def test_local_file_success_paths(tmp_path, monkeypatch, redact, tool):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    target = tmp_path / "report.xlsx"
    if tool == "audit":
        source = tmp_path / "audit.json"
        source.write_text(json.dumps(audit_result()))
        result = server.revit_audit_report(str(source), str(target), "xlsx")
        paths = result["files"]
    elif tool == "register":
        result = asyncio.run(
            server.revit_issue_register(
                str(target),
                {},
                [
                    {
                        "title": "Review",
                        "category": "QA",
                        "finding": "Check",
                        "severity": "info",
                        "snapshot": "none",
                    }
                ],
            )
        )
        paths = [result["path"]]
    else:
        execute = AsyncMock(
            side_effect=[
                {"success": True, "data": {"counts": {}}},
                {"success": True, "data": {"groups": []}},
            ]
        )
        with patch.object(server, "_execute", execute):
            result = asyncio.run(server.revit_model_health(output_path=str(target)))
        paths = [result["workbook"]["path"]]
    assert paths == [target.name if redact else str(target)]
    assert target.is_file()


@pytest.mark.parametrize("redact", [False, True])
def test_snapshot_existing_resolved_parent(tmp_path, monkeypatch, redact):
    from pathlib import Path

    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    parent = tmp_path / "private" / "tmp"
    parent.mkdir(parents=True)
    alias = tmp_path / "tmp"
    alias.symlink_to(parent, target_is_directory=True)
    (parent / "report.xlsx").touch()
    snapshots = Path(__file__).parent / "fixtures" / "snapshots" / "current"
    with pytest.raises(ToolError) as caught:
        server.revit_build_report(str(snapshots), str(alias / "report.xlsx"))
    expected = "report.xlsx" if redact else str(parent.resolve() / "report.xlsx")
    assert str(caught.value) == f"Local file already exists: {expected}"


@pytest.mark.parametrize(
    ("content", "message"),
    [
        ("[]", "root must be an object"),
        ('{"schema": true, "findings": []}', "schema 1"),
        ('{"schema": 1, "findings": []}', "missing field: model"),
        ('{"schema": 1, "findings": [false]}', "invalid field types"),
        ("[" * 10000 + "0" + "]" * 10000, "nested too deeply"),
        ("{", "Invalid audit JSON"),
    ],
)
def test_invalid_audit_json(tmp_path, content, message):
    source = tmp_path / "invalid.json"
    source.write_text(content)
    with pytest.raises(ToolError, match=message):
        server.revit_audit_report(str(source), str(tmp_path / "report"))
    assert sorted(path.name for path in tmp_path.iterdir()) == ["invalid.json"]


@pytest.mark.parametrize("format", ["json", "yaml"])
def test_deep_rule_files(tmp_path, format):
    source = tmp_path / f"rules.{format}"
    source.write_text("[" * 10000 + ("0" if format == "json" else "value") + "]" * 10000)
    with pytest.raises(ToolError, match="Rule or audit history data is nested too deeply"):
        asyncio.run(server.revit_model_audit(rules=str(source), save=False))


def test_boolean_rule_schema_rejected():
    with pytest.raises(ToolError, match="not of type 'integer'"):
        asyncio.run(server.revit_model_audit(
            rules_inline={"schema": True, "name": "Invalid", "rules": []}, save=False
        ))
