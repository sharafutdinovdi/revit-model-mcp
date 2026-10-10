import json
from pathlib import Path

import pytest

from revit_model_mcp import policy


def _write(tmp_path, monkeypatch, values):
    folder = tmp_path / "RevitModelMcp"
    folder.mkdir()
    (folder / "policy.json").write_text(json.dumps(values), encoding="utf-8")
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))
    monkeypatch.setattr(policy, "_registry_values", lambda: {})


def test_no_policy_file_means_no_restrictions(tmp_path, monkeypatch):
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))
    monkeypatch.setattr(policy, "_registry_values", lambda: {})
    assert policy.policy_read_only() is False
    assert policy.policy_refusal("revit_delete") is None


def test_read_only_from_file(tmp_path, monkeypatch):
    _write(tmp_path, monkeypatch, {"ReadOnly": True})
    assert policy.policy_read_only() is True


def test_registry_wins_over_file(tmp_path, monkeypatch):
    _write(tmp_path, monkeypatch, {"ReadOnly": True})
    monkeypatch.setattr(policy, "_registry_values", lambda: {"ReadOnly": 0})
    assert policy.policy_read_only() is False


def test_tool_deny_allow_and_code(tmp_path, monkeypatch):
    _write(
        tmp_path,
        monkeypatch,
        {"ToolDeny": ["revit_delete"], "ToolAllow": ["delete", "move"], "ExecuteCode": "off"},
    )
    assert policy.policy_refusal("delete").startswith(policy.POLICY_REFUSAL_PREFIX)
    assert policy.policy_refusal("move") is None
    assert policy.policy_refusal("copy").startswith(policy.POLICY_REFUSAL_PREFIX)
    assert policy.policy_refusal("ping") is None


def test_execute_code_off(tmp_path, monkeypatch):
    _write(tmp_path, monkeypatch, {"ExecuteCode": "off"})
    assert "C# code is disabled" in policy.policy_refusal("execute-code")


def _no_registry(monkeypatch):
    monkeypatch.setattr(policy, "_registry_values", lambda: {})


@pytest.mark.parametrize(
    "text",
    [
        "{nope",
        "",
        "[]",
        "null",
        '{"ReadOnly": "yes"}',
        '{"ReadOnly": false, "ToolDeny": 5}',
        '{"ReadOnly": false, "ToolDeny": [1]}',
        '{"ReadOnly": false, "ExecuteCode": "sometimes"}',
        '{"ReadOnly": false, "UpdateMode": "sometimes"}',
        '{"ReadOnly": false, "ReadOnlyy": true}',
        '{"ReadOnly": false, "readonly": true}',
        '{"ReadOnly": false, "LogRetentionDays": 0}',
    ],
)
def test_unreadable_file_fails_closed(tmp_path, monkeypatch, text):
    folder = tmp_path / "RevitModelMcp"
    folder.mkdir()
    (folder / "policy.json").write_text(text, encoding="utf-8")
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))
    _no_registry(monkeypatch)
    loaded = policy.load_policy()
    assert policy.UNREADABLE_KEY in loaded
    assert policy.policy_read_only() is True
    assert policy.policy_refusal("execute-code") == (
        "Blocked by organization policy: the policy file could not be read."
    )
    assert policy.policy_read_only_refusal() == policy.POLICY_UNREADABLE_MESSAGE
    assert loaded["UpdateMode"] == "off" and loaded["HttpEnabled"] is False


def test_unopenable_file_fails_closed(tmp_path, monkeypatch):
    folder = tmp_path / "RevitModelMcp"
    folder.mkdir()
    (folder / "policy.json").write_text('{"ReadOnly": false}', encoding="utf-8")
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))
    _no_registry(monkeypatch)

    def locked(self, *args, **kwargs):
        raise PermissionError("locked by another process")

    monkeypatch.setattr(Path, "read_text", locked)
    assert policy.policy_read_only() is True


def test_unreadable_registry_fails_closed(tmp_path, monkeypatch):
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))

    def broken():
        raise policy.PolicyUnreadableError("the registry policy could not be read")

    monkeypatch.setattr(policy, "_registry_values", broken)
    assert policy.policy_read_only() is True


def test_unreadable_policy_is_logged_once(tmp_path, monkeypatch, caplog):
    folder = tmp_path / "RevitModelMcp"
    folder.mkdir()
    (folder / "policy.json").write_text("{nope", encoding="utf-8")
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))
    _no_registry(monkeypatch)
    monkeypatch.setattr(policy, "_logged_problem", None)
    with caplog.at_level("WARNING"):
        policy.load_policy()
        policy.load_policy()
    assert len([r for r in caplog.records if "could not be read" in r.message]) == 1


def test_camel_case_keys_and_single_string_lists(tmp_path, monkeypatch):
    _write(
        tmp_path,
        monkeypatch,
        {"readOnly": True, "toolDeny": "revit_delete", "executeCode": "OFF", "httpEnabled": 0},
    )
    loaded = policy.load_policy()
    assert loaded == {
        "ReadOnly": True,
        "ToolDeny": ["revit_delete"],
        "ExecuteCode": "off",
        "HttpEnabled": False,
    }
    assert policy.policy_refusal("delete").startswith(policy.POLICY_REFUSAL_PREFIX)


def test_registry_types_are_validated(tmp_path, monkeypatch):
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))
    monkeypatch.setattr(
        policy,
        "_registry_values",
        lambda: {"ReadOnly": 1, "ToolDeny": ["a"], "LogRetentionDays": 7},
    )
    assert policy.load_policy() == {"ReadOnly": True, "ToolDeny": ["a"], "LogRetentionDays": 7}
    monkeypatch.setattr(policy, "_registry_values", lambda: {"ReadOnly": 2})
    assert policy.UNREADABLE_KEY in policy.load_policy()


def test_tool_names_alias_to_commands():
    assert policy.normalize_tool("revit_run_actions") == "batch"
    assert policy.normalize_tool("revit_batch_start") == "batch-supervisor-start"
    deny = {"ToolDeny": ["revit_batch_start", "revit_run_actions"]}
    assert policy.policy_refusal("batch-supervisor-start", deny) is not None
    assert policy.policy_refusal("batch", deny) is not None
    deny = {"ToolDeny": ["batch-supervisor-start", "batch"]}
    assert policy.policy_refusal("revit_batch_start", deny) is not None
    assert policy.policy_refusal("revit_run_actions", deny) is not None


MANIFEST = json.loads(
    (Path(__file__).resolve().parents[2] / "docs" / "tool-manifest.json").read_text("utf-8")
)
TOOL_NAMES = [tool["name"] for tool in MANIFEST["tools"]]


@pytest.mark.parametrize("tool", TOOL_NAMES)
def test_tool_deny_refuses_every_manifest_tool(tool):
    deny = {"ToolDeny": [tool]}
    refused = policy.policy_refusal(tool, deny)
    if tool in {"revit_ping", "revit_jobs"}:
        assert refused is None
        return
    assert refused is not None and refused.startswith(policy.POLICY_REFUSAL_PREFIX)
    sent = {"revit_run_actions": "batch", "revit_batch_start": "batch-supervisor-start"}.get(
        tool, tool.removeprefix("revit_").replace("_", "-")
    )
    assert policy.policy_refusal(sent, deny) is not None
    assert policy.policy_refusal(tool, {"ToolAllow": ["revit_ping"]}) is not None


def test_server_only_tools_enforce_policy(tmp_path, monkeypatch):
    import asyncio

    from mcp.server.mcpserver.exceptions import ToolError

    from revit_model_mcp import server as revit_server

    _write(
        tmp_path,
        monkeypatch,
        {
            "ToolDeny": [
                "revit_batch_start",
                "revit_batch_status",
                "revit_batch_cancel",
                "revit_batch_fetch",
                "revit_build_report",
                "revit_issue_register",
                "revit_list_instances",
                "revit_cancel_job",
                "revit_document_info",
            ]
        },
    )
    tools = {tool.name: tool for tool in asyncio.run(revit_server.mcp.list_tools())}
    arguments = {
        "revit_batch_start": {"folder": "C:/models"},
        "revit_batch_status": {"run_id": "a" * 32},
        "revit_batch_cancel": {"run_id": "a" * 32},
        "revit_batch_fetch": {"run_id": "a" * 32, "output_dir": str(tmp_path)},
        "revit_build_report": {
            "snapshots_dir": str(tmp_path),
            "output_path": str(tmp_path / "r.xlsx"),
        },
        "revit_issue_register": {
            "output_path": str(tmp_path / "i.xlsx"),
            "project": {},
            "issues": [],
        },
        "revit_list_instances": {},
        "revit_cancel_job": {"job_id": "x"},
        "revit_document_info": {},
    }
    assert set(arguments) <= set(tools)
    for name, args in arguments.items():
        try:
            result = asyncio.run(revit_server.mcp.call_tool(name, args))
        except ToolError as error:
            text = str(error)
        else:
            text = json.dumps(
                result.model_dump(mode="json") if hasattr(result, "model_dump") else str(result)
            )
        assert "Blocked by organization policy" in text, name


def test_gate_names_unreadable_policy_for_mutating_calls(tmp_path, monkeypatch):
    folder = tmp_path / "RevitModelMcp"
    folder.mkdir()
    (folder / "policy.json").write_text("{nope", encoding="utf-8")
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))
    _no_registry(monkeypatch)
    gate = policy.policy_gate("revit_delete")
    assert gate["errorCode"] == "policy_blocked"
    assert gate["error"] == policy.POLICY_UNREADABLE_MESSAGE
    assert policy.policy_gate("revit_list_views", mutating=False) is None


def test_policy_covers_tag_elements_and_create_schedule(tmp_path, monkeypatch):
    _write(tmp_path, monkeypatch, {"ToolDeny": ["revit_tag_elements", "create-schedule"]})
    assert policy.policy_refusal("tag-elements").startswith(policy.POLICY_REFUSAL_PREFIX)
    assert policy.policy_refusal("revit_create_schedule").startswith(policy.POLICY_REFUSAL_PREFIX)
    assert policy.policy_refusal("create-view") is None
