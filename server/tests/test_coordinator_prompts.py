from __future__ import annotations

import asyncio
import runpy
from pathlib import Path

import pytest

from revit_model_mcp.server import mcp

ROOT = Path(__file__).resolve().parents[2]
GUIDE = ROOT / "server" / "revit_model_mcp" / "guides" / "coordinator.md"
SKILL = ROOT / "skills" / "revit-model-coordinator" / "SKILL.md"
RESOURCE_URI = "revit://guides/coordinator"
PROMPT_TOOLS = {
    "model_overview": {
        "revit_document_info",
        "revit_model_health",
        "revit_list_warnings",
        "revit_links_status",
        "revit_shared_coordinates",
        "revit_family_audit",
    },
    "pre_issue_check": {
        "revit_document_info",
        "revit_model_health",
        "revit_list_warnings",
        "revit_links_status",
        "revit_shared_coordinates",
        "revit_parameter_fill_check",
        "revit_family_audit",
        "revit_list_catalog",
        "revit_aggregate_elements",
        "revit_query_elements",
    },
    "warnings_triage": {"revit_model_health", "revit_list_warnings"},
    "parameter_fill_report": {"revit_list_catalog", "revit_parameter_fill_check"},
}
ACTION_TOOLS = {
    "revit_select",
    "revit_show",
    "revit_isolate",
    "revit_move",
    "revit_place_family",
    "revit_create_wall",
    "revit_set_parameter",
    "revit_delete",
    "revit_batch",
    "revit_export_nwc",
    "revit_edit_families",
    "revit_align_link_datums",
    "revit_open_document",
    "revit_close_document",
    "revit_save_document",
    "revit_sync_document",
    "revit_set_view_visibility",
    "revit_remove_links",
    "revit_undo_last",
}


def prompt_text(name: str, arguments: dict[str, str] | None = None) -> str:
    result = asyncio.run(mcp.get_prompt(name, arguments))
    return "\n".join(message.content.text for message in result.messages)


def test_coordinator_prompts_are_registered_with_expected_arguments():
    prompts = {prompt.name: prompt for prompt in asyncio.run(mcp.list_prompts())}
    assert PROMPT_TOOLS.keys() <= prompts.keys()
    for name in ("model_overview", "pre_issue_check", "warnings_triage"):
        assert not prompts[name].arguments
    assert {argument.name for argument in prompts["parameter_fill_report"].arguments} == {
        "categories",
        "parameters",
    }
    assert all(argument.required for argument in prompts["parameter_fill_report"].arguments)


@pytest.mark.parametrize("name", list(PROMPT_TOOLS))
def test_coordinator_prompts_render_read_only_workflows(name: str):
    arguments = None
    if name == "parameter_fill_report":
        arguments = {"categories": "Walls, Doors", "parameters": "Mark, Comments"}
    rendered = prompt_text(name, arguments)
    assert RESOURCE_URI in rendered
    assert "read-only" in rendered.lower()
    assert "Do not call action tools" in rendered
    assert all(tool in rendered for tool in PROMPT_TOOLS[name])
    assert not any(tool in rendered for tool in ACTION_TOOLS)
    if arguments:
        assert all(value in rendered for value in arguments.values())


def test_coordinator_guide_resource_matches_source():
    resources = asyncio.run(mcp.list_resources())
    assert any(
        str(resource.uri) == RESOURCE_URI and resource.mime_type == "text/markdown"
        for resource in resources
    )
    contents = asyncio.run(mcp.read_resource(RESOURCE_URI))
    assert len(contents) == 1
    assert contents[0].mime_type == "text/markdown"
    assert contents[0].content == GUIDE.read_text(encoding="utf-8")


def test_generated_skill_matches_guide_and_has_frontmatter():
    render_skill = runpy.run_path(str(ROOT / "build" / "generate_coordinator_skill.py"))[
        "render_skill"
    ]
    content = SKILL.read_text(encoding="utf-8")
    assert content == render_skill(GUIDE.read_text(encoding="utf-8"))
    parts = content.split("---\n", 2)
    assert len(parts) == 3 and parts[0] == ""
    metadata = dict(line.split(": ", 1) for line in parts[1].strip().splitlines())
    assert metadata == {
        "name": "revit-model-coordinator",
        "description": (
            "Use for Revit model review, coordination, or model audit with the "
            "revit-model MCP server."
        ),
    }
