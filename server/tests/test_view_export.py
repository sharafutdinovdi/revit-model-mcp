from __future__ import annotations

import base64
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock

from revit_model_mcp.artifact_download import save_artifact
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
            "viewName": "Level 1 Plan",
            "viewType": "FloorPlan",
        },
        "elapsedMs": 812,
    },
    ensure_ascii=False,
)
PNG = b"\x89PNG\r\n\x1a\n" + b"image-content"


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
        image = PNG
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
                "response_20260916_120000_000_export-view.json",
                ["mcp.tmp", "response_20260916_120000_000_export-view.json"],
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

    def test_save_artifact_refuses_existing_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "image.png"
            target.write_bytes(b"original")
            package = {"artifactName": "view.png", "artifact": base64.b64encode(PNG).decode()}
            with self.assertRaisesRegex(ValueError, "already exists"):
                save_artifact(package, str(target))
            self.assertEqual(target.read_bytes(), b"original")

    def test_save_artifact_refuses_symlink(self) -> None:
        if os.name == "nt":
            self.skipTest("Creating symlinks requires additional privileges on Windows")
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "image.png"
            destination = Path(directory) / "destination.png"
            destination.write_bytes(b"original")
            target.symlink_to(destination)
            package = {"artifactName": "view.png", "artifact": base64.b64encode(PNG).decode()}
            with self.assertRaisesRegex(ValueError, "already exists"):
                save_artifact(package, str(target))
            self.assertEqual(destination.read_bytes(), b"original")

    def test_save_artifact_refuses_non_png(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "image.png"
            package = {
                "artifactName": "view.png",
                "artifact": base64.b64encode(b"not a PNG").decode(),
            }
            with self.assertRaisesRegex(ValueError, "PNG image"):
                save_artifact(package, str(target))
            self.assertFalse(target.exists())

    def test_save_artifact_requires_png_suffix(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "image.txt"
            package = {"artifactName": "view.png", "artifact": base64.b64encode(PNG).decode()}
            with self.assertRaisesRegex(ValueError, ".png extension"):
                save_artifact(package, str(target))
            self.assertFalse(target.exists())

    def test_save_artifact_writes_valid_png(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            target = Path(directory) / "nested" / "image.PNG"
            package = {"artifactName": "view.png", "artifact": base64.b64encode(PNG).decode()}
            self.assertEqual(save_artifact(package, str(target)), str(target))
            self.assertEqual(target.read_bytes(), PNG)

    async def test_channel_returns_local_path_with_plugin_metadata(self) -> None:
        events: list[str] = []

        class Remote:
            async def select_job(self, job):
                return self, job

            async def prepare_job(self, name, content, command):
                events.append("prepare")
                return set()

            async def wait_until_trigger_is_gone(self, timeout_seconds):
                events.append("pickup")
                return JobPickupStatus(True, 0, False, 0.1)

            async def wait_for_new_response(
                self, command, known_names, timeout_seconds, correlation_id=None
            ):
                events.append("response")
                return "response_export-view.json"

            async def finish_job(self, response_name, cleanup_names, download_artifact, save_to):
                events.append("finish")
                return EXPORT_RESPONSE, "/tmp/view.png"

            async def delete_files(self, names):
                events.append("delete")
                return None

        response = await RevitReadChannel(Remote()).execute(ReadJob.export_view("Level 1 Plan"))

        self.assertEqual(response["data"]["localPath"], "/tmp/view.png")
        self.assertEqual(response["data"]["width"], 1600)
        self.assertEqual(response["data"]["viewType"], "FloorPlan")
        self.assertEqual(events, ["prepare", "pickup", "response", "finish", "finish", "delete"])
        self.assertEqual(len([event for event in events if event != "delete"]), 5)


if __name__ == "__main__":
    unittest.main()
