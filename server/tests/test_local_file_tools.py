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
    if redact:
        assert tmp_path.name not in message
        assert tmp_path.parent.name not in message
    else:
        assert any(form in message for form in (str(tmp_path), repr(str(tmp_path))[1:-1]))
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
    try:
        alias.symlink_to(parent, target_is_directory=True)
    except OSError:
        pytest.skip("Directory symlinks are unavailable on this filesystem")
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
    with pytest.raises(ToolError, match="Rule .*nested too deeply"):
        asyncio.run(server.revit_model_audit(rules=str(source), save=False))


def test_boolean_rule_schema_rejected():
    with pytest.raises(ToolError, match="1 was expected"):
        asyncio.run(
            server.revit_model_audit(
                rules_inline={"schema": True, "name": "Invalid", "rules": []}, save=False
            )
        )


@pytest.mark.parametrize("failed_command", ["model-health", "list-warnings"])
def test_health_workbook_preserves_transport_failure(tmp_path, failed_command):
    from revit_model_mcp.errors import unavailable

    failure = unavailable(failed_command, "Connection refused")
    responses = (
        [failure]
        if failed_command == "model-health"
        else [{"success": True, "data": {"counts": {}}}, failure]
    )
    execute = AsyncMock(side_effect=responses)
    target = tmp_path / "health.xlsx"
    with patch.object(server, "_execute", execute):
        result = asyncio.run(server.revit_model_health(output_path=str(target)))
    if failed_command == "model-health":
        assert result == failure
    else:
        assert result["data"] == {"counts": {}}
        assert result["partial"] is True
        assert result["skippedCount"] == 1
        assert result["skipped"] == [
            {"what": "list-warnings", "reason": "Connection refused", "response": failure}
        ]
    assert execute.await_count == len(responses)
    assert not target.exists()


@pytest.mark.parametrize("redact", [False, True])
def test_audit_existing_output_redaction(tmp_path, monkeypatch, redact):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    source = tmp_path / "audit.json"
    source.write_text(json.dumps(audit_result()))
    target = tmp_path / "report.xlsx"
    target.touch()
    with pytest.raises(ToolError) as caught:
        server.revit_audit_report(str(source), str(target), "xlsx")
    expected = target.name if redact else str(target)
    assert str(caught.value) == f"Local file already exists: {expected}"


@pytest.mark.parametrize("redact", [False, True])
@pytest.mark.parametrize("operation", ["compare", "save"])
def test_audit_history_errors_redacted(tmp_path, monkeypatch, redact, operation):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(tmp_path / "history"))
    snapshot = {
        "schemaVersion": 1,
        "passport": {"title": "Tower", "centralPath": "Tower.rvt", "versionGuid": "family-a"},
        "source": {"path": "Tower.rvt"},
    }
    channel = AsyncMock()
    channel.execute.return_value = {
        "success": True,
        "data": snapshot,
        "responder": {"processId": 123, "documentName": "Tower", "documentPath": "Tower.rvt"},
    }
    baseline = tmp_path / "private baseline.json"
    if operation == "save":
        (tmp_path / "history").touch()
    with patch.object(server, "channel", channel), pytest.raises(ToolError) as caught:
        asyncio.run(
            server.revit_model_audit(
                rules_inline={"schema": 1, "name": "Empty", "rules": []},
                compare_with=str(baseline) if operation == "compare" else "none",
                save=operation == "save",
            )
        )
    assert isinstance(caught.value.__cause__, OSError)
    message = str(caught.value)
    if redact:
        assert tmp_path.name not in message
        assert tmp_path.parent.name not in message
    else:
        assert any(form in message for form in (str(tmp_path), repr(str(tmp_path))[1:-1]))


def test_health_preserves_data_when_warnings_raise(tmp_path):
    health = {
        "success": True,
        "data": {"counts": {"elements": 12}},
        "skipped": [{"what": "fileSize", "reason": "unsaved"}],
        "skippedCount": 3,
    }
    with patch.object(
        server, "_execute", AsyncMock(side_effect=[health, ToolError("Warning read failed")])
    ):
        result = asyncio.run(server.revit_model_health(output_path=str(tmp_path / "health.xlsx")))
    assert result["data"] == health["data"]
    assert result["partial"] is True
    assert result["skippedCount"] == 4
    assert result["skipped"][0] == health["skipped"][0]
    assert result["skipped"][1]["reason"] == "Warning read failed"
