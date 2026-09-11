from __future__ import annotations

import json
import os
import tomllib
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import AsyncMock, patch

from mcp import Client, StdioServerParameters
from mcp.client.stdio import stdio_client
from revit_model_mcp import server as revit_server
from revit_model_mcp.ssh_host import SshPowerShellHost

MCP_DIRECTORY = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = MCP_DIRECTORY.parent
EXPECTED_TOOLS = {
    "revit_ping",
    "revit_document_info",
    "revit_list_views",
    "revit_view_summary",
    "revit_export_view",
    "revit_view_elements",
    "revit_element_details",
    "revit_view_warnings",
    "revit_list_catalog",
    "revit_aggregate_elements",
    "revit_query_elements",
    "revit_list_warnings",
    "revit_list_relations",
    "revit_list_instances",
}
EXPECTED_PARAMETERS = {
    "revit_ping": ["timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_document_info": ["timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_list_catalog": ["section", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_aggregate_elements": [
        "group_by", "sum_field", "categories", "family", "type_name", "level",
        "view", "workset", "phase", "area_scheme", "parameter_filters",
        "timeout_seconds", "pickup_timeout_seconds", "document",
    ],
    "revit_query_elements": [
        "categories", "family", "type_name", "level", "view", "workset", "phase",
        "area_scheme", "parameter_filters", "fields", "offset", "limit",
        "sort_field", "sort_direction", "timeout_seconds", "pickup_timeout_seconds", "document",
    ],
    "revit_list_views": ["view_type", "name_contains", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_view_summary": ["view", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_export_view": ["view", "pixel_size", "save_to", "document"],
    "revit_view_elements": ["view", "categories", "offset", "limit", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_element_details": ["element_id", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_view_warnings": ["view", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_list_warnings": ["warning_text", "include_elements", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_list_relations": ["relation", "source_id", "source_name", "timeout_seconds", "pickup_timeout_seconds", "document"],
    "revit_list_instances": ["document"],
}


class RecordingChannel:
    def __init__(self) -> None:
        self.calls = []

    async def execute(self, job, timeout_seconds, pickup_timeout_seconds):
        self.calls.append((job, timeout_seconds, pickup_timeout_seconds))
        return {"command": job.command, "payload": job.payload}


class ServerTests(unittest.IsolatedAsyncioTestCase):
    async def test_stdio_server_starts_and_lists_tools_without_revit(self) -> None:
        parameters = StdioServerParameters(
            command="revit-model-mcp",
            args=[],
            cwd=str(REPOSITORY_ROOT),
            env=os.environ.copy(),
        )

        async with Client(stdio_client(parameters), read_timeout_seconds=10) as client:
            result = await client.list_tools()

        tools = {tool.name: tool for tool in result.tools}
        self.assertEqual(set(tools), EXPECTED_TOOLS)
        self.assertTrue(
            all(
                tool.annotations and tool.annotations.read_only_hint
                for tool in tools.values()
            )
        )
        self.assertIn(
            "Call revit_list_views next",
            tools["revit_document_info"].description,
        )
        self.assertIn(
            "with revit_view_summary", tools["revit_list_views"].description
        )
        self.assertIn(
            "before calling revit_view_elements",
            tools["revit_view_summary"].description,
        )
        self.assertIn("numbers do not explain geometry", tools["revit_export_view"].description)
        self.assertIn("change the active view", tools["revit_export_view"].description)
        self.assertIn("Start universal queries here", tools["revit_list_catalog"].description)
        self.assertIn("after revit_list_catalog", tools["revit_aggregate_elements"].description)
        self.assertIn("after revit_list_catalog", tools["revit_query_elements"].description)
        self.assertEqual(
            tools["revit_view_elements"].input_schema["properties"]["timeout_seconds"][
                "default"
            ],
            120,
        )
        self.assertEqual(
            tools["revit_view_elements"].input_schema["properties"][
                "pickup_timeout_seconds"
            ]["default"],
            300,
        )
        for name, parameters in EXPECTED_PARAMETERS.items():
            properties = tools[name].input_schema["properties"]
            self.assertEqual(list(properties), parameters)
            self.assertTrue(all(parameter == parameter.lower() for parameter in properties))
            description = " ".join(tools[name].description.split())
            self.assertIn(f"Parameters: {', '.join(parameters)}.", description)
            if name != "revit_list_instances":
                self.assertIn("document is required", description)

    async def test_camel_case_aliases_reach_jobs_without_revit(self) -> None:
        channel = RecordingChannel()
        with patch.object(revit_server, "channel", channel):
            await revit_server.mcp.call_tool(
                "revit_aggregate_elements",
                {"groupBy": ["level"], "sumField": "Площадь", "areaScheme": "АР"},
            )
            await revit_server.mcp.call_tool(
                "revit_aggregate_elements",
                {"group_by": ["level"], "sum_field": "Площадь", "area_scheme": "АР"},
            )
            await revit_server.mcp.call_tool(
                "revit_view_summary", {"viewName": "План 1", "document": "QC 0091"}
            )
            await revit_server.mcp.call_tool(
                "revit_export_view", {"view": "План 1", "pixelSize": 2400, "saveTo": "/tmp/plan.png"}
            )
            await revit_server.mcp.call_tool(
                "revit_list_relations", {"relation": "level-rooms", "sourceName": "01"}
            )
            await revit_server.mcp.call_tool(
                "revit_list_relations", {"relation": "level-rooms", "source_name": "01"}
            )

        camel_aggregate, snake_aggregate, view_summary, export_view, camel_relation, snake_relation = [
            call[0] for call in channel.calls
        ]
        self.assertEqual(camel_aggregate.payload, snake_aggregate.payload)
        self.assertEqual(camel_aggregate.payload["numericField"], "Площадь")
        self.assertEqual(view_summary.payload["view"], "План 1")
        self.assertEqual(view_summary.payload["targetDocument"], "QC 0091")
        self.assertEqual(export_view.payload["pixelSize"], 2400)
        self.assertEqual(export_view.save_to, "/tmp/plan.png")
        self.assertEqual(camel_relation.payload, snake_relation.payload)
        self.assertEqual(camel_relation.payload["sourceName"], "01")

    async def test_list_instances_reads_processes_without_channel(self) -> None:
        class RecordingHost:
            async def list_revit_instances(self, document):
                self.document = document
                return [{"processId": 42, "documentName": "SampleModel"}]

        host = RecordingHost()
        with patch.object(revit_server, "host", host):
            result = await revit_server.mcp.call_tool(
                "revit_list_instances", {"document": "QC 0091"}
            )

        self.assertEqual(host.document, "QC 0091")
        self.assertIn("42", str(result))

    async def test_list_instances_ignores_stale_instance_file(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(return_value=json.dumps({
            "processes": [],
            "files": [{"name": "instance_42.json", "content": json.dumps({
                "processId": 42,
                "revitVersion": "2023",
                "documentTitle": "Stale model",
                "documentPath": r"C:\Models\Stale.rvt",
                "updatedUtc": "2000-01-01T00:00:00Z",
            })}],
        }))

        self.assertEqual(await host.list_revit_instances(), [])

    async def test_list_instances_reads_files_without_window_titles(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(return_value=json.dumps({
            "processes": [{"processId": 42, "revitVersion": "2023.1"}],
            "files": [{"name": "instance_42.json", "content": json.dumps({
                "processId": 42,
                "revitVersion": "2023",
                "documentTitle": "SampleModel",
                "documentPath": r"C:\Models\SampleModel.rvt",
                "updatedUtc": datetime.now(timezone.utc).isoformat(),
            })}],
        }))

        result = await host.list_revit_instances("Sample")

        self.assertEqual(result[0]["documentName"], "SampleModel")
        self.assertTrue(result[0]["pluginResponding"])
        script = host._run.await_args.args[0]
        self.assertIn("instance_*.json", script)
        self.assertNotIn("MainWindowTitle", script)

    async def test_list_instances_marks_process_when_plugin_does_not_respond(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(return_value=json.dumps({
            "processes": [{"processId": 84, "revitVersion": "2024.2"}],
            "files": [],
        }))

        result = await host.list_revit_instances()

        self.assertEqual(result[0]["documentName"], "")
        self.assertFalse(result[0]["pluginResponding"])

    async def test_package_registers_console_entry_point(self) -> None:
        config = tomllib.loads((MCP_DIRECTORY / "pyproject.toml").read_text())
        self.assertEqual(config["project"]["scripts"]["revit-model-mcp"], "revit_model_mcp.server:main")

    def test_host_configuration(self) -> None:
        self.assertTrue(revit_server.create_host("local").local)
        remote = revit_server.create_host("ssh:revit-host")
        self.assertFalse(remote.local)
        self.assertEqual(remote.host, "revit-host")
        for invalid in ["revit-host", "ssh:", "ssh:-option", "ssh:host;command"]:
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                revit_server.create_host(invalid)

    async def test_redaction_covers_tool_and_instance_responses(self) -> None:
        response = {
            "command": "document-info", "success": True,
            "responder": {"documentPath": r"C:\Models\Sample.rvt"},
            "data": [{"documentPath": r"\\host\share\Linked.rvt", "localPath": "/tmp/view.png"}],
        }
        instances = [{"documentPath": "C:/Models/Sample.rvt"}]
        with (
            patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "1"}),
            patch.object(revit_server.channel, "execute", AsyncMock(return_value=response)),
            patch.object(revit_server.host, "list_revit_instances", AsyncMock(return_value=instances)),
        ):
            result = await revit_server.revit_document_info()
            listed = await revit_server.revit_list_instances()
        self.assertEqual(result["responder"]["documentPath"], "Sample.rvt")
        self.assertEqual(result["data"][0]["documentPath"], "Linked.rvt")
        self.assertEqual(result["data"][0]["localPath"], "/tmp/view.png")
        self.assertEqual(listed[0]["documentPath"], "Sample.rvt")
        self.assertEqual(response["responder"]["documentPath"], r"C:\Models\Sample.rvt")
        with patch.dict(os.environ, {"REVIT_MCP_REDACT_PATHS": "0"}):
            self.assertEqual(revit_server.redact_model_paths(response), response)

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
            patch.dict(os.environ, {
                "REVIT_MCP_HOST": "ssh:revit-host",
                "REVIT_MCP_CHANNEL_DIR": r"C:\RevitChannel",
                "REVIT_MCP_REDACT_PATHS": "1",
            }),
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
