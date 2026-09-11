from __future__ import annotations

import base64
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock

from revit_model_mcp.revit_channel import JobPickupStatus, ReadJob, RevitReadChannel
from revit_model_mcp.ssh_host import SshPowerShellHost


EXPORT_RESPONSE = json.dumps(
    {
        "command": "export-view",
        "success": True,
        "data": {
            "fileName": "view_20260817_120000_000_42.png",
            "width": 1600,
            "height": 900,
            "sizeBytes": 7,
            "viewName": "План 1",
            "viewType": "FloorPlan",
        },
        "elapsedMs": 812,
    },
    ensure_ascii=False,
)


class ViewExportTests(unittest.IsolatedAsyncioTestCase):
    def test_forms_export_job(self) -> None:
        job = ReadJob.export_view(" 42 ", 2400, "/tmp/plan.png")

        self.assertEqual(
            job.payload,
            {
                "command": "export-view",
                "view": "42",
                "pixelSize": 2400,
                "zoomToFit": True,
            },
        )
        self.assertEqual(job.save_to, "/tmp/plan.png")

    async def test_downloads_image_in_same_remote_read_and_saves_path(self) -> None:
        image = b"pngdata"
        package = json.dumps(
            {
                "response": base64.b64encode(EXPORT_RESPONSE.encode()).decode(),
                "artifactName": "view_20260817_120000_000_42.png",
                "artifact": base64.b64encode(image).decode(),
            }
        )
        host = SshPowerShellHost()
        host._run = AsyncMock(return_value=package)
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "saved.png"
            content, local_path = await host.finish_job(
                "response_export-view.json",
                ["mcp.tmp", "response_export-view.json"],
                True,
                str(target),
            )

            self.assertEqual(content, EXPORT_RESPONSE)
            self.assertEqual(local_path, str(target.resolve()))
            self.assertEqual(target.read_bytes(), image)

        host._run.assert_awaited_once()
        script = host._run.await_args.args[0]
        self.assertIn("response.data.fileName", script)
        self.assertIn("ReadAllBytes($artifactPath)", script)
        self.assertIn("Remove-Item", script)

    async def test_channel_returns_local_path_with_plugin_metadata(self) -> None:
        events: list[str] = []

        class Remote:
            async def prepare_job(self, name, content, command):
                events.append("prepare")
                return set()

            async def wait_until_trigger_is_gone(self, timeout_seconds):
                events.append("pickup")
                return JobPickupStatus(True, 0, False, 0.1)

            async def wait_for_new_response(self, command, known_names, timeout_seconds):
                events.append("response")
                return "response_export-view.json"

            async def finish_job(
                self, response_name, cleanup_names, download_artifact, save_to
            ):
                events.append("finish")
                return EXPORT_RESPONSE, "/tmp/view.png"

            async def delete_files(self, names):
                events.append("delete")
                return None

        response = await RevitReadChannel(Remote()).execute(
            ReadJob.export_view("План 1")
        )

        self.assertEqual(response["data"]["localPath"], "/tmp/view.png")
        self.assertEqual(response["data"]["width"], 1600)
        self.assertEqual(response["data"]["viewType"], "FloorPlan")
        self.assertEqual(events, ["prepare", "pickup", "response", "finish", "delete"])
        self.assertEqual(len([event for event in events if event != "delete"]), 4)


if __name__ == "__main__":
    unittest.main()
