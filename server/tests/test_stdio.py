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
    ids=["invalid-request", "invalid-json", "deep-arguments", "oversized-frame", "non-json"],
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
                await asyncio.wait_for(process.stdin.drain(), 10)
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
            await asyncio.wait_for(process.stdin.drain(), 10)
            process.stdin.write(b" \t\r\n\n")
            response = await send(frame)
            assert response["id"] == request_id
            assert response["error"]["code"] == code
            assert message in response["error"]["message"]
            following = await send('{"jsonrpc":"2.0","id":11,"method":"ping"}')
            assert following == {"jsonrpc": "2.0", "id": 11, "result": {}}
        finally:
            if process.returncode is None:
                process.kill()
            await asyncio.wait_for(process.communicate(), 5)

    asyncio.run(asyncio.wait_for(check(), 45))


@pytest.mark.parametrize("failure", ["missing", "signature", "startup"])
def test_sdk_private_api_changes_fall_back_to_public_stdio(failure):
    async def check():
        source = """
import sys
from types import SimpleNamespace
import mcp.server
import mcp.server.stdio as original
shim = SimpleNamespace(stdio_server=original.stdio_server)
if sys.argv[1] != "missing":
    for name in ("_claim_fd", "_open_stdin_diversion", "_open_stdout_diversion", "_UnownedTextWrapper"):
        setattr(shim, name, getattr(original, name))
    if sys.argv[1] == "signature":
        shim._claim_fd = lambda: None
    else:
        def fail_claim(*args):
            raise RuntimeError("Simulated SDK setup failure")
        shim._claim_fd = fail_claim
mcp.server.stdio = shim
from revit_model_mcp import server
sys.argv = [sys.argv[0]]
server.main()
"""
        process = await asyncio.create_subprocess_exec(
            sys.executable,
            "-c",
            source,
            failure,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env={**os.environ, "REVIT_MCP_CHECK_UPDATES": "0"},
        )
        try:
            for request_id in (1, 2):
                process.stdin.write(
                    (
                        json.dumps({"jsonrpc": "2.0", "id": request_id, "method": "ping"}) + "\n"
                    ).encode()
                )
                await asyncio.wait_for(process.stdin.drain(), 10)
                result = json.loads(await asyncio.wait_for(process.stdout.readline(), 10))
                assert result == {"jsonrpc": "2.0", "id": request_id, "result": {}}
        finally:
            if process.returncode is None:
                process.kill()
            _, stderr = await asyncio.wait_for(process.communicate(), 5)
        stderr = stderr.decode()
        assert stderr.count("Warning: guarded stdio is unavailable; using SDK stdio.") == 1

    asyncio.run(asyncio.wait_for(check(), 45))


@pytest.mark.parametrize("shutdown", ["eof", "cancel"])
def test_stdio_exits_with_idle_input_on_shutdown(shutdown):
    async def check():
        source = """
import asyncio
import sys
from revit_model_mcp import server
async def main():
    try:
        await asyncio.wait_for(server.mcp.run_stdio_async(), 0.1)
    except TimeoutError:
        print("cancelled", file=sys.stderr)
asyncio.run(main())
"""
        arguments = ["-c", source] if shutdown == "cancel" else ["-m", "revit_model_mcp.server"]
        process = await asyncio.create_subprocess_exec(
            sys.executable,
            *arguments,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env={**os.environ, "REVIT_MCP_CHECK_UPDATES": "0"},
        )
        try:
            if shutdown == "eof":
                process.stdin.close()
            assert await asyncio.wait_for(process.wait(), 5) == 0
            if shutdown == "cancel":
                assert b"cancelled" in await asyncio.wait_for(process.stderr.read(), 1)
        finally:
            if process.returncode is None:
                process.kill()
            await asyncio.wait_for(process.communicate(), 5)

    asyncio.run(asyncio.wait_for(check(), 15))


def test_stdio_exits_when_output_pipe_is_closed():
    import subprocess

    process = subprocess.Popen(
        [sys.executable, "-m", "revit_model_mcp.server"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        env={**os.environ, "REVIT_MCP_CHECK_UPDATES": "0"},
    )
    try:
        process.stdout.close()
        process.stdin.write(b'{"jsonrpc":"2.0","id":1,"method":"ping"}\n')
        process.stdin.flush()
        assert process.wait(timeout=5) != 0
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=5)
        process.stdin.close()
