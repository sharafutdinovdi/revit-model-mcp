import json

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


def test_malformed_file_is_ignored(tmp_path, monkeypatch):
    folder = tmp_path / "RevitModelMcp"
    folder.mkdir()
    (folder / "policy.json").write_text("{nope", encoding="utf-8")
    monkeypatch.setenv("PROGRAMDATA", str(tmp_path))
    monkeypatch.setattr(policy, "_registry_values", lambda: {})
    assert policy.load_policy() == {}


def test_policy_covers_tag_elements_and_create_schedule(tmp_path, monkeypatch):
    _write(tmp_path, monkeypatch, {"ToolDeny": ["revit_tag_elements", "create-schedule"]})
    assert policy.policy_refusal("tag-elements").startswith(policy.POLICY_REFUSAL_PREFIX)
    assert policy.policy_refusal("revit_create_schedule").startswith(policy.POLICY_REFUSAL_PREFIX)
    assert policy.policy_refusal("create-view") is None
