"""Measure the size of the tools/list response, per toolset and in total."""

import argparse
import asyncio
import json

from revit_model_mcp.server import mcp
from revit_model_mcp.toolsets import TOOLSETS, select_tools


def wire_size(tool) -> int:
    payload = tool.model_dump(mode="json", by_alias=True, exclude_none=True)
    return len(
        json.dumps(payload, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--json", action="store_true", help="Print machine-readable output."
    )
    args = parser.parse_args()
    sizes = {tool.name: wire_size(tool) for tool in asyncio.run(mcp.list_tools())}
    rows = []
    for name, members in TOOLSETS.items():
        total = sum(sizes[tool] for tool in members)
        rows.append(
            {
                "toolset": name,
                "tools": len(members),
                "bytes": total,
                "tokens": total // 4,
            }
        )
    total = sum(sizes.values())
    rows.append(
        {"toolset": "all", "tools": len(sizes), "bytes": total, "tokens": total // 4}
    )
    if args.json:
        print(json.dumps(rows, indent=2))
        return
    print(f"{'toolset':<14}{'tools':>6}{'bytes':>9}{'~tokens':>9}")
    for row in rows:
        print(
            f"{row['toolset']:<14}{row['tools']:>6}{row['bytes']:>9}{row['tokens']:>9}"
        )
    core_and_read = select_tools("core,read")
    subtotal = sum(sizes[name] for name in core_and_read)
    print(f"{'core,read':<14}{len(core_and_read):>6}{subtotal:>9}{subtotal // 4:>9}")


if __name__ == "__main__":
    main()
