"""OS-level input self-test: drives the real app with synthetic mouse events.

Covers the reported symptoms:
  1. drag on the pad tile  -> system cursor must move proportionally
  2. horizontal swipe on ModeStrip -> layout cycles (settings.json changes)

Run:  python tests/selftest_input.py
"""
import ctypes
import glob
import json
import os
import re
import subprocess
import sys
import time

user32 = ctypes.windll.user32

MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SETTINGS = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")),
                        "TouchPadClone", "settings.json")


def debug_log_path():
    base = os.environ.get("TEMP") or os.environ.get("TMP") or "."
    return os.path.join(base, "touchpad_debug.log")


def find_rect(title):
    hwnd = user32.FindWindowW(None, title)
    if not hwnd:
        return None
    r = (ctypes.c_long * 4)()
    user32.GetWindowRect(hwnd, r)
    return (r[0], r[1], r[2], r[3])


def find_rect_for_pid(title, pid):
    """Find a top-level window by title OWNED by pid (ignores strays)."""
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(hwnd, _):
        if not user32.IsWindowVisible(hwnd):
            return True
        ln = user32.GetWindowTextLengthW(hwnd)
        if ln <= 0 or ln > 256:
            return True
        buf = ctypes.create_unicode_buffer(ln + 1)
        user32.GetWindowTextW(hwnd, buf, ln + 1)
        if buf.value != title:
            return True
        wpid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(wpid))
        if wpid.value == pid:
            r = (ctypes.c_long * 4)()
            user32.GetWindowRect(hwnd, r)
            found.append((r[0], r[1], r[2], r[3]))
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else None


def cursor():
    pt = (ctypes.c_long * 2)()
    user32.GetCursorPos(pt)
    return pt[0], pt[1]


def move_to(x, y):
    user32.SetCursorPos(int(x), int(y))
    time.sleep(0.05)


def read_log_lines():
    try:
        return open(debug_log_path(), encoding="utf-8",
                    errors="ignore").read().splitlines()
    except Exception:
        return []


def drag(x0, y0, x1, y1, steps=8):
    """Single relative-move drag (for the strip, which has no event log)."""
    move_to(x0, y0)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.08)
    dx, dy = (x1 - x0) / steps, (y1 - y0) / steps
    for _ in range(steps):
        user32.mouse_event(MOUSEEVENTF_MOVE, int(round(dx)),
                           int(round(dy)), 0, 0)
        time.sleep(0.03)
    time.sleep(0.08)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.25)


def drag_until_press(x0, y0, x1, y1, steps=8, tries=4):
    """Drag, retrying if the press never reached the app (e.g. a human is
    moving the mouse concurrently and stole the cursor between our
    positioning and the button-down). Returns new log lines."""
    for attempt in range(tries):
        before = len(read_log_lines())
        move_to(x0, y0)
        time.sleep(0.05)
        cx, cy = cursor()
        if abs(cx - x0) > 8 or abs(cy - y0) > 8:
            print(f"  attempt {attempt}: cursor stolen ({cx},{cy}), retrying")
            time.sleep(0.4)
            continue
        user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
        time.sleep(0.08)
        dx, dy = (x1 - x0) / steps, (y1 - y0) / steps
        for _ in range(steps):
            user32.mouse_event(MOUSEEVENTF_MOVE, int(round(dx)),
                               int(round(dy)), 0, 0)
            time.sleep(0.03)
        time.sleep(0.08)
        user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
        time.sleep(0.25)
        new = read_log_lines()[before:]
        if any("press " in l for l in new):
            return new
        print(f"  attempt {attempt}: press not delivered, retrying")
        time.sleep(0.4)
    return read_log_lines()[before:]


def main():
    backup = None
    if os.path.exists(SETTINGS):
        backup = open(SETTINGS, encoding="utf-8").read()
        os.remove(SETTINGS)  # force code defaults (speed/accel) for assertions
    try:
        open(debug_log_path(), "w").write("--- selftest start ---\n")
    except Exception:
        pass
    env = dict(os.environ, TOUCHPAD_DEBUG="1")
    proc = subprocess.Popen([sys.executable, "-m", "touchpad_clone.main"],
                            cwd=REPO, env=env)
    try:
        pad = strip = None
        for _ in range(100):
            pad = find_rect_for_pid("TouchPad Clone", proc.pid)
            strip = find_rect_for_pid("TouchPadClone ModeStrip", proc.pid)
            if pad and strip:
                break
            time.sleep(0.2)
            if proc.poll() is not None:
                raise RuntimeError(f"app exited early code={proc.returncode}")
        assert pad, "pad window (own pid) not found"
        assert strip, "modestrip window (own pid) not found"
        print("pad rect:", pad, "strip rect:", strip)
        time.sleep(0.5)

        # --- 1. pad drag -> app applies dx*speed (log is ground truth:
        # the shared OS cursor may be moved by the human tester mid-run) ---
        px0, py0, px1, py1 = pad
        # middle of window; floatpad center is the pad tile in bundled preset
        sx, sy = (px0 + px1) // 2, (py0 + py1) // 2 + 30
        new = drag_until_press(sx, sy, sx + 40, sy, steps=5)
        presses = [l for l in new if "press " in l]
        moves = re.findall(r"move \S+: d=\((-?\d+),(-?\d+)\) -> \((-?\d+),(-?\d+)\)",
                           "\n".join(new))
        applied_x = sum(int(m[2]) for m in moves)
        print(f"press events: {presses[-1] if presses else 'NONE'}")
        print(f"move events: {len(moves)}, applied_x total={applied_x}")
        assert any("-> pad" in l for l in presses), \
            f"press did not hit pad tile: {presses[-3:]}"
        # Linear gain: 40px x speed1.6 = 64 (chunking-independent).
        assert 40 <= applied_x <= 100, \
            f"relative gain off (applied_x={applied_x})"
        print("PASS: pad drag applies single-delivery linear gain")
        # No click-storm: the release tap lands on our own window, so the
        # click must be suppressed, not re-enter as a press flood.
        time.sleep(0.6)
        total_presses = sum(1 for l in read_log_lines() if "press " in l)
        print(f"total press lines after settle: {total_presses}")
        assert total_presses < 10, f"CLICK STORM: {total_presses} presses"
        print("PASS: no self-click storm")

        # --- 2. strip swipe -> layout cycles ---
        sx0, sy0, sx1, sy1 = strip
        cx, cy = (sx0 + sx1) // 2, (sy0 + sy1) // 2
        lay_before = json.load(open(SETTINGS, encoding="utf-8"))["layout"]
        drag(cx, cy, cx + 80, cy, steps=6)
        time.sleep(0.4)
        lay_after = json.load(open(SETTINGS, encoding="utf-8"))["layout"]
        print(f"layout {lay_before} -> {lay_after}")
        assert lay_after != lay_before, "strip swipe did not cycle layout"
        print("PASS: strip swipe cycles layout")

        # --- 3. strip swipe DOWN -> fullscreen toggle (pad covers screen) ---
        sw = user32.GetSystemMetrics(0)
        sh = user32.GetSystemMetrics(1)
        drag(cx, cy, cx, cy + 60, steps=5)
        time.sleep(0.4)
        pad2 = find_rect_for_pid("TouchPad Clone", proc.pid)
        print("pad rect after swipe-down:", pad2, "screen:", (sw, sh))
        assert pad2 and (pad2[2] - pad2[0]) >= sw - 10, \
            "strip swipe-down did not enter fullscreen"
        print("PASS: strip swipe-down toggles fullscreen")
        print("ALL SELF-TESTS PASSED")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=8)
        except Exception:
            proc.kill()
        if backup is not None:
            open(SETTINGS, "w", encoding="utf-8").write(backup)
        elif os.path.exists(SETTINGS):
            os.remove(SETTINGS)


if __name__ == "__main__":
    main()
