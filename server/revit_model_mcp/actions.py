from __future__ import annotations

import os
from functools import partial
from typing import Annotated, Any, Literal

from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import AliasChoices, BaseModel, ConfigDict, Field, create_model, model_validator

from revit_model_mcp.revit_channel import (
    DEFAULT_PICKUP_TIMEOUT_SECONDS,
    DEFAULT_TIMEOUT_SECONDS,
    ReadJob,
    RevitChannelError,
)

ElementId = Annotated[int, Field(strict=True, gt=0, le=9223372036854775807)]
ElementIds = list[ElementId]
NonEmptyIds = Annotated[ElementIds, Field(min_length=1)]
Number = Annotated[float, Field(allow_inf_nan=False)]
PositiveLength = Annotated[float, Field(gt=0, allow_inf_nan=False)]
Name = Annotated[str, Field(min_length=1, pattern=r"\S")]
Point = Annotated[list[Number], Field(min_length=2, max_length=2)]
Document = Annotated[
    str | None,
    Field(
        validation_alias=AliasChoices("document", "targetDocument"),
        description="Case-insensitive substring of the target open document's title or file name. Required to disambiguate when the Revit process has more than one document open; omit only when a single document is open (the active document is used). An unknown or ambiguous reference is rejected before any change.",
    ),
]


_BATCH_FIELDS = {
    "select": {"element_ids": (ElementIds, ...)},
    "isolate": {"element_ids": (ElementIds, ...), "reset": (bool, False)},
    "move": {
        "element_ids": (NonEmptyIds, ...),
        "dx_mm": (Number, ...),
        "dy_mm": (Number, ...),
        "dz_mm": (Number, 0),
    },
    "place_family": {
        "family": (Name, ...),
        "type_name": (Name | None, ...),
        "x_mm": (Number, ...),
        "y_mm": (Number, ...),
        "level": (Name, ...),
        "rotation_deg": (Number, 0),
    },
    "create_wall": {
        "start_mm": (Point, ...),
        "end_mm": (Point, ...),
        "level": (Name, ...),
        "wall_type": (Name | None, ...),
        "height_mm": (PositiveLength, 3000),
    },
    "set_parameter": {
        "element_id": (ElementId, ...),
        "parameter": (Name, ...),
        "value": (str, ...),
    },
    "delete": {"element_ids": (NonEmptyIds, ...)},
}
for _action in ("move", "place_family", "create_wall", "set_parameter", "delete"):
    _BATCH_FIELDS[_action]["dry_run"] = (bool, False)
_BATCH_MODELS = {
    action: create_model(action, __config__=ConfigDict(extra="forbid"), **fields)
    for action, fields in _BATCH_FIELDS.items()
}


class BatchStep(BaseModel):
    model_config = ConfigDict(extra="forbid")
    action: Literal[
        "move", "place_family", "create_wall", "set_parameter", "delete", "select", "isolate"
    ]
    args: dict

    @model_validator(mode="after")
    def validate_args(self):
        self.args = _BATCH_MODELS[self.action].model_validate(self.args).model_dump()
        if self.action == "isolate" and not self.args["reset"] and not self.args["element_ids"]:
            raise ValueError("element_ids must not be empty unless reset is true.")
        if self.action == "create_wall" and self.args["start_mm"] == self.args["end_mm"]:
            raise ValueError("Wall endpoints must differ.")
        return self

    def payload(self) -> dict:
        def camel(key):
            first, *rest = key.split("_")
            return first + "".join(part.title() for part in rest)

        return {
            "command": self.action.replace("_", "-"),
            **{camel(key): value for key, value in self.args.items()},
        }


def env_flag(name: str, default: bool = False) -> bool:
    """Read a boolean environment setting; reject unrecognized values."""
    value = os.environ.get(name)
    if value is None:
        return default
    normalized = value.strip().lower()
    if normalized in {"1", "true", "yes", "on"}:
        return True
    if normalized in {"0", "false", "no", "off"}:
        return False
    raise ValueError(f"{name} must be 1/0, true/false, yes/no or on/off.")


def millimeters_to_feet(value: float) -> float:
    """Convert a finite millimetre length to Revit internal feet for client calculations."""
    import math

    if not math.isfinite(value):
        raise ValueError("Length must be finite.")
    return value / 304.8


async def _send_action(
    execute,
    host_provider,
    command: str,
    *,
    document: str | None = None,
    response_timeout_s: int = DEFAULT_TIMEOUT_SECONDS,
    **payload,
) -> dict[str, Any]:
    try:
        instances = await host_provider().list_revit_instances()
    except RevitChannelError as error:
        raise ToolError(str(error)) from error
    if len(instances) != 1:
        raise ToolError("Actions require exactly one running Revit instance.")
    if document is not None:
        payload["targetDocument"] = document
    job = ReadJob(
        command,
        {
            "command": command,
            **payload,
            "targetProcessId": instances[0]["processId"],
        },
    )
    return await execute(job, response_timeout_s, DEFAULT_PICKUP_TIMEOUT_SECONDS, None)


def register_actions(mcp, execute, host_provider) -> None:
    if not env_flag("REVIT_MCP_ALLOW_WRITE", False):
        return

    send = partial(_send_action, execute, host_provider)

    def action(function):
        title = {
            "revit_select": "Select Elements",
            "revit_show": "Show Elements",
            "revit_isolate": "Isolate Elements",
            "revit_move": "Move Elements",
            "revit_place_family": "Place Family",
            "revit_create_wall": "Create Wall",
            "revit_set_parameter": "Set Parameter",
            "revit_delete": "Delete Elements",
            "revit_batch": "Run Action Batch",
            "revit_align_link_datums": "Align Link Datums",
        }[function.__name__]
        return mcp.tool(
            title=title,
            annotations=ToolAnnotations(
                title=title,
                readOnlyHint=False,
                destructiveHint=function.__name__
                not in {"revit_select", "revit_show", "revit_isolate"},
                idempotentHint=function.__name__
                in {"revit_select", "revit_show", "revit_isolate", "revit_set_parameter"},
            ),
        )(function)

    @action
    async def revit_align_link_datums(
        link: Name,
        kinds: list[str] | None = None,
        name_map: dict[str, str] | None = None,
        prefix: str = "",
        suffix: str = "",
        level_offset_mm: Number = 0,
        reuse_matching: bool = True,
        tolerance_mm: PositiveLength = 0.5,
        create_missing: bool = True,
        level_type: str | None = None,
        grid_type: str | None = None,
        include_pinned: bool = False,
        create_plan_views: bool = False,
        plan_view_type: str | None = None,
        dry_run: bool = False,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 600,
        document: Document = None,
    ) -> dict[str, Any]:
        """Align host grids and levels to a loaded link, with optional creation and rollback preview.

        Geometric alignment does not create a monitor relationship or later Coordination Review warnings.
        """
        return await send(
            "align-link-datums",
            link=link,
            kinds=kinds if kinds is not None else ["grids", "levels"],
            nameMap=name_map or {},
            prefix=prefix,
            suffix=suffix,
            levelOffsetMm=level_offset_mm,
            reuseMatching=reuse_matching,
            toleranceMm=tolerance_mm,
            createMissing=create_missing,
            levelType=level_type,
            gridType=grid_type,
            includePinned=include_pinned,
            createPlanViews=create_plan_views,
            planViewType=plan_view_type,
            dryRun=dry_run,
            response_timeout_s=response_timeout_s,
            document=document,
        )

    @action
    async def revit_select(element_ids: ElementIds, document: Document = None) -> dict[str, Any]:
        """Select element IDs for inspection in Revit; an empty list clears selection; IDs are unitless.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        return await send("select", elementIds=element_ids, document=document)

    @action
    async def revit_show(
        element_ids: NonEmptyIds, select: bool = True, document: Document = None
    ) -> dict[str, Any]:
        """Show elements, optionally selecting them; open a level plan or 3D view when needed. Returns activeView, viewOpened and dialogsSuppressed; IDs are unitless.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        return await send("show", elementIds=element_ids, select=select, document=document)

    @action
    async def revit_isolate(
        element_ids: ElementIds, reset: bool = False, document: Document = None
    ) -> dict[str, Any]:
        """Temporarily isolate IDs for visual review in the active view, or reset with an empty list; IDs are unitless.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        if not reset and not element_ids:
            raise ToolError("element_ids must not be empty unless reset is true.")
        return await send("isolate", elementIds=element_ids, reset=reset, document=document)

    @action
    async def revit_move(
        element_ids: NonEmptyIds,
        dx_mm: Number,
        dy_mm: Number,
        dz_mm: Number = 0,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Move elements when adjusting their position; dx_mm, dy_mm and dz_mm are offsets in millimetres on model axes.
        dry_run executes and rolls back, returning the same verification block without changing the model.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        return await send(
            "move",
            elementIds=element_ids,
            dxMm=dx_mm,
            dyMm=dy_mm,
            dzMm=dz_mm,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_place_family(
        family: Name,
        type_name: Name | None,
        x_mm: Number,
        y_mm: Number,
        level: Name,
        rotation_deg: Number = 0,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Place a loaded unhosted family on a named level for layout.

        family accepts a family name or Family: Type, case-insensitively.
        null type_name uses the embedded type or the first type. Conflicting types
        are rejected. Missing families return similar names with categories.
        Model XY is in millimetres and Z rotation in degrees.
        Use roomCenterMm when placing something inside a room.

        dry_run executes and rolls back, returning the same verification block without changing the model.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        return await send(
            "place-family",
            family=family,
            typeName=type_name,
            xMm=x_mm,
            yMm=y_mm,
            level=level,
            rotationDeg=rotation_deg,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_create_wall(
        start_mm: Point,
        end_mm: Point,
        level: Name,
        wall_type: Name | None,
        height_mm: PositiveLength = 3000,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Create a straight wall for layout on a named level; model XY endpoints and height are millimetres; null wall_type chooses the first basic type.
        dry_run executes and rolls back, returning the same verification block without changing the model.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        if start_mm == end_mm:
            raise ToolError("Wall endpoints must differ.")
        return await send(
            "create-wall",
            startMm=start_mm,
            endMm=end_mm,
            level=level,
            wallType=wall_type,
            heightMm=height_mm,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_set_parameter(
        element_id: ElementId,
        parameter: Name,
        value: str,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Set a named instance parameter, falling back to its shared type; use for edits, with length in mm, area in m2 and other doubles in internal units.
        dry_run executes and rolls back, returning the same verification block without changing the model.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        return await send(
            "set-parameter",
            elementId=element_id,
            parameter=parameter,
            value=value,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_delete(
        element_ids: NonEmptyIds, dry_run: bool = False, document: Document = None
    ) -> dict[str, Any]:
        """Delete elements and their Revit dependencies when removal is intended; IDs are unitless and the returned count includes dependents.
        dry_run executes and rolls back, returning the same verification block without changing the model.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        return await send("delete", elementIds=element_ids, dryRun=dry_run, document=document)

    @action
    async def revit_batch(
        steps: Annotated[list[BatchStep], Field(min_length=1, max_length=50)],
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Execute up to 50 actions with one undo step; roll back the batch on its first failure.

        dry_run executes and rolls back, returning the same verification block without changing the model.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        return await send(
            "batch", steps=[step.payload() for step in steps], dryRun=dry_run, document=document
        )
