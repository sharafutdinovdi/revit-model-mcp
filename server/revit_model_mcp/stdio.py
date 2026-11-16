"""Validate bounded raw frames before the SDK's JSON decoder can drop them."""

import json
import sys
from contextlib import ExitStack

import anyio
from mcp import types
from mcp.server import MCPServer as SdkServer
from mcp.server.stdio import (
    _claim_fd,
    _open_stdin_diversion,
    _open_stdout_diversion,
    _UnownedTextWrapper,
    stdio_server,
)

MAX_FRAME_SIZE = 4 * 1024 * 1024
MAX_FRAME_DEPTH = 64


def _scan_frame(line):
    depth = 0
    deepest = 0
    request_id = None
    index = 0
    decoder = json.JSONDecoder()
    while index < len(line):
        char = line[index]
        if char == '"':
            start = index
            index += 1
            while index < len(line):
                if line[index] == "\\":
                    index += 2
                    continue
                if line[index] == '"':
                    break
                index += 1
            if depth == 1 and line[start : index + 1] == '"id"':
                tail = line[index + 1 :].lstrip()
                if tail.startswith(":"):
                    value = tail[1:].lstrip()
                    if value and (value[0] == '"' or value[0] in "-0123456789"):
                        try:
                            candidate, _ = decoder.raw_decode(value)
                            if isinstance(candidate, (str, int)) and not isinstance(
                                candidate, bool
                            ):
                                request_id = candidate
                        except ValueError:
                            pass
        elif char in "{[":
            depth += 1
            deepest = max(deepest, depth)
        elif char in "}]":
            depth -= 1
        index += 1
    return request_id, deepest


class _Output:
    def __init__(self, stream):
        self.stream = stream
        self.lock = anyio.Lock()

    async def write(self, value):
        async with self.lock:
            await self.stream.write(value)
            await self.stream.flush()

    async def flush(self):
        async with self.lock:
            await self.stream.flush()


class _Input:
    def __init__(self, stream, output):
        self.stream = stream
        self.output = output

    async def __aiter__(self):
        while line := await anyio.to_thread.run_sync(
            self.stream.wrapped.readline, MAX_FRAME_SIZE + 1
        ):
            oversized = len(line) > MAX_FRAME_SIZE
            if oversized and not line.endswith("\n"):
                while chunk := await anyio.to_thread.run_sync(
                    self.stream.wrapped.readline, MAX_FRAME_SIZE + 1
                ):
                    if chunk.endswith("\n"):
                        break
            request_id, depth = _scan_frame(line)
            code = -32600
            message = None
            if oversized or len(line.encode("utf-8")) > MAX_FRAME_SIZE:
                message = f"Frame size limit is {MAX_FRAME_SIZE} bytes."
            elif depth > MAX_FRAME_DEPTH:
                message = f"JSON nesting limit is {MAX_FRAME_DEPTH} levels."
            else:
                try:
                    json.loads(line)
                except (ValueError, RecursionError):
                    code, message = -32700, "Parse error."
                if message is None:
                    try:
                        types.jsonrpc_message_adapter.validate_json(line, by_name=False)
                    except ValueError:
                        message = "Invalid JSON-RPC request."
            if message is None:
                yield line
            else:
                await self.output.write(
                    json.dumps(
                        {
                            "jsonrpc": "2.0",
                            "id": request_id,
                            "error": {"code": code, "message": message},
                        }
                    )
                    + "\n"
                )


class MCPServer(SdkServer):
    async def run_stdio_async(self):
        # Retain the SDK's descriptor diversion so stray prints cannot corrupt frames.
        with ExitStack() as cleanup:
            stdin, restore = _claim_fd(0, sys.stdin, "rb", _open_stdin_diversion)
            if restore is not None:
                cleanup.callback(restore)
            stdout, restore = _claim_fd(1, sys.stdout, "wb", _open_stdout_diversion)
            if restore is not None:
                cleanup.callback(restore)
            reader = anyio.wrap_file(_UnownedTextWrapper(stdin, encoding="utf-8", errors="replace"))
            writer = _Output(anyio.wrap_file(_UnownedTextWrapper(stdout, encoding="utf-8")))
            async with stdio_server(stdin=_Input(reader, writer), stdout=writer) as (read, write):
                await self._lowlevel_server.run(
                    read, write, self._lowlevel_server.create_initialization_options()
                )
