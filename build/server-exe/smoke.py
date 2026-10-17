"""Smoke-test the bundled server: --version, then MCP initialize and tools/list over stdio."""

from __future__ import annotations

import argparse
import json
import os
import queue
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import NoReturn

RESPONSE_TIMEOUT = 120
EXIT_GRACE = 10

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_EXE = (
    ROOT
    / "output"
    / "server-exe"
    / "revit-model-mcp"
    / ("revit-model-mcp.exe" if sys.platform == "win32" else "revit-model-mcp")
)


def expected_tool_count() -> int:
    manifest = json.loads(
        (ROOT / "docs" / "tool-manifest.json").read_text(encoding="utf-8")
    )
    return len(manifest["tools"])


def _pump_stdout(
    process: subprocess.Popen[str], lines: queue.Queue[str | None]
) -> None:
    assert process.stdout is not None
    for line in process.stdout:
        lines.put(line)
    lines.put(None)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", nargs="?", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--tools", type=int, default=None, help="expected tool count")
    parser.add_argument(
        "--command",
        action="append",
        default=None,
        metavar="ARG",
        help="one server argument; repeat to launch this command instead of exe (local testing)",
    )
    args = parser.parse_args()
    expected = args.tools if args.tools is not None else expected_tool_count()
    command = args.command if args.command is not None else [str(args.exe)]

    version = subprocess.run(
        [*command, "--version"],
        capture_output=True,
        text=True,
        timeout=120,
        check=True,
    ).stdout.strip()
    print(f"--version: {version}")
    if not version.startswith("revit-model-mcp ") or version.endswith("+unknown"):
        raise SystemExit(f"Unexpected --version output: {version!r}")

    initialize = {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "smoke", "version": "0"},
        },
    }
    initialized = {"jsonrpc": "2.0", "method": "notifications/initialized"}
    tools_list = {"jsonrpc": "2.0", "id": 2, "method": "tools/list"}
    environment = {
        **os.environ,
        "REVIT_MCP_HOST": "local",
        "REVIT_MCP_NO_UPDATE_CHECK": "1",
    }
    initialize_started = time.perf_counter()
    process = subprocess.Popen(
        command,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        env=environment,
    )
    stdout_lines: queue.Queue[str | None] = queue.Queue()
    stderr_chunks: list[str] = []
    threading.Thread(
        target=_pump_stdout, args=(process, stdout_lines), daemon=True
    ).start()
    threading.Thread(
        target=lambda: stderr_chunks.extend(process.stderr), daemon=True
    ).start()
    seen: list[str] = []

    def fail(what: str) -> NoReturn:
        process.kill()
        raise SystemExit(
            f"{what}: {''.join(seen)!r} {''.join(stderr_chunks)[-2000:]!r}"
        )

    def send(message: dict) -> None:
        assert process.stdin is not None
        try:
            process.stdin.write(json.dumps(message) + "\n")
            process.stdin.flush()
        except OSError:
            pass  # server already gone; the wait below reports it

    def wait_response(message_id: int, what: str) -> dict:
        deadline = time.monotonic() + RESPONSE_TIMEOUT
        while True:
            try:
                line = stdout_lines.get(timeout=max(0.0, deadline - time.monotonic()))
            except queue.Empty:
                fail(f"{what} timed out")
            if line is None:
                fail(f"{what} failed: server closed stdout")
            seen.append(line)
            if not line.strip():
                continue
            message = json.loads(line)
            if message.get("id") == message_id:
                if "result" not in message:
                    fail(f"{what} failed")
                return message

    send(initialize)
    server = wait_response(1, "initialize")["result"]["serverInfo"]
    print(
        f"initialize: {server['name']} {server.get('version')} "
        f"({time.perf_counter() - initialize_started:.3f} s)"
    )
    send(initialized)
    tools_started = time.perf_counter()
    send(tools_list)
    count = len(wait_response(2, "tools/list")["result"]["tools"])
    print(f"tools/list: {count} tools ({time.perf_counter() - tools_started:.3f} s)")

    assert process.stdin is not None
    process.stdin.close()
    try:
        process.wait(timeout=EXIT_GRACE)
    except subprocess.TimeoutExpired:
        process.terminate()
        try:
            process.wait(timeout=EXIT_GRACE)
        except subprocess.TimeoutExpired:
            process.kill()
    if count != expected:
        raise SystemExit(f"Expected {expected} tools, got {count}")
    print("Smoke test passed")


if __name__ == "__main__":
    main()
