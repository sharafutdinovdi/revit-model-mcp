from __future__ import annotations

import argparse
import os
from typing import Annotated, Any

from mcp.server import MCPServer
from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import AliasChoices, Field

from revit_model_mcp import package_version
from revit_model_mcp.actions import env_flag, redact_model_paths, register_actions
from revit_model_mcp.http_host import HttpHost
from revit_model_mcp.pipe_host import LocalPipeHost
from revit_model_mcp.revit_channel import (
    CHANNEL_DIRECTORY,
    DEFAULT_HOST,
    DEFAULT_PICKUP_TIMEOUT_SECONDS,
    DEFAULT_TIMEOUT_SECONDS,
    ReadJob,
    RevitChannelError,
    RevitReadChannel,
    with_client_identity,
)
from revit_model_mcp.ssh_host import SshPowerShellHost


def create_host(
    value: str, token: str | None = None
) -> LocalPipeHost | SshPowerShellHost | HttpHost:
    if value.startswith(("http://", "https://")):
        return HttpHost(value, token)
    if value == "local":
        # Named pipe when the add-in advertises pipe/1, otherwise the local file channel.
        return LocalPipeHost(SshPowerShellHost("local", local=True))
    if value.startswith("ssh:") and value[4:]:
        return SshPowerShellHost(value[4:])
    raise ValueError(
        "REVIT_MCP_HOST must be local, ssh:<alias>, http://host:port or https://host:port."
    )


host = create_host(os.environ.get("REVIT_MCP_HOST", DEFAULT_HOST))
channel = RevitReadChannel(host)

READ_ONLY_TOOL = ToolAnnotations(readOnlyHint=True, destructiveHint=False, idempotentHint=True)
TimeoutSeconds = Annotated[
    int,
    Field(
        validation_alias=AliasChoices("timeout_seconds", "timeoutSeconds"),
        description="Positive integer seconds to wait for a result after pickup (120 when omitted); HTTP uses this as its response budget. Expiry raises an error, and increasing it does not override the add-in's execution limits.",
    ),
]
PickupTimeoutSeconds = Annotated[
    int,
    Field(
        validation_alias=AliasChoices("pickup_timeout_seconds", "pickupTimeoutSeconds"),
        description="Positive integer seconds to wait for the add-in to pick up a local or SSH job (300 when omitted); ignored over HTTP. A pickup timeout raises an error but the pending job may still execute later.",
    ),
]
GroupBy = Annotated[
    list[str],
    Field(
        validation_alias=AliasChoices("group_by", "groupBy"),
        description="Required list of one or two distinct system fields (e.g. category, family, type, level) or exact localized parameter names from the catalog; no default. Each combination produces a count, with numeric totals added by sum_field.",
    ),
]
SumField = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("sum_field", "sumField", "numeric_field", "numericField"),
        description="Numeric system field or exact localized parameter name to sum and average within each group. Default null omits numeric aggregation; lengths use mm, areas m2, volumes m3, and other quantities use the returned unit.",
    ),
]
TypeName = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("type_name", "typeName", "type"),
        description="Exact type name to match, case-insensitively, combined with the other model filters. Default null applies no type filter; discover names with the family-types catalog.",
    ),
]
AreaScheme = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("area_scheme", "areaScheme"),
        description="Exact area-scheme name from the area-schemes catalog, matched case-insensitively. Default null applies no scheme filter; selecting a scheme restricts results to its areas and combines with the other filters.",
    ),
]
ParameterFilters = Annotated[
    list[dict[str, Any]] | None,
    Field(
        validation_alias=AliasChoices("parameter_filters", "parameterFilters"),
        description="AND-combined objects with an exact localized parameter name in parameter, an operator (equals, contains, greater, less, empty, not-empty, exists), and value for comparisons; default null applies no parameter filters. Numeric values use mm, m2, m3 or other document display units; contains requires text, and empty/not-empty/exists need no value.",
    ),
]
SortField = Annotated[
    str,
    Field(
        validation_alias=AliasChoices("sort_field", "sortField"),
        description="System field (e.g. id, category, level) or exact localized parameter name to sort before pagination; default id sorts by Revit element ID. Uses sort_direction, with element ID breaking ties for other fields.",
    ),
]
SortDirection = Annotated[
    str,
    Field(
        validation_alias=AliasChoices("sort_direction", "sortDirection"),
        description="Sort order: asc or desc, case-insensitively; default asc means ascending. Applies to sort_field before offset and limit.",
    ),
]
ViewType = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("view_type", "viewType"),
        description="English Revit ViewType name, such as FloorPlan or ThreeD, matched case-insensitively. Default null includes all non-template view types; combines with name_contains.",
    ),
]
NameContains = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("name_contains", "nameContains"),
        description="Case-insensitive substring of the view name. Default null applies no name filter; combines with view_type and excludes templates.",
    ),
]
ViewName = Annotated[
    str,
    Field(
        validation_alias=AliasChoices("view", "view_name", "viewName"),
        description="Required exact, case-sensitive non-template view name from revit_list_views, or its Revit view ID as a decimal string; no default. An exact name takes precedence over interpreting a numeric string as an ID.",
    ),
]
PixelSize = Annotated[
    int,
    Field(
        validation_alias=AliasChoices("pixel_size", "pixelSize"),
        ge=1,
        le=4000,
        description="PNG size in pixels along the fitted image dimension, an integer from 1 to 4000. Default 1600 fits the view at that size while preserving its aspect ratio.",
    ),
]
SaveTo = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("save_to", "saveTo"),
        description="New PNG file path on the MCP client machine, not the Revit host; an existing destination causes an error. Default null downloads to a local temporary directory and returns localPath.",
    ),
]
OptionalViewName = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("view", "view_name", "viewName"),
        description="Exact non-template view name from the views catalog, matched case-insensitively, to restrict the element collector. Default null searches the document without a view filter; combines with the other model filters.",
    ),
]
ElementId = Annotated[
    int,
    Field(
        validation_alias=AliasChoices("element_id", "elementId", "id"),
        description="Required positive integer Revit element ID from revit_query_elements or revit_view_elements; no default. The ID must exist in the target document.",
    ),
]
WarningText = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("warning_text", "warningText"),
        description="Exact warning description from revit_list_warnings, matched case-insensitively. Default null includes all warning groups; an unmatched supplied text raises an error.",
    ),
]
IncludeGeometry = Annotated[
    bool,
    Field(
        validation_alias=AliasChoices("include_geometry", "includeGeometry"),
        description="Whether each query row includes available location, boundingBox and placed-room roomCenterMm coordinates in model millimetres, rounded to one decimal. Default false omits geometry; true increases the response size.",
    ),
]
IncludeElements = Annotated[
    bool,
    Field(
        validation_alias=AliasChoices("include_elements", "includeElements"),
        description="Whether warning groups include affected elements with ID, category and name. Default false returns counts without element rows; combine true with warning_text to inspect one group.",
    ),
]
SourceId = Annotated[
    int | None,
    Field(
        validation_alias=AliasChoices("source_id", "sourceId"),
        description="Positive integer Revit ID of the source group for group-elements or family instance for nested-family. Default null is valid for name-based relations; these two ID-based relations require a value.",
    ),
]
SourceName = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("source_name", "sourceName"),
        description="Exact, case-insensitive source name: a level for level-rooms, area scheme for area-scheme-elements, or view template for view-template-dependents. Default null is valid for ID-based relations; name-based relations require a value from the catalog.",
    ),
]
Document = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("document", "targetDocument"),
        description="Case-insensitive substring of the target active document title or file name. Reads require exactly one matching instance; omitted document requires exactly one running instance. Zero or multiple matches fail before publishing. revit_list_instances returns all matching instances, or all running instances when omitted.",
    ),
]

mcp = MCPServer(
    "Revit Model Reader",
    version=package_version(),
    instructions=(
        "Read-only by default. Actions are a separate tool set you enable on purpose. "
        "For save, sync and close-with-loss, show confirmationText to the user and retry "
        "with confirm_token only after the user explicitly agrees in chat. "
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
    title = {
        "revit_ping": "Check Revit Connection",
        "revit_jobs": "List Revit Jobs",
        "revit_document_info": "Document Info",
        "revit_documents": "Open Documents",
        "revit_list_catalog": "List Catalog",
        "revit_aggregate_elements": "Aggregate Elements",
        "revit_query_elements": "Query Elements",
        "revit_list_views": "List Views",
        "revit_view_summary": "View Summary",
        "revit_view_info": "View Info",
        "revit_export_view": "Export View to PNG",
        "revit_view_elements": "View Elements",
        "revit_element_details": "Element Details",
        "revit_view_warnings": "View Warnings",
        "revit_list_warnings": "List Warnings",
        "revit_list_relations": "List Relations",
        "revit_list_instances": "List Instances",
        "revit_model_health": "Model Health Check",
        "revit_links_status": "Links Status",
        "revit_shared_coordinates": "Shared Coordinates",
        "revit_parameter_fill_check": "Parameter Fill Check",
        "revit_family_audit": "Audit Families",
        "revit_nwc_settings_check": "Check NWC Settings",
        "revit_compare_link_datums": "Compare Link Datums",
    }[function.__name__]
    return mcp.tool(title=title, annotations=READ_ONLY_TOOL.model_copy(update={"title": title}))(
        with_client_identity(function)
    )


@addressed_tool
async def revit_jobs(
    cancel_job_id: str | None = None,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """List queued and running jobs in the selected Revit process.

    Supply cancel_job_id to cancel one of this server process's own jobs.
    A running action finishes without interruption.
    """
    return await _execute(
        ReadJob.jobs(cancel_job_id), timeout_seconds, pickup_timeout_seconds, document
    )


@addressed_tool
async def revit_compare_link_datums(
    link: str,
    kinds: list[str] | None = None,
    name_map: dict[str, str] | None = None,
    prefix: str = "",
    suffix: str = "",
    level_offset_mm: float = 0,
    reuse_matching: bool = True,
    tolerance_mm: float = 0.5,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Compare host grids and levels with one loaded Revit link. No model change is made.

    A geometric match does not create a monitor relationship or later Coordination Review warnings.
    """
    return await _execute(
        ReadJob(
            "compare-link-datums",
            {
                "command": "compare-link-datums",
                "link": link,
                "kinds": kinds if kinds is not None else ["grids", "levels"],
                "nameMap": name_map or {},
                "prefix": prefix,
                "suffix": suffix,
                "levelOffsetMm": level_offset_mm,
                "reuseMatching": reuse_matching,
                "toleranceMm": tolerance_mm,
            },
        ),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_ping(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Check the RevitModelMcp connection without reading the model.

    Returns a response with data="pong", even when no document is active.
    Connection failures and timeouts raise errors; no partial result is returned.
    """
    return await _execute(ReadJob.ping(), timeout_seconds, pickup_timeout_seconds, document)


@addressed_tool
async def revit_nwc_settings_check(
    settings_xml: str,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Parse a Navisworks exporter XML file on the Revit workstation without exporting."""
    return await _execute(
        ReadJob(
            "nwc-settings-check", {"command": "nwc-settings-check", "settingsXml": settings_xml}
        ),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_document_info(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read general information about the active Revit model.

    Returns data with file name, Revit version, levels (elevations in mm), area schemes, worksets and view count.
    Absent collections are empty; non-workshared models have no worksets.
    Call revit_list_views next for view analysis.
    A missing active document, read failure or timeout raises an error; partial data is not returned.
    """
    return await _execute(
        ReadJob.document_info(), timeout_seconds, pickup_timeout_seconds, document
    )


@addressed_tool
async def revit_documents(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """List every open document in one Revit process, including background documents.

    Returns title, path, isActive, isFamilyDocument, isWorkshared, isDetached,
    isModified, openedByMcp and centralPath when available. An empty process returns [].
    """
    return await _execute(
        ReadJob("documents", {"command": "documents"}),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_model_health(
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read model quality counts before an export or hand-over.

    Returns data with project metadata, file size in bytes, counts, unit settings and the ten most frequent warning groups.
    Absent objects have zero counts; unavailable metrics are null and listed in skipped with their errors.
    Use revit_list_warnings to inspect affected elements.
    A missing active document, overall read failure or timeout raises an error; timeout partials are not returned.
    """
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
    """Read RVT, CAD and image link status before an export or hand-over.

    Returns data with summary counts and rvtLinks, cadLinks and images lists containing status, paths and instance counts.
    Each list is capped at 100 entries by ID without pagination; summary counts cover all entries.
    No links produce empty lists; per-entry failures appear in error with status Other.
    A missing active document, overall read failure or timeout raises an error; timeout partials are not returned.
    """
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
    """Read project and survey coordinates before an export or hand-over.

    Returns data with base/survey points, the active site, project locations and link offsets in mm and rotations in degrees, rounded to one decimal.
    Location and link lists are capped at 100 without pagination, with total counts; no links produce an empty sharedSiteFromLinks list.
    A missing active document, read failure or timeout raises an error; partial data is not returned.
    """
    return await _execute(
        ReadJob("shared-coordinates", {"command": "shared-coordinates"}),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_parameter_fill_check(
    categories: Annotated[
        list[str],
        Field(
            min_length=1,
            max_length=20,
            description="Required list of 1-20 category names from the categories catalog; no default. Matches any listed category and combines with level, workset and view filters.",
        ),
    ],
    parameters: Annotated[
        list[str],
        Field(
            min_length=1,
            max_length=30,
            description="Required list of 1-30 exact localized parameter names; no default. Each name uses the first LookupParameter match, with type fallback controlled by include_types; missing names are counted as missing.",
        ),
    ],
    level: str | None = None,
    workset: str | None = None,
    view: OptionalViewName = None,
    sample_limit: Annotated[
        int,
        Field(
            ge=1,
            le=100,
            description="Maximum element IDs sampled per parameter for each empty and missing list, an integer from 1 to 100. Default 20 limits samples only; all matching elements contribute to counts.",
        ),
    ] = 20,
    include_types: bool = True,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Count filled, empty and missing parameters before an export or hand-over.

    Returns data with scope, per-parameter and per-category counts, instance/type ownership, storage types and empty/missing element ID samples.
    No matching elements produce zero counts and empty samples; absent parameters count as missing, and numeric zero counts as filled.
    A missing document, invalid scope or timeout raises an error; partial data is not returned.
    """
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

    Returns data with section and items containing names and section-specific IDs, categories, types or counts; an empty catalog returns items=[].
    section is required: categories, family-types, levels, area-schemes, views, worksets, phases or parameters.
    The parameters section reports localized names, categories and value types.
    Start universal queries here, then prefer revit_aggregate_elements for counts and breakdowns; use revit_query_elements only for individual rows.
    An unknown section, missing document, read failure or timeout raises an error; partial data is not returned.
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
    """Summarize matching elements by one or two fields after revit_list_catalog.

    Returns data with matchedElements and groups containing keys, count and optional numericCount, sum, average and unit.
    Lengths use mm, areas m2 and volumes m3; groups without numeric values have null sum and average.
    No matches return groups=[]; invalid field or filter names raise errors even for empty results.
    Call revit_list_catalog first; prefer this tool for counts and breakdowns, and revit_query_elements only for individual rows.
    For area totals, group by level and select the area scheme.
    A missing document, read failure or timeout raises an error; partial data is not returned.
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
    """Read a page of matching element rows after revit_list_catalog.

    Returns data with elements (id and values), fields, total, offset, limit and hasMore; values include availability, source and units when available.
    Lengths use mm, areas m2 and volumes m3; optional geometry uses model mm rounded to one decimal, and unavailable geometry is omitted.
    Use roomCenterMm for placement inside rooms; a bounding-box centre can lie outside the room.
    No matches or an offset beyond the result return elements=[]; advance offset while hasMore=true.
    Call revit_list_catalog first and prefer revit_aggregate_elements for counts and breakdowns.
    Invalid fields or filters, a missing document, read failure or timeout raise errors; partial data is not returned.
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
    """Find non-template views before analyzing a view.

    Returns data with views containing id, name, type, level, scale and template, plus total and processed counts for scanned non-template views.
    No filter matches return views=[].
    Use a returned name with revit_view_summary before requesting element pages.
    A missing document, read failure or timeout raises an error; partial data is not returned.
    """
    return await _execute(
        ReadJob.list_views(view_type, name_contains),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@addressed_tool
async def revit_view_info(
    view: ViewName,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Inspect one view's template, display, categories, worksets, filters and links."""
    return await _execute(
        ReadJob.view_info(view), timeout_seconds, pickup_timeout_seconds, document
    )


@addressed_tool
async def revit_view_summary(
    view: ViewName,
    timeout_seconds: TimeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    pickup_timeout_seconds: PickupTimeoutSeconds = DEFAULT_PICKUP_TIMEOUT_SECONDS,
    document: Document = None,
) -> dict[str, Any]:
    """Read element categories and counts for a selected view.

    Returns data with header metadata and categories containing count and differentTypes; an empty view returns categories=[].
    Prefer this tool for view counts; select relevant categories before calling revit_view_elements for individual rows.
    A missing document, unknown or unsupported view, read failure or timeout raises an error; partial data is not returned.
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

    Returns data with localPath on the MCP client, image width/height in pixels, sizeBytes and view metadata, without base64.
    The export does not change the active view or write to the model; use it to inspect outlines, zones and room boundaries.
    A missing document, unknown or unsupported view, existing destination, missing PNG or download failure raises an error.
    Uses the default 120-second response and 300-second pickup budgets; timeouts raise errors without partial data.
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

    Returns data with elements, total, offset, limit and hasMore; rows include IDs, category, family, type, level and available measurements in mm, m2 and m3.
    No matches or an offset beyond the result return elements=[]; advance offset while hasMore=true.
    Prefer revit_view_summary for counts and category discovery; use this tool for individual rows and revit_element_details for all parameters.
    A missing document, unknown or unsupported view, read failure or timeout raises an error; partial data is not returned.
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
    """Read parameters and geometry of an element by Revit ID.

    Returns data with element, instance parameters, available typeElement parameters and related warnings; no warnings return an empty list.
    Rooms include level, area in m2, volume in m3 and boundaries in mm; parameter values include display/internal values and metric units when available.
    Location and boundingBox use model mm rounded to one decimal; unavailable geometry is omitted.
    Use roomCenterMm for placement inside rooms; boundingBox.centerMm may lie outside a nonrectangular room.
    An absent element or document, read failure or timeout raises an error; partial data is not returned.
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
    """Read warnings involving elements present in a selected view.

    Returns data with view and warnings containing text, severity and element IDs with presentOnView flags; no related warnings return warnings=[].
    A warning may also involve elements outside the view.
    Use revit_view_summary to inspect view contents, or revit_list_warnings for model-wide warning groups.
    A missing document, unknown or unsupported view, read failure or timeout raises an error; partial data is not returned.
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
    """Group model warnings by description text.

    Returns data with totalWarnings and groups containing text, severity, count, affectedElementCount and optional element rows.
    Start without filters, then repeat with a returned warning_text and include_elements=true to inspect one group.
    No model warnings return groups=[]; an unmatched warning_text raises an error.
    A missing document, read failure or timeout raises an error; partial data is not returned.
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

    Returns data with relation, source and elements containing IDs, names, categories, families and types; no related objects return elements=[].
    relation is required: level-rooms, area-scheme-elements or view-template-dependents with source_name, or group-elements or nested-family with source_id.
    Obtain source names from revit_list_catalog and IDs from element queries.
    An invalid relation, missing or wrong source, missing document, read failure or timeout raises an error; partial data is not returned.
    """
    return await _execute(
        ReadJob.list_relations(relation, source_id, source_name),
        timeout_seconds,
        pickup_timeout_seconds,
        document,
    )


@mcp.tool(
    title="List Instances",
    annotations=READ_ONLY_TOOL.model_copy(update={"title": "List Instances"}),
)
async def revit_list_instances(document: Document = None) -> list[dict[str, object]]:
    """List Revit processes and their active documents.

    Returns documentName, documentPath, revitVersion, processId and pluginResponding; heartbeats also expose fileChannelVersion, startedUtc and httpPort when available.
    For file channel v2, pluginResponding means a bounded correlated ping confirmed the PID and startup identity.
    Busy or unresponsive processes remain listed with pluginResponding=false; processes without a fresh heartbeat remain visible when no document filter is given.
    Legacy heartbeat presence is only a pre-check. No matching instances return [].
    Local and SSH modes use add-in heartbeats with process fallback; fallback records have an empty document and pluginResponding=false.
    HTTP mode reports only its connected process; transport failures raise errors.
    Use this tool before choosing a unique document substring for other tools.
    """
    try:
        return redact_model_paths(await host.list_revit_instances(document))
    except RevitChannelError as error:
        raise ToolError(str(error)) from error


@addressed_tool
async def revit_family_audit(
    families: Annotated[list[str] | None, Field(min_length=1, max_length=200)] = None,
    response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 600,
    document: Document = None,
) -> dict[str, Any]:
    """Audit an open family or named project families without saving or loading changes.

    In project mode, pass exact names or ["*"]. In family mode, omit families.
    Unused shared parameters may still carry schedule or tag data in the project.
    """
    if families is not None and (not families or any(not name.strip() for name in families)):
        raise ToolError("families must contain 1 to 200 non-empty names.")
    if families is not None and "*" in families and families != ["*"]:
        raise ToolError("The '*' family selector must be alone.")
    return await _execute(
        ReadJob("family-audit", {"command": "family-audit", "families": families}),
        response_timeout_s,
        DEFAULT_PICKUP_TIMEOUT_SECONDS,
        document,
    )


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
            f"  redact paths: {env_flag('REVIT_MCP_REDACT_PATHS', False)}"
        ),
    )
    parser.add_argument(
        "--host",
        default=default_host,
        help=(
            "local (named pipe, file channel fallback), ssh:<alias> (file channel over SSH), "
            "http://host:port or https://host:port; overrides REVIT_MCP_HOST."
        ),
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
