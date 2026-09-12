from __future__ import annotations

import argparse
import os
from pathlib import PureWindowsPath
from typing import Annotated, Any

from mcp.server import MCPServer
from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import AliasChoices, Field

from revit_model_mcp.actions import register_actions
from revit_model_mcp.http_host import HttpHost
from revit_model_mcp.revit_channel import (
    CHANNEL_DIRECTORY,
    DEFAULT_HOST,
    DEFAULT_PICKUP_TIMEOUT_SECONDS,
    DEFAULT_TIMEOUT_SECONDS,
    ReadJob,
    RevitChannelError,
    RevitReadChannel,
)
from revit_model_mcp.ssh_host import SshPowerShellHost


def create_host(value: str, token: str | None = None) -> SshPowerShellHost | HttpHost:
    if value.startswith(("http://", "https://")):
        return HttpHost(value, token)
    if value == "local":
        return SshPowerShellHost("local", local=True)
    if value.startswith("ssh:") and value[4:]:
        return SshPowerShellHost(value[4:])
    raise ValueError(
        "REVIT_MCP_HOST must be local, ssh:<alias>, http://host:port or https://host:port."
    )


host = create_host(os.environ.get("REVIT_MCP_HOST", DEFAULT_HOST))
channel = RevitReadChannel(host)


def redact_model_paths(value: Any) -> Any:
    if os.environ.get("REVIT_MCP_REDACT_PATHS") != "1":
        return value
    if isinstance(value, dict):
        return {
            key: PureWindowsPath(item).name
            if key in {"documentPath", "path"} and isinstance(item, str)
            else redact_model_paths(item)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [redact_model_paths(item) for item in value]
    return value


READ_ONLY_TOOL = ToolAnnotations(readOnlyHint=True, destructiveHint=False, idempotentHint=True)
TimeoutSeconds = Annotated[
    int, Field(validation_alias=AliasChoices("timeout_seconds", "timeoutSeconds"))
]
PickupTimeoutSeconds = Annotated[
    int, Field(validation_alias=AliasChoices("pickup_timeout_seconds", "pickupTimeoutSeconds"))
]
GroupBy = Annotated[list[str], Field(validation_alias=AliasChoices("group_by", "groupBy"))]
SumField = Annotated[
    str | None,
    Field(validation_alias=AliasChoices("sum_field", "sumField", "numeric_field", "numericField")),
]
TypeName = Annotated[
    str | None, Field(validation_alias=AliasChoices("type_name", "typeName", "type"))
]
AreaScheme = Annotated[
    str | None, Field(validation_alias=AliasChoices("area_scheme", "areaScheme"))
]
ParameterFilters = Annotated[
    list[dict[str, Any]] | None,
    Field(validation_alias=AliasChoices("parameter_filters", "parameterFilters")),
]
SortField = Annotated[str, Field(validation_alias=AliasChoices("sort_field", "sortField"))]
SortDirection = Annotated[
    str, Field(validation_alias=AliasChoices("sort_direction", "sortDirection"))
]
ViewType = Annotated[str | None, Field(validation_alias=AliasChoices("view_type", "viewType"))]
NameContains = Annotated[
    str | None, Field(validation_alias=AliasChoices("name_contains", "nameContains"))
]
ViewName = Annotated[str, Field(validation_alias=AliasChoices("view", "view_name", "viewName"))]
PixelSize = Annotated[
    int, Field(validation_alias=AliasChoices("pixel_size", "pixelSize"), ge=1, le=4000)
]
SaveTo = Annotated[str | None, Field(validation_alias=AliasChoices("save_to", "saveTo"))]
OptionalViewName = Annotated[
    str | None, Field(validation_alias=AliasChoices("view", "view_name", "viewName"))
]
ElementId = Annotated[int, Field(validation_alias=AliasChoices("element_id", "elementId", "id"))]
WarningText = Annotated[
    str | None, Field(validation_alias=AliasChoices("warning_text", "warningText"))
]
IncludeGeometry = Annotated[
    bool, Field(validation_alias=AliasChoices("include_geometry", "includeGeometry"))
]
IncludeElements = Annotated[
    bool, Field(validation_alias=AliasChoices("include_elements", "includeElements"))
]
SourceId = Annotated[int | None, Field(validation_alias=AliasChoices("source_id", "sourceId"))]
SourceName = Annotated[
    str | None, Field(validation_alias=AliasChoices("source_name", "sourceName"))
]
Document = Annotated[str | None, Field(validation_alias=AliasChoices("document", "targetDocument"))]

mcp = MCPServer(
    "Revit Model Reader",
    version="0.1.0",
    instructions=(
        "Read-only by default. Actions are a separate tool set you enable on purpose. "
        "For universal model analysis, call revit_list_catalog first, "
        "revit_aggregate_elements second, and revit_query_elements only when rows are needed."
    ),
)


async def _execute(
    job: ReadJob,
    timeout_seconds: int,
    pickup_timeout_seconds: int,
    document: str | None,
) -> dict[str, Any]:
    try:
        result = await channel.execute(
            job.for_document(document), timeout_seconds, pickup_timeout_seconds
        )
        return redact_model_paths(result)
    except RevitChannelError as error:
        raise ToolError(str(error)) from error


def addressed_tool(function):
    function.__doc__ = (function.__doc__ or "") + (
        "\n\nIf more than one Revit instance is running, document is required; "
        "otherwise any instance may respond."
    )
    return mcp.tool(annotations=READ_ONLY_TOOL)(function)


@addressed_tool
async def revit_ping(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Check the RevitModelMcp connection without reading the model.

    Returns pong even when no document is active.
    Parameters: timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(ReadJob.ping(), timeout_seconds, pickup_timeout_seconds, document)


@addressed_tool
async def revit_document_info(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read general information about the active Revit model.

    Returns the file name, Revit version, levels, area schemes, worksets and view count.
    Call revit_list_views next for view analysis.
    Parameters: timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.document_info(), timeout_seconds, pickup_timeout_seconds, document
    )


@addressed_tool
async def revit_model_health(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read model quality counts; call before an export or hand-over."""
    return await _execute(
        ReadJob("model-health", {"command": "model-health"}),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_links_status(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read RVT, CAD and image link status; call before an export or hand-over."""
    return await _execute(
        ReadJob("links-status", {"command": "links-status"}),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_shared_coordinates(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read project and survey coordinates; call before an export or hand-over."""
    return await _execute(
        ReadJob("shared-coordinates", {"command": "shared-coordinates"}),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_parameter_fill_check(
    categories: Annotated[list[str], Field(min_length=1, max_length=20)],
    parameters: Annotated[list[str], Field(min_length=1, max_length=30)],
    level: str | None = None,
    workset: str | None = None,
    view: OptionalViewName = None,
    sample_limit: Annotated[int, Field(ge=1, le=100)] = 20,
    include_types: bool = True,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Count filled, empty and missing parameters; call before an export or hand-over."""
    payload = dict(
        ReadJob.query_elements(
            categories=categories,
            level=level,
            workset=workset,
            view=view,
        ).payload
    )
    for key in ("fields", "offset", "limit", "sort"):
        payload.pop(key, None)
    payload.update(
        command="parameter-fill-check",
        parameters=parameters,
        sampleLimit=sample_limit,
        includeTypes=include_types,
    )
    return await _execute(
        ReadJob("parameter-fill-check", payload),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_list_catalog(
    section: str,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Discover valid model names before filtering.

    Start universal queries here. section accepts categories, family-types, levels,
    area-schemes, views, worksets, phases or parameters. The parameters section lists
    names, categories and value types. Prefer aggregation before requesting rows.
    Parameters: section, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.list_catalog(section), timeout_seconds, pickup_timeout_seconds, document
    )


@addressed_tool
async def revit_aggregate_elements(
    group_by: GroupBy,
    sum_field: SumField = None,
    categories: list[str] | None = None,
    family: str | None = None,
    type_name: TypeName = None,
    level: str | None = None,
    view: OptionalViewName = None,
    workset: str | None = None,
    phase: str | None = None,
    area_scheme: AreaScheme = None,
    parameter_filters: ParameterFilters = None,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read a compact element summary after revit_list_catalog.

    group_by accepts one or two system fields or exact parameter names. count is
    always included. sum_field adds sum and average. Use an exact localized Revit
    parameter name from the parameters catalog. parameter_filters accepts objects
    with parameter/operator/value. Operators: equals, contains, greater, less,
    empty, not-empty, exists. For area totals group by level and select the area scheme.
    Parameters: group_by, sum_field, categories, family, type_name, level, view, workset, phase, area_scheme, parameter_filters, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.aggregate_elements(
            group_by,
            sum_field,
            categories,
            family,
            type_name,
            level,
            view,
            workset,
            phase,
            area_scheme,
            parameter_filters,
        ),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_query_elements(
    categories: list[str] | None = None,
    family: str | None = None,
    type_name: TypeName = None,
    level: str | None = None,
    view: OptionalViewName = None,
    workset: str | None = None,
    phase: str | None = None,
    area_scheme: AreaScheme = None,
    parameter_filters: ParameterFilters = None,
    fields: list[str] | None = None,
    offset: int = 0,
    limit: int = 100,
    sort_field: SortField = "id",
    sort_direction: SortDirection = "asc",
    include_geometry: IncludeGeometry = False,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read a page of elements after revit_list_catalog.

    Use aggregation first when a summary is sufficient. Model filters combine.
    parameter_filters accepts equals, contains, greater, less, empty, not-empty,
    exists. fields accepts system fields or exact localized parameter names.
    Advance offset while hasMore=true. sort_direction accepts asc or desc.
    include_geometry=true adds location and boundingBox in model millimetres,
    rounded to 1 decimal, plus roomCenterMm for placed rooms. Default false keeps
    large queries small. Use roomCenterMm when placing something inside a room.
    Parameters: categories, family, type_name, level, view, workset, phase, area_scheme, parameter_filters, fields, offset, limit, sort_field, sort_direction, include_geometry, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.query_elements(
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
            include_geometry,
        ),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_list_views(
    view_type: ViewType = None,
    name_contains: NameContains = None,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Find views in the active model before analyzing a view.

    view_type accepts an English Revit ViewType such as FloorPlan. name_contains
    matches a substring. Both filters are optional. Use an exact returned name
    with revit_view_summary before requesting element pages.
    Parameters: view_type, name_contains, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.list_views(view_type, name_contains),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_view_summary(
    view: ViewName,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read element categories and counts for a selected view.

    Use an exact name from revit_list_views. Select relevant categories from this
    summary before calling revit_view_elements.
    Parameters: view, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.view_summary(view), timeout_seconds, pickup_timeout_seconds, document
    )


@addressed_tool
async def revit_export_view(
    view: ViewName,
    pixel_size: PixelSize = 1600,
    save_to: SaveTo = None,
    document: Document = None,
) -> dict[str, Any]:
    """Export a selected view to PNG when numbers do not explain geometry.

    Use for visual checks of outlines, zones and room boundaries. The tool does not
    change the active view or write to the model. The image is copied from the Revit
    host to save_to or a local temporary directory. The response contains a local
    path and metadata without base64.
    Parameters: view, pixel_size, save_to, document.
    """
    return await _execute(
        ReadJob.export_view(view, pixel_size, save_to),
        DEFAULT_TIMEOUT_SECONDS,
        DEFAULT_PICKUP_TIMEOUT_SECONDS,
        document,
    )


@addressed_tool
async def revit_view_elements(
    view: ViewName,
    categories: list[str] | None = None,
    offset: int = 0,
    limit: int = 100,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read one page of elements in a selected view.

    Call revit_view_summary first. Supply relevant categories, offset and limit.
    Advance offset while hasMore=true. Use revit_element_details for all parameters
    of a selected element.
    Parameters: view, categories, offset, limit, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.view_elements(view, categories, offset, limit),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_element_details(
    element_id: ElementId,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read parameters and geometry of an element by Revit id.

    Use an id from revit_query_elements or revit_view_elements. Returns instance
    and type parameters and related warnings. Rooms also include level, area,
    volume and boundaries. location and boundingBox use model millimetres rounded
    to 1 decimal. Unavailable geometry is omitted. Use roomCenterMm from the
    room location when placing something inside a room; boundingBox.centerMm
    may lie outside a nonrectangular room.
    Parameters: element_id, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.element_details(element_id), timeout_seconds, pickup_timeout_seconds, document
    )


@addressed_tool
async def revit_view_warnings(
    view: ViewName,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read Revit warnings related to elements in a selected view.

    Use an exact name from revit_list_views. Use revit_view_summary first to inspect
    the contents of the view.
    Parameters: view, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.view_warnings(view), timeout_seconds, pickup_timeout_seconds, document
    )


@addressed_tool
async def revit_list_warnings(
    warning_text: WarningText = None,
    include_elements: IncludeElements = False,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Group model warnings by text.

    Start without warning_text or elements. Repeat with an exact warning_text
    and include_elements=true to inspect elements in a warning group.
    Parameters: warning_text, include_elements, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.list_warnings(warning_text, include_elements),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_list_relations(
    relation: str,
    source_id: SourceId = None,
    source_name: SourceName = None,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read model object membership or dependencies.

    relation accepts level-rooms with source_name, group-elements or nested-family
    with source_id, area-scheme-elements with source_name, or
    view-template-dependents with source_name. Obtain names from the catalog.
    Parameters: relation, source_id, source_name, timeout_seconds, pickup_timeout_seconds, document.
    """
    return await _execute(
        ReadJob.list_relations(relation, source_id, source_name),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@mcp.tool(annotations=READ_ONLY_TOOL)
async def revit_list_instances(document: Document = None) -> list[dict[str, object]]:
    """List Revit processes with active documents, versions and processId.

    The add-in reports the document through a heartbeat file. Fallback processes
    have an empty document and pluginResponding=false. Optional document filters
    by a substring of the model name.
    Parameters: document.
    """
    try:
        return redact_model_paths(await host.list_revit_instances(document))
    except RevitChannelError as error:
        raise ToolError(str(error)) from error


register_actions(mcp, _execute, lambda: host)


def main() -> None:
    default_host = os.environ.get("REVIT_MCP_HOST", DEFAULT_HOST)
    channel_dir = os.environ.get("REVIT_MCP_CHANNEL_DIR") or rf"%LOCALAPPDATA%\{CHANNEL_DIRECTORY}"
    parser = argparse.ArgumentParser(
        description="Read a live Revit model through MCP.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Transport settings from the environment (no connection is opened):\n"
            f"  host mode: {default_host}\n"
            f"  channel dir (Windows): {channel_dir}\n"
            f"  redact paths: {os.environ.get('REVIT_MCP_REDACT_PATHS') == '1'}"
        ),
    )
    parser.add_argument(
        "--host",
        default=default_host,
        help="local, ssh:<alias>, http://host:port or https://host:port; overrides REVIT_MCP_HOST.",
    )
    parser.add_argument(
        "--redact-paths",
        action="store_true",
        help="Return model file names without directory paths.",
    )
    parser.add_argument(
        "--token",
        default=None,
        help="HTTP bearer token; overrides REVIT_MCP_TOKEN. Prefer the environment to keep tokens out of shell history.",
    )
    args = parser.parse_args()
    global host, channel
    host = create_host(args.host, args.token)
    channel = RevitReadChannel(host)
    if args.redact_paths:
        os.environ["REVIT_MCP_REDACT_PATHS"] = "1"
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
