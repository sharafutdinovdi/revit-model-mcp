from __future__ import annotations

import asyncio
import base64
import copy
import json
import logging
import os
import re
import shlex
from collections import deque
from collections.abc import Callable
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from pathlib import Path

from revit_model_mcp.artifact_download import save_artifact
from revit_model_mcp.revit_channel import (
    ACTIVATION_TASK,
    CHANNEL_DIRECTORY,
    TRIGGER_FILE,
    ActivationError,
    JobPickupStatus,
    ReadJob,
    ResponseParseError,
    RevitChannelError,
    RevitNotRunningError,
    RevitReadChannel,
    SshUnavailableError,
    matches_document,
    select_instance,
)

RELAY_CONNECTION_LIMIT = 5
RELAY_WINDOW_SECONDS = 30.0
POLL_INTERVAL_SECONDS = 10.0
POLL_COMMAND_TIMEOUT_SECONDS = 5.0
PICKUP_COMMAND_TIMEOUT_SECONDS = 10.0
ACTIVATION_DELAY_SECONDS = 60.0
INSTANCE_STALE_SECONDS = 60.0
HANDSHAKE_TIMEOUT_SECONDS = 60.0
LOGGER = logging.getLogger(__name__)


class RemoteCommandTimeoutError(RevitChannelError):
    pass


class RemoteCommandError(RevitChannelError):
    def __init__(self, returncode: int, detail: str) -> None:
        super().__init__(detail)
        self.returncode = returncode


class SshPowerShellHost:
    def __init__(
        self, host: str = "localhost", connect_timeout_seconds: int = 45, *, local: bool = False
    ) -> None:
        if not re.fullmatch(r"[A-Za-z0-9_.@:-]+", host) or host.startswith("-"):
            raise ValueError("REVIT_MCP_HOST contains an invalid SSH host name.")
        self.host = host
        self.local = local
        self.connect_timeout_seconds = connect_timeout_seconds
        self._connection_starts: deque[float] = deque()
        self._connection_lock = asyncio.Lock()
        self._root_directory = _ps_directory()
        self._directory = self._root_directory
        self._instance: dict[str, object] | None = None

    @property
    def requires_identity(self) -> bool:
        return self._instance is not None and self._instance.get("fileChannelVersion") == 2

    async def _discover_instances(self) -> list[dict[str, object]]:
        script = (
            f"$directory = {self._root_directory}; "
            "$processes = @(Get-Process Revit -ErrorAction SilentlyContinue | ForEach-Object { "
            "[ordered]@{ processId = $_.Id; revitVersion = $_.FileVersionInfo.ProductVersion } }); "
            "$files = @(Get-ChildItem -LiteralPath $directory -Filter 'instance_*.json' -File -ErrorAction SilentlyContinue | "
            "ForEach-Object { [ordered]@{ name = $_.Name; content = [IO.File]::ReadAllText($_.FullName) } }); "
            "[ordered]@{ processes = $processes; files = $files } | ConvertTo-Json -Depth 4 -Compress"
        )
        try:
            package = json.loads(await self._run(script))
        except (json.JSONDecodeError, TypeError) as error:
            raise ResponseParseError(
                f"Revit instance list could not be parsed as JSON: {error}"
            ) from error
        return _parse_instance_package(package, "", datetime.now(timezone.utc))

    def _for_instance(self, instance: dict[str, object]) -> SshPowerShellHost:
        version = instance.get("fileChannelVersion")
        if "fileChannelVersion" in instance and (type(version) is not int or version != 2):
            raise RevitChannelError(
                f"Unsupported file channel version: {version!r}. Update the server and add-in."
            )
        if not instance.get("updatedUtc"):
            raise RevitChannelError(
                "The selected Revit instance has no fresh heartbeat; its file channel is unconfirmed."
            )
        if version == 2 and not instance.get("startedUtc"):
            raise RevitChannelError(
                "The selected Revit instance has no startup identity; its file channel is unconfirmed."
            )
        selected = copy.copy(self)
        selected._instance = dict(instance)
        selected._directory = (
            f"(Join-Path ({self._root_directory}) 'instances\\{instance['processId']}')"
            if version == 2
            else self._root_directory
        )
        return selected

    async def list_revit_instances(self, document: str | None = None) -> list[dict[str, object]]:
        instances = await self._discover_instances()
        for instance in instances:
            if document and not matches_document(instance, document):
                continue
            if instance.get("fileChannelVersion") == 2:
                try:
                    await self._for_instance(instance)._handshake()
                    instance["pluginResponding"] = True
                except RevitChannelError:
                    instance["pluginResponding"] = False
        return [item for item in instances if not document or matches_document(item, document)]

    async def select_job(self, job: ReadJob) -> tuple[SshPowerShellHost, ReadJob]:
        instances = await self._discover_instances()
        instance = select_instance(instances, job)
        selected = self._for_instance(instance)
        if instance.get("fileChannelVersion") is None:
            if len(instances) != 1:
                raise RevitChannelError(
                    "Legacy file channels require exactly one running Revit instance. Update the add-in for per-PID routing."
                )
        else:
            await selected._handshake()
        return selected, replace(
            job, payload={**job.payload, "targetProcessId": instance["processId"]}
        )

    async def _verify_identity(self) -> None:
        instances = await self._discover_instances()
        current = next(
            (item for item in instances if item["processId"] == self._instance["processId"]), None
        )
        if (
            current is None
            or not current.get("updatedUtc")
            or any(
                current.get(key) != self._instance.get(key)
                for key in ("startedUtc", "fileChannelVersion")
            )
        ):
            raise RevitChannelError(
                "The selected Revit instance identity changed or its heartbeat expired. Retry discovery."
            )

    async def _handshake(self) -> None:
        async def confirm() -> None:
            job = ReadJob(
                "ping", {"command": "ping", "targetProcessId": self._instance["processId"]}
            )
            await RevitReadChannel(self)._execute_serial(
                job, HANDSHAKE_TIMEOUT_SECONDS, HANDSHAKE_TIMEOUT_SECONDS
            )
            await self._verify_identity()

        try:
            await asyncio.wait_for(confirm(), HANDSHAKE_TIMEOUT_SECONDS)
        except TimeoutError as error:
            raise RevitChannelError(
                "The selected file channel is unconfirmed: handshake timed out; its ping may still execute later."
            ) from error

    async def prepare_job(self, name: str, content: str, command: str) -> set[str]:
        if self._instance is None:
            raise RevitChannelError("Select a Revit instance before publishing a job.")
        process_id = self._instance["processId"]
        identity_check = ""
        if self._instance.get("fileChannelVersion") == 2:
            identity = _ps_quote(str(self._instance["startedUtc"]))
            identity_check = (
                f"$heartbeat = Get-Content -LiteralPath (Join-Path ({self._root_directory}) 'instance_{process_id}.json') -Raw -ErrorAction Stop | ConvertFrom-Json; "
                f"if ($heartbeat.processId -ne {process_id} -or ([DateTime]$heartbeat.startedUtc).ToUniversalTime() -ne [DateTime]::Parse('{identity}').ToUniversalTime() -or $heartbeat.fileChannelVersion -ne 2 "
                "-or ([DateTime]$heartbeat.updatedUtc).ToUniversalTime() -lt [DateTime]::UtcNow.AddSeconds(-60)) { throw 'Selected Revit identity changed or heartbeat expired.' }; "
            )
        encoded = base64.b64encode(content.encode("utf-8")).decode("ascii")
        pattern = f"response_*_{_ps_quote(command)}*.json"
        script = (
            f"$directory = {self._directory}; "
            f"$revitRunning = $null -ne (Get-Process Revit -ErrorAction SilentlyContinue | Where-Object {{ $_.Id -eq {process_id} }}); "
            f"$responses = @(Get-ChildItem -LiteralPath $directory -Filter '{pattern}' -File -ErrorAction SilentlyContinue | "
            "ForEach-Object { $_.Name }); "
            "$result = [ordered]@{ revitRunning = $revitRunning; responses = $responses; published = $false; channelBusy = $false }; "
            "if (-not $revitRunning) { $result | ConvertTo-Json -Compress; exit }; "
            + identity_check
            + "New-Item -ItemType Directory -Force -Path $directory | Out-Null; "
            f"$source = Join-Path $directory '{_ps_quote(name)}'; $target = Join-Path $directory '{TRIGGER_FILE}'; "
            "if (Test-Path -LiteralPath $target) { $result.channelBusy = $true; $result | ConvertTo-Json -Compress; exit }; "
            f"$bytes = [Convert]::FromBase64String('{encoded}'); "
            "[IO.File]::WriteAllBytes($source, $bytes); "
            "try { [IO.File]::Move($source, $target); $result.published = $true } "
            "catch { Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue; "
            "if (Test-Path -LiteralPath $target) { $result.channelBusy = $true } else { throw } }; "
            "$result | ConvertTo-Json -Compress"
        )
        try:
            result = json.loads(await self._run(script))
        except (json.JSONDecodeError, TypeError) as error:
            raise ResponseParseError(
                f"Job preparation result could not be parsed as JSON: {error}"
            ) from error
        if not isinstance(result, dict):
            raise ResponseParseError("Preparation result must be a JSON object.")
        if result.get("revitRunning") is not True:
            raise RevitNotRunningError(
                f"Revit is not running on {self.host}. Open Revit and a model before calling the tool."
            )
        if result.get("channelBusy") is True:
            raise RevitChannelError(
                "RevitModelMcp channel is busy: trigger.txt already exists. Wait for the current job and retry."
            )
        if result.get("published") is not True:
            raise RevitChannelError("Remote preparation did not publish the job.")
        responses = result.get("responses")
        if not isinstance(responses, list) or not all(isinstance(x, str) for x in responses):
            raise ResponseParseError("Preparation result contains an invalid response list.")
        return set(responses)

    async def wait_until_trigger_is_gone(self, timeout_seconds: float) -> JobPickupStatus:
        trigger_assignment = f"$trigger = Join-Path ({self._directory}) '{TRIGGER_FILE}'; "
        trigger_check = "if (Test-Path -LiteralPath $trigger) { 'present' } else { 'gone' }"
        check_script = trigger_assignment + trigger_check
        activation_attempts = 0

        def pickup_script(elapsed_seconds: float) -> str:
            nonlocal activation_attempts
            if (
                ACTIVATION_TASK
                and activation_attempts == 0
                and elapsed_seconds >= ACTIVATION_DELAY_SECONDS
            ):
                activation_attempts = 1
                LOGGER.warning(
                    "Job was not picked up within a minute; if trigger.txt is still "
                    "present, the Revit window will be restored and focused "
                    "through scheduled task %s.",
                    ACTIVATION_TASK,
                )
                return (
                    trigger_assignment
                    + "if (Test-Path -LiteralPath $trigger) { "
                    + self._activation_script()
                    + "; "
                    + trigger_check
                    + " } else { 'gone' }"
                )
            return check_script

        try:
            result, _, elapsed = await self._poll_for_change(
                pickup_script,
                "present",
                timeout_seconds,
                command_timeout_seconds=PICKUP_COMMAND_TIMEOUT_SECONDS,
            )
        except RemoteCommandError as error:
            if error.returncode == 32:
                raise ActivationError(
                    f"Could not activate Revit through task {ACTIVATION_TASK}: {error}"
                ) from error
            raise
        return JobPickupStatus(
            taken=result == "gone",
            activation_attempts=activation_attempts,
            trigger_present=result != "gone",
            elapsed_seconds=elapsed,
        )

    async def wait_for_new_response(
        self,
        command: str,
        known_names: set[str],
        timeout_seconds: float,
        correlation_id: str | None = None,
    ) -> str | None:
        known = ",".join(f"'{_ps_quote(name)}'" for name in sorted(known_names))
        pattern = f"response_*_{command}*.json"
        selection = "Select-Object -Last 1"
        if correlation_id is not None:
            identity = _ps_quote(correlation_id)
            fallback = "$null" if self.requires_identity else "$legacy"
            selection = (
                "ForEach-Object { "
                "$file = $_; $response = $null; "
                "try { $response = [Text.Encoding]::UTF8.GetString((Read-ResponseBytes $file.FullName)) | ConvertFrom-Json -ErrorAction Stop } catch {}; "
                f"if ($response.correlationId -eq '{identity}' -or "
                f"($file.Name.EndsWith('_{identity}.json') -and -not $response.correlationId)) {{ "
                "$matched = $file } "
                "elseif ($null -ne $response -and -not $response.correlationId "
                f"-and $response.command -eq '{_ps_quote(command)}' "
                f"-and $file.Name -match '_{_ps_quote(command)}(?:_[0-9]{{2,}})?\\.json$') {{ $legacy = $file }} "
                f"}}; $candidate = if ($null -ne $matched) {{ $matched }} else {{ {fallback} }}"
            )
        script = (
            _ps_response_reader()
            + f"$directory = {self._directory}; $known = @({known}); $matched = $null; $legacy = $null; "
            f"$candidate = Get-ChildItem -LiteralPath $directory -Filter '{pattern}' -File -ErrorAction SilentlyContinue | "
            "Where-Object { $known -notcontains $_.Name } | Sort-Object LastWriteTimeUtc | "
            + selection
            + "; if ($null -ne $candidate) { $candidate.Name }"
        )
        result, _, _ = await self._poll_for_change(script, "", timeout_seconds)
        return result

    async def _poll_for_change(
        self,
        script: str | Callable[[float], str],
        pending_output: str,
        timeout_seconds: float,
        command_timeout_seconds: float = POLL_COMMAND_TIMEOUT_SECONDS,
    ) -> tuple[str | None, int, float]:
        loop = asyncio.get_running_loop()
        started = loop.time()
        deadline = loop.time() + timeout_seconds
        last_error: RevitChannelError | None = None
        successful_poll = False
        attempts = 0
        while True:
            remaining = deadline - loop.time()
            if remaining <= 0:
                if not successful_poll and last_error is not None:
                    raise last_error
                return None, attempts, loop.time() - started
            try:
                attempts += 1
                elapsed = loop.time() - started
                current_script = script(elapsed) if callable(script) else script
                output = await self._run(
                    current_script,
                    timeout_seconds=min(command_timeout_seconds, remaining),
                )
                successful_poll = True
                if output.strip() != pending_output:
                    return output.strip(), attempts, loop.time() - started
            except (SshUnavailableError, RemoteCommandTimeoutError) as error:
                last_error = error
            remaining = deadline - loop.time()
            if remaining <= 0:
                if not successful_poll and last_error is not None:
                    raise last_error
                return None, attempts, loop.time() - started
            await asyncio.sleep(min(POLL_INTERVAL_SECONDS, remaining))

    def _activation_script(self) -> str:
        task_name = _ps_quote(ACTIVATION_TASK)
        return (
            f"$output = & schtasks.exe /Run /TN '{task_name}' 2>&1; "
            "if ($LASTEXITCODE -ne 0) { Write-Error ($output -join ' '); exit 32 }; "
            "Start-Sleep -Milliseconds 750; "
            f"$deadline = [DateTime]::UtcNow.AddSeconds(5); $task = Get-ScheduledTask -TaskName '{task_name}' -ErrorAction Stop; "
            "while ($task.State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline) { "
            f"Start-Sleep -Milliseconds 200; $task = Get-ScheduledTask -TaskName '{task_name}' -ErrorAction Stop }}; "
            f"$info = Get-ScheduledTaskInfo -TaskName '{task_name}' -ErrorAction Stop; "
            "if ($task.State -eq 'Running' -or $info.LastTaskResult -ne 0) { exit 32 }"
        )

    async def finish_job(
        self,
        response_name: str,
        cleanup_names: list[str],
        download_artifact: bool,
        save_to: str | None,
    ) -> tuple[str, str | None]:
        paths = ",".join(f"'{_ps_quote(name)}'" for name in cleanup_names)
        output = await self._run(
            _ps_response_reader()
            + f"$directory = {self._directory}; $path = Join-Path $directory '{_ps_quote(response_name)}'; "
            "$artifactName = $null; try { $responseBytes = Read-ResponseBytes $path; $artifact = $null; "
            + (
                "$response = [Text.Encoding]::UTF8.GetString($responseBytes) | ConvertFrom-Json; "
                "$artifactName = [IO.Path]::GetFileName([string]$response.data.fileName); "
                "if ([string]::IsNullOrWhiteSpace($artifactName)) { throw 'Response does not contain an image file name.' }; "
                "$artifactPath = Join-Path $directory $artifactName; "
                "$artifact = [Convert]::ToBase64String([IO.File]::ReadAllBytes($artifactPath)); "
                if download_artifact
                else ""
            )
            + "$result = [ordered]@{ response = [Convert]::ToBase64String($responseBytes); "
            "artifactName = $artifactName; artifact = $artifact } } finally { "
            f"@({paths}) + @($artifactName) | Where-Object {{ -not [string]::IsNullOrWhiteSpace($_) }} | "
            "ForEach-Object { $cleanup = Join-Path $directory $_; "
            "Remove-Item -LiteralPath $cleanup -Force -ErrorAction SilentlyContinue } }; "
            "$result | ConvertTo-Json -Compress"
        )
        try:
            result = json.loads(output)
            content = base64.b64decode(result["response"], validate=True).decode("utf-8-sig")
            local_path = save_artifact(result, save_to) if download_artifact else None
            return content, local_path
        except (KeyError, TypeError, ValueError, UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ResponseParseError(
                f"Could not parse response and image after remote read: {error}"
            ) from error

    async def delete_files(self, names: list[str]) -> None:
        if not names:
            return
        paths = ",".join(f"'{_ps_quote(name)}'" for name in names)
        await self._run(
            f"$directory = {self._directory}; @({paths}) | ForEach-Object {{ "
            "$path = Join-Path $directory $_; Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }"
        )

    def _build_command(self, script: str) -> list[str]:
        encoded_script = base64.b64encode(script.encode("utf-16le")).decode("ascii")
        powershell = [
            "powershell.exe",
            "-NoProfile",
            "-NonInteractive",
            "-EncodedCommand",
            encoded_script,
        ]
        if self.local:
            return powershell
        command = [
            "ssh",
            "-o",
            "BatchMode=yes",
            "-o",
            f"ConnectTimeout={self.connect_timeout_seconds}",
        ]
        if os.environ.get("REVIT_MCP_SSH_MUX") != "0":
            runtime = os.environ.get("XDG_RUNTIME_DIR")
            # Unix sockets cap the path at about 100 bytes and ssh appends a random suffix, so keep this short.
            directory = (
                Path(runtime)
                if runtime
                else Path("/tmp") / f"revit-model-mcp-{getattr(os, 'getuid', lambda: 'user')()}"
            )
            directory.mkdir(mode=0o700, parents=True, exist_ok=True)
            directory.chmod(0o700)
            command.extend(
                [
                    "-o",
                    "ControlMaster=auto",
                    "-o",
                    f"ControlPath={directory}/mux-%C",
                    "-o",
                    "ControlPersist=600",
                ]
            )
        command.extend(shlex.split(os.environ.get("REVIT_MCP_SSH_OPTIONS", "")))
        return command + [self.host] + powershell

    async def _run(self, script: str, timeout_seconds: float = 60) -> str:
        command = self._build_command(script)
        process: asyncio.subprocess.Process | None = None
        try:
            if not self.local:
                await self._reserve_connection()
            process = await asyncio.create_subprocess_exec(
                *command, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE
            )
            stdout, stderr = await asyncio.wait_for(process.communicate(), timeout=timeout_seconds)
        except asyncio.CancelledError:
            if process is not None and process.returncode is None:
                process.kill()
                await process.communicate()
            raise
        except FileNotFoundError as error:
            if process is not None and process.returncode is None:
                process.kill()
                await process.communicate()
            raise SshUnavailableError(
                "Could not start the transport executable. Check that PowerShell (local mode) or ssh (SSH mode) is available in PATH."
            ) from error
        except asyncio.TimeoutError as error:
            if process is not None and process.returncode is None:
                process.kill()
                await process.communicate()
            raise RemoteCommandTimeoutError(
                f"Command on {self.host} did not complete within {timeout_seconds:g} s and was stopped. "
                "This is a command execution timeout, not evidence that SSH is unavailable."
            ) from error
        if process is None:
            raise SshUnavailableError(
                f"Host {self.host} is unavailable: the transport process did not start."
            )
        output = stdout.decode("utf-8", errors="replace").strip()
        detail = stderr.decode("utf-8", errors="replace").strip()
        if process.returncode == 255:
            if _looks_like_connection_failure(detail):
                raise SshUnavailableError(
                    f"SSH connection to {self.host} was not established: "
                    f"{detail or 'ssh did not report a reason.'} Check the route, tunnel and port availability."
                )
            raise SshUnavailableError(
                f"ssh for host {self.host} failed (code 255): "
                f"{detail or 'no reason given.'} The connection may have been established; inspect the ssh message."
            )
        if process.returncode != 0:
            raise RemoteCommandError(
                process.returncode or 1,
                f"Transport command on {self.host} exited with code {process.returncode}: "
                f"{detail or output or 'no reason given.'}",
            )
        return output

    async def _reserve_connection(self) -> None:
        async with self._connection_lock:
            loop = asyncio.get_running_loop()
            while True:
                now = loop.time()
                cutoff = now - RELAY_WINDOW_SECONDS
                while self._connection_starts and self._connection_starts[0] <= cutoff:
                    self._connection_starts.popleft()
                if len(self._connection_starts) < RELAY_CONNECTION_LIMIT:
                    self._connection_starts.append(now)
                    return
                await asyncio.sleep(
                    RELAY_WINDOW_SECONDS - (now - self._connection_starts[0]) + 0.01
                )


def _parse_instance_package(
    package: object, filter_text: str, now: datetime
) -> list[dict[str, object]]:
    if not isinstance(package, dict):
        raise ResponseParseError("Revit instance list must be a JSON object.")
    files = package.get("files")
    processes = package.get("processes")
    if not isinstance(files, list) or not isinstance(processes, list):
        raise ResponseParseError("Revit instance list contains invalid files or processes.")
    running = {
        item["processId"]: item
        for item in processes
        if isinstance(item, dict) and type(item.get("processId")) is int
    }
    instances: list[dict[str, object]] = []
    stale_before = now - timedelta(seconds=INSTANCE_STALE_SECONDS)
    for item in files:
        try:
            status = json.loads(item["content"])
            updated = datetime.fromisoformat(status["updatedUtc"].replace("Z", "+00:00"))
            if updated.tzinfo is None or updated < stale_before:
                continue
            process_id = status["processId"]
            if process_id not in running or item.get("name") != f"instance_{process_id}.json":
                continue
            version = status["revitVersion"]
            title = status["documentTitle"]
            path = status["documentPath"]
            if not isinstance(process_id, int) or not all(
                isinstance(value, str) for value in (version, title, path)
            ):
                continue
        except (KeyError, TypeError, ValueError, json.JSONDecodeError):
            continue
        instances.append(
            {
                "processId": process_id,
                "revitVersion": version,
                "documentName": title,
                "documentTitle": title,
                "documentPath": path,
                "windowTitle": "",
                "pluginResponding": "fileChannelVersion" not in status,
                "updatedUtc": status["updatedUtc"],
                **{
                    key: status[key]
                    for key in ("fileChannelVersion", "startedUtc", "httpPort")
                    if key in status
                },
            }
        )
    fallback = instances
    confirmed_ids = {item["processId"] for item in instances}
    for process in processes:
        if not isinstance(process, dict) or not isinstance(process.get("processId"), int):
            continue
        if process["processId"] in confirmed_ids:
            continue
        fallback.append(
            {
                "processId": process["processId"],
                "revitVersion": process.get("revitVersion", ""),
                "documentName": "",
                "documentTitle": "",
                "documentPath": "",
                "windowTitle": "",
                "pluginResponding": False,
            }
        )
    return sorted(
        (item for item in fallback if not filter_text or matches_document(item, filter_text)),
        key=lambda item: int(item["processId"]),
    )


def _ps_response_reader() -> str:
    # Atomic replacement on Windows requires readers to permit deletion of the old file.
    return (
        "function Read-ResponseBytes([string]$path) { "
        "$stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, "
        "([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)); "
        "try { $reader = New-Object IO.BinaryReader($stream); "
        "try { return ,$reader.ReadBytes([int]$stream.Length) } finally { $reader.Dispose() } "
        "} finally { $stream.Dispose() } }; "
    )


def _ps_directory() -> str:
    if override := os.environ.get("REVIT_MCP_CHANNEL_DIR"):
        return f"'{_ps_quote(override)}'"
    return f"(Join-Path $env:LOCALAPPDATA '{CHANNEL_DIRECTORY}')"


def _ps_quote(value: str) -> str:
    return value.replace("'", "''")


def _looks_like_connection_failure(detail: str) -> bool:
    normalized = detail.casefold()
    return any(
        marker in normalized
        for marker in (
            "connection timed out",
            "connection refused",
            "no route to host",
            "network is unreachable",
            "could not resolve hostname",
            "connection closed by remote host",
            "permission denied",
        )
    )
