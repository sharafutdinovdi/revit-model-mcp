"""Machine policy mirror for the server host.

The add-in enforces the policy. The server reads the same values when it runs on the Revit workstation,
so a refused call fails fast and the user sees the same text. Registry values win over policy.json.
"""

from __future__ import annotations

import json
import logging
import os
import sys
from pathlib import Path
from typing import Any

from revit_model_mcp.errors import POLICY_BLOCKED, READ_ONLY, READ_ONLY_MESSAGE, refusal

POLICY_REFUSAL_PREFIX = "Blocked by organization policy:"
_REGISTRY_KEY = r"Software\Policies\RevitModelMcp"


def _registry_values() -> dict[str, Any]:
    if sys.platform != "win32":
        return {}
    import winreg

    values: dict[str, Any] = {}
    try:
        flags = winreg.KEY_READ | winreg.KEY_WOW64_64KEY
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, _REGISTRY_KEY, 0, flags) as key:
            index = 0
            while True:
                try:
                    name, value, _ = winreg.EnumValue(key, index)
                except OSError:
                    break
                values[name] = value
                index += 1
    except FileNotFoundError:
        return {}
    except OSError as error:
        raise PolicyUnreadableError(
            f"the registry policy could not be read ({type(error).__name__})"
        ) from error
    return values


class PolicyUnreadableError(Exception):
    """The policy exists but cannot be read or understood."""


_KNOWN = {
    name.lower(): name
    for name in (
        "ReadOnly",
        "ToolDeny",
        "ToolAllow",
        "ExecuteCode",
        "UpdateMode",
        "TrustedNetworkRoots",
        "LogDir",
        "LogRetentionDays",
        "RedactPaths",
        "HttpEnabled",
    )
}
_FLAGS = {"ReadOnly", "RedactPaths", "HttpEnabled"}
_LISTS = {"ToolDeny", "ToolAllow", "TrustedNetworkRoots"}
_CHOICES = {"ExecuteCode": ("off", "confirm"), "UpdateMode": ("off", "notify", "auto")}
UNREADABLE_KEY = "_Unreadable"
IGNORED_KEY = "_IgnoredKeys"
POLICY_UNREADABLE_MESSAGE = f"{POLICY_REFUSAL_PREFIX} the policy file could not be read."
_logged_problem: str | None = None
_logged_ignored: tuple[str, ...] = ()

# Tool names whose add-in command differs, mirrored from MachinePolicy.CommandAliases.
_COMMAND_ALIASES = {"run-actions": "batch", "batch-start": "batch-supervisor-start"}


def fail_closed(problem: str) -> dict[str, Any]:
    """The state used when the policy cannot be trusted: read-only, no C# code, no updates, no HTTP."""
    return {
        "ReadOnly": True,
        "ExecuteCode": "off",
        "UpdateMode": "off",
        "HttpEnabled": False,
        UNREADABLE_KEY: problem,
    }


def _normalize(raw: dict[str, Any]) -> dict[str, Any]:
    """Validate raw registry or file values. Names are case-insensitive; any problem raises."""
    result: dict[str, Any] = {}
    for key, value in raw.items():
        if not isinstance(key, str) or not key.strip():
            continue
        name = _KNOWN.get(key.strip().lower())
        if name is None:
            # A newer policy file can carry settings this version does not know: report, never enforce.
            result.setdefault(IGNORED_KEY, []).append(key.strip())
            continue
        if name in result:
            raise PolicyUnreadableError(f"{name} is set more than once")
        if name in _FLAGS:
            if isinstance(value, bool):
                result[name] = value
            elif isinstance(value, int) and value in (0, 1):
                result[name] = value == 1
            elif isinstance(value, str) and value.strip().lower() in ("0", "1", "true", "false"):
                result[name] = value.strip().lower() in ("1", "true")
            else:
                raise PolicyUnreadableError(f"{name}: expected true/false or 1/0")
        elif name in _LISTS:
            items = [value] if isinstance(value, str) else value
            if not isinstance(items, (list, tuple)) or not all(isinstance(i, str) for i in items):
                raise PolicyUnreadableError(f"{name}: expected text or a list of text")
            result[name] = [item.strip() for item in items if item.strip()]
        elif name in _CHOICES:
            if not isinstance(value, str):
                raise PolicyUnreadableError(f"{name}: expected text")
            if value.strip():
                choice = value.strip().lower()
                if choice not in _CHOICES[name]:
                    raise PolicyUnreadableError(
                        f"{name}: '{choice}' is not one of {', '.join(_CHOICES[name])}"
                    )
                result[name] = choice
        elif name == "LogDir":
            if not isinstance(value, str):
                raise PolicyUnreadableError("LogDir: expected text")
            if value.strip():
                result[name] = value.strip()
        else:  # LogRetentionDays
            number = (
                int(value)
                if isinstance(value, str) and value.strip().lstrip("-").isdigit()
                else value
            )
            if isinstance(number, bool) or not isinstance(number, int) or not 1 <= number <= 3650:
                raise PolicyUnreadableError("LogRetentionDays: expected a number from 1 to 3650")
            result[name] = number
    return result


def _file_values() -> dict[str, Any]:
    root = os.environ.get("PROGRAMDATA")
    if not root:
        return {}
    path = Path(root) / "RevitModelMcp" / "policy.json"
    try:
        text = path.read_text("utf-8-sig")
    except FileNotFoundError:
        return {}
    except OSError as error:
        raise PolicyUnreadableError(
            f"the policy file could not be opened ({type(error).__name__})"
        ) from error
    except ValueError as error:
        raise PolicyUnreadableError("the policy file is not valid text") from error
    try:
        data = json.loads(text)
    except ValueError as error:
        raise PolicyUnreadableError("the policy file is not valid JSON") from error
    if not isinstance(data, dict):
        raise PolicyUnreadableError("the policy file is not a JSON object")
    return data


def load_policy() -> dict[str, Any]:
    """Merge registry over file per value, with the PascalCase policy names.

    Any read error, parse error, wrong type or unknown enum value returns the fail-closed
    policy (the last good policy is not kept, because it can be looser than the current intent).
    """
    global _logged_problem, _logged_ignored
    try:
        file_values, registry_values = _normalize(_file_values()), _normalize(_registry_values())
        merged = {**file_values, **registry_values}
        ignored = [*file_values.get(IGNORED_KEY, []), *registry_values.get(IGNORED_KEY, [])]
        if ignored:
            merged[IGNORED_KEY] = ignored
    except PolicyUnreadableError as error:
        problem = str(error)
        if problem != _logged_problem:
            _logged_problem = problem
            logging.getLogger(__name__).warning(
                "Organization policy could not be read, so the server is read-only: %s.", problem
            )
        return fail_closed(problem)
    _logged_problem = None
    names = tuple(merged.get(IGNORED_KEY, ()))
    if names != _logged_ignored:
        _logged_ignored = names
        if names:
            logging.getLogger(__name__).warning(
                "Organization policy has settings this version does not know and ignores: %s.",
                ", ".join(names),
            )
    return merged


def normalize_tool(name: str) -> str:
    value = name.strip().lower().replace("_", "-").removeprefix("revit-")
    return _COMMAND_ALIASES.get(value, value)


def policy_read_only(policy: dict[str, Any] | None = None) -> bool:
    return (load_policy() if policy is None else policy).get("ReadOnly") is True


def policy_read_only_refusal(policy: dict[str, Any] | None = None) -> str | None:
    """The text for a read-only policy: the unreadable-policy text when the policy failed closed."""
    values = load_policy() if policy is None else policy
    if UNREADABLE_KEY in values:
        return POLICY_UNREADABLE_MESSAGE
    return (
        f"{POLICY_REFUSAL_PREFIX} the workstation is read-only."
        if values.get("ReadOnly") is True
        else None
    )


def policy_refusal(command: str, policy: dict[str, Any] | None = None) -> str | None:
    """Return the refusal text when ToolDeny, ToolAllow or ExecuteCode blocks a tool or command name."""
    values = load_policy() if policy is None else policy
    name = normalize_tool(command)
    if name in {"ping", "jobs", "jobs/persisted"}:
        return None

    def listed(key: str) -> list[str]:
        raw = values.get(key)
        if isinstance(raw, str):
            raw = [raw]
        return [normalize_tool(item) for item in raw if isinstance(item, str)] if raw else []

    if name in listed("ToolDeny"):
        return f"{POLICY_REFUSAL_PREFIX} the tool '{name}' is denied."
    allowed = listed("ToolAllow")
    if allowed and name not in allowed:
        return f"{POLICY_REFUSAL_PREFIX} the tool '{name}' is not in the allowed list."
    if name == "execute-code" and str(values.get("ExecuteCode", "")).lower() == "off":
        if UNREADABLE_KEY in values:
            return POLICY_UNREADABLE_MESSAGE
        return f"{POLICY_REFUSAL_PREFIX} running C# code is disabled."
    return None


def policy_gate(
    command: str, read_only: bool = False, *, label: str | None = None, mutating: bool = True
) -> dict[str, Any] | None:
    """Refuse a tool or command for the server flag, a read-only or unreadable policy, ToolDeny, ToolAllow or ExecuteCode.

    Returns the refusal response, or None when the call may proceed. ``label`` is the command name
    shown in the response. ``mutating=False`` skips the read-only checks.
    """
    values = load_policy()
    shown = label or command
    if mutating:
        if read_only:
            return refusal(shown, READ_ONLY, READ_ONLY_MESSAGE)
        if UNREADABLE_KEY in values:
            return refusal(shown, POLICY_BLOCKED, POLICY_UNREADABLE_MESSAGE)
        if values.get("ReadOnly") is True:
            return refusal(shown, READ_ONLY, READ_ONLY_MESSAGE)
    if (blocked := policy_refusal(command, values)) is not None:
        return refusal(shown, POLICY_BLOCKED, blocked)
    return None
