import asyncio
import base64
import errno
import json
import os
import sys
from unittest.mock import AsyncMock

import pytest

from revit_model_mcp import atomic_write
from revit_model_mcp.artifact_download import preflight_artifact_target
from revit_model_mcp.revit_channel import ResponseParseError, RevitChannelError
from revit_model_mcp.ssh_host import SshPowerShellHost

PNG = b"\x89PNG\r\n\x1a\ncontent"
RESPONSE_NAME = "response_20260916_120000_000_capture-view.json"
ARTIFACT_NAME = "view.png"
CONTENT = json.dumps({"success": True, "data": {"fileName": ARTIFACT_NAME}})


@pytest.fixture
def host():
    host = SshPowerShellHost()
    package = {
        "response": base64.b64encode(CONTENT.encode()).decode(),
        "artifactName": ARTIFACT_NAME,
        "artifact": base64.b64encode(PNG).decode(),
    }
    host._run = AsyncMock(return_value=json.dumps(package))
    return host


def finish(host, target):
    return asyncio.run(
        host.finish_job(RESPONSE_NAME, [RESPONSE_NAME, "", RESPONSE_NAME], True, str(target))
    )


def test_existing_target_prevents_remote_read(tmp_path, host):
    target = tmp_path / "capture.png"
    target.write_bytes(b"original")
    with pytest.raises(RevitChannelError, match="already exists"):
        finish(host, target)
    host._run.assert_not_awaited()
    assert target.read_bytes() == b"original"
    assert list(tmp_path.iterdir()) == [target]


@pytest.mark.skipif(
    sys.platform == "win32" or (hasattr(os, "geteuid") and os.geteuid() == 0),
    reason="Directory permission bits are not enforced here",
)
def test_unwritable_directory_prevents_remote_read(tmp_path, host):
    tmp_path.chmod(0o555)
    try:
        with pytest.raises(RevitChannelError, match="Cannot save image"):
            finish(host, tmp_path / "capture.png")
        host._run.assert_not_awaited()
        assert list(tmp_path.iterdir()) == []
    finally:
        tmp_path.chmod(0o755)


def test_save_failure_preserves_remote_capture(tmp_path, host, monkeypatch):
    target = tmp_path / "capture.png"

    def full_disk(descriptor):
        raise OSError(errno.ENOSPC, "No space left on device")

    monkeypatch.setattr(atomic_write.os, "fsync", full_disk)
    with pytest.raises(RevitChannelError, match="Cannot save image.*No space left on device"):
        finish(host, target)
    host._run.assert_awaited_once()
    assert "Remove-Item" not in host._run.await_args.args[0]
    assert not target.exists()
    assert list(tmp_path.iterdir()) == []


def test_success_deletes_remote_files_after_save(tmp_path, host):
    target = tmp_path / "capture.png"

    async def run(script):
        if "Remove-Item" in script:
            assert target.read_bytes() == PNG
            return ""
        return host._run.return_value

    host._run.side_effect = run
    assert finish(host, target) == (CONTENT, str(target))
    assert target.read_bytes() == PNG
    assert list(tmp_path.iterdir()) == [target]
    assert host._run.await_count == 2
    read, delete = [call.args[0] for call in host._run.await_args_list]
    assert "Remove-Item" not in read
    assert "finally" not in read.split("$directory =", 1)[1]
    assert "Remove-Item" in delete
    assert delete.count(RESPONSE_NAME) == 1
    assert ARTIFACT_NAME in delete
    assert "''" not in delete


def test_delete_failure_returns_saved_image(tmp_path, host, caplog):
    target = tmp_path / "capture.png"
    host._run.side_effect = [host._run.return_value, RevitChannelError("cleanup failed")]
    assert finish(host, target) == (CONTENT, str(target))
    assert target.read_bytes() == PNG
    assert host._run.await_count == 2
    assert "remote capture cleanup failed" in caplog.text


@pytest.mark.parametrize("save_to", [None, ""])
def test_no_destination_preflight(save_to):
    preflight_artifact_target(save_to)


def test_invalid_extension_prevents_remote_read(tmp_path, host):
    with pytest.raises(RevitChannelError, match="must have a .png extension"):
        finish(host, tmp_path / "capture.xlsx")
    host._run.assert_not_awaited()
    assert list(tmp_path.iterdir()) == []


def test_dangling_symlink_prevents_remote_read(tmp_path, host):
    target = tmp_path / "capture.png"
    target.symlink_to(tmp_path / "missing.png")
    with pytest.raises(RevitChannelError, match="already exists"):
        finish(host, target)
    host._run.assert_not_awaited()
    assert target.is_symlink()
    assert list(tmp_path.iterdir()) == [target]


def test_preflight_creates_parent_without_leaving_files(tmp_path):
    target = tmp_path / "nested" / "capture.png"
    preflight_artifact_target(str(target))
    assert target.parent.is_dir()
    assert list(target.parent.iterdir()) == []


@pytest.mark.parametrize("artifact", ["invalid base64!", base64.b64encode(b"not PNG").decode()])
def test_invalid_image_preserves_remote_capture(tmp_path, host, artifact):
    package = json.loads(host._run.return_value)
    package["artifact"] = artifact
    host._run.return_value = json.dumps(package)
    with pytest.raises((RevitChannelError, ResponseParseError)):
        finish(host, tmp_path / "capture.png")
    host._run.assert_awaited_once()
    assert "Remove-Item" not in host._run.await_args.args[0]
    assert list(tmp_path.iterdir()) == []
