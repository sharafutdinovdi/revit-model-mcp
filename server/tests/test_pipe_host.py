from __future__ import annotations

import asyncio
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from unittest.mock import AsyncMock

from revit_model_mcp.pipe_host import (
    PIPE_PROTOCOL,
    LocalPipeHost,
    PipeDisconnectedError,
    PipeJobHost,
)
from revit_model_mcp.revit_channel import CLIENT_ID, ReadJob, RevitReadChannel

PID = 4242
INSTANCE_ID = "0f8fad5bd9cb469fa16570867728950e"


def write_heartbeat(directory: Path, protocols: list[str] | None) -> None:
    status: dict[str, Any] = {
        "processId": PID,
        "revitVersion": "2026",
        "documentTitle": "SampleModel",
        "documentPath": r"C:\Models\SampleModel.rvt",
        "updatedUtc": datetime.now(timezone.utc).isoformat(),
        "fileChannelVersion": 2,
        "startedUtc": "2026-09-24T00:00:00Z",
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

    def __init__(self, drop_after_submit: int = 0, close_after_result: bool = False) -> None:
        self.drop_after_submit = drop_after_submit
        self.close_after_result = close_after_result
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
                            "documents": [],
                        }
                    )
                elif kind == "submit":
                    job = message["job"]
                    self.jobs[job["jobId"]] = job
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
                    job = self.jobs[message["jobId"]]
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
            selected, job = await host.select_job(ReadJob.ping())
            assert isinstance(selected, PipeJobHost)
            assert job.payload["targetProcessId"] == PID
            instances = await host.list_revit_instances()
            assert instances[0]["pluginResponding"] is True
            assert instances[0]["documents"][0]["isActive"] is True
            await host.aclose()

    asyncio.run(scenario())
    fallback.select_job.assert_not_awaited()
    fallback.list_revit_instances.assert_not_awaited()


def test_file_channel_is_used_when_pipe_is_not_advertised(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, None)
    fallback = AsyncMock()
    fallback.select_job.return_value = ("file-host", ReadJob.ping())
    connector = AsyncMock()

    selected, _ = asyncio.run(
        LocalPipeHost(fallback, connector, tmp_path).select_job(ReadJob.ping())
    )

    assert selected == "file-host"
    connector.assert_not_awaited()


def test_file_channel_is_used_when_the_advertised_pipe_is_unreachable(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])
    fallback = AsyncMock()
    fallback.select_job.return_value = ("file-host", ReadJob.ping())
    connector = AsyncMock(side_effect=PipeDisconnectedError("pipe not found"))

    selected, _ = asyncio.run(
        LocalPipeHost(fallback, connector, tmp_path).select_job(ReadJob.ping())
    )

    assert selected == "file-host"
    connector.assert_awaited_once_with(PID)


def test_hello_submit_and_pushed_result(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])

    async def scenario() -> tuple[dict[str, Any], FakeRevit]:
        async with FakeRevit() as revit:
            host = LocalPipeHost(AsyncMock(), revit.connect, tmp_path)
            result = await RevitReadChannel(host).execute(ReadJob.ping(), timeout_seconds=5)
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
            channel = RevitReadChannel(host)
            for _ in range(2):
                result = await channel.execute(ReadJob.ping(), timeout_seconds=5)
                assert result["success"] is True
            await host.aclose()
            return revit

    revit = asyncio.run(scenario())

    assert revit.connections == 2
    assert revit.messages(1) == ["hello", "submit"]
    assert revit.messages(2) == ["hello", "submit"]


def test_running_job_resumes_through_status_after_disconnect(tmp_path: Path) -> None:
    write_heartbeat(tmp_path, [PIPE_PROTOCOL, "file/2"])

    async def scenario() -> tuple[dict[str, Any], FakeRevit]:
        async with FakeRevit(drop_after_submit=1) as revit:
            host = LocalPipeHost(AsyncMock(), revit.connect, tmp_path)
            result = await RevitReadChannel(host).execute(ReadJob.ping(), timeout_seconds=5)
            await host.aclose()
            return result, revit

    result, revit = asyncio.run(scenario())

    assert result["success"] is True
    assert revit.messages(2) == ["hello", "status"]
    submitted = revit.received[1][1]["job"]["jobId"]
    assert revit.received[-1][1]["jobId"] == submitted
