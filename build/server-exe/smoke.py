"""Smoke-test the bundled server: --version, then MCP initialize and tools/list over stdio."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

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


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("exe", nargs="?", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--tools", type=int, default=None, help="expected tool count")
    args = parser.parse_args()
    expected = args.tools if args.tools is not None else expected_tool_count()

    version = subprocess.run(
        [str(args.exe), "--version"],
        capture_output=True,
        text=True,
        timeout=120,
        check=True,
    ).stdout.strip()
    print(f"--version: {version}")
    if not version.startswith("revit-model-mcp ") or version.endswith("+unknown"):
        raise SystemExit(f"Unexpected --version output: {version!r}")

    messages = [
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2025-06-18",
                "capabilities": {},
                "clientInfo": {"name": "smoke", "version": "0"},
            },
        },
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
    ]
    environment = {
        **os.environ,
        "REVIT_MCP_HOST": "local",
        "REVIT_MCP_NO_UPDATE_CHECK": "1",
    }
    process = subprocess.run(
        [str(args.exe)],
        input="".join(json.dumps(message) + "\n" for message in messages),
        capture_output=True,
        text=True,
        timeout=120,
        env=environment,
        check=False,
    )
    responses = {}
    for line in process.stdout.splitlines():
        if line.strip():
            message = json.loads(line)
            responses[message.get("id")] = message
    if 1 not in responses or "result" not in responses[1]:
        raise SystemExit(
            f"initialize failed: {process.stdout!r} {process.stderr[-2000:]!r}"
        )
    server = responses[1]["result"]["serverInfo"]
    print(f"initialize: {server['name']} {server.get('version')}")
    if 2 not in responses or "result" not in responses[2]:
        raise SystemExit(
            f"tools/list failed: {process.stdout!r} {process.stderr[-2000:]!r}"
        )
    count = len(responses[2]["result"]["tools"])
    print(f"tools/list: {count} tools")
    if count != expected:
        raise SystemExit(f"Expected {expected} tools, got {count}")
    print("Smoke test passed")


if __name__ == "__main__":
    main()
