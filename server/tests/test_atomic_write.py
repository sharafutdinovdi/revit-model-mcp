import ctypes
import errno
import os
import re
from unittest.mock import MagicMock

import pytest

from revit_model_mcp import atomic_write
from revit_model_mcp.atomic_write import write_new_file


def test_success(tmp_path):
    target = tmp_path / "capture.png"
    write_new_file(target, lambda output: output.write(b"content"))
    assert target.read_bytes() == b"content"
    assert list(tmp_path.iterdir()) == [target]


def test_existing_target(tmp_path):
    target = tmp_path / "capture.png"
    target.write_bytes(b"original")
    with pytest.raises(ValueError, match=re.escape(f"Local file already exists: {target}")):
        write_new_file(target, lambda output: output.write(b"replacement"))
    assert target.read_bytes() == b"original"
    assert list(tmp_path.iterdir()) == [target]


def test_writer_failure(tmp_path):
    target = tmp_path / "capture.png"

    def fail(output):
        output.write(b"partial")
        raise RuntimeError("write failed")

    with pytest.raises(RuntimeError, match="write failed"):
        write_new_file(target, fail)
    assert list(tmp_path.iterdir()) == []


@pytest.fixture(params=["win32", "linux", "darwin"])
def publication(request, monkeypatch):
    platform = request.param
    real_rename = os.rename
    state = {"before_publish": lambda target: None, "errno": None}

    def publish(source, target):
        source, target = os.fsdecode(source), os.fsdecode(target)
        state["before_publish"](target)
        if os.path.lexists(target):
            raise FileExistsError(errno.EEXIST, "Destination exists", target)
        if state["errno"] is not None:
            raise OSError(state["errno"], "Publication unavailable")
        real_rename(source, target)

    def native_publish(*arguments):
        if platform == "linux":
            assert arguments[0] == arguments[2] == -100
            assert arguments[4] == 1
            source, target = arguments[1], arguments[3]
        else:
            assert arguments[2] == 4
            source, target = arguments[:2]
        try:
            publish(source, target)
        except OSError as error:
            ctypes.set_errno(error.errno)
            return -1
        return 0

    def unsupported_link(source, target):
        raise OSError(state.get("link_errno", errno.EXDEV), "Hard links unsupported")

    library = MagicMock()
    native = MagicMock(side_effect=native_publish)
    library.renameat2 = native
    library.renamex_np = native
    monkeypatch.setattr(atomic_write.sys, "platform", platform)
    monkeypatch.setattr(atomic_write.os, "link", unsupported_link)
    monkeypatch.setattr(atomic_write.os, "rename", publish)
    monkeypatch.setattr(atomic_write.ctypes, "CDLL", MagicMock(return_value=library))
    state["platform"] = platform
    state["native"] = native
    return state


@pytest.mark.parametrize("link_errno", [errno.EXDEV, errno.ENOSYS, errno.ENOTSUP, errno.EOPNOTSUPP])
def test_unsupported_link_fallback(tmp_path, publication, link_errno):
    target = tmp_path / "capture.png"
    publication["link_errno"] = link_errno
    write_new_file(target, lambda output: output.write(b"content"))
    assert target.read_bytes() == b"content"
    assert list(tmp_path.iterdir()) == [target]
    if publication["platform"] != "win32":
        assert publication["native"].restype is ctypes.c_int
        assert publication["native"].argtypes[-1] is ctypes.c_uint


@pytest.mark.parametrize("destination_kind", ["file", "symlink", "dangling_symlink"])
@pytest.mark.parametrize("concurrent", [False, True])
def test_fallback_publication_conflict(tmp_path, publication, destination_kind, concurrent):
    target = tmp_path / "capture.png"
    original = tmp_path / "original.png"
    if destination_kind == "symlink":
        original.write_bytes(b"original")

    def create_destination(path):
        if destination_kind == "file":
            target.write_bytes(b"original")
        else:
            target.symlink_to(original)

    if concurrent:
        publication["before_publish"] = create_destination
    else:
        create_destination(target)
    with pytest.raises(ValueError, match=re.escape(f"Local file already exists: {target}")):
        write_new_file(target, lambda output: output.write(b"replacement"))
    if destination_kind == "file":
        assert target.read_bytes() == b"original"
    else:
        assert target.is_symlink()
        assert os.path.normcase(os.path.realpath(target)) == os.path.normcase(
            os.path.realpath(original)
        )
        if destination_kind == "symlink":
            assert original.read_bytes() == b"original"
        else:
            assert not original.exists()
    expected = {target, original} if destination_kind == "symlink" else {target}
    assert set(tmp_path.iterdir()) == expected


@pytest.mark.parametrize("failure_errno", [errno.EINVAL, errno.ENOTSUP, errno.ENOSYS, errno.EACCES])
def test_fallback_publication_failure(tmp_path, publication, failure_errno):
    target = tmp_path / "capture.png"
    publication["errno"] = failure_errno
    with pytest.raises(OSError) as caught:
        write_new_file(target, lambda output: output.write(b"content"))
    assert caught.value.errno == failure_errno
    if publication["platform"] != "win32":
        assert "Atomic no-replace publication failed" in str(caught.value)
    assert list(tmp_path.iterdir()) == []


@pytest.mark.parametrize("platform", ["linux", "darwin", "unknown"])
def test_fallback_missing_primitive(tmp_path, monkeypatch, platform):
    target = tmp_path / "capture.png"

    def unsupported_link(source, destination):
        raise OSError(errno.EXDEV, "Hard links unsupported")

    monkeypatch.setattr(atomic_write.os, "link", unsupported_link)
    monkeypatch.setattr(atomic_write.sys, "platform", platform)
    monkeypatch.setattr(atomic_write.ctypes, "CDLL", lambda *args, **kwargs: object())
    with pytest.raises(OSError, match="Atomic no-replace publication is unavailable") as caught:
        write_new_file(target, lambda output: output.write(b"content"))
    assert caught.value.errno == errno.ENOTSUP
    assert list(tmp_path.iterdir()) == []


@pytest.mark.parametrize("failure_stage", ["write", "flush", "fsync"])
def test_staging_failure_never_publishes(tmp_path, monkeypatch, publication, failure_stage):
    target = tmp_path / "capture.png"
    real_fdopen = os.fdopen
    publish = MagicMock()
    monkeypatch.setattr(atomic_write, "_rename_new_file", publish)

    def fail(*args, **kwargs):
        raise OSError("Staging failed")

    def failing_fdopen(*args, **kwargs):
        stream = real_fdopen(*args, **kwargs)
        wrapped = MagicMock(wraps=stream)
        wrapped.__enter__.return_value = wrapped
        wrapped.__exit__.side_effect = stream.__exit__
        wrapped.flush.side_effect = fail
        return wrapped

    def write(output):
        output.write(b"partial")
        if failure_stage == "write":
            fail()

    if failure_stage == "flush":
        monkeypatch.setattr(atomic_write.os, "fdopen", failing_fdopen)
    elif failure_stage == "fsync":
        monkeypatch.setattr(atomic_write.os, "fsync", fail)
    with pytest.raises(OSError, match="Staging failed"):
        write_new_file(target, write)
    publish.assert_not_called()
    assert list(tmp_path.iterdir()) == []
