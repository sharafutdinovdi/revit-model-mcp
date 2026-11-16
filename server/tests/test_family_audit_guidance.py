import asyncio
from unittest.mock import AsyncMock, patch

import pytest

from revit_model_mcp import server


@pytest.mark.parametrize("families", [["Door", "Window"], ["*"]])
def test_partial_family_audit_explains_budget_and_continuation(families):
    response = {
        "success": False,
        "partial": True,
        "message": "Family audit stopped before completion, reached its read budget, or was cancelled; results are partial.",
        "data": {
            "mode": "project",
            "families": [{"name": "Door", "status": "audited"}],
            "skipped": [],
            "skippedCount": 0,
        },
        "skipped": [
            {
                "what": "family-audit remaining families",
                "reason": "Inspection stopped at the read budget or cancellation boundary.",
            }
        ],
        "skippedCount": 1,
    }
    with patch.object(server, "_execute", AsyncMock(return_value=response)):
        result = asyncio.run(server.revit_family_audit(families=families))
    assert result["partial"] is True
    assert result["success"] is False
    assert result["data"] == response["data"]
    assert result["skipped"] == response["skipped"]
    assert result["skippedCount"] == 1
    assert "time budget" in result["message"]
    assert "families=" in result["message"]
    if families == ["*"]:
        assert "1 families audited" in result["message"]
        assert "total" in result["message"] and "unavailable" in result["message"]
    else:
        assert "1 of 2" in result["message"]
        assert 'families=["Window"]' in result["message"]


def test_wildcard_budget_guidance_points_to_names_past_family_limit():
    skipped = [
        {"what": "Window", "reason": "The audit is limited to the first 200 editable families."}
    ]
    response = {
        "success": False,
        "partial": True,
        "data": {
            "mode": "project",
            "families": [{"name": "Door", "status": "audited"}],
            "skipped": skipped,
            "skippedCount": 1,
        },
        "skipped": [
            *skipped,
            {
                "what": "family-audit remaining families",
                "reason": "Inspection stopped at the read budget or cancellation boundary.",
            },
        ],
        "skippedCount": 2,
    }
    with patch.object(server, "_execute", AsyncMock(return_value=response)):
        result = asyncio.run(server.revit_family_audit(families=["*"]))
    assert result["data"] == response["data"]
    assert result["skipped"] == response["skipped"]
    assert result["skippedCount"] == 2
    assert "data.skipped" in result["message"]
    assert "past the 200-family limit" in result["message"]
    assert "remaining family names are unavailable" not in result["message"]
    assert "families=" in result["message"]
