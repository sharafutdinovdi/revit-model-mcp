from __future__ import annotations

from typing import Any, Callable


def query_payload(
    categories: list[str] | None,
    family: str | None,
    type_name: str | None,
    level: str | None,
    view: str | None,
    workset: str | None,
    phase: str | None,
    area_scheme: str | None,
    parameter_filters: list[dict[str, Any]] | None,
    fields: list[str] | None,
    offset: int,
    limit: int,
    sort_field: str,
    sort_direction: str,
    optional_text: Callable[[str | None], str | None],
    unique_texts: Callable[[list[str]], list[str]],
) -> dict[str, Any]:
    payload = common_payload(
        "query-elements",
        categories,
        family,
        type_name,
        level,
        view,
        workset,
        phase,
        area_scheme,
        parameter_filters,
        optional_text,
        unique_texts,
    )
    if offset < 0 or limit <= 0:
        raise ValueError("offset must be nonnegative and limit must be greater than zero.")
    direction = (optional_text(sort_direction) or "asc").lower()
    if direction not in {"asc", "desc"}:
        raise ValueError("sort_direction must be asc or desc.")
    payload.update(
        {
            "offset": offset,
            "limit": limit,
            "sort": {
                "field": optional_text(sort_field) or "id",
                "direction": direction,
            },
        }
    )
    if normalized := unique_texts(fields or []):
        payload["fields"] = normalized
    return payload


def aggregate_payload(
    categories: list[str] | None,
    family: str | None,
    type_name: str | None,
    level: str | None,
    view: str | None,
    workset: str | None,
    phase: str | None,
    area_scheme: str | None,
    parameter_filters: list[dict[str, Any]] | None,
    group_by: list[str],
    numeric_field: str | None,
    optional_text: Callable[[str | None], str | None],
    unique_texts: Callable[[list[str]], list[str]],
) -> dict[str, Any]:
    payload = common_payload(
        "aggregate-elements",
        categories,
        family,
        type_name,
        level,
        view,
        workset,
        phase,
        area_scheme,
        parameter_filters,
        optional_text,
        unique_texts,
    )
    groups = unique_texts(group_by)
    if len(groups) not in {1, 2}:
        raise ValueError("group_by must contain one or two fields.")
    payload["groupBy"] = groups
    if numeric := optional_text(numeric_field):
        payload["numericField"] = numeric
    return payload


def common_payload(
    command: str,
    categories: list[str] | None,
    family: str | None,
    type_name: str | None,
    level: str | None,
    view: str | None,
    workset: str | None,
    phase: str | None,
    area_scheme: str | None,
    parameter_filters: list[dict[str, Any]] | None,
    optional_text: Callable[[str | None], str | None],
    unique_texts: Callable[[list[str]], list[str]],
) -> dict[str, Any]:
    payload: dict[str, Any] = {"command": command}
    if normalized := unique_texts(categories or []):
        payload["categories"] = normalized
    for key, value in (
        ("family", family),
        ("type", type_name),
        ("level", level),
        ("view", view),
        ("workset", workset),
        ("phase", phase),
        ("areaScheme", area_scheme),
    ):
        if normalized := optional_text(value):
            payload[key] = normalized
    if normalized_filters := normalize_parameter_filters(
        parameter_filters or [], optional_text
    ):
        payload["parameterFilters"] = normalized_filters
    return payload


def normalize_parameter_filters(
    filters: list[dict[str, Any]],
    optional_text: Callable[[str | None], str | None],
) -> list[dict[str, str]]:
    result: list[dict[str, str]] = []
    for item in filters:
        raw_name = item.get("parameter")
        raw_operation = item.get("operator")
        name = optional_text(raw_name) if isinstance(raw_name, str) else None
        operation = (
            optional_text(raw_operation) if isinstance(raw_operation, str) else None
        )
        if not name or not operation:
            raise ValueError("Each parameter_filter requires parameter and operator.")
        normalized = {"parameter": name, "operator": operation.lower()}
        raw_value = item.get("value")
        value = optional_text(str(raw_value)) if raw_value is not None else None
        if value:
            normalized["value"] = value
        result.append(normalized)
    return result
