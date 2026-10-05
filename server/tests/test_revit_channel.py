from __future__ import annotations

import asyncio
import base64
import binascii
import json
import os
import shutil
import stat
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock, patch

import pytest

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
    matches_document,
    parse_response,
    resolve_instance,
)
from revit_model_mcp.ssh_host import (
    ACTIVATION_DELAY_SECONDS,
    POLL_INTERVAL_SECONDS,
    RELAY_CONNECTION_LIMIT,
    RELAY_WINDOW_SECONDS,
    RemoteCommandError,
    RemoteCommandTimeoutError,
    SshPowerShellHost,
    _mux_directory,
    _parse_instance_package,
    _ps_quote,
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
        self.input: bytes | None = None

    async def communicate(self, input: bytes | None = None) -> tuple[bytes, bytes]:
        self.input = input
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

    async def select_job(self, job):
        return self, job

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
        host._instance = {"processId": 42}
        host._run = AsyncMock(
            return_value=json.dumps(
                {
                    "revitRunning": True,
                    "responses": ["response_20260916_120000_000_ping.json"],
                    "published": True,
                    "channelBusy": False,
                }
            )
        )

        responses = await host.prepare_job(
            "mcp_test.tmp", '{"command":"ping","jobId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}', "ping"
        )
        self.assertEqual(responses, {"response_20260916_120000_000_ping.json"})
        host._run.assert_awaited_once()
        script = host._run.await_args.args[0]
        self.assertIn("Get-Process Revit", script)
        self.assertIn("Get-ChildItem", script)
        self.assertIn("WriteAllBytes", script)
        self.assertIn("[IO.File]::Move", script)

    async def test_prepare_ignores_malformed_response_names(self) -> None:
        host = SshPowerShellHost()
        host._instance = {"processId": 42}
        good = "response_20260916_120000_000_ping_job-24.json"
        host._run = AsyncMock(
            return_value=json.dumps(
                {
                    "revitRunning": True,
                    "responses": [good, "response_20260916_120000_000_ping\u2019.json"],
                    "published": True,
                }
            )
        )

        names = await host.prepare_job(
            "mcp_test.tmp", '{"command":"ping","jobId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}', "ping"
        )

        self.assertEqual(names, {good})
        host._run.assert_awaited_once()

    async def test_poll_and_finish_ignore_malformed_response_names(self) -> None:
        host = SshPowerShellHost()
        host._poll_for_change = AsyncMock(
            return_value=("response_20260916_120000_000_ping\u2019.json", 1, 0)
        )
        self.assertIsNone(await host.wait_for_new_response("ping", set(), 5))
        host._run = AsyncMock()
        with self.assertRaisesRegex(ResponseParseError, "invalid file name"):
            await host.finish_job("response_20260916_120000_000_ping\u2019.json", [], False, None)
        host._run.assert_not_awaited()

    async def test_prepare_rejects_stopped_revit_and_accepts_next_job(self) -> None:
        host = SshPowerShellHost()
        host._instance = {"processId": 42}
        host._run = AsyncMock(
            side_effect=[
                '{"revitRunning":false,"responses":[],"published":false,"channelBusy":false}',
                '{"revitRunning":true,"responses":[],"published":true}',
            ]
        )
        with self.assertRaisesRegex(RevitNotRunningError, "Revit is not running"):
            await host.prepare_job(
                "first.tmp", '{"command":"ping","jobId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}', "ping"
            )
        await host.prepare_job(
            "second.tmp", '{"command":"ping","jobId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}', "ping"
        )
        self.assertEqual(host._run.await_count, 2)

    async def test_finishes_job_with_one_remote_command(self) -> None:
        host = SshPowerShellHost()
        encoded = base64.b64encode(SUCCESS_RESPONSE.encode()).decode()
        package = {"response": encoded, "artifactName": None, "artifact": None}
        host._run = AsyncMock(return_value=json.dumps(package))
        response_name = "response_20260916_120000_000_document-info.json"
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

    async def test_unmuxed_connection_budget_delays_sixth_start(self) -> None:
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

    async def test_muxed_starts_do_not_use_connection_budget(self) -> None:
        class FakeLoop:
            now = 0.0

            def time(self) -> float:
                return self.now

        loop = FakeLoop()
        host = SshPowerShellHost()

        with (
            patch("revit_model_mcp.ssh_host.asyncio.get_running_loop", return_value=loop),
            patch("revit_model_mcp.ssh_host.asyncio.sleep", new=AsyncMock()) as sleep,
        ):
            await host._reserve_connection(multiplexed=True)
            self.assertEqual(len(host._connection_starts), 1)
            host._last_successful_ssh_time[0] = loop.time()
            for _ in range(RELAY_CONNECTION_LIMIT + 1):
                await host._reserve_connection(multiplexed=True)

        self.assertEqual(len(host._connection_starts), 1)
        sleep.assert_not_awaited()

    async def test_muxed_start_after_idle_uses_connection_budget(self) -> None:
        class FakeLoop:
            now = 0.0

            def time(self) -> float:
                return self.now

        loop = FakeLoop()
        host = SshPowerShellHost()

        with patch("revit_model_mcp.ssh_host.asyncio.get_running_loop", return_value=loop):
            await host._reserve_connection(multiplexed=True)
            host._last_successful_ssh_time[0] = loop.time()
            loop.now = 540.0
            await host._reserve_connection(multiplexed=True)

        self.assertEqual(list(host._connection_starts), [540.0])

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
        response_name = "response_20260916_120000_000_ping.json"
        host._run = AsyncMock(
            side_effect=[
                RemoteCommandTimeoutError("The quick check timed out"),
                response_name,
            ]
        )

        with patch("revit_model_mcp.ssh_host.POLL_INTERVAL_SECONDS", 0):
            response = await host.wait_for_new_response("ping", set(), 1)

        self.assertEqual(response, response_name)
        self.assertEqual(host._run.await_count, 2)


class ChannelErrorTests(unittest.IsolatedAsyncioTestCase):
    async def test_progress_writes_wait_for_terminal_response(self) -> None:
        progress_messages = (
            "Command accepted; preparing the view element list.",
            "The element list is ready; reading data in batches.",
            "Processing is waiting for the next ExternalEvent call.",
            "New job rejected: RevitModelMcp is busy reading elements.",
            "Processed 3 of 9 views. Current view: 'Level 1'. View elements were not read.",
            "Command accepted and running.",
        )
        jobs = (ReadJob.view_elements("Level 1"), ReadJob.list_views())
        for job in jobs:
            for message in progress_messages:
                for correlated in (False, True):
                    with self.subTest(command=job.command, message=message, correlated=correlated):
                        remote = FakeRemoteHost()
                        reads = 0
                        terminal = {
                            "command": job.command,
                            "success": True,
                            "partial": False,
                            "data": {"complete": True},
                            "elapsedMs": 100,
                        }

                        async def read_response(name, cleanup_names, download_artifact, save_to):
                            nonlocal reads
                            reads += 1
                            self.assertEqual(cleanup_names, [])
                            self.assertNotIn(remote.response_name, remote.deleted_names)
                            identity = json.loads(remote.written_content)["correlationId"]
                            if reads == 1:
                                progress = {
                                    "command": job.command,
                                    "success": False,
                                    "partial": True,
                                    "message": message,
                                    "elapsedMs": 0
                                    if message.startswith("Command accepted")
                                    else 100,
                                    "data": "accepted"
                                    if message == "Command accepted and running."
                                    else {"items": []},
                                }
                                if correlated:
                                    progress["correlationId"] = identity
                                return json.dumps(progress), None
                            if correlated:
                                terminal["correlationId"] = identity
                            return json.dumps(terminal), None

                        remote.finish_job = AsyncMock(side_effect=read_response)
                        with patch("revit_model_mcp.revit_channel.asyncio.sleep", new=AsyncMock()):
                            result = await RevitReadChannel(remote).execute(job)
                        self.assertEqual(result, terminal)
                        self.assertEqual(remote.finish_job.await_count, 2)
                        self.assertIn(remote.response_name, remote.deleted_names)

    async def test_process_models_partial_result_is_terminal(self) -> None:
        remote = FakeRemoteHost()
        partial = {
            "command": "process-models",
            "success": False,
            "partial": True,
            "message": "Processed 0 of 1 models; 1 failed, 0 skipped.",
            "data": {
                "models": [
                    {
                        "path": r"C:\Models\missing.rvt",
                        "status": "failed",
                        "error": "File not found.",
                    }
                ],
                "total": 1,
                "done": 0,
                "failed": 1,
            },
            "elapsedMs": 5,
        }

        async def read_response(name, cleanup_names, download_artifact, save_to):
            partial["correlationId"] = json.loads(remote.written_content)["correlationId"]
            return json.dumps(partial), None

        remote.finish_job = AsyncMock(side_effect=read_response)
        result = await RevitReadChannel(remote)._execute_serial(
            ReadJob("process-models", {"command": "process-models"}), 10, 300
        )

        self.assertEqual(result, partial)
        self.assertEqual(remote.finish_job.await_count, 1)
        self.assertIn(remote.response_name, remote.deleted_names)

    async def test_list_views_progress_name_containing_terminal_text_is_intermediate(self) -> None:
        for elapsed_ms in (0, 61000):
            with self.subTest(elapsed_ms=elapsed_ms):
                remote = FakeRemoteHost()
                reads = 0
                terminal = {
                    "command": "list-views",
                    "success": True,
                    "partial": False,
                    "data": {"views": [{"name": "Level 1"}]},
                }

                async def read_response(name, cleanup_names, download_artifact, save_to):
                    nonlocal reads
                    reads += 1
                    self.assertEqual(cleanup_names, [])
                    self.assertNotIn(remote.response_name, remote.deleted_names)
                    identity = json.loads(remote.written_content)["correlationId"]
                    if reads == 1:
                        return (
                            json.dumps(
                                {
                                    "command": "list-views",
                                    "success": False,
                                    "partial": True,
                                    "message": "Processed 3 of 9 views. Current view: "
                                    "'The 60-second limit was reached'. View elements were not read.",
                                    "elapsedMs": elapsed_ms,
                                    "data": {"views": [{"name": "Level 1"}]},
                                    "correlationId": identity,
                                }
                            ),
                            None,
                        )
                    terminal["correlationId"] = identity
                    return json.dumps(terminal), None

                remote.finish_job = AsyncMock(side_effect=read_response)
                with patch("revit_model_mcp.revit_channel.asyncio.sleep", new=AsyncMock()):
                    result = await RevitReadChannel(remote).execute(ReadJob.list_views())
                self.assertEqual(result, terminal)
                self.assertEqual(remote.finish_job.await_count, 2)
                self.assertIn(remote.response_name, remote.deleted_names)

    async def test_correlated_list_views_limit_partial_is_terminal(self) -> None:
        remote = FakeRemoteHost()
        partial = {
            "command": "list-views",
            "success": False,
            "partial": True,
            "message": "The 60-second limit was reached. Processed 3 of 9 views.",
            "elapsedMs": 60000,
            "data": {"views": [{"name": "Level 1"}]},
        }

        async def read_response(name, cleanup_names, download_artifact, save_to):
            self.assertEqual(cleanup_names, [])
            partial["correlationId"] = json.loads(remote.written_content)["correlationId"]
            return json.dumps(partial), None

        remote.finish_job = AsyncMock(side_effect=read_response)
        with patch("revit_model_mcp.revit_channel.asyncio.sleep", new=AsyncMock()):
            result = await RevitReadChannel(remote).execute(ReadJob.list_views())
        self.assertEqual(result, partial)
        self.assertEqual(remote.finish_job.await_count, 1)

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
        self.assertIn("job file is still present", message)
        self.assertIn("may still execute later", message)
        self.assertNotIn("trigger.txt", remote.deleted_names)

    async def test_reports_response_timeout_for_long_command(self) -> None:
        remote = FakeRemoteHost()
        remote.response_name = None

        with self.assertRaises(ResponseTimeoutError) as raised:
            await RevitReadChannel(remote).execute(
                ReadJob.view_elements("Level 1 Plan", limit=100), timeout_seconds=9
            )

        self.assertIn("The add-in picked up jobId=", str(raised.exception))
        self.assertIn("may still execute", str(raised.exception))
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

    async def test_returns_result_when_cleanup_fails(self) -> None:
        remote = FakeRemoteHost()
        remote.delete_files = AsyncMock(side_effect=RevitChannelError("ssh dropped"))

        with self.assertLogs("revit_model_mcp.revit_channel", "WARNING") as logs:
            result = await RevitReadChannel(remote).execute(ReadJob.document_info())

        self.assertEqual(result, json.loads(SUCCESS_RESPONSE))
        self.assertIn("ssh dropped", logs.output[0])
        remote.delete_files.assert_awaited_once()

    async def test_preserves_response_timeout_when_cleanup_fails(self) -> None:
        remote = FakeRemoteHost()
        remote.response_name = None
        remote.delete_files = AsyncMock(side_effect=RevitChannelError("ssh dropped"))

        with self.assertLogs("revit_model_mcp.revit_channel", "WARNING") as logs:
            with self.assertRaises(ResponseTimeoutError):
                await RevitReadChannel(remote).execute(ReadJob.document_info())

        self.assertIn("ssh dropped", logs.output[0])
        remote.delete_files.assert_awaited_once()

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
    async def test_batch_create_transfers_long_unicode_state_on_stdin(self) -> None:
        run_id = "a" * 32
        state = json.dumps(
            {
                "runId": run_id,
                "status": 0,
                "cancelRequested": False,
                "years": [2024, 2025, 2026],
                "parameterRules": [
                    {"category": "Walls", "parameter": "Mark"},
                    {"category": "Doors", "parameter": "Comments"},
                ],
                "models": [
                    {
                        "path": f"C:\\Models\\Проект 東京 {number:02d}\\" + "Модель " * 12 + ".rvt",
                        "status": 0,
                        "phaseTimingsMs": {},
                    }
                    for number in range(13)
                ],
            },
            ensure_ascii=False,
        )
        encoded_state = base64.b64encode(state.encode("utf-8")).decode("ascii")
        self.assertGreater(len(base64.b64encode(encoded_state.encode("utf-16le"))), 8191)

        for local in (False, True):
            with self.subTest(local=local):
                host = SshPowerShellHost("local" if local else "revit-host", local=local)
                process = FakeSshProcess(0)
                with patch(
                    "revit_model_mcp.ssh_host.asyncio.create_subprocess_exec",
                    AsyncMock(return_value=process),
                ) as start:
                    await host.batch_create(run_id, state)

                command = start.call_args.args
                script = base64.b64decode(command[-1]).decode("utf-16le")
                self.assertEqual(command[0], "powershell.exe" if local else "ssh")
                self.assertLess(len(command[-1]), 8191)
                self.assertNotIn(encoded_state, script)
                self.assertNotIn("Проект", script)
                self.assertIn("[Console]::OpenStandardInput().CopyTo($stream)", script)
                self.assertIn("[IO.File]::Move($temporary, $target)", script)
                self.assertIn("Batch run already exists.", script)
                self.assertIn("Remove-Item -LiteralPath $run", script)
                self.assertEqual(start.call_args.kwargs["stdin"], asyncio.subprocess.PIPE)
                self.assertEqual(process.input, state.encode("utf-8"))

    async def test_successful_muxed_ssh_updates_shared_last_success(self) -> None:
        host = SshPowerShellHost("revit-host")
        process = FakeSshProcess(0, stdout=b"ok")
        with (
            tempfile.TemporaryDirectory() as runtime,
            patch.dict(os.environ, {"XDG_RUNTIME_DIR": runtime, "REVIT_MCP_SSH_MUX": "1"}),
            patch(
                "revit_model_mcp.ssh_host.asyncio.create_subprocess_exec",
                AsyncMock(return_value=process),
            ),
        ):
            self.assertEqual(await host._run("'ok'"), "ok")

        self.assertIsNotNone(host._last_successful_ssh_time[0])
        self.assertIs(
            host._for_instance(instance_status())._last_successful_ssh_time,
            host._last_successful_ssh_time,
        )

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

    def test_powershell_quotes_each_unicode_single_quote(self) -> None:
        for quote in ("\u2018", "\u2019", "\u201a", "\u201b"):
            self.assertEqual(_ps_quote(f"before{quote}after"), f"before{quote}{quote}after")


def test_ssh_command_reuses_private_runtime_directory(monkeypatch, tmp_path):
    _mux_directory.cache_clear()
    monkeypatch.chdir(tmp_path)
    directory = Path("runtime")
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
        "-T",
        "-a",
        "-x",
        "-o",
        "ClearAllForwardings=yes",
        "-o",
        "ForwardAgent=no",
        "-o",
        "ForwardX11=no",
        "-o",
        "PermitLocalCommand=no",
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


def test_ssh_command_falls_back_to_user_cache(monkeypatch, tmp_path):
    _mux_directory.cache_clear()
    monkeypatch.chdir(tmp_path)
    monkeypatch.delenv("XDG_RUNTIME_DIR", raising=False)
    monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
    monkeypatch.delenv("REVIT_MCP_SSH_OPTIONS", raising=False)
    monkeypatch.setattr("revit_model_mcp.ssh_host.tempfile.gettempdir", lambda: "/tmp")
    with patch("revit_model_mcp.ssh_host.Path.home", return_value=Path(".")):
        command = SshPowerShellHost()._build_command("'ok'")
    directory = Path(".") / ".cache" / "revit-model-mcp"
    assert f"ControlPath={directory}/mux-%C" in command
    assert directory.is_dir()
    if os.name != "nt":
        assert stat.S_IMODE(directory.stat().st_mode) == 0o700
    _mux_directory.cache_clear()


@pytest.mark.skipif(os.name == "nt", reason="Windows does not use the Unix ownership check")
def test_ssh_command_disables_mux_for_symlink(monkeypatch):
    with tempfile.TemporaryDirectory(prefix="rmm-", dir="/tmp") as root:
        directory = Path(root) / "runtime"
        target = Path(root) / "target"
        target.mkdir(mode=0o755)
        original_mode = stat.S_IMODE(target.stat().st_mode)
        directory.symlink_to(target, target_is_directory=True)
        monkeypatch.setenv("XDG_RUNTIME_DIR", str(directory))
        monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
        command = SshPowerShellHost()._build_command("'ok'")
        assert "ControlMaster=auto" not in command
        assert stat.S_IMODE(target.stat().st_mode) == original_mode


@pytest.mark.skipif(not hasattr(os, "getuid"), reason="Unix user IDs are unavailable")
def test_ssh_command_disables_mux_for_foreign_owner(monkeypatch):
    with tempfile.TemporaryDirectory(prefix="rmm-", dir="/tmp") as root:
        directory = Path(root) / "runtime"
        directory.mkdir(mode=0o755)
        details = os.lstat(directory)
        original_mode = stat.S_IMODE(details.st_mode)
        monkeypatch.setenv("XDG_RUNTIME_DIR", str(directory))
        monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
        with patch(
            "revit_model_mcp.ssh_host.os.lstat",
            return_value=SimpleNamespace(st_mode=details.st_mode, st_uid=os.getuid() + 1),
        ):
            command = SshPowerShellHost()._build_command("'ok'")
        assert "ControlMaster=auto" not in command
        assert stat.S_IMODE(directory.stat().st_mode) == original_mode


def test_ssh_command_shortens_socket_path_above_86_bytes(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    long_runtime = Path("x" * 42)
    monkeypatch.setenv("XDG_RUNTIME_DIR", str(long_runtime))
    monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
    with patch("revit_model_mcp.ssh_host.Path.home", return_value=Path(".")):
        command = SshPowerShellHost()._build_command("'ok'")
    assert f"ControlPath={Path('.') / '.cache' / 'rmm'}/mux-%C" in command
    assert not long_runtime.exists()


def test_ssh_command_disables_forwarding_before_user_options(monkeypatch):
    monkeypatch.setenv("REVIT_MCP_SSH_MUX", "0")
    monkeypatch.setenv("REVIT_MCP_SSH_OPTIONS", "-o ForwardAgent=yes -o ForwardX11=yes")
    command = SshPowerShellHost("revit-host")._build_command("'ok'")
    assert command.index("ClearAllForwardings=yes") < command.index("ForwardAgent=yes")
    assert command.index("ForwardAgent=no") < command.index("ForwardAgent=yes")
    assert command.index("ForwardX11=no") < command.index("ForwardX11=yes")
    assert command[5:8] == ["-T", "-a", "-x"]


def test_ssh_command_can_disable_mux_and_append_options(monkeypatch):
    monkeypatch.setenv("REVIT_MCP_SSH_MUX", "0")
    monkeypatch.setenv("REVIT_MCP_SSH_OPTIONS", '-p 2222 -o "IdentityFile=/keys/revit key"')
    with patch("revit_model_mcp.ssh_host.Path.mkdir") as mkdir:
        command = SshPowerShellHost("revit-host")._build_command("'ok'")
    mkdir.assert_not_called()
    assert not any(option.startswith("Control") for option in command)
    assert command[16:22] == [
        "-p",
        "2222",
        "-o",
        "IdentityFile=/keys/revit key",
        "revit-host",
        "powershell.exe",
    ]


def test_ssh_command_without_usable_mux_directory_is_unmuxed(tmp_path, monkeypatch):
    _mux_directory.cache_clear()
    monkeypatch.chdir(tmp_path)
    monkeypatch.setenv("XDG_RUNTIME_DIR", "runtime")
    monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
    monkeypatch.delenv("REVIT_MCP_SSH_OPTIONS", raising=False)
    with patch("revit_model_mcp.ssh_host.Path.mkdir", side_effect=OSError):
        command = SshPowerShellHost("revit-host")._build_command("'ok'")
    assert "ControlMaster=auto" not in command
    _mux_directory.cache_clear()


def test_ssh_extra_options_follow_mux_options(monkeypatch, tmp_path):
    _mux_directory.cache_clear()
    monkeypatch.chdir(tmp_path)
    monkeypatch.setenv("XDG_RUNTIME_DIR", "runtime")
    monkeypatch.delenv("REVIT_MCP_SSH_MUX", raising=False)
    monkeypatch.setenv("REVIT_MCP_SSH_OPTIONS", "-o ServerAliveInterval=30")
    command = SshPowerShellHost("revit-host")._build_command("'ok'")
    assert command[21:25] == [
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


def instance_status(process_id=42, title="Structural", **extra):
    return {
        "processId": process_id,
        "documentTitle": title,
        "documentPath": rf"C:\Models\{title}.rvt",
        "revitVersion": "2024",
        "updatedUtc": "2026-09-16T00:00:00Z",
        "startedUtc": "2026-09-15T23:00:00.0000000Z",
        "fileChannelVersion": 2,
        "httpPort": None,
        **extra,
    }


def encode_discovery_payload(package):
    return base64.b64encode(json.dumps(package, ensure_ascii=False).encode("utf-8")).decode("ascii")


def test_ssh_heartbeat_preserves_addin_compatibility_fields():
    now = datetime.now(timezone.utc)
    status = instance_status(
        updatedUtc=now.isoformat(),
        addinVersion="0.6.0",
        protocolVersion=1,
        commands=["ping", "document-info"],
    )
    package = {
        "processes": [{"processId": 42, "revitVersion": "2024"}],
        "files": [{"name": "instance_42.json", "content": json.dumps(status)}],
    }
    instance = _parse_instance_package(package, "", now)[0]
    assert instance["addinVersion"] == "0.6.0"
    assert instance["protocolVersion"] == 1
    assert instance["commands"] == ["ping", "document-info"]


class MatchesDocumentAndResolveInstanceTests(unittest.TestCase):
    def test_matches_document_searches_v3_documents_list(self):
        instance = {
            "processId": 1,
            "documentTitle": "",
            "documentPath": "",
            "documents": [
                {"title": "Structural.rvt", "path": r"C:\Models\Structural.rvt", "isActive": True},
                {
                    "title": "Architectural.rvt",
                    "path": r"C:\Models\Architectural.rvt",
                    "isActive": False,
                },
            ],
        }
        self.assertTrue(matches_document(instance, "structural"))
        self.assertTrue(matches_document(instance, "Architectural.rvt"))
        self.assertFalse(matches_document(instance, "Mechanical"))

    def test_resolve_instance_single_instance_always_wins(self):
        instance = {"processId": 42}
        self.assertEqual(resolve_instance([instance], None), instance)
        self.assertEqual(resolve_instance([instance], "Anything"), instance)

    def test_resolve_instance_no_document_lists_running_instances(self):
        with self.assertRaisesRegex(
            RevitChannelError, r"pid 1 \(Structural\); pid 2 \(Architectural\)"
        ):
            resolve_instance(
                [
                    {"processId": 1, "documentTitle": "Structural"},
                    {"processId": 2, "documentTitle": "Architectural"},
                ],
                None,
            )


class InstanceRoutingTests(unittest.IsolatedAsyncioTestCase):
    async def test_discovery_rejects_invalid_started_utc(self):
        now = datetime.now(timezone.utc)
        host = SshPowerShellHost()
        bad = instance_status(startedUtc="2026-09-15T23:00:00\u2019; exit 1")
        host._run = AsyncMock(
            return_value=encode_discovery_payload(
                {
                    "processes": [{"processId": 42, "revitVersion": "2024"}],
                    "files": [
                        {
                            "name": "instance_42.json",
                            "content": json.dumps({**bad, "updatedUtc": now.isoformat()}),
                        }
                    ],
                }
            )
        )

        instances = await host._discover_instances()

        self.assertEqual(len(instances), 1)
        self.assertNotIn("startedUtc", instances[0])
        host._run.assert_awaited_once()

    async def test_discovery_preserves_valid_started_utc(self):
        now = datetime.now(timezone.utc)
        host = SshPowerShellHost()
        status = instance_status(startedUtc="2026-09-16T01:00:00.1234567+02:00")
        host._run = AsyncMock(
            return_value=encode_discovery_payload(
                {
                    "processes": [{"processId": 42, "revitVersion": "2024"}],
                    "files": [
                        {
                            "name": "instance_42.json",
                            "content": json.dumps({**status, "updatedUtc": now.isoformat()}),
                        }
                    ],
                }
            )
        )

        instances = await host._discover_instances()

        self.assertEqual(instances[0]["startedUtc"], status["startedUtc"])

    async def test_discovery_decodes_utf8_payload_with_cyrillic_names(self):
        host = SshPowerShellHost()
        status = instance_status(
            title="Жилой дом",
            documentPath=r"C:\Модели\Жилой дом.rvt",
            updatedUtc=datetime.now(timezone.utc).isoformat(),
        )
        host._run = AsyncMock(
            return_value=encode_discovery_payload(
                {
                    "processes": [{"processId": 42, "revitVersion": "2024"}],
                    "files": [
                        {
                            "name": "instance_42.json",
                            "content": json.dumps(status, ensure_ascii=False),
                        }
                    ],
                }
            )
        )

        instances = await host._discover_instances()

        self.assertEqual(instances[0]["documentTitle"], "Жилой дом")
        self.assertEqual(instances[0]["documentPath"], r"C:\Модели\Жилой дом.rvt")
        self.assertEqual(await host.list_revit_instances("Жилой"), instances)

    async def test_discovery_script_encodes_payload_as_base64_utf8(self):
        host = SshPowerShellHost()
        host._run = AsyncMock(return_value=encode_discovery_payload({"processes": [], "files": []}))

        await host._discover_instances()

        script = host._run.await_args.args[0]
        self.assertIn("[Text.Encoding]::UTF8.GetBytes", script)
        self.assertIn("[Convert]::ToBase64String", script)

    async def test_discovery_rejects_invalid_base64_payload(self):
        host = SshPowerShellHost()
        host._run = AsyncMock(return_value="not base64!")

        with self.assertRaisesRegex(
            ResponseParseError, "Revit instance list could not be parsed"
        ) as caught:
            await host._discover_instances()
        self.assertIsInstance(caught.exception.__cause__, binascii.Error)

    async def test_prepare_preserves_seventh_started_utc_digit(self):
        started_utc = "2026-09-15T23:00:00.1234567Z"
        host = SshPowerShellHost()._for_instance(instance_status(startedUtc=started_utc))
        host._run = AsyncMock(return_value='{"revitRunning":true,"responses":[],"published":true}')

        await host.prepare_job(
            "mcp_test.tmp", '{"command":"ping","jobId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}', "ping"
        )

        script = host._run.await_args.args[0]
        self.assertIn(f"[DateTime]::Parse('{started_utc}')", script)

    def test_rejects_started_utc_with_excess_precision(self):
        with self.assertRaisesRegex(RevitChannelError, "invalid startup identity"):
            SshPowerShellHost()._for_instance(
                instance_status(startedUtc="2026-09-15T23:00:00.12345678Z")
            )

    async def test_directed_reads_pin_pid_identity_and_directory(self):
        host = SshPowerShellHost()
        original = instance_status()
        host._discover_instances = AsyncMock(
            return_value=[original, instance_status(84, "Architectural")]
        )
        with patch.object(SshPowerShellHost, "_handshake", AsyncMock()) as handshake:
            selected, job = await host.select_job(
                ReadJob.document_info().for_document("Structural.rvt")
            )
        handshake.assert_awaited_once()
        self.assertEqual(job.payload["targetProcessId"], 42)
        self.assertIn(r"instances\42", selected._directory)
        original["startedUtc"] = "replacement"
        host._discover_instances.return_value = [instance_status(84)]
        await host._discover_instances()
        self.assertNotEqual(selected._instance["startedUtc"], "replacement")
        self.assertIn(r"instances\42", selected._directory)
        self.assertEqual(host._directory, host._root_directory)

    async def test_explicit_pid_selects_exact_instance_and_rejects_contradiction(self):
        host = SshPowerShellHost()
        host._discover_instances = AsyncMock(
            return_value=[instance_status(42, "Structural"), instance_status(84, "Architectural")]
        )
        with patch.object(SshPowerShellHost, "_handshake", AsyncMock()):
            selected, job = await host.select_job(ReadJob.document_info().for_process(84))
            self.assertEqual(selected._instance["processId"], 84)
            self.assertEqual(job.payload["targetProcessId"], 84)
            with self.assertRaisesRegex(RevitChannelError, "contradict"):
                await host.select_job(
                    ReadJob.document_info().for_document("Structural").for_process(84)
                )
            with self.assertRaisesRegex(RevitChannelError, "absent or ambiguous"):
                await host.select_job(ReadJob.document_info().for_process(99))
        for value in (0, -1, True, "84"):
            with self.assertRaisesRegex(RevitChannelError, "strict positive"):
                ReadJob.document_info().for_process(value)

    async def test_zero_many_and_undirected_matches_fail_before_publish(self):
        for document, message in [
            ("Missing", "No running"),
            ("Model", "ambiguous"),
            (None, "exactly one"),
        ]:
            with self.subTest(document=document):
                host = SshPowerShellHost()
                host._discover_instances = AsyncMock(
                    return_value=[instance_status(42, "Model A"), instance_status(84, "Model B")]
                )
                with patch.object(SshPowerShellHost, "prepare_job", AsyncMock()) as prepare:
                    with self.assertRaisesRegex(RevitChannelError, message):
                        await RevitReadChannel(host).execute(
                            ReadJob.document_info().for_document(document)
                        )
                prepare.assert_not_awaited()

    async def test_actions_resolve_a_unique_document_match_across_processes(self):
        host = SshPowerShellHost()
        host._discover_instances = AsyncMock(
            return_value=[instance_status(), {"processId": 84, "pluginResponding": False}]
        )
        with patch.object(SshPowerShellHost, "_handshake", AsyncMock()) as handshake:
            selected, job = await host.select_job(
                ReadJob("delete", {"command": "delete", "targetDocument": "Structural"})
            )
        handshake.assert_awaited_once()
        self.assertEqual(job.payload["targetProcessId"], 42)
        self.assertIn(r"instances\42", selected._directory)

    async def test_actions_require_one_process_without_a_document(self):
        host = SshPowerShellHost()
        host._discover_instances = AsyncMock(
            return_value=[instance_status(), {"processId": 84, "pluginResponding": False}]
        )
        with self.assertRaisesRegex(RevitChannelError, "exactly one"):
            await host.select_job(ReadJob("delete", {"command": "delete"}))

    async def test_legacy_single_instance_and_mixed_versions(self):
        legacy = instance_status()
        del legacy["fileChannelVersion"]
        del legacy["startedUtc"]
        host = SshPowerShellHost()
        host._discover_instances = AsyncMock(return_value=[legacy])
        with patch.object(SshPowerShellHost, "_handshake", AsyncMock()) as handshake:
            selected, _ = await host.select_job(ReadJob.ping())
            self.assertEqual(selected._directory, host._root_directory)
            handshake.assert_not_awaited()
            host._discover_instances.return_value = [legacy, instance_status(84, "Architectural")]
            with self.assertRaisesRegex(RevitChannelError, "Legacy file channels"):
                await host.select_job(ReadJob.ping().for_document("Structural"))
            selected, _ = await host.select_job(ReadJob.ping().for_document("Architectural"))
            self.assertIn(r"instances\84", selected._directory)
            handshake.assert_awaited_once()

    async def test_unconfirmed_and_unknown_protocol_rejected_before_publish(self):
        cases = [
            ({"processId": 42}, "unconfirmed"),
            (instance_status(startedUtc=""), "startup identity"),
        ]
        cases += [
            (instance_status(fileChannelVersion=value), "Unsupported")
            for value in (3, 1, "2", None)
        ]
        for instance, message in cases:
            with self.subTest(instance=instance):
                host = SshPowerShellHost()
                host._discover_instances = AsyncMock(return_value=[instance])
                host._run = AsyncMock()
                with self.assertRaisesRegex(RevitChannelError, message):
                    await host.select_job(ReadJob.ping())
                host._run.assert_not_awaited()

    async def test_identity_change_and_exited_pid_rejected(self):
        for current in (
            [],
            [instance_status(startedUtc="replacement")],
            [{"processId": 42}],
            [instance_status(fileChannelVersion=3)],
        ):
            host = SshPowerShellHost()._for_instance(instance_status())
            host._discover_instances = AsyncMock(return_value=current)
            with self.assertRaisesRegex(RevitChannelError, "identity changed"):
                await host._verify_identity()

    async def test_previously_pinned_action_does_not_switch_process(self):
        host = SshPowerShellHost()
        host._discover_instances = AsyncMock(return_value=[instance_status(84)])
        with self.assertRaisesRegex(RevitChannelError, "absent or ambiguous"):
            await host.select_job(ReadJob("select", {"command": "select", "targetProcessId": 42}))

    async def test_discovery_keeps_busy_and_timed_out_instances(self):
        for error in (RevitChannelError("busy"), RevitChannelError("handshake timed out")):
            host = SshPowerShellHost()
            host._discover_instances = AsyncMock(
                return_value=[instance_status(), instance_status(84)]
            )
            with patch.object(SshPowerShellHost, "_handshake", AsyncMock(side_effect=error)):
                instances = await host.list_revit_instances()
            self.assertEqual([item["processId"] for item in instances], [42, 84])
            self.assertTrue(all(item["pluginResponding"] is False for item in instances))

    async def test_handshake_requires_correlation_pid_and_stable_identity(self):
        for wrong in ("correlation", "pid", "missing", "identity", None):
            with self.subTest(wrong=wrong):
                host = SshPowerShellHost()._for_instance(instance_status())
                submitted = {}

                async def prepare(name, content, command):
                    submitted.update(json.loads(content))
                    return set()

                async def finish(*args):
                    return json.dumps(
                        {
                            "command": "ping",
                            "success": True,
                            "data": "pong",
                            "correlationId": None
                            if wrong == "missing"
                            else submitted["correlationId"],
                            "responder": {"processId": 84 if wrong == "pid" else 42},
                        }
                    ), None

                host.prepare_job = AsyncMock(side_effect=prepare)
                host.wait_until_trigger_is_gone = AsyncMock(
                    return_value=JobPickupStatus(True, 0, False, 0)
                )
                host.wait_for_new_response = AsyncMock(return_value="response_ping.json")
                host.finish_job = AsyncMock(side_effect=finish)
                host.delete_files = AsyncMock()
                host._discover_instances = AsyncMock(
                    return_value=[instance_status(startedUtc="replacement")]
                    if wrong == "identity"
                    else [instance_status()]
                )
                if wrong == "correlation":
                    host.finish_job = AsyncMock(
                        return_value=(
                            json.dumps({"command": "ping", "correlationId": "late-job"}),
                            None,
                        )
                    )
                    host.wait_for_new_response.side_effect = ["late.json", None]
                    with patch("revit_model_mcp.revit_channel.asyncio.sleep", AsyncMock()):
                        with self.assertRaises(ResponseTimeoutError):
                            await host._handshake()
                    self.assertNotIn("late.json", host.delete_files.await_args.args[0])
                elif wrong:
                    with self.assertRaises(RevitChannelError):
                        await host._handshake()
                else:
                    await host._handshake()
                    self.assertEqual(submitted["targetProcessId"], 42)
                    self.assertEqual(len(submitted["correlationId"]), 32)
                    self.assertEqual(host._discover_instances.await_count, 1)

    async def test_handshake_is_bounded_and_never_deletes_pending_trigger(self):
        host = SshPowerShellHost()._for_instance(instance_status())
        host.prepare_job = AsyncMock(return_value=set())

        async def pending(timeout):
            await asyncio.sleep(10)

        host.wait_until_trigger_is_gone = pending
        host.delete_files = AsyncMock()
        with patch("revit_model_mcp.ssh_host.HANDSHAKE_TIMEOUT_SECONDS", 0.01):
            with self.assertRaisesRegex(RevitChannelError, "handshake timed out"):
                await host._handshake()
        self.assertNotIn("trigger.txt", host.delete_files.await_args.args[0])

    async def test_v2_response_selection_does_not_fall_back_to_legacy(self):
        host = SshPowerShellHost()._for_instance(instance_status())
        host._poll_for_change = AsyncMock(return_value=(None, 1, 0))
        await host.wait_for_new_response("ping", set(), 1, "fresh-id")
        script = host._poll_for_change.await_args.args[0]
        self.assertIn(r"instances\42", script)
        self.assertIn("else { $null }", script)
        self.assertNotIn("else { $legacy }", script)


class PowerShellIsolationTests(unittest.IsolatedAsyncioTestCase):
    async def test_real_file_operations_remain_in_selected_pid_directory(self):
        import shutil
        import tempfile
        from pathlib import Path

        executable = shutil.which("pwsh")
        if not executable:
            self.skipTest("PowerShell is not installed")
        with tempfile.TemporaryDirectory() as root:
            host = SshPowerShellHost(local=True)
            host._root_directory = "'" + root.replace("'", "''") + "'"
            scripts = []

            async def run(script, timeout_seconds=60):
                scripts.append(script)
                prefix = "function Get-Process { @([pscustomobject]@{Id=42}, [pscustomobject]@{Id=84}) }; "
                process = await asyncio.create_subprocess_exec(
                    executable,
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    prefix + script,
                    stdout=asyncio.subprocess.PIPE,
                    stderr=asyncio.subprocess.PIPE,
                )
                stdout, stderr = await process.communicate()
                self.assertEqual(process.returncode, 0, stderr.decode())
                return stdout.decode().strip()

            host._run = run
            statuses = [
                instance_status(process_id, updatedUtc=datetime.now(timezone.utc).isoformat())
                for process_id in (42, 84)
            ]
            for status in statuses:
                Path(root, f"instance_{status['processId']}.json").write_text(json.dumps(status))
            first, second = [host._for_instance(status) for status in statuses]
            first_id = "a" * 32
            second_id = "b" * 32
            contender_id = "c" * 32
            await first.prepare_job(
                "one.tmp", json.dumps({"command": "ping", "jobId": first_id}), "ping"
            )
            await second.prepare_job(
                "two.tmp", json.dumps({"command": "ping", "jobId": second_id}), "ping"
            )
            self.assertTrue(Path(root, "instances", "42", f"job_{first_id}.json").exists())
            self.assertTrue(Path(root, "instances", "84", f"job_{second_id}.json").exists())
            self.assertFalse(Path(root, f"job_{first_id}.json").exists())
            await first.prepare_job(
                "contender.tmp", json.dumps({"command": "ping", "jobId": contender_id}), "ping"
            )
            self.assertTrue(Path(root, "instances", "42", f"job_{contender_id}.json").exists())
            Path(root, "instances", "42", f"job_{contender_id}.json").unlink()
            self.assertTrue((await first.wait_until_trigger_is_gone(5)).taken)
            for process_id in (42, 84):
                directory = Path(root, "instances", str(process_id))
                directory.joinpath("response_20260916_120000_000_ping_fresh.json").write_text(
                    json.dumps(
                        {
                            "command": "ping",
                            "correlationId": "fresh",
                            "data": {"fileName": "view.png"},
                        }
                    )
                )
                directory.joinpath("view.png").write_bytes(b"\x89PNG\r\n\x1a\ncontent")
                directory.joinpath("response_20260916_120001_000_ping_late.json").write_text(
                    json.dumps({"command": "ping", "correlationId": "late"})
                )
            response = await first.wait_for_new_response("ping", set(), 5, "fresh")
            self.assertEqual(response, "response_20260916_120000_000_ping_fresh.json")
            target = Path(root, "download.png")
            _, saved = await first.finish_job(response, [response], True, str(target))
            self.assertEqual(Path(saved).read_bytes(), b"\x89PNG\r\n\x1a\ncontent")
            self.assertFalse(Path(root, "instances", "42", "view.png").exists())
            self.assertTrue(Path(root, "instances", "84", "view.png").exists())
            await first.delete_files(["response_20260916_120001_000_ping_late.json"])
            self.assertTrue(
                Path(
                    root, "instances", "84", "response_20260916_120001_000_ping_late.json"
                ).exists()
            )
            self.assertTrue(
                Path(
                    root, "instances", "84", "response_20260916_120000_000_ping_fresh.json"
                ).exists()
            )
            self.assertTrue(Path(root, "instances", "84", f"job_{second_id}.json").exists())
            self.assertTrue(any("$_.Id -eq 42" in script for script in scripts))

            Path(root, "instances", "84", f"job_{second_id}.json").unlink()
            Path(root, "instances", "42", "instance_999.json").write_text(
                json.dumps(instance_status(999))
            )
            discovered = await host._discover_instances()
            self.assertEqual([item["processId"] for item in discovered], [42, 84])
            self.assertTrue(all(item["pluginResponding"] is False for item in discovered))
            published = []
            original_prepare = SshPowerShellHost.prepare_job

            async def respond(selected, name, content, command):
                known = await original_prepare(selected, name, content, command)
                payload = json.loads(content)
                process_id = selected._instance["processId"]
                directory = Path(root, "instances", str(process_id))
                published.append((process_id, payload))
                directory.joinpath(f"job_{payload['jobId']}.json").unlink()
                directory.joinpath(
                    f"response_20260917_120000_000_{command}_{payload['correlationId']}.json"
                ).write_text(
                    json.dumps(
                        {
                            "command": command,
                            "success": True,
                            "data": "pong",
                            "correlationId": payload["correlationId"],
                            "responder": {"processId": process_id},
                        }
                    )
                )
                return known

            statuses[0]["documentTitle"] = "Model A"
            statuses[1]["documentTitle"] = "Model B"
            for status in statuses:
                Path(root, f"instance_{status['processId']}.json").write_text(json.dumps(status))
            with patch.object(SshPowerShellHost, "prepare_job", respond):
                for process_id, document in ((42, "Model A"), (84, "Model B")):
                    result = await RevitReadChannel(host).execute(
                        ReadJob.document_info().for_document(document)
                    )
                    self.assertEqual(result["responder"]["processId"], process_id)
            self.assertEqual(
                [(process_id, payload["command"]) for process_id, payload in published],
                [(42, "ping"), (42, "document-info"), (84, "ping"), (84, "document-info")],
            )
            self.assertEqual(len({payload["correlationId"] for _, payload in published}), 4)
            self.assertFalse(list(Path(root, "instances", "42").glob("response_20260917_*")))
            self.assertFalse(list(Path(root, "instances", "84").glob("response_20260917_*")))


def test_two_server_processes_complete_jobs_on_simulated_host(tmp_path):
    import subprocess
    import sys
    import time

    worker = r"""
import asyncio, json, pathlib, sys
from revit_model_mcp.revit_channel import JobPickupStatus, ReadJob, RevitReadChannel
root = pathlib.Path(sys.argv[1])
class SimulatedHost:
    async def select_job(self, job): return self, job
    async def prepare_job(self, name, content, command):
        self.job_id = json.loads(content)["jobId"]
        root.joinpath("job_" + self.job_id + ".json").write_text(content)
        return set()
    async def wait_until_trigger_is_gone(self, timeout_seconds):
        while root.joinpath("job_" + self.job_id + ".json").exists():
            await asyncio.sleep(.01)
        return JobPickupStatus(True, 0, False, 0)
    async def wait_for_new_response(self, command, known_names, timeout_seconds, correlation_id=None):
        name = "response_" + self.job_id + ".json"
        while not root.joinpath(name).exists(): await asyncio.sleep(.01)
        return name
    async def finish_job(self, response_name, cleanup_names, download_artifact, save_to):
        return root.joinpath(response_name).read_text(), None
    async def delete_files(self, names): pass
asyncio.run(RevitReadChannel(SimulatedHost()).execute(ReadJob.ping()))
"""
    workers = [subprocess.Popen([sys.executable, "-c", worker, str(tmp_path)]) for _ in range(2)]
    try:
        deadline = time.monotonic() + 10
        while len(list(tmp_path.glob("job_*.json"))) < 2 and time.monotonic() < deadline:
            time.sleep(0.01)
        jobs = list(tmp_path.glob("job_*.json"))
        assert len(jobs) == 2
        payloads = [json.loads(path.read_text()) for path in jobs]
        assert payloads[0]["clientId"] != payloads[1]["clientId"]
        for path, payload in zip(jobs, payloads):
            path.unlink()
            tmp_path.joinpath(f"response_{payload['jobId']}.json").write_text(
                json.dumps({"command": "ping", "success": True, "data": "pong"})
            )
        assert all(process.wait(timeout=10) == 0 for process in workers)
    finally:
        for process in workers:
            if process.poll() is None:
                process.kill()
                process.wait()


@pytest.mark.parametrize("completed", [False, True])
def test_long_action_budget_boundary_preserves_result_and_job(completed):
    async def check():
        host = FakeRemoteHost()
        host.instance_info = {
            "addinVersion": "0.7.0",
            "commands": ["process-models", "jobs/persisted"],
        }
        host.response_name = None

        async def expire_response_wait(command, known_names, timeout_seconds, correlation_id):
            await asyncio.sleep(timeout_seconds)
            return None

        host.wait_for_new_response = expire_response_wait
        response = {
            "command": "process-models",
            "success": True,
            "partial": not completed,
            "message": "Done" if completed else "Command accepted and running.",
            "data": {
                "currentIndex": 2,
                "total": 4,
                "models": [{"status": "done", "result": {"files": ["a.ifc"]}}],
            },
            "verification": {"warning": "Check exported files."},
        }
        host.fetch_job = AsyncMock(return_value=response)
        with patch("revit_model_mcp.revit_channel.tool_budget_seconds", return_value=0.1):
            result = await RevitReadChannel(host).execute(
                ReadJob("process-models", {"command": "process-models"})
            )
        job_id = json.loads(host.written_content)["jobId"]
        host.fetch_job.assert_awaited_once_with(job_id)
        assert host.deleted_names == []
        if completed:
            assert result == response
        else:
            assert result["status"] == "running"
            assert result["jobId"] == job_id
            assert result["progress"] == {
                "currentIndex": 2,
                "total": 4,
                "models": [{"status": "done"}],
            }
            assert result["partial"] == [{"status": "done"}]
            assert "may already have changed" in result["message"]

    asyncio.run(check())


def test_long_action_completed_before_budget_keeps_response_shape():
    async def check():
        host = FakeRemoteHost()
        host.instance_info = {"addinVersion": "0.7.0", "commands": ["export", "jobs/persisted"]}
        response = {"command": "export", "success": True, "data": {"files": ["a.ifc"]}}
        host.response_content = json.dumps(response)
        host.fetch_job = AsyncMock()
        result = await RevitReadChannel(host).execute(ReadJob("export", {"command": "export"}))
        assert result == response
        host.fetch_job.assert_not_awaited()
        assert host.deleted_names
        assert host.response_timeout <= 50

    asyncio.run(check())


def test_jobs_poll_waits_and_fetches_final_result_after_server_restart():
    async def check():
        host = FakeRemoteHost()
        progress = {
            "command": "process-models",
            "success": True,
            "partial": True,
            "message": "Command accepted and running.",
            "data": {"total": 4},
        }
        final = {
            "command": "process-models",
            "success": False,
            "partial": True,
            "message": "Cancelled before the next model.",
            "data": {"cancelled": True, "models": [{"status": "done"}]},
        }
        host.fetch_job = AsyncMock(side_effect=[progress, final])
        # A new channel has no in-memory submission record.
        result = await RevitReadChannel(host).execute(
            ReadJob(
                "jobs",
                {
                    "command": "jobs",
                    "fetchJobId": "a" * 32,
                    "waitSeconds": 1,
                },
            )
        )
        assert result == final
        assert host.fetch_job.await_count == 2
        assert host.written_content is None

    asyncio.run(check())


def test_jobs_zero_wait_returns_progress_and_cancel_uses_direct_host():
    async def check():
        host = FakeRemoteHost()
        host.fetch_job = AsyncMock(
            return_value={
                "command": "process-models",
                "success": True,
                "partial": True,
                "message": "Command accepted and running.",
                "data": {"total": 4},
            }
        )
        host.cancel_job = AsyncMock(return_value={"cancelled": True})
        payload = {"command": "jobs", "fetchJobId": "a" * 32, "waitSeconds": 0}
        channel = RevitReadChannel(host)
        assert (await channel.execute(ReadJob("jobs", payload)))["status"] == "running"
        assert await channel.execute(ReadJob("jobs", {**payload, "requestCancellation": True})) == {
            "cancelled": True
        }
        host.cancel_job.assert_awaited_once_with("a" * 32)
        assert host.written_content is None

    asyncio.run(check())


@pytest.mark.parametrize("value", ["9", "201", "nan", "invalid"])
def test_invalid_tool_budget_is_rejected(value):
    from revit_model_mcp.revit_channel import tool_budget_seconds

    with patch.dict(os.environ, {"REVIT_MCP_TOOL_BUDGET_S": value}):
        with pytest.raises(RevitChannelError, match="10 and 200"):
            tool_budget_seconds()


def test_file_action_fetch_and_cancel_use_retained_instance_job_files():
    async def check():
        host = SshPowerShellHost("local", local=True)
        host._directory = "selected-instance-directory"
        progress = {
            "command": "process-models",
            "success": False,
            "partial": True,
            "message": "Command accepted and running.",
            "data": {"currentIndex": 1},
        }
        encoded = base64.b64encode(json.dumps(progress).encode()).decode()
        host._run = AsyncMock(side_effect=[encoded, encoded, ""])
        assert await host.fetch_job("a" * 32) == progress
        assert (await host.cancel_job("a" * 32))["cancelled"] is True
        scripts = [call.args[0] for call in host._run.await_args_list]
        assert all("selected-instance-directory" in script for script in scripts)
        assert "jobs/" + "a" * 32 + ".json" in scripts[0]
        assert "AddHours(-24)" in scripts[0]
        assert "'jobs'" in scripts[2]
        assert "a" * 32 + ".cancel" in scripts[2]
        assert "read-only" in scripts[2]
        assert "trigger.txt" not in "".join(scripts)

    asyncio.run(check())


def test_budget_expires_before_pickup_without_removing_submitted_action():
    async def check():
        host = FakeRemoteHost()
        host.instance_info = {
            "addinVersion": "0.7.0",
            "commands": ["process-models", "jobs/persisted"],
        }

        async def pickup(_):
            await asyncio.sleep(1)
            return JobPickupStatus(True, 0, False, 1)

        host.wait_until_trigger_is_gone = pickup
        with patch("revit_model_mcp.revit_channel.tool_budget_seconds", return_value=0.01):
            result = await RevitReadChannel(host).execute(
                ReadJob("process-models", {"command": "process-models"})
            )
        assert result["status"] == "running"
        assert result["jobId"] == json.loads(host.written_content)["jobId"]
        assert host.deleted_names == []

    asyncio.run(check())


@pytest.mark.parametrize("persisted", [False, True])
def test_long_action_requires_persisted_jobs_for_background_wait(persisted):
    async def check():
        host = FakeRemoteHost()
        host.instance_info = {"addinVersion": "0.7.0", "commands": ["export"]}
        if persisted:
            host.instance_info["commands"].append("jobs/persisted")
        host.response_content = json.dumps({"command": "export", "success": True, "data": {}})
        with patch("revit_model_mcp.revit_channel.tool_budget_seconds", return_value=10):
            await RevitReadChannel(host).execute(ReadJob("export", {"command": "export"}), 120)
        if persisted:
            assert host.response_timeout <= 10
        else:
            assert host.response_timeout == 120

    asyncio.run(check())


def test_background_wait_never_exceeds_budget_when_clock_is_frozen():
    async def check():
        loop = asyncio.get_running_loop()
        host = FakeRemoteHost()
        host.instance_info = {"addinVersion": "0.7.0", "commands": ["export", "jobs/persisted"]}
        host.response_content = json.dumps({"command": "export", "success": True, "data": {}})
        with (
            patch("revit_model_mcp.revit_channel.tool_budget_seconds", return_value=10),
            patch.object(loop, "time", return_value=4087.907388981937),
        ):
            await RevitReadChannel(host).execute(ReadJob("export", {"command": "export"}), 120)
        assert host.response_timeout <= 10

    asyncio.run(check())


@pytest.mark.parametrize("error_type", [RemoteCommandTimeoutError, RevitChannelError, TimeoutError])
def test_file_poll_boundary_error_returns_running_job(error_type):
    async def check():
        host = FakeRemoteHost()
        host.instance_info = {
            "addinVersion": "0.7.0",
            "commands": ["process-models", "jobs/persisted"],
        }
        host.response_content = json.dumps(
            {
                "command": "process-models",
                "success": True,
                "partial": True,
                "message": "Command accepted and running.",
                "data": {"total": 4},
            }
        )
        host.wait_for_new_response = AsyncMock(
            side_effect=[host.response_name, error_type("Poll timed out.")]
        )
        host.fetch_job = AsyncMock(side_effect=error_type("Boundary fetch timed out."))
        with patch("revit_model_mcp.revit_channel.tool_budget_seconds", return_value=1.1):
            result = await RevitReadChannel(host).execute(
                ReadJob("process-models", {"command": "process-models"})
            )
        assert host.wait_for_new_response.await_count == 2
        assert host.wait_for_new_response.await_args.args[2] < 1
        assert result["status"] == "running"
        assert result["jobId"] == json.loads(host.written_content)["jobId"]
        assert host.deleted_names == []

    asyncio.run(check())


@pytest.mark.parametrize("command", ["export-view", "capture-elements"])
def test_image_read_downloads_artifact(command):
    remote = FakeRemoteHost()
    remote.instance_info = {"addinVersion": "0.7.0", "commands": [command]}
    response = json.dumps({"command": command, "success": True, "data": {"fileName": "view.png"}})
    remote.response_content = response
    remote.finish_job = AsyncMock(side_effect=[(response, None), (response, "/tmp/view.png")])
    result = asyncio.run(
        RevitReadChannel(remote).execute(ReadJob(command, {"command": command}, "/tmp/view.png"))
    )
    assert result["data"]["localPath"] == "/tmp/view.png"
    assert remote.finish_job.await_args.args[2:] == (True, "/tmp/view.png")


def test_capture_http_rejected_before_submission():
    from revit_model_mcp.http_host import HttpHost

    host = HttpHost("http://127.0.0.1:53110", "token")
    host.select_job = AsyncMock()
    with pytest.raises(
        RevitChannelError, match="element snapshots need the local or SSH transport"
    ):
        asyncio.run(
            RevitReadChannel(host).execute(
                ReadJob("capture-elements", {"command": "capture-elements", "elementIds": [1]})
            )
        )
    host.select_job.assert_not_awaited()


@pytest.mark.parametrize("command", ["export-view", "capture-elements"])
def test_image_read_commands_download_artifact(command, tmp_path):
    remote = FakeRemoteHost()
    remote.instance_info = {"addinVersion": "0.7.0", "commands": [command]}
    downloads = []
    target = str(tmp_path / "image.png")

    async def finish(name, cleanup_names, download_artifact, save_to):
        downloads.append(download_artifact)
        response = {
            "command": command,
            "success": True,
            "data": {"fileName": "view.png"},
            "correlationId": json.loads(remote.written_content)["correlationId"],
        }
        return json.dumps(response), target if download_artifact else None

    remote.finish_job = AsyncMock(side_effect=finish)
    result = asyncio.run(
        RevitReadChannel(remote).execute(ReadJob(command, {"command": command}, target))
    )
    assert downloads == [False, True]
    assert result["data"]["localPath"] == target
    assert remote.finish_job.await_args.args[3] == target


def test_http_element_snapshots_fail_before_transport_call():
    from revit_model_mcp.http_host import HttpHost

    host = object.__new__(HttpHost)
    with pytest.raises(
        RevitChannelError, match="element snapshots need the local or SSH transport"
    ):
        asyncio.run(
            RevitReadChannel(host).execute(
                ReadJob("capture-elements", {"command": "capture-elements", "elementIds": [1]})
            )
        )
