"""Generate tool metadata for the desktop and Smithery bundles."""

import argparse
import asyncio
import json
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from revit_model_mcp.server import mcp

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
MANIFEST_PATH = REPOSITORY_ROOT / "bundle" / "manifest.json"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--smithery", type=Path, help="Output Smithery archive")
    parser.add_argument(
        "--from", dest="source", type=Path, help="Desktop bundle archive or directory"
    )
    args = parser.parse_args()
    if (args.smithery is None) != (args.source is None):
        parser.error("--smithery and --from must be used together")

    registry = asyncio.run(mcp.list_tools())
    desktop_tools = [
        {"name": tool.name, "description": tool.description} for tool in registry
    ]
    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    manifest["tools"] = desktop_tools
    MANIFEST_PATH.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )
    print(f"Wrote {len(desktop_tools)} tools to {MANIFEST_PATH}")

    if args.smithery is not None:
        smithery_tools = [
            {**tool, "inputSchema": registry[index].input_schema}
            for index, tool in enumerate(desktop_tools)
        ]
        with ZipFile(args.smithery, "w", ZIP_DEFLATED) as output:
            if args.source.is_dir():
                files = (
                    (path.relative_to(args.source).as_posix(), path.read_bytes())
                    for path in sorted(args.source.rglob("*"))
                    if path.is_file()
                )
                write_archive(output, files, smithery_tools)
            else:
                with ZipFile(args.source) as source:
                    files = (
                        (name, source.read(name))
                        for name in source.namelist()
                        if not name.endswith("/")
                    )
                    write_archive(output, files, smithery_tools)
        print(f"Wrote {len(smithery_tools)} tools to {args.smithery}")


def write_archive(output: ZipFile, files, tools: list[dict]) -> None:
    found_manifest = False
    for name, contents in files:
        if name == "manifest.json":
            manifest = json.loads(contents)
            manifest["tools"] = tools
            contents = (
                json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
            ).encode("utf-8")
            found_manifest = True
        output.writestr(name, contents)
    if not found_manifest:
        raise ValueError("Source bundle has no root manifest.json")


if __name__ == "__main__":
    main()
