from __future__ import annotations

import os
from typing import Annotated, Any

from mcp.types import ToolAnnotations
from mcp.server.mcpserver.exceptions import ToolError
from pydantic import Field

from revit_model_mcp.revit_channel import (
    DEFAULT_PICKUP_TIMEOUT_SECONDS, DEFAULT_TIMEOUT_SECONDS,
    ReadJob, RevitChannelError,
)

ElementId = Annotated[int, Field(strict=True, gt=0, le=9223372036854775807)]
ElementIds = list[ElementId]
NonEmptyIds = Annotated[ElementIds, Field(min_length=1)]
Number = Annotated[float, Field(allow_inf_nan=False)]
PositiveLength = Annotated[float, Field(gt=0, allow_inf_nan=False)]
Name = Annotated[str, Field(min_length=1, pattern=r"\S")]
Point = Annotated[list[Number], Field(min_length=2, max_length=2)]


def millimeters_to_feet(value: float) -> float:
    """Convert a finite millimetre length to Revit internal feet for client calculations."""
    import math

    if not math.isfinite(value):
        raise ValueError("Length must be finite.")
    return value / 304.8


def register_actions(mcp, execute, host_provider) -> None:
    if os.environ.get("REVIT_MCP_ALLOW_WRITE") != "1":
        return

    async def send(command: str, **payload) -> dict[str, Any]:
        try:
            instances = await host_provider().list_revit_instances()
        except RevitChannelError as error:
            raise ToolError(str(error)) from error
        if len(instances) != 1:
            raise ToolError("Actions require exactly one running Revit instance.")
        job = ReadJob(command, {
            "command": command, **payload,
            "targetProcessId": instances[0]["processId"],
        })
        return await execute(job, DEFAULT_TIMEOUT_SECONDS, DEFAULT_PICKUP_TIMEOUT_SECONDS, None)

    def action(function):
        return mcp.tool(annotations=ToolAnnotations(
            readOnlyHint=False,
            destructiveHint=function.__name__ not in {"revit_select", "revit_show", "revit_isolate"},
            idempotentHint=function.__name__ in {"revit_select", "revit_show", "revit_isolate", "revit_set_parameter"},
        ))(function)

    @action
    async def revit_select(element_ids: ElementIds) -> dict[str, Any]:
        """Select element IDs for inspection in Revit; an empty list clears selection; IDs are unitless."""
        return await send("select", elementIds=element_ids)

    @action
    async def revit_show(element_ids: NonEmptyIds, select: bool = True) -> dict[str, Any]:
        """Show elements when locating them visually, optionally selecting them; IDs are unitless and Revit may switch views."""
        return await send("show", elementIds=element_ids, select=select)

    @action
    async def revit_isolate(element_ids: ElementIds, reset: bool = False) -> dict[str, Any]:
        """Temporarily isolate IDs for visual review in the active view, or reset with an empty list; IDs are unitless."""
        if not reset and not element_ids:
            raise ToolError("element_ids must not be empty unless reset is true.")
        return await send("isolate", elementIds=element_ids, reset=reset)

    @action
    async def revit_move(element_ids: NonEmptyIds, dx_mm: Number, dy_mm: Number, dz_mm: Number = 0) -> dict[str, Any]:
        """Move elements when adjusting their position; dx_mm, dy_mm and dz_mm are offsets in millimetres on model axes."""
        return await send("move", elementIds=element_ids, dxMm=dx_mm, dyMm=dy_mm, dzMm=dz_mm)

    @action
    async def revit_place_family(family: Name, type_name: Name | None, x_mm: Number, y_mm: Number, level: Name, rotation_deg: Number = 0) -> dict[str, Any]:
        """Place a loaded unhosted family on a named level for layout; model XY is in millimetres and Z rotation in degrees; null type_name chooses the first type."""
        return await send("place-family", family=family, typeName=type_name, xMm=x_mm, yMm=y_mm, level=level, rotationDeg=rotation_deg)

    @action
    async def revit_create_wall(start_mm: Point, end_mm: Point, level: Name, wall_type: Name | None, height_mm: PositiveLength = 3000) -> dict[str, Any]:
        """Create a straight wall for layout on a named level; model XY endpoints and height are millimetres; null wall_type chooses the first basic type."""
        if start_mm == end_mm:
            raise ToolError("Wall endpoints must differ.")
        return await send("create-wall", startMm=start_mm, endMm=end_mm, level=level, wallType=wall_type, heightMm=height_mm)

    @action
    async def revit_set_parameter(element_id: ElementId, parameter: Name, value: str) -> dict[str, Any]:
        """Set a named instance parameter, falling back to its shared type; use for edits, with length in mm, area in m2 and other doubles in internal units."""
        return await send("set-parameter", elementId=element_id, parameter=parameter, value=value)

    @action
    async def revit_delete(element_ids: NonEmptyIds) -> dict[str, Any]:
        """Delete elements and their Revit dependencies when removal is intended; IDs are unitless and the returned count includes dependents."""
        return await send("delete", elementIds=element_ids)
