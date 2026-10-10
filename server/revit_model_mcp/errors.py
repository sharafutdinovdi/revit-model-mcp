"""Machine-readable codes for action failures and refusals."""

READ_ONLY = "read_only"
"""The server or workstation refused an action in read-only mode."""
POLICY_BLOCKED = "policy_blocked"
"""The add-in refused the call because of the machine policy set by an administrator."""
POLICY_BLOCKED_PREFIX = "Blocked by organization policy:"
POLICY_READ_ONLY_MESSAGE = f"{POLICY_BLOCKED_PREFIX} the workstation is read-only."
ACTION_FAILED = "action_failed"
"""The add-in tried an action and returned an error."""
READ_ONLY_MESSAGE = "read-only mode"


def refusal(command: str, code: str, message: str) -> dict:
    return {"success": False, "command": command, "error": message, "errorCode": code}


def with_error_code(response: dict) -> dict:
    from revit_model_mcp.revit_channel import _is_intermediate_response

    if (
        response.get("success") is False
        and isinstance(response.get("error"), str)
        and "errorCode" not in response
        and not _is_intermediate_response(response)
    ):
        error = response["error"]
        if error == READ_ONLY_MESSAGE:
            response["errorCode"] = READ_ONLY
        elif error.startswith(POLICY_BLOCKED_PREFIX):
            response["errorCode"] = POLICY_BLOCKED
        else:
            response["errorCode"] = ACTION_FAILED
    return response
