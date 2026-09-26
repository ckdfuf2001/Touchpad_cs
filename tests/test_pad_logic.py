"""Deterministic pad-logic tests: drive PadWindow handlers with fake events.

Immune to whoever is wiggling the OS mouse. OS-level delivery is covered
separately by selftest_input.py (needs a hands-off machine).
"""
import os
import sys
import time
import types

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, REPO)

from touchpad_clone import mouse as M
from touchpad_clone.config import Settings
from touchpad_clone.layout import parse_preset_file
from touchpad_clone.pad import PadWindow


class Ev:
    def __init__(self, x, y, state=0x100):
        self.x, self.y, self.state = x, y, state


def make_pad():
    s = Settings(speed=1.6, acceleration=1.0, tap_to_click=True)
    d = parse_preset_file(os.path.join(REPO, "touchpad_clone", "presets",
                                       "Default.ini"))
    pad = PadWindow(s, lambda: d["floatpad"], callbacks={})
    pad.root.update_idletasks()
    pad.root.update()
    return pad


def main():
    calls = {"move": [], "click": [], "wheel": []}
    M.move_relative = lambda dx, dy: calls["move"].append((dx, dy))
    M.click = lambda b="left": calls["click"].append(b)
    M.wheel = lambda d, horizontal=False: calls["wheel"].append(d)

    pad = make_pad()
    W = pad.canvas.winfo_width() or 340
    H = pad.canvas.winfo_height() or 260
    cx, cy = int(W * 0.5), int(H * 0.5 + 30)  # pad tile, center-ish

    # --- Closed-loop world model (matches the measured machine) ---
    # Event coords TRACK the OS cursor: every move_relative we apply shifts
    # the coordinates the next event reports. The sim keeps a virtual cursor
    # starting at the press point; each fake event reports sim_cursor, and
    # our handler's output feeds back into it - exactly like Windows.
    ORGX, ORGY = 758, 331  # pretend window origin (screen coords)
    sim = {"x": ORGX + cx, "y": ORGY + cy}

    def sim_move(dx, dy):
        calls["move"].append((dx, dy))
        sim["x"] += dx
        sim["y"] += dy

    M.move_relative = sim_move

    def sim_ev():
        return Ev(sim["x"] - ORGX, sim["y"] - ORGY)

    # 1. 40px finger drag, delivered as 5x8px cursor-following events.
    # Without own-motion compensation this diverges into corner ping-pong
    # (measured live: repeating full-screen diagonals -> +-11000px flings).
    # With compensation the total must equal the linear expectation.
    pad._on_press(sim_ev())
    for _ in range(5):
        sim["x"] += 8  # finger advances 8px; event reports cursor pos
        pad._on_drag(sim_ev(), "b1")
    pad._on_release(sim_ev())
    total = sum(m[0] for m in calls["move"])
    print("closed-loop drag applied total =", total, "final cursor =", sim)
    assert abs(total - 5 * int(8 * 1.6)) <= 10, f"gain wrong: {total}"
    assert abs(sim["x"] - (ORGX + cx + 64)) < 60, f"cursor flung: {sim}"
    print("PASS: closed-loop drag stable at linear gain (no ping-pong)")

    # 1b. one coalesced 40px jump (worst-case chunking) must not explode.
    calls["move"].clear()
    sim["x"], sim["y"] = ORGX + cx, ORGY + cy
    pad._press = None
    pad._last_press_t = 0  # anti-storm floor would eat a back-to-back press
    pad._on_press(sim_ev())
    sim["x"] += 40
    pad._on_drag(sim_ev(), "b1")
    pad._on_release(sim_ev())
    total = sum(m[0] for m in calls["move"])
    print("coalesced 40px jump applied total =", total)
    assert abs(total) <= 100, f"chunk exploded: {total}"
    print("PASS: coalesced jump bounded")

    # 2. duplicate <Motion> delivery of the same points is ignored (no 2x)
    n0 = len(calls["move"])
    pad._on_press(Ev(cx, cy))
    pad._last_b1_t = time.time()
    pad._on_drag(Ev(cx + 8, cy), "m")  # dup while B1 alive -> skipped
    assert len(calls["move"]) == n0, "duplicate Motion was applied!"
    print("PASS: duplicate Motion ignored while B1 stream alive")

    # 3. sub-10ms press spacing is dropped (storm floor)
    pad._last_press_t = time.time()
    pad._press = None
    pad._on_press(Ev(cx, cy))
    assert pad._press is None, "storm press was not dropped!"
    print("PASS: sub-10ms press dropped")

    # 4. tap with cursor OVER our own window -> click suppressed (no loop)
    M.get_cursor = lambda: (pad.root.winfo_rootx() + 10,
                            pad.root.winfo_rooty() + 10)
    pad._last_press_t = 0
    pad._press = None
    pad._on_press(Ev(cx, cy))
    time.sleep(0.05)
    pad._on_release(Ev(cx, cy))
    assert calls["click"] == [], f"self-click NOT suppressed: {calls['click']}"
    print("PASS: self-click suppressed")

    # 5. tap with cursor elsewhere -> click delivered
    M.get_cursor = lambda: (10, 10)
    pad._last_press_t = 0
    pad._press = None
    pad._on_press(Ev(cx, cy))
    time.sleep(0.05)
    pad._on_release(Ev(cx, cy))
    assert calls["click"] == ["left"], f"tap lost: {calls['click']}"
    print("PASS: normal tap clicks")

    pad.root.destroy()
    print("ALL LOGIC TESTS PASSED")


if __name__ == "__main__":
    main()
