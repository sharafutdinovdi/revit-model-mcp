from __future__ import annotations

import asyncio
import base64
import binascii
import copy
import json
import logging
import os
import re
import shlex
import stat
import tempfile
from collections import deque
from collections.abc import Callable
from dataclasses import replace
from datetime import datetime, timedelta, timezone
from functools import lru_cache
from pathlib import Path
from typing import Any

from revit_model_mcp.artifact_download import preflight_artifact_target, save_artifact
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
MUX_RECONNECT_SECONDS = 540.0
POLL_INTERVAL_SECONDS = 10.0
POLL_COMMAND_TIMEOUT_SECONDS = 5.0
PICKUP_COMMAND_TIMEOUT_SECONDS = 10.0
ACTIVATION_DELAY_SECONDS = 60.0
INSTANCE_STALE_SECONDS = 60.0
HANDSHAKE_TIMEOUT_SECONDS = 60.0
LOGGER = logging.getLogger(__name__)
RESPONSE_NAME = re.compile(
    r"response_[0-9]{8}_[0-9]{6}_[0-9]{3}_[A-Za-z0-9_-]+"
    r"(?:_(?:[A-Za-z0-9_.~-]|%[0-9A-Fa-f]{2})+)?\.json\Z"
)
STARTED_UTC = re.compile(
    r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}"
    r"(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\Z"
)


def _fail_dead_supervisor(state: dict[str, object]) -> dict[str, object]:
    if state.get("status") not in (0, 1):
        return state
    failed = copy.deepcopy(state)
    failed["status"] = 4
    for model in failed.get("models", []):
        if model.get("status") in (0, 1):
            model["status"] = 3
            model["error"] = "Batch supervisor process exited unexpectedly."
    return failed


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
        self._last_successful_ssh_time: list[float | None] = [None]
        self._root_directory = _ps_directory()
        self._directory = self._root_directory
        self._instance: dict[str, object] | None = None
        self._published_job_file: str | None = None

    @property
    def instance_info(self) -> dict[str, object]:
        return self._instance or {}

    @property
    def requires_identity(self) -> bool:
        return self._instance is not None and self._instance.get("fileChannelVersion") == 2

    async def batch_discover(self, folder: str, recursive: bool) -> list[str]:
        recurse = "-Recurse" if recursive else ""
        script = (
            f"$files = @(Get-ChildItem -LiteralPath '{_ps_quote(folder)}' -File {recurse} "
            "-ErrorAction Stop | Where-Object { $_.Extension -in '.rvt', '.rfa' } | "
            "ForEach-Object { $_.FullName }); "
            "$json = ConvertTo-Json -InputObject $files -Compress; "
            "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))"
        )
        output = await self._run(script)
        result = json.loads(base64.b64decode(output, validate=True))
        if not isinstance(result, list) or not all(isinstance(path, str) for path in result):
            raise ResponseParseError("Batch folder discovery returned invalid paths.")
        return result

    async def batch_create(self, run_id: str, content: str) -> None:
        if not re.fullmatch(r"[0-9a-f]{32}", run_id):
            raise RevitChannelError("Invalid batch run id.")
        await self._run(
            f"$root = Join-Path ({self._root_directory}) 'runs'; "
            f"$run = Join-Path $root '{run_id}'; "
            "New-Item -ItemType Directory -Force -Path $root | Out-Null; "
            "if (Test-Path -LiteralPath $run) { throw 'Batch run already exists.' }; "
            "New-Item -ItemType Directory -Path $run -ErrorAction Stop | Out-Null; "
            "$temporary = Join-Path $run 'run.tmp'; $target = Join-Path $run 'run.json'; "
            "try { $stream = [IO.File]::Create($temporary); "
            "try { [Console]::OpenStandardInput().CopyTo($stream) } finally { $stream.Dispose() }; "
            "[IO.File]::Move($temporary, $target) } catch { "
            "Remove-Item -LiteralPath $run -Recurse -Force -ErrorAction SilentlyContinue; throw }",
            input=content.encode("utf-8"),
        )

    async def batch_status(self, run_id: str) -> dict[str, object]:
        if not re.fullmatch(r"[0-9a-f]{32}", run_id):
            raise RevitChannelError("Invalid batch run id.")
        output = await self._run(
            f"$run = Join-Path (Join-Path ({self._root_directory}) 'runs') '{run_id}'; "
            "$path = Join-Path $run 'run.json'; "
            "if (!(Test-Path -LiteralPath $path)) { throw 'Batch run was not found.' }; "
            "$state = [IO.File]::ReadAllBytes($path); "
            "$decoded = [Text.Encoding]::UTF8.GetString($state); "
            "if ($decoded.Length -gt 0 -and $decoded[0] -eq [char]0xFEFF) { $decoded = $decoded.Substring(1) }; "
            "$parsed = ConvertFrom-Json $decoded; "
            "$alive = $null; "
            "if ($parsed.supervisorProcessId -and $parsed.supervisorProcessStartedUtc) { "
            "$supervisor = Get-Process -Id $parsed.supervisorProcessId -ErrorAction SilentlyContinue; "
            "$alive = $false; "
            "if ($supervisor) { try { $alive = $supervisor.StartTime.ToUniversalTime().ToString('o') -eq "
            "([DateTimeOffset]::Parse($parsed.supervisorProcessStartedUtc)).UtcDateTime.ToString('o') "
            "} catch { $alive = $true } } }; "
            "$cancel = Test-Path -LiteralPath (Join-Path $run 'cancel.json'); "
            "[ordered]@{ state = [Convert]::ToBase64String($state); cancel = $cancel; "
            "supervisorAlive = $alive } | ConvertTo-Json -Compress"
        )
        package = json.loads(output)
        state = json.loads(base64.b64decode(package["state"], validate=True))
        if not isinstance(state, dict):
            raise ResponseParseError("Batch run state is invalid.")
        if package.get("supervisorAlive") is False and state.get("status") in (0, 1):
            supervisor_id = state.get("supervisorProcessId")
            supervisor_started = state.get("supervisorProcessStartedUtc")
            if (
                type(supervisor_id) is not int
                or supervisor_id <= 0
                or not isinstance(supervisor_started, str)
            ):
                raise ResponseParseError("Batch supervisor identity is invalid.")
            failed = _fail_dead_supervisor(state)
            encoded = base64.b64encode(json.dumps(failed).encode("utf-8")).decode("ascii")
            await self._run(
                f"$run = Join-Path (Join-Path ({self._root_directory}) 'runs') '{run_id}'; "
                "$path = Join-Path $run 'run.json'; "
                "$current = ConvertFrom-Json ([IO.File]::ReadAllText($path)); "
                f"if ($current.supervisorProcessId -eq {supervisor_id} -and "
                f"$current.supervisorProcessStartedUtc -eq '{_ps_quote(supervisor_started)}' "
                "-and $current.status -in 0, 1) { "
                "$temporary = Join-Path $run ('run.' + [Guid]::NewGuid().ToString('N') + '.tmp'); "
                f"[IO.File]::WriteAllBytes($temporary, [Convert]::FromBase64String('{encoded}')); "
                "try { [IO.File]::Replace($temporary, $path, $null) } finally { "
                "if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } } }"
            )
            return await self.batch_status(run_id)
        if package["cancel"]:
            state["cancelRequested"] = True
        return state

    async def batch_cancel(self, run_id: str) -> None:
        if not re.fullmatch(r"[0-9a-f]{32}", run_id):
            raise RevitChannelError("Invalid batch run id.")
        await self._run(
            "if (Test-Path -LiteralPath (Join-Path (Join-Path $env:LOCALAPPDATA 'RevitModelMcp') 'read-only')) "
            "{ throw 'read-only mode' }; "
            f"$run = Join-Path (Join-Path ({self._root_directory}) 'runs') '{run_id}'; "
            "if (!(Test-Path -LiteralPath (Join-Path $run 'run.json'))) { throw 'Batch run was not found.' }; "
            "$target = Join-Path $run 'cancel.json'; "
            "if (!(Test-Path -LiteralPath $target)) { "
            "$temporary = Join-Path $run ('cancel.' + [Guid]::NewGuid().ToString('N') + '.tmp'); "
            "[IO.File]::WriteAllText($temporary, '{\"cancelRequested\":true}'); "
            "try { [IO.File]::Move($temporary, $target) } finally { "
            "if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } } }"
        )

    async def batch_fetch_artifact(self, run_id: str, name: str) -> dict[str, str]:
        if not re.fullmatch(r"[0-9a-f]{32}", run_id) or not re.fullmatch(
            r"snapshot_[0-9]{4}\.json", name
        ):
            raise RevitChannelError("Invalid batch artifact address.")
        output = await self._run(
            f"$run = Join-Path (Join-Path ({self._root_directory}) 'runs') '{run_id}'; "
            f"$path = Join-Path $run '{name}'; "
            "if (!(Test-Path -LiteralPath $path)) { throw 'Batch snapshot is missing.' }; "
            f"[ordered]@{{ artifactName = '{name}'; artifact = [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) }} | ConvertTo-Json -Compress"
        )
        result = json.loads(output)
        if not isinstance(result, dict):
            raise ResponseParseError("Batch artifact response is invalid.")
        return result

    async def batch_activate_interactive(self) -> None:
        if not ACTIVATION_TASK:
            raise ActivationError(
                "No interactive Revit scheduled task is configured in REVIT_MCP_ACTIVATE_TASK."
            )
        await self._run(self._activation_script())

    async def _discover_instances(self) -> list[dict[str, object]]:
        script = (
            f"$directory = {self._root_directory}; "
            "$processes = @(Get-Process Revit -ErrorAction SilentlyContinue | ForEach-Object { "
            "[ordered]@{ processId = $_.Id; revitVersion = $_.FileVersionInfo.ProductVersion } }); "
            "$files = @(Get-ChildItem -LiteralPath $directory -Filter 'instance_*.json' -File -ErrorAction SilentlyContinue | "
            "ForEach-Object { [ordered]@{ name = $_.Name; content = [IO.File]::ReadAllText($_.FullName) } }); "
            "$package = [ordered]@{ processes = $processes; files = $files } | ConvertTo-Json -Depth 4 -Compress; "
            "$bytes = [Text.Encoding]::UTF8.GetBytes($package); "
            "[Convert]::ToBase64String($bytes)"
        )
        try:
            output = await self._run(script)
            package = json.loads(
                base64.b64decode(output.strip(), validate=True).decode("utf-8-sig")
            )
        except (binascii.Error, UnicodeDecodeError, json.JSONDecodeError, TypeError) as error:
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
        if version == 2:
            try:
                started_utc = _validated_started_utc(instance["startedUtc"])
            except ValueError as error:
                raise RevitChannelError(
                    "The selected Revit instance has an invalid startup identity. Update the add-in."
                ) from error
            instance = {**instance, "startedUtc": started_utc}
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
        candidates = [
            item for item in instances if not document or matches_document(item, document)
        ]

        async def ping(instance: dict[str, object]) -> None:
            if instance.get("fileChannelVersion") == 2:
                try:
                    await self._for_instance(instance)._handshake(timeout=5.0)
                    instance["pluginResponding"] = True
                except RevitChannelError:
                    instance["pluginResponding"] = False

        await asyncio.gather(*(ping(instance) for instance in candidates))
        return candidates

    async def select_job(self, job: ReadJob) -> tuple[SshPowerShellHost, ReadJob]:
        instances = await self._discover_instances()
        instance = select_instance(instances, job)
        selected = self._for_instance(instance)
        if instance.get("fileChannelVersion") is None:
            if len(instances) != 1:
                raise RevitChannelError(
                    "Legacy file channels require exactly one running Revit instance. Update the add-in for per-PID routing."
                )
        elif not job.payload.get("fetchJobId"):
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

    async def _handshake(self, timeout: float | None = None) -> None:
        resolved_timeout = HANDSHAKE_TIMEOUT_SECONDS if timeout is None else timeout

        async def confirm() -> None:
            job = ReadJob(
                "ping", {"command": "ping", "targetProcessId": self._instance["processId"]}
            )
            await RevitReadChannel(self)._execute_serial(job, resolved_timeout, resolved_timeout)
            await self._verify_identity()

        try:
            await asyncio.wait_for(confirm(), resolved_timeout)
        except TimeoutError as error:
            raise RevitChannelError(
                "The selected file channel is unconfirmed: handshake timed out; its ping may still execute later."
            ) from error

    async def fetch_job(self, job_id: str) -> dict[str, Any]:
        if not re.fullmatch(r"[0-9a-fA-F]{32}", job_id):
            raise RevitChannelError("Invalid action job id.")
        output = await self._run(
            _ps_response_reader() + f"$path = Join-Path ({self._directory}) 'jobs/{job_id}.json'; "
            "if (-not (Test-Path -LiteralPath $path)) { "
            f"$queued = Join-Path ({self._directory}) 'job_{job_id}.json'; "
            "if (-not (Test-Path -LiteralPath $queued)) { throw 'Job not found or expired.' }; "
            "$job = Get-Content -LiteralPath $queued -Raw | ConvertFrom-Json; "
            "$pending = @{ command = $job.command; success = $true; partial = $true; "
            "message = 'Command accepted and running.'; data = @{ state = 'queued' } } | ConvertTo-Json -Compress; "
            "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pending)); exit }; "
            "if ((Get-Item -LiteralPath $path).LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-24)) { "
            "Remove-Item -LiteralPath $path; throw 'Job expired.' }; "
            "[Convert]::ToBase64String((Read-ResponseBytes $path))"
        )
        return json.loads(base64.b64decode(output, validate=True).decode("utf-8-sig"))

    async def cancel_job(self, job_id: str) -> dict[str, Any]:
        response = await self.fetch_job(job_id)
        if (
            response.get("command") != "process-models"
            or response.get("partial") is not True
            or response.get("message") != "Command accepted and running."
        ):
            return {
                "cancelled": False,
                "message": "The command cannot be interrupted or has already finished.",
            }
        await self._run(
            f"$root = {self._root_directory}; "
            "if (Test-Path -LiteralPath (Join-Path $root 'read-only')) { throw 'read-only mode' }; "
            f"$jobs = Join-Path ({self._directory}) 'jobs'; "
            "New-Item -ItemType Directory -Force -Path $jobs | Out-Null; "
            f"$path = Join-Path $jobs '{job_id}.cancel'; "
            "[IO.File]::WriteAllText($path, '')"
        )
        return {
            "cancelled": True,
            "jobId": job_id,
            "message": "Cancellation requested before the next model.",
        }

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
        job_id = json.loads(content).get("jobId")
        if not isinstance(job_id, str) or not re.fullmatch(r"[0-9a-fA-F]{32}", job_id):
            raise RevitChannelError("A jobId GUID is required before publishing a file job.")
        target_name = f"job_{job_id}.json"
        script = (
            f"$directory = {self._directory}; "
            f"$revitRunning = $null -ne (Get-Process Revit -ErrorAction SilentlyContinue | Where-Object {{ $_.Id -eq {process_id} }}); "
            f"$responses = @(Get-ChildItem -LiteralPath $directory -Filter '{pattern}' -File -ErrorAction SilentlyContinue | "
            "ForEach-Object { $_.Name }); "
            "$result = [ordered]@{ revitRunning = $revitRunning; responses = $responses; published = $false }; "
            "if (-not $revitRunning) { $result | ConvertTo-Json -Compress; exit }; "
            + identity_check
            + "New-Item -ItemType Directory -Force -Path $directory | Out-Null; "
            f"$source = Join-Path $directory '{_ps_quote(name)}'; $target = Join-Path $directory '{target_name}'; "
            f"$bytes = [Convert]::FromBase64String('{encoded}'); "
            "[IO.File]::WriteAllBytes($source, $bytes); "
            "try { [IO.File]::Move($source, $target); $result.published = $true } "
            "catch { Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue; throw }; "
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
        if result.get("published") is not True:
            raise RevitChannelError("Remote preparation did not publish the job.")
        self._published_job_file = target_name
        responses = result.get("responses")
        if not isinstance(responses, list) or not all(isinstance(x, str) for x in responses):
            raise ResponseParseError("Preparation result contains an invalid response list.")
        return {name for name in responses if RESPONSE_NAME.fullmatch(name)}

    async def wait_until_trigger_is_gone(self, timeout_seconds: float) -> JobPickupStatus:
        trigger_assignment = f"$trigger = Join-Path ({self._directory}) '{self._published_job_file or TRIGGER_FILE}'; "
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
                    "Job was not picked up within a minute; if its job file is still "
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
        known = ",".join(
            f"'{_ps_quote(name)}'" for name in sorted(known_names) if RESPONSE_NAME.fullmatch(name)
        )
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
            f"Where-Object {{ $_.Name -cmatch '{RESPONSE_NAME.pattern}' -and $known -notcontains $_.Name }} | Sort-Object LastWriteTimeUtc | "
            + selection
            + "; if ($null -ne $candidate) { $candidate.Name }"
        )
        result, _, _ = await self._poll_for_change(script, "", timeout_seconds)
        return result if result is None or RESPONSE_NAME.fullmatch(result) else None

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
        if not RESPONSE_NAME.fullmatch(response_name):
            raise ResponseParseError("Remote response has an invalid file name. Update the add-in.")
        if download_artifact:
            try:
                preflight_artifact_target(save_to)
            except ValueError as error:
                raise RevitChannelError(str(error)) from error
        paths = ",".join(f"'{_ps_quote(name)}'" for name in cleanup_names)
        output = await self._run(
            _ps_response_reader()
            + f"$directory = {self._directory}; $path = Join-Path $directory '{_ps_quote(response_name)}'; "
            "$artifactName = $null; "
            + ("" if download_artifact else "try { ")
            + "$responseBytes = Read-ResponseBytes $path; $artifact = $null; "
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
            "artifactName = $artifactName; artifact = $artifact }"
            + (
                "; "
                if download_artifact
                else " } finally { "
                f"@({paths}) + @($artifactName) | Where-Object {{ -not [string]::IsNullOrWhiteSpace($_) }} | "
                "ForEach-Object { $cleanup = Join-Path $directory $_; "
                "Remove-Item -LiteralPath $cleanup -Force -ErrorAction SilentlyContinue } }; "
            )
            + "$result | ConvertTo-Json -Compress"
        )
        try:
            result = json.loads(output)
            content = base64.b64decode(result["response"], validate=True).decode("utf-8-sig")
            if download_artifact:
                name = result.get("artifactName")
                encoded = result.get("artifact")
                if (
                    not isinstance(name, str)
                    or Path(name).name != name
                    or not isinstance(encoded, str)
                ):
                    raise ValueError("Remote response does not contain a safe image artifact.")
        except (KeyError, TypeError, ValueError, UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ResponseParseError(
                f"Could not parse response and image after remote read: {error}"
            ) from error
        if not download_artifact:
            return content, None
        try:
            local_path = save_artifact(result, save_to)
        except binascii.Error as error:
            raise ResponseParseError(
                f"Could not parse response and image after remote read: {error}"
            ) from error
        except ValueError as error:
            raise RevitChannelError(str(error)) from error
        names = list(dict.fromkeys(item for item in cleanup_names + [name] if item))
        try:
            await self.delete_files(names)
        except RevitChannelError:
            LOGGER.warning("Image saved, but remote capture cleanup failed.")
        return content, local_path

    async def delete_files(self, names: list[str]) -> None:
        if not names:
            return
        paths = ",".join(f"'{_ps_quote(name)}'" for name in names)
        await self._run(
            f"$directory = {self._directory}; @({paths}) | ForEach-Object {{ "
            "$path = Join-Path $directory $_; Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }"
        )

    def _build_command(self, script: str, *, takes_input: bool = False) -> list[str]:
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
            "-T",
            *([] if takes_input else ["-n"]),
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
        ]
        if os.environ.get("REVIT_MCP_SSH_MUX") != "0":
            directory = _mux_directory(os.environ.get("XDG_RUNTIME_DIR"))
            if directory is not None:
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

    async def _run(
        self, script: str, timeout_seconds: float = 60, *, input: bytes | None = None
    ) -> str:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + timeout_seconds
        command = self._build_command(script, takes_input=input is not None)
        multiplexed = os.environ.get("REVIT_MCP_SSH_MUX") != "0" and "ControlMaster=auto" in command
        process: asyncio.subprocess.Process | None = None
        try:
            if not self.local:
                await asyncio.wait_for(
                    self._reserve_connection(multiplexed), timeout=timeout_seconds
                )
            if deadline - loop.time() <= 0:
                raise asyncio.TimeoutError
            process = await asyncio.create_subprocess_exec(
                *command,
                stdin=asyncio.subprocess.PIPE if input is not None else asyncio.subprocess.DEVNULL,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(
                process.communicate(input=input), timeout=deadline - loop.time()
            )
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
            if process is None:
                raise RemoteCommandTimeoutError(
                    f"Command on {self.host} was not started: the SSH connection quota "
                    f"({RELAY_CONNECTION_LIMIT} connections per {RELAY_WINDOW_SECONDS:g} s) "
                    f"did not free up within {timeout_seconds:g} s. Nothing was sent to the host."
                ) from error
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
        if multiplexed and not self.local:
            self._last_successful_ssh_time[0] = asyncio.get_running_loop().time()
        return output

    async def _reserve_connection(self, multiplexed: bool = False) -> None:
        async with self._connection_lock:
            loop = asyncio.get_running_loop()
            while True:
                now = loop.time()
                last_success = self._last_successful_ssh_time[0]
                if (
                    multiplexed
                    and last_success is not None
                    and now - last_success < MUX_RECONNECT_SECONDS
                ):
                    return
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
            if "startedUtc" in status:
                status["startedUtc"] = _validated_started_utc(status["startedUtc"])
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
                    for key in (
                        "fileChannelVersion",
                        "startedUtc",
                        "httpPort",
                        "httpState",
                        "httpReason",
                        "discoveryVersion",
                        "instanceId",
                        "pipeName",
                        "protocols",
                        "documents",
                        "addinVersion",
                        "protocolVersion",
                        "commands",
                    )
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
    for quote in ("'", "\u2018", "\u2019", "\u201a", "\u201b"):
        value = value.replace(quote, quote * 2)
    return value


def _validated_started_utc(value: object) -> str:
    if not isinstance(value, str) or not STARTED_UTC.fullmatch(value):
        raise ValueError("startup identity is not a timestamp")
    started = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if started.tzinfo is None:
        raise ValueError("startup identity has no time zone")
    return value


@lru_cache(maxsize=None)
def _mux_directory(runtime: str | None) -> Path | None:
    try:
        home = Path.home()
        temporary = Path(tempfile.gettempdir())
        if runtime:
            directory = Path(runtime)
        elif temporary != Path("/tmp"):
            directory = temporary / f"revit-model-mcp-{getattr(os, 'getuid', lambda: 'user')()}"
        else:
            directory = home / ".cache" / "revit-model-mcp"
        if len(os.fsencode(f"{directory}/mux-{'0' * 40}")) > 86:
            directory = home / ".cache" / "rmm"
        if len(os.fsencode(f"{directory}/mux-{'0' * 40}")) > 86:
            raise ValueError("multiplexing socket path is too long")
        directory.mkdir(mode=0o700, parents=True, exist_ok=True)
        if hasattr(os, "getuid"):
            details = os.lstat(directory)
            if not stat.S_ISDIR(details.st_mode) or details.st_uid != os.getuid():
                raise ValueError("multiplexing directory is not owned by this user")
        directory.chmod(0o700)
        return directory
    except (OSError, RuntimeError, ValueError) as error:
        LOGGER.warning(
            "SSH multiplexing disabled: %s. Set REVIT_MCP_SSH_MUX=0 to silence this warning.", error
        )
        return None


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
