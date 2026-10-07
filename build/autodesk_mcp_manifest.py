"""Generate the Autodesk Design and Make Marketplace MCP tool manifest."""

import argparse
import asyncio
import importlib.util
import inspect
import json
import sys
from pathlib import Path

from revit_model_mcp.server import mcp

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
MANIFEST_PATH = REPOSITORY_ROOT / "store" / "autodesk-mcp-manifest.json"
REGENERATE_COMMAND = "cd server && uv run python ../build/autodesk_mcp_manifest.py"

_bundle_spec = importlib.util.spec_from_file_location(
    "bundle_manifest", Path(__file__).with_name("bundle_manifest.py")
)
_bundle_manifest = importlib.util.module_from_spec(_bundle_spec)
_bundle_spec.loader.exec_module(_bundle_manifest)
tool_manifest_entry = _bundle_manifest.tool_manifest_entry

CONFIRMATION = {
    "revit_save_document": ("always", "Every call."),
    "revit_sync_document": ("always", "Every call; a non-empty comment is required."),
    "revit_execute_code": ("always", "Every call except dry_run."),
    "revit_remove_links": (
        "always",
        "Every call except dry_run; removal cannot be undone in Revit.",
    ),
    "revit_close_document": (
        "conditional",
        "Closing with save=true, or closing a modified document without saving.",
    ),
    "revit_export": (
        "conditional",
        "overwrite=true and at least one target file already exists.",
    ),
    "revit_export_nwc": (
        "conditional",
        "overwrite=true and the target file already exists.",
    ),
    "revit_process_models": (
        "conditional",
        "In-place saves, and code without dry_run.",
    ),
}

IRREVERSIBLE = {
    "revit_remove_links",
    "revit_save_document",
    "revit_sync_document",
    "revit_close_document",
    "revit_export",
    "revit_export_nwc",
    "revit_process_models",
    "revit_execute_code",
}

FILE_SYSTEM = {
    "revit_open_document": ("read", "Model file on the Revit workstation."),
    "revit_load_family": ("read", "Family files (.rfa) on the Revit workstation."),
    "revit_link_cad": ("read", "DWG file on the Revit workstation."),
    "revit_batch_start": ("read", "Model files on the Revit workstation."),
    "revit_nwc_settings_check": (
        "read",
        "Navisworks exporter XML on the Revit workstation.",
    ),
    "revit_model_health": ("write", "Optional workbook at save_to on the client."),
    "revit_capture_elements": (
        "write",
        "PNG at save_to or a temporary directory on the client.",
    ),
    "revit_export_view": (
        "write",
        "PNG at save_to or a temporary directory on the client.",
    ),
    "revit_export": ("write", "Export folder on the Revit workstation."),
    "revit_export_nwc": ("write", "NWC file path on the Revit workstation."),
    "revit_new_document": ("write", "Optional save_as path on the Revit workstation."),
    "revit_save_document": ("write", "Model path on the Revit workstation."),
    "revit_sync_document": (
        "write",
        "Local copy and central model on the Revit workstation.",
    ),
    "revit_batch_fetch": (
        "write",
        "New snapshot files on the client; existing files are not overwritten.",
    ),
    "revit_issue_register": ("write", "New .xlsx file at output_path on the client."),
    "revit_build_report": (
        "read_write",
        "Reads snapshot directories and writes an .xlsx report on the client.",
    ),
    "revit_process_models": (
        "read_write",
        "Model files, export folders and saved copies on the Revit workstation.",
    ),
    "revit_execute_code": (
        "read_write",
        "Not restricted by the server; confirmed C# runs with the user's rights.",
    ),
}

UNRESTRICTED_NETWORK = {"revit_execute_code", "revit_process_models"}

_UPDATE_CHECK_DISABLE = (
    "Install with UPDATECHECK=0 or set updateCheck to false in settings.json."
)
_HTTPS_REQUEST_ONLY = "None beyond a standard HTTPS request."
_RELEASE_DOWNLOAD_PURPOSE = (
    "Download the release MSI and its SHA256 checksum when an update is available; "
    "the checksum is verified before install."
)
_PYPI_LAUNCHER_PURPOSE = "Download the revit-model-mcp package and its dependencies when the launcher starts."
_PYPI_LAUNCHER_DISABLE = (
    "Run the server with a preinstalled package instead of the launcher."
)


def _endpoint(domain, component, purpose, data_sent, disable):
    return {
        "domain": domain,
        "protocol": "https",
        "component": component,
        "purpose": purpose,
        "data_sent": data_sent,
        "optional": True,
        "disable": disable,
    }


EXTERNAL_ENDPOINTS = [
    _endpoint(
        "api.github.com",
        "Revit add-in",
        "Check for the latest stable release at most once every 24 hours after Revit starts.",
        "None beyond a standard HTTPS request; no model data.",
        _UPDATE_CHECK_DISABLE,
    ),
    _endpoint(
        "github.com",
        "Revit add-in",
        _RELEASE_DOWNLOAD_PURPOSE,
        _HTTPS_REQUEST_ONLY,
        _UPDATE_CHECK_DISABLE,
    ),
    _endpoint(
        "release-assets.githubusercontent.com",
        "Revit add-in",
        _RELEASE_DOWNLOAD_PURPOSE,
        _HTTPS_REQUEST_ONLY,
        _UPDATE_CHECK_DISABLE,
    ),
    _endpoint(
        "pypi.org",
        "Python server",
        "Best-effort check for a newer revit-model-mcp release.",
        _HTTPS_REQUEST_ONLY,
        "Set REVIT_MCP_NO_UPDATE_CHECK=1.",
    ),
    _endpoint(
        "pypi.org",
        "Claude Desktop bundle launcher (uvx)",
        _PYPI_LAUNCHER_PURPOSE,
        "None.",
        _PYPI_LAUNCHER_DISABLE,
    ),
    _endpoint(
        "files.pythonhosted.org",
        "Claude Desktop bundle launcher (uvx)",
        _PYPI_LAUNCHER_PURPOSE,
        "None.",
        _PYPI_LAUNCHER_DISABLE,
    ),
]

AUTODESK_APIS_USED = [
    "Revit API (read access to the open document through ExternalEvent)",
    "Revit API (element, view, sheet, link and family changes, each in one named transaction)",
    "Revit API (document open, save, synchronize and close)",
    "Revit API (PDF, DWG, IFC and schedule CSV export)",
    "Navisworks NWC Export Utility (optional, only for revit_export_nwc)",
]

DATA_HANDLING = {
    "ai_llm_providers_note": (
        "The server does not call any AI service. Tool results go to the MCP client the "
        "user chose; that client may send them to its own model provider under the "
        "user's own agreement."
    ),
    "read_only_mode": (
        "Actions are refused when REVIT_MCP_READ_ONLY=1 or the workstation read-only "
        "file exists."
    ),
    "local_channels": (
        "Named pipe (current Windows user only), a file channel under "
        "%LOCALAPPDATA%\\RevitModelMcp, and an HTTP listener bound to 127.0.0.1 with a "
        "bearer token. A remote workstation is reached only through a user-configured "
        "SSH or HTTP(S) host."
    ),
    "telemetry": "None.",
}


def _tool_entry(tool) -> dict:
    annotations = tool.annotations
    read_only = bool(annotations and annotations.read_only_hint)
    required, condition = CONFIRMATION.get(tool.name, ("never", None))
    confirmation = {"required": required}
    if required != "never":
        confirmation["mechanism"] = "confirm_token"
        confirmation["condition"] = condition
    access, scope = FILE_SYSTEM.get(tool.name, ("none", None))
    file_system = {"access": access}
    if access != "none":
        file_system["scope"] = scope
    return {
        **tool_manifest_entry(tool),
        "access": "read_only" if read_only else "modifying",
        "annotations": {
            "readOnlyHint": annotations.read_only_hint if annotations else None,
            "destructiveHint": annotations.destructive_hint if annotations else None,
            "idempotentHint": annotations.idempotent_hint if annotations else None,
        },
        "irreversible": tool.name in IRREVERSIBLE,
        "confirmation": confirmation,
        "file_system": file_system,
        "network_access": (
            "unrestricted_by_confirmed_code"
            if tool.name in UNRESTRICTED_NETWORK
            else "none"
        ),
    }


def _described(item, *fields) -> dict:
    entry = {}
    for field in fields:
        value = getattr(item, field, None)
        if value is None:
            continue
        entry[field] = inspect.cleandoc(value) if field == "description" else str(value)
    return entry


def build_manifest(tools, resources, prompts) -> dict:
    return {
        "mcp_manifest_version": "1.0",
        "app_model": "A",
        "mcp_spec_version": "2025-11-25",
        "server": {"name": "revit-model-mcp", "transport": "stdio"},
        "tools": [_tool_entry(tool) for tool in tools],
        "resources": [
            _described(item, "uri", "name", "description") for item in resources
        ],
        "prompts": [_described(item, "name", "description") for item in prompts],
        "external_endpoints": [dict(endpoint) for endpoint in EXTERNAL_ENDPOINTS],
        "autodesk_apis_used": list(AUTODESK_APIS_USED),
        "ai_llm_providers": [],
        "data_handling": dict(DATA_HANDLING),
    }


def render(manifest: dict) -> str:
    return json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output", type=Path, default=MANIFEST_PATH, help="Manifest path"
    )
    parser.add_argument(
        "--check", action="store_true", help="Fail when the file differs; do not write"
    )
    args = parser.parse_args()

    manifest = build_manifest(
        asyncio.run(mcp.list_tools()),
        asyncio.run(mcp.list_resources()),
        asyncio.run(mcp.list_prompts()),
    )
    text = render(manifest)
    if args.check:
        current = (
            args.output.read_text(encoding="utf-8") if args.output.exists() else None
        )
        if current != text:
            print(
                f"{args.output} is out of date. Regenerate with {REGENERATE_COMMAND}",
                file=sys.stderr,
            )
            sys.exit(1)
        print(f"{args.output} is up to date")
        return
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(text, encoding="utf-8")
    print(f"Wrote {len(manifest['tools'])} tools to {args.output}")


if __name__ == "__main__":
    main()
