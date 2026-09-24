from __future__ import annotations

import os
from pathlib import PureWindowsPath
from typing import Annotated, Any, Literal, Union

from mcp.server.mcpserver.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import AliasChoices, BaseModel, ConfigDict, Field, create_model, model_validator

from revit_model_mcp.revit_channel import (
    DEFAULT_PICKUP_TIMEOUT_SECONDS,
    DEFAULT_TIMEOUT_SECONDS,
    ReadJob,
    RevitChannelError,
    resolve_instance,
    with_client_identity,
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


class SharedParameter(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: Name
    guid: str | None = None
    group: Name
    instance: bool = True


class AddSharedParameters(BaseModel):
    model_config = ConfigDict(extra="forbid")
    op: Literal["add_shared_parameters"]
    parameters: Annotated[list[SharedParameter], Field(min_length=1)]
    replace_family_parameter: bool = False
    shared_parameter_file: str | None = None


class RemoveParameters(BaseModel):
    model_config = ConfigDict(extra="forbid")
    op: Literal["remove_parameters"]
    names: Annotated[list[Name], Field(min_length=1)]
    include_shared: bool = False


class Purge(BaseModel):
    model_config = ConfigDict(extra="forbid")
    op: Literal["purge"]


class SetShared(BaseModel):
    model_config = ConfigDict(extra="forbid")
    op: Literal["set_shared"]
    shared: bool


FamilyOperation = Annotated[
    Union[AddSharedParameters, RemoveParameters, Purge, SetShared], Field(discriminator="op")
]


def family_operation_payload(operation: FamilyOperation) -> dict[str, Any]:
    value = operation.model_dump()
    return {
        "replaceFamilyParameter"
        if key == "replace_family_parameter"
        else "sharedParameterFile"
        if key == "shared_parameter_file"
        else "includeShared"
        if key == "include_shared"
        else key: item
        for key, item in value.items()
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


def redact_model_paths(value: Any) -> Any:
    """Reduce documentPath/path/centralPath strings to file names when REVIT_MCP_REDACT_PATHS is set."""
    if not env_flag("REVIT_MCP_REDACT_PATHS", False):
        return value
    if isinstance(value, dict):
        return {
            key: PureWindowsPath(item).name
            if key in {"documentPath", "path", "centralPath"} and isinstance(item, str)
            else redact_model_paths(item)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [redact_model_paths(item) for item in value]
    return value


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
        selected = resolve_instance(instances, document)
    except RevitChannelError as error:
        raise ToolError(str(error)) from error
    if document is not None:
        payload["targetDocument"] = document
    job = ReadJob(
        command,
        {
            "command": command,
            **payload,
            "targetProcessId": selected["processId"],
        },
    )
    return redact_model_paths(
        await execute(job, response_timeout_s, DEFAULT_PICKUP_TIMEOUT_SECONDS, None)
    )


def register_actions(mcp, execute, host_provider) -> None:
    read_only = env_flag("REVIT_MCP_READ_ONLY", False)

    async def send(
        command: str,
        *,
        document: str | None = None,
        response_timeout_s: int = DEFAULT_TIMEOUT_SECONDS,
        **payload,
    ) -> dict[str, Any]:
        if read_only:
            return {"success": False, "command": command, "error": "read-only mode"}
        return await _send_action(
            execute,
            host_provider,
            command,
            document=document,
            response_timeout_s=response_timeout_s,
            **payload,
        )

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
            "revit_export_nwc": "Export Navisworks NWC",
            "revit_edit_families": "Edit Families",
            "revit_align_link_datums": "Align Link Datums",
            "revit_open_document": "Open Document",
            "revit_close_document": "Close Document",
            "revit_save_document": "Save Document",
            "revit_sync_document": "Synchronize Document",
            "revit_set_view_visibility": "Set View Visibility",
            "revit_remove_links": "Remove Links",
            "revit_undo_last": "Undo Last Action",
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
        )(with_client_identity(function))

    @action
    async def revit_open_document(
        path: Name,
        mode: Literal[
            "detached", "detached_discard_worksets", "local_copy", "read_only_local"
        ] = "detached",
        worksets: Literal["all", "none"] | dict[str, list[Name]] = "all",
        activate: bool = False,
        audit: bool = False,
    ) -> dict[str, Any]:
        """Open a local, UNC or RSN model. Central models default to detached. Cloud paths are unsupported."""
        if isinstance(worksets, dict) and (set(worksets) != {"open"} or not worksets["open"]):
            raise ToolError("worksets must be all, none or {'open': [names]}.")
        if audit:
            raise ToolError("audit must be false.")
        return await send(
            "open-document",
            path=path,
            mode=mode,
            worksets="open" if isinstance(worksets, dict) else worksets,
            worksetsOpen=worksets["open"] if isinstance(worksets, dict) else None,
            activate=activate,
            audit=audit,
        )

    @action
    async def revit_close_document(
        document: Name, save: bool = False, confirm_token: str | None = None
    ) -> dict[str, Any]:
        """Close a background document. Show confirmationText and retry with the token only after explicit chat approval."""
        return await send(
            "close-document", document=document, save=save, confirmToken=confirm_token
        )

    @action
    async def revit_save_document(
        document: Name,
        save_as: str | None = None,
        overwrite: bool = False,
        compact: bool = False,
        confirm_token: str | None = None,
    ) -> dict[str, Any]:
        """Save only after showing confirmationText and receiving explicit chat approval for the token retry."""
        return await send(
            "save-document",
            document=document,
            saveAs=save_as,
            overwrite=overwrite,
            compact=compact,
            confirmToken=confirm_token,
        )

    @action
    async def revit_sync_document(
        document: Name,
        comment: Name,
        relinquish: Literal["all", "none"] | dict[str, bool] = "all",
        compact: bool = False,
        save_local_before: bool = True,
        save_local_after: bool = True,
        confirm_token: str | None = None,
    ) -> dict[str, Any]:
        """Synchronize only after showing confirmationText and receiving explicit chat approval for the token retry."""
        allowed = {
            "borrowed",
            "user_worksets",
            "family_worksets",
            "view_worksets",
            "standard_worksets",
        }
        if isinstance(relinquish, dict) and not set(relinquish) <= allowed:
            raise ToolError("relinquish contains unknown options.")
        return await send(
            "sync-document",
            document=document,
            comment=comment,
            relinquish="custom" if isinstance(relinquish, dict) else relinquish,
            relinquishFlags=relinquish if isinstance(relinquish, dict) else None,
            compact=compact,
            saveLocalBefore=save_local_before,
            saveLocalAfter=save_local_after,
            confirmToken=confirm_token,
        )

    @action
    async def revit_set_view_visibility(
        view: Name,
        hide_categories: list[str | int] | None = None,
        show_categories: list[str | int] | None = None,
        category_classes: dict[str, bool] | None = None,
        hide_categories_by_type: list[str] | None = None,
        worksets: dict[str, list[str]] | None = None,
        filters: list[dict[str, Any]] | None = None,
        template_mode: Literal["detach", "edit_template", "duplicate_view"] | None = None,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Change category, workset and filter visibility in one view; preview with dry_run."""
        return await send(
            "set-view-visibility",
            view=view,
            hideCategories=[str(value) for value in hide_categories] if hide_categories else None,
            showCategories=[str(value) for value in show_categories] if show_categories else None,
            categoryClasses=category_classes,
            hideCategoriesByType=hide_categories_by_type,
            worksets={
                "hideMask": worksets.get("hide_mask", []),
                "showMask": worksets.get("show_mask", []),
            }
            if worksets
            else None,
            filters=filters,
            templateMode=template_mode,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_remove_links(
        links: list[str | int] | Literal["*"],
        kinds: list[Literal["revit", "cad", "point_cloud", "image"]] | None = None,
        include_imported_cad: bool = False,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Delete selected link types and their instances; preview with dry_run."""
        return await send(
            "remove-links",
            links=[str(value) for value in ([links] if isinstance(links, str) else links)],
            kinds=kinds,
            includeImportedCad=include_imported_cad,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_undo_last(document: Document = None) -> dict[str, Any]:
        """Undo the last MCP action in Revit, through Revit's own undo command.

        Allowed only when the target document is active, no command is pending in Revit, and
        Revit's last undo entry is still the one this session recorded. Otherwise the action
        result carries a clear refusal reason instead of running.
        Pass `document` to address a specific open model when several are open; an unknown or ambiguous reference is rejected.
        """
        return await send("undo-last", document=document)

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
    async def revit_export_nwc(
        path: Name,
        scope: Literal["model", "view", "selection"] | None = None,
        settings_xml: Name | None = None,
        view: str | ElementId | None = None,
        element_ids: ElementIds | None = None,
        coordinates: Literal["shared", "internal"] | None = None,
        parameters: Literal["all", "elements", "none"] | None = None,
        export_element_ids: bool | None = None,
        convert_element_properties: bool | None = None,
        export_parts: bool | None = None,
        export_room_as_attribute: bool | None = None,
        export_room_geometry: bool | None = None,
        convert_lights: bool | None = None,
        convert_linked_cad_formats: bool | None = None,
        export_links: bool | None = None,
        export_urls: bool | None = None,
        divide_file_into_levels: bool | None = None,
        find_missing_materials: bool | None = None,
        faceting_factor: Annotated[float, Field(gt=0, le=100, allow_inf_nan=False)] | None = None,
        overwrite: bool = False,
        dry_run: bool = False,
        document: Document = None,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 1800,
    ) -> dict[str, Any]:
        """Export NWC on the Revit workstation. Requires the Navisworks exporter; refused in read-only mode. The file stays on the workstation; settings_xml applies exporter XML values; explicit arguments take precedence."""
        if scope == "view" and not view:
            raise ToolError("view is required for scope=view.")
        if scope == "selection" and not element_ids:
            raise ToolError("element_ids must be non-empty for scope=selection.")
        options = {
            "settingsXml": settings_xml,
            "scope": scope,
            "view": str(view) if view is not None else None,
            "elementIds": element_ids,
            "coordinates": coordinates,
            "parameters": parameters,
            "exportElementIds": export_element_ids,
            "convertElementProperties": convert_element_properties,
            "exportParts": export_parts,
            "exportRoomAsAttribute": export_room_as_attribute,
            "exportRoomGeometry": export_room_geometry,
            "convertLights": convert_lights,
            "convertLinkedCadFormats": convert_linked_cad_formats,
            "exportLinks": export_links,
            "exportUrls": export_urls,
            "divideFileIntoLevels": divide_file_into_levels,
            "findMissingMaterials": find_missing_materials,
            "facetingFactor": faceting_factor,
        }
        return await send(
            "export-nwc",
            path=path,
            **{key: value for key, value in options.items() if value is not None},
            overwrite=overwrite,
            dryRun=dry_run,
            document=document,
            response_timeout_s=response_timeout_s,
        )

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

    @action
    async def revit_edit_families(
        operations: Annotated[list[FamilyOperation], Field(min_length=1)],
        families: Annotated[list[Name] | None, Field(min_length=1, max_length=200)] = None,
        overwrite_parameter_values: bool = False,
        stop_on_error: bool = True,
        dry_run: bool = False,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 1800,
        document: Document = None,
    ) -> dict[str, Any]:
        """Edit an open family in place or named project families in one load cycle each.

        In project mode, pass exact family names or ["*"]. A dry run rolls back all changes.
        Refused in read-only mode.
        """
        if families is not None and "*" in families and families != ["*"]:
            raise ToolError("The '*' family selector must be alone.")
        return await send(
            "edit-families",
            operations=[family_operation_payload(operation) for operation in operations],
            families=families,
            overwriteParameterValues=overwrite_parameter_values,
            stopOnError=stop_on_error,
            dryRun=dry_run,
            response_timeout_s=response_timeout_s,
            document=document,
        )
