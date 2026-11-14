import asyncio
import errno
import json
from pathlib import Path
from unittest.mock import AsyncMock, patch

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp import server

CURRENT = Path(__file__).parent / "fixtures" / "snapshots" / "current"
RULES = {"schema": 1, "name": "Empty", "rules": []}
AUDIT = {
    "schema": 1,
    "model": {"title": "Tower"},
    "evaluatedAtUtc": "2026-01-01T00:00:00Z",
    "rules": {},
    "summary": {},
    "findings": [],
    "skipped": [],
    "skippedCount": 0,
}
ISSUES = [
    {
        "title": "Review",
        "category": "QA",
        "finding": "Check",
        "severity": "info",
        "snapshot": "none",
    }
]


@pytest.mark.parametrize("redact", [False, True])
@pytest.mark.parametrize("tool", ["audit", "register", "model_audit", "health", "snapshot"])
@pytest.mark.parametrize(
    "variant", ["readonly", "spaces", "unicode", "relative", "tilde", "symlink"]
)
def test_permission_errors_redact_known_paths(tmp_path, monkeypatch, redact, tool, variant):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    name = {
        "spaces": "sensitive parent",
        "unicode": "秘密",
        "symlink": "sensitive parent 秘密",
    }.get(variant, "sensitive_parent")
    parent = tmp_path / name
    parent.mkdir()
    (parent / "audit.json").write_text(json.dumps(AUDIT))
    (parent / "rules.json").write_text(json.dumps(RULES))
    given = str(parent)
    if variant == "relative":
        monkeypatch.chdir(tmp_path)
        given = name
    elif variant == "tilde":
        monkeypatch.setenv("HOME", str(tmp_path))
        given = "~/" + name
    elif variant == "symlink":
        alias = tmp_path / "sensitive alias"
        alias.symlink_to(parent, target_is_directory=True)
        given = str(alias)
    target = given + "/report.xlsx"
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", given + "/history")
    channel = AsyncMock()
    channel.execute.return_value = {
        "success": True,
        "data": {
            "passport": {"title": "Tower", "centralPath": "Tower.rvt"},
            "source": {"path": "Tower.rvt"},
        },
        "responder": {"processId": 123, "documentName": "Tower", "documentPath": "Tower.rvt"},
    }
    execute = AsyncMock(
        side_effect=[
            {"success": True, "data": {"counts": {}}},
            {"success": True, "data": {"groups": []}},
        ]
    )
    parent.chmod(0o555)
    try:
        with patch.object(server, "channel", channel), patch.object(server, "_execute", execute):
            with pytest.raises(ToolError) as caught:
                if tool == "audit":
                    server.revit_audit_report(given + "/audit.json", target)
                elif tool == "register":
                    asyncio.run(server.revit_issue_register(target, {}, ISSUES))
                elif tool == "model_audit":
                    asyncio.run(
                        server.revit_model_audit(rules=given + "/rules.json", compare_with="none")
                    )
                elif tool == "health":
                    asyncio.run(server.revit_model_health(output_path=target))
                else:
                    server.revit_build_report(str(CURRENT), target)
    finally:
        parent.chmod(0o755)
    message = str(caught.value)
    assert "Permission denied" in message
    if redact:
        assert name not in message
        assert "sensitive alias" not in message
        assert str(tmp_path) not in message
        assert "report.xlsx" in message or tool == "model_audit"
    else:
        assert message == str(caught.value.__cause__)
        assert name in message or "sensitive alias" in message


@pytest.mark.parametrize("redact", [False, True])
def test_audit_sibling_conflict_with_private_parent(tmp_path, monkeypatch, redact):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    parent = tmp_path / "Client Projects" / "Acme Tower 秘密"
    parent.mkdir(parents=True)
    source = parent / "audit.json"
    source.write_text(json.dumps(AUDIT))
    (parent / "report.html").touch()
    with pytest.raises(ToolError) as caught:
        server.revit_audit_report(str(source), str(parent / "report.xlsx"), "both")
    expected = "report.html" if redact else str(parent / "report.html")
    assert str(caught.value) == f"Local file already exists: {expected}"


@pytest.mark.parametrize("directory", [".", ".."])
def test_dot_directory_does_not_remove_punctuation(tmp_path, monkeypatch, directory):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    current = tmp_path / "empty"
    current.mkdir()
    monkeypatch.chdir(current)
    with pytest.raises(ToolError) as caught:
        server.revit_build_report(directory, "report.xlsx")
    assert str(caught.value) == f"Snapshot directory contains no JSON files: {directory}"


@pytest.mark.parametrize("redact", [False, True])
def test_audit_json_error_preserves_location(tmp_path, monkeypatch, redact):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    source = tmp_path / "broken.json"
    source.write_text('{\n  "schema": !\n}')
    with pytest.raises(ToolError) as caught:
        server.revit_audit_report(str(source), str(tmp_path / "report"))
    assert str(caught.value) == (
        "Invalid audit JSON in broken.json: Expecting value (line 2, column 13)."
    )


def test_audit_type_error_keeps_details(tmp_path):
    with patch(
        "revit_model_mcp.audit.report.build_report",
        side_effect=TypeError("invalid measured value"),
    ):
        with pytest.raises(ToolError, match="invalid measured value"):
            server.revit_audit_report("audit.json", str(tmp_path / "report"))


@pytest.mark.parametrize("redact", [False, True])
@pytest.mark.parametrize("chain", ["cause", "context"])
def test_register_redacts_both_os_filenames(tmp_path, monkeypatch, redact, chain):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    source = str(tmp_path / "sensitive parent 秘密" / ".report.xlsx.tmp")
    destination = str(tmp_path / "other secret folder" / "report.xlsx")
    failure = PermissionError(errno.EACCES, "Permission denied", source, None, destination)
    wrapped = ValueError(f"Cannot save report: {failure}")
    if chain == "cause":
        wrapped.__cause__ = failure
    else:
        wrapped.__context__ = failure
    with patch("revit_model_mcp.issue_register.write_register", side_effect=wrapped):
        with pytest.raises(ToolError) as caught:
            asyncio.run(server.revit_issue_register(str(tmp_path / "report.xlsx"), {}, ISSUES))
    message = str(caught.value)
    if redact:
        assert "sensitive parent" not in message
        assert "other secret folder" not in message
        assert str(tmp_path) not in message
        assert ".report.xlsx.tmp" in message and "report.xlsx" in message
    else:
        assert message == str(wrapped)


@pytest.mark.parametrize("redact", [False, True])
def test_offline_batch_redacts_history_failure(tmp_path, monkeypatch, redact):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    history = tmp_path / "sensitive parent 秘密" / "history"
    monkeypatch.setenv("REVIT_MCP_AUDIT_DIR", str(history))
    failure = PermissionError(errno.EACCES, "Permission denied", str(history / "private.json"))
    with patch("revit_model_mcp.audit.result.write_new_file", side_effect=failure):
        result = asyncio.run(
            server.revit_model_audit(
                snapshots_dir=str(CURRENT), rules_inline=RULES, compare_with="none"
            )
        )
    assert result["models"]
    for model in result["models"]:
        assert model["status"] == "failed"
        if redact:
            assert "sensitive parent" not in model["reason"]
            assert str(tmp_path) not in model["reason"]
            assert "private.json" in model["reason"]
        else:
            assert model["reason"] == str(failure)
    if redact:
        assert "sensitive parent" not in json.dumps(result["skipped"])


def test_register_falls_back_for_remaining_folder_fragment(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    parent = tmp_path / "sensitive parent 秘密"
    parent.mkdir()
    error = ValueError("Cannot save report: sensitive parent 秘密/extra/.report.xlsx.tmp")
    with patch("revit_model_mcp.issue_register.write_register", side_effect=error):
        with pytest.raises(ToolError) as caught:
            asyncio.run(server.revit_issue_register(str(parent / "report.xlsx"), {}, ISSUES))
    assert str(caught.value) == "Local file operation failed: report.xlsx"
