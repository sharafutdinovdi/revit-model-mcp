import pytest

from revit_model_mcp.errors import (
    ACTION_FAILED,
    READ_ONLY,
    READ_ONLY_MESSAGE,
    refusal,
    with_error_code,
)


def test_refusal_shape():
    assert refusal("move", READ_ONLY, READ_ONLY_MESSAGE) == {
        "success": False,
        "command": "move",
        "error": "read-only mode",
        "errorCode": "read_only",
    }


@pytest.mark.parametrize(
    "response,code",
    [
        ({"success": False, "error": "read-only mode"}, READ_ONLY),
        ({"success": False, "error": "Cannot move element."}, ACTION_FAILED),
        ({"success": False, "error": "Failed", "errorCode": "future_code"}, "future_code"),
        ({"success": True, "error": "Ignored"}, None),
        ({"success": True, "data": {"needsConfirmation": True}}, None),
        (
            {
                "success": False,
                "partial": True,
                "error": "Pending",
                "message": "Command accepted and running.",
            },
            None,
        ),
        ({"success": False, "partial": True, "correlationId": "abc", "error": "Pending"}, None),
        ({"success": False}, None),
        ({"success": False, "error": None}, None),
        ({"success": False, "error": 42}, None),
    ],
)
def test_with_error_code(response, code):
    original = response.copy()
    result = with_error_code(response)
    assert result is response
    assert result == (original if code is None else {**original, "errorCode": code})
