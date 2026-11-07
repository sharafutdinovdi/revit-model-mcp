import builtins
import errno
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
    assert not target.exists()
    assert list(tmp_path.iterdir()) == []


@pytest.mark.parametrize("link_errno", [errno.EXDEV, errno.ENOSYS, errno.ENOTSUP, errno.EOPNOTSUPP])
def test_unsupported_link_fallback(tmp_path, monkeypatch, link_errno):
    target = tmp_path / "capture.png"

    def unsupported_link(source, destination):
        assert target.exists() is False or target.read_bytes() == b"original"
        assert source != destination
        raise OSError(link_errno, "Hard links unsupported")

    monkeypatch.setattr(atomic_write.os, "link", unsupported_link)
    write_new_file(target, lambda output: output.write(b"content"))
    assert target.read_bytes() == b"content"
    assert list(tmp_path.iterdir()) == [target]
    target.write_bytes(b"original")
    with pytest.raises(ValueError, match="Local file already exists"):
        write_new_file(target, lambda output: output.write(b"replacement"))
    assert target.read_bytes() == b"original"
    assert list(tmp_path.iterdir()) == [target]


@pytest.mark.parametrize("link_errno", [errno.EXDEV, errno.ENOSYS, errno.ENOTSUP, errno.EOPNOTSUPP])
@pytest.mark.parametrize("destination_kind", ["file", "symlink", "dangling_symlink"])
def test_fallback_publication_conflict(tmp_path, monkeypatch, link_errno, destination_kind):
    target = tmp_path / "capture.png"
    original = tmp_path / "original.png"
    if destination_kind == "symlink":
        original.write_bytes(b"original")

    def unsupported_link(source, destination):
        raise OSError(link_errno, "Hard links unsupported")

    def concurrent_open(path, mode):
        if path == target:
            if destination_kind == "file":
                target.write_bytes(b"original")
            else:
                target.symlink_to(original)
        return builtins.open(path, mode)

    monkeypatch.setattr(atomic_write.os, "link", unsupported_link)
    monkeypatch.setattr(atomic_write, "open", concurrent_open, raising=False)
    with pytest.raises(ValueError, match=re.escape(f"Local file already exists: {target}")):
        write_new_file(target, lambda output: output.write(b"replacement"))

    if destination_kind == "file":
        assert target.read_bytes() == b"original"
    else:
        assert target.is_symlink()
        assert target.readlink() == original
        if destination_kind == "symlink":
            assert original.read_bytes() == b"original"
        else:
            assert not original.exists()
    expected = {target, original} if destination_kind == "symlink" else {target}
    assert set(tmp_path.iterdir()) == expected


def test_fallback_creation_failure_cleans_temporary(tmp_path, monkeypatch):
    target = tmp_path / "capture.png"

    def unsupported_link(source, destination):
        raise OSError(errno.EXDEV, "Hard links unsupported")

    def denied_open(path, mode):
        raise PermissionError("Creation denied")

    monkeypatch.setattr(atomic_write.os, "link", unsupported_link)
    monkeypatch.setattr(atomic_write, "open", denied_open, raising=False)
    with pytest.raises(PermissionError, match="Creation denied"):
        write_new_file(target, lambda output: output.write(b"content"))
    assert list(tmp_path.iterdir()) == []


@pytest.mark.parametrize("failure_stage", ["read", "copy", "flush", "fsync"])
def test_fallback_write_failure_cleans_destination(tmp_path, monkeypatch, failure_stage):
    target = tmp_path / "capture.png"
    real_fsync = atomic_write.os.fsync

    def unsupported_link(source, destination):
        raise OSError(errno.EXDEV, "Hard links unsupported")

    def fail(*args, **kwargs):
        raise OSError("Publication failed")

    def failing_open(path, mode):
        if failure_stage == "read" and mode == "rb":
            fail()
        stream = builtins.open(path, mode)
        if failure_stage == "flush" and mode == "xb":
            wrapped = MagicMock(wraps=stream)
            wrapped.__enter__.return_value = wrapped
            wrapped.__exit__.side_effect = stream.__exit__
            wrapped.flush.side_effect = fail
            return wrapped
        return stream

    def failing_copy(source, output):
        output.write(b"partial")
        fail()

    def failing_fsync(descriptor):
        if target.exists():
            fail()
        real_fsync(descriptor)

    monkeypatch.setattr(atomic_write.os, "link", unsupported_link)
    monkeypatch.setattr(atomic_write, "open", failing_open, raising=False)
    if failure_stage == "copy":
        monkeypatch.setattr(atomic_write.shutil, "copyfileobj", failing_copy)
    if failure_stage == "fsync":
        monkeypatch.setattr(atomic_write.os, "fsync", failing_fsync)
    with pytest.raises(OSError, match="Publication failed"):
        write_new_file(target, lambda output: output.write(b"content"))
    assert list(tmp_path.iterdir()) == []


@pytest.mark.parametrize("replacement_kind", ["file", "symlink", "missing"])
def test_fallback_failure_preserves_concurrent_replacement(tmp_path, monkeypatch, replacement_kind):
    target = tmp_path / "capture.png"

    def unsupported_link(source, destination):
        raise OSError(errno.EXDEV, "Hard links unsupported")

    def failing_copy(source, output):
        output.write(b"partial")
        target.unlink()
        if replacement_kind == "file":
            target.write_bytes(b"replacement")
        elif replacement_kind == "symlink":
            target.symlink_to(source.name)
        raise OSError("Publication failed")

    monkeypatch.setattr(atomic_write.os, "link", unsupported_link)
    monkeypatch.setattr(atomic_write.shutil, "copyfileobj", failing_copy)
    with pytest.raises(OSError, match="Publication failed"):
        write_new_file(target, lambda output: output.write(b"content"))
    if replacement_kind == "file":
        assert target.read_bytes() == b"replacement"
    elif replacement_kind == "symlink":
        assert target.is_symlink()
    expected = [] if replacement_kind == "missing" else [target]
    assert list(tmp_path.iterdir()) == expected
