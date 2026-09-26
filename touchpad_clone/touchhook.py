"""Ground-truth touch probe: WM_TOUCH contacts for a window, via wndproc hook.

tkinter only delivers emulated mouse events, which mix finger motion with
our own cursor displacement (and interleave multi-contacts). This hook
reports the RAW touch contacts (id, x, y in px, down/move/up) straight from
the OS, independent of the cursor - the reference signal for diagnosing
jitter, ghost touches and multi-finger interleaving.

Attached to the pad window by main.py only when TOUCHPAD_DEBUG=1.
"""
from __future__ import annotations

import ctypes
from ctypes import wintypes

user32 = ctypes.windll.user32

WM_TOUCH = 0x0240
GWL_WNDPROC = -4
TOUCHEVENTF_MOVE = 0x0001
TOUCHEVENTF_DOWN = 0x0002
TOUCHEVENTF_UP = 0x0004

RegisterTouchWindow = user32.RegisterTouchWindow
RegisterTouchWindow.argtypes = [wintypes.HWND, wintypes.ULONG]
RegisterTouchWindow.restype = wintypes.BOOL

GetTouchInputInfo = user32.GetTouchInputInfo
CloseTouchInputHandle = user32.CloseTouchInputHandle


class TOUCHINPUT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG),
                ("y", wintypes.LONG),
                ("hSource", wintypes.HANDLE),
                ("dwID", wintypes.DWORD),
                ("dwFlags", wintypes.DWORD),
                ("dwMask", wintypes.DWORD),
                ("dwTime", wintypes.DWORD),
                ("dwExtraInfo", ctypes.c_void_p),
                ("cxContact", wintypes.DWORD),
                ("cyContact", wintypes.DWORD)]


GetTouchInputInfo.argtypes = [wintypes.HANDLE, wintypes.UINT,
                              ctypes.POINTER(TOUCHINPUT), ctypes.c_int]
GetTouchInputInfo.restype = wintypes.BOOL

_hooks: dict = {}


def _flag_str(f: int) -> str:
    parts = []
    if f & TOUCHEVENTF_DOWN:
        parts.append("DOWN")
    if f & TOUCHEVENTF_MOVE:
        parts.append("MOVE")
    if f & TOUCHEVENTF_UP:
        parts.append("UP")
    return "|".join(parts) or f"0x{f:X}"


def attach(hwnd: int, logfn) -> bool:
    """Subclass hwnd, log WM_TOUCH contacts via logfn(str). Keeps hook alive."""
    if hwnd in _hooks:
        return True
    try:
        ok = RegisterTouchWindow(hwnd, 0)
    except Exception as e:
        logfn(f"touchhook: RegisterTouchWindow failed: {e!r}")
        return False
    logfn(f"touchhook: RegisterTouchWindow(hwnd) -> {bool(ok)}")

    if ctypes.sizeof(ctypes.c_void_p) == 8:
        SetWndProc = user32.SetWindowLongPtrW
    else:
        SetWndProc = user32.SetWindowLongW
    CallWndProc = user32.CallWindowProcW

    WNDPROC = ctypes.WINFUNCTYPE(wintypes.LRESULT, wintypes.HWND,
                                 wintypes.UINT, wintypes.WPARAM,
                                 wintypes.LPARAM)

    def new_proc(h, msg, wp, lp):
        if msg == WM_TOUCH:
            try:
                n = int(wp)
                arr = (TOUCHINPUT * max(n, 1))()
                if GetTouchInputInfo(lp, n, arr, ctypes.sizeof(TOUCHINPUT)):
                    # to screen px (1/100px -> px); window origin unknown
                    # here, so report screen coords like e.x_root.
                    items = " ".join(
                        f"id={arr[i].dwID}@{arr[i].x // 100},{arr[i].y // 100}"
                        f":{_flag_str(arr[i].dwFlags)}"
                        for i in range(n))
                    logfn(f"TOUCH n={n} {items}")
                CloseTouchInputHandle(lp)
            except Exception as e:
                logfn(f"touchhook: decode failed: {e!r}")
            return 0
        return CallWndProc(_hooks[hwnd]["old"], h, msg, wp, lp)

    cb = WNDPROC(new_proc)
    old = SetWndProc(hwnd, GWL_WNDPROC, cb)
    if not old:
        logfn("touchhook: subclass failed")
        return False
    _hooks[hwnd] = {"cb": cb, "old": old}  # keep alive (no GC)
    return True
