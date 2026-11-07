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
