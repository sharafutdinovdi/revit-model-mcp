"""Local named pipe transport (pipe/1) with a file channel fallback."""

from __future__ import annotations

import asyncio
import itertools
import json
import logging
import os
import shutil
import sys
import tempfile
from collections.abc import Awaitable, Callable
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

from revit_model_mcp.revit_channel import (
    CHANNEL_DIRECTORY,
    CLIENT_ID,
    JobPickupStatus,
    ReadJob,
    RemoteHost,
    ResponseParseError,
    RevitChannelError,
    matches_document,
    select_instance,
)
from revit_model_mcp.ssh_host import SshPowerShellHost

LOGGER = logging.getLogger(__name__)
PIPE_PROTOCOL = "pipe/1"
REQUEST_LIMIT_BYTES = 1024 * 1024
# Requests are capped at 1 MiB; command responses may be larger.
RESPONSE_LIMIT_BYTES = 256 * 1024 * 1024
REQUEST_TIMEOUT_SECONDS = 30.0
HEARTBEAT_STALE_SECONDS = 60.0
ERROR_PIPE_BUSY = 231
FINISHED_STATES = frozenset({"done", "failed", "cancelled"})

StreamPair = tuple[asyncio.StreamReader, asyncio.StreamWriter]
Connector = Callable[[int], Awaitable[StreamPair]]


class PipeDisconnectedError(RevitChannelError):
    pass


def pipe_address(process_id: int) -> str:
    return rf"\\.\pipe\RevitModelMcp.{process_id}"


def channel_root() -> Path | None:
    if override := os.environ.get("REVIT_MCP_CHANNEL_DIR"):
        return Path(override)
    base = os.environ.get("LOCALAPPDATA")
    return Path(base) / CHANNEL_DIRECTORY if base else None


async def open_windows_pipe(process_id: int) -> StreamPair:
    address = pipe_address(process_id)
    if sys.platform != "win32":
        raise PipeDisconnectedError("Named pipes are available only on Windows.")
    loop = asyncio.get_running_loop()
    create = getattr(loop, "create_pipe_connection", None)
    if create is None:
        raise PipeDisconnectedError("This event loop cannot open named pipes.")
    reader = asyncio.StreamReader(limit=RESPONSE_LIMIT_BYTES)
    for attempt in range(20):
        try:
            transport, protocol = await create(
                lambda: asyncio.StreamReaderProtocol(reader), address
            )
            return reader, asyncio.StreamWriter(transport, protocol, reader, loop)
        except OSError as error:
            # Every listening instance is taken for a moment; the add-in creates the next one.
            if getattr(error, "winerror", None) != ERROR_PIPE_BUSY or attempt == 19:
                raise PipeDisconnectedError(
                    f"Revit pipe {address} is not reachable: {error}"
                ) from None
            await asyncio.sleep(0.05)
    raise PipeDisconnectedError(f"Revit pipe {address} stayed busy.")


class PipeConnection:
    """One pipe/1 connection: correlates replies by id and final job pushes by jobId."""

    def __init__(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        self._reader = reader
        self._writer = writer
        self._ids = itertools.count(1)
        self._replies: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self._jobs: dict[str, asyncio.Future[dict[str, Any]]] = {}
        self.closed = False
        self.hello: dict[str, Any] = {}
        self._task = asyncio.create_task(self._read_loop())

    async def request(
        self, message: dict[str, Any], timeout: float = REQUEST_TIMEOUT_SECONDS
    ) -> dict[str, Any]:
        request_id = str(next(self._ids))
        line = (
            json.dumps({**message, "id": request_id}, ensure_ascii=False, separators=(",", ":"))
            + "\n"
        ).encode("utf-8")
        if len(line) > REQUEST_LIMIT_BYTES + 1:
            raise RevitChannelError("The request exceeds the 1 MiB pipe message limit.")
        if self.closed:
            raise PipeDisconnectedError("The Revit pipe connection is closed.")
        future: asyncio.Future[dict[str, Any]] = asyncio.get_running_loop().create_future()
        self._replies[request_id] = future
        try:
            self._writer.write(line)
            await self._writer.drain()
            return await asyncio.wait_for(future, timeout)
        except (ConnectionError, OSError) as error:
            self._close(PipeDisconnectedError(f"The Revit pipe connection failed: {error}"))
            raise PipeDisconnectedError(
                f"The Revit pipe connection failed during {message.get('type')}."
            ) from None
        except TimeoutError:
            raise RevitChannelError(
                f"Revit did not answer the pipe {message.get('type')} request within {timeout:g} s."
            ) from None
        finally:
            self._replies.pop(request_id, None)

    def watch(self, job_id: str) -> asyncio.Future[dict[str, Any]]:
        future = self._jobs.get(job_id)
        if future is None:
            future = asyncio.get_running_loop().create_future()
            if self.closed:
                future.set_exception(PipeDisconnectedError("The Revit pipe connection is closed."))
            else:
                self._jobs[job_id] = future
        return future

    def forget(self, job_id: str) -> None:
        self._jobs.pop(job_id, None)

    async def aclose(self) -> None:
        self._task.cancel()
        self._close(PipeDisconnectedError("The Revit pipe connection was closed."))

    async def _read_loop(self) -> None:
        error: RevitChannelError = PipeDisconnectedError("The Revit pipe connection closed.")
        try:
            while True:
                line = await self._reader.readline()
                if not line.endswith(b"\n"):
                    break
                try:
                    message = json.loads(line)
                except ValueError:
                    error = ResponseParseError("Revit sent an invalid pipe message.")
                    break
                if not isinstance(message, dict):
                    continue
                if message.get("type") == "job":
                    future = self._jobs.get(str(message.get("jobId")))
                    if (
                        future is not None
                        and not future.done()
                        and message.get("state") in FINISHED_STATES
                        and "result" in message
                    ):
                        self._jobs.pop(str(message.get("jobId")), None)
                        future.set_result(message)
                    continue
                future = self._replies.get(str(message.get("id")))
                if future is not None and not future.done():
                    future.set_result(message)
                elif message.get("type") == "error":
                    LOGGER.warning("Revit pipe error: %s", message.get("message"))
        except (ConnectionError, OSError, ValueError) as failure:
            error = PipeDisconnectedError(f"The Revit pipe connection failed: {failure}")
        finally:
            self._close(error)

    def _close(self, error: RevitChannelError) -> None:
        if self.closed:
            return
        self.closed = True
        for future in [*self._replies.values(), *self._jobs.values()]:
            if not future.done():
                future.set_exception(error)
                # Nobody may await a watcher any more; mark the exception as retrieved.
                future.exception()
        self._jobs.clear()
        self._writer.close()


def _protocols(instance: dict[str, Any]) -> list[Any]:
    protocols = instance.get("protocols")
    return protocols if isinstance(protocols, list) else []


def _error_text(reply: dict[str, Any]) -> str:
    code = reply.get("error")
    messages = {
        "queue_full": "Revit job queue is full for this client; retry after a short wait.",
        "actions_disabled": "Revit denied this request. Actions require the workstation allow-write gate and REVIT_MCP_ALLOW_WRITE=1 in the MCP server.",
    }
    return messages.get(code, f"Revit rejected the pipe request ({code}): {reply.get('message')}")


class LocalPipeHost:
    """REVIT_MCP_HOST=local: named pipe when the heartbeat advertises pipe/1, else the file channel."""

    local = True

    def __init__(
        self,
        fallback: SshPowerShellHost | None = None,
        connector: Connector = open_windows_pipe,
        directory: Path | None = None,
    ) -> None:
        self.fallback = fallback or SshPowerShellHost("local", local=True)
        self._connector = connector
        self._directory = directory
        self._connections: dict[int, PipeConnection] = {}
        self._connect_lock = asyncio.Lock()

    @property
    def root(self) -> Path | None:
        return self._directory if self._directory is not None else channel_root()

    def heartbeats(self) -> list[dict[str, Any]]:
        root = self.root
        if root is None or not root.is_dir():
            return []
        stale_before = datetime.now(timezone.utc) - timedelta(seconds=HEARTBEAT_STALE_SECONDS)
        instances: list[dict[str, Any]] = []
        for path in root.glob("instance_*.json"):
            try:
                status = json.loads(path.read_text(encoding="utf-8-sig"))
                updated = datetime.fromisoformat(status["updatedUtc"].replace("Z", "+00:00"))
                process_id = status["processId"]
                title = status["documentTitle"]
                document_path = status["documentPath"]
            except (OSError, KeyError, TypeError, ValueError, AttributeError):
                continue
            if (
                updated.tzinfo is None
                or updated < stale_before
                or type(process_id) is not int
                or path.name != f"instance_{process_id}.json"
                or not isinstance(title, str)
                or not isinstance(document_path, str)
            ):
                continue
            instances.append(
                {
                    "processId": process_id,
                    "revitVersion": status.get("revitVersion", ""),
                    "documentName": title,
                    "documentTitle": title,
                    "documentPath": document_path,
                    "windowTitle": "",
                    "pluginResponding": False,
                    "updatedUtc": status["updatedUtc"],
                    **{
                        key: status[key]
                        for key in (
                            "fileChannelVersion",
                            "startedUtc",
                            "httpPort",
                            "discoveryVersion",
                            "instanceId",
                            "pipeName",
                            "protocols",
                            "documents",
                        )
                        if key in status
                    },
                }
            )
        return sorted(instances, key=lambda item: item["processId"])

    async def list_revit_instances(self, document: str | None = None) -> list[dict[str, Any]]:
        instances = self.heartbeats()
        if not instances or any(PIPE_PROTOCOL not in _protocols(item) for item in instances):
            return await self.fallback.list_revit_instances(document)
        result = []
        for instance in instances:
            if document and not matches_document(instance, document):
                continue
            try:
                await self.connection(instance)
                instance["pluginResponding"] = True
            except RevitChannelError:
                instance["pluginResponding"] = False
            result.append(instance)
        return result

    async def select_job(self, job: ReadJob) -> tuple[RemoteHost, ReadJob]:
        instances = self.heartbeats()
        if any(PIPE_PROTOCOL in _protocols(item) for item in instances):
            instance = select_instance(instances, job)
            if PIPE_PROTOCOL in _protocols(instance):
                try:
                    connection = await self.connection(instance)
                except PipeDisconnectedError as error:
                    LOGGER.warning("Revit pipe unavailable; using the file channel: %s", error)
                else:
                    return PipeJobHost(self, instance, connection), replace(
                        job, payload={**job.payload, "targetProcessId": instance["processId"]}
                    )
        return await self.fallback.select_job(job)

    async def connection(self, instance: dict[str, Any]) -> PipeConnection:
        process_id = instance["processId"]
        async with self._connect_lock:
            current = self._connections.get(process_id)
            if (
                current is not None
                and not current.closed
                and current.hello.get("instanceId") == instance.get("instanceId")
            ):
                return current
            if current is not None:
                await current.aclose()
                del self._connections[process_id]
            reader, writer = await self._connector(process_id)
            connection = PipeConnection(reader, writer)
            try:
                hello = await connection.request(
                    {
                        "type": "hello",
                        "protocol": PIPE_PROTOCOL,
                        "clientId": CLIENT_ID,
                        "clientName": "revit-model-mcp",
                    },
                    timeout=10,
                )
                if hello.get("type") == "error":
                    raise RevitChannelError(_error_text(hello))
                if (
                    hello.get("type") != "hello"
                    or hello.get("pid") != process_id
                    or (
                        instance.get("instanceId")
                        and hello.get("instanceId") != instance["instanceId"]
                    )
                ):
                    raise RevitChannelError(
                        "The Revit pipe identity does not match its heartbeat. Retry discovery."
                    )
            except BaseException:
                await connection.aclose()
                raise
            connection.hello = hello
            self._connections[process_id] = connection
            return connection

    async def aclose(self) -> None:
        for connection in self._connections.values():
            await connection.aclose()
        self._connections.clear()


class PipeJobHost:
    """RemoteHost for one job on a selected pipe instance."""

    requires_identity = False

    def __init__(
        self, parent: LocalPipeHost, instance: dict[str, Any], connection: PipeConnection
    ) -> None:
        self._parent = parent
        self._instance = instance
        self._connection = connection
        self._job_id: str | None = None
        self._future: asyncio.Future[dict[str, Any]] | None = None
        self._result: str | None = None

    async def select_job(self, job: ReadJob) -> tuple[RemoteHost, ReadJob]:
        return await self._parent.select_job(job)

    async def prepare_job(self, name: str, content: str, command: str) -> set[str]:
        payload = json.loads(content)
        job_id = payload.get("jobId")
        if not isinstance(job_id, str) or not job_id:
            raise RevitChannelError("A jobId is required before submitting a pipe job.")
        self._job_id = job_id
        self._result = None
        # Watch first: the final push may follow the submit reply immediately.
        self._future = self._connection.watch(job_id)
        try:
            reply = await self._connection.request({"type": "submit", "job": payload})
        except PipeDisconnectedError:
            # The connection may have died before the write ever reached Revit (safe to
            # resubmit), or after Revit already queued the job but before the "submitted"
            # reply came back (resubmitting would fail with duplicate_job_id and would wrongly
            # report a job that is actually running). Reconnect and ask `status` first so a
            # job Revit already accepted is resumed instead of rejected or run twice.
            self._connection = await self._parent.connection(self._instance)
            self._future = self._connection.watch(job_id)
            status_reply = await self._connection.request({"type": "status", "jobId": job_id})
            if status_reply.get("type") == "status":
                if status_reply.get("state") in FINISHED_STATES and "result" in status_reply:
                    self._connection.forget(job_id)
                    result = status_reply["result"]
                    if not isinstance(result, dict):
                        raise ResponseParseError("The finished pipe job has no JSON result.")
                    self._result = json.dumps(result, ensure_ascii=False)
                return set()
            # Not found: the original submit never reached Revit; resubmitting is safe.
            reply = await self._connection.request({"type": "submit", "job": payload})
        if reply.get("type") == "error":
            self._connection.forget(job_id)
            raise RevitChannelError(_error_text(reply))
        if reply.get("type") != "submitted" or reply.get("jobId") != job_id:
            raise ResponseParseError("Revit sent an unexpected reply to a pipe submit.")
        return set()

    async def wait_until_trigger_is_gone(self, timeout_seconds: float) -> JobPickupStatus:
        return JobPickupStatus(
            taken=True, activation_attempts=0, trigger_present=False, elapsed_seconds=0
        )

    async def wait_for_new_response(
        self,
        command: str,
        known_names: set[str],
        timeout_seconds: float,
        correlation_id: str | None = None,
    ) -> str | None:
        if self._result is not None:
            return self._job_id
        if self._future is None or self._job_id is None:
            raise RevitChannelError("Submit a pipe job before waiting for its result.")
        loop = asyncio.get_running_loop()
        deadline = loop.time() + timeout_seconds
        while True:
            remaining = deadline - loop.time()
            if remaining <= 0:
                return None
            try:
                message = await asyncio.wait_for(asyncio.shield(self._future), remaining)
            except TimeoutError:
                return None
            except PipeDisconnectedError:
                message = await self._resume()
                if message is None:
                    continue
            result = message.get("result")
            if not isinstance(result, dict):
                raise ResponseParseError("The finished pipe job has no JSON result.")
            self._result = json.dumps(result, ensure_ascii=False)
            return self._job_id

    async def _resume(self) -> dict[str, Any] | None:
        # The add-in cancels a disconnected client's queued jobs, but a running job finishes.
        assert self._job_id is not None
        self._connection = await self._parent.connection(self._instance)
        self._future = self._connection.watch(self._job_id)
        reply = await self._connection.request({"type": "status", "jobId": self._job_id})
        if reply.get("type") == "error":
            self._connection.forget(self._job_id)
            raise RevitChannelError(
                f"The Revit pipe closed during jobId={self._job_id} and the job is no longer "
                f"known: {reply.get('message')} It may have executed; inspect the model before retrying an action."
            )
        if reply.get("state") in FINISHED_STATES and "result" in reply:
            self._connection.forget(self._job_id)
            return reply
        return None

    async def finish_job(
        self,
        response_name: str,
        cleanup_names: list[str],
        download_artifact: bool,
        save_to: str | None,
    ) -> tuple[str, str | None]:
        if self._result is None:
            raise ResponseParseError("The pipe job has no completed response.")
        local_path = None
        if download_artifact:
            response = json.loads(self._result)
            if response.get("success") is True:
                local_path = self._move_artifact(response, save_to)
        return self._result, local_path

    def _move_artifact(self, response: dict[str, Any], save_to: str | None) -> str:
        data = response.get("data")
        name = data.get("fileName") if isinstance(data, dict) else None
        if not isinstance(name, str) or not name or Path(name).name != name or "\\" in name:
            raise ResponseParseError("The image response contains an unsafe file name.")
        root = self._parent.root
        if root is None:
            raise RevitChannelError("The local channel directory is unknown; set LOCALAPPDATA.")
        source = root / "instances" / str(self._instance["processId"]) / name
        target = (
            Path(save_to).expanduser().resolve()
            if save_to
            else Path(tempfile.mkdtemp(prefix="revit-view-")) / name
        )
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            with source.open("rb") as image, target.open("xb") as output:
                shutil.copyfileobj(image, output)
        except FileExistsError as error:
            raise RevitChannelError(f"Local file already exists: {target}") from error
        except OSError as error:
            raise RevitChannelError(f"Could not copy the exported image: {error}") from error
        source.unlink(missing_ok=True)
        return str(target)

    async def delete_files(self, names: list[str]) -> None:
        # Pipe results travel in messages; the only file, an exported image, moves in finish_job.
        return
