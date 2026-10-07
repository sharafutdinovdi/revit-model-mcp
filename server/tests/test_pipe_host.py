from __future__ import annotations

import asyncio
import ctypes
import json
import sys
from ctypes import wintypes
from datetime import datetime, timezone
from pathlib import Path
from types import ModuleType
from typing import Any
from unittest.mock import AsyncMock, MagicMock

from revit_model_mcp import pipe_host
from revit_model_mcp.pipe_host import (
    PIPE_PROTOCOL,
    LocalPipeHost,
    PipeDisconnectedError,
    PipeJobHost,
)
from revit_model_mcp.revit_channel import CLIENT_ID, Job, RevitChannel

PID = 4242
INSTANCE_ID = "0f8fad5bd9cb469fa16570867728950e"


def mock_windows_pipe(monkeypatch, server_pid: int | None):
    pipe = MagicMock()
    windows_utils = ModuleType("asyncio.windows_utils")
    windows_utils.PipeHandle = MagicMock(return_value=pipe)
    monkeypatch.setitem(sys.modules, "asyncio.windows_utils", windows_utils)
    kernel32 = MagicMock()
    kernel32.CreateFileW.return_value = 123
    if server_pid is None:
        del kernel32.GetNamedPipeServerProcessId
    else:

        def get_server_pid(_handle, result):
            ctypes.cast(result, ctypes.POINTER(wintypes.ULONG)).contents.value = server_pid
            return True

        kernel32.GetNamedPipeServerProcessId.side_effect = get_server_pid
    monkeypatch.setattr(
        pipe_host.ctypes, "WinDLL", lambda *_args, **_kwargs: kernel32, raising=False
    )
    return kernel32, pipe


def test_windows_pipe_rejects_different_server_process(monkeypatch) -> None:
    kernel32, pipe = mock_windows_pipe(monkeypatch, PID + 1)

    try:
        pipe_host._open_verified_pipe(PID, pipe_host.pipe_address(PID))
    except PipeDisconnectedError as error:
        assert "does not match selected process" in str(error)
    else:
        raise AssertionError("The pipe from another process was accepted.")

    kernel32.GetNamedPipeServerProcessId.assert_called_once()
    pipe.close.assert_called_once()


def test_windows_pipe_accepts_matching_server_process_with_identification_qos(monkeypatch) -> None:
    kernel32, pipe = mock_windows_pipe(monkeypatch, PID)

    opened = pipe_host._open_verified_pipe(PID, pipe_host.pipe_address(PID))

    assert opened is pipe
    pipe.close.assert_not_called()
    assert (
        kernel32.CreateFileW.call_args.args[5]
        & (pipe_host.SECURITY_SQOS_PRESENT | pipe_host.SECURITY_IDENTIFICATION)
        == pipe_host.SECURITY_SQOS_PRESENT | pipe_host.SECURITY_IDENTIFICATION
    )


def test_windows_pipe_rejects_unavailable_server_process_check(monkeypatch) -> None:
    _, pipe = mock_windows_pipe(monkeypatch, None)

    try:
        pipe_host._open_verified_pipe(PID, pipe_host.pipe_address(PID))
    except PipeDisconnectedError as error:
        assert "Update Windows or use the file channel" in str(error)
    else:
        raise AssertionError("The pipe was accepted without a process check.")

    pipe.close.assert_called_once()


def write_heartbeat(directory: Path, protocols: list[str] | None) -> None:
    status: dict[str, Any] = {
        "processId": PID,
        "revitVersion": "2026",
        "documentTitle": "SampleModel",
        "documentPath": r"C:\Models\SampleModel.rvt",
        "updatedUtc": datetime.now(timezone.utc).isoformat(),
        "fileChannelVersion": 2,
        "startedUtc": "2026-09-24T00:00:00Z",
        "addinVersion": "0.6.0",
        "protocolVersion": 1,
        "commands": ["ping", "document-info"],
    }
    if protocols is not None:
        status.update(
            discoveryVersion=3,
            instanceId=INSTANCE_ID,
            pipeName=f"RevitModelMcp.{PID}",
            protocols=protocols,
            documents=[
                {
                    "title": "SampleModel",
                    "path": r"C:\Models\SampleModel.rvt",
                    "isActive": True,
                    "isFamilyDocument": False,
                }
            ],
        )
    (directory / f"instance_{PID}.json").write_text(json.dumps(status), encoding="utf-8")


class FakeRevit:
    """Loopback TCP stand-in for the add-in pipe: the same newline-delimited pipe/1 messages."""

    def __init__(
        self,
        drop_after_submit: int = 0,
        close_after_result: bool = False,
        drop_before_submitted_reply: bool = False,
    ) -> None:
        self.drop_after_submit = drop_after_submit
        self.close_after_result = close_after_result
        self.drop_before_submitted_reply = drop_before_submitted_reply
        self.received: list[tuple[int, dict[str, Any]]] = []
        self.jobs: dict[str, dict[str, Any]] = {}
        self.connections = 0
        self.port = 0
        self._server: asyncio.Server | None = None

    async def __aenter__(self) -> FakeRevit:
        self._server = await asyncio.start_server(self._handle, "127.0.0.1", 0)
        self.port = self._server.sockets[0].getsockname()[1]
        return self

    async def __aexit__(self, *_: object) -> None:
        assert self._server is not None
        self._server.close()

    async def connect(self, process_id: int):
        assert process_id == PID
        return await asyncio.open_connection("127.0.0.1", self.port)

    def messages(self, connection: int) -> list[str]:
        return [message["type"] for number, message in self.received if number == connection]

    @staticmethod
    def result(job: dict[str, Any]) -> dict[str, Any]:
        return {
            "command": job["command"],
            "success": True,
            "data": {"status": "ok"},
            "correlationId": job["correlationId"],
            "elapsedMs": 1,
        }

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        self.connections += 1
        number = self.connections

        async def send(message: dict[str, Any]) -> None:
            writer.write((json.dumps(message) + "\n").encode("utf-8"))
            await writer.drain()

        try:
            while line := await reader.readline():
                message = json.loads(line)
                self.received.append((number, message))
                kind = message["type"]
                if kind == "hello":
                    await send(
                        {
                            "type": "hello",
                            "id": message["id"],
                            "protocol": PIPE_PROTOCOL,
                            "instanceId": INSTANCE_ID,
                            "pid": PID,
                            "revitVersion": "2026",
                            "addinVersion": "0.6.0",
                            "protocolVersion": 1,
                            "commands": ["ping", "document-info"],
                            "documents": [],
                        }
                    )
                elif kind == "submit":
                    job = message["job"]
                    self.jobs[job["jobId"]] = job
                    if self.drop_before_submitted_reply:
                        self.drop_before_submitted_reply = False
                        return
                    await send(
                        {
                            "type": "submitted",
                            "id": message["id"],
                            "jobId": job["jobId"],
                            "state": "queued",
                            "position": 1,
                        }
                    )
                    if self.drop_after_submit:
                        self.drop_after_submit -= 1
                        return
                    await send({"type": "job", "jobId": job["jobId"], "state": "running"})
                    await send(
                        {
                            "type": "job",
                            "jobId": job["jobId"],
                            "state": "done",
                            "result": self.result(job),
                        }
                    )
                    if self.close_after_result:
                        return
                elif kind == "status":
                    job = self.jobs.get(message["jobId"])
                    if job is None:
                        await send(
                            {
                                "type": "error",
                                "id": message["id"],
                                "error": "not_found",
                                "jobId": message["jobId"],
                                "message": "Job not found or expired.",
                            }
                        )
                        continue
                    await send(
                        {
                            "type": "status",
                            "id": message["id"],
                            "jobId": job["jobId"],
                            "state": "done",
                            "position": 0,
                            "result": self.result(job),
                        }
                    )
        finally:
            writer.close()


def test_pipe_is_selected_when_the_heartbeat_advertises_it(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])
    fallback = AsyncMock()

    async def scenario() -> None:
        async with FakeRevit() as revit:
            host = LocalPipeHost(fallback, revit.connect, tmp_path)
            selected, job = await host.select_job(Job.ping())
            assert isinstance(selected, PipeJobHost)
            assert job.payload["targetProcessId"] == PID
            assert selected.instance_info["addinVersion"] == "0.6.0"
            assert selected.instance_info["commands"] == ["ping", "document-info"]
            instances = await host.list_revit_instances()
            assert instances[0]["pluginResponding"] is True
            assert instances[0]["documents"][0]["isActive"] is True
            assert instances[0]["protocolVersion"] == 1
            await host.aclose()

    asyncio.run(scenario())
    fallback.select_job.assert_not_awaited()
    fallback.list_revit_instances.assert_not_awaited()


def test_file_channel_is_used_when_pipe_is_not_advertised(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, None)
    fallback = AsyncMock()
    fallback.select_job.return_value = ("file-host", Job.ping())
    connector = AsyncMock()

    selected, _ = asyncio.run(LocalPipeHost(fallback, connector, tmp_path).select_job(Job.ping()))

    assert selected == "file-host"
    connector.assert_not_awaited()


def test_file_channel_is_used_when_the_advertised_pipe_is_unreachable(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])
    fallback = AsyncMock()
    fallback.select_job.return_value = ("file-host", Job.ping())
    connector = AsyncMock(side_effect=PipeDisconnectedError("pipe not found"))

    selected, _ = asyncio.run(LocalPipeHost(fallback, connector, tmp_path).select_job(Job.ping()))

    assert selected == "file-host"
    connector.assert_awaited_once_with(PID)


def test_hello_submit_and_pushed_result(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])

    async def scenario() -> tuple[dict[str, Any], FakeRevit]:
        async with FakeRevit() as revit:
            host = LocalPipeHost(AsyncMock(), revit.connect, tmp_path)
            result = await RevitChannel(host).execute(Job.ping(), timeout_seconds=5)
            await host.aclose()
            return result, revit

    result, revit = asyncio.run(scenario())

    assert result["success"] is True
    assert revit.messages(1) == ["hello", "submit"]
    hello = revit.received[0][1]
    assert hello["protocol"] == PIPE_PROTOCOL
    assert hello["clientId"] == CLIENT_ID
    job = revit.received[1][1]["job"]
    assert job["command"] == "ping"
    assert job["clientId"] == CLIENT_ID
    assert job["targetProcessId"] == PID
    assert result["correlationId"] == job["correlationId"]


def test_reconnects_after_the_pipe_closes(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])

    async def scenario() -> FakeRevit:
        async with FakeRevit(close_after_result=True) as revit:
            host = LocalPipeHost(AsyncMock(), revit.connect, tmp_path)
            channel = RevitChannel(host)
            for _ in range(2):
                result = await channel.execute(Job.ping(), timeout_seconds=5)
                assert result["success"] is True
            await host.aclose()
            return revit

    revit = asyncio.run(scenario())

    assert revit.connections == 2
    assert revit.messages(1) == ["hello", "submit"]
    assert revit.messages(2)[0] == "hello"
    assert revit.messages(2)[-1] == "submit"


def test_running_job_resumes_through_status_after_disconnect(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])

    async def scenario() -> tuple[dict[str, Any], FakeRevit]:
        async with FakeRevit(drop_after_submit=1) as revit:
            host = LocalPipeHost(AsyncMock(), revit.connect, tmp_path)
            result = await RevitChannel(host).execute(Job.ping(), timeout_seconds=5)
            await host.aclose()
            return result, revit

    result, revit = asyncio.run(scenario())

    assert result["success"] is True
    assert revit.messages(2) == ["hello", "status"]
    submitted = revit.received[1][1]["job"]["jobId"]
    assert revit.received[-1][1]["jobId"] == submitted


def test_lost_submit_reply_resumes_instead_of_reporting_duplicate_job_id(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])

    async def scenario() -> tuple[dict[str, Any], FakeRevit]:
        async with FakeRevit(drop_before_submitted_reply=True) as revit:
            host = LocalPipeHost(AsyncMock(), revit.connect, tmp_path)
            result = await RevitChannel(host).execute(Job.ping(), timeout_seconds=5)
            await host.aclose()
            return result, revit

    result, revit = asyncio.run(scenario())

    assert result["success"] is True
    assert revit.messages(1) == ["hello", "submit"]
    assert revit.messages(2) == ["hello", "status"]
    submitted = revit.received[1][1]["job"]["jobId"]
    assert revit.received[-1][1]["jobId"] == submitted


def test_action_result_can_be_fetched_by_new_server_connection(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])

    async def scenario():
        async with FakeRevit() as revit:
            job_id = "a" * 32
            job = {"command": "export", "jobId": job_id, "correlationId": "old-request"}
            revit.jobs[job_id] = job
            host = LocalPipeHost(AsyncMock(), revit.connect, tmp_path)
            result = await RevitChannel(host).execute(
                Job(
                    "jobs",
                    {
                        "command": "jobs",
                        "fetchJobId": job_id,
                        "waitSeconds": 0,
                    },
                )
            )
            await host.aclose()
            assert result == FakeRevit.result(job)
            assert revit.messages(1) == ["hello", "status"]

    asyncio.run(scenario())


def test_pipe_cancel_job_uses_existing_cancel_protocol():
    async def scenario():
        connection = AsyncMock()
        connection.request.return_value = {"type": "cancel", "cancelled": True}
        parent = AsyncMock()
        parent.connection.return_value = connection
        host = PipeJobHost(parent, {"processId": PID}, connection)
        assert (await host.cancel_job("a" * 32))["cancelled"] is True
        connection.request.assert_awaited_once_with({"type": "cancel", "jobId": "a" * 32})

    asyncio.run(scenario())


def test_local_heartbeat_preserves_http_listener_status(tmp_path, monkeypatch):
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])
    path = tmp_path / f"instance_{PID}.json"
    status = json.loads(path.read_text(encoding="utf-8"))
    status.update(httpPort=53110, httpState="failed", httpReason="Access denied.")
    path.write_text(json.dumps(status), encoding="utf-8")
    monkeypatch.setenv("REVIT_MCP_CHANNEL_DIR", str(tmp_path))
    instance = LocalPipeHost().heartbeats()[0]
    assert instance["httpPort"] == 53110
    assert instance["httpState"] == "failed"
    assert instance["httpReason"] == "Access denied."


def test_pipe_read_only_action_returns_refusal_and_read_raises():
    import pytest

    from revit_model_mcp import package_version
    from revit_model_mcp.revit_channel import ReadOnlyRefusedError

    async def check():
        connection = MagicMock()
        connection.request = AsyncMock(return_value={"type": "error", "error": "read_only"})
        parent = MagicMock()
        parent.connection = AsyncMock(return_value=connection)
        remote = PipeJobHost(parent, {"processId": PID}, connection)
        connection.hello = {"addinVersion": package_version(), "commands": ["select", "ping"]}
        parent.select_job = AsyncMock(side_effect=lambda job: (remote, job))
        assert await RevitChannel(remote).execute(Job("select", {"command": "select"})) == {
            "success": False,
            "command": "select",
            "error": "read-only mode",
            "errorCode": "read_only",
        }
        with pytest.raises(ReadOnlyRefusedError, match="workstation is in read-only mode"):
            await RevitChannel(remote).execute(Job.ping())
        for method in (remote.fetch_job, remote.cancel_job):
            with pytest.raises(ReadOnlyRefusedError, match="workstation is in read-only mode"):
                await method("a" * 32)

    asyncio.run(check())
