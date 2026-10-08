from __future__ import annotations

import asyncio
import json
import runpy
from pathlib import Path
from unittest.mock import patch

import pytest

from revit_model_mcp import server as revit_server

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPOSITORY_ROOT / "build" / "tool_manifest.py"
COMMITTED = REPOSITORY_ROOT / "docs" / "tool-manifest.json"


@pytest.fixture(scope="module")
def generator():
    return runpy.run_path(str(SCRIPT))


@pytest.fixture(scope="module")
def registry():
    return (
        asyncio.run(revit_server.mcp.list_tools()),
        asyncio.run(revit_server.mcp.list_resources()),
        asyncio.run(revit_server.mcp.list_prompts()),
    )


@pytest.fixture(scope="module")
def built(generator, registry):
    return generator["build_manifest"](*registry)


def test_committed_manifest_matches_registry(generator, built):
    assert json.loads(COMMITTED.read_text(encoding="utf-8")) == built, (
        "The tool manifest differs from the server registry. Regenerate with "
        "cd server && uv run python ../build/tool_manifest.py"
    )
    assert COMMITTED.read_text(encoding="utf-8") == generator["render"](built)


def test_managed_manifest(generator, registry, built):
    managed = generator["build_manifest"](*registry, managed=True)
    assert managed["externalEndpoints"] == []
    assert managed["variant"] == "managed"
    assert list(managed)[:2] == ["schemaVersion", "variant"]
    assert managed["tools"] == built["tools"]
    assert managed["dataHandling"]["updateChecks"] == (
        "None. The managed add-in build never checks for updates, "
        "and the server is started with REVIT_MCP_NO_UPDATE_CHECK=1."
    )
    assert "variant" not in built
    assert "updateChecks" not in built["dataHandling"]


def test_tools_match_bundle_manifest(built):
    bundle = json.loads((REPOSITORY_ROOT / "bundle" / "manifest.json").read_text())
    assert [
        {"name": tool["name"], "description": tool["description"]} for tool in built["tools"]
    ] == bundle["tools"]


def test_policy_tables_reference_registered_tools(generator, registry):
    tools = registry[0]
    names = {tool.name for tool in tools}
    for table in ("CONFIRMATION", "IRREVERSIBLE", "FILE_SYSTEM"):
        assert set(generator[table]) <= names, table
    with_token = {
        tool.name for tool in tools if "confirm_token" in tool.input_schema.get("properties", {})
    }
    assert with_token == set(generator["CONFIRMATION"])


def test_read_only_tools_are_safe(built, registry):
    hints = {tool.name: tool.annotations.read_only_hint for tool in registry[0]}
    for entry in built["tools"]:
        assert (entry["access"] == "read_only") == bool(hints[entry["name"]])
        if entry["access"] == "read_only":
            assert entry["confirmation"]["required"] == "never"
            assert entry["irreversible"] is False
        assert entry["fileSystem"]["access"] in {"none", "read", "write", "read_write"}


def test_required_manifest_keys(built):
    assert built["schemaVersion"] == "1.0"
    assert isinstance(built["mcpSpecVersion"], str)
    assert built["server"]["transport"] == "stdio"
    for key in ("tools", "resources", "prompts", "externalEndpoints", "apisUsed"):
        assert isinstance(built[key], list) and built[key], key
    assert built["aiServices"] == []
    assert isinstance(built["dataHandling"], dict)
    endpoints = built["externalEndpoints"]
    assert all(endpoint["protocol"] == "https" for endpoint in endpoints)
    keys = [(endpoint["domain"], endpoint["component"]) for endpoint in endpoints]
    assert len(keys) == len(set(keys))


def test_committed_manifest_has_neutral_keys():
    text = COMMITTED.read_text(encoding="utf-8")
    for old in (
        "mcp_manifest_version",
        "app_model",
        "autodesk_apis_used",
        "ai_llm_providers",
        "external_endpoints",
        "data_handling",
        "file_system",
        "network_access",
    ):
        assert old not in text, old


@pytest.mark.parametrize("managed", [False, True])
def test_snake_case_export(generator, built, tmp_path, managed):
    output = tmp_path / "nested" / "export.json"
    argv = ["tool_manifest.py", "--export-snake-case", str(output)]
    if managed:
        argv.append("--managed")
    with patch("sys.argv", argv):
        generator["main"]()
    exported = json.loads(output.read_text(encoding="utf-8"))
    assert exported["tools"] == [
        {"name": tool["name"], "description": tool["description"]} for tool in built["tools"]
    ]
    assert "mcp_manifest_version" in exported
    assert "app_model" in exported
    assert [e["domain"] for e in exported["external_endpoints"]] == (
        [] if managed else [e["domain"] for e in built["externalEndpoints"]]
    )


def test_check_mode(generator, tmp_path):
    main = generator["main"]
    output = tmp_path / "manifest.json"

    def run_check() -> int:
        with patch("sys.argv", ["tool_manifest.py", "--check", "--output", str(output)]):
            try:
                main()
            except SystemExit as exit_info:
                return exit_info.code
        return 0

    output.write_text("{}\n", encoding="utf-8")
    assert run_check() == 1
    assert output.read_text(encoding="utf-8") == "{}\n"

    with patch("sys.argv", ["tool_manifest.py", "--output", str(output)]):
        main()
    assert run_check() == 0


def test_managed_output_and_check(generator, tmp_path):
    output = tmp_path / "nested" / "managed.json"
    argv = ["tool_manifest.py", "--managed", "--output", str(output)]
    with patch("sys.argv", argv):
        generator["main"]()
    assert json.loads(output.read_text(encoding="utf-8"))["variant"] == "managed"
    with patch("sys.argv", [*argv, "--check"]):
        generator["main"]()


def test_managed_default_preserves_public_manifest(generator, tmp_path):
    main = generator["main"]
    output = tmp_path / "output" / "tool-manifest-managed.json"
    original = COMMITTED.read_bytes()
    assert generator["MANAGED_MANIFEST_PATH"] == (
        REPOSITORY_ROOT / "output" / "tool-manifest-managed.json"
    )
    with patch.dict(main.__globals__, {"MANAGED_MANIFEST_PATH": output}):
        with patch("sys.argv", ["tool_manifest.py", "--managed"]):
            main()
        assert json.loads(output.read_text(encoding="utf-8"))["variant"] == "managed"
        with patch("sys.argv", ["tool_manifest.py", "--managed", "--check"]):
            main()
    assert COMMITTED.read_bytes() == original


def test_generated_text_is_clean():
    text = COMMITTED.read_text(encoding="utf-8")
    assert "\u2014" not in text
    assert "\u2013" not in text
    assert "\\Users\\" not in text
    assert "/Users/" not in text


def test_resources_and_prompts_have_descriptions(built):
    for section in ("resources", "prompts"):
        assert built[section]
        for entry in built[section]:
            assert entry["description"].strip(), entry
