"""Time the MCP read and action paths against a live Revit workstation.

Starts the server over stdio, calls tools through the MCP client and writes
bench-<utc timestamp>.json and .md. Compare only runs from the same workstation and model.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import math
import os
import re
import shlex
import statistics
import sys
import tempfile
import time
from collections.abc import Awaitable, Callable
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

COLD_START_RUNS = 5
DETAILS_COUNT = 50
ACTION_STEPS = 50
FIELD_COUNT = 10
FAMILY_COUNT = 10
CALL_TIMEOUT = 900.0


class BenchError(Exception):
    """A tool call or a scenario precondition failed."""


@dataclass
class Measurement:
    runs_ms: list[float] = field(default_factory=list)
    bytes: int = 0
    skipped_count: int | None = None
    notes: list[str] = field(default_factory=list)


@dataclass
class ScenarioResult:
    name: str
    runs: int
    median_ms: float | None
    p95_ms: float | None
    min_ms: float | None
    max_ms: float | None
    bytes: int
    skipped_count: int | None
    notes: list[str]
    error: str | None


def percentile(values: list[float], fraction: float) -> float:
    """Nearest-rank percentile."""
    ordered = sorted(values)
    return ordered[max(0, math.ceil(fraction * len(ordered)) - 1)]


def summarize(name: str, measurement: Measurement, error: str | None) -> ScenarioResult:
    runs = measurement.runs_ms
    return ScenarioResult(
        name=name,
        runs=len(runs),
        median_ms=statistics.median(runs) if runs else None,
        p95_ms=percentile(runs, 0.95) if runs else None,
        min_ms=min(runs) if runs else None,
        max_ms=max(runs) if runs else None,
        bytes=measurement.bytes,
        skipped_count=measurement.skipped_count,
        notes=measurement.notes,
        error=error,
    )


def unwrap(payload: dict[str, Any]) -> dict[str, Any]:
    data = payload.get("data")
    return data if isinstance(data, dict) else payload


class Context:
    """Shared state for one bench run: the session, the document and cached discovery."""

    def __init__(
        self,
        session: Any,
        runs: int,
        document: str | None = None,
        tool_properties: dict[str, set[str]] | None = None,
        spawn_cold_start: Callable[[], Awaitable[float]] | None = None,
    ) -> None:
        self.session = session
        self.runs = runs
        self.document = document
        self.tool_properties = tool_properties
        self.spawn_cold_start = spawn_cold_start
        self.cache: dict[str, Any] = {}
        self.skipped = 0
        self.last_bytes = 0

    def _arguments(self, name: str, args: dict[str, Any] | None) -> dict[str, Any]:
        arguments = dict(args or {})
        if self.document is not None and (
            self.tool_properties is None
            or "document" in self.tool_properties.get(name, set())
        ):
            arguments["document"] = self.document
        return arguments

    async def call(
        self, name: str, args: dict[str, Any] | None = None
    ) -> dict[str, Any]:
        """Call a tool and record bytes and skippedCount; no timing."""
        payload, size = await self.session.call_tool(name, self._arguments(name, args))
        self.last_bytes = size
        count = payload.get("skippedCount")
        if isinstance(count, int):
            self.skipped = max(self.skipped, count)
        return payload

    async def timed(
        self, name: str, args: dict[str, Any] | None = None
    ) -> tuple[float, int]:
        started = time.perf_counter()
        await self.call(name, args)
        return (time.perf_counter() - started) * 1000, self.last_bytes

    async def repeat(
        self, name: str, args: dict[str, Any] | None = None
    ) -> Measurement:
        measurement = Measurement()
        for _ in range(self.runs):
            elapsed, measurement.bytes = await self.timed(name, args)
            measurement.runs_ms.append(elapsed)
        return measurement

    async def catalog(self, section: str, limit: int = 200) -> list[dict[str, Any]]:
        key = f"catalog:{section}:{limit}"
        if key not in self.cache:
            payload = await self.call(
                "revit_list_catalog", {"section": section, "limit": limit}
            )
            self.cache[key] = unwrap(payload).get("items") or []
        return self.cache[key]

    async def first_category(self) -> str:
        items = await self.catalog("categories")
        if not items:
            raise BenchError("the catalog has no categories")
        return items[0]["name"]

    async def parameter_names(self, count: int) -> list[str]:
        names = [item["name"] for item in await self.catalog("parameters")][:count]
        if not names:
            raise BenchError("the catalog has no parameters")
        return names

    async def query_ids(self, size: int) -> list[int]:
        key = f"ids:{size}"
        if key not in self.cache:
            category = await self.first_category()
            payload = await self.call(
                "revit_query_elements", {"categories": [category], "limit": size}
            )
            self.cache[key] = [e["id"] for e in unwrap(payload).get("elements") or []]
        if not self.cache[key]:
            raise BenchError("the query returned no elements")
        return self.cache[key]


async def ping(ctx: Context) -> Measurement:
    return await ctx.repeat("revit_ping")


async def catalog(ctx: Context) -> Measurement:
    return await ctx.repeat(
        "revit_list_catalog", {"section": "categories", "limit": 2000}
    )


async def catalog_limited(ctx: Context) -> Measurement:
    return await ctx.repeat(
        "revit_list_catalog", {"section": "categories", "limit": 50}
    )


async def aggregate_category(ctx: Context) -> Measurement:
    category = await ctx.first_category()
    return await ctx.repeat(
        "revit_aggregate_elements", {"categories": [category], "group_by": ["category"]}
    )


async def aggregate_category_type(ctx: Context) -> Measurement:
    category = await ctx.first_category()
    return await ctx.repeat(
        "revit_aggregate_elements",
        {"categories": [category], "group_by": ["category", "type"]},
    )


async def _query(ctx: Context, size: int) -> Measurement:
    category = await ctx.first_category()
    payload = await ctx.call(
        "revit_query_elements", {"categories": [category], "limit": 1}
    )
    fields = (unwrap(payload).get("fields") or [])[:FIELD_COUNT]
    if not fields:
        raise BenchError("the query returned no field names")
    return await ctx.repeat(
        "revit_query_elements",
        {"categories": [category], "fields": fields, "limit": size},
    )


async def query_100(ctx: Context) -> Measurement:
    return await _query(ctx, 100)


async def query_1000(ctx: Context) -> Measurement:
    return await _query(ctx, 1000)


async def details_50(ctx: Context) -> Measurement:
    ids = (await ctx.query_ids(100))[:DETAILS_COUNT]
    measurement = Measurement()
    per_call: list[float] = []
    for _ in range(ctx.runs):
        total = 0.0
        size = 0
        for element_id in ids:
            elapsed, call_size = await ctx.timed(
                "revit_element_details", {"element_id": element_id}
            )
            total += elapsed
            size += call_size
            per_call.append(elapsed)
        measurement.runs_ms.append(total)
        measurement.bytes = size
    measurement.notes.append(
        f"{len(ids)} calls per run, per-call median {statistics.median(per_call):.1f} ms"
    )
    return measurement


async def warnings(ctx: Context) -> Measurement:
    return await ctx.repeat("revit_list_warnings")


async def health(ctx: Context) -> Measurement:
    return await ctx.repeat("revit_model_health")


async def fill_check(ctx: Context) -> Measurement:
    category = await ctx.first_category()
    parameters = await ctx.parameter_names(3)
    return await ctx.repeat(
        "revit_parameter_fill_check",
        {"categories": [category], "parameters": parameters},
    )


async def _family_audit(ctx: Context, detail: str) -> Measurement:
    families = []
    for item in await ctx.catalog("family-types"):
        family = item.get("family")
        if family and family not in families:
            families.append(family)
    if not families:
        raise BenchError("the catalog has no families")
    measurement = await ctx.repeat(
        "revit_family_audit", {"families": families[:FAMILY_COUNT], "detail": detail}
    )
    measurement.notes.append(f"{min(len(families), FAMILY_COUNT)} families")
    return measurement


async def family_audit_compact(ctx: Context) -> Measurement:
    # The tool names its compact level "summary".
    return await _family_audit(ctx, "summary")


async def family_audit_full(ctx: Context) -> Measurement:
    return await _family_audit(ctx, "full")


async def snapshot(ctx: Context) -> Measurement:
    return await ctx.repeat("revit_model_snapshot")


async def export_view(ctx: Context) -> Measurement:
    payload = await ctx.call("revit_list_views")
    views = unwrap(payload).get("views") or []
    plan = next((v for v in views if "plan" in str(v.get("type", "")).lower()), None)
    if plan is None:
        raise BenchError("the model has no plan view")
    measurement = Measurement()
    with tempfile.TemporaryDirectory(prefix="rmm-bench-") as folder:
        for index in range(ctx.runs):
            target = str(Path(folder) / f"view-{index}.png")
            elapsed, size = await ctx.timed(
                "revit_export_view", {"view": str(plan["id"]), "output_path": target}
            )
            measurement.runs_ms.append(elapsed)
            measurement.bytes = size
    measurement.notes.append(f"view {plan['name']}")
    return measurement


async def actions_50(ctx: Context) -> Measurement:
    categories = await ctx.catalog("categories")
    walls = next(
        (
            item
            for item in categories
            if item.get("id") == -2000011 or item.get("name", "").casefold() == "walls"
        ),
        None,
    )
    if walls is None:
        raise BenchError("the catalog has no Walls category")
    payload = await ctx.call(
        "revit_query_elements", {"categories": [walls["name"]], "limit": 100}
    )
    ids = [element["id"] for element in unwrap(payload).get("elements") or []][
        :ACTION_STEPS
    ]
    if not ids:
        raise BenchError("the Walls query returned no elements")
    details = await ctx.call("revit_element_details", {"element_id": ids[0]})
    parameters = [
        parameter
        for parameter in unwrap(details).get("parameters") or []
        if parameter.get("storageType") == "String"
    ]
    parameters.sort(key=lambda parameter: parameter["name"] != "Comments")
    parameter_name = None
    for parameter in parameters:
        # Element details omit writability; a rolled-back action verifies it.
        preview = await ctx.call(
            "revit_run_actions",
            {
                "dry_run": True,
                "steps": [
                    {
                        "action": "set_parameter",
                        "args": {
                            "element_id": ids[0],
                            "parameter": parameter["name"],
                            "value": "bench preview",
                        },
                    }
                ],
            },
        )
        if preview.get("success") is True:
            parameter_name = parameter["name"]
            break
    if parameter_name is None:
        raise BenchError("the first wall has no writable text parameter")
    measurement = Measurement()
    undo_ms: list[float] = []
    for index in range(ctx.runs):
        steps = [
            {
                "action": "set_parameter",
                "args": {
                    "element_id": i,
                    "parameter": parameter_name,
                    "value": f"bench {index}",
                },
            }
            for i in ids
        ]
        started = time.perf_counter()
        payload = await ctx.call("revit_run_actions", {"steps": steps})
        elapsed = (time.perf_counter() - started) * 1000
        if payload.get("success") is False:
            raise BenchError(f"revit_run_actions failed: {json.dumps(payload)}")
        measurement.bytes = ctx.last_bytes
        measurement.runs_ms.append(elapsed)
        undo, _ = await ctx.timed("revit_undo_last")
        undo_ms.append(undo)
    measurement.notes.append(
        f"{len(ids)} steps; undo median {statistics.median(undo_ms):.1f} ms, p95 "
        f"{percentile(undo_ms, 0.95):.1f} ms"
    )
    return measurement


async def cold_start(ctx: Context) -> Measurement:
    if ctx.spawn_cold_start is None:
        raise BenchError("cold start needs a server command")
    measurement = Measurement()
    for _ in range(COLD_START_RUNS):
        measurement.runs_ms.append(await ctx.spawn_cold_start())
    measurement.notes.append("initialize plus tools/list on a fresh server")
    return measurement


SCENARIOS: dict[str, Callable[[Context], Awaitable[Measurement]]] = {
    "ping": ping,
    "catalog": catalog,
    "catalog_limited": catalog_limited,
    "aggregate_category": aggregate_category,
    "aggregate_category_type": aggregate_category_type,
    "query_100": query_100,
    "query_1000": query_1000,
    "details_50": details_50,
    "warnings": warnings,
    "health": health,
    "fill_check": fill_check,
    "family_audit_compact": family_audit_compact,
    "family_audit_full": family_audit_full,
    "snapshot": snapshot,
    "export_view": export_view,
    "actions_50": actions_50,
    "cold_start": cold_start,
}


def select_scenarios(spec: str | None) -> list[str]:
    if not spec:
        return list(SCENARIOS)
    names = [name.strip() for name in spec.split(",") if name.strip()]
    unknown = [name for name in names if name not in SCENARIOS]
    if unknown:
        raise ValueError(
            f"Unknown scenarios: {', '.join(unknown)}. Known: {', '.join(SCENARIOS)}"
        )
    return names


async def run_scenario(ctx: Context, name: str) -> ScenarioResult:
    ctx.skipped = 0
    measurement = Measurement()
    error = None
    try:
        measurement = await SCENARIOS[name](ctx)
    except Exception as exception:  # noqa: BLE001  a failed scenario must not stop the run
        error = f"{type(exception).__name__}: {exception}"
    if measurement.skipped_count is None and ctx.skipped:
        measurement.skipped_count = ctx.skipped
    return summarize(name, measurement, error)


def instance_info(ping_payload: dict[str, Any]) -> dict[str, str | None]:
    sources = [ping_payload, unwrap(ping_payload), ping_payload.get("responder") or {}]
    addin = next((s["addinVersion"] for s in sources if s.get("addinVersion")), None)
    raw = next((s["revitVersion"] for s in sources if s.get("revitVersion")), None)
    match = re.search(r"20\d\d", str(raw or ""))
    return {"addinVersion": addin, "revitYear": match.group(0) if match else raw}


async def run_bench(
    ctx: Context, names: list[str], label: str | None, server_version: str | None
) -> dict[str, Any]:
    info: dict[str, str | None] = {"addinVersion": None, "revitYear": None}
    try:
        info = instance_info(await ctx.call("revit_ping"))
    except Exception:  # noqa: BLE001, S110  the ping scenario reports the failure
        pass
    results = [await run_scenario(ctx, name) for name in names]
    return {
        "label": label,
        "timestamp": datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "serverVersion": server_version,
        **info,
        "document": ctx.document,
        "runs": ctx.runs,
        "scenarios": [result.__dict__ for result in results],
    }


def _ms(value: float | None) -> str:
    return "-" if value is None else f"{value:.1f}"


def render_markdown(result: dict[str, Any]) -> str:
    lines = [
        "# Bench result",
        "",
        f"- Label: {result.get('label') or '-'}",
        f"- Timestamp: {result['timestamp']}",
        f"- Server version: {result.get('serverVersion') or '-'}",
        f"- Add-in version: {result.get('addinVersion') or '-'}",
        f"- Revit year: {result.get('revitYear') or '-'}",
        "",
        "| Scenario | runs | median ms | p95 ms | bytes | notes |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    for item in result["scenarios"]:
        notes = list(item["notes"])
        if item["skipped_count"]:
            notes.append(f"skippedCount {item['skipped_count']}")
        if item["error"]:
            notes.append(f"FAILED: {item['error']}")
        text = "; ".join(notes).replace("|", "/").replace("\n", " ")
        lines.append(
            f"| {item['name']} | {item['runs']} | {_ms(item['median_ms'])} | "
            f"{_ms(item['p95_ms'])} | {item['bytes']} | {text} |"
        )
    return "\n".join(lines) + "\n"


def write_outputs(result: dict[str, Any], folder: Path) -> tuple[Path, Path]:
    folder.mkdir(parents=True, exist_ok=True)
    stamp = (
        datetime.strptime(result["timestamp"], "%Y-%m-%dT%H:%M:%SZ")
        .replace(tzinfo=UTC)
        .strftime("%Y%m%dT%H%M%SZ")
    )
    json_path = folder / f"bench-{stamp}.json"
    markdown_path = folder / f"bench-{stamp}.md"
    json_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    markdown_path.write_text(render_markdown(result), encoding="utf-8")
    return json_path, markdown_path


class McpSession:
    """Adapts an mcp ClientSession to call_tool(name, args) -> (payload, response bytes)."""

    def __init__(self, client: Any) -> None:
        self.client = client

    async def call_tool(
        self, name: str, args: dict[str, Any]
    ) -> tuple[dict[str, Any], int]:
        result = await self.client.call_tool(
            name, args, read_timeout_seconds=CALL_TIMEOUT
        )
        text = "".join(getattr(block, "text", "") for block in result.content)
        if result.is_error:
            raise BenchError(f"{name}: {text[:300]}")
        payload = result.structured_content
        if payload is None:
            try:
                payload = json.loads(text)
            except ValueError:
                payload = {}
        size = len((text or json.dumps(payload)).encode("utf-8"))
        return (payload if isinstance(payload, dict) else {"data": payload}), size


def server_parameters(command: str, cwd: str) -> Any:
    from mcp import StdioServerParameters

    parts = shlex.split(command, posix=os.name != "nt")
    environment = {**os.environ, "REVIT_MCP_NO_UPDATE_CHECK": "1"}
    environment.setdefault("REVIT_MCP_HOST", "local")
    return StdioServerParameters(
        command=parts[0], args=parts[1:], cwd=cwd, env=environment
    )


@asynccontextmanager
async def open_session(command: str, cwd: str):
    from mcp import ClientSession
    from mcp.client.stdio import stdio_client

    async with (
        stdio_client(server_parameters(command, cwd)) as (read, write),
        ClientSession(read, write) as client,
    ):
        initialized = await client.initialize()
        yield client, initialized.server_info.version


async def time_cold_start(command: str, cwd: str) -> float:
    started = time.perf_counter()
    async with open_session(command, cwd) as (client, _):
        await client.list_tools()
        return (time.perf_counter() - started) * 1000


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-cmd", default="uv run revit-model-mcp")
    parser.add_argument(
        "--cwd",
        default=str(Path(__file__).resolve().parents[2] / "server"),
        help="working directory of the server command",
    )
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument(
        "--document", default=None, help="passed to every tool that accepts it"
    )
    parser.add_argument(
        "--scenarios",
        default=None,
        help=f"comma list, default all: {','.join(SCENARIOS)}",
    )
    parser.add_argument(
        "--out", type=Path, default=None, help="folder for bench-<timestamp>.json/.md"
    )
    parser.add_argument(
        "--label", default=None, help="free text such as a commit or version"
    )
    args = parser.parse_args(argv)
    if args.runs < 1:
        parser.error("--runs must be at least 1")
    try:
        args.names = select_scenarios(args.scenarios)
    except ValueError as error:
        parser.error(str(error))
    return args


async def main_async(args: argparse.Namespace) -> dict[str, Any]:
    async with open_session(args.server_cmd, args.cwd) as (client, version):
        tools = await client.list_tools()
        properties = {
            t.name: set((t.input_schema or {}).get("properties", {}))
            for t in tools.tools
        }
        ctx = Context(
            McpSession(client),
            args.runs,
            args.document,
            properties,
            lambda: time_cold_start(args.server_cmd, args.cwd),
        )
        return await run_bench(ctx, args.names, args.label, version)


def main(argv: list[str] | None = None) -> None:
    args = parse_args(argv)
    result = asyncio.run(main_async(args))
    print(render_markdown(result))
    if args.out is not None:
        for path in write_outputs(result, args.out):
            print(f"wrote {path}", file=sys.stderr)


if __name__ == "__main__":
    main()
