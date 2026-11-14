import asyncio
import errno
import json
import os
from pathlib import Path
from unittest.mock import AsyncMock, patch

import pytest
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp import health_workbook, issue_register, server, snapshot_report
from revit_model_mcp.audit import report, rules

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
    assert str(caught.value) == "Cannot save report: report.xlsx"


@pytest.mark.parametrize("redact", [False, True])
@pytest.mark.parametrize(
    ("model_path", "basename"),
    [
        (r"C:\Projects\Client X\Tower.rvt", "Tower.rvt"),
        (r"\\fileserver\Projects\Client X\Tower.rvt", "Tower.rvt"),
        ("/models/Client X/Tower.rvt", "/models/Client X/Tower.rvt"),
    ],
)
def test_offline_batch_preserves_model_error_redaction(
    tmp_path, monkeypatch, redact, model_path, basename
):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    directory = tmp_path / "Projects" / "offline"
    directory.mkdir(parents=True)
    message = f"Could not open {model_path}"
    result = asyncio.run(
        server.revit_model_audit(
            rules_inline=RULES,
            snapshots_dir=str(directory),
            batch_models=[{"status": "failed", "error": message, "localPath": None}],
            save=False,
        )
    )
    expected = f"Could not open {basename}" if redact else message
    assert result["models"][0]["reason"] == expected
    assert result["skipped"][0]["reason"] == expected


@pytest.mark.parametrize("redact", [False, True])
def test_unknown_home_does_not_replace_original_file_error(tmp_path, monkeypatch, redact):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    source = tmp_path / "none.json"
    with pytest.raises(ToolError) as caught:
        server.revit_audit_report(str(source), "~revit_model_mcp_missing_user_4a1b/r")
    assert isinstance(caught.value.__cause__, FileNotFoundError)
    assert "No such file or directory" in str(caught.value)
    assert "none.json" in str(caught.value)
    assert (str(tmp_path) in str(caught.value)) is not redact


@pytest.mark.parametrize("redact", [False, True])
def test_relative_argument_does_not_damage_ordinary_text(tmp_path, monkeypatch, redact):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    monkeypatch.chdir(tmp_path)
    message = "Cannot save report: about/ and about/out/r stay unchanged."
    with patch("revit_model_mcp.audit.report.build_report", side_effect=ValueError(message)):
        with pytest.raises(ToolError) as caught:
            server.revit_audit_report("audit.json", "out/r")
    assert str(caught.value) == message


def test_fallback_retains_wrapper_and_os_error_context(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    parent = tmp_path / "sensitive parent 秘密"
    parent.mkdir()
    cause = PermissionError(errno.EACCES, "Permission denied", str(tmp_path / ".report.xlsx.tmp"))
    error = ValueError("Cannot save report: sensitive parent 秘密/extra/.report.xlsx.tmp")
    error.__cause__ = cause
    with patch("revit_model_mcp.issue_register.write_register", side_effect=error):
        with pytest.raises(ToolError) as caught:
            asyncio.run(server.revit_issue_register(str(parent / "report.xlsx"), {}, ISSUES))
    assert str(caught.value) == "Cannot save report: Permission denied: report.xlsx"


@pytest.mark.parametrize("redact", [False, True])
@pytest.mark.parametrize("tool", ["audit", "rules", "register", "health", "snapshot"])
@pytest.mark.parametrize("style", ["drive", "unc"])
@pytest.mark.parametrize("as_bytes", [False, True])
@pytest.mark.parametrize("second_filename", [False, True])
def test_os_errors_use_fields_not_repr(
    tmp_path, monkeypatch, redact, tool, style, as_bytes, second_filename
):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    filename = (
        r"C:\Projects\Client X 秘密\.report.xlsx.tmp"
        if style == "drive"
        else r"\\server\share\Client X 秘密\.report.xlsx.tmp"
    )
    other = r"\\other\archive\Private 秘密\report.xlsx" if second_filename else None
    if as_bytes:
        filename = os.fsencode(filename)
        other = os.fsencode(other) if other is not None else None
    failure = PermissionError(errno.EACCES, "Permission denied", filename, None, other)
    operations = {
        "audit": (report.Path, "read_text"),
        "rules": (rules.Path, "read_text"),
        "register": (issue_register, "write_new_file"),
        "health": (health_workbook, "write_new_file"),
        "snapshot": (snapshot_report, "write_new_file"),
    }
    module, operation = operations[tool]
    target = str(tmp_path / "report.xlsx")
    execute = AsyncMock(
        side_effect=[
            {"success": True, "data": {"counts": {}}},
            {"success": True, "data": {"groups": []}},
        ]
    )
    with (
        patch.object(module, operation, side_effect=failure),
        patch.object(server, "_execute", execute),
    ):
        with pytest.raises(ToolError) as caught:
            if tool == "audit":
                server.revit_audit_report(str(tmp_path / "audit.json"), target)
            elif tool == "rules":
                asyncio.run(
                    server.revit_model_audit(rules=str(tmp_path / "rules.yaml"), save=False)
                )
            elif tool == "register":
                asyncio.run(server.revit_issue_register(target, {}, ISSUES))
            elif tool == "health":
                asyncio.run(server.revit_model_health(output_path=target))
            else:
                server.revit_build_report(str(CURRENT), target)
    message = str(caught.value)
    if redact:
        detail = "Permission denied: .report.xlsx.tmp"
        if second_filename:
            detail += " -> report.xlsx"
        expected = detail if tool in ("audit", "rules") else "Cannot save report: " + detail
        assert message == expected
        assert "Client X" not in message and "Private" not in message
        assert "秘密" not in message
        assert "[Errno" not in message
    else:
        if tool in ("audit", "rules"):
            expected = str(failure)
        elif tool == "snapshot":
            expected = f"Cannot save report: Permission denied: {filename}"
        else:
            expected = f"Cannot save report: {failure}"
        assert message == expected


@pytest.mark.parametrize("redact", [False, True])
def test_derived_relative_report_name_is_displayed_at_source(tmp_path, monkeypatch, redact):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    monkeypatch.chdir(tmp_path)
    Path("audit.json").write_text(json.dumps(AUDIT))
    Path("out").mkdir()
    Path("out/r.xlsx").touch()
    with pytest.raises(ToolError) as caught:
        server.revit_audit_report("audit.json", "out/r", "xlsx")
    expected = "r.xlsx" if redact else str(tmp_path / "out" / "r.xlsx")
    assert str(caught.value) == f"Local file already exists: {expected}"


@pytest.mark.parametrize("redact", [False, True])
def test_known_repr_escaped_path_is_redacted(tmp_path, monkeypatch, redact):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", str(int(redact)))
    path = r"\\server\share\Client X 秘密\a.json"
    message = f"Local file already exists: {path!r}"
    with patch.object(report, "build_report", side_effect=ValueError(message)):
        with pytest.raises(ToolError) as caught:
            server.revit_audit_report(path, str(tmp_path / "report"))
    if redact:
        assert str(caught.value) == "Local file already exists: 'a.json'"
        assert "Client X" not in str(caught.value)
        assert "秘密" not in str(caught.value)
    else:
        assert str(caught.value) == message


def test_fallback_does_not_choose_between_unrelated_files(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    parent = tmp_path / "sensitive parent 秘密"
    parent.mkdir()
    message = "Cannot save report: sensitive parent 秘密/extra/file.tmp"
    with patch.object(report, "build_report", side_effect=ValueError(message)):
        with pytest.raises(ToolError) as caught:
            server.revit_audit_report(str(parent / "audit.json"), str(parent / "report.xlsx"))
    assert str(caught.value) == "Cannot save report: local file path withheld"
    assert "sensitive parent" not in str(caught.value)
    assert "秘密" not in str(caught.value)


def test_fallback_uses_longest_full_path_from_original_error(tmp_path, monkeypatch):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    parent = tmp_path / "sensitive parent 秘密"
    parent.mkdir()
    target = parent / "r.xlsx"
    message = f"Cannot save report: {target}; sensitive parent 秘密/extra/file.tmp"
    with patch.object(report, "build_report", side_effect=ValueError(message)):
        with pytest.raises(ToolError) as caught:
            server.revit_audit_report(str(parent / "audit.json"), str(target))
    assert str(caught.value) == "Cannot save report: r.xlsx"
    assert "sensitive parent" not in str(caught.value)
    assert "秘密" not in str(caught.value)
