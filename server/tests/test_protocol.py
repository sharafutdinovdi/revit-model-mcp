import json

import anyio
import pytest
from mcp import types
from mcp.client._memory import InMemoryTransport
from mcp.server.mcpserver import Context, MCPServer
from mcp.shared.message import SessionMessage

from revit_model_mcp import server as revit_server


async def exchange(streams, request, expected_error=None):
    read, write = streams
    await write.send(SessionMessage(types.JSONRPCRequest.model_validate(request)))
    with anyio.fail_after(10):
        response = await read.receive()
    result = response.message.model_dump(by_alias=True, exclude_none=True)
    assert result["id"] == request["id"]
    if expected_error is not None:
        assert result["error"]["code"] == expected_error, result
        return None
    assert "error" not in result, result
    return result["result"]


@pytest.mark.anyio
@pytest.mark.parametrize(
    ("offered", "negotiated"),
    [
        ("2024-11-05", "2024-11-05"),
        ("2025-03-26", "2025-03-26"),
        ("2025-06-18", "2025-06-18"),
        ("2025-11-25", "2025-11-25"),
        ("2026-07-28", "2025-11-25"),
        ("unknown", "2025-11-25"),
    ],
)
async def test_initialize_negotiates_handshake_versions(offered, negotiated):
    async with InMemoryTransport(revit_server.mcp) as streams:
        result = await exchange(
            streams,
            {
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": offered,
                    "capabilities": {},
                    "clientInfo": {"name": "protocol-test", "version": "1"},
                },
            },
        )
    assert result["protocolVersion"] == negotiated


@pytest.mark.anyio
async def test_modern_envelope_identity_is_request_scoped_in_session_params():
    server = MCPServer("identity-test")

    @server.tool()
    async def identity(ctx: Context) -> dict:
        params = ctx.session.client_params
        info = params.client_info if params is not None else None
        return {
            "protocolVersion": ctx.protocol_version,
            "name": info.name if info else None,
            "version": info.version if info else None,
        }

    async with InMemoryTransport(server) as streams:
        for request_id, info in enumerate(
            [
                {"name": "first-client", "version": "1.2.3"},
                {"name": "second-client", "version": "4.5.6"},
                None,
                {"name": "invalid-without-version"},
            ],
            start=1,
        ):
            meta = {
                "io.modelcontextprotocol/protocolVersion": "2026-07-28",
                "io.modelcontextprotocol/clientCapabilities": {},
            }
            if info is not None:
                meta["io.modelcontextprotocol/clientInfo"] = info
            invalid = info is not None and "version" not in info
            result = await exchange(
                streams,
                {
                    "jsonrpc": "2.0",
                    "id": request_id,
                    "method": "tools/call",
                    "params": {"name": "identity", "arguments": {}, "_meta": meta},
                },
                expected_error=-32602 if invalid else None,
            )
            if invalid:
                continue
            valid_info = info or {}
            assert not result.get("isError"), result
            assert json.loads(result["content"][0]["text"]) == {
                "protocolVersion": "2026-07-28",
                "name": valid_info.get("name"),
                "version": valid_info.get("version"),
            }
