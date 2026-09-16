from __future__ import annotations

import asyncio
import copy
import json
import os
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import replace
from pathlib import Path
from typing import Any

from revit_model_mcp.revit_channel import (
    JobPickupStatus,
    ReadJob,
    ResponseParseError,
    ResponseTimeoutError,
    RevitChannelError,
    matches_document,
    select_instance,
)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class HttpHost:
    def __init__(self, host: str, token: str | None = None) -> None:
        parsed = urllib.parse.urlsplit(host)
        if (
            parsed.scheme not in {"http", "https"}
            or not parsed.hostname
            or parsed.username is not None
            or parsed.password is not None
            or parsed.query
            or parsed.fragment
        ):
            raise ValueError(
                "HTTP host must be an http:// or https:// URL without credentials, query or fragment."
            )
        self.host = host.rstrip("/")
        self.token = os.environ.get("REVIT_MCP_TOKEN", "") if token is None else token
        self._opener = urllib.request.build_opener(_NoRedirect)
        self._job_id: str | None = None
        self._response: str | None = None
        self._payload: dict[str, Any] = {}
        self._identity: dict[str, Any] | None = None

    async def health(self) -> dict[str, Any]:
        _, body, _ = await self._request("GET", "/health", authenticated=False)
        result = self._json(body)
        if result.get("ok") is not True or not isinstance(result.get("processId"), int):
            raise ResponseParseError("Invalid Revit health response.")
        return result

    async def list_revit_instances(self, document: str | None = None) -> list[dict[str, object]]:
        status = await self.health()
        name = status.get("documentName", "")
        if document and not matches_document(status, document):
            return []
        return [
            {
                **status,
                "documentTitle": name,
                "documentPath": "",
                "windowTitle": "",
                "pluginResponding": True,
            }
        ]

    async def select_job(self, job: ReadJob) -> tuple[HttpHost, ReadJob]:
        instance = select_instance(await self.list_revit_instances(), job)
        selected = copy.copy(self)
        selected._identity = instance
        return selected, replace(
            job, payload={**job.payload, "targetProcessId": instance["processId"]}
        )

    async def prepare_job(self, name: str, content: str, command: str) -> set[str]:
        if self._identity is not None:
            status = await self.health()
            if any(
                status.get(key) != self._identity.get(key) for key in ("processId", "startedUtc")
            ):
                raise RevitChannelError(
                    "The configured HTTP endpoint identity changed before submission. Retry discovery."
                )
        self._job_id = None
        self._response = None
        self._payload = json.loads(content)
        status, body, headers = await self._request(
            "POST", "/jobs?timeout=0", content.encode("utf-8")
        )
        self._job_id = headers.get("X-Revit-Job-Id")
        if status == 202:
            self._job_id = self._pending_id(body)
        elif status == 200:
            self._response = body.decode("utf-8-sig")
        else:
            raise ResponseParseError(f"Unexpected job submission status: {status}.")
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
        loop = asyncio.get_running_loop()
        deadline = loop.time() + timeout_seconds
        while self._response is None:
            remaining = deadline - loop.time()
            if remaining <= 0:
                raise ResponseTimeoutError(
                    f"Revit job {self._job_id} has no result after {timeout_seconds:g} s. "
                    f"Fetch it at {self.host}/jobs/{self._job_id}. It may still execute; "
                    "inspect the result before retrying an action."
                )
            if self._job_id is None:
                raise ResponseParseError("The HTTP response did not contain a job id.")
            status, body, _ = await self._request(
                "GET",
                "/jobs/" + urllib.parse.quote(self._job_id, safe=""),
                timeout=min(10, remaining),
            )
            if status == 200:
                self._response = body.decode("utf-8-sig")
                break
            if status != 202:
                raise ResponseParseError(f"Unexpected job result status: {status}.")
            await asyncio.sleep(min(0.25, max(0, deadline - loop.time())))
        return self._job_id or "completed"

    async def finish_job(
        self,
        response_name: str,
        cleanup_names: list[str],
        download_artifact: bool,
        save_to: str | None,
    ) -> tuple[str, str | None]:
        if self._response is None:
            raise ResponseParseError("The HTTP job has no completed response.")
        response = self._json(self._response.encode("utf-8"))
        local_path = None
        if download_artifact and response.get("success") is True:
            if self._job_id is None:
                raise ResponseParseError("The image response did not contain X-Revit-Job-Id.")
            view = urllib.parse.quote(self._payload["view"], safe="")
            query = {"pixel": self._payload.get("pixelSize", 1600), "jobId": self._job_id}
            if "targetDocument" in self._payload:
                query["document"] = self._payload["targetDocument"]
            status, image, headers = await self._request(
                "GET", f"/views/{view}/image?{urllib.parse.urlencode(query)}", timeout=130
            )
            if (
                status != 200
                or headers.get_content_type() != "image/png"
                or not image.startswith(b"\x89PNG\r\n\x1a\n")
            ):
                raise ResponseParseError("The Revit endpoint did not return a PNG image.")
            name = response.get("data", {}).get("fileName")
            if not isinstance(name, str) or not name or Path(name).name != name or "\\" in name:
                raise ResponseParseError("The image response contains an unsafe file name.")
            target = (
                Path(save_to).expanduser().resolve()
                if save_to
                else Path(tempfile.mkdtemp(prefix="revit-view-")) / name
            )
            target.parent.mkdir(parents=True, exist_ok=True)
            try:
                with target.open("xb") as output:
                    output.write(image)
            except FileExistsError as error:
                raise RevitChannelError(f"Local file already exists: {target}") from error
            local_path = str(target)
        return self._response, local_path

    async def delete_files(self, names: list[str]) -> None:
        # Server results remain available for ten minutes, including after client timeouts.
        return

    @staticmethod
    def _json(body: bytes) -> dict[str, Any]:
        try:
            value = json.loads(body)
        except (ValueError, UnicodeError) as error:
            raise ResponseParseError("The Revit endpoint returned invalid JSON.") from error
        if not isinstance(value, dict):
            raise ResponseParseError("The Revit endpoint must return a JSON object.")
        return value

    @classmethod
    def _pending_id(cls, body: bytes) -> str:
        job_id = cls._json(body).get("jobId")
        if not isinstance(job_id, str) or not job_id:
            raise ResponseParseError("The pending HTTP response did not contain a job id.")
        return job_id

    async def _request(
        self,
        method: str,
        path: str,
        body: bytes | None = None,
        timeout: float = 10,
        authenticated: bool = True,
    ):
        headers = {"Accept": "application/json, image/png"}
        if body is not None:
            headers["Content-Type"] = "application/json"
        if authenticated:
            if not self.token or any(
                ord(character) < 32 or ord(character) == 127 for character in self.token
            ):
                raise RevitChannelError(
                    "Set REVIT_MCP_TOKEN or --token to the token in the workstation settings.json."
                )
            headers["Authorization"] = "Bearer " + self.token
        request = urllib.request.Request(
            self.host + path, data=body, headers=headers, method=method
        )

        def send():
            try:
                with self._opener.open(request, timeout=timeout) as response:
                    return response.status, response.read(), response.headers
            except urllib.error.HTTPError as error:
                error.close()
                messages = {
                    401: "Revit rejected the bearer token. Check REVIT_MCP_TOKEN or --token against the workstation settings.json.",
                    403: "Revit denied this request. Actions require the workstation allow-write gate and REVIT_MCP_ALLOW_WRITE=1 in the MCP server.",
                    409: "Revit is busy with another job. Wait for it to finish before retrying.",
                    404: "Revit job or endpoint not found; completed results expire after ten minutes.",
                }
                raise RevitChannelError(
                    messages.get(error.code, f"Revit endpoint returned HTTP {error.code}.")
                ) from None
            except (TimeoutError, urllib.error.URLError, OSError) as error:
                if isinstance(error, TimeoutError) or isinstance(
                    getattr(error, "reason", None), TimeoutError
                ):
                    raise RevitChannelError(
                        f"Revit HTTP request timed out at {self.host}. A submitted job may still execute; "
                        "inspect the model before retrying an action."
                    ) from None
                raise RevitChannelError(
                    f"Revit endpoint not reachable at {self.host}: is Revit running with the add-in, "
                    "and is the port reachable from this machine?"
                ) from None

        return await asyncio.to_thread(send)
