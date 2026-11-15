import asyncio
from unittest.mock import AsyncMock, patch

import pytest

from revit_model_mcp import server
from revit_model_mcp.issue_register import (
    ISSUE_ITEM_CONTRACT,
    ISSUE_REQUIRED_TEXT_FIELDS,
    SEVERITIES,
)


@pytest.mark.parametrize(
    ("item", "first_sentence"),
    [
        (
            {"title": "Review", "finding": "Check", "severity": "major"},
            "issues[0].category is required and must be a non-blank string.",
        ),
        (
            {"title": "Review", "category": "QA", "severity": "major"},
            "issues[0].finding is required and must be a non-blank string.",
        ),
        (
            {"title": "Review", "category": "QA", "finding": "Check", "severity": "medium"},
            "issues[0].severity must be critical, major, minor or info.",
        ),
    ],
)
def test_first_refusal_teaches_a_complete_accepted_item(tmp_path, item, first_sentence):
    arguments = {"output_path": str(tmp_path / "register.xlsx"), "project": {}, "issues": [item]}
    with pytest.raises(server.ToolError) as caught:
        asyncio.run(server.mcp.call_tool("revit_issue_register", arguments))
    message = str(caught.value)
    assert message == (
        f"Error executing tool revit_issue_register: {first_sentence} {ISSUE_ITEM_CONTRACT}"
    )
    assert not (tmp_path / "register.xlsx").exists()

    # Build the corrected item from the refusal, not from a separately known field list.
    sentence = message.split(". ", 1)[1]
    assert sentence.startswith("Each issue needs ") and sentence.endswith(").")
    field_text, severity_text = sentence[len("Each issue needs ") : -2].split(" (")
    fields = field_text.replace(" and ", ", ").split(", ")
    severities = severity_text.replace(" or ", ", ").split(", ")
    assert fields == list(ISSUE_REQUIRED_TEXT_FIELDS)
    assert severities == list(SEVERITIES)
    corrected = dict.fromkeys(fields, "Example")
    corrected[fields[-1]] = severities[0]
    arguments["issues"] = [corrected]
    with patch.object(server.channel, "execute", new=AsyncMock()) as execute:
        result = asyncio.run(server.mcp.call_tool("revit_issue_register", arguments))
    assert not result.is_error
    assert result.structured_content["issueCount"] == 1
    assert (tmp_path / "register.xlsx").is_file()
    execute.assert_not_awaited()


@pytest.mark.parametrize("field", ISSUE_REQUIRED_TEXT_FIELDS)
@pytest.mark.parametrize("value", [None, "", " \t\n"])
def test_missing_or_blank_required_text_includes_complete_contract(tmp_path, field, value):
    item = {"title": "Review", "category": "QA", "finding": "Check", "severity": "major"}
    if value is None:
        item.pop(field)
    else:
        item[field] = value
    with pytest.raises(server.ToolError) as caught:
        asyncio.run(server.revit_issue_register(str(tmp_path / "register.xlsx"), {}, [item]))
    assert str(caught.value) == (
        f"issues[0].{field} is required and must be a non-blank string. {ISSUE_ITEM_CONTRACT}"
    )
