"""Validate bounded raw frames before the SDK's JSON decoder can drop them."""

import asyncio
import inspect
import json
import sys
from contextlib import ExitStack
from threading import Thread

import anyio
from mcp import types
from mcp.server import MCPServer as SdkServer
from mcp.server import stdio as sdk_stdio

MAX_FRAME_SIZE = 4 * 1024 * 1024
MAX_FRAME_DEPTH = 64
EOF_DRAIN_SECONDS = 1.0


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
        self.pending = set()
        self.drained = anyio.Event()
        self.drained.set()

    def expect(self, request_id):
        if not self.pending:
            self.drained = anyio.Event()
        self.pending.add(request_id)

    async def drain(self):
        # The SDK cancels handlers on EOF; only already forwarded requests get this grace period.
        with anyio.move_on_after(EOF_DRAIN_SECONDS):
            await self.drained.wait()

    async def write(self, value):
        async with self.lock:
            await self.stream.write(value)
            await self.stream.flush()
            frame = json.loads(value)
            if "id" in frame and ("result" in frame or "error" in frame):
                self.pending.discard(frame["id"])
                if not self.pending:
                    self.drained.set()

    async def flush(self):
        async with self.lock:
            await self.stream.flush()


class _Input:
    def __init__(self, stream, output):
        self.stream = stream
        self.output = output

    async def _readline(self):
        loop = asyncio.get_running_loop()
        result = loop.create_future()

        def complete(line, error):
            if not result.done():
                if error is None:
                    result.set_result(line)
                else:
                    result.set_exception(error)

        def read():
            try:
                line, error = self.stream.wrapped.readline(MAX_FRAME_SIZE + 1), None
            except Exception as failure:
                line, error = None, failure
            try:
                loop.call_soon_threadsafe(complete, line, error)
            except RuntimeError:
                # Cancellation can close the loop before the input peer disconnects.
                return

        # An idle pipe read cannot be interrupted portably; it must not own process shutdown.
        Thread(target=read, daemon=True, name="MCP stdin reader").start()
        return await result

    async def __aiter__(self):
        while line := await self._readline():
            oversized = len(line) > MAX_FRAME_SIZE
            if oversized and not line.endswith("\n"):
                while chunk := await self._readline():
                    if chunk.endswith("\n"):
                        break
            if not oversized and not line.strip():
                continue
            request_id, depth = _scan_frame(line)
            code = -32600
            message = None
            if oversized or len(line.encode("utf-8")) > MAX_FRAME_SIZE:
                message = f"Frame size limit is {MAX_FRAME_SIZE} bytes."
            elif depth > MAX_FRAME_DEPTH:
                message = f"JSON nesting limit is {MAX_FRAME_DEPTH} levels."
            else:
                try:
                    frame = json.loads(line)
                except (ValueError, RecursionError):
                    code, message = -32700, "Parse error."
                if message is None:
                    try:
                        types.jsonrpc_message_adapter.validate_json(line, by_name=False)
                    except ValueError:
                        message = "Invalid JSON-RPC request."
            if message is None:
                if "method" in frame and "id" in frame:
                    self.output.expect(frame["id"])
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
        await self.output.drain()


class MCPServer(SdkServer):
    async def run_stdio_async(self):
        # Retain the SDK's descriptor diversion so stray prints cannot corrupt frames.
        with ExitStack() as cleanup:
            try:
                claim = sdk_stdio._claim_fd
                stdin_diversion = sdk_stdio._open_stdin_diversion
                stdout_diversion = sdk_stdio._open_stdout_diversion
                wrapper = sdk_stdio._UnownedTextWrapper
                inspect.signature(claim).bind(0, sys.stdin, "rb", stdin_diversion)
                inspect.signature(stdin_diversion).bind()
                inspect.signature(stdout_diversion).bind()
                inspect.signature(wrapper).bind(sys.stdin.buffer, encoding="utf-8")
                stdin, restore = claim(0, sys.stdin, "rb", stdin_diversion)
                if restore is not None:
                    cleanup.callback(restore)
                stdout, restore = claim(1, sys.stdout, "wb", stdout_diversion)
                if restore is not None:
                    cleanup.callback(restore)
                reader = anyio.wrap_file(wrapper(stdin, encoding="utf-8", errors="replace"))
                writer = _Output(anyio.wrap_file(wrapper(stdout, encoding="utf-8")))
            except Exception:
                cleanup.close()
                print(
                    "revit-model-mcp: Warning: guarded stdio is unavailable; using SDK stdio.",
                    file=sys.stderr,
                )
                await super().run_stdio_async()
                return
            async with sdk_stdio.stdio_server(stdin=_Input(reader, writer), stdout=writer) as (
                read,
                write,
            ):
                await self._lowlevel_server.run(
                    read, write, self._lowlevel_server.create_initialization_options()
                )
