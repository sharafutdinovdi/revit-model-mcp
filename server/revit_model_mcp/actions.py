from __future__ import annotations

import inspect
import math
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
MepPoint = Annotated[list[Number], Field(min_length=2, max_length=3)]
MepPoints = Annotated[list[MepPoint], Field(min_length=2, max_length=200)]
CopyCount = Annotated[int, Field(strict=True, ge=1, le=100)]
MaxElements = Annotated[int, Field(strict=True, ge=1, le=20000)]
ViewReferences = Annotated[list[Name | ElementId], Field(min_length=1)]


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


class ViewBox(BaseModel):
    model_config = ConfigDict(extra="forbid")
    min_mm: Annotated[list[Number], Field(min_length=3, max_length=3)]
    max_mm: Annotated[list[Number], Field(min_length=3, max_length=3)]

    @model_validator(mode="after")
    def positive_extents(self):
        if any(low >= high for low, high in zip(self.min_mm, self.max_mm)):
            raise ValueError("box must have positive extents.")
        return self


class SheetPlacement(BaseModel):
    model_config = ConfigDict(extra="forbid")
    view: Name
    x_mm: Number | None = None
    y_mm: Number | None = None

    @model_validator(mode="after")
    def paired_coordinates(self):
        if (self.x_mm is None) != (self.y_mm is None):
            raise ValueError("x_mm and y_mm must be supplied together.")
        return self


_BATCH_FIELDS = {
    "select": {"element_ids": (ElementIds, ...)},
    "isolate": {"element_ids": (ElementIds, ...), "reset": (bool, False)},
    "override_graphics": {
        "element_ids": (NonEmptyIds, ...),
        "color": (Annotated[str, Field(pattern=r"^#[0-9A-Fa-f]{6}$")], "#FF0000"),
        "views": (Literal["active", "all"] | ViewReferences, "active"),
        "halftone_others": (bool, False),
        "line_weight": (Annotated[int, Field(strict=True, ge=1, le=16)] | None, None),
        "fill": (bool, True),
        "transparency": (Annotated[int, Field(strict=True, ge=0, le=100)], 0),
        "reset": (bool, False),
    },
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
        "copy_enabled": (bool, Field(default=True, alias="copy")),
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
    "create_mep_run": {
        "kind": (Literal["duct", "pipe", "cable_tray", "conduit"], ...),
        "points_mm": (MepPoints, ...),
        "level": (Name, ...),
        "type_name": (Name | None, None),
        "system_type": (Name | None, None),
        "width_mm": (PositiveLength | None, None),
        "height_mm": (PositiveLength | None, None),
        "diameter_mm": (PositiveLength | None, None),
        "offset_mm": (Number | None, None),
        "connect_to": (ElementId | None, None),
    },
    "set_parameter": {
        "element_id": (ElementId, ...),
        "parameter": (Name, ...),
        "parameter_id": (ParameterId | None, None),
        "value": (ParameterValue, ...),
    },
    "delete": {"element_ids": (NonEmptyIds, ...)},
    "create_view": {
        "kind": (
            Literal["floor_plan", "ceiling_plan", "structural_plan", "section", "3d", "drafting"],
            ...,
        ),
        "name": (Name | None, None),
        "level": (Name | None, None),
        "view_family_type": (Name | None, None),
        "template": (Name | None, None),
        "scale": (Annotated[int, Field(strict=True, gt=0)] | None, None),
        "box": (ViewBox | None, None),
        "element_ids": (NonEmptyIds | None, None),
        "display_style": (
            Literal["hidden_line", "shaded", "consistent_colors", "realistic"] | None,
            None,
        ),
        "detail_level": (Literal["coarse", "medium", "fine"] | None, None),
    },
    "duplicate_view": {
        "view": (Name, ...),
        "mode": (Literal["duplicate", "with_detailing", "dependent"], "duplicate"),
        "name": (Name | None, None),
    },
    "apply_view_template": {
        "views": (Annotated[list[Name], Field(min_length=1)], ...),
        "template": (Name, ...),
    },
    "create_sheet": {
        "number": (Name, ...),
        "name": (Name, ...),
        "title_block": (Name | None, None),
    },
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
    "create_mep_run",
    "set_parameter",
    "delete",
    "create_view",
    "duplicate_view",
    "apply_view_template",
    "create_sheet",
    "override_graphics",
):
    _BATCH_FIELDS[_action]["dry_run"] = (bool, False)
_BATCH_MODELS = {
    action: create_model(action, __config__=ConfigDict(extra="forbid"), **fields)
    for action, fields in _BATCH_FIELDS.items()
}


def validate_mep_run(
    kind: str,
    points_mm: list[list[float]],
    system_type: str | None = None,
    width_mm: float | None = None,
    height_mm: float | None = None,
    diameter_mm: float | None = None,
) -> None:
    if system_type is not None and kind not in {"duct", "pipe"}:
        raise ValueError("system_type requires duct or pipe.")
    if (width_mm is not None or height_mm is not None) and kind not in {"duct", "cable_tray"}:
        raise ValueError("width_mm and height_mm require duct or cable_tray.")
    if diameter_mm is not None and kind == "cable_tray":
        raise ValueError("diameter_mm is not supported for cable trays.")
    if (
        kind == "duct"
        and diameter_mm is not None
        and (width_mm is not None or height_mm is not None)
    ):
        raise ValueError("Duct diameter cannot be combined with width or height.")
    for first, second in zip(points_mm, points_mm[1:]):
        if len(first) == len(second) and math.dist(first, second) <= 2.54:
            raise ValueError("Consecutive points must be more than 2.54 mm apart.")


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
        "create_mep_run",
        "set_parameter",
        "delete",
        "select",
        "isolate",
        "create_view",
        "duplicate_view",
        "apply_view_template",
        "create_sheet",
        "override_graphics",
    ]
    args: dict

    @model_validator(mode="after")
    def validate_args(self):
        self.args = _BATCH_MODELS[self.action].model_validate(self.args).model_dump(by_alias=True)
        if self.action == "isolate" and not self.args["reset"] and not self.args["element_ids"]:
            raise ValueError("element_ids must not be empty unless reset is true.")
        if self.action == "create_wall" and self.args["start_mm"] == self.args["end_mm"]:
            raise ValueError("Wall endpoints must differ.")
        if self.action == "create_mep_run":
            validate_mep_run(
                self.args["kind"],
                self.args["points_mm"],
                self.args["system_type"],
                self.args["width_mm"],
                self.args["height_mm"],
                self.args["diameter_mm"],
            )
        if self.action == "create_view":
            data = self.args
            if (
                data["kind"] in {"floor_plan", "ceiling_plan", "structural_plan"}
                and not data["level"]
            ):
                raise ValueError("level is required for plans.")
            if data["kind"] == "section" and (data["box"] is None) == (data["element_ids"] is None):
                raise ValueError("Supply exactly one of box or element_ids.")
            if data["box"] is not None and data["element_ids"] is not None:
                raise ValueError("Supply at most one of box or element_ids.")
            if data["kind"] == "3d":
                data["display_style"] = data["display_style"] or "shaded"
                data["detail_level"] = data["detail_level"] or "fine"
            if data["kind"] not in {"section", "3d"} and (
                data["box"] is not None or data["element_ids"] is not None
            ):
                raise ValueError("box and element_ids require section or 3d.")
        return self

    def payload(self) -> dict:
        def camel(key):
            first, *rest = key.split("_")
            return first + "".join(part.title() for part in rest)

        payload = {
            "command": self.action.replace("_", "-"),
            **{
                ("queryFilters" if key == "filters" else camel(key)): (
                    query_filter_payload(UpdateFilters.model_validate(value))
                    if key == "filters"
                    else {"minMm": value["min_mm"], "maxMm": value["max_mm"]}
                    if key == "box" and value is not None
                    else value
                )
                for key, value in self.args.items()
                if key != "parameter_id" or value is not None
            },
        }
        if self.action == "override_graphics":
            views = payload.pop("views")
            payload["viewScope"] = views if isinstance(views, str) else "list"
            if isinstance(views, list):
                payload["views"] = [str(view) for view in views]
        return payload


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
                if key in {"documentPath", "path", "currentPath", "centralPath", "folder", "saved"}
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
        if function.__name__ in {
            "revit_process_models",
            "revit_export",
            "revit_export_nwc",
            "revit_edit_families",
            "revit_open_document",
            "revit_execute_code",
        }:
            function.__doc__ = (
                inspect.cleandoc(function.__doc__ or "")
                + "\n\nLong actions may return status=running and jobId. Call revit_jobs(job_id=jobId) until the original action result is returned. The action may already have changed the model; do not resubmit it."
            )
        title = {
            "revit_cancel_job": "Cancel Action Job",
            "revit_select": "Select Elements",
            "revit_show": "Show Elements",
            "revit_isolate": "Isolate Elements",
            "revit_override_graphics": "Highlight Elements",
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
            "revit_create_mep_run": "Create MEP Run",
            "revit_link_cad": "Link CAD Drawing",
            "revit_walls_from_cad": "Build Walls from CAD",
            "revit_create_view": "Create View",
            "revit_duplicate_view": "Duplicate View",
            "revit_apply_view_template": "Apply View Template",
            "revit_create_sheet": "Create Sheet",
            "revit_place_views_on_sheet": "Place Views on Sheet",
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
        document: Annotated[
            str | None, Field(description="Target document title or path reference.")
        ] = None,
        activate_document: bool = False,
        view_type: str | None = None,
        zoom: Literal["fit", "none"] | NonEmptyIds = "fit",
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Activate a non-template view. Zoom to fit, preserve zoom, or frame element IDs."""
        return await send(
            "activate-view",
            view=view,
            document=document,
            activateDocument=activate_document,
            viewType=view_type,
            zoom="elements" if isinstance(zoom, list) else zoom,
            zoomElementIds=list(dict.fromkeys(zoom)) if isinstance(zoom, list) else None,
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
        confirm_token: str | None = None,
        document: Document = None,
    ) -> dict[str, Any]:
        """Delete selected link types and their instances; preview with dry_run. A real removal cannot be undone in Revit. The first call without confirm_token changes nothing and returns needsConfirmation, confirmationText and confirmToken. Show confirmationText and retry with the same arguments plus confirm_token only after explicit chat approval."""
        return await send(
            "remove-links",
            links=[str(value) for value in ([links] if isinstance(links, str) else links)],
            kinds=kinds,
            includeImportedCad=include_imported_cad,
            dryRun=dry_run,
            confirmToken=confirm_token,
            document=document,
        )

    @action
    async def revit_execute_code(
        code: Annotated[str, Field(min_length=1, max_length=200000)],
        transaction: Literal["auto", "none"] = "auto",
        document: Document = None,
        dry_run: bool = False,
        confirm_token: str | None = None,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Compile and run C# against the live Revit API on the Revit thread.

        Use a method body or a public static Script class with Execute(ScriptContext ctx).
        Auto mode owns one transaction and undo entry. transaction="none" is refused by
        default; use auto. Every call except dry_run first returns needsConfirmation,
        confirmationText (code hash, length, mode, document) and confirmToken without running
        the code. Show it and retry with identical arguments plus confirm_token only after
        explicit chat approval. dry_run needs no token.
        """
        if transaction == "none" and dry_run:
            raise ToolError("dry_run requires transaction='auto'.")
        return await send(
            "execute-code",
            code=code,
            transaction=transaction,
            document=document,
            dryRun=dry_run,
            confirmToken=confirm_token,
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
    async def revit_cancel_job(
        job_id: str,
        document: Document = None,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Request cancellation of an action jobId.

        Process-models stops before the next model, lists every unstarted model in data.models with status cancelled, and returns data.cancelled:true with completed results.
        A single running Revit operation finishes without interruption.
        Poll revit_jobs for the final result; completed changes remain committed.
        """
        if read_only:
            return {"success": False, "command": "jobs", "error": "read-only mode"}
        job = (
            ReadJob(
                "jobs",
                {
                    "command": "jobs",
                    "fetchJobId": job_id,
                    "requestCancellation": True,
                    "waitSeconds": 0,
                },
            )
            .for_document(document)
            .for_process(process_id)
        )
        return redact_model_paths(await execute(job, 50, DEFAULT_PICKUP_TIMEOUT_SECONDS, None))

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
        Members of groups that Revit refuses to change are reported in skipped.inGroup and skipped; dry_run predicts this, and the call is refused if every element is such a member.
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
    async def revit_override_graphics(
        element_ids: NonEmptyIds,
        color: Annotated[str, Field(pattern=r"^#[0-9A-Fa-f]{6}$")] = "#FF0000",
        views: Literal["active", "all"] | ViewReferences = "active",
        halftone_others: bool = False,
        line_weight: Annotated[int, Field(strict=True, ge=1, le=16)] | None = None,
        fill: bool = True,
        transparency: Annotated[int, Field(strict=True, ge=0, le=100)] = 0,
        reset: bool = False,
        document: Document = None,
        dry_run: bool = False,
    ) -> dict[str, Any]:
        """Colour elements in selected model views; reset restores overrides saved in this Revit session."""
        return await send(
            "override-graphics",
            elementIds=element_ids,
            color=color,
            viewScope=views if isinstance(views, str) else "list",
            views=[str(view) for view in views] if isinstance(views, list) else None,
            halftoneOthers=halftone_others,
            lineWeight=line_weight,
            fill=fill,
            transparency=transparency,
            reset=reset,
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
        """Rotate elements about a vertical axis through center_mm, or their combined bounding box center.
        Members of groups that Revit refuses to change are reported in skipped.inGroup and skipped; dry_run predicts this, and the call is refused if every element is such a member.
        """
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
        """Create 1 to 100 successive copies at multiples of the model-axis offset in mm.
        Members of groups that Revit refuses to change are reported in skipped.inGroup and skipped; dry_run predicts this, and the call is refused if every element is such a member.
        """
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
        """Mirror across a model X or Y parallel line through point_mm; copy keeps originals.
        Members of groups that Revit refuses to change are reported in skipped.inGroup and skipped; dry_run predicts this, and the call is refused if every element is such a member.
        """
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
        """Change each element to one compatible type; family resolves duplicate type names.
        Members of groups that Revit refuses to change are reported in skipped.inGroup and skipped; dry_run predicts this, and the call is refused if every element is such a member.
        """
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
        """Update a parameter on elements matching the same filters as revit_query_elements.
        Members of groups that Revit refuses to change are reported in skipped.inGroup instead of failing the call; dry_run predicts this.
        """
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
    async def revit_create_mep_run(
        kind: Literal["duct", "pipe", "cable_tray", "conduit"],
        points_mm: MepPoints,
        level: Name,
        type_name: Name | None = None,
        system_type: Name | None = None,
        width_mm: PositiveLength | None = None,
        height_mm: PositiveLength | None = None,
        diameter_mm: PositiveLength | None = None,
        offset_mm: Number | None = None,
        connect_to: ElementId | None = None,
        document: Document = None,
        dry_run: bool = False,
    ) -> dict[str, Any]:
        """Create connected duct, pipe, cable tray or conduit segments from model millimetre points. Missing Z uses the level elevation plus the kind's default offset. Unplaceable elbows are reported as unjoined pairs."""
        try:
            validate_mep_run(
                kind,
                points_mm,
                system_type,
                width_mm,
                height_mm,
                diameter_mm,
            )
        except ValueError as error:
            raise ToolError(str(error)) from error
        return await send(
            "create-mep-run",
            kind=kind,
            pointsMm=points_mm,
            level=level,
            typeName=type_name,
            systemType=system_type,
            widthMm=width_mm,
            heightMm=height_mm,
            diameterMm=diameter_mm,
            offsetMm=offset_mm,
            connectTo=connect_to,
            document=document,
            dryRun=dry_run,
        )

    @action
    async def revit_link_cad(
        path: Name,
        view: str | ElementId | None = None,
        level: Name | None = None,
        link: bool = True,
        origin: Literal["internal", "shared", "center"] = "internal",
        units: Literal["auto", "mm", "cm", "m", "in", "ft"] = "auto",
        layers: Annotated[list[Name], Field(min_length=1)] | None = None,
        document: Document = None,
        dry_run: bool = False,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Link or import a DWG into a plan view on the Revit workstation. Return CAD layers and extents in mm."""
        return await send(
            "link-cad",
            path=path,
            view=str(view) if view is not None else None,
            level=level,
            cadLink=link,
            origin=origin,
            units=units,
            layers=layers,
            document=document,
            dryRun=dry_run,
            response_timeout_s=response_timeout_s,
        )

    @action
    async def revit_walls_from_cad(
        cad_id: ElementId,
        layers: Annotated[list[Name], Field(min_length=1)],
        level: Name,
        wall_type: Name | None = None,
        height_mm: PositiveLength = 3000,
        min_thickness_mm: PositiveLength = 80,
        max_thickness_mm: PositiveLength = 700,
        min_length_mm: PositiveLength = 300,
        max_gap_mm: Annotated[float, Field(ge=0, allow_inf_nan=False)] = 3000,
        join: bool = True,
        document: Document = None,
        dry_run: bool = False,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Bridge openings, pair parallel CAD lines and join basic walls in one undo entry. Dimensions are mm.
        With join=true joins Revit would reject are left out and reported as unjoinedEnds with unjoinedReasons; dry_run reports the same join numbers.
        """
        if max_thickness_mm < min_thickness_mm:
            raise ToolError("max_thickness_mm must be at least min_thickness_mm.")
        return await send(
            "walls-from-cad",
            cadId=cad_id,
            layers=layers,
            level=level,
            wallType=wall_type,
            heightMm=height_mm,
            minThicknessMm=min_thickness_mm,
            maxThicknessMm=max_thickness_mm,
            minLengthMm=min_length_mm,
            maxGapMm=max_gap_mm,
            join=join,
            document=document,
            dryRun=dry_run,
            response_timeout_s=response_timeout_s,
        )

    @action
    async def revit_create_view(
        kind: Literal["floor_plan", "ceiling_plan", "structural_plan", "section", "3d", "drafting"],
        name: Name | None = None,
        level: Name | None = None,
        view_family_type: Name | None = None,
        template: Name | None = None,
        scale: Annotated[int, Field(strict=True, gt=0)] | None = None,
        box: ViewBox | None = None,
        element_ids: NonEmptyIds | None = None,
        display_style: Literal["hidden_line", "shaded", "consistent_colors", "realistic"]
        | None = None,
        detail_level: Literal["coarse", "medium", "fine"] | None = None,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Create a plan, section, 3D or drafting view. Unbounded 3D views show the whole model with shaded, fine defaults. Box coordinates use model millimetres."""
        try:
            step = BatchStep(
                action="create_view",
                args={
                    "kind": kind,
                    "name": name,
                    "level": level,
                    "view_family_type": view_family_type,
                    "template": template,
                    "scale": scale,
                    "box": box,
                    "element_ids": element_ids,
                    "display_style": display_style,
                    "detail_level": detail_level,
                },
            )
        except ValueError as error:
            raise ToolError(str(error)) from error
        return await send(
            "create-view",
            **{
                key: value
                for key, value in step.payload().items()
                if key not in {"command", "dryRun"}
            },
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_duplicate_view(
        view: Name,
        mode: Literal["duplicate", "with_detailing", "dependent"] = "duplicate",
        name: Name | None = None,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Duplicate a view, optionally including detailing or creating a dependent view."""
        return await send(
            "duplicate-view", view=view, mode=mode, name=name, dryRun=dry_run, document=document
        )

    @action
    async def revit_apply_view_template(
        views: Annotated[list[Name], Field(min_length=1)],
        template: Name,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Apply a matching view template to one or more views."""
        return await send(
            "apply-view-template", views=views, template=template, dryRun=dry_run, document=document
        )

    @action
    async def revit_create_sheet(
        number: Name,
        name: Name,
        title_block: Name | None = None,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Create a sheet with the first loaded or a named title block."""
        return await send(
            "create-sheet",
            number=number,
            name=name,
            titleBlock=title_block,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_place_views_on_sheet(
        sheet: Name,
        views: Annotated[list[SheetPlacement], Field(min_length=1)],
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Place views and schedules on a sheet. Coordinates use sheet millimetres."""
        placements = [{"view": item.view, "xMm": item.x_mm, "yMm": item.y_mm} for item in views]
        return await send(
            "place-views-on-sheet",
            sheet=sheet,
            placements=placements,
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
        Group members that Revit refuses to change fail with a clear message, including in dry_run.
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
        confirm_token: str | None = None,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 1800,
    ) -> dict[str, Any]:
        """Export PDF, DWG, IFC or schedule CSV files on the Revit workstation. Refused in read-only mode. dry_run returns planned file names. With overwrite=true and at least one existing target file, the first call returns needsConfirmation with the files that would be replaced; retry with identical arguments plus confirm_token after explicit approval. No token when no target exists or with dry_run."""
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
            confirmToken=confirm_token,
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
        confirm_token: str | None = None,
        response_timeout_s: Annotated[int, Field(ge=30, le=3600)] = 1800,
    ) -> dict[str, Any]:
        """Export NWC on the Revit workstation. Requires the Navisworks exporter; refused in read-only mode. The file stays on the workstation; settings_xml applies exporter XML values; explicit arguments take precedence. With overwrite=true and an existing path, the first call returns needsConfirmation with the file that would be replaced; retry with identical arguments plus confirm_token after explicit approval. No token when the path does not exist or with dry_run."""
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
            confirmToken=confirm_token,
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
        """Open each model in the interactive session, run steps and C# code, export, save as requested, and close. In-place saves and code require a confirmation token from the preview response."""
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
