from __future__ import annotations

import ctypes
import errno
import os
import sys
import tempfile
from collections.abc import Callable
from pathlib import Path
from typing import BinaryIO


def _rename_new_file(source: str, target: Path) -> None:
    if sys.platform == "win32":
        os.rename(source, target)
        return
    try:
        libc = ctypes.CDLL(None, use_errno=True)
        if sys.platform == "linux":
            rename = libc.renameat2
            rename.argtypes = [
                ctypes.c_int,
                ctypes.c_char_p,
                ctypes.c_int,
                ctypes.c_char_p,
                ctypes.c_uint,
            ]
            arguments = (-100, os.fsencode(source), -100, os.fsencode(target), 1)
        elif sys.platform == "darwin":
            rename = libc.renamex_np
            rename.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_uint]
            arguments = (os.fsencode(source), os.fsencode(target), 4)
        else:
            raise AttributeError("Unsupported platform")
    except AttributeError as error:
        raise OSError(errno.ENOTSUP, "Atomic no-replace publication is unavailable") from error
    rename.restype = ctypes.c_int
    if rename(*arguments) != 0:
        code = ctypes.get_errno()
        raise OSError(code, f"Atomic no-replace publication failed: {os.strerror(code)}", target)


def write_new_file(target: Path, write: Callable[[BinaryIO], None]) -> None:
    descriptor, temporary = tempfile.mkstemp(prefix=f".{target.name}.", dir=target.parent)
    try:
        with os.fdopen(descriptor, "wb") as output:
            write(output)
            output.flush()
            os.fsync(output.fileno())
        try:
            os.link(temporary, target)
        except FileExistsError as error:
            raise ValueError(f"Local file already exists: {target}") from error
        except OSError as error:
            if error.errno not in {errno.EXDEV, errno.ENOSYS, errno.ENOTSUP, errno.EOPNOTSUPP}:
                raise
            try:
                _rename_new_file(temporary, target)
            except FileExistsError as conflict:
                raise ValueError(f"Local file already exists: {target}") from conflict
    finally:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass
