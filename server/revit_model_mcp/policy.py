"""Machine policy mirror for the server host.

The add-in enforces the policy. The server reads the same values when it runs on the Revit workstation,
so a refused call fails fast and the user sees the same text. Registry values win over policy.json.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any

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
    except OSError:
        return {}
    return values


def _file_values() -> dict[str, Any]:
    root = os.environ.get("PROGRAMDATA")
    if not root:
        return {}
    try:
        data = json.loads((Path(root) / "RevitModelMcp" / "policy.json").read_text("utf-8-sig"))
    except (OSError, ValueError):
        return {}
    return data if isinstance(data, dict) else {}


def load_policy() -> dict[str, Any]:
    """Merge registry over file per value. Names are the PascalCase policy names."""
    return {**_file_values(), **_registry_values()}


def normalize_tool(name: str) -> str:
    value = name.strip().lower().replace("_", "-")
    return value.removeprefix("revit-")


def policy_read_only(policy: dict[str, Any] | None = None) -> bool:
    value = (load_policy() if policy is None else policy).get("ReadOnly")
    return value is True or (isinstance(value, int) and not isinstance(value, bool) and value != 0)


def policy_refusal(command: str, policy: dict[str, Any] | None = None) -> str | None:
    """Return the refusal text when ToolDeny, ToolAllow or ExecuteCode blocks the command."""
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
        return f"{POLICY_REFUSAL_PREFIX} running C# code is disabled."
    return None
