from __future__ import annotations

import asyncio
import runpy
from pathlib import Path

import pytest

from revit_model_mcp.server import mcp

ROOT = Path(__file__).resolve().parents[2]
CHECK = runpy.run_path(str(ROOT / "build/store_check.py"))
GENERATOR = runpy.run_path(str(ROOT / "build/tool_manifest.py"))


def registry():
    tools = asyncio.run(mcp.list_tools())
    return tools, [
        {
            "name": tool.name,
            "description": tool.description,
            "inputSchema": tool.input_schema,
            "annotations": tool.annotations.model_dump(by_alias=True, exclude_none=True),
        }
        for tool in tools
    ]


def test_store_manifest_and_annotations():
    tools, live = registry()
    built = GENERATOR["build_manifest"](tools, [], [], managed=True)
    exported = GENERATOR["export_snake_case"](built, tools)
    assert CHECK["check_manifest"](exported, live) == []
    for tool in live:
        if tool["name"] in CHECK["FILE_WRITERS"]:
            assert tool["annotations"]["readOnlyHint"] is False
            assert tool["annotations"]["idempotentHint"] is False


@pytest.mark.parametrize("failure", ["missing", "read_only", "registry", "schema", "transports"])
def test_manifest_failures(failure):
    tools, live = registry()
    exported = GENERATOR["export_snake_case"](
        GENERATOR["build_manifest"](tools, [], [], managed=True), tools
    )
    tool = next(tool for tool in exported["tools"] if tool["name"] == "revit_export_view")
    if failure == "missing":
        del tool["annotations"]
    elif failure == "read_only":
        tool["annotations"]["readOnlyHint"] = True
    elif failure == "registry":
        exported["tools"].pop()
    elif failure == "schema":
        tool["inputSchema"] = {}
    else:
        del exported["server"]["transports"]
    assert CHECK["check_manifest"](exported, live)


@pytest.mark.parametrize("encoding", ["utf-8", "utf-16-le"])
@pytest.mark.parametrize("value", CHECK["FORBIDDEN"])
def test_forbidden_binary_strings(value, encoding):
    assert CHECK["check_bytes"](value.decode().encode(encoding), "fixture.dll")
    assert (
        CHECK["check_bytes"](
            b"https://sharafutdinovdi.github.io/revit-model-mcp/install/", "fixture.dll"
        )
        == []
    )


def test_compressed_python_code_strings():
    code = compile('def nested():\n return "https://api.github.com"', "fixture", "exec")
    assert CHECK["check_bytes"](CHECK["code_bytes"](code), "fixture")


def test_source_checks(tmp_path):
    addin = tmp_path / "src/RevitModelMcp.Addin"
    (addin / "Control").mkdir(parents=True)
    window = addin / "Fixture.cs"
    settings = addin / "Control/HttpChannel.cs"
    settings.write_text('#if !STORE_CHANNEL\n[DataMember(Name = "updateCheck")]\n#endif')
    window.write_text("class Fixture : Window { Icon = AppIcon; }")
    assert CHECK["check_sources"](tmp_path) == []
    window.write_text('class Fixture : Window {}\nApplication.CreatePanel("MCP");')
    assert len(CHECK["check_sources"](tmp_path)) == 2
    settings.write_text('[DataMember(Name = "updateCheck")]')
    assert len(CHECK["check_sources"](tmp_path)) == 3


def test_settings_defaults_per_channel():
    source = (ROOT / "src/RevitModelMcp.Addin/Control/HttpChannel.cs").read_text()
    assert 'DataMember(Name = "updateCheck"' not in CHECK["store_source"](source)
    github = CHECK["store_source"](source, False)
    assert (
        '[DataMember(Name = "updateCheck", Order = 7)] public bool UpdateCheck { get; set; } = true;'
        in github
    )
    assert CHECK["check_sources"](ROOT) == []


def test_publish_fixtures(tmp_path):
    binary = tmp_path / "RevitModelMcp.dll"
    binary.write_bytes(b"store fixture")
    (tmp_path / "RevitModelMcp.BatchSupervisor.exe").write_bytes(b"supervisor fixture")
    assert CHECK["check_folder"](tmp_path) == []
    (tmp_path / "settings.json").write_text('{"updateCheck":false}')
    assert CHECK["check_folder"](tmp_path)
    (tmp_path / "settings.json").unlink()
    binary.write_bytes("updateCheck".encode("utf-16-le"))
    assert CHECK["check_folder"](tmp_path)
    binary.unlink()
    assert CHECK["check_folder"](tmp_path)


def test_store_registry_import_without_update_module(tmp_path):
    import os
    import shutil
    import subprocess
    import sys

    package = tmp_path / "revit_model_mcp"
    shutil.copytree(
        ROOT / "server/revit_model_mcp", package, ignore=shutil.ignore_patterns("__pycache__")
    )
    init = package / "__init__.py"
    init.write_text(init.read_text().replace('CHANNEL = "github"', 'CHANNEL = "store"'))
    (package / "updates.py").unlink()
    result = subprocess.run(
        [sys.executable, "-m", "revit_model_mcp.server", "--tool-manifest"],
        cwd=tmp_path,
        env={**os.environ, "PYTHONPATH": str(tmp_path)},
        capture_output=True,
        text=True,
        check=True,
        timeout=30,
    )
    import json

    actual = json.loads(result.stdout)
    assert actual["channel"] == "store"
    assert {tool["name"] for tool in actual["tools"]} == {tool.name for tool in registry()[0]}


def test_source_checks_reject_native_task_dialog(tmp_path):
    addin = tmp_path / "src/RevitModelMcp.Addin"
    addin.mkdir(parents=True)
    (addin / "Fixture.cs").write_text('Autodesk.Revit.UI.TaskDialog.Show("Title", "Message");')
    assert CHECK["check_sources"](tmp_path) == [
        "Native TaskDialog requires an owned app dialog: Fixture.cs"
    ]
