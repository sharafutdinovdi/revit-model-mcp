import unittest
from unittest.mock import AsyncMock, patch

from revit_model_mcp import server


class CoordinatorToolTests(unittest.IsolatedAsyncioTestCase):
    async def test_simple_jobs(self):
        for command in ("model-health", "links-status", "shared-coordinates"):
            with self.subTest(command=command):
                execute = AsyncMock(return_value={"success": True})
                with patch.object(server, "_execute", execute):
                    await server.mcp.call_tool(
                        "revit_" + command.replace("-", "_"),
                        {"timeout_seconds": 41, "pickup_timeout_seconds": 42, "document": "Model"},
                    )
                job, *options = execute.call_args.args
                self.assertEqual(job.command, command)
                self.assertEqual(job.payload, {"command": command})
                self.assertEqual(options, [41, 42, "Model"])

    async def test_fill_job(self):
        execute = AsyncMock(return_value={"success": True})
        with patch.object(server, "_execute", execute):
            await server.mcp.call_tool(
                "revit_parameter_fill_check",
                {
                    "categories": [" Walls ", "Doors"],
                    "parameters": ["Mark", "Comments"],
                    "level": "Level 1",
                    "workset": "Shell",
                    "view": "Plan",
                    "sample_limit": 100,
                    "include_types": False,
                    "document": "Model",
                    "timeout_seconds": 41,
                    "pickup_timeout_seconds": 42,
                },
            )
        job, *options = execute.call_args.args
        self.assertEqual(
            job.payload,
            {
                "command": "parameter-fill-check",
                "categories": ["Walls", "Doors"],
                "parameters": ["Mark", "Comments"],
                "level": "Level 1",
                "workset": "Shell",
                "view": "Plan",
                "sampleLimit": 100,
                "includeTypes": False,
            },
        )
        self.assertEqual(options, [41, 42, "Model"])

    async def test_fill_defaults(self):
        execute = AsyncMock(return_value={"success": True})
        with patch.object(server, "_execute", execute):
            await server.mcp.call_tool(
                "revit_parameter_fill_check",
                {
                    "categories": ["Walls"],
                    "parameters": ["Mark"],
                },
            )
        self.assertEqual(
            execute.call_args.args[0].payload,
            {
                "command": "parameter-fill-check",
                "categories": ["Walls"],
                "parameters": ["Mark"],
                "sampleLimit": 20,
                "includeTypes": True,
            },
        )

    async def test_invalid_fill_inputs_do_not_execute(self):
        for field, value in (
            ("categories", []),
            ("categories", [str(index) for index in range(21)]),
            ("parameters", []),
            ("parameters", [str(index) for index in range(31)]),
            ("sample_limit", 0),
            ("sample_limit", 101),
        ):
            with self.subTest(field=field, value=value):
                execute = AsyncMock()
                with patch.object(server, "_execute", execute):
                    with self.assertRaises(Exception):
                        await server.mcp.call_tool(
                            "revit_parameter_fill_check",
                            {
                                "categories": ["Walls"],
                                "parameters": ["Mark"],
                                field: value,
                            },
                        )
                execute.assert_not_awaited()

    async def test_link_paths_are_redacted_through_execute(self):
        channel = AsyncMock()
        channel.execute.return_value = {"data": {"rvtLinks": [{"path": r"C:\Models\A.rvt"}]}}
        with (
            patch.object(server, "channel", channel),
            patch.dict("os.environ", {"REVIT_MCP_REDACT_PATHS": "1"}),
        ):
            result = await server.revit_links_status(document="Model")
        self.assertEqual(result["data"]["rvtLinks"][0]["path"], "A.rvt")
        self.assertEqual(channel.execute.call_args.args[0].payload["targetDocument"], "Model")
