import asyncio
from unittest.mock import AsyncMock, patch

import pytest

from revit_model_mcp import server
from revit_model_mcp.audit.evaluate import Evaluator
from revit_model_mcp.revit_channel import Job, RevitChannelError


def test_catalog_paging_and_validation():
    assert Job.list_catalog("ownership", limit=20, offset=40).payload["offset"] == 40
    for arguments in ({"offset": -1}, {"limit": 0}, {"limit": 2001}, {"name_contains": "x"}):
        with pytest.raises(RevitChannelError):
            Job.list_catalog("ownership", **arguments)


def test_catalog_preserves_refusal_counts_and_skipped():
    responses = [
        {"success": False, "error": "Workset and ownership reads require a workshared document."},
        {
            "success": True,
            "data": {
                "items": [{"name": "Structure", "count": 15, "owner": "Owner", "isOpen": True}],
                "total": 2,
                "hasMore": True,
                "nextOffset": 1,
            },
            "skipped": [{"what": "element 5 ownership", "reason": "Unavailable"}],
            "skippedCount": 1,
        },
    ]
    for response in responses:
        execute = AsyncMock(return_value=response)
        with patch.object(server, "_execute", execute):
            actual = asyncio.run(server.revit_list_catalog("worksets", offset=1))
        assert actual == response
        assert execute.call_args.args[0].payload["offset"] == 1


@pytest.mark.parametrize("skipped,expected", [(False, "passed"), (True, "partial")])
def test_workset_audit_pages_counts_and_incomplete_evidence(skipped, expected):
    source = AsyncMock()

    async def read(tool, arguments):
        assert tool == "revit_list_catalog"
        offset = arguments["offset"]
        return {
            "success": True,
            "data": {
                "isWorkshared": True,
                "items": [{"isOpen": True}] * (2000 if offset == 0 else 1),
                "total": 2001,
                "hasMore": offset == 0,
            },
            "skipped": [{"what": "counts", "reason": "Limited"}] if skipped else [],
            "skippedCount": int(skipped),
        }

    source.read.side_effect = read
    result = asyncio.run(
        Evaluator(source).evaluate(
            [
                {
                    "id": "closed",
                    "title": "Closed",
                    "severity": "major",
                    "check": {"type": "worksets", "metric": "closed"},
                }
            ]
        )
    )[0]
    assert result["status"] == expected
    assert result["measured"] == 0
    assert result["population"] == 2001
    assert source.read.await_count == 2


@pytest.mark.parametrize(
    "response",
    [
        {"success": False, "error": "Requires a workshared document."},
        {"success": True, "data": {"items": [], "total": 0, "hasMore": False}},
        {"success": True, "data": {"isWorkshared": False, "items": [], "total": 0}},
    ],
)
def test_workset_audit_refusal_or_missing_marker_is_not_assessed(response):
    source = AsyncMock()
    source.read.return_value = response
    result = asyncio.run(
        Evaluator(source).evaluate(
            [
                {
                    "id": "closed",
                    "title": "Closed",
                    "severity": "major",
                    "check": {"type": "worksets", "metric": "closed"},
                }
            ]
        )
    )[0]
    assert result["status"] == "not_assessed"
