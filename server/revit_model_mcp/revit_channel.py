from __future__ import annotations

import asyncio
import json
import logging
import os
import uuid
from dataclasses import dataclass, replace
from typing import Any, Protocol

from revit_model_mcp.universal_jobs import aggregate_payload, query_payload

LOGGER = logging.getLogger(__name__)
DEFAULT_HOST = "local"
DEFAULT_TIMEOUT_SECONDS = 120
DEFAULT_PICKUP_TIMEOUT_SECONDS = 300
ACTIVATION_TASK = os.environ.get("REVIT_MCP_ACTIVATE_TASK", "")
CHANNEL_DIRECTORY = "RevitModelMcp"
TRIGGER_FILE = "trigger.txt"
ACTION_COMMANDS = frozenset(
    {
        "select",
        "show",
        "isolate",
        "move",
        "place-family",
        "create-wall",
        "set-parameter",
        "delete",
        "batch",
        "export-nwc",
        "edit-families",
        "align-link-datums",
    }
)


class RevitChannelError(RuntimeError):
    """User-facing Revit read channel error."""


class SshUnavailableError(RevitChannelError):
    pass


class RevitNotRunningError(RevitChannelError):
    pass


class ActivationError(RevitChannelError):
    pass


class JobPickupTimeoutError(RevitChannelError):
    pass


class ResponseTimeoutError(RevitChannelError):
    pass


class PluginResponseError(RevitChannelError):
    pass


class ResponseParseError(RevitChannelError):
    pass


@dataclass(frozen=True)
class ReadJob:
    command: str
    payload: dict[str, Any]
    save_to: str | None = None

    @classmethod
    def ping(cls) -> ReadJob:
        return cls("ping", {"command": "ping"})

    @classmethod
    def document_info(cls) -> ReadJob:
        return cls("document-info", {"command": "document-info"})

    @classmethod
    def list_views(cls, view_type: str | None = None, name_contains: str | None = None) -> ReadJob:
        payload: dict[str, Any] = {"command": "list-views"}
        if normalized := _optional_text(view_type):
            payload["viewType"] = normalized
        if normalized := _optional_text(name_contains):
            payload["nameContains"] = normalized
        return cls("list-views", payload)

    @classmethod
    def view_summary(cls, view: str) -> ReadJob:
        return cls(
            "view-summary",
            {"command": "view-summary", "view": _required_text(view, "view")},
        )

    @classmethod
    def export_view(cls, view: str, pixel_size: int = 1600, save_to: str | None = None) -> ReadJob:
        if pixel_size < 1 or pixel_size > 4000:
            raise RevitChannelError("pixel_size must be between 1 and 4000.")
        payload = {
            "command": "export-view",
            "view": _required_text(view, "view"),
            "pixelSize": pixel_size,
            "zoomToFit": True,
        }
        return cls("export-view", payload, save_to)

    @classmethod
    def view_elements(
        cls,
        view: str,
        categories: list[str] | None = None,
        offset: int = 0,
        limit: int = 100,
    ) -> ReadJob:
        if offset < 0:
            raise RevitChannelError("offset must not be negative.")
        if limit <= 0:
            raise RevitChannelError("limit must be greater than zero.")
        payload: dict[str, Any] = {
            "command": "view-elements",
            "view": _required_text(view, "view"),
            "offset": offset,
            "limit": limit,
        }
        normalized_categories = _unique_texts(categories or [])
        if normalized_categories:
            payload["categories"] = normalized_categories
        return cls("view-elements", payload)

    @classmethod
    def element_details(cls, element_id: int) -> ReadJob:
        if element_id <= 0:
            raise RevitChannelError("element-details requires a positive element id.")
        return cls("element-details", {"command": "element-details", "id": element_id})

    @classmethod
    def view_warnings(cls, view: str) -> ReadJob:
        return cls(
            "view-warnings",
            {"command": "view-warnings", "view": _required_text(view, "view")},
        )

    @classmethod
    def query_elements(
        cls,
        categories: list[str] | None = None,
        family: str | None = None,
        type_name: str | None = None,
        level: str | None = None,
        view: str | None = None,
        workset: str | None = None,
        phase: str | None = None,
        area_scheme: str | None = None,
        parameter_filters: list[dict[str, Any]] | None = None,
        fields: list[str] | None = None,
        offset: int = 0,
        limit: int = 100,
        sort_field: str = "id",
        sort_direction: str = "asc",
        include_geometry: bool = False,
    ) -> ReadJob:
        try:
            payload = query_payload(
                categories,
                family,
                type_name,
                level,
                view,
                workset,
                phase,
                area_scheme,
                parameter_filters,
                fields,
                offset,
                limit,
                sort_field,
                sort_direction,
                _optional_text,
                _unique_texts,
            )
        except ValueError as error:
            raise RevitChannelError(str(error)) from error
        if include_geometry:
            payload["includeGeometry"] = True
        return cls("query-elements", payload)

    @classmethod
    def aggregate_elements(
        cls,
        group_by: list[str],
        numeric_field: str | None = None,
        categories: list[str] | None = None,
        family: str | None = None,
        type_name: str | None = None,
        level: str | None = None,
        view: str | None = None,
        workset: str | None = None,
        phase: str | None = None,
        area_scheme: str | None = None,
        parameter_filters: list[dict[str, Any]] | None = None,
    ) -> ReadJob:
        try:
            payload = aggregate_payload(
                categories,
                family,
                type_name,
                level,
                view,
                workset,
                phase,
                area_scheme,
                parameter_filters,
                group_by,
                numeric_field,
                _optional_text,
                _unique_texts,
            )
        except ValueError as error:
            raise RevitChannelError(str(error)) from error
        return cls("aggregate-elements", payload)

    @classmethod
    def list_catalog(cls, section: str) -> ReadJob:
        return cls(
            "list-catalog",
            {"command": "list-catalog", "section": _required_text(section, "section")},
        )

    @classmethod
    def list_warnings(
        cls, warning_text: str | None = None, include_elements: bool = False
    ) -> ReadJob:
        payload: dict[str, Any] = {"command": "list-warnings", "includeElements": include_elements}
        if normalized := _optional_text(warning_text):
            payload["warningText"] = normalized
        return cls("list-warnings", payload)

    @classmethod
    def list_relations(
        cls, relation: str, source_id: int | None = None, source_name: str | None = None
    ) -> ReadJob:
        payload: dict[str, Any] = {
            "command": "list-relations",
            "relation": _required_text(relation, "relation"),
        }
        if source_id is not None:
            if source_id <= 0:
                raise RevitChannelError("source_id must be positive.")
            payload["sourceId"] = source_id
        if normalized := _optional_text(source_name):
            payload["sourceName"] = normalized
        return cls("list-relations", payload)

    def to_json(self) -> str:
        return json.dumps(self.payload, ensure_ascii=False, separators=(",", ":"))

    def for_document(self, document: str | None) -> ReadJob:
        normalized = _optional_text(document)
        if normalized is None:
            return self
        payload = dict(self.payload)
        payload["targetDocument"] = normalized
        return replace(self, payload=payload)


@dataclass(frozen=True)
class JobPickupStatus:
    taken: bool
    activation_attempts: int
    trigger_present: bool
    elapsed_seconds: float


def _optional_text(value: str | None) -> str | None:
    normalized = value.strip() if value else ""
    return normalized or None


def _required_text(value: str, field: str) -> str:
    normalized = _optional_text(value)
    if normalized is None:
        raise RevitChannelError(f"Field {field} must not be empty.")
    return normalized


def _unique_texts(values: list[str]) -> list[str]:
    result: list[str] = []
    seen: set[str] = set()
    for value in values:
        normalized = _optional_text(value)
        key = normalized.casefold() if normalized else ""
        if normalized and key not in seen:
            seen.add(key)
            result.append(normalized)
    return result


def matches_document(instance: dict[str, Any], document: str) -> bool:
    needle = document.strip().casefold()
    filename = str(instance.get("documentPath", "")).replace("\\", "/").rsplit("/", 1)[-1]
    return any(
        needle in str(value).casefold()
        for value in (instance.get("documentTitle", instance.get("documentName", "")), filename)
    )


def select_instance(instances: list[dict[str, Any]], job: ReadJob) -> dict[str, Any]:
    document = job.payload.get("targetDocument")
    if job.command in ACTION_COMMANDS or not document:
        if len(instances) != 1:
            raise RevitChannelError(
                "Actions and undirected reads require exactly one running Revit instance. Use revit_list_instances and a unique document for directed reads."
            )
        selected = instances[0]
    else:
        matches = [item for item in instances if matches_document(item, document)]
        if not matches:
            raise RevitChannelError(
                "No running Revit instance has a matching active document. Use revit_list_instances."
            )
        if len(matches) != 1:
            raise RevitChannelError(
                "The document reference is ambiguous across Revit instances. Use a unique title or file name."
            )
        selected = matches[0]
    if job.payload.get("targetProcessId", selected["processId"]) != selected["processId"]:
        raise RevitChannelError(
            "The selected Revit process changed before submission. Retry discovery."
        )
    return selected


class RemoteHost(Protocol):
    async def select_job(self, job: ReadJob) -> tuple[RemoteHost, ReadJob]: ...

    async def prepare_job(self, name: str, content: str, command: str) -> set[str]: ...

    async def wait_until_trigger_is_gone(self, timeout_seconds: float) -> JobPickupStatus: ...

    async def wait_for_new_response(
        self,
        command: str,
        known_names: set[str],
        timeout_seconds: float,
        correlation_id: str | None = None,
    ) -> str | None: ...

    async def finish_job(
        self,
        response_name: str,
        cleanup_names: list[str],
        download_artifact: bool,
        save_to: str | None,
    ) -> tuple[str, str | None]: ...

    async def delete_files(self, names: list[str]) -> None: ...


class RevitReadChannel:
    def __init__(self, remote: RemoteHost) -> None:
        self.remote = remote
        self._lock = asyncio.Lock()

    async def execute(
        self,
        job: ReadJob,
        timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
        pickup_timeout_seconds: int = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    ) -> dict[str, Any]:
        if timeout_seconds <= 0:
            raise RevitChannelError("timeout_seconds must be greater than zero.")
        if pickup_timeout_seconds <= 0:
            raise RevitChannelError("pickup_timeout_seconds must be greater than zero.")

        async with self._lock:
            remote, job = await self.remote.select_job(job)
            return await RevitReadChannel(remote)._execute_serial(
                job, timeout_seconds, pickup_timeout_seconds
            )

    async def _execute_serial(
        self, job: ReadJob, timeout_seconds: int, pickup_timeout_seconds: int
    ) -> dict[str, Any]:
        correlation_id = uuid.uuid4().hex
        job = replace(job, payload={**job.payload, "correlationId": correlation_id})
        temporary_name = f"mcp_{correlation_id}.tmp"
        response_name: str | None = None
        job_prepared = False
        finish_attempted = False
        trigger_taken = False
        result: dict[str, Any] | None = None
        failure: BaseException | None = None

        try:
            known_responses = await self.remote.prepare_job(
                temporary_name, job.to_json(), job.command
            )
            job_prepared = True

            pickup = await self.remote.wait_until_trigger_is_gone(pickup_timeout_seconds)
            trigger_taken = pickup.taken
            if not trigger_taken:
                trigger_state = "is still present" if pickup.trigger_present else "is absent"
                raise JobPickupTimeoutError(
                    f"Job was not picked up within {pickup.elapsed_seconds:.1f} s; "
                    f"Revit activation attempts: {pickup.activation_attempts}; "
                    f"{TRIGGER_FILE} {trigger_state}. The job was not deleted and may still execute later."
                )

            loop = asyncio.get_running_loop()
            deadline = loop.time() + timeout_seconds
            response_name = await self.remote.wait_for_new_response(
                job.command, known_responses, timeout_seconds, correlation_id
            )
            while response_name is not None:
                # Progress writes reuse this file; cleanup must wait for a real result.
                try:
                    content, _ = await asyncio.wait_for(
                        self.remote.finish_job(response_name, [], False, None),
                        timeout=max(0, deadline - loop.time()),
                    )
                except TimeoutError:
                    break
                try:
                    envelope = json.loads(content)
                except json.JSONDecodeError:
                    envelope = None
                if isinstance(envelope, dict) and envelope.get("correlationId") not in (
                    None,
                    "",
                    correlation_id,
                ):
                    known_responses.add(response_name)
                    response_name = None
                elif envelope is not None:
                    result = parse_response(content, job.command)
                    responder = result.get("responder")
                    if getattr(self.remote, "requires_identity", False) and (
                        result.get("correlationId") != correlation_id
                        or not isinstance(responder, dict)
                        or responder.get("processId") != job.payload.get("targetProcessId")
                    ):
                        raise RevitChannelError(
                            "The selected channel returned an unconfirmed response identity or correlation."
                        )
                    if not _is_intermediate_response(result):
                        break
                    result = None
                remaining = deadline - loop.time()
                if remaining <= 0:
                    break
                await asyncio.sleep(min(1, remaining))
                if loop.time() >= deadline:
                    break
                response_name = await self.remote.wait_for_new_response(
                    job.command, known_responses, deadline - loop.time(), correlation_id
                )

            if result is None:
                raise ResponseTimeoutError(
                    f"The add-in picked up the job, but no result for {job.command} appeared within {timeout_seconds} s. "
                    + (
                        "The action may have executed. Inspect the model before retrying."
                        if job.command in ACTION_COMMANDS
                        else "The command may need more time; increase timeout_seconds and retry."
                    )
                )

            if job.command == "export-view" and result.get("success") is True:
                finish_attempted = True
                content, local_path = await self.remote.finish_job(
                    response_name,
                    [temporary_name, response_name],
                    True,
                    job.save_to,
                )
                result = parse_response(content, job.command)
                if local_path is not None:
                    result["data"]["localPath"] = local_path
        except (Exception, asyncio.CancelledError) as error:
            # Preserve the original failure until temporary-file cleanup finishes.
            failure = error

        cleanup_names = [temporary_name] if job_prepared and not finish_attempted else []
        if response_name and not finish_attempted:
            cleanup_names.append(response_name)
        try:
            await self.remote.delete_files(cleanup_names)
        except (Exception, asyncio.CancelledError) as cleanup_error:
            # Cleanup failure must not replace the original command failure.
            if failure is None:
                failure = RevitChannelError(
                    f"Could not clean up channel temporary files: {cleanup_error}"
                )
            else:
                LOGGER.warning(
                    "Could not clean up temporary files after an error: %s",
                    cleanup_error,
                )

        if failure is not None:
            raise failure
        if result is None:
            raise RevitChannelError("Channel completed without a response.")
        return result


def _is_intermediate_response(response: dict[str, Any]) -> bool:
    if response.get("partial") is not True:
        return False
    message = response.get("message")
    if response.get("correlationId"):
        terminal_partial = isinstance(message, str) and (
            "The 60-second limit was reached" in message
            or message.startswith(
                (
                    "Processing limit reached:",
                    "Element reading stopped:",
                    "Element reading aborted:",
                )
            )
        )
        return not terminal_partial
    return (
        message
        in (
            "Command accepted and running.",
            "Command accepted; preparing the view element list.",
            "The element list is ready; reading data in batches.",
            "Processing is waiting for the next ExternalEvent call.",
            "New job rejected: RevitModelMcp is busy reading elements.",
        )
        or (isinstance(message, str) and message.startswith("Processed "))
        or (response.get("data") == "accepted" and response.get("elapsedMs") == 0)
    )


def parse_response(content: str, expected_command: str) -> dict[str, Any]:
    try:
        response = json.loads(content)
    except (json.JSONDecodeError, TypeError) as error:
        raise ResponseParseError(f"Response could not be parsed as JSON: {error}") from error

    if not isinstance(response, dict):
        raise ResponseParseError("Invalid response: the JSON root must be an object.")
    if response.get("command") != expected_command:
        raise ResponseParseError(
            f"Invalid response: expected command {expected_command}, got {response.get('command')!r}."
        )
    if not isinstance(response.get("success"), bool):
        raise ResponseParseError(
            "Invalid response: field success is missing or has the wrong type."
        )
    if response["success"] is False:
        if expected_command in ACTION_COMMANDS:
            return response
        if _is_intermediate_response(response) or (
            response.get("partial") is True
            and isinstance(response.get("data"), (dict, list))
            and isinstance(response.get("elapsedMs"), (int, float))
            and response["elapsedMs"] > 0
        ):
            return response
        message = response.get("message")
        raise PluginResponseError(
            message
            if isinstance(message, str) and message
            else "The add-in returned an error without a message."
        )
    if "data" not in response:
        raise ResponseParseError("Invalid response: a successful response must contain data.")
    return response
