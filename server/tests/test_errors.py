import pytest

from revit_model_mcp.errors import (
    ACTION_FAILED,
    POLICY_BLOCKED,
    READ_ONLY,
    READ_ONLY_MESSAGE,
    refusal,
    unavailable,
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
        (
            {
                "success": False,
                "error": "Read-only is on for this Windows user (set from chat at 2026-10-22T12:00:00Z). Turn it off with the Model MCP ribbon button or by asking the assistant.",
            },
            READ_ONLY,
        ),
        (
            {
                "success": False,
                "error": "Read-only is enforced by workstation policy; ask your administrator.",
            },
            POLICY_BLOCKED,
        ),
        (
            {
                "success": False,
                "error": "Blocked by organization policy: the tool 'delete' is denied.",
            },
            POLICY_BLOCKED,
        ),
        (
            {
                "success": False,
                "error": "Blocked by organization policy: the workstation is read-only.",
            },
            POLICY_BLOCKED,
        ),
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


def test_unavailable_shape():
    assert unavailable("ping", "offline") == {
        "success": False,
        "command": "ping",
        "error": "offline",
        "errorCode": "transport_unavailable",
        "retryable": True,
    }
