"""Check a store bundle or publish folder against its source and live tool registry."""

from __future__ import annotations

import argparse
import asyncio
import json
import re
import subprocess
import sys
import tempfile
import types
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FILE_WRITERS = {
    "revit_capture_elements",
    "revit_export_view",
    "revit_model_health",
    "revit_issue_register",
    "revit_batch_fetch",
    "revit_build_report",
    "revit_model_audit",
    "revit_audit_report",
    "revit_batch_start",
    "revit_batch_cancel",
}
FORBIDDEN = (
    b"api.github.com",
    b"github.com/sharafutdinovdi/revit-model-mcp/releases",
    b"pypi.org",
)


def store_source(text: str, store: bool = True) -> str:
    """Select channel branches while retaining unrelated preprocessor branches."""
    enabled = [True]
    result = []
    for line in text.splitlines():
        directive = line.strip()
        if directive.startswith("#if "):
            condition = directive[4:].strip()
            enabled.append(
                enabled[-1]
                and (
                    {"STORE_CHANNEL": store, "!STORE_CHANNEL": not store}.get(
                        condition, True
                    )
                )
            )
        elif directive == "#else":
            enabled[-1] = enabled[-2] and not enabled[-1]
        elif directive == "#endif":
            enabled.pop()
        elif enabled[-1]:
            result.append(line)
    return "\n".join(result)


def check_sources(root: Path) -> list[str]:
    errors = []
    addin = root / "src/RevitModelMcp.Addin"
    if not addin.is_dir():
        return ["Add-in source directory is missing"]
    for path in addin.rglob("*.cs"):
        text = path.read_text(encoding="utf-8")
        if re.search(r"\bTaskDialog\b", text):
            errors.append(
                f"Native TaskDialog requires an owned app dialog: {path.name}"
            )
        for match in re.finditer(
            r"class\s+(\w+)\s*:\s*(?:System\.Windows\.)?Window\b", text
        ):
            body = text[match.end() :]
            if not re.search(r"\bIcon\s*=.*(?:Resources/Icons/|AppIcon)", body):
                errors.append(f"Window without app Icon: {path.name}:{match[1]}")
        if re.search(r'CreatePanel\(\s*"MCP"\s*\)', text):
            errors.append(f"Generic MCP panel: {path.name}")
        if re.search(r"new\s+(?:System\.Windows\.)?Window\s*\(", text):
            errors.append(f"Anonymous Window requires an icon audit: {path.name}")
    for path in addin.rglob("*.xaml"):
        text = path.read_text(encoding="utf-8")
        if re.search(r"<Window\b", text) and not re.search(
            r'\bIcon="[^"]*Resources/Icons/', text
        ):
            code = path.with_suffix(path.suffix + ".cs")
            if not code.exists() or not re.search(
                r"\bIcon\s*=", code.read_text(encoding="utf-8")
            ):
                errors.append(f"XAML Window without app Icon: {path.name}")
    settings = addin / "Control/HttpChannel.cs"
    if settings.exists() and 'DataMember(Name = "updateCheck"' in store_source(
        settings.read_text(encoding="utf-8")
    ):
        errors.append("Store settings default contains updateCheck")
    return errors


def check_manifest(manifest: dict, registry: list[dict]) -> list[str]:
    errors = []
    tools = manifest.get("tools", [])
    names = [tool["name"] for tool in tools]
    live = {tool["name"]: tool for tool in registry}
    if not names or len(names) != len(set(names)) or set(names) != set(live):
        errors.append("Manifest tools differ from live registry")
    for tool in tools:
        name = tool["name"]
        annotations = tool.get("annotations", {})
        if any(
            not isinstance(annotations.get(key), bool)
            for key in ("readOnlyHint", "destructiveHint", "idempotentHint")
        ):
            errors.append(f"Missing annotations: {name}")
        if name in FILE_WRITERS and annotations.get("readOnlyHint") is True:
            errors.append(f"File writer labelled read-only: {name}")
        if name in live:
            for key in ("description", "inputSchema"):
                if tool.get(key) != live[name].get(key):
                    errors.append(f"Manifest {key} differs from registry: {name}")
            for key, value in annotations.items():
                if value != live[name].get("annotations", {}).get(key):
                    errors.append(
                        f"Manifest annotation differs from registry: {name}:{key}"
                    )
    transports = manifest.get("server", {}).get("transports", {})
    if transports.get("default") != ["stdio", "named pipe"] or not all(
        any(transport in value for value in transports.get("optional", []))
        for transport in ("HTTP", "SSH")
    ):
        errors.append("Missing transport declarations")
    return errors


def check_bytes(data: bytes, name: str) -> list[str]:
    errors = []
    lowered = data.lower()
    for value in FORBIDDEN:
        if value in lowered or value.decode().encode("utf-16-le") in lowered:
            errors.append(f"Forbidden string {value.decode()} in {name}")
    return errors


def code_bytes(code: types.CodeType) -> bytes:
    values = []
    for value in code.co_consts:
        if isinstance(value, types.CodeType):
            values.append(code_bytes(value))
        elif isinstance(value, str):
            values.append(value.encode("utf-8"))
        elif isinstance(value, bytes):
            values.append(value)
    return b"\n".join(values)


def check_archive(path: Path) -> list[str]:
    # PyInstaller compresses Python code. Raw binary strings cannot inspect the PYZ.
    from PyInstaller.archive.readers import CArchiveReader

    errors = []
    archive = CArchiveReader(str(path))
    for name in archive.toc:
        data = archive.extract(name)
        if data:
            errors.extend(check_bytes(data, f"{path.name}:{name}"))
        if name.startswith("PYZ"):
            pyz = archive.open_embedded_archive(name)
            for module in pyz.toc:
                if module == "revit_model_mcp.updates":
                    errors.append("Packaged server update module")
                code = pyz.extract(module)
                if isinstance(code, types.CodeType):
                    errors.extend(check_bytes(code_bytes(code), module))
    return errors


def check_folder(folder: Path) -> list[str]:
    errors = []
    binaries = [
        path
        for path in folder.rglob("*")
        if path.is_file()
        and (
            path.suffix.lower() in {".dll", ".exe", ".pyd", ".so", ".dylib"}
            or path.name == "revit-model-mcp"
        )
    ]
    if not any(path.name == "RevitModelMcp.dll" for path in binaries):
        errors.append("No published add-in binary found")
    for path in binaries:
        errors.extend(check_bytes(path.read_bytes(), str(path.relative_to(folder))))
        if "Updater" in path.name:
            errors.append(f"Packaged updater: {path.name}")
        if path.name == "RevitModelMcp.dll":
            if "updateCheck".encode("utf-16-le") in path.read_bytes():
                errors.append(f"Store settings contains updateCheck: {path}")
            if not path.with_name("RevitModelMcp.BatchSupervisor.exe").is_file():
                errors.append(f"Missing batch supervisor beside {path}")
    for path in folder.rglob("settings.json"):
        if "updateCheck" in json.loads(path.read_text(encoding="utf-8")):
            errors.append(f"Store settings contains updateCheck: {path}")
    for path in folder.rglob("updates.py*"):
        if "revit_model_mcp" in path.parts:
            errors.append(f"Packaged update module: {path}")
    return errors


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--source-root", type=Path, default=ROOT)
    parser.add_argument(
        "--registry-source",
        action="store_true",
        help="Use the source registry for a macOS add-in-only publish check; frozen server remains unverified.",
    )
    args = parser.parse_args()
    with tempfile.TemporaryDirectory() as temporary:
        folder = args.bundle
        if folder.is_file() and zipfile.is_zipfile(folder):
            with zipfile.ZipFile(folder) as archive:
                archive.extractall(temporary)
            folder = Path(temporary)
        errors = check_folder(folder) + check_sources(args.source_root)
        if args.registry_source:
            sys.path.insert(0, str(args.source_root / "server"))
            from revit_model_mcp.server import mcp

            registry = [
                {
                    "name": tool.name,
                    "description": tool.description,
                    "inputSchema": tool.input_schema,
                    "annotations": tool.annotations.model_dump(
                        by_alias=True, exclude_none=True
                    ),
                }
                for tool in asyncio.run(mcp.list_tools())
            ]
            print(
                "Source registry mode: frozen server and its archive are not verified"
            )
        else:
            servers = list(folder.rglob("revit-model-mcp.exe"))
            if not servers and sys.platform != "win32":
                servers = [
                    path for path in folder.rglob("revit-model-mcp") if path.is_file()
                ]
            if len(servers) != 1:
                raise SystemExit("Expected one bundled server executable")
            errors.extend(check_archive(servers[0]))
            result = subprocess.run(
                [str(servers[0].resolve()), "--tool-manifest"],
                check=True,
                capture_output=True,
                text=True,
                timeout=60,
            )
            live = json.loads(result.stdout)
            if live.get("channel") != "store":
                errors.append("Bundled server channel is not store")
            registry = live["tools"]
        errors.extend(
            check_manifest(
                json.loads(args.manifest.read_text(encoding="utf-8")), registry
            )
        )
        if errors:
            raise SystemExit("\n".join(errors))
    print("Store checks passed")


if __name__ == "__main__":
    main()
