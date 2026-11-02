"""Read-only audit evaluation with bounded, cached reads."""

import asyncio
import inspect
import json
import math
import re
import time
from typing import Protocol

import regex


class NotAssessed(Exception):
    """The source cannot supply enough evidence for this rule."""


class ReadSource(Protocol):
    async def read(self, tool_name: str, arguments: dict) -> dict: ...


class LiveReadSource:
    def __init__(self, document=None, timeout_seconds=60):
        self.document = document
        self.timeout_seconds = timeout_seconds

    async def read(self, tool_name, arguments):
        from revit_model_mcp import server
        from revit_model_mcp.revit_channel import Job

        if tool_name == "revit_list_views":
            if arguments.get("include_templates"):
                raise NotAssessed("revit_list_views cannot include view templates on this add-in.")
            # Current add-ins return all views. Paged readers also accept offset and limit.
            return await server._execute(
                Job(
                    "list-views",
                    {
                        "command": "list-views",
                        "offset": arguments.get("offset", 0),
                        "limit": arguments.get("limit", 500),
                    },
                ),
                self.timeout_seconds,
                self.document,
            )
        if tool_name == "revit_list_sheets":
            return await server._execute(
                Job(
                    "list-sheets",
                    {
                        "command": "list-sheets",
                        "includeUnplacedViews": arguments.get("include_unplaced_views", True),
                        "offset": arguments.get("offset", 0),
                        "limit": arguments.get("limit", 200),
                    },
                ),
                self.timeout_seconds,
                self.document,
            )
        function = inspect.unwrap(getattr(server, tool_name))
        return await function(
            **arguments, document=self.document, timeout_seconds=self.timeout_seconds
        )


class SnapshotReadSource:
    def __init__(self, snapshot):
        if snapshot.get("schemaVersion") != 1:
            raise ValueError("Only snapshot schemaVersion 1 is supported.")
        self.snapshot = snapshot

    async def read(self, tool_name, arguments):
        s = self.snapshot
        if tool_name == "revit_model_health":
            counts = dict(s.get("passport", {}).get("counts", {}))
            if "warnings" in s:
                counts["warnings"] = s["warnings"].get("total")
            if "families" in s:
                counts["familiesInPlace"] = s["families"].get("inPlace")
            data = {"counts": counts}
        elif tool_name == "revit_list_warnings" and "warnings" in s:
            data = s["warnings"]
        elif tool_name == "revit_document_info":
            data = {**s.get("passport", {}), "path": s.get("source", {}).get("path")}
        elif tool_name == "revit_parameter_fill_check":
            if arguments.keys() - {"categories", "parameters"}:
                raise NotAssessed("Snapshot parameter fill does not preserve scoped filters.")
            rows = s.get("parameterFill", {}).get("rows", [])
            selected = [
                r
                for r in rows
                if r["category"] in arguments["categories"]
                and r["parameter"] in arguments["parameters"]
            ]
            expected = {(c, p) for c in arguments["categories"] for p in arguments["parameters"]}
            if {(r["category"], r["parameter"]) for r in selected} != expected:
                raise NotAssessed("Snapshot has no matching parameter fill rows.")
            data = {"rows": selected}
        elif tool_name == "revit_list_catalog":
            # V1 has localized fill labels, but no stable builtin or GUID mapping.
            rows = s.get("parameterFill", {}).get("rows", [])
            field = "category" if arguments["section"] == "categories" else "parameter"
            data = {"items": [{"name": n} for n in sorted({r[field] for r in rows})]}
        else:
            raise NotAssessed(f"{tool_name} is not present in snapshot v1.")
        return {
            "data": data,
            "skipped": s.get("skipped", []),
            "skippedCount": s.get("skippedCount", 0),
        }


def compile_pattern(pattern):
    """Reject nested repetition and bound every match to two seconds."""
    if len(pattern) > 200:
        raise NotAssessed("Unsafe regular expression: length exceeds 200 characters.")
    groups, escaped, character_class, repeated_group = [False], False, False, False
    for index, char in enumerate(pattern):
        if escaped:
            escaped = False
            repeated_group = False
            continue
        if char == "\\":
            escaped = True
            continue
        if char == "[":
            character_class = True
        if character_class:
            if char == "]":
                character_class = False
            repeated_group = False
            continue
        if char == "(":
            groups.append(False)
            repeated_group = False
        elif char == ")" and len(groups) > 1:
            repeated_group = groups.pop()
            groups[-1] |= repeated_group
        elif char in "*+?{" and not (char == "?" and index and pattern[index - 1] == "("):
            if char == "{" and not re.match(r"\{\d+(?:,\d*)?\}", pattern[index:]):
                repeated_group = False
                continue
            if repeated_group:
                raise NotAssessed("Unsafe regular expression: nested quantifiers.")
            groups[-1] = True
            repeated_group = False
        else:
            repeated_group = False
    try:
        return regex.compile(pattern)
    except regex.error as error:
        raise NotAssessed(f"Invalid regular expression: {error}") from error


class Evaluator:
    def __init__(self, source: ReadSource, budget_seconds=600):
        self.source = source
        self.deadline = time.monotonic() + min(budget_seconds, 600)
        self.cache = {}
        self.current = None

    async def read(self, tool, **arguments):
        if tool not in self.current["reads"]:
            self.current["reads"].append(tool)
        key = json.dumps([tool, arguments], sort_keys=True)
        if key not in self.cache:
            remaining = self.deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("Whole-run audit budget exhausted.")
            try:
                self.cache[key] = await asyncio.wait_for(
                    self.source.read(tool, arguments), remaining
                )
            except Exception as error:
                self.cache[key] = (
                    error
                    if isinstance(error, (NotAssessed, TimeoutError))
                    else NotAssessed(str(error))
                )
        response = self.cache[key]
        if isinstance(response, Exception):
            raise response
        self.current["skipped"].extend(response.get("skipped", []))
        self.current["skippedCount"] += response.get(
            "skippedCount", len(response.get("skipped", []))
        )
        data = response.get("data")
        failed = response.get("success") is False
        if (
            failed and not (tool == "revit_list_views" and response.get("partial"))
        ) or not isinstance(data, dict):
            raise NotAssessed(str(response.get("error", "Reader did not return data.")))
        if response.get("partial"):
            self.current["skipped"].append(
                {"what": tool, "reason": "Reader reported partial evidence."}
            )
        if data.get("skipped"):
            self.current["skipped"].extend(data["skipped"])
        self.current["skippedCount"] += data.get("skippedCount", len(data.get("skipped", [])))
        return data

    def matches(self, pattern, value):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("Whole-run audit budget exhausted.")
        return pattern.fullmatch(str(value), timeout=min(2, remaining))

    async def resolve(self, reference, section):
        data = await self.read("revit_list_catalog", section=section, limit=2000)
        rows = data.get("items", [])
        for row in rows:
            if ("builtin" in reference and row.get("builtin") == reference["builtin"]) or (
                "guid" in reference
                and str(row.get("guid", "")).lower() == reference["guid"].lower()
            ):
                return row["name"]
        for name in reference.get("names", []):
            if any(row.get("name") == name for row in rows):
                return name
        raise NotAssessed(f"Unresolved {section} reference: {reference}")

    async def check(self, rule):
        check = rule["check"]
        kind = check["type"]
        threshold = rule.get(
            "threshold", {"min_percent": 100} if kind == "parameter_filled" else {"max_count": 0}
        )
        ids, partial = [], False
        if kind in ("model_health", "coordinates", "sheets") and rule.get("scope"):
            raise NotAssessed("This reader cannot honor an element scope.")
        if kind == "worksets":
            raise NotAssessed("Worksets reader is not available in this slice (#213).")
        if kind == "model_health":
            metric = check["metric"]
            if metric == "unloaded_links":
                data = await self.read("revit_links_status")
                summary = data["summary"]
                measured = summary["rvt"] - summary["rvtLoaded"]
                population = summary["rvt"]
            else:
                data = await self.read("revit_model_health")
                field = {
                    "warnings_total": "warnings",
                    "in_place_families": "familiesInPlace",
                    "imported_cad": "cadImports",
                }.get(metric)
                measured = data.get("counts", {}).get(field)
                population = data.get("counts", {}).get("elements")
            if measured is None:
                raise NotAssessed(f"Metric is unavailable: {metric}")
        elif kind == "count" and check.get("metric") == "warnings_total":
            data = await self.read("revit_list_warnings")
            measured = data["total"]
            population = measured
        elif kind == "coordinates":
            if check["metric"] != "link_offset_mm":
                raise NotAssessed("Only link_offset_mm is assessed in this slice.")
            data = await self.read("revit_shared_coordinates")
            links = data["sharedSiteFromLinks"]
            population = data["linkInstancesTotal"]
            offsets = [
                math.sqrt(sum(row["offsetMm"][axis] ** 2 for axis in ("x", "y", "z")))
                for row in links
            ]
            measured = max(offsets, default=0)
            partial = population > len(links)
        elif kind == "sheets":
            rows, unplaced = [], []
            for page in range(20):
                data = await self.read(
                    "revit_list_sheets", include_unplaced_views=True, offset=page * 200, limit=200
                )
                rows.extend(data["sheets"])
                if page == 0:
                    unplaced = data["unplacedViews"]
                if not data["hasMore"]:
                    break
            partial = data["hasMore"]
            metric = check["metric"]
            if metric == "missing_title_block":
                bad = [r for r in rows if not r.get("titleBlock")]
            elif metric == "unplaced_views":
                rows = unplaced
                bad = rows
            elif metric == "name_matches":
                pattern = compile_pattern(check.get("pattern", ""))
                bad = [
                    r
                    for r in rows
                    if not self.matches(pattern, r.get(check.get("field", "name"), ""))
                ]
            else:
                raise NotAssessed(f"Unsupported sheets metric: {metric}")
            population, measured, ids = len(rows), len(bad), [r["id"] for r in bad]
        elif kind == "name_matches" and rule.get("scope", {}).get("categories") == [
            {"builtin": "OST_Views"}
        ]:
            scope = rule["scope"]
            if scope.keys() - {"categories", "include_templates"}:
                raise NotAssessed("revit_list_views cannot honor these scope filters.")
            pattern = compile_pattern(check["pattern"])
            rows, offset = [], 0
            while True:
                data = await self.read(
                    "revit_list_views",
                    include_templates=scope.get("include_templates", False),
                    offset=offset,
                    limit=500,
                )
                page = data["views"]
                rows.extend(
                    row
                    for row in page
                    if scope.get("include_templates", False) or not row.get("isTemplate", False)
                )
                if not data.get("hasMore", False):
                    break
                if not page:
                    partial = True
                    break
                offset += len(page)
            bad = [row for row in rows if not self.matches(pattern, row["name"])]
            population, measured, ids = len(rows), len(bad), [row["id"] for row in bad]
            if "max_percent" in threshold or "min_percent" in threshold:
                if not population:
                    raise NotAssessed("Element population is empty.")
                measured = (
                    100
                    * (len(bad) if "max_percent" in threshold else population - len(bad))
                    / population
                )
        else:
            if kind == "count" and check.get("metric"):
                raise NotAssessed(f"Unsupported count metric: {check['metric']}")
            scope = dict(rule.get("scope", {}))
            if scope.get("categories"):
                scope["categories"] = [
                    await self.resolve(r, "categories") for r in scope["categories"]
                ]
            parameter = (
                await self.resolve(check["parameter"], "parameters")
                if "parameter" in check
                else None
            )
            if kind == "parameter_filled":
                if scope.keys() - {"categories", "level", "workset", "view"} or not scope.get(
                    "categories"
                ):
                    raise NotAssessed(
                        "Parameter fill requires categories and supported scope filters."
                    )
                data = await self.read(
                    "revit_parameter_fill_check", **scope, parameters=[parameter]
                )
                rows = data.get("rows")
                if rows is None:
                    # Live fill responses group all selected categories under each parameter.
                    parameters = data["parameters"]
                    selected = [p for p in parameters if p["name"] == parameter]
                    rows = [
                        {
                            "total": p["elements"],
                            "filled": p["filled"],
                            "sampleEmptyIds": p.get("emptySampleIds", []),
                            "sampleMissingIds": p.get("missingSampleIds", []),
                        }
                        for p in selected
                    ]
                if not rows:
                    raise NotAssessed("No parameter fill rows returned.")
                population = sum(r["total"] for r in rows)
                if not population:
                    raise NotAssessed("Parameter population is empty.")
                measured = 100 * sum(r["filled"] for r in rows) / population
                ids = [
                    i
                    for r in rows
                    for i in r.get("sampleEmptyIds", []) + r.get("sampleMissingIds", [])
                ]
                self.current["element_ids_truncated"] = sum(
                    r["total"] - r["filled"] for r in rows
                ) > len(set(ids))
            else:
                field = parameter or "name"
                pattern = compile_pattern(check["pattern"]) if "pattern" in check else None
                rows = []
                for page in range(20):
                    data = await self.read(
                        "revit_query_elements",
                        **scope,
                        fields=["id", field],
                        offset=page * 500,
                        limit=500,
                    )
                    rows.extend(data["elements"])
                    if not data["hasMore"]:
                        break
                partial = data["hasMore"]
                population = len(rows)
                if kind == "count":
                    measured = population
                else:

                    def value(row):
                        cell = row.get("values", row).get(field)
                        if isinstance(cell, dict):
                            return (
                                cell.get("value", cell.get("numericValue"))
                                if cell.get("hasValue", True)
                                else None
                            )
                        return cell

                    bad = [
                        r
                        for r in rows
                        if (
                            value(r) not in check["values"]
                            if kind == "parameter_in"
                            else not self.matches(pattern, value(r) if value(r) is not None else "")
                        )
                    ]
                    measured, ids = len(bad), [r["id"] for r in bad]
                    if "max_percent" in threshold or "min_percent" in threshold:
                        if not population:
                            raise NotAssessed("Element population is empty.")
                        measured = (
                            100
                            * (len(bad) if "max_percent" in threshold else population - len(bad))
                            / population
                        )
        if kind in ("model_health", "sheets") and (
            "max_percent" in threshold or "min_percent" in threshold
        ):
            if not population:
                raise NotAssessed("A nonempty population is required for a percentage threshold.")
            measured = 100 * measured / population
        operator, limit = next(iter(threshold.items()))
        failed = measured < limit if operator == "min_percent" else measured > limit
        self.current.update(
            measured=measured,
            population=population,
            element_ids=list(dict.fromkeys(ids))[:500],
            threshold=threshold,
        )
        self.current["element_ids_truncated"] |= len(set(ids)) > 500
        self.current["status"] = (
            "partial"
            if partial or self.current["skipped"] or self.current["skippedCount"]
            else ("failed" if failed else "passed")
        )
        self.current["violated"] = failed

    async def evaluate(self, rules):
        findings = []
        for rule in rules:
            self.current = {
                **rule,
                "status": "not_assessed",
                "measured": None,
                "threshold": rule.get("threshold", {"max_count": 0}),
                "population": None,
                "element_ids": [],
                "element_ids_truncated": False,
                "reads": [],
                "skipped": [],
                "skippedCount": 0,
            }
            try:
                if not rule.get("enabled", True):
                    raise NotAssessed("Rule is disabled.")
                if time.monotonic() >= self.deadline:
                    raise TimeoutError("Whole-run audit budget exhausted.")
                await self.check(rule)
            except TimeoutError as error:
                self.current.update(
                    status="partial",
                    reason=str(error) or "Regular expression exceeded two seconds.",
                )
            except (NotAssessed, KeyError, TypeError, ValueError) as error:
                self.current.update(status="not_assessed", reason=str(error))
            findings.append(self.current)
        return findings
