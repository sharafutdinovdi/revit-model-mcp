from __future__ import annotations

import os
import re
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
    _optional_text,
    _unique_texts,
    resolve_instance,
    select_instance,
    with_client_identity,
)
from revit_model_mcp.universal_jobs import common_payload

ElementId = Annotated[int, Field(strict=True, gt=0, le=9223372036854775807)]
ElementIds = list[ElementId]
NonEmptyIds = Annotated[ElementIds, Field(min_length=1)]
Number = Annotated[float, Field(allow_inf_nan=False)]
PositiveLength = Annotated[float, Field(gt=0, allow_inf_nan=False)]
Name = Annotated[str, Field(min_length=1, pattern=r"\S")]
ProcessId = Annotated[
    int | None, Field(strict=True, gt=0, validation_alias=AliasChoices("process_id", "processId"))
]
ParameterId = Annotated[
    str,
    Field(
        pattern=r"^(?:[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+|[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}|0*[1-9][0-9]*)$"
    ),
]
ParameterValue = Union[
    Annotated[str, Field(strict=True)],
    Annotated[int, Field(strict=True)],
    Annotated[float, Field(strict=True, allow_inf_nan=False)],
]
Point = Annotated[list[Number], Field(min_length=2, max_length=2)]
CopyCount = Annotated[int, Field(strict=True, ge=1, le=100)]
MaxElements = Annotated[int, Field(strict=True, ge=1, le=20000)]


class UpdateFilters(BaseModel):
    model_config = ConfigDict(extra="forbid")
    categories: list[str] | None = None
    family: str | None = None
    type_name: str | None = None
    level: str | None = None
    view: str | None = None
    workset: str | None = None
    phase: str | None = None
    area_scheme: str | None = None
    parameter_filters: list[dict[str, Any]] | None = None


def query_filter_payload(filters: UpdateFilters) -> dict[str, Any]:
    payload = common_payload(
        "query-elements",
        **filters.model_dump(),
        optional_text=_optional_text,
        unique_texts=_unique_texts,
    )
    del payload["command"]
    return payload


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
    "rotate": {
        "element_ids": (NonEmptyIds, ...),
        "angle_deg": (Number, ...),
        "center_mm": (Point | None, None),
    },
    "copy": {
        "element_ids": (NonEmptyIds, ...),
        "dx_mm": (Number, ...),
        "dy_mm": (Number, ...),
        "dz_mm": (Number, 0),
        "count": (CopyCount, 1),
    },
    "mirror": {
        "element_ids": (NonEmptyIds, ...),
        "axis": (Literal["x", "y"], ...),
        "point_mm": (Point, ...),
        "copy": (bool, True),
    },
    "change_type": {
        "element_ids": (NonEmptyIds, ...),
        "type_name": (Name, ...),
        "family": (Name | None, None),
    },
    "update_parameters": {
        "filters": (UpdateFilters, ...),
        "parameter": (Name, ...),
        "value": (ParameterValue, ...),
        "parameter_id": (ParameterId | None, None),
        "max_elements": (MaxElements, 5000),
        "include_type_parameters": (bool, False),
    },
    "place_family": {
        "family": (Name, ...),
        "type_name": (Name | None, ...),
        "x_mm": (Number, ...),
        "y_mm": (Number, ...),
        "level": (Name, ...),
        "rotation_deg": (Number, 0),
    },
    "load_family": {
        "paths": (Annotated[list[Name], Field(min_length=1, max_length=100)], ...),
        "overwrite": (bool, False),
        "overwrite_parameter_values": (bool, False),
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
        "parameter_id": (ParameterId | None, None),
        "value": (ParameterValue, ...),
    },
    "delete": {"element_ids": (NonEmptyIds, ...)},
}
for _action in (
    "move",
    "rotate",
    "copy",
    "mirror",
    "change_type",
    "update_parameters",
    "place_family",
    "load_family",
    "create_wall",
    "set_parameter",
    "delete",
):
    _BATCH_FIELDS[_action]["dry_run"] = (bool, False)
_BATCH_MODELS = {
    action: create_model(action, __config__=ConfigDict(extra="forbid"), **fields)
    for action, fields in _BATCH_FIELDS.items()
}


class BatchStep(BaseModel):
    model_config = ConfigDict(extra="forbid")
    action: Literal[
        "move",
        "rotate",
        "copy",
        "mirror",
        "change_type",
        "update_parameters",
        "place_family",
        "load_family",
        "create_wall",
        "set_parameter",
        "delete",
        "select",
        "isolate",
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
            **{
                ("queryFilters" if key == "filters" else camel(key)): (
                    query_filter_payload(UpdateFilters.model_validate(value))
                    if key == "filters"
                    else value
                )
                for key, value in self.args.items()
                if key != "parameter_id" or value is not None
            },
        }


class ProcessOpen(BaseModel):
    model_config = ConfigDict(extra="forbid")
    mode: Literal["detached", "detached_discard_worksets", "local_copy", "read_only_local"] = (
        "detached"
    )
    worksets: Literal["all", "none"] | dict[str, list[Name]] = "all"
    audit: bool = False

    @model_validator(mode="after")
    def validate_worksets(self):
        if isinstance(self.worksets, dict) and (
            len(self.worksets) != 1
            or next(iter(self.worksets)) not in {"open", "close"}
            or not next(iter(self.worksets.values()))
            or any(
                name.lower().startswith("regex:")
                for names in self.worksets.values()
                for name in names
            )
        ):
            raise ValueError("worksets must be all, none, {'open': [names]} or {'close': [names]}.")
        return self

    def payload(self) -> dict[str, Any]:
        return {
            "mode": self.mode,
            "worksets": next(iter(self.worksets))
            if isinstance(self.worksets, dict)
            else self.worksets,
            "worksetsOpen": self.worksets.get("open") if isinstance(self.worksets, dict) else None,
            "worksetsClose": self.worksets.get("close")
            if isinstance(self.worksets, dict)
            else None,
            "audit": self.audit,
        }


class ProcessCode(BaseModel):
    model_config = ConfigDict(extra="forbid")
    code: Annotated[str, Field(min_length=1, max_length=200000)]
    transaction: Literal["auto", "none"] = "auto"


class ProcessExport(BaseModel):
    model_config = ConfigDict(extra="forbid")
    format: Literal["pdf", "dwg", "ifc", "csv"]
    views: list[str | ElementId] | None = None
    sheets: list[str | ElementId] | None = None
    sheet_set: str | None = None
    all_sheets: bool = False
    folder: str | None = None
    options: dict[str, Any] | None = None
    overwrite: bool = False

    @model_validator(mode="after")
    def validate_request(self):
        if self.folder is not None:
            _validate_workstation_path(self.folder.replace("{model}", "model"), folder=True)
        if self.format in {"pdf", "dwg"} and not (
            self.views or self.sheets or self.sheet_set or self.all_sheets
        ):
            raise ValueError("PDF and DWG require a view or sheet target.")
        if self.format in {"ifc", "csv"} and (
            self.sheets is not None or self.sheet_set is not None or self.all_sheets
        ):
            raise ValueError("Sheets are supported only for PDF and DWG.")
        if self.format == "ifc" and self.views is not None and len(self.views) > 1:
            raise ValueError("IFC accepts at most one view.")
        return self

    def payload(self) -> dict[str, Any]:
        return {
            "format": self.format,
            "views": [str(view) for view in self.views] if self.views is not None else None,
            "sheets": [str(sheet) for sheet in self.sheets] if self.sheets is not None else None,
            "sheetSet": self.sheet_set,
            "allSheets": self.all_sheets,
            "folder": self.folder,
            "options": self.options or {},
            "overwrite": self.overwrite,
        }


class ProcessSave(BaseModel):
    model_config = ConfigDict(extra="forbid")
    mode: Literal["none", "output_dir", "in_place"] = "none"
    output_dir: str | None = None
    compact: bool = True
    overwrite: bool = False

    @model_validator(mode="after")
    def validate_output(self):
        if self.mode == "output_dir":
            _validate_workstation_path(self.output_dir, folder=True)
        elif self.output_dir is not None:
            raise ValueError("output_dir requires output_dir mode.")
        return self


def _validate_workstation_path(value: str | None, *, folder: bool = False) -> None:
    if not value or value.upper().startswith("RSN://") and folder:
        raise ValueError("A local or UNC absolute path is required.")
    if value.upper().startswith("RSN://"):
        parts = value[6:].split("/")
        if len(parts) < 3 or any(part in {"", ".", ".."} for part in parts):
            raise ValueError("Invalid RSN model path.")
    else:
        path = PureWindowsPath(value)
        if (
            not path.is_absolute()
            or value.startswith(("\\\\?\\", "\\\\.\\"))
            or ".." in path.parts
            or "://" in value
        ):
            raise ValueError("An absolute local or UNC path without traversal is required.")
    if not folder and not value.lower().endswith(".rvt"):
        raise ValueError("Only .rvt models can be processed.")


class FamilyPlacement(BaseModel):
    model_config = ConfigDict(extra="forbid")
    family: Name
    type_name: Name
    x_mm: Number
    y_mm: Number
    z_mm: Number = 0
    level: Name
    rotation_deg: Number = 0
    host_id: ElementId | None = None
    parameters: dict[Name, ParameterValue] | None = None


class RoomPlacement(BaseModel):
    model_config = ConfigDict(extra="forbid")
    family: Name
    type_name: Name
    level: Name | None = None
    rooms: Annotated[list[Name], Field(min_length=1)] | None = None
    z_mm: Number = 0
    rotation_deg: Number = 0
    parameters: dict[Name, ParameterValue] | None = None


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


_WINDOWS_PATH = re.compile(
    r"(?<!\w)(?:[A-Za-z]:[\\/]|(?:\\\\|(?<!:)//)[^\\/\s\"'`,;:!?()<>|]+[\\/]"
    r"(?:[^\\/\s\"'`,;:!?()<>|]+(?: [^\\/\s\"'`,;:!?()<>|]+)*)[\\/])"
    r"(?:(?>[^\\/\s\"'`,;:!?()<>|]+(?: [^\\/\s\"'`,;:!?()<>|]+)*)[\\/])*"
    r"(?:[^\\/\s\"'`,;:!?()<>|]+(?: [^\\/\s\"'`,;:!?()<>|]+)*?\.[A-Za-z0-9]{1,10}\b"
    r"|[^\\/\s\"'`,;:!?()<>|]*[^\\/\s\"'`,;:!?()<>|.])"
)
_TEXT_FIELDS = {
    "confirmationText",
    "summary",
    "error",
    "message",
    "warning",
    "warnings",
    "reason",
    "stackTrace",
    "dialogsDismissed",
    "log",
    "returnValue",
}


def redact_model_paths(value: Any) -> Any:
    """Reduce Windows paths in response path and message fields to file names."""
    if not env_flag("REVIT_MCP_REDACT_PATHS", False):
        return value

    def scrub(item: Any, text_field: bool = False) -> Any:
        if isinstance(item, dict):
            return {
                key: PureWindowsPath(nested).name
                if key in {"documentPath", "path", "centralPath", "folder", "saved"}
                and isinstance(nested, str)
                else scrub(nested, key in _TEXT_FIELDS)
                for key, nested in item.items()
            }
        if isinstance(item, list):
            return [scrub(nested, text_field) for nested in item]
        if text_field and isinstance(item, str):
            return _WINDOWS_PATH.sub(lambda match: PureWindowsPath(match.group()).name, item)
        return item

    return scrub(value)


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
    process_id: int | None = None,
    response_timeout_s: int = DEFAULT_TIMEOUT_SECONDS,
    **payload,
) -> dict[str, Any]:
    try:
        instances = await host_provider().list_revit_instances()
        selected = (
            select_instance(
                instances,
                ReadJob(command, {"targetProcessId": process_id, "targetDocument": document}),
            )
            if process_id is not None
            else resolve_instance(instances, document)
        )
    except RevitChannelError as error:
        raise ToolError(redact_model_paths({"error": str(error)})["error"]) from error
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
        process_id: int | None = None,
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
            process_id=process_id,
            response_timeout_s=response_timeout_s,
            **payload,
        )

    def action(function):
        title = {
            "revit_select": "Select Elements",
            "revit_show": "Show Elements",
            "revit_isolate": "Isolate Elements",
            "revit_move": "Move Elements",
            "revit_rotate": "Rotate Elements",
            "revit_copy": "Copy Elements",
            "revit_mirror": "Mirror Elements",
            "revit_change_type": "Change Element Type",
            "revit_update_parameters": "Update Parameters",
            "revit_place_family": "Place Family",
            "revit_load_family": "Load Families",
            "revit_place_families": "Place Families",
            "revit_create_wall": "Create Wall",
            "revit_set_parameter": "Set Parameter",
            "revit_delete": "Delete Elements",
            "revit_batch": "Run Action Batch",
            "revit_process_models": "Process Many Models",
            "revit_export_nwc": "Export Navisworks NWC",
            "revit_export": "Export Model Files",
            "revit_edit_families": "Edit Families",
            "revit_align_link_datums": "Align Link Datums",
            "revit_open_document": "Open Document",
            "revit_activate_document": "Activate Document",
            "revit_activate_view": "Activate View",
            "revit_close_views": "Close Views",
            "revit_new_document": "New Document",
            "revit_close_document": "Close Document",
            "revit_save_document": "Save Document",
            "revit_sync_document": "Synchronize Document",
            "revit_set_view_visibility": "Set View Visibility",
            "revit_remove_links": "Remove Links",
            "revit_undo_last": "Undo Last Action",
            "revit_execute_code": "Execute C# Code",
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
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Open a local, UNC or RSN model. Central models default to detached. Cloud paths are unsupported."""
        if isinstance(worksets, dict) and (
            len(worksets) != 1
            or next(iter(worksets)) not in {"open", "close"}
            or not next(iter(worksets.values()))
        ):
            raise ToolError("worksets must be all, none, {'open': [names]} or {'close': [names]}.")
        return await send(
            "open-document",
            path=path,
            mode=mode,
            worksets=next(iter(worksets)) if isinstance(worksets, dict) else worksets,
            worksetsOpen=worksets.get("open") if isinstance(worksets, dict) else None,
            worksetsClose=worksets.get("close") if isinstance(worksets, dict) else None,
            activate=activate,
            audit=audit,
            process_id=process_id,
            response_timeout_s=1800 if audit else DEFAULT_TIMEOUT_SECONDS,
        )

    @action
    async def revit_activate_document(
        document: Name, process_id: ProcessId = None
    ) -> dict[str, Any]:
        """Activate an already open document by title or path reference."""
        return await send("activate-document", document=document, process_id=process_id)

    @action
    async def revit_activate_view(
        view: Name,
        document: str | None = None,
        activate_document: bool = False,
        view_type: str | None = None,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Activate a non-template view in the selected document."""
        return await send(
            "activate-view",
            view=view,
            document=document,
            activateDocument=activate_document,
            viewType=view_type,
            process_id=process_id,
        )

    @action
    async def revit_close_views(
        views: list[Name] | None = None,
        keep_active: bool = True,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Close open UI views in the active document."""
        return await send("close-views", views=views, keepActive=keep_active, process_id=process_id)

    @action
    async def revit_new_document(
        template: str | None = None,
        kind: Literal["project", "family"] = "project",
        activate: bool = True,
        save_as: str | None = None,
        name: str | None = None,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Create a project or family from a template on the Revit workstation."""
        if kind == "family" and not template:
            raise ToolError("family requires a template.")
        return await send(
            "new-document",
            template=template,
            kind=kind,
            activate=activate,
            saveAs=save_as,
            name=name,
            process_id=process_id,
            response_timeout_s=600,
        )

    @action
    async def revit_close_document(
        document: Name,
        save: bool = False,
        confirm_token: str | None = None,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Close a background document. Show confirmationText and retry with the token only after explicit chat approval."""
        return await send(
            "close-document",
            document=document,
            save=save,
            confirmToken=confirm_token,
            process_id=process_id,
        )

    @action
    async def revit_save_document(
        document: Name,
        save_as: str | None = None,
        overwrite: bool = False,
        compact: bool = False,
        confirm_token: str | None = None,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Save only after showing confirmationText and receiving explicit chat approval for the token retry."""
        return await send(
            "save-document",
            document=document,
            saveAs=save_as,
            overwrite=overwrite,
            compact=compact,
            confirmToken=confirm_token,
            process_id=process_id,
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
        process_id: ProcessId = None,
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
            process_id=process_id,
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
        """Change category, workset and filter visibility in one view; preview with dry_run.
        Categories accept the Revit UI name, the BuiltInCategory name (OST_StructuralColumns), the English name or an ID.
        """
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
    async def revit_execute_code(
        code: Annotated[str, Field(min_length=1, max_length=200000)],
        transaction: Literal["auto", "none"] = "auto",
        document: Document = None,
        dry_run: bool = False,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Compile and run C# against the live Revit API on the Revit thread.

        Use a method body or a public static Script class with Execute(ScriptContext ctx).
        Auto mode owns one transaction and undo entry. None mode allows document lifecycle
        calls and user-owned transactions; dry_run is available only in auto mode.
        """
        if transaction == "none" and dry_run:
            raise ToolError("dry_run requires transaction='auto'.")
        return await send(
            "execute-code",
            code=code,
            transaction=transaction,
            document=document,
            dryRun=dry_run,
            response_timeout_s=response_timeout_s,
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
    async def revit_rotate(
        element_ids: NonEmptyIds,
        angle_deg: Number,
        center_mm: Point | None = None,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Rotate elements about a vertical axis through center_mm, or their combined bounding box center."""
        return await send(
            "rotate",
            elementIds=element_ids,
            angleDeg=angle_deg,
            centerMm=center_mm,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_copy(
        element_ids: NonEmptyIds,
        dx_mm: Number,
        dy_mm: Number,
        dz_mm: Number = 0,
        count: CopyCount = 1,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Create 1 to 100 successive copies at multiples of the model-axis offset in mm."""
        return await send(
            "copy",
            elementIds=element_ids,
            dxMm=dx_mm,
            dyMm=dy_mm,
            dzMm=dz_mm,
            count=count,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_mirror(
        element_ids: NonEmptyIds,
        axis: Literal["x", "y"],
        point_mm: Point,
        copy: bool = True,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Mirror across a model X or Y parallel line through point_mm; copy keeps originals."""
        return await send(
            "mirror",
            elementIds=element_ids,
            axis=axis,
            pointMm=point_mm,
            copy=copy,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_change_type(
        element_ids: NonEmptyIds,
        type_name: Name,
        family: Name | None = None,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Change each element to one compatible type; family resolves duplicate type names."""
        return await send(
            "change-type",
            elementIds=element_ids,
            typeName=type_name,
            family=family,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_update_parameters(
        filters: UpdateFilters,
        parameter: Name,
        value: ParameterValue,
        parameter_id: ParameterId | None = None,
        max_elements: MaxElements = 5000,
        include_type_parameters: bool = False,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Update a parameter on elements matching the same filters as revit_query_elements."""
        return await send(
            "update-parameters",
            queryFilters=query_filter_payload(filters),
            parameter=parameter,
            value=value,
            parameterId=parameter_id,
            maxElements=max_elements,
            includeTypeParameters=include_type_parameters,
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
    async def revit_load_family(
        paths: Annotated[list[Name], Field(min_length=1, max_length=100)],
        overwrite: bool = False,
        overwrite_parameter_values: bool = False,
        dry_run: bool = False,
        document: Document = None,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Load workstation .rfa files in one undo entry. Existing families are skipped unless overwrite is true."""
        return await send(
            "load-family",
            paths=paths,
            overwrite=overwrite,
            overwriteParameterValues=overwrite_parameter_values,
            dryRun=dry_run,
            document=document,
            response_timeout_s=response_timeout_s,
        )

    @action
    async def revit_place_families(
        placements: Annotated[list[FamilyPlacement], Field(min_length=1, max_length=2000)]
        | None = None,
        at_rooms: RoomPlacement | None = None,
        load: Annotated[list[Name], Field(min_length=1, max_length=100)] | None = None,
        dry_run: bool = False,
        stop_on_error: bool = True,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 600,
        document: Document = None,
    ) -> dict[str, Any]:
        """Load optional families and place instances in one transaction and undo entry."""
        if (placements is None) == (at_rooms is None):
            raise ToolError("Exactly one of placements or at_rooms is required.")

        def camel(item):
            return {
                "".join(
                    part.title() if index else part for index, part in enumerate(key.split("_"))
                ): value
                for key, value in item.items()
                if value is not None
            }

        return await send(
            "place-families",
            placements=[camel(item.model_dump()) for item in placements]
            if placements is not None
            else None,
            atRooms=camel(at_rooms.model_dump()) if at_rooms is not None else None,
            load=load,
            dryRun=dry_run,
            stopOnError=stop_on_error,
            document=document,
            response_timeout_s=response_timeout_s,
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
        value: ParameterValue,
        dry_run: bool = False,
        document: Document = None,
        parameter_id: ParameterId | None = None,
    ) -> dict[str, Any]:
        """Set exactly one instance or type parameter. Supply parameter_id to select by BuiltInParameter name, shared GUID or positive ParameterElement ID.
        Without parameter_id, parameter accepts a localized Revit UI name, BuiltInParameter name or supported English alias; ambiguous matches are refused with candidate details.
        Use a JSON string for String, integer for Integer and number for Double. Lengths use mm, areas m2 and other doubles internal units.
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
            **({"parameterId": parameter_id} if parameter_id is not None else {}),
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
    async def revit_export(
        format: Literal["pdf", "dwg", "ifc", "csv"],
        views: list[str | ElementId] | None = None,
        sheets: list[str | ElementId] | None = None,
        sheet_set: str | None = None,
        all_sheets: bool = False,
        folder: str | None = None,
        options: dict[str, Any] | None = None,
        overwrite: bool = False,
        document: Document = None,
        dry_run: bool = False,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 1800,
    ) -> dict[str, Any]:
        """Export PDF, DWG, IFC or schedule CSV files on the Revit workstation. Refused in read-only mode. dry_run returns planned file names."""
        return await send(
            "export",
            format=format,
            views=[str(view) for view in views] if views is not None else None,
            sheets=[str(sheet) for sheet in sheets] if sheets is not None else None,
            sheetSet=sheet_set,
            allSheets=all_sheets,
            folder=folder,
            options=options or {},
            overwrite=overwrite,
            document=document,
            dryRun=dry_run,
            response_timeout_s=response_timeout_s,
        )

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
    async def revit_process_models(
        paths: Annotated[list[str] | None, Field(min_length=1, max_length=500)] = None,
        folder: str | None = None,
        recursive: bool = False,
        pattern: str = "*.rvt",
        open: ProcessOpen | None = None,
        steps: Annotated[list[BatchStep] | None, Field(min_length=1, max_length=50)] = None,
        code: ProcessCode | None = None,
        exports: list[ProcessExport] | None = None,
        save: ProcessSave | None = None,
        stop_on_error: bool = False,
        dry_run: bool = False,
        confirm_token: str | None = None,
        response_timeout_s: Annotated[int, Field(ge=30, le=14400)] = 14400,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Open each model in the interactive session, run steps and C# code, export, save as requested, and close. In-place saves require a confirmation token from the preview response."""
        if (paths is None) == (folder is None):
            raise ToolError("Provide paths or folder, but not both.")
        try:
            for path in paths or []:
                _validate_workstation_path(path)
            if paths and len({path.replace("/", "\\").casefold() for path in paths}) != len(paths):
                raise ValueError("paths must not contain duplicates.")
            if folder is not None:
                _validate_workstation_path(folder, folder=True)
            if (
                not pattern
                or any(character in pattern for character in "\\/:")
                or not pattern.lower().endswith(".rvt")
            ):
                raise ValueError("pattern must be a .rvt file name pattern.")
            if save and save.mode == "output_dir" and paths:
                targets = {
                    str(PureWindowsPath(save.output_dir) / PureWindowsPath(path).name).casefold()
                    for path in paths
                }
                if any(str(PureWindowsPath(path)).casefold() in targets for path in paths):
                    raise ValueError("A save target matches a source model.")
            if (
                save
                and save.mode == "in_place"
                and open
                and open.mode
                in {
                    "local_copy",
                    "read_only_local",
                }
            ):
                raise ValueError("in_place requires opening the source model directly.")
            if (
                save
                and save.mode == "in_place"
                and paths
                and any(path.upper().startswith("RSN://") for path in paths)
            ):
                raise ValueError("in_place requires local or UNC non-workshared files.")
            if dry_run and code and code.transaction == "none":
                raise ValueError("dry_run requires code.transaction=auto.")
        except ValueError as error:
            raise ToolError(str(error)) from error
        return await send(
            "process-models",
            process={
                "paths": paths,
                "folder": folder,
                "recursive": recursive,
                "pattern": pattern,
                "open": open.payload() if open else None,
                "steps": [step.payload() for step in steps] if steps else None,
                "code": code.model_dump() if code else None,
                "exports": [request.payload() for request in exports] if exports else None,
                "save": {
                    "mode": save.mode,
                    "outputDir": save.output_dir,
                    "compact": save.compact,
                    "overwrite": save.overwrite,
                }
                if save
                else None,
                "stopOnError": stop_on_error,
                "dryRun": dry_run,
                "confirmToken": confirm_token,
            },
            process_id=process_id,
            response_timeout_s=response_timeout_s,
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
