import base64
import json
from pathlib import Path
from unittest.mock import AsyncMock, patch

import pytest

from revit_model_mcp import server
from revit_model_mcp.revit_channel import Job

pytestmark = pytest.mark.anyio


@pytest.mark.parametrize("command", ["execute-code", "run-tool"])
async def test_truncated_execution_returns_full_artifact_and_partial_receipt(command):
    full = {"items": list(range(6000)), "nested": [[[[[[[[7]]]]]]]]}
    response = {
        "partial": True,
        "skipped": [{"what": "returnValue", "reason": "Inline result truncated: itemBudget"}],
        "skippedCount": 1,
        "success": True,
        "data": {
            "returnValue": {"items": list(range(5000))},
            "resultCompleteness": {
                "truncated": True,
                "retainedCount": 5000,
                "omittedCount": 1010,
                "reasons": ["itemBudget", "depth"],
            },
            "resultArtifact": base64.b64encode(json.dumps(full).encode()).decode(),
        },
    }
    with patch.object(server.channel, "execute", AsyncMock(return_value=response)):
        result = await server._execute(Job(command, {}), 120, None)
    assert result["partial"] is True
    assert result["skippedCount"] == 1
    assert result["skipped"][0]["reasons"] == ["itemBudget", "depth"]
    artifact = result["data"]["resultArtifact"]
    assert artifact["complete"] is True
    target = Path(artifact["path"])
    try:
        assert json.loads(target.read_text()) == full
    finally:
        target.unlink()
        target.parent.rmdir()


@pytest.mark.parametrize("failure", ["write", "missing", "invalid"])
async def test_artifact_failure_never_returns_complete_result(failure):
    response = {
        "success": True,
        "data": {
            "resultCompleteness": {"truncated": True, "reasons": ["inlineSize"]},
        },
    }
    if failure != "missing":
        response["data"]["resultArtifact"] = (
            base64.b64encode(b"[1,2,3]").decode() if failure == "write" else "invalid!"
        )
    with (
        patch.object(server.channel, "execute", AsyncMock(return_value=response)),
        patch(
            "revit_model_mcp.artifact_download.write_new_file",
            side_effect=OSError("Cannot write artifact"),
        ),
    ):
        result = await server._execute(Job("execute-code", {}), 120, None)
    assert result["partial"] is True
    assert result["skippedCount"] == 2
    assert result["data"]["resultArtifact"]["complete"] is False
    assert "path" not in result["data"]["resultArtifact"]


async def test_complete_execution_does_not_create_artifact():
    response = {
        "success": True,
        "data": {"returnValue": 7, "resultCompleteness": {"truncated": False}},
    }
    with patch.object(server.channel, "execute", AsyncMock(return_value=response)):
        result = await server._execute(Job("execute-code", {}), 120, None)
    assert not result.get("partial")
    assert result["skipped"] == []
    assert "resultArtifact" not in result["data"]


async def test_full_artifact_applies_configured_path_redaction(monkeypatch):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "1")
    response = {
        "success": True,
        "data": {
            "resultCompleteness": {"truncated": True, "reasons": ["inlineSize"]},
            "resultArtifact": base64.b64encode(
                json.dumps({"path": r"C:\Private\Building.rvt"}).encode()
            ).decode(),
        },
    }
    with patch.object(server.channel, "execute", AsyncMock(return_value=response)):
        result = await server._execute(Job("run-tool", {}), 120, None)
    target = Path(result["data"]["resultArtifact"]["path"])
    try:
        assert json.loads(target.read_text()) == {"path": "Building.rvt"}
    finally:
        target.unlink()
        target.parent.rmdir()


@pytest.mark.parametrize("command", ["execute-code", "run-tool"])
async def test_log_truncation_preserves_complete_return_value(command):
    response = {
        "success": True,
        "partial": True,
        "skipped": [
            {
                "what": "log",
                "reason": "Script log truncated: 1 lines omitted after the first 2000.",
            }
        ],
        "skippedCount": 1,
        "data": {
            "returnValue": 1,
            "log": [f"line {index}" for index in range(2000)],
            "logOmittedCount": 1,
            "resultCompleteness": {
                "truncated": False,
                "retainedCount": 1,
                "omittedCount": 0,
                "reasons": [],
            },
        },
    }
    expected = json.loads(json.dumps(response))
    with patch.object(server.channel, "execute", AsyncMock(return_value=response)):
        result = await server._execute(Job(command, {}), 120, None)
    assert result == expected
    assert "resultArtifact" not in result["data"]
    assert result["skippedCount"] == 1
    assert [item["what"] for item in result["skipped"]] == ["log"]


@pytest.mark.parametrize("command", ["execute-code", "run-tool"])
async def test_log_and_value_truncation_keep_value_artifact_counts(command):
    full = list(range(6000))
    completeness = {
        "truncated": True,
        "retainedCount": 5000,
        "omittedCount": 1000,
        "reasons": ["itemBudget"],
    }
    log_receipt = {
        "what": "log",
        "reason": "Script log truncated: 1 lines omitted after the first 2000.",
    }
    response = {
        "success": True,
        "partial": True,
        "skipped": [
            {"what": "returnValue", "reason": "Inline result truncated: itemBudget"},
            log_receipt.copy(),
        ],
        "skippedCount": 2,
        "data": {
            "returnValue": full[:5000],
            "log": [f"line {index}" for index in range(2000)],
            "logOmittedCount": 1,
            "resultCompleteness": completeness.copy(),
            "resultArtifact": base64.b64encode(json.dumps(full).encode()).decode(),
        },
    }
    with patch.object(server.channel, "execute", AsyncMock(return_value=response)):
        result = await server._execute(Job(command, {}), 120, None)
    artifact = result["data"]["resultArtifact"]
    target = Path(artifact["path"])
    try:
        assert artifact["complete"] is True
        assert json.loads(target.read_text()) == full
        assert result["data"]["resultCompleteness"] == completeness
        assert result["data"]["logOmittedCount"] == 1
        assert result["partial"] is True
        assert result["skippedCount"] == 2
        assert [item["what"] for item in result["skipped"]] == ["returnValue", "log"]
        receipt = result["skipped"][0]
        assert receipt["omittedCount"] == 1000
        assert receipt["retainedCount"] == 5000
        assert receipt["reasons"] == ["itemBudget"]
        assert result["skipped"][1] == log_receipt
    finally:
        target.unlink()
        target.parent.rmdir()
