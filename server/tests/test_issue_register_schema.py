import asyncio

from revit_model_mcp import server


def test_image_size_descriptions_match_tool_defaults():
    tools = {tool.name: tool for tool in asyncio.run(server.mcp.list_tools())}
    for name, default in (
        ("revit_issue_register", 900),
        ("revit_capture_elements", 1600),
        ("revit_export_view", 1600),
    ):
        pixel_size = tools[name].input_schema["properties"]["pixel_size"]
        assert pixel_size["default"] == default
        assert f"(default {default})" in pixel_size["description"]
