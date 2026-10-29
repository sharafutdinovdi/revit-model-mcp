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
    window.write_text(
        'class Fixture : Window { icon.UriSource = "Resources/Icons/App32.png"; Icon = icon; }'
    )
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


@pytest.mark.parametrize("encoding", ["utf-8", "utf-16-le"])
def test_update_endpoints_are_precise(encoding):
    for url in (
        "https://www.python.org/download/releases/2.3/mro/",
        "https://example.com/releases",
        "https://api.github.com.example.com/",
        "https://pypi.org.example.com/",
        "https://github.com/owner/repo/releases-notes",
    ):
        assert CHECK["check_bytes"](url.encode(encoding), "fixture.dll") == []
    for url in (
        "https://api.github.com/repos/owner/repo",
        "https://pypi.org/pypi/package/json",
        "https://github.com/another-owner/another-repo/releases/latest",
    ):
        assert CHECK["check_bytes"](url.encode(encoding), "fixture.dll")


def test_source_checks_reject_native_task_dialog(tmp_path):
    addin = tmp_path / "src/RevitModelMcp.Addin"
    addin.mkdir(parents=True)
    (addin / "Fixture.cs").write_text('Autodesk.Revit.UI.TaskDialog.Show("Title", "Message");')
    assert CHECK["check_sources"](tmp_path) == [
        "Native TaskDialog requires an owned app dialog: Fixture.cs"
    ]


@pytest.mark.parametrize("linked", [False, True])
@pytest.mark.parametrize("source_kind", ["cs", "xaml"])
def test_icon_pack_uri_requires_existing_wpf_resource(tmp_path, linked, source_kind):
    addin = tmp_path / "src/RevitModelMcp.Addin"
    icons = addin / "Resources/Icons"
    icons.mkdir(parents=True)
    icon = icons / "App32.png"
    icon.write_bytes(b"icon fixture")
    project = addin / "RevitModelMcp.Addin.csproj"
    if linked:
        icon = tmp_path / "app.png"
        icon.write_bytes(b"linked fixture")
        resource = '<Resource Include="../../app.png" Link="Resources/Icons/App32.png" />'
    else:
        resource = '<Resource Include="Resources\\Icons\\*.png" />'
    content = (
        "<Project><PropertyGroup><AssemblyName>RevitModelMcp</AssemblyName>"
        f"</PropertyGroup><ItemGroup>{resource}</ItemGroup></Project>"
    )
    project.write_text(content)
    source = addin / f"Fixture.{source_kind}"
    uri = "pack://application:,,,/RevitModelMcp;component/Resources/Icons/App32.png"
    source.write_text(f'Icon = "{uri}"')
    assert CHECK["check_sources"](tmp_path) == []
    source.write_text(f'Icon = "{uri.replace("App32", "Missing32")}"')
    assert "Icon pack URI without matching Resource" in CHECK["check_sources"](tmp_path)[0]
    source.write_text(f'Icon = "{uri.replace("RevitModelMcp;", "WrongAssembly;")}"')
    assert CHECK["check_sources"](tmp_path)
    source.write_text(f'Icon = "{uri}"')
    project.write_text(content.replace("Resource", "EmbeddedResource"))
    assert CHECK["check_sources"](tmp_path)
    project.write_text(content)
    icon.unlink()
    assert CHECK["check_sources"](tmp_path)


def test_icon_pack_uri_checks_interpolated_resources(tmp_path):
    addin = tmp_path / "src/RevitModelMcp.Addin"
    icons = addin / "Resources/Icons"
    icons.mkdir(parents=True)
    (icons / "Logo32-dark.png").write_bytes(b"fixture")
    (addin / "RevitModelMcp.Addin.csproj").write_text(
        "<Project><AssemblyName>RevitModelMcp</AssemblyName>"
        '<ItemGroup><Resource Include="Resources/Icons/*.png" /></ItemGroup></Project>'
    )
    source = addin / "Fixture.cs"
    source.write_text(
        'Icon = $"pack://application:,,,/RevitModelMcp;component/Resources/Icons/Logo32{suffix}.png"'
    )
    assert CHECK["check_sources"](tmp_path) == []
    source.write_text(source.read_text().replace("Logo32", "Missing32"))
    assert CHECK["check_sources"](tmp_path)
