"""Toolsets: named groups of tools that decide which tools tools/list exposes.

A toolset limits visibility only. It is not a permission: read-only mode, confirmation
tokens and the add-in's own checks apply to every tool that is listed.
"""

from __future__ import annotations

from typing import Any

TOOLSETS: dict[str, tuple[str, ...]] = {
    "core": (
        "revit_ping",
        "revit_set_read_only",
        "revit_list_instances",
        "revit_documents",
        "revit_document_info",
        "revit_ui_state",
        "revit_jobs",
        "revit_cancel_job",
        "revit_undo_last",
    ),
    "read": (
        "revit_list_catalog",
        "revit_aggregate_elements",
        "revit_query_elements",
        "revit_element_details",
        "revit_list_relations",
        "revit_model_health",
        "revit_model_audit",
        "revit_list_warnings",
        "revit_links_status",
        "revit_shared_coordinates",
        "revit_parameter_fill_check",
        "revit_model_snapshot",
        "revit_schedule_data",
    ),
    "actions": (
        "revit_set_parameter",
        "revit_update_parameters",
        "revit_move",
        "revit_rotate",
        "revit_copy",
        "revit_mirror",
        "revit_change_type",
        "revit_delete",
        "revit_run_actions",
    ),
    "documents": (
        "revit_open_document",
        "revit_activate_document",
        "revit_new_document",
        "revit_close_document",
        "revit_save_document",
        "revit_sync_document",
    ),
    "views-sheets": (
        "revit_list_sheets",
        "revit_list_views",
        "revit_view_info",
        "revit_view_summary",
        "revit_view_elements",
        "revit_view_warnings",
        "revit_capture_elements",
        "revit_export_view",
        "revit_activate_view",
        "revit_close_views",
        "revit_create_view",
        "revit_duplicate_view",
        "revit_apply_view_template",
        "revit_set_view_visibility",
        "revit_override_graphics",
        "revit_tag_elements",
        "revit_create_schedule",
        "revit_select",
        "revit_show",
        "revit_isolate",
        "revit_create_sheet",
        "revit_place_views_on_sheet",
    ),
    "family": (
        "revit_family_audit",
        "revit_edit_families",
        "revit_load_family",
        "revit_place_family",
        "revit_place_families",
    ),
    "links": (
        "revit_compare_link_datums",
        "revit_align_link_datums",
        "revit_remove_links",
    ),
    "cad-mep": (
        "revit_link_cad",
        "revit_walls_from_cad",
        "revit_create_wall",
        "revit_create_mep_run",
    ),
    "export": (
        "revit_export",
        "revit_export_nwc",
        "revit_nwc_settings_check",
        "revit_issue_register",
    ),
    "batch": (
        "revit_batch_start",
        "revit_batch_status",
        "revit_batch_cancel",
        "revit_batch_fetch",
        "revit_build_report",
        "revit_audit_report",
        "revit_process_models",
    ),
    "code": ("revit_execute_code", "revit_api_lookup"),
}

ALL = "all"
ALWAYS_ON = "core"
ENVIRONMENT_VARIABLE = "REVIT_MCP_TOOLSETS"


def parse_toolsets(value: str | None) -> list[str]:
    """Return the selected toolset names, or an empty list when every tool stays listed."""
    names = [name.strip().lower() for name in (value or ALL).split(",") if name.strip()]
    if not names or ALL in names:
        return []
    unknown = [name for name in names if name not in TOOLSETS]
    if unknown:
        raise ValueError(
            f"Unknown toolset {', '.join(unknown)}. Valid toolsets: {ALL}, {', '.join(TOOLSETS)}."
        )
    return list(dict.fromkeys([ALWAYS_ON, *names]))


def select_tools(value: str | None) -> set[str]:
    """Return the names of the tools the toolsets in value expose."""
    names = parse_toolsets(value)
    if not names:
        return {tool for members in TOOLSETS.values() for tool in members}
    return {tool for name in names for tool in TOOLSETS[name]}


def apply_toolsets(server: Any, value: str | None) -> list[str]:
    """Remove every tool outside the selected toolsets; return the names removed."""
    if not parse_toolsets(value):
        return []
    keep = select_tools(value)
    removed = [tool.name for tool in server._tool_manager.list_tools() if tool.name not in keep]
    for name in removed:
        server.remove_tool(name)
    return removed
