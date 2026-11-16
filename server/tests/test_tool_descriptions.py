from __future__ import annotations

import asyncio
import re

from revit_model_mcp import server as revit_server

IMPERATIVE = re.compile(
    r"(^|[.;:]\s+|\n)(Call|Use|Show|Prefer|Always|Never|Do not|Pass|Supply|Start with|Retry|Poll)\b"
)
GATED_PHRASE = re.compile(r"only after|chat approval|before running", re.IGNORECASE)


def _texts() -> list[tuple[str, str]]:
    texts = [("instructions", revit_server.mcp.instructions or "")]
    for tool in asyncio.run(revit_server.mcp.list_tools()):
        texts.append((f"{tool.name} description", tool.description or ""))

        def walk(node, path):
            if isinstance(node, dict):
                description = node.get("description")
                if isinstance(description, str):
                    texts.append((f"{tool.name} {path}", description))
                for key, value in node.items():
                    walk(value, f"{path}.{key}")
            elif isinstance(node, list):
                for index, value in enumerate(node):
                    walk(value, f"{path}[{index}]")

        walk(tool.input_schema, "input")
    return texts


def test_descriptions_state_behavior_without_directing_the_model():
    offenders = [
        f"{where}: {match.group(0).strip()!r}"
        for where, text in _texts()
        for match in [IMPERATIVE.search(text) or GATED_PHRASE.search(text)]
        if match
    ]
    assert not offenders, "Model-directed wording in descriptions:\n" + "\n".join(offenders)


def test_export_description_states_destination_and_job_recovery():
    tools = {tool.name: tool for tool in asyncio.run(revit_server.mcp.list_tools())}
    description = tools["revit_export"].description
    for text in (
        "output_dir on the Revit workstation",
        r"Omitting output_dir uses a dated folder under %LOCALAPPDATA%\RevitModelMcp\exports",
        "dry_run lists the planned names",
        "overwrite=true replaces an existing destination only through the two-step confirm_token",
        "Model MCP provides no separate dialog",
        "status=running and jobId; revit_jobs retrieves its result",
        "Resubmitting repeats the action",
    ):
        assert text in description


def test_export_view_description_states_destination_and_incomplete_results():
    tools = {tool.name: tool for tool in asyncio.run(revit_server.mcp.list_tools())}
    description = tools["revit_export_view"].description
    for text in (
        "output_path on the computer running the AI app",
        "Omitting output_path uses a temporary folder",
        "A destination already present is refused",
        "The active view and model remain unchanged",
        "skipped means incomplete",
        "skippedCount is the total skipped",
    ):
        assert text in description


def test_export_description_states_each_job_fact_once():
    tools = {tool.name: tool for tool in asyncio.run(revit_server.mcp.list_tools())}
    description = tools["revit_export"].description
    assert description.count("revit_jobs") == 1
    assert "retry repeats" not in description
    assert "may commit" in description
    assert "CSV schedules" in description
    assert "CSV exports include all schedules when views is omitted." in description


def test_export_view_description_states_incompleteness_once():
    tools = {tool.name: tool for tool in asyncio.run(revit_server.mcp.list_tools())}
    description = tools["revit_export_view"].description
    assert description.count("incomplete") == 1
    assert description.count("skippedCount") == 1
