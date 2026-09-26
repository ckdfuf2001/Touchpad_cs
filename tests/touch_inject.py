"""Real-touch repro harness: drives the ACTUAL app with synthesized touch.

Uses user32.InjectTouchInput (real POINTER_TOUCH_INFO contacts through the
OS input stack - not mouse emulation), so WPF TouchDown/Move/Up, capture,
promotion-suppression and all gesture paths behave exactly like a finger.

Usage:  python tests/touch_inject.py [tap|hold|dblhold|dbltap|all]
Kills stray app instances first, launches dist build, asserts via
%TEMP%\\touchpad_v2.log + GetAsyncKeyState (stuck buttons) + fake totals.

Coordinates are PHYSICAL pixels (virtual-screen space).
"""
import ctypes
import json
import os
import re
import subprocess
import sys
import time

u = ctypes.windll.user32

PT_TOUCH = 2
F_DOWN = 0x00010000 | 0x00000002 | 0x00000004
F_MOVE = 0x00020000 | 0x00000002 | 0x00000004
F_UP = 0x00040000
TOUCH_MASK_ALL = 0x00000004 | 0x00000002 | 0x00000001


class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


class RECT(ctypes.Structure):
    _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long),
                ("r", ctypes.c_long), ("b", ctypes.c_long)]


class POINTER_INFO(ctypes.Structure):
    _fields_ = [("pointerType", ctypes.c_uint32),
                ("pointerId", ctypes.c_uint32),
                ("frameId", ctypes.c_uint32),
                ("pointerFlags", ctypes.c_uint32),
                ("sourceDevice", ctypes.c_void_p),
                ("hwndTarget", ctypes.c_void_p),
                ("ptPixelLocation", POINT),
                ("ptHimetricLocation", POINT),
                ("ptHimetricLocationRaw", POINT),
                ("dwTime", ctypes.c_uint32),
                ("historyCount", ctypes.c_uint32),
                ("inputData", ctypes.c_int32),
                ("dwKeyStates", ctypes.c_uint32),
                ("PerformanceCount", ctypes.c_uint64),
                ("ButtonChangeType", ctypes.c_uint32)]


class POINTER_TOUCH_INFO(ctypes.Structure):
    _fields_ = [("pointerInfo", POINTER_INFO),
                ("touchFlags", ctypes.c_uint32),
                ("touchMask", ctypes.c_uint32),
                ("rcContact", RECT),
                ("rcContactRaw", RECT),
                ("orientation", ctypes.c_uint32),
                ("pressure", ctypes.c_uint32)]


def _contact(pid, x, y, flags):
    c = POINTER_TOUCH_INFO()
    c.pointerInfo.pointerType = PT_TOUCH
    c.pointerInfo.pointerId = pid
    c.pointerInfo.pointerFlags = flags
    c.pointerInfo.ptPixelLocation = POINT(int(x), int(y))
    c.touchFlags = 0
    c.touchMask = TOUCH_MASK_ALL
    c.rcContact = RECT(int(x) - 2, int(y) - 2, int(x) + 2, int(y) + 2)
    c.rcContactRaw = RECT(int(x) - 2, int(y) - 2, int(x) + 2, int(y) + 2)
    c.orientation = 90
    c.pressure = 32000
    return c


def inject(*contacts):
    arr = (POINTER_TOUCH_INFO * len(contacts))(*contacts)
    ok = u.InjectTouchInput(len(contacts), arr)
    if not ok:
        raise RuntimeError(f"InjectTouchInput failed err={ctypes.GetLastError()}")


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "cs", "TouchPadCloneV2", "dist", "TouchPadCloneV2.exe")
TMP = os.environ.get("TEMP", ".")
LOG = os.path.join(TMP, "touchpad_v2.log")


def log_lines():
    try:
        return open(LOG, encoding="utf-8", errors="ignore").read().splitlines()
    except Exception:
        return []


def log_since(n):
    return log_lines()[n:]


def buttons():
    """True if any mouse button is physically/logically down (stuck check)."""
    states = []
    for vk in (1, 2, 4):
        states.append(bool(u.GetAsyncKeyState(vk) & 0x8000))
    return states


def pad_center():
    h = u.FindWindowW(None, "TouchPad Clone")
    assert h, "pad window not found"
    r = (ctypes.c_long * 4)()
    u.GetWindowRect(h, r)
    # pad tile area: middle of window, slightly below title bar
    return (r[0] + r[2]) // 2, (r[1] + r[3]) // 2 + 30


def seq_tap(x, y):
    inject(_contact(0, x, y, F_DOWN))
    time.sleep(0.08)
    inject(_contact(0, x, y, F_UP))
    time.sleep(0.3)


def seq_hold(x, y, hold=0.8):
    inject(_contact(0, x, y, F_DOWN))
    time.sleep(hold)
    inject(_contact(0, x, y, F_UP))
    time.sleep(0.4)


def seq_drag(x, y, dx, hold_first=0.0, steps=5):
    inject(_contact(0, x, y, F_DOWN))
    time.sleep(hold_first if hold_first else 0.08)
    for i in range(1, steps + 1):
        inject(_contact(0, x + dx * i / steps, y, F_MOVE))
        time.sleep(0.03)
    time.sleep(0.1)
    inject(_contact(0, x + dx, y, F_UP))
    time.sleep(0.4)


def seq_double_hold(x, y):
    seq_tap(x, y)
    time.sleep(0.15)
    # second press, hold, then drag right, then release
    inject(_contact(0, x, y, F_DOWN))
    time.sleep(0.5)
    for i in range(1, 6):
        inject(_contact(0, x + 40 * i / 5, y, F_MOVE))
        time.sleep(0.03)
    time.sleep(0.1)
    inject(_contact(0, x + 40, y, F_UP))
    time.sleep(0.4)


def fake_total(lines):
    tot = 0
    for m in re.findall(r"FAKE d=\((-?\d+),(-?\d+)\) -> \((-?\d+),(-?\d+)\)",
                        "\n".join(lines)):
        tot += int(m[2])
    return tot


def run_case(name, fn, checks):
    n0 = len(log_lines())
    print(f"--- case: {name} ---")
    fn()
    new = log_since(n0)
    for desc, check in checks:
        ok, info = check(new)
        print(f"  [{'PASS' if ok else 'FAIL'}] {desc} {info}")
        if not ok:
            raise AssertionError(f"{name}: {desc} {info}")
    lb, rb, mb = buttons()
    assert not (lb or rb or mb), f"BUTTON STUCK L={lb} R={rb} M={mb}"
    print("  [PASS] no stuck buttons")


def main(which="all"):
    if not u.InitializeTouchInjection(256, 0x1):
        print("init rc=", ctypes.GetLastError())
    # fresh app
    os.system("taskkill /F /IM TouchPadCloneV2.exe >nul 2>&1")
    time.sleep(1.5)
    open(os.path.join(TMP, "TOUCHPAD_DEBUG"), "w").write("1")
    subprocess.Popen([EXE])
    for _ in range(100):
        if u.FindWindowW(None, "TouchPadClone ModeStrip"):
            break
        time.sleep(0.2)
    time.sleep(1.0)
    # launch model = strip only: tap strip to show the pad
    h = u.FindWindowW(None, "TouchPadClone ModeStrip")
    r = (ctypes.c_long * 4)()
    u.GetWindowRect(h, r)
    seq_tap((r[0] + r[2]) // 2, (r[1] + r[3]) // 2)
    for _ in range(100):
        if u.FindWindowW(None, "TouchPad Clone"):
            break
        time.sleep(0.2)
    time.sleep(1.0)
    x, y = pad_center()
    print(f"pad tile point: {(x, y)}")

    cases = {
        "tap": lambda: run_case("tap", lambda: seq_tap(x, y), [
            ("tap click fired",
             lambda L: (any("ACTION left_click" in l for l in L),
                        [l for l in L if "ACTION" in l or "TOUCHUP" in l][-2:])),
        ]),
        "hold": lambda: run_case("long-press hold", lambda: seq_hold(x, y, 0.8), [
            ("long-press fired",
             lambda L: (any("GESTURE long-press" in l for l in L), "")),
            ("right click fired",
             lambda L: (any("ACTION right_click" in l for l in L),
                        [l for l in L if "ACTION" in l])),
        ]),
        "dblhold": lambda: run_case("double+hold drag", lambda: seq_double_hold(x, y), [
            ("immediate drag engaged",
             lambda L: (any("second-hold (immediate drag)" in l for l in L), "")),
            ("fake roamed with finger",
             lambda L: ((lambda t: (40 <= t <= 400, f"total={t}"))(abs(fake_total(L))))),
        ]),
        "dbltap": lambda: run_case("double tap", lambda: (seq_tap(x, y),
                                                          time.sleep(0.15),
                                                          seq_tap(x, y)), [
            ("two left clicks",
             lambda L: ((lambda n: (n >= 2, f"clicks={n}"))(
                 sum("ACTION left_click" in l for l in L)))),
        ]),
    }
    if which == "all":
        for name in ("tap", "hold", "dblhold", "dbltap"):
            cases[name]()
    else:
        cases[which]()
    print("ALL INJECTION TESTS PASSED")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "all")
