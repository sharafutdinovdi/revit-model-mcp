import asyncio
from unittest.mock import AsyncMock

import pytest
from mcp.server import MCPServer

from revit_model_mcp.actions import register_actions


@pytest.mark.parametrize("transaction", ["none", "auto", "group"])
@pytest.mark.parametrize("success", [True, False])
def test_code_preview_reports_transaction_override(transaction, success):
    server = MCPServer("code-preview-test")
    response = {
        "success": success,
        "warnings": ["Existing warning"],
        "partial": True,
        "skipped": ["Existing diagnostic"],
        "skippedCount": 1,
    }
    execute = AsyncMock(return_value=response)
    host = AsyncMock()
    host.list_revit_instances.return_value = [{"processId": 42}]
    register_actions(server, execute, lambda: host)

    result = asyncio.run(
        server.call_tool(
            "revit_execute_code",
            {"code": "return 42;", "transaction": transaction, "dry_run": True},
        )
    ).structured_content

    assert execute.await_args.args[0].payload["transaction"] == (
        "auto" if transaction == "none" else transaction
    )
    assert execute.await_args.args[0].payload["dryRun"] is True
    assert result["warnings"] == ["Existing warning"] + (
        ["dry_run forced transaction=auto"] if transaction == "none" else []
    )
    assert result["success"] is success
    assert result["partial"] is True
    assert result["skipped"] == ["Existing diagnostic"]
    assert result["skippedCount"] == 1
