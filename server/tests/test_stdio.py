import asyncio
import json
import os
import sys

import pytest

from revit_model_mcp.stdio import MAX_FRAME_SIZE


@pytest.mark.parametrize(
    ("frame", "request_id", "code", "message"),
    [
        ('{"jsonrpc":"2.0","id":7,"method":42}', 7, -32600, "Invalid"),
        ('{"jsonrpc":"2.0","id":8,}', 8, -32700, "Parse"),
        (
            '{"jsonrpc":"2.0","method":"tools/call","params":{"name":"revit_ping",'
            '"arguments":' + '{"a":' * 5000 + "0" + "}" * 5000 + '},"id":9}',
            9,
            -32600,
            "nesting limit",
        ),
        ('{"id":10,"payload":"' + "x" * MAX_FRAME_SIZE + '"}', 10, -32600, "size limit"),
        ("not-json", None, -32700, "Parse"),
    ],
)
def test_bad_stdio_frame_replies_and_keeps_connection(frame, request_id, code, message):
    async def check():
        env = {**os.environ, "REVIT_MCP_CHECK_UPDATES": "0"}
        process = await asyncio.create_subprocess_exec(
            sys.executable,
            "-m",
            "revit_model_mcp.server",
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.DEVNULL,
            env=env,
        )
        try:

            async def send(value):
                process.stdin.write((value + "\n").encode())
                await process.stdin.drain()
                return json.loads(await asyncio.wait_for(process.stdout.readline(), 10))

            initialized = await send(
                json.dumps(
                    {
                        "jsonrpc": "2.0",
                        "id": 1,
                        "method": "initialize",
                        "params": {
                            "protocolVersion": "2025-11-25",
                            "capabilities": {},
                            "clientInfo": {"name": "test", "version": "1"},
                        },
                    }
                )
            )
            assert "result" in initialized
            process.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
            await process.stdin.drain()
            response = await send(frame)
            assert response["id"] == request_id
            assert response["error"]["code"] == code
            assert message in response["error"]["message"]
            following = await send('{"jsonrpc":"2.0","id":11,"method":"ping"}')
            assert following == {"jsonrpc": "2.0", "id": 11, "result": {}}
        finally:
            process.terminate()
            await process.wait()

    asyncio.run(check())
