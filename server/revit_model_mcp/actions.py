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

from revit_model_mcp.errors import unavailable
from revit_model_mcp.policy import policy_gate
from revit_model_mcp.revit_channel import (
    DEFAULT_TIMEOUT_SECONDS,
    Job,
    RevitChannelError,
    TransportUnavailableError,
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
        description="Title or file name substring of the target open document; needed when several are open.",
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


ScheduleFilterOperator = Literal[
    "equal",
    "not_equal",
    "greater_than",
    "greater_than_or_equal",
    "less_than",
    "less_than_or_equal",
    "contains",
    "not_contains",
    "begins_with",
    "not_begins_with",
    "ends_with",
    "not_ends_with",
    "has_value",
    "has_no_value",
]


class ScheduleFilter(BaseModel):
    model_config = ConfigDict(extra="forbid")
    field: Name
    operator: ScheduleFilterOperator
    value: str | bool | int | float | None = None

    @model_validator(mode="after")
    def value_matches_operator(self):
        needs_value = self.operator not in {"has_value", "has_no_value"}
        if needs_value and self.value is None:
            raise ValueError(f"operator {self.operator} requires a value.")
        if not needs_value and self.value is not None:
            raise ValueError(f"operator {self.operator} takes no value.")
        if isinstance(self.value, float) and not math.isfinite(self.value):
            raise ValueError("value must be finite.")
        return self

    def payload(self) -> dict[str, Any]:
        value = self.value
        if isinstance(value, bool):
            value = "1" if value else "0"
        elif value is not None:
            value = str(value)
        return {"field": self.field, "operator": self.operator, "value": value}


class ScheduleSort(BaseModel):
    model_config = ConfigDict(extra="forbid")
    field: Name
    descending: bool = False
    header: bool = False
    footer: bool = False
    blank_line: bool = False


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


class ActionStep(BaseModel):
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
    output_dir: str | None = None
    options: dict[str, Any] | None = None
    overwrite: bool = False

    @model_validator(mode="after")
    def validate_request(self):
        if self.output_dir is not None:
            _validate_workstation_path(self.output_dir.replace("{model}", "model"), folder=True)
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
            "folder": self.output_dir,
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
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
    **payload,
) -> dict[str, Any]:
    try:
        instances = await host_provider().list_revit_instances()
        if command == "set-read-only" and process_id is None and instances:
            process_id = min(int(instance["processId"]) for instance in instances)
        selected = (
            select_instance(
                instances,
                Job(command, {"targetProcessId": process_id, "targetDocument": document}),
            )
            if process_id is not None
            else resolve_instance(instances, document)
        )
    except TransportUnavailableError as error:
        return redact_model_paths(unavailable(command, str(error)))
    except RevitChannelError as error:
        raise ToolError(redact_model_paths({"error": str(error)})["error"]) from error
    if document is not None:
        payload["targetDocument"] = document
    job = Job(
        command,
        {
            "command": command,
            **payload,
            "targetProcessId": selected["processId"],
        },
    )
    return redact_model_paths(await execute(job, timeout_seconds, None))


def register_actions(mcp, execute, host_provider) -> None:
    async def send(
        command: str,
        *,
        document: str | None = None,
        process_id: int | None = None,
        timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
        **payload,
    ) -> dict[str, Any]:
        if (gate := policy_gate(command, mutating=command != "set-read-only")) is not None:
            return gate
        return await _send_action(
            execute,
            host_provider,
            command,
            document=document,
            process_id=process_id,
            timeout_seconds=timeout_seconds,
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
                + " A long action can return status=running and jobId when the wait budget expires; it may already have changed the model, and revit_jobs returns its result. Resubmitting repeats the action."
            )
        title = {
            "revit_set_read_only": "Set Workstation Read-only",
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
            "revit_tag_elements": "Tag Elements",
            "revit_create_schedule": "Create Schedule",
            "revit_set_parameter": "Set Parameter",
            "revit_delete": "Delete Elements",
            "revit_run_actions": "Run Actions",
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
        session_tool = function.__name__ in {
            "revit_activate_view",
            "revit_activate_document",
            "revit_select",
            "revit_show",
            "revit_isolate",
            "revit_close_views",
        }
        return mcp.tool(
            title=title,
            annotations=ToolAnnotations(
                title=title,
                readOnlyHint=session_tool,
                destructiveHint=not session_tool,
                idempotentHint=function.__name__
                in {"revit_select", "revit_show", "revit_isolate", "revit_set_parameter"},
            ),
        )(with_client_identity(function))

    @action
    async def revit_set_read_only(
        enabled: bool, confirm_token: str | None = None
    ) -> dict[str, Any]:
        """Set the workstation read-only lock for all Revit versions of this Windows user. Locking needs no token. Unlocking requires a user request and explicit confirmation. The first call returns confirmationText and a token; the second call with confirm_token unlocks. Workstation policy cannot be changed."""
        return await send("set-read-only", enabled=enabled, confirmToken=confirm_token)

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
        """Open a local, UNC or RSN model. Central models open detached by default. Cloud paths are unsupported."""
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
            timeout_seconds=1800 if audit else DEFAULT_TIMEOUT_SECONDS,
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
            timeout_seconds=600,
        )

    @action
    async def revit_close_document(
        document: Name,
        save: bool = False,
        confirm_token: str | None = None,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Close a background document. Needs confirm_token from a first call, which changes nothing."""
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
        """Save a document. Needs confirm_token from a first call, which changes nothing."""
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
        """Synchronize a workshared document with central. Needs confirm_token from a first call, which changes nothing."""
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
        """Change category, workset and filter visibility in one view; dry_run previews. Categories accept the Revit UI name, the BuiltInCategory name (OST_StructuralColumns), the English name or an ID."""
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
        """Delete selected link types and their instances; dry_run previews. Cannot be undone in Revit. Needs confirm_token from a first call, which changes nothing."""
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
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Compile and run C# against the live Revit API on the Revit thread. revit_api_lookup supplies reference metadata before writing C# for an unfamiliar type. Accepts a method body or a public static Script class with Execute(ScriptContext ctx). Auto mode owns one transaction and undo entry; transaction="none" is refused by default. Needs confirm_token from a first call, which runs nothing; dry_run needs none and forces transaction="auto"."""
        if dry_run:
            transaction = "auto"
        return await send(
            "execute-code",
            code=code,
            transaction=transaction,
            document=document,
            dryRun=dry_run,
            confirmToken=confirm_token,
            timeout_seconds=timeout_seconds,
        )

    @action
    async def revit_undo_last(document: Document = None) -> dict[str, Any]:
        """Undo the last MCP action through Revit's own undo command. Runs only when the target document is active, no command is pending and Revit's last undo entry is the one this session recorded; otherwise the result carries a refusal reason."""
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
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 600,
        document: Document = None,
    ) -> dict[str, Any]:
        """Align host grids and levels to a loaded link, with optional creation and rollback preview. Does not create a monitor relationship."""
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
            timeout_seconds=timeout_seconds,
            document=document,
        )

    @action
    async def revit_cancel_job(
        job_id: str,
        document: Document = None,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Request cancellation of an action jobId. Process-models stops before the next model and marks unstarted models cancelled; a single running Revit operation finishes, and completed changes stay committed."""
        if (gate := policy_gate("revit_cancel_job", label="jobs")) is not None:
            return gate
        job = (
            Job(
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
        return redact_model_paths(await execute(job, 50, None))

    @action
    async def revit_select(element_ids: ElementIds, document: Document = None) -> dict[str, Any]:
        """Select element IDs for inspection in Revit; an empty list clears the selection."""
        return await send("select", elementIds=element_ids, document=document)

    @action
    async def revit_show(
        element_ids: NonEmptyIds, select: bool = True, document: Document = None
    ) -> dict[str, Any]:
        """Display elements in Revit, optionally selecting them; opens a level plan or 3D view when needed."""
        return await send("show", elementIds=element_ids, select=select, document=document)

    @action
    async def revit_isolate(
        element_ids: ElementIds, reset: bool = False, document: Document = None
    ) -> dict[str, Any]:
        """Temporarily isolate element IDs in the active view; an empty list resets."""
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
        """Move elements by dx_mm, dy_mm and dz_mm on model axes. dry_run rolls back and returns the verification block. Group members, pinned, hosted and constrained elements are reported in skipped.inGroup, skipped.pinned, skipped.hosted and skipped.constrained and are not changed; the call is refused if every element is a group member."""
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
        """Rotate elements about a vertical axis through center_mm, or through their combined bounding box center. dry_run rolls back. Group members, pinned, hosted and constrained elements are reported in skipped.inGroup, skipped.pinned, skipped.hosted and skipped.constrained and are not changed; the call is refused if every element is a group member."""
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
        """Create 1 to 100 successive copies at multiples of the model-axis offset in mm. dry_run rolls back. Group members are reported in skipped.inGroup; the call is refused if every element is a group member."""
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
        """Mirror across a model X or Y parallel line through point_mm; copy keeps originals. dry_run rolls back. Group members, pinned, hosted and constrained elements are reported in skipped.inGroup, skipped.pinned, skipped.hosted and skipped.constrained and are not changed; the call is refused if every element is a group member."""
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
        """Change each element to one compatible type; family resolves duplicate type names. dry_run rolls back. Group members are reported in skipped.inGroup; the call is refused if every element is a group member."""
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
        """Update a parameter on elements matching the same filters as revit_query_elements. Group members are reported in skipped.inGroup instead of failing the call; dry_run predicts this."""
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
        """Place a loaded unhosted family on a named level. family is a name or Family: Type; a null type_name uses the embedded or first type. XY is in mm, rotation in degrees."""
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
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Load workstation .rfa files in one undo entry. Existing families are skipped unless overwrite is true."""
        return await send(
            "load-family",
            paths=paths,
            overwrite=overwrite,
            overwriteParameterValues=overwrite_parameter_values,
            dryRun=dry_run,
            document=document,
            timeout_seconds=timeout_seconds,
        )

    @action
    async def revit_place_families(
        placements: Annotated[list[FamilyPlacement], Field(min_length=1, max_length=2000)]
        | None = None,
        at_rooms: RoomPlacement | None = None,
        load: Annotated[list[Name], Field(min_length=1, max_length=100)] | None = None,
        dry_run: bool = False,
        stop_on_error: bool = True,
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 600,
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
            timeout_seconds=timeout_seconds,
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
        """Create a straight wall on a named level. XY endpoints and height are in mm; a null wall_type uses the first basic type."""
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
        """Create connected duct, pipe, cable tray or conduit segments from model mm points. A missing Z uses the level elevation plus the kind's default offset. Unplaceable elbows are reported as unjoined pairs."""
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
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Link or import a DWG into a plan view on the Revit workstation. Returns CAD layers and extents in mm."""
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
            timeout_seconds=timeout_seconds,
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
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 600,
    ) -> dict[str, Any]:
        """Bridge openings, pair parallel CAD lines and join basic walls in one undo entry. Dimensions are mm. Joins Revit rejects are left out and reported as unjoinedEnds with unjoinedReasons; dry_run reports the same join numbers."""
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
            timeout_seconds=timeout_seconds,
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
        """Create a plan, section, 3D or drafting view. Unbounded 3D views show the whole model, shaded, fine detail. Box coordinates are model mm."""
        try:
            step = ActionStep(
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
        """Duplicate a view, optionally with detailing or as a dependent view."""
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
        sheet: Name | ElementId,
        views: Annotated[list[SheetPlacement], Field(min_length=1)],
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Place views and schedules on a sheet. Coordinates are sheet mm."""
        placements = [{"view": item.view, "xMm": item.x_mm, "yMm": item.y_mm} for item in views]
        return await send(
            "place-views-on-sheet",
            sheet=str(sheet),
            placements=placements,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_tag_elements(
        view: Name | ElementId,
        categories: Annotated[list[Name], Field(min_length=1)] | None = None,
        element_ids: NonEmptyIds | None = None,
        leader: bool = False,
        orientation: Literal["horizontal", "vertical"] = "horizontal",
        tag_type: Name | None = None,
        skip_tagged: bool = True,
        max_elements: MaxElements = 5000,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Tag elements in a view, chosen by category names or by element IDs (exactly one of the two). Returns created tag IDs in createdElementIds; already tagged elements are skipped unless skip_tagged is false, and untaggable inputs are listed in skipped. tag_type matches a tag type name, a family name or "Family: Type"; the default tag type is used otherwise. Room tags need a plan view. dry_run rolls back and returns the tags that would be created."""
        if (categories is None) == (element_ids is None):
            raise ToolError("Supply exactly one of categories or element_ids.")
        return await send(
            "tag-elements",
            view=str(view),
            categories=categories,
            elementIds=element_ids,
            leader=leader,
            orientation=orientation,
            typeName=tag_type,
            skipTagged=skip_tagged,
            maxElements=max_elements,
            dryRun=dry_run,
            document=document,
        )

    @action
    async def revit_create_schedule(
        category: Name,
        fields: Annotated[list[Name], Field(min_length=1, max_length=100)],
        name: Name | None = None,
        filters: Annotated[list[ScheduleFilter], Field(max_length=20)] | None = None,
        sort: Annotated[list[ScheduleSort], Field(max_length=10)] | None = None,
        itemized: bool = True,
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Create a schedule for one category with the named fields in the given order, using Revit UI names such as Mark, Level or Count. filters keep rows by field, operator and value; sort orders and, with header or footer, groups rows. Lengths in mm, areas in m2, volumes in m3. itemized=false collapses identical rows. Returns viewId and the resolved fields; unknown names are listed in skipped. dry_run rolls back."""
        return await send(
            "create-schedule",
            category=category,
            scheduleFields=fields,
            scheduleFilters=[item.payload() for item in filters or []],
            scheduleSort=[
                {
                    "field": item.field,
                    "descending": item.descending,
                    "header": item.header,
                    "footer": item.footer,
                    "blankLine": item.blank_line,
                }
                for item in sort or []
            ],
            name=name,
            itemized=itemized,
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
        """Set one instance or type parameter. parameter_id selects by BuiltInParameter name, shared GUID or ParameterElement ID; otherwise parameter takes a localized UI name, BuiltInParameter name or English alias, and ambiguous matches are refused. Value is a string, integer or number by storage type; lengths in mm, areas in m2. Group members fail with a clear message. dry_run rolls back and returns the verification block."""
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
        element_ids: NonEmptyIds,
        dry_run: bool = False,
        confirm_token: str | None = None,
        document: Document = None,
    ) -> dict[str, Any]:
        """Delete elements and their Revit dependencies; the count includes dependents. dry_run returns the count. Above 500 elements including dependents a confirm_token from a first call is needed; revit_run_actions refuses such deletions."""
        return await send(
            "delete",
            elementIds=element_ids,
            dryRun=dry_run,
            confirmToken=confirm_token,
            document=document,
        )

    @action
    async def revit_export(
        format: Literal["pdf", "dwg", "ifc", "csv"],
        views: list[str | ElementId] | None = None,
        sheets: list[str | ElementId] | None = None,
        sheet_set: str | None = None,
        all_sheets: bool = False,
        output_dir: str | None = None,
        options: dict[str, Any] | None = None,
        overwrite: bool = False,
        document: Document = None,
        dry_run: bool = False,
        confirm_token: str | None = None,
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 1800,
    ) -> dict[str, Any]:
        """Export PDF, DWG, IFC or schedule CSV files on the Revit workstation; dry_run returns planned file names. Replacing existing files (overwrite=true) needs a confirm_token from a first call."""
        return await send(
            "export",
            format=format,
            views=[str(view) for view in views] if views is not None else None,
            sheets=[str(sheet) for sheet in sheets] if sheets is not None else None,
            sheetSet=sheet_set,
            allSheets=all_sheets,
            folder=output_dir,
            options=options or {},
            overwrite=overwrite,
            document=document,
            dryRun=dry_run,
            confirmToken=confirm_token,
            timeout_seconds=timeout_seconds,
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
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 1800,
    ) -> dict[str, Any]:
        """Export NWC on the Revit workstation; needs the Navisworks exporter. settings_xml applies exporter XML values, explicit arguments take precedence. Replacing an existing file (overwrite=true) needs a confirm_token from a first call."""
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
            timeout_seconds=timeout_seconds,
        )

    @action
    async def revit_run_actions(
        steps: Annotated[list[ActionStep], Field(min_length=1, max_length=50)],
        dry_run: bool = False,
        document: Document = None,
    ) -> dict[str, Any]:
        """Execute up to 50 actions with one undo step; the whole batch rolls back on its first failure. dry_run rolls back and returns the verification block."""
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
        steps: Annotated[list[ActionStep] | None, Field(min_length=1, max_length=50)] = None,
        code: ProcessCode | None = None,
        exports: list[ProcessExport] | None = None,
        save: ProcessSave | None = None,
        stop_on_error: bool = False,
        dry_run: bool = False,
        confirm_token: str | None = None,
        timeout_seconds: Annotated[int, Field(ge=30, le=14400)] = 14400,
        process_id: ProcessId = None,
    ) -> dict[str, Any]:
        """Open each model in the interactive session, run steps and C# code, export, save as requested, and close. In-place saves and code need a confirm_token from the preview response. dry_run forces code.transaction="auto" and reports the override in warnings."""
        if (paths is None) == (folder is None):
            raise ToolError("Provide paths or folder, but not both.")
        warnings = []
        if dry_run and code and code.transaction == "none":
            code = code.model_copy(update={"transaction": "auto"})
            warnings.append("dry_run forced transaction=auto")
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
            if paths and (
                (save and save.mode == "output_dir")
                or any(
                    request.output_dir is None or "{model}" in request.output_dir
                    for request in exports or []
                )
            ):
                groups: dict[str, list[str]] = {}
                for path in paths:
                    groups.setdefault(PureWindowsPath(path).stem.casefold(), []).append(path)
                collisions = [group for group in groups.values() if len(group) > 1]
                if collisions:
                    listed = "; ".join(
                        f'"{PureWindowsPath(group[0]).stem}" ({", ".join(group)})'
                        for group in collisions[:5]
                    )
                    if len(collisions) > 5:
                        listed += f", and {len(collisions) - 5} more"
                    raise ValueError(
                        "Sources share a model name, so their outputs would overwrite each other: "
                        + listed
                        + ". Process them in separate calls or with different output folders."
                    )
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
        except ValueError as error:
            raise ToolError(str(error)) from error
        response = await send(
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
            timeout_seconds=timeout_seconds,
        )
        if warnings:
            response.setdefault("warnings", []).extend(warnings)
        return response

    @action
    async def revit_edit_families(
        operations: Annotated[list[FamilyOperation], Field(min_length=1)],
        families: Annotated[list[Name] | None, Field(min_length=1, max_length=200)] = None,
        overwrite_parameter_values: bool = False,
        stop_on_error: bool = True,
        dry_run: bool = False,
        timeout_seconds: Annotated[int, Field(ge=30, le=3600)] = 1800,
        document: Document = None,
    ) -> dict[str, Any]:
        """Edit an open family in place, or named project families (exact names or ["*"]), one load cycle each. A dry run rolls back all changes."""
        if families is not None and "*" in families and families != ["*"]:
            raise ToolError("The '*' family selector must be alone.")
        return await send(
            "edit-families",
            operations=[family_operation_payload(operation) for operation in operations],
            families=families,
            overwriteParameterValues=overwrite_parameter_values,
            stopOnError=stop_on_error,
            dryRun=dry_run,
            timeout_seconds=timeout_seconds,
            document=document,
        )
