from __future__ import annotations

import os
import socket
import struct


def enable_abortive_close(sock: socket.socket) -> tuple[int, int]:
    """Require zero-timeout linger; listeners must call this before accepting.

    Winsock's linger uses two unsigned shorts; POSIX uses two ints. Transport
    owners still close their sockets. Never restore graceful close on this path.
    Socket errors propagate unchanged, including errno / winerror.
    """
    fmt = "HH" if os.name == "nt" else "ii"
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack(fmt, 1, 0))
    actual = struct.unpack(
        fmt, sock.getsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.calcsize(fmt))
    )
    if actual != (1, 0):
        raise RuntimeError(f"SO_LINGER abortive close verification failed: expected (1, 0), got {actual}")
    return actual
