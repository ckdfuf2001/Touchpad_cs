"""Top-edge mode-switch strip.

The original TouchMousePointer has NO dedicated top-of-screen swipe area -
mode switching there is: pinch-out/pinch-in on the pad itself, tray-icon tap
(lamp = fullscreen on), a Gesture mapped to "Layout...", or the pad's own
menu tile. (See ANALYSIS.md / official screenshots page.)

This strip is a clone-side convenience implementing what was asked for:
a thin always-on-top bar docked at the top edge of the primary screen.

  - swipe/drag left  -> previous layout
  - swipe/drag right -> next layout   (order = preset section order)
  - tap              -> settings menu callback
  - drag down (>30px)-> fullscreen toggle callback

It is 10px tall, semi-transparent, and widens while hovered so it stays out
of the way. Disabled automatically while a fullscreen pad covers the screen
only if `hide_in_fullscreen=True` is passed.
"""
from __future__ import annotations

import time
import tkinter as tk

SWIPE_PX = 32
TAP_MS = 300
TAP_PX = 12
DOWN_PX = 24


class ModeStrip:
    def __init__(self, on_prev, on_next, on_menu, on_fullscreen=None,
                 hide_in_fullscreen: bool = False):
        self.cb = dict(prev=on_prev, next=on_next, menu=on_menu,
                       fullscreen=on_fullscreen)
        self.hide_in_fullscreen = hide_in_fullscreen
        self.win = tk.Tk()
        self.win.title("TouchPadClone ModeStrip")
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        try:
            self.win.attributes("-alpha", 0.35)
        except Exception:
            pass
        sw = self.win.winfo_screenwidth()
        self._w = max(300, sw // 3)
        self._x = (sw - self._w) // 2
        self._collapsed_h = 10
        self._expanded_h = 30
        self.win.geometry(f"{self._w}x{self._collapsed_h}+{self._x}+0")
        self.label = tk.Label(self.win, text="◀  mode  ▶",
                              bg="#10131a", fg="#9aa6bd",
                              font=("Segoe UI", 8))
        self.label.pack(fill="both", expand=True)
        for ev, fn in (("<ButtonPress-1>", self._press),
                       ("<B1-Motion>", self._drag),
                       ("<Motion>", self._drag),
                       ("<ButtonRelease-1>", self._release),
                       ("<Enter>", lambda e: self._expand(True)),
                       ("<Leave>", lambda e: self._expand(False))):
            self.win.bind(ev, fn)
            self.label.bind(ev, fn)
        self._p = None
        self._label_text = "◀  mode  ▶"

    def set_label(self, text: str):
        self._label_text = text
        short = f"◀  {text}  ▶"
        try:
            self.label.config(text=short)
        except Exception:
            pass

    def _expand(self, on: bool):
        h = self._expanded_h if on else self._collapsed_h
        try:
            self.win.geometry(f"{self._w}x{h}+{self._x}+0")
            self.win.attributes("-alpha", 0.85 if on else 0.35)
        except Exception:
            pass

    def _press(self, e):
        now = time.time()
        if now - getattr(self, "_last_press_t", 0) < 0.010:
            # Same anti-storm floor as the pad: storm clicks landing on the
            # strip must not open a hundred settings windows.
            self._p = None
            return
        self._last_press_t = now
        self._p = {"x0": e.x_root, "y0": e.y_root, "t0": now, "moved": False}

    def _drag(self, e):
        if not self._p:
            return
        # Hover without button (touch stacks may send it) is not a swipe.
        if not (getattr(e, "state", 0x100) & 0x100):
            return
        if abs(e.x_root - self._p["x0"]) + abs(e.y_root - self._p["y0"]) > TAP_PX:
            self._p["moved"] = True

    def _release(self, e):
        p, self._p = self._p, None
        if not p:
            return
        dx, dy = e.x_root - p["x0"], e.y_root - p["y0"]
        dt = (time.time() - p["t0"]) * 1000
        if not p["moved"] and dt < TAP_MS:
            self.cb["menu"]()
            return
        if abs(dx) >= SWIPE_PX and abs(dx) >= abs(dy):
            (self.cb["next"] if dx > 0 else self.cb["prev"])()
        elif dy > DOWN_PX and abs(dy) > abs(dx) and self.cb["fullscreen"]:
            self.cb["fullscreen"]()

    # -- external control --
    def show(self):
        try:
            self.win.deiconify()
        except Exception:
            pass

    def hide(self):
        try:
            self.win.withdraw()
        except Exception:
            pass

    def destroy(self):
        try:
            self.win.destroy()
        except Exception:
            pass
