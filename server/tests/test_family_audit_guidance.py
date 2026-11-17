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


@pytest.mark.parametrize("stopped_at_read_boundary", [False, True])
def test_family_budget_guidance_survives_capped_diagnostics(stopped_at_read_boundary):
    skipped = [
        {
            "what": f"Family {index}",
            "reason": "The audit is limited to the first 200 editable families.",
        }
        for index in range(201, 442)
    ]
    response = {
        "success": False,
        "partial": True,
        "data": {
            "mode": "project",
            "stoppedAtReadBoundary": stopped_at_read_boundary,
            "families": [{"name": f"Audited {index}", "status": "audited"} for index in range(52)],
            "skipped": skipped,
            "skippedCount": 241,
        },
        "skipped": [
            *skipped,
            {"what": "unrelated", "reason": "Unavailable."},
            *(
                [{"what": "family-audit remaining families", "reason": "Read budget reached."}]
                if stopped_at_read_boundary
                else []
            ),
        ],
        "skippedCount": 243 if stopped_at_read_boundary else 242,
    }
    with patch.object(server.channel, "execute", AsyncMock(return_value=response)):
        result = asyncio.run(server.revit_family_audit(families=["*"]))
    assert len(result["skipped"]) == 100
    assert result["skippedCount"] == (243 if stopped_at_read_boundary else 242)
    assert result["data"]["skipped"] == skipped
    assert result["partial"] is True
    if stopped_at_read_boundary:
        assert "52 families audited" in result["message"]
        assert "read time budget or cancellation boundary" in result["message"]
        assert "families=" in result["message"]
        assert "data.skipped" in result["message"]
    else:
        assert "message" not in result
