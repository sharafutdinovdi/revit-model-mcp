from __future__ import annotations

import asyncio
import base64
import json
import os
import shutil
import stat
import tempfile
import unittest
from pathlib import Path
from unittest.mock import AsyncMock, patch

from revit_model_mcp.revit_channel import (
    DEFAULT_PICKUP_TIMEOUT_SECONDS,
    ActivationError,
    JobPickupStatus,
    JobPickupTimeoutError,
    PluginResponseError,
    ReadJob,
    ResponseParseError,
    ResponseTimeoutError,
    RevitChannelError,
    RevitNotRunningError,
    RevitReadChannel,
    SshUnavailableError,
    parse_response,
)
from revit_model_mcp.ssh_host import (
    ACTIVATION_DELAY_SECONDS,
    POLL_INTERVAL_SECONDS,
    RELAY_CONNECTION_LIMIT,
    RELAY_WINDOW_SECONDS,
    RemoteCommandError,
    RemoteCommandTimeoutError,
    SshPowerShellHost,
)

SUCCESS_RESPONSE = json.dumps(
    {
        "command": "document-info",
        "success": True,
        "data": {"fileName": "Sample Model.rvt", "viewCount": 84},
        "elapsedMs": 251,
    },
    ensure_ascii=False,
)


class FakeSshProcess:
    def __init__(
        self,
        returncode: int | None,
        stdout: bytes = b"",
        stderr: bytes = b"",
        hang: bool = False,
    ) -> None:
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr
        self.hang = hang
        self.killed = False

    async def communicate(self) -> tuple[bytes, bytes]:
        if self.hang and not self.killed:
            await asyncio.Future()
        return self.stdout, self.stderr

    def kill(self) -> None:
        self.killed = True
        self.returncode = -9


class FakeRemoteHost:
    def __init__(self) -> None:
        self.prepare_error: BaseException | None = None
        self.activation_error: BaseException | None = None
        self.trigger_taken = True
        self.trigger_present = False
        self.activation_attempts = 0
        self.pickup_elapsed_seconds = 6.4
        self.response_name: str | None = "response_new_document-info.json"
        self.response_content = SUCCESS_RESPONSE
        self.known_responses = {"response_old_document-info.json"}
        self.written_name: str | None = None
        self.written_content: str | None = None
        self.published_name: str | None = None
        self.deleted_names: list[str] = []
        self.pickup_timeout: float | None = None
        self.response_timeout: float | None = None
        self.events: list[str] = []

    async def prepare_job(self, name: str, content: str, command: str) -> set[str]:
        self.events.append("prepare")
        if self.prepare_error:
            raise self.prepare_error
        self.written_name = name
        self.written_content = content
        self.published_name = name
        return self.known_responses

    async def wait_until_trigger_is_gone(self, timeout_seconds: float) -> JobPickupStatus:
        self.events.append("pickup")
        self.pickup_timeout = timeout_seconds
        if self.activation_error:
            raise self.activation_error
        return JobPickupStatus(
            self.trigger_taken,
            self.activation_attempts,
            self.trigger_present,
            self.pickup_elapsed_seconds,
        )

    async def wait_for_new_response(
        self,
        command: str,
        known_names: set[str],
        timeout_seconds: float,
        correlation_id: str | None = None,
    ) -> str | None:
        self.events.append("response")
        self.response_timeout = timeout_seconds
        return self.response_name

    async def finish_job(self, response_name, cleanup_names, download_artifact, save_to):
        self.events.append("finish")
        self.deleted_names.extend(cleanup_names)
        return self.response_content, None

    async def delete_files(self, names: list[str]) -> None:
        self.events.append("delete")
        self.deleted_names.extend(names)


class ReadJobTests(unittest.TestCase):
    def test_forms_ping_job(self) -> None:
        self.assertEqual(ReadJob.ping().payload, {"command": "ping"})

    def test_forms_document_info_job(self) -> None:
        self.assertEqual(ReadJob.document_info().payload, {"command": "document-info"})

    def test_forms_list_views_job_with_optional_filters(self) -> None:
        self.assertEqual(
            ReadJob.list_views(" FloorPlan ", " Plan ").payload,
            {"command": "list-views", "viewType": "FloorPlan", "nameContains": "Plan"},
        )
        self.assertEqual(ReadJob.list_views().payload, {"command": "list-views"})

    def test_forms_view_summary_job(self) -> None:
        self.assertEqual(
            ReadJob.view_summary(" Level 1 Plan ").payload,
            {"command": "view-summary", "view": "Level 1 Plan"},
        )

    def test_forms_view_elements_job(self) -> None:
        self.assertEqual(
            ReadJob.view_elements(
                "Level 1 Plan", ["Walls", " Doors ", "walls", ""], 25, 10
            ).payload,
            {
                "command": "view-elements",
                "view": "Level 1 Plan",
                "categories": ["Walls", "Doors"],
                "offset": 25,
                "limit": 10,
            },
        )

    def test_forms_element_details_job(self) -> None:
        self.assertEqual(
            ReadJob.element_details(11327511).payload,
            {"command": "element-details", "id": 11327511},
        )

    def test_forms_view_warnings_job(self) -> None:
        self.assertEqual(
            ReadJob.view_warnings("Level 1 Plan").payload,
            {"command": "view-warnings", "view": "Level 1 Plan"},
        )

    def test_round_trips_non_ascii_mixed_scripts_as_compact_utf8_json(self) -> None:
        self.assertEqual(
            json.loads(ReadJob.view_summary("Plan 東京 Δ").to_json())["view"], "Plan 東京 Δ"
        )
        self.assertEqual(
            ReadJob.view_summary("Plan 東京 Δ").to_json(),
            '{"command":"view-summary","view":"Plan 東京 Δ"}',
        )

    def test_adds_optional_document_address_without_mutating_job(self) -> None:
        job = ReadJob.document_info()
        addressed = job.for_document(" SampleModel ")
        self.assertEqual(addressed.payload["targetDocument"], "SampleModel")
        self.assertNotIn("targetDocument", job.payload)
        self.assertIs(job.for_document(None), job)

    def test_rejects_invalid_paging_and_id_before_ssh(self) -> None:
        with self.assertRaisesRegex(Exception, "offset"):
            ReadJob.view_elements("Plan", offset=-1)
        with self.assertRaisesRegex(Exception, "limit"):
            ReadJob.view_elements("Plan", limit=0)
        with self.assertRaisesRegex(Exception, "positive"):
            ReadJob.element_details(0)


class ResponseTests(unittest.TestCase):
    def test_parses_successful_response_without_changing_envelope(self) -> None:
        parsed = parse_response(SUCCESS_RESPONSE, "document-info")
        self.assertEqual(parsed["data"]["fileName"], "Sample Model.rvt")
        self.assertEqual(parsed["elapsedMs"], 251)

    def test_returns_plugin_error_message_as_is(self) -> None:
        content = json.dumps(
            {
                "command": "document-info",
                "success": False,
                "message": "No active Revit document.",
                "elapsedMs": 0,
            }
        )
        with self.assertRaises(PluginResponseError) as raised:
            parse_response(content, "document-info")
        self.assertEqual(str(raised.exception), "No active Revit document.")

    def test_returns_unsuccessful_batch_response_with_data_intact(self) -> None:
        content = json.dumps(
            {
                "command": "batch",
                "success": False,
                "data": {"steps": [{"success": True}, {"success": False}], "failedStep": 1},
            }
        )

        parsed = parse_response(content, "batch")

        self.assertEqual(
            parsed["data"],
            {"steps": [{"success": True}, {"success": False}], "failedStep": 1},
        )

    def test_reports_malformed_json(self) -> None:
        with self.assertRaisesRegex(ResponseParseError, "could not be parsed as JSON"):
            parse_response("not-json", "document-info")

    def test_reports_wrong_response_contract(self) -> None:
        with self.assertRaisesRegex(ResponseParseError, "expected command"):
            parse_response('{"command":"list-views","success":true,"data":{}}', "document-info")
        with self.assertRaisesRegex(ResponseParseError, "field success"):
            parse_response('{"command":"document-info","data":{}}', "document-info")


class SshHostErrorMappingTests(unittest.IsolatedAsyncioTestCase):
    async def test_prepares_job_with_one_remote_command(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(
            return_value=json.dumps(
                {
                    "revitRunning": True,
                    "responses": ["response_old_ping.json"],
                    "published": True,
                    "channelBusy": False,
                }
            )
        )

        responses = await host.prepare_job("mcp_test.tmp", '{"command":"ping"}', "ping")
        self.assertEqual(responses, {"response_old_ping.json"})
        host._run.assert_awaited_once()
        script = host._run.await_args.args[0]
        self.assertIn("Get-Process Revit", script)
        self.assertIn("Get-ChildItem", script)
        self.assertIn("WriteAllBytes", script)
        self.assertIn("[IO.File]::Move", script)

    async def test_prepare_keeps_revit_and_busy_errors_distinct(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(
            side_effect=[
                '{"revitRunning":false,"responses":[],"published":false,"channelBusy":false}',
                '{"revitRunning":true,"responses":[],"published":false,"channelBusy":true}',
            ]
        )
        with self.assertRaisesRegex(RevitNotRunningError, "Revit is not running"):
            await host.prepare_job("first.tmp", "{}", "ping")
        with self.assertRaisesRegex(RevitChannelError, "RevitModelMcp channel is busy"):
            await host.prepare_job("second.tmp", "{}", "ping")
        self.assertEqual(host._run.await_count, 2)

    async def test_finishes_job_with_one_remote_command(self) -> None:
        host = SshPowerShellHost()
        encoded = base64.b64encode(SUCCESS_RESPONSE.encode()).decode()
        package = {"response": encoded, "artifactName": None, "artifact": None}
        host._run = AsyncMock(return_value=json.dumps(package))
        response_name = "response_new_document-info.json"
        content, local_path = await host.finish_job(
            response_name, ["mcp_test.tmp", response_name], False, None
        )
        self.assertEqual(content, SUCCESS_RESPONSE)
        self.assertIsNone(local_path)
        host._run.assert_awaited_once()
        script = host._run.await_args.args[0]
        self.assertIn("Read-ResponseBytes $path", script)
        self.assertIn("[IO.FileShare]::Delete", script)
        self.assertIn("Remove-Item", script)

    async def test_connection_budget_delays_sixth_start(self) -> None:
        class FakeLoop:
            now = 0.0

            def time(self) -> float:
                return self.now

        loop = FakeLoop()
        host = SshPowerShellHost()

        async def advance(seconds: float) -> None:
            loop.now += seconds

        with (
            patch("revit_model_mcp.ssh_host.asyncio.get_running_loop", return_value=loop),
            patch("revit_model_mcp.ssh_host.asyncio.sleep", side_effect=advance) as sleep,
        ):
            for _ in range(RELAY_CONNECTION_LIMIT + 1):
                await host._reserve_connection()

        sleep.assert_awaited_once()
        self.assertGreaterEqual(loop.now, RELAY_WINDOW_SECONDS)
        self.assertLessEqual(
            2 + RELAY_WINDOW_SECONDS / POLL_INTERVAL_SECONDS,
            RELAY_CONNECTION_LIMIT,
        )

    async def test_reports_connection_not_established(self) -> None:
        process = FakeSshProcess(
            255,
            stderr=b"ssh: connect to host revit-host port 22: Connection timed out",
        )
        host = SshPowerShellHost()

        with patch(
            "revit_model_mcp.ssh_host.asyncio.create_subprocess_exec",
            new=AsyncMock(return_value=process),
        ):
            with self.assertRaises(SshUnavailableError) as raised:
                await host._run("'ok'")

        message = str(raised.exception)
        self.assertIn("SSH connection", message)
        self.assertIn("was not established", message)
        self.assertIn("tunnel", message)

    async def test_reports_running_command_timeout_separately(self) -> None:
        process = FakeSshProcess(None, hang=True)
        host = SshPowerShellHost()

        with patch(
            "revit_model_mcp.ssh_host.asyncio.create_subprocess_exec",
            new=AsyncMock(return_value=process),
        ):
            with self.assertRaises(RemoteCommandTimeoutError) as raised:
                await host._run("Start-Sleep 60", timeout_seconds=0.001)

        message = str(raised.exception)
        self.assertIn("did not complete", message)
        self.assertIn("command execution timeout", message)
        self.assertIn("not evidence that SSH is unavailable", message)
        self.assertTrue(process.killed)

    async def test_reports_ssh_process_error_without_claiming_connection_failure(
        self,
    ) -> None:
        process = FakeSshProcess(255, stderr=b"Connection reset by peer")
        host = SshPowerShellHost()

        with patch(
            "revit_model_mcp.ssh_host.asyncio.create_subprocess_exec",
            new=AsyncMock(return_value=process),
        ):
            with self.assertRaises(SshUnavailableError) as raised:
                await host._run("'ok'")

        message = str(raised.exception)
        self.assertIn("ssh for host localhost failed", message)
        self.assertIn("The connection may have been established", message)
        self.assertNotIn("was not established", message)

    async def test_reports_remote_command_error_with_exit_code(self) -> None:
        process = FakeSshProcess(17, stderr=b"PowerShell failed")
        host = SshPowerShellHost()

        with patch(
            "revit_model_mcp.ssh_host.asyncio.create_subprocess_exec",
            new=AsyncMock(return_value=process),
        ):
            with self.assertRaises(RemoteCommandError) as raised:
                await host._run("exit 17")

        self.assertIn("Transport command", str(raised.exception))
        self.assertIn("code 17", str(raised.exception))

    async def test_trigger_polling_stops_at_overall_deadline(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(return_value="present")

        with patch("revit_model_mcp.ssh_host.POLL_INTERVAL_SECONDS", 0.001):
            pickup = await host.wait_until_trigger_is_gone(0.005)

        self.assertFalse(pickup.taken)
        self.assertEqual(pickup.activation_attempts, 0)
        self.assertGreaterEqual(host._run.await_count, 1)
        for call in host._run.await_args_list:
            self.assertIn("trigger.txt", call.args[0])
            self.assertNotIn("schtasks.exe", call.args[0])
            self.assertNotIn("while (test-path", call.args[0].casefold())
            self.assertLessEqual(call.kwargs["timeout_seconds"], 10)

    async def test_pickup_activates_once_after_a_minute(self) -> None:
        class FakeLoop:
            now = 0.0

            def time(self) -> float:
                return self.now

        loop = FakeLoop()
        host = SshPowerShellHost()
        host._run = AsyncMock(return_value="present")

        async def advance(seconds: float) -> None:
            loop.now += seconds

        with (
            patch("revit_model_mcp.ssh_host.ACTIVATION_TASK", "ActivateRevit"),
            self.assertLogs("revit_model_mcp.ssh_host", level="WARNING") as logs,
        ):
            with (
                patch("revit_model_mcp.ssh_host.asyncio.get_running_loop", return_value=loop),
                patch("revit_model_mcp.ssh_host.asyncio.sleep", side_effect=advance),
            ):
                pickup = await host.wait_until_trigger_is_gone(70)

        self.assertFalse(pickup.taken)
        self.assertEqual(pickup.activation_attempts, 1)
        scripts = [call.args[0] for call in host._run.await_args_list]
        activation_index = int(ACTIVATION_DELAY_SECONDS / POLL_INTERVAL_SECONDS)
        self.assertEqual(sum("schtasks.exe" in script for script in scripts), 1)
        self.assertTrue(all("schtasks.exe" not in script for script in scripts[:activation_index]))
        self.assertIn("schtasks.exe", scripts[activation_index])
        self.assertLess(
            scripts[activation_index].index("Test-Path"),
            scripts[activation_index].index("schtasks.exe"),
        )
        self.assertTrue(all("trigger.txt" in script for script in scripts))
        self.assertEqual(len(scripts), 7)
        self.assertIn("Revit window will be restored", " ".join(logs.output))

    async def test_trigger_polling_retries_one_broken_poll(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(
            side_effect=[
                SshUnavailableError("SSH command failed"),
                "gone",
            ]
        )

        with patch("revit_model_mcp.ssh_host.POLL_INTERVAL_SECONDS", 0):
            pickup = await host.wait_until_trigger_is_gone(1)

        self.assertTrue(pickup.taken)
        self.assertEqual(pickup.activation_attempts, 0)
        self.assertEqual(host._run.await_count, 2)

    async def test_response_polling_retries_one_timed_out_poll(self) -> None:
        host = SshPowerShellHost()
        host._run = AsyncMock(
            side_effect=[
                RemoteCommandTimeoutError("The quick check timed out"),
                "response_new_ping.json",
            ]
        )

        with patch("revit_model_mcp.ssh_host.POLL_INTERVAL_SECONDS", 0):
            response = await host.wait_for_new_response("ping", set(), 1)

        self.assertEqual(response, "response_new_ping.json")
        self.assertEqual(host._run.await_count, 2)


class ChannelErrorTests(unittest.IsolatedAsyncioTestCase):
    async def test_waits_for_success_after_accepted_response(self) -> None:
        for placeholder in (
            {"data": "accepted", "message": "Command accepted and running.", "elapsedMs": 0},
            {"message": "Command accepted and running.", "elapsedMs": 0},
            {"data": "accepted", "elapsedMs": 0},
        ):
            with self.subTest(placeholder=placeholder):
                remote = FakeRemoteHost()
                accepted = json.dumps(
                    {"command": "document-info", "success": False, "partial": True, **placeholder}
                )
                remote.finish_job = AsyncMock(
                    side_effect=[(accepted, None), (SUCCESS_RESPONSE, None)]
                )
                with patch("revit_model_mcp.revit_channel.asyncio.sleep", new=AsyncMock()):
                    result = await RevitReadChannel(remote).execute(ReadJob.document_info())
                self.assertTrue(result["success"])
                self.assertEqual(result["data"]["viewCount"], 84)
                self.assertEqual(remote.finish_job.await_count, 2)
                for call in remote.finish_job.await_args_list:
                    self.assertEqual(call.args, (remote.response_name, [], False, None))
                self.assertIn(remote.response_name, remote.deleted_names)

    async def test_waits_for_error_after_accepted_response(self) -> None:
        remote = FakeRemoteHost()
        accepted = json.dumps(
            {
                "command": "document-info",
                "success": False,
                "partial": True,
                "data": "accepted",
                "message": "Command accepted and running.",
                "elapsedMs": 0,
            }
        )
        error = json.dumps(
            {
                "command": "document-info",
                "success": False,
                "partial": False,
                "message": "The document was closed.",
                "elapsedMs": 20,
            }
        )
        remote.finish_job = AsyncMock(side_effect=[(accepted, None), (error, None)])
        with patch("revit_model_mcp.revit_channel.asyncio.sleep", new=AsyncMock()):
            with self.assertRaisesRegex(PluginResponseError, "The document was closed"):
                await RevitReadChannel(remote).execute(ReadJob.document_info())
        self.assertEqual(remote.finish_job.await_count, 2)
        self.assertIn(remote.response_name, remote.deleted_names)

    async def test_accepted_response_times_out_with_original_response_budget(self) -> None:
        class FakeLoop:
            now = 0.0

            def time(self) -> float:
                return self.now

        loop = FakeLoop()
        remote = FakeRemoteHost()
        remote.response_content = json.dumps(
            {
                "command": "document-info",
                "success": False,
                "partial": True,
                "data": "accepted",
                "message": "Command accepted and running.",
                "elapsedMs": 0,
            }
        )

        async def discover(command, known_names, timeout_seconds, correlation_id=None):
            loop.now += 2
            return remote.response_name

        async def advance(seconds):
            self.assertNotIn(remote.response_name, remote.deleted_names)
            loop.now += seconds

        remote.wait_for_new_response = AsyncMock(side_effect=discover)
        with (
            patch("revit_model_mcp.revit_channel.asyncio.get_running_loop", return_value=loop),
            patch("revit_model_mcp.revit_channel.asyncio.sleep", side_effect=advance),
        ):
            with self.assertRaises(ResponseTimeoutError):
                await RevitReadChannel(remote).execute(ReadJob.document_info(), timeout_seconds=3)
        self.assertEqual(loop.now, 3)
        self.assertIn(remote.response_name, remote.deleted_names)
        self.assertIn(remote.written_name, remote.deleted_names)

    async def test_returns_genuine_partial_after_accepted_response(self) -> None:
        remote = FakeRemoteHost()
        accepted = json.dumps(
            {
                "command": "list-views",
                "success": False,
                "partial": True,
                "data": "accepted",
                "message": "Command accepted and running.",
                "elapsedMs": 0,
            }
        )
        partial = {
            "command": "list-views",
            "success": False,
            "partial": True,
            "data": {"views": [{"name": "L1"}]},
            "message": "The 60-second limit was reached.",
            "elapsedMs": 60000,
        }
        remote.finish_job = AsyncMock(side_effect=[(accepted, None), (json.dumps(partial), None)])
        with patch("revit_model_mcp.revit_channel.asyncio.sleep", new=AsyncMock()):
            result = await RevitReadChannel(remote).execute(ReadJob.list_views())
        self.assertEqual(result, partial)
        self.assertIn(remote.response_name, remote.deleted_names)

    async def test_ignores_another_job_and_awaits_correlated_terminal_response(self) -> None:
        remote = FakeRemoteHost()
        other_name = "response_other_document-info.json"
        own_name = "response_own_document-info.json"
        remote.wait_for_new_response = AsyncMock(side_effect=[other_name, own_name, own_name])
        reads = 0

        async def read_response(name, cleanup_names, download_artifact, save_to):
            nonlocal reads
            reads += 1
            identity = json.loads(remote.written_content)["correlationId"]
            envelope = json.loads(SUCCESS_RESPONSE)
            envelope["correlationId"] = "other-job" if name == other_name else identity
            envelope["partial"] = reads == 2
            envelope["message"] = "Reading model elements." if reads == 2 else "Done."
            return json.dumps(envelope), None

        remote.finish_job = AsyncMock(side_effect=read_response)
        with patch("revit_model_mcp.revit_channel.asyncio.sleep", new=AsyncMock()):
            result = await RevitReadChannel(remote).execute(ReadJob.document_info())
        identity = json.loads(remote.written_content)["correlationId"]
        self.assertEqual(result["correlationId"], identity)
        self.assertFalse(result["partial"])
        self.assertEqual(reads, 3)
        self.assertNotIn(other_name, remote.deleted_names)
        self.assertIn(own_name, remote.deleted_names)
        self.assertTrue(
            all(call.args[3] == identity for call in remote.wait_for_new_response.await_args_list)
        )

    async def test_reusing_job_generates_a_fresh_id_without_mutating_payload(self) -> None:
        remote = FakeRemoteHost()
        job = ReadJob.document_info()
        channel = RevitReadChannel(remote)
        await channel.execute(job)
        first = json.loads(remote.written_content)["correlationId"]
        await channel.execute(job)
        second = json.loads(remote.written_content)["correlationId"]
        self.assertNotEqual(first, second)
        self.assertEqual(job.payload, {"command": "document-info"})

    async def test_reports_ssh_unavailable(self) -> None:
        remote = FakeRemoteHost()
        remote.prepare_error = SshUnavailableError("Host revit-host is unreachable over SSH.")

        with self.assertRaisesRegex(SshUnavailableError, "unreachable over SSH"):
            await RevitReadChannel(remote).execute(ReadJob.document_info())

    async def test_reports_revit_not_running(self) -> None:
        remote = FakeRemoteHost()
        remote.prepare_error = RevitNotRunningError("Revit is not running on host revit-host.")

        with self.assertRaisesRegex(RevitNotRunningError, "Revit is not running"):
            await RevitReadChannel(remote).execute(ReadJob.document_info())

    async def test_reports_activation_failure(self) -> None:
        remote = FakeRemoteHost()
        remote.activation_error = ActivationError(
            "Failed to activate the Revit window through the ActivateRevit task."
        )

        with self.assertRaisesRegex(ActivationError, "ActivateRevit"):
            await RevitReadChannel(remote).execute(ReadJob.document_info())

        self.assertIsNotNone(remote.written_name)
        self.assertIn(remote.written_name, remote.deleted_names)

    async def test_reports_measured_job_pickup_timeout_and_keeps_trigger(self) -> None:
        remote = FakeRemoteHost()
        remote.trigger_taken = False
        remote.trigger_present = True
        remote.activation_attempts = 1
        remote.pickup_elapsed_seconds = 7.2

        with self.assertRaises(JobPickupTimeoutError) as raised:
            await RevitReadChannel(remote).execute(
                ReadJob.document_info(), pickup_timeout_seconds=7
            )

        message = str(raised.exception)
        self.assertIn("Job was not picked up within 7.2 s", message)
        self.assertIn("Revit activation attempts: 1", message)
        self.assertIn("trigger.txt is still present", message)
        self.assertIn("may still execute later", message)
        self.assertNotIn("trigger.txt", remote.deleted_names)

    async def test_reports_response_timeout_for_long_command(self) -> None:
        remote = FakeRemoteHost()
        remote.response_name = None

        with self.assertRaises(ResponseTimeoutError) as raised:
            await RevitReadChannel(remote).execute(
                ReadJob.view_elements("Level 1 Plan", limit=100), timeout_seconds=9
            )

        self.assertIn("The add-in picked up the job", str(raised.exception))
        self.assertIn("increase timeout_seconds", str(raised.exception))

    async def test_propagates_plugin_error_and_cleans_response(self) -> None:
        remote = FakeRemoteHost()
        remote.response_content = json.dumps(
            {
                "command": "document-info",
                "success": False,
                "message": "The add-in is busy.",
                "elapsedMs": 2,
            }
        )

        with self.assertRaises(PluginResponseError) as raised:
            await RevitReadChannel(remote).execute(ReadJob.document_info())

        self.assertEqual(str(raised.exception), "The add-in is busy.")
        self.assertIn(remote.response_name, remote.deleted_names)

    async def test_retries_unparseable_response(self) -> None:
        remote = FakeRemoteHost()
        remote.finish_job = AsyncMock(side_effect=[("{broken", None), (SUCCESS_RESPONSE, None)])
        with patch("revit_model_mcp.revit_channel.asyncio.sleep", new=AsyncMock()):
            response = await RevitReadChannel(remote).execute(ReadJob.document_info())
        self.assertTrue(response["success"])
        self.assertEqual(remote.finish_job.await_count, 2)

    async def test_returns_response_and_cleans_temporary_files(self) -> None:
        remote = FakeRemoteHost()

        response = await RevitReadChannel(remote).execute(
            ReadJob.document_info(), timeout_seconds=120
        )

        self.assertEqual(response["data"]["viewCount"], 84)
        self.assertEqual(
            remote.events,
            [
                "prepare",
                "pickup",
                "response",
                "finish",
                "delete",
            ],
        )
        payload = json.loads(remote.written_content or "{}")
        self.assertEqual(payload["command"], "document-info")
        self.assertRegex(payload["correlationId"], r"^[0-9a-f]{32}$")
        self.assertEqual(remote.written_name, f"mcp_{payload['correlationId']}.tmp")
        self.assertEqual(remote.published_name, remote.written_name)
        self.assertIn(remote.written_name, remote.deleted_names)
        self.assertIn(remote.response_name, remote.deleted_names)
        self.assertNotIn("trigger.txt", remote.deleted_names)
        self.assertEqual(remote.pickup_timeout, DEFAULT_PICKUP_TIMEOUT_SECONDS)
        self.assertEqual(remote.response_timeout, 120)
        connection_events = remote.events
        self.assertEqual(len(connection_events), 5)
        self.assertLessEqual(len(connection_events), RELAY_CONNECTION_LIMIT)

    async def test_serializes_parallel_calls_to_the_single_trigger(
        self,
    ) -> None:
        events: list[str] = []

        class BlockingRemote(FakeRemoteHost):
            async def prepare_job(self, name: str, content: str, command: str) -> set[str]:
                events.append("prepare")
                await asyncio.sleep(0.01)
                return await super().prepare_job(name, content, command)

            async def delete_files(self, names: list[str]) -> None:
                events.append("delete")
                await super().delete_files(names)

        remote = BlockingRemote()
        channel = RevitReadChannel(remote)

        await asyncio.gather(
            channel.execute(ReadJob.document_info()),
            channel.execute(ReadJob.document_info()),
        )

        self.assertEqual(events, ["prepare", "delete", "prepare", "delete"])


class HostConfigurationTests(unittest.IsolatedAsyncioTestCase):
    async def test_local_mode_runs_powershell_without_ssh(self) -> None:
        host = SshPowerShellHost("local", local=True)
        process = FakeSshProcess(0, stdout=b"ok")
        with patch(
            "revit_model_mcp.ssh_host.asyncio.create_subprocess_exec",
            AsyncMock(return_value=process),
        ) as start:
            result = await host._run("'ok'")
        self.assertEqual(result, "ok")
        self.assertEqual(start.call_args.args[0], "powershell.exe")
        self.assertNotIn("ssh", start.call_args.args)
        self.assertEqual(base64.b64decode(start.call_args.args[-1]).decode("utf-16le"), "'ok'")

    async def test_missing_activation_task_only_checks_trigger(self) -> None:
        host = SshPowerShellHost()

        async def poll(script, pending, timeout, **kwargs):
            self.assertNotIn("schtasks", script(61))
            return "gone", 1, 61

        host._poll_for_change = poll
        with patch("revit_model_mcp.ssh_host.ACTIVATION_TASK", ""):
            status = await host.wait_until_trigger_is_gone(120)
        self.assertEqual(status.activation_attempts, 0)

    def test_channel_and_task_paths_escape_powershell_quotes(self) -> None:
        import os

        from revit_model_mcp.ssh_host import _ps_directory

        with patch.dict(os.environ, {"REVIT_MCP_CHANNEL_DIR": "C:\\User's channel"}):
            self.assertEqual(_ps_directory(), "'C:\\User''s channel'")
        with patch("revit_model_mcp.ssh_host.ACTIVATION_TASK", "User's task"):
            self.assertIn("'User''s task'", SshPowerShellHost()._activation_script())


def test_ssh_command_reuses_private_runtime_directory(tmp_path, monkeypatch):
    directory = tmp_path / "runtime"
    directory.mkdir(mode=0o755)
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(directory))
    monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
    monkeypatch.delenv("REVIT_MCP_SSH_OPTIONS", raising=False)
    host = SshPowerShellHost("revit-host")

    command = host._build_command("'ok'")

    assert command == [
        "ssh",
        "-o",
        "BatchMode=yes",
        "-o",
        "ConnectTimeout=45",
        "-o",
        "ControlMaster=auto",
        "-o",
        f"ControlPath={directory}/mux-%C",
        "-o",
        "ControlPersist=600",
        "revit-host",
        "powershell.exe",
        "-NoProfile",
        "-NonInteractive",
        "-EncodedCommand",
        base64.b64encode("'ok'".encode("utf-16le")).decode("ascii"),
    ]
    assert host._build_command("'ok'") == command
    if os.name != "nt":
        assert stat.S_IMODE(directory.stat().st_mode) == 0o700


def test_ssh_command_falls_back_to_user_cache(tmp_path, monkeypatch):
    monkeypatch.delenv("XDG_RUNTIME_DIR", raising=False)
    monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
    monkeypatch.delenv("REVIT_MCP_SSH_OPTIONS", raising=False)
    with patch("revit_model_mcp.ssh_host.Path.home", return_value=tmp_path):
        command = SshPowerShellHost()._build_command("'ok'")
    directory = Path("/tmp") / f"revit-model-mcp-{getattr(os, 'getuid', lambda: 'user')()}"
    assert f"ControlPath={directory}/mux-%C" in command
    assert directory.is_dir()
    if os.name != "nt":
        assert stat.S_IMODE(directory.stat().st_mode) == 0o700


def test_ssh_command_can_disable_mux_and_append_options(monkeypatch):
    monkeypatch.setenv("REVIT_MCP_SSH_MUX", "0")
    monkeypatch.setenv("REVIT_MCP_SSH_OPTIONS", '-p 2222 -o "IdentityFile=/keys/revit key"')
    with patch("revit_model_mcp.ssh_host.Path.mkdir") as mkdir:
        command = SshPowerShellHost("revit-host")._build_command("'ok'")
    mkdir.assert_not_called()
    assert not any(option.startswith("Control") for option in command)
    assert command[5:11] == [
        "-p",
        "2222",
        "-o",
        "IdentityFile=/keys/revit key",
        "revit-host",
        "powershell.exe",
    ]


def test_ssh_extra_options_follow_mux_options(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(tmp_path))
    monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
    monkeypatch.setenv("REVIT_MCP_SSH_OPTIONS", "-o ServerAliveInterval=30")
    command = SshPowerShellHost("revit-host")._build_command("'ok'")
    assert command[10:14] == [
        "ControlPersist=600",
        "-o",
        "ServerAliveInterval=30",
        "revit-host",
    ]


def test_local_command_ignores_ssh_settings(monkeypatch):
    monkeypatch.setenv("REVIT_MCP_SSH_OPTIONS", "'invalid shell quoting")
    with patch("revit_model_mcp.ssh_host.Path.mkdir") as mkdir:
        command = SshPowerShellHost(local=True)._build_command("'ok'")
    mkdir.assert_not_called()
    assert command[:4] == ["powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand"]


if __name__ == "__main__":
    unittest.main()


@unittest.skipUnless(
    shutil.which("pwsh"), "PowerShell is required for file selection integration tests"
)
class ResponseSelectionTests(unittest.IsolatedAsyncioTestCase):
    async def test_selects_matching_id_before_newer_legacy_and_ignores_tmp_and_other_ids(self):
        with tempfile.TemporaryDirectory() as directory:

            async def run_script(script, timeout_seconds=60):
                process = await asyncio.create_subprocess_exec(
                    shutil.which("pwsh"),
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    script,
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.PIPE,
                )
                stdout, stderr = await process.communicate()
                self.assertEqual(process.returncode, 0, stderr.decode())
                return stdout.decode().strip()

            def publish(name, identity=None):
                body = {"command": "ping", "success": True, "partial": False, "data": "pong"}
                if identity is not None:
                    body["correlationId"] = identity
                Path(directory, name).write_text(json.dumps(body))
                return name

            own = publish("response_20260916_120000_000_ping_job-24.json", "job-24")
            legacy = publish("response_20260916_120001_000_ping_01.json")
            publish("response_20260916_120002_000_ping_other-job.json", "other-job")
            Path(directory, "response_20260916_120003_000_ping_job-24.json.tmp").write_text(
                "{broken"
            )
            with patch.dict(os.environ, {"REVIT_MCP_CHANNEL_DIR": directory}):
                for local in (False, True):
                    host = SshPowerShellHost("local" if local else "test-host")
                    host._run = AsyncMock(side_effect=run_script)
                    self.assertEqual(
                        await host.wait_for_new_response("ping", set(), 10, "job-24"), own
                    )
                    self.assertEqual(
                        await host.wait_for_new_response("ping", {own}, 10, "job-24"), legacy
                    )
                    content, artifact = await host.finish_job(own, [], False, None)
                    self.assertEqual(json.loads(content)["correlationId"], "job-24")
                    self.assertIsNone(artifact)
                Path(directory, own).unlink()
                Path(directory, legacy).unlink()
                # A matching JSON id also works with an old filename.
                parsed = publish("response_20260916_120004_000_ping.json", "job-24")
                self.assertEqual(
                    await host.wait_for_new_response("ping", set(), 10, "job-24"), parsed
                )
                Path(directory, parsed).unlink()
                self.assertIsNone(await host.wait_for_new_response("ping", set(), 2, "job-24"))
