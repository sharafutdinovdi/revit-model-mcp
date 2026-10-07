from __future__ import annotations

import base64
import json
import os
import runpy
import tomllib
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch
from zipfile import ZipFile

import pytest
from mcp import Client, StdioServerParameters
from mcp.client.stdio import stdio_client
from mcp.server.mcpserver.exceptions import ToolError

from revit_model_mcp import package_version
from revit_model_mcp import server as revit_server
from revit_model_mcp.pipe_host import LocalPipeHost
from revit_model_mcp.revit_channel import Job, parse_response
from revit_model_mcp.ssh_host import SshPowerShellHost

MCP_DIRECTORY = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = MCP_DIRECTORY.parent
EXPECTED_TOOLS = {
    "revit_issue_register",
    "revit_capture_elements",
    "revit_build_report",
    "revit_jobs",
    "revit_model_health",
    "revit_model_snapshot",
    "revit_links_status",
    "revit_shared_coordinates",
    "revit_parameter_fill_check",
    "revit_ping",
    "revit_document_info",
    "revit_documents",
    "revit_ui_state",
    "revit_list_views",
    "revit_view_summary",
    "revit_view_info",
    "revit_export_view",
    "revit_schedule_data",
    "revit_view_elements",
    "revit_element_details",
    "revit_view_warnings",
    "revit_list_catalog",
    "revit_aggregate_elements",
    "revit_query_elements",
    "revit_list_warnings",
    "revit_list_relations",
    "revit_list_instances",
    "revit_family_audit",
    "revit_compare_link_datums",
    "revit_nwc_settings_check",
    "revit_batch_start",
    "revit_batch_status",
    "revit_batch_cancel",
    "revit_batch_fetch",
}
ACTION_TOOL_NAMES = {
    "revit_cancel_job",
    "revit_process_models",
    "revit_execute_code",
    "revit_select",
    "revit_show",
    "revit_isolate",
    "revit_override_graphics",
    "revit_move",
    "revit_rotate",
    "revit_copy",
    "revit_mirror",
    "revit_change_type",
    "revit_update_parameters",
    "revit_place_family",
    "revit_load_family",
    "revit_place_families",
    "revit_create_wall",
    "revit_create_mep_run",
    "revit_link_cad",
    "revit_walls_from_cad",
    "revit_create_view",
    "revit_duplicate_view",
    "revit_apply_view_template",
    "revit_create_sheet",
    "revit_place_views_on_sheet",
    "revit_set_parameter",
    "revit_delete",
    "revit_batch",
    "revit_export_nwc",
    "revit_export",
    "revit_edit_families",
    "revit_align_link_datums",
    "revit_open_document",
    "revit_activate_document",
    "revit_activate_view",
    "revit_close_views",
    "revit_new_document",
    "revit_close_document",
    "revit_save_document",
    "revit_sync_document",
    "revit_set_view_visibility",
    "revit_remove_links",
    "revit_undo_last",
}


def test_schedule_data_maps_paging_to_read_job():
    job = Job.schedule_data("Doors", max_rows=25, offset=10)
    assert job.payload == {"command": "schedule-data", "view": "Doors", "limit": 25, "offset": 10}


def encode_discovery_payload(package):
    return base64.b64encode(json.dumps(package, ensure_ascii=False).encode("utf-8")).decode("ascii")


def test_bundle_tool_description_is_dedented():
    tool_manifest_entry = runpy.run_path(str(REPOSITORY_ROOT / "build" / "bundle_manifest.py"))[
        "tool_manifest_entry"
    ]
    tool = SimpleNamespace(
        name="example",
        description=(
            "First line.\n\n    Second line.\n    \n\n"
            "If more than one Revit instance is running, choose one."
        ),
    )
    assert tool_manifest_entry(tool) == {
        "name": "example",
        "description": "First line.\n\nSecond line.\n\nIf more than one Revit instance is running, choose one.",
    }


def test_addressing_sentence_matches_tool_parameters():
    import asyncio

    tools = {tool.name: tool for tool in asyncio.run(revit_server.mcp.list_tools())}
    assert "document is required" not in tools["revit_ui_state"].description
    assert "process_id" in tools["revit_ui_state"].description
    assert "document is required" in tools["revit_document_info"].description


def test_bundle_manifest_matches_tool_registry():
    import asyncio

    manifest = json.loads((REPOSITORY_ROOT / "bundle" / "manifest.json").read_text())
    tools = asyncio.run(revit_server.mcp.list_tools())
    tool_manifest_entry = runpy.run_path(str(REPOSITORY_ROOT / "build" / "bundle_manifest.py"))[
        "tool_manifest_entry"
    ]
    expected = [tool_manifest_entry(tool) for tool in tools]
    assert manifest["tools"] == expected, (
        "Bundle tools differ from the server registry. Regenerate with "
        "cd server && uv run python ../build/bundle_manifest.py"
    )


def test_parameter_names_follow_the_contract():
    import asyncio

    tools = asyncio.run(revit_server.mcp.list_tools())
    retired = {"response_timeout_s", "wait_s", "save_to", "saveTo", "dest_dir"}
    for tool in tools:
        properties = set(tool.input_schema.get("properties", {}))
        assert not properties & retired, f"{tool.name} uses a retired parameter name"
    by_name = {tool.name: set(tool.input_schema["properties"]) for tool in tools}
    assert "wait_seconds" in by_name["revit_jobs"]
    assert "output_dir" in by_name["revit_batch_fetch"]
    assert "output_dir" in by_name["revit_export"]
    assert "output_path" in by_name["revit_export_view"]
    assert "timeout_seconds" in by_name["revit_family_audit"]
    assert "timeout_seconds" in by_name["revit_execute_code"]


def test_smithery_bundle_keeps_desktop_contents_and_adds_schemas(tmp_path):
    import asyncio

    source = tmp_path / "desktop"
    source.mkdir()
    manifest = json.loads((REPOSITORY_ROOT / "bundle" / "manifest.json").read_text())
    manifest["version"] = "9.9.9"
    manifest_path = tmp_path / "manifest.json"
    manifest_path.write_text(json.dumps(manifest))
    (source / "manifest.json").write_text(json.dumps(manifest))
    (source / "icon.png").write_bytes(b"icon")
    archive = tmp_path / "smithery.mcpb"

    bundle_manifest = runpy.run_path(str(REPOSITORY_ROOT / "build" / "bundle_manifest.py"))
    generate = bundle_manifest["main"]
    with (
        patch(
            "sys.argv", ["bundle_manifest.py", "--smithery", str(archive), "--from", str(source)]
        ),
        patch.dict(generate.__globals__, {"MANIFEST_PATH": manifest_path}),
    ):
        generate()

    registry = asyncio.run(revit_server.mcp.list_tools())
    with ZipFile(archive) as bundle:
        assert set(bundle.namelist()) == {"manifest.json", "icon.png"}
        assert bundle.read("icon.png") == b"icon"
        published = json.loads(bundle.read("manifest.json"))
    assert published["version"] == "9.9.9"
    assert published["tools"] == [
        {**bundle_manifest["tool_manifest_entry"](tool), "inputSchema": tool.input_schema}
        for tool in registry
    ]


EXPECTED_PARAMETERS = {
    "revit_model_health": ["timeout_seconds", "pickup_timeout_seconds", "document", "output_path"],
    "revit_activate_view": ["view", "document", "activate_document", "view_type", "zoom"],
    "revit_model_snapshot": [
        "parameter_rules",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_build_report": ["snapshots_dir", "output_path", "previous_dir", "findings"],
    "revit_ping": ["timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_jobs": [
        "cancel_job_id",
        "job_id",
        "wait_seconds",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_document_info": ["timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_documents": ["include_linked", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_ui_state": [],
    "revit_list_catalog": ["section", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_aggregate_elements": [
        "group_by",
        "sum_field",
        "categories",
        "family",
        "type_name",
        "level",
        "view",
        "workset",
        "phase",
        "area_scheme",
        "parameter_filters",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_query_elements": [
        "categories",
        "family",
        "type_name",
        "level",
        "view",
        "workset",
        "phase",
        "area_scheme",
        "parameter_filters",
        "fields",
        "offset",
        "limit",
        "sort_field",
        "sort_direction",
        "include_geometry",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_list_views": [
        "view_type",
        "name_contains",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_view_summary": ["view", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_view_info": ["view", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_issue_register": ["output_path", "project", "issues", "pixel_size", "document"],
    "revit_capture_elements": [
        "element_ids",
        "pixel_size",
        "padding_mm",
        "mode",
        "output_path",
        "document",
    ],
    "revit_export_view": ["view", "pixel_size", "output_path", "document"],
    "revit_schedule_data": ["schedule", "max_rows", "offset", "document"],
    "revit_view_elements": [
        "view",
        "categories",
        "offset",
        "limit",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_element_details": [
        "element_id",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_view_warnings": ["view", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_list_warnings": [
        "warning_text",
        "include_elements",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_list_relations": [
        "relation",
        "source_id",
        "source_name",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
    "revit_list_instances": ["document"],
    "revit_family_audit": ["families", "timeout_seconds", "document"],
    "revit_nwc_settings_check": [
        "settings_xml",
        "timeout_seconds",
        "pickup_timeout_seconds",
        "document",
    ],
}

UNADDRESSED_TOOLS = {
    "revit_list_instances",
    "revit_batch_start",
    "revit_batch_status",
    "revit_batch_cancel",
    "revit_batch_fetch",
    "revit_build_report",
}
EXPECTED_PARAMETERS = {
    name: parameters if name in UNADDRESSED_TOOLS else [*parameters, "process_id"]
    for name, parameters in EXPECTED_PARAMETERS.items()
}


class RecordingChannel:
    def __init__(self) -> None:
        self.calls = []

    async def execute(self, job, timeout_seconds, pickup_timeout_seconds):
        self.calls.append((job, timeout_seconds, pickup_timeout_seconds))
        return {"command": job.command, "payload": job.payload}


class ServerTests(unittest.IsolatedAsyncioTestCase):
    async def test_ping_reports_versions_and_update_state(self) -> None:
        channel = RecordingChannel()
        with (
            patch.object(revit_server, "channel", channel),
            patch.object(
                revit_server,
                "update_status",
                return_value={"latestKnownVersion": "0.7.0", "updateCheck": "disabled"},
            ),
        ):
            result = await revit_server._execute(Job.ping(), 120, 300, None)
        self.assertEqual(result["serverVersion"], package_version())
        self.assertEqual(result["latestKnownVersion"], "0.7.0")
        self.assertEqual(result["updateCheck"], "disabled")

    async def test_jobs_tool_passes_cancellation_without_write_gate(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool("revit_jobs", {"cancel_job_id": "job-1"})
        job, _, _ = channel.calls[0]
        self.assertEqual(job.command, "jobs")
        self.assertEqual(job.payload["cancelJobId"], "job-1")

    async def test_documents_lists_background_models_as_read(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool("revit_documents", {})
        job, response_timeout, pickup_timeout = channel.calls[0]
        self.assertEqual(job.command, "documents")
        self.assertEqual(job.payload, {"command": "documents", "includeLinked": False})
        self.assertEqual(response_timeout, 120)
        self.assertEqual(pickup_timeout, 300)

    async def test_documents_include_linked_reaches_channel(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool("revit_documents", {"include_linked": True})
        job, _, _ = channel.calls[0]
        self.assertEqual(job.payload, {"command": "documents", "includeLinked": True})

    async def test_ui_state_reads_selected_process(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool("revit_ui_state", {"process_id": 84})
        job, _, _ = channel.calls[0]
        self.assertEqual(job.command, "ui-state")
        self.assertEqual(job.payload["command"], "ui-state")
        self.assertEqual(job.payload["targetProcessId"], 84)

    async def test_view_info_maps_view_and_document(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool(
                "revit_view_info", {"view": "3D NWC", "document": "Model"}
            )
        job, _, _ = channel.calls[0]
        self.assertEqual(job.command, "view-info")
        self.assertEqual(job.payload["view"], "3D NWC")
        self.assertEqual(job.payload["targetDocument"], "Model")

    async def test_family_audit_is_read_only_and_uses_response_budget(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool(
                "revit_family_audit",
                {"families": ["Door"], "timeout_seconds": 600, "document": "Model"},
            )
        job, response_timeout, pickup_timeout = channel.calls[0]
        self.assertEqual(job.command, "family-audit")
        self.assertEqual(job.payload["families"], ["Door"])
        self.assertEqual(job.payload["targetDocument"], "Model")
        self.assertEqual(response_timeout, 600)
        self.assertEqual(pickup_timeout, 300)

    async def test_nwc_settings_check_routes_as_read(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool(
                "revit_nwc_settings_check", {"settings_xml": "C:\\x\\settings.xml"}
            )
        job, _, _ = channel.calls[0]
        self.assertEqual(job.command, "nwc-settings-check")
        self.assertEqual(job.payload["settingsXml"], "C:\\x\\settings.xml")

    def test_server_version_matches_package_metadata(self) -> None:
        self.assertEqual(revit_server.mcp.version, package_version())

    def test_instructions_state_the_confirmation_rule(self) -> None:
        instructions = revit_server.mcp.instructions
        self.assertIn("summary", instructions)
        self.assertIn("explicit confirmation", instructions)
        self.assertIn("REVIT_MCP_READ_ONLY", instructions)
        self.assertIn("revit_undo_last", instructions)

    async def test_stdio_server_starts_and_lists_tools_without_revit(self) -> None:
        parameters = StdioServerParameters(
            command="revit-model-mcp",
            args=[],
            cwd=str(REPOSITORY_ROOT),
            env=dict(os.environ),
        )

        async with Client(stdio_client(parameters), read_timeout_seconds=10) as client:
            result = await client.list_tools()

        tools = {tool.name: tool for tool in result.tools}
        self.assertEqual(set(tools), EXPECTED_TOOLS | ACTION_TOOL_NAMES)
        read_tools = {name: tool for name, tool in tools.items() if name in EXPECTED_TOOLS}
        for tool in read_tools.values():
            self.assertTrue(tool.title)
            self.assertLessEqual(len(tool.title), 40)
            self.assertEqual(tool.annotations.title, tool.title)
            self.assertIs(tool.annotations.read_only_hint, True)
        self.assertTrue(
            all(
                tool.annotations and tool.annotations.read_only_hint for tool in read_tools.values()
            )
        )
        action_tools = {name: tool for name, tool in tools.items() if name in ACTION_TOOL_NAMES}
        self.assertTrue(all(not tool.annotations.read_only_hint for tool in action_tools.values()))
        for name, required in {
            "revit_override_graphics": {"element_ids"},
            "revit_create_view": {"kind"},
            "revit_create_mep_run": {"kind", "points_mm", "level"},
            "revit_link_cad": {"path"},
            "revit_walls_from_cad": {"cad_id", "layers", "level"},
            "revit_duplicate_view": {"view"},
            "revit_apply_view_template": {"views", "template"},
            "revit_create_sheet": {"number", "name"},
            "revit_place_views_on_sheet": {"sheet", "views"},
        }.items():
            properties = action_tools[name].input_schema["properties"]
            self.assertTrue(required.issubset(action_tools[name].input_schema["required"]))
            self.assertIn("dry_run", properties)
            self.assertIn("document", properties)
        view_properties = action_tools["revit_create_view"].input_schema["properties"]
        self.assertIn("display_style", view_properties)
        self.assertIn("detail_level", view_properties)
        self.assertEqual(
            action_tools["revit_walls_from_cad"].input_schema["properties"]["max_gap_mm"][
                "default"
            ],
            3000,
        )
        self.assertIn(
            "Call revit_list_views next",
            tools["revit_document_info"].description,
        )
        self.assertIn("with revit_view_summary", tools["revit_list_views"].description)
        self.assertIn(
            "before calling revit_view_elements",
            tools["revit_view_summary"].description,
        )
        self.assertIn("numbers do not explain geometry", tools["revit_export_view"].description)
        self.assertIn("change the active view", tools["revit_export_view"].description)
        self.assertIn("Start universal queries here", tools["revit_list_catalog"].description)
        self.assertIn("after revit_list_catalog", tools["revit_aggregate_elements"].description)
        self.assertIn("after revit_list_catalog", tools["revit_query_elements"].description)
        self.assertFalse(
            tools["revit_query_elements"].input_schema["properties"]["include_geometry"]["default"]
        )
        self.assertIn("roomCenterMm", tools["revit_query_elements"].description)
        self.assertIn("roomCenterMm", tools["revit_element_details"].description)
        self.assertEqual(
            tools["revit_view_elements"].input_schema["properties"]["timeout_seconds"]["default"],
            120,
        )
        self.assertEqual(
            tools["revit_view_elements"].input_schema["properties"]["pickup_timeout_seconds"][
                "default"
            ],
            300,
        )
        for name, parameters in EXPECTED_PARAMETERS.items():
            properties = tools[name].input_schema["properties"]
            self.assertEqual(list(properties), parameters)
            self.assertTrue(all(parameter == parameter.lower() for parameter in properties))
            self.assertTrue(tools[name].description.strip())
            if "document" in properties:
                self.assertTrue(properties["document"].get("description"))

    async def test_camel_case_aliases_reach_jobs_without_revit(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool(
                "revit_aggregate_elements",
                {"groupBy": ["level"], "sumField": "Area", "areaScheme": "Architecture"},
            )
            await revit_server.mcp.call_tool(
                "revit_aggregate_elements",
                {"group_by": ["level"], "sum_field": "Area", "area_scheme": "Architecture"},
            )
            await revit_server.mcp.call_tool(
                "revit_view_summary", {"viewName": "Level 1 Plan", "document": "Sample Model"}
            )
            await revit_server.mcp.call_tool(
                "revit_export_view",
                {"view": "Level 1 Plan", "pixelSize": 2400, "outputPath": "/tmp/plan.png"},
            )
            await revit_server.mcp.call_tool(
                "revit_list_relations", {"relation": "level-rooms", "sourceName": "Level 1"}
            )
            await revit_server.mcp.call_tool(
                "revit_list_relations", {"relation": "level-rooms", "source_name": "Level 1"}
            )

        (
            camel_aggregate,
            snake_aggregate,
            view_summary,
            export_view,
            camel_relation,
            snake_relation,
        ) = [call[0] for call in channel.calls]
        self.assertEqual(camel_aggregate.payload, snake_aggregate.payload)
        self.assertEqual(camel_aggregate.payload["numericField"], "Area")
        self.assertEqual(view_summary.payload["view"], "Level 1 Plan")
        self.assertEqual(view_summary.payload["targetDocument"], "Sample Model")
        self.assertEqual(export_view.payload["pixelSize"], 2400)
        self.assertEqual(export_view.output_path, "/tmp/plan.png")
        self.assertEqual(camel_relation.payload, snake_relation.payload)
        self.assertEqual(camel_relation.payload["sourceName"], "Level 1")

    async def test_query_geometry_flag_reaches_channel_with_both_aliases(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            for name in ("include_geometry", "includeGeometry"):
                await revit_server.mcp.call_tool("revit_query_elements", {name: True})
                self.assertTrue(channel.calls[-1][0].payload["includeGeometry"])
            await revit_server.mcp.call_tool("revit_query_elements", {})
            self.assertNotIn("includeGeometry", channel.calls[-1][0].payload)

    async def test_list_instances_reads_processes_without_channel(self) -> None:
        class RecordingHost:
            async def list_revit_instances(self, document):
                self.document = document
                return [{"processId": 42, "documentName": "SampleModel"}]

        host = RecordingHost()
        with patch.object(revit_server, "host", host):
            result = await revit_server.mcp.call_tool(
                "revit_list_instances", {"document": "Sample Model"}
            )

        self.assertEqual(host.document, "Sample Model")
        content = result.structured_content
        self.assertEqual(set(content), {"instances", "skipped", "skippedCount"})
        self.assertEqual(
            content["instances"],
            [{"processId": 42, "documentName": "SampleModel"}],
        )
        self.assertEqual(content["skipped"], [])
        self.assertEqual(content["skippedCount"], 0)

    async def test_list_instances_ignores_stale_instance_file(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(
            return_value=encode_discovery_payload(
                {
                    "processes": [],
                    "files": [
                        {
                            "name": "instance_42.json",
                            "content": json.dumps(
                                {
                                    "processId": 42,
                                    "revitVersion": "2023",
                                    "documentTitle": "Stale model",
                                    "documentPath": r"C:\Models\Stale.rvt",
                                    "updatedUtc": "2000-01-01T00:00:00Z",
                                }
                            ),
                        }
                    ],
                }
            )
        )

        self.assertEqual(await host.list_revit_instances(), [])

    async def test_list_instances_reads_files_without_window_titles(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(
            return_value=encode_discovery_payload(
                {
                    "processes": [{"processId": 42, "revitVersion": "2023.1"}],
                    "files": [
                        {
                            "name": "instance_42.json",
                            "content": json.dumps(
                                {
                                    "processId": 42,
                                    "revitVersion": "2023",
                                    "documentTitle": "SampleModel",
                                    "documentPath": r"C:\Models\SampleModel.rvt",
                                    "updatedUtc": datetime.now(timezone.utc).isoformat(),
                                }
                            ),
                        }
                    ],
                }
            )
        )

        result = await host.list_revit_instances("Sample")

        self.assertEqual(result[0]["documentName"], "SampleModel")
        self.assertTrue(result[0]["pluginResponding"])
        script = host._run.await_args.args[0]
        self.assertIn("instance_*.json", script)
        self.assertNotIn("MainWindowTitle", script)

    async def test_list_instances_marks_process_when_plugin_does_not_respond(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(
            return_value=encode_discovery_payload(
                {
                    "processes": [{"processId": 84, "revitVersion": "2024.2"}],
                    "files": [],
                }
            )
        )

        result = await host.list_revit_instances()

        self.assertEqual(result[0]["documentName"], "")
        self.assertFalse(result[0]["pluginResponding"])

    async def test_package_registers_console_entry_point(self) -> None:
        config = tomllib.loads((MCP_DIRECTORY / "pyproject.toml").read_text())
        self.assertEqual(
            config["project"]["scripts"]["revit-model-mcp"], "revit_model_mcp.server:main"
        )

    def test_bundle_win32_launcher_escapes_version_floor(self) -> None:
        manifest = json.loads((REPOSITORY_ROOT / "bundle/manifest.json").read_text())
        command = manifest["server"]["mcp_config"]["platform_overrides"]["win32"]["args"][1]
        self.assertEqual(command.count("revit-model-mcp^>="), 2)
        self.assertNotIn('"', command)

    def test_host_configuration(self) -> None:
        local = revit_server.create_host("local")
        self.assertIsInstance(local, LocalPipeHost)
        self.assertTrue(local.fallback.local)
        remote = revit_server.create_host("ssh:revit-host")
        self.assertFalse(remote.local)
        self.assertEqual(remote.host, "revit-host")
        for invalid in ["revit-host", "ssh:", "ssh:-option", "ssh:host;command"]:
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                revit_server.create_host(invalid)

    async def test_redaction_covers_tool_and_instance_responses(self) -> None:
        response = {
            "command": "document-info",
            "success": True,
            "responder": {"documentPath": r"C:\Models\Sample.rvt"},
            "data": [{"documentPath": r"\\host\share\Linked.rvt", "localPath": "/tmp/view.png"}],
        }
        instances = [{"documentPath": "C:/Models/Sample.rvt"}]
        with (
            patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}),
            patch.object(revit_server.channel, "execute", AsyncMock(return_value=response)),
            patch.object(
                revit_server.host, "list_revit_instances", AsyncMock(return_value=instances)
            ),
        ):
            result = await revit_server.revit_document_info()
            listed = await revit_server.revit_list_instances()
        self.assertEqual(result["responder"]["documentPath"], "Sample.rvt")
        self.assertEqual(result["data"][0]["documentPath"], "Linked.rvt")
        self.assertEqual(result["data"][0]["localPath"], "/tmp/view.png")
        self.assertEqual(listed["instances"][0]["documentPath"], "Sample.rvt")
        self.assertEqual(response["responder"]["documentPath"], r"C:\Models\Sample.rvt")
        with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "0"}):
            self.assertEqual(revit_server.redact_model_paths(response), response)

    async def test_read_diagnostics_survive_tool_response_and_redact_reason(self) -> None:
        response = {
            "command": "links-status",
            "success": True,
            "data": {"rvtLinks": [{"typeId": 10, "error": r"Could not read C:\Models\A.rvt"}]},
            "skipped": [{"what": "RVT link type 10", "reason": r"Could not read C:\Models\A.rvt"}],
            "skippedCount": 101,
        }
        with (
            patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}),
            patch.object(revit_server.channel, "execute", AsyncMock(return_value=response)),
        ):
            result = await revit_server.revit_links_status()
        self.assertEqual(result["skippedCount"], 101)
        self.assertEqual(
            result["skipped"], [{"what": "RVT link type 10", "reason": "Could not read A.rvt"}]
        )
        self.assertEqual(result["data"]["rvtLinks"][0]["error"], "Could not read A.rvt")

    async def test_complete_read_adds_empty_diagnostics(self) -> None:
        with patch.object(
            revit_server.channel,
            "execute",
            AsyncMock(return_value={"command": "ping", "success": True, "data": "pong"}),
        ):
            result = await revit_server.revit_ping()
        self.assertEqual(result["skipped"], [])
        self.assertEqual(result["skippedCount"], 0)

    async def test_redaction_covers_failed_read_tool_error(self) -> None:
        failure = {
            "command": "document-info",
            "success": False,
            "message": "Could not find file 'C:\\Users\\Owner\\Tower.rvt'.",
        }

        async def failed_execute(*_args):
            return parse_response(json.dumps(failure), "document-info")

        with (
            patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}),
            patch.object(revit_server.channel, "execute", failed_execute),
            self.assertRaises(ToolError) as raised,
        ):
            await revit_server.revit_document_info()

        self.assertEqual(str(raised.exception), "Could not find file 'Tower.rvt'.")

    def test_cli_enables_redaction_and_selects_host(self) -> None:
        with (
            patch("sys.argv", ["revit-model-mcp", "--host", "ssh:revit-host", "--redact-paths"]),
            patch.dict(os.environ, {}, clear=False),
            patch.object(revit_server, "host"),
            patch.object(revit_server, "channel"),
            patch.object(revit_server.mcp, "run") as run,
        ):
            revit_server.main()
            self.assertEqual(revit_server.host.host, "revit-host")
            self.assertEqual(os.environ["REVIT_MCP_REDACT_PATHS"], "1")
            run.assert_called_once_with(transport="stdio")

    def test_cli_help_reports_environment_without_starting_transport(self) -> None:
        import contextlib
        import io

        output = io.StringIO()
        with (
            patch("sys.argv", ["revit-model-mcp", "--help"]),
            patch.dict(
                os.environ,
                {
                    "REVIT_MCP_HOST": "ssh:revit-host",
                    "REVIT_MCP_CHANNEL_DIR": r"C:\RevitChannel",
                    "REVIT_MCP_REDACT_PATHS": "1",
                },
            ),
            patch.object(revit_server, "create_host") as create_host,
            patch.object(revit_server.mcp, "run") as run,
            contextlib.redirect_stdout(output),
            self.assertRaises(SystemExit) as raised,
        ):
            revit_server.main()
        self.assertEqual(raised.exception.code, 0)
        self.assertIn("host mode: ssh:revit-host", output.getvalue())
        self.assertIn(r"channel dir (Windows): C:\RevitChannel", output.getvalue())
        self.assertIn("redact paths: True", output.getvalue())
        create_host.assert_not_called()
        run.assert_not_called()


if __name__ == "__main__":
    unittest.main()


@pytest.mark.parametrize("default", [False, True])
@pytest.mark.parametrize(
    "value, expected",
    [
        (None, None),
        ("1", True),
        ("0", False),
        ("true", True),
        ("false", False),
        ("yes", True),
        ("no", False),
        ("on", True),
        ("off", False),
        ("  TrUe \t", True),
        (" NO ", False),
    ],
)
def test_env_flag(monkeypatch, default, value, expected):
    monkeypatch.delenv("REVIT_MCP_READ_ONLY", raising=False)
    if value is not None:
        monkeypatch.setenv("REVIT_MCP_READ_ONLY", value)
    assert revit_server.env_flag("REVIT_MCP_READ_ONLY", default) is (
        default if expected is None else expected
    )


@pytest.mark.parametrize("value", ["", "enabled", "2"])
def test_env_flag_rejects_invalid_values(monkeypatch, value):
    monkeypatch.setenv("REVIT_MCP_READ_ONLY", value)
    with pytest.raises(ValueError, match="REVIT_MCP_READ_ONLY must be"):
        revit_server.env_flag("REVIT_MCP_READ_ONLY", False)


def test_redaction_accepts_boolean_string(monkeypatch):
    monkeypatch.setenv("REVIT_MCP_REDACT_PATHS", "true")
    assert revit_server.redact_model_paths({"documentPath": r"C:\Models\Model.rvt"}) == {
        "documentPath": "Model.rvt"
    }


def test_in_process_read_tool_titles():
    import asyncio

    tools = asyncio.run(revit_server.mcp.list_tools())
    reads = {tool.name: tool for tool in tools if tool.name in EXPECTED_TOOLS}
    assert set(reads) == EXPECTED_TOOLS
    for tool in reads.values():
        assert tool.title and len(tool.title) <= 40
        assert tool.annotations.title == tool.title
        assert tool.annotations.read_only_hint is True


def test_discovery_unions_heartbeats_with_all_running_processes():
    from revit_model_mcp.ssh_host import _parse_instance_package

    now = datetime.now(timezone.utc)
    status = {
        "processId": 42,
        "revitVersion": "2024",
        "documentTitle": "Model A",
        "documentPath": r"C:\Models\Unique.rvt",
        "updatedUtc": now.isoformat(),
        "fileChannelVersion": 2,
        "startedUtc": now.isoformat(),
        "httpPort": None,
    }
    package = {
        "processes": [{"processId": 42}, {"processId": 84}],
        "files": [{"name": "instance_42.json", "content": json.dumps(status)}],
    }
    instances = _parse_instance_package(package, "", now)
    assert [item["processId"] for item in instances] == [42, 84]
    assert all(item["pluginResponding"] is False for item in instances)
    assert instances[0]["startedUtc"] == status["startedUtc"]
    assert instances[0]["httpPort"] is None
    assert _parse_instance_package(package, "unique.rvt", now)[0]["processId"] == 42
    package["processes"] = [{"processId": 84}]
    assert [item["processId"] for item in _parse_instance_package(package, "", now)] == [84]


def test_client_name_comes_from_initialize_context():
    import asyncio
    from types import SimpleNamespace

    from mcp.server.mcpserver import Context

    from revit_model_mcp.revit_channel import JobPickupStatus, RevitChannel

    class Host:
        async def select_job(self, job):
            return self, job

        async def prepare_job(self, name, content, command):
            self.payload = json.loads(content)
            return set()

        async def wait_until_trigger_is_gone(self, timeout_seconds):
            return JobPickupStatus(True, 0, False, 0)

        async def wait_for_new_response(
            self, command, known_names, timeout_seconds, correlation_id=None
        ):
            return "response_ping.json"

        async def finish_job(self, response_name, cleanup_names, download_artifact, output_path):
            return json.dumps({"command": "ping", "success": True, "data": "pong"}), None

        async def delete_files(self, names):
            pass

    host = Host()
    session = SimpleNamespace(
        client_params=SimpleNamespace(client_info=SimpleNamespace(name="codex"))
    )
    context = Context(request_context=SimpleNamespace(session=session))
    with patch.object(revit_server, "channel", RevitChannel(host)):
        asyncio.run(revit_server.mcp.call_tool("revit_ping", {}, context=context))
    assert host.payload["clientName"] == "codex"
    assert len(host.payload["clientId"]) == 32
