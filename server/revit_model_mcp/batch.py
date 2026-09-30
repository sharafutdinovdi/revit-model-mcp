"""Persistent, read-only batch collection tools."""

from __future__ import annotations

import asyncio
import ntpath
import re
import uuid
from pathlib import PureWindowsPath
from typing import Any

from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations

from revit_model_mcp.actions import redact_model_paths
from revit_model_mcp.artifact_download import save_batch_artifact
from revit_model_mcp.revit_channel import ReadJob, RevitChannelError, resolve_instance
from revit_model_mcp.ssh_host import SshPowerShellHost

BATCH_TOOL = ToolAnnotations(readOnlyHint=True, destructiveHint=False, idempotentHint=False)
RUN_ID = re.compile(r"[0-9a-f]{32}\Z")


def _file_host(host: object) -> SshPowerShellHost:
    candidate = getattr(host, "fallback", host)
    if not isinstance(candidate, SshPowerShellHost):
        raise ToolError("Batch collection requires a local or SSH workstation file channel.")
    return candidate


def _run_id(value: str) -> str:
    if not isinstance(value, str) or not RUN_ID.fullmatch(value):
        raise ToolError("run_id must be a batch run id.")
    return value


def _paths(paths: list[str]) -> list[str]:
    if not paths:
        raise ToolError("Batch source contains no models.")
    seen: set[str] = set()
    result: list[str] = []
    for path in paths:
        if not isinstance(path, str) or not path.strip() or path != path.strip():
            raise ToolError("Batch paths must contain nonblank model paths.")
        if not path.upper().startswith("RSN://") and not PureWindowsPath(path).is_absolute():
            raise ToolError("Batch paths must be absolute local, UNC, or RSN paths.")
        if ntpath.splitext(path.replace("/", "\\"))[1].lower() not in {".rvt", ".rfa"}:
            raise ToolError("Batch paths must end in .rvt or .rfa.")
        normalized = ntpath.normcase(ntpath.normpath(path.replace("/", "\\")))
        if normalized in seen:
            raise ToolError("Batch paths contain a duplicate normalized path.")
        seen.add(normalized)
        result.append(path)
    return result


def _years(years: list[int] | None) -> list[int]:
    if years is None:
        return []
    if (
        not isinstance(years, list)
        or not years
        or any(type(year) is not int or year < 2022 or year > 2027 for year in years)
        or len(set(years)) != len(years)
    ):
        raise ToolError("years must contain distinct Revit years from 2022 through 2027.")
    return years


def _rules(rules: list[dict[str, str]] | None) -> list[dict[str, str]]:
    if rules is None:
        return []
    if not isinstance(rules, list) or any(
        not isinstance(rule, dict)
        or set(rule) != {"category", "parameter"}
        or any(
            not isinstance(rule[key], str) or not rule[key].strip()
            for key in ("category", "parameter")
        )
        for rule in rules
    ):
        raise ToolError(
            "parameter_rules must be a list of nonblank category and parameter objects."
        )
    return rules


def _public(state: dict[str, Any]) -> dict[str, Any]:
    status_names = ["pending", "running", "completed", "cancelled", "failed"]
    model_names = ["pending", "running", "completed", "failed", "cancelled"]
    public = dict(state)
    if type(public.get("status")) is int and 0 <= public["status"] < len(status_names):
        public["status"] = status_names[public["status"]]
    phase_names = ["startup", "pre-pass", "open", "snapshot", "close"]
    public["models"] = []
    for model in state.get("models", []):
        record = dict(model)
        if type(record.get("status")) is int and 0 <= record["status"] < len(model_names):
            record["status"] = model_names[record["status"]]
        if type(record.get("phase")) is int and 0 <= record["phase"] < len(phase_names):
            record["phase"] = phase_names[record["phase"]]
        public["models"].append(record)
    return redact_model_paths(public)


async def _responsive_instances(host: object) -> list[dict[str, Any]]:
    return [
        item for item in await host.list_revit_instances() if item.get("pluginResponding", True)
    ]


async def _wait_for_responsive_instance(host: object) -> list[dict[str, Any]]:
    for _ in range(120):
        instances = await _responsive_instances(host)
        if instances:
            return instances
        await asyncio.sleep(1)
    return []


def register_batch(mcp, host_provider, channel_provider) -> None:
    @mcp.tool(
        title="Start batch collection",
        annotations=BATCH_TOOL.model_copy(update={"title": "Start batch collection"}),
    )
    async def revit_batch_start(
        paths: list[str] | None = None,
        folder: str | None = None,
        recursive: bool = False,
        parameter_rules: list[dict[str, str]] | None = None,
        years: list[int] | None = None,
    ) -> dict[str, Any]:
        """Start a persistent, read-only run over local, UNC, or RSN models."""
        if (paths is None) == (folder is None):
            raise ToolError("Supply exactly one of paths or folder.")
        if folder is not None and (not isinstance(folder, str) or not folder.strip()):
            raise ToolError("folder must be a nonblank path.")
        if paths is not None and (not isinstance(paths, list) or not paths):
            raise ToolError("paths must be a non-empty list.")
        selected_years = _years(years)
        selected_rules = _rules(parameter_rules)
        host = host_provider()
        file_host = _file_host(host)
        try:
            discovered = (
                await file_host.batch_discover(folder, recursive) if folder is not None else paths
            )
            selected_paths = _paths(discovered)
            instances = await _responsive_instances(host)
            if not instances and isinstance(host, SshPowerShellHost) and not host.local:
                await file_host.batch_activate_interactive()
                instances = await _wait_for_responsive_instance(host)
            selected = resolve_instance(instances, None)
            run_id = uuid.uuid4().hex
            state = {
                "runId": run_id,
                "status": 0,
                "cancelRequested": False,
                "years": selected_years,
                "parameterRules": selected_rules,
                "models": [
                    {"path": path, "status": 0, "phaseTimingsMs": {}} for path in selected_paths
                ],
            }
            import json

            await file_host.batch_create(run_id, json.dumps(state))
            result = await channel_provider().execute(
                ReadJob(
                    "batch-supervisor-start",
                    {
                        "command": "batch-supervisor-start",
                        "runId": run_id,
                        "targetProcessId": selected["processId"],
                    },
                )
            )
            if result.get("success") is not True:
                raise ToolError(
                    redact_model_paths(
                        {"error": str(result.get("error", "Supervisor launch failed."))}
                    )["error"]
                )
            return {"runId": run_id, "acceptedModels": len(selected_paths)}
        except RevitChannelError as error:
            raise ToolError(redact_model_paths({"error": str(error)})["error"]) from error

    @mcp.tool(
        title="Batch collection status",
        annotations=BATCH_TOOL.model_copy(
            update={"title": "Batch collection status", "idempotentHint": True}
        ),
    )
    async def revit_batch_status(run_id: str) -> dict[str, Any]:
        """Read durable run and model status after the initiating client exits."""
        try:
            return _public(await _file_host(host_provider()).batch_status(_run_id(run_id)))
        except RevitChannelError as error:
            raise ToolError(redact_model_paths({"error": str(error)})["error"]) from error

    @mcp.tool(
        title="Cancel batch collection",
        annotations=BATCH_TOOL.model_copy(update={"title": "Cancel batch collection"}),
    )
    async def revit_batch_cancel(run_id: str) -> dict[str, Any]:
        """Persist cancellation and prevent unstarted models from running."""
        try:
            run_id = _run_id(run_id)
            file_host = _file_host(host_provider())
            await file_host.batch_cancel(run_id)
            return _public(await file_host.batch_status(run_id))
        except RevitChannelError as error:
            raise ToolError(redact_model_paths({"error": str(error)})["error"]) from error

    @mcp.tool(
        title="Fetch batch snapshots",
        annotations=BATCH_TOOL.model_copy(update={"title": "Fetch batch snapshots"}),
    )
    async def revit_batch_fetch(run_id: str, dest_dir: str) -> dict[str, Any]:
        """Download completed snapshots to new local files without overwriting."""
        if not isinstance(dest_dir, str) or not dest_dir.strip():
            raise ToolError("dest_dir must be a nonblank local directory.")
        try:
            run_id = _run_id(run_id)
            file_host = _file_host(host_provider())
            state = await file_host.batch_status(run_id)
            if state.get("status") not in (2, 4, "completed", "failed"):
                raise ToolError("Batch run is incomplete; fetch requires a terminal run.")
            names = [
                model.get("snapshotFile")
                for model in state.get("models", [])
                if model.get("status") in (2, "completed")
            ]
            if not names or any(
                not isinstance(name, str) or not re.fullmatch(r"snapshot_[0-9]{4}\.json", name)
                for name in names
            ):
                raise ToolError("Completed batch snapshots are missing from run state.")
            from pathlib import Path

            destination = Path(dest_dir).expanduser().absolute()
            collisions = [name for name in names if (destination / name).exists()]
            if collisions:
                raise ToolError(f"Local batch snapshot already exists: {collisions[0]}")
            artifacts = [await file_host.batch_fetch_artifact(run_id, name) for name in names]
            return {
                "runId": run_id,
                "localPaths": [save_batch_artifact(artifact, dest_dir) for artifact in artifacts],
            }
        except (RevitChannelError, ValueError) as error:
            raise ToolError(redact_model_paths({"error": str(error)})["error"]) from error
