from __future__ import annotations
import asyncio
import base64
import json
import logging
import os
import re
from collections import deque
from collections.abc import Callable
from datetime import datetime, timedelta, timezone
from revit_model_mcp.artifact_download import save_artifact
from revit_model_mcp.revit_channel import (
    ACTIVATION_TASK,
    CHANNEL_DIRECTORY,
    TRIGGER_FILE,
    ActivationError,
    JobPickupStatus,
    ResponseParseError,
    RevitChannelError,
    RevitNotRunningError,
    SshUnavailableError,
)
RELAY_CONNECTION_LIMIT = 5
RELAY_WINDOW_SECONDS = 30.0
POLL_INTERVAL_SECONDS = 10.0
POLL_COMMAND_TIMEOUT_SECONDS = 5.0
PICKUP_COMMAND_TIMEOUT_SECONDS = 10.0
ACTIVATION_DELAY_SECONDS = 60.0
INSTANCE_STALE_SECONDS = 60.0
LOGGER = logging.getLogger(__name__)
class RemoteCommandTimeoutError(RevitChannelError): pass
class RemoteCommandError(RevitChannelError):
    def __init__(self, returncode: int, detail: str) -> None:
        super().__init__(detail)
        self.returncode = returncode
class SshPowerShellHost:
    def __init__(
        self, host: str = "localhost", connect_timeout_seconds: int = 45,
        *, local: bool = False) -> None:
        if not re.fullmatch(r"[A-Za-z0-9_.@:-]+", host) or host.startswith("-"):
            raise ValueError("REVIT_MCP_HOST contains an invalid SSH host name.")
        self.host = host
        self.local = local
        self.connect_timeout_seconds = connect_timeout_seconds
        self._connection_starts: deque[float] = deque()
        self._connection_lock = asyncio.Lock()
    async def list_revit_instances(self, document: str | None = None) -> list[dict[str, object]]:
        filter_text = document.strip() if document else ""
        script = (
            f"$directory = {_ps_directory()}; "
            "$processes = @(Get-Process Revit -ErrorAction SilentlyContinue | ForEach-Object { "
            "[ordered]@{ processId = $_.Id; revitVersion = $_.FileVersionInfo.ProductVersion } }); "
            "$files = @(Get-ChildItem -LiteralPath $directory -Filter 'instance_*.json' -File -ErrorAction SilentlyContinue | "
            "ForEach-Object { [ordered]@{ name = $_.Name; content = [IO.File]::ReadAllText($_.FullName) } }); "
            "[ordered]@{ processes = $processes; files = $files } | ConvertTo-Json -Depth 4 -Compress"
        )
        try:
            package = json.loads(await self._run(script))
        except (json.JSONDecodeError, TypeError) as error:
            raise ResponseParseError(f"Revit instance list could not be parsed as JSON: {error}") from error
        return _parse_instance_package(package, filter_text, datetime.now(timezone.utc))
    async def prepare_job(self, name: str, content: str, command: str) -> set[str]:
        encoded = base64.b64encode(content.encode("utf-8")).decode("ascii")
        pattern = f"response_*_{_ps_quote(command)}*.json"
        script = (
            f"$directory = {_ps_directory()}; "
            "$revitRunning = $null -ne (Get-Process Revit -ErrorAction SilentlyContinue); "
            f"$responses = @(Get-ChildItem -LiteralPath $directory -Filter '{pattern}' -File -ErrorAction SilentlyContinue | "
            "ForEach-Object { $_.Name }); "
            "$result = [ordered]@{ revitRunning = $revitRunning; responses = $responses; published = $false; channelBusy = $false }; "
            "if (-not $revitRunning) { $result | ConvertTo-Json -Compress; exit }; "
            "New-Item -ItemType Directory -Force -Path $directory | Out-Null; "
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
        trigger_assignment = f"$trigger = Join-Path ({_ps_directory()}) '{TRIGGER_FILE}'; "
        trigger_check = "if (Test-Path -LiteralPath $trigger) { 'present' } else { 'gone' }"
        check_script = trigger_assignment + trigger_check
        activation_attempts = 0
        def pickup_script(elapsed_seconds: float) -> str:
            nonlocal activation_attempts
            if ACTIVATION_TASK and activation_attempts == 0 and elapsed_seconds >= ACTIVATION_DELAY_SECONDS:
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
    async def wait_for_new_response(self, command: str, known_names: set[str], timeout_seconds: float) -> str | None:
        known = ",".join(f"'{_ps_quote(name)}'" for name in sorted(known_names))
        pattern = f"response_*_{command}*.json"
        script = (
            f"$directory = {_ps_directory()}; $known = @({known}); "
            f"$candidate = Get-ChildItem -LiteralPath $directory -Filter '{pattern}' -File -ErrorAction SilentlyContinue | "
            "Where-Object { $known -notcontains $_.Name } | Sort-Object LastWriteTimeUtc | Select-Object -Last 1; "
            "if ($null -ne $candidate) { $candidate.Name }"
        )
        result, _, _ = await self._poll_for_change(script, "", timeout_seconds)
        return result
    async def _poll_for_change(
        self, script: str | Callable[[float], str], pending_output: str,
        timeout_seconds: float, command_timeout_seconds: float = POLL_COMMAND_TIMEOUT_SECONDS,
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
        self, response_name: str, cleanup_names: list[str],
        download_artifact: bool, save_to: str | None,
    ) -> tuple[str, str | None]:
        paths = ",".join(f"'{_ps_quote(name)}'" for name in cleanup_names)
        output = await self._run(
            f"$directory = {_ps_directory()}; $path = Join-Path $directory '{_ps_quote(response_name)}'; "
            "$artifactName = $null; try { $responseBytes = [IO.File]::ReadAllBytes($path); $artifact = $null; "
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
            f"$directory = {_ps_directory()}; @({paths}) | ForEach-Object {{ "
            "$path = Join-Path $directory $_; Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }"
        )
    async def _run(self, script: str, timeout_seconds: float = 60) -> str:
        encoded_script = base64.b64encode(script.encode("utf-16le")).decode("ascii")
        command = [
            "ssh",
            "-o",
            "BatchMode=yes",
            "-o",
            f"ConnectTimeout={self.connect_timeout_seconds}",
            self.host,
            "powershell.exe",
            "-NoProfile",
            "-NonInteractive",
            "-EncodedCommand",
            encoded_script]
        if self.local:
            command = command[6:]
        process: asyncio.subprocess.Process | None = None
        try:
            if not self.local:
                await self._reserve_connection()
            process = await asyncio.create_subprocess_exec(
                *command, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
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
    instances: list[dict[str, object]] = []
    stale_before = now - timedelta(seconds=INSTANCE_STALE_SECONDS)
    for item in files:
        try:
            status = json.loads(item["content"])
            updated = datetime.fromisoformat(status["updatedUtc"].replace("Z", "+00:00"))
            if updated.tzinfo is None or updated < stale_before:
                continue
            process_id = status["processId"]
            version = status["revitVersion"]
            title = status["documentTitle"]
            path = status["documentPath"]
            if not isinstance(process_id, int) or not all(
                isinstance(value, str) for value in (version, title, path)
            ):
                continue
        except (KeyError, TypeError, ValueError, json.JSONDecodeError):
            continue
        instances.append({
            "processId": process_id, "revitVersion": version,
            "documentName": title, "documentTitle": title, "documentPath": path,
            "windowTitle": "", "pluginResponding": True,
        })
    if instances:
        needle = filter_text.casefold()
        return sorted(
            (item for item in instances if not needle or needle in str(item["documentName"]).casefold()),
            key=lambda item: int(item["processId"]),
        )
    fallback: list[dict[str, object]] = []
    for process in processes:
        if not isinstance(process, dict) or not isinstance(process.get("processId"), int):
            continue
        fallback.append({
            "processId": process["processId"],
            "revitVersion": process.get("revitVersion", ""),
            "documentName": "", "documentTitle": "", "documentPath": "",
            "windowTitle": "", "pluginResponding": False,
        })
    return sorted(fallback, key=lambda item: int(item["processId"]))
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
