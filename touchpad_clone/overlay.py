"""Cursor visualizers: sonar ring / track trail / fake arrow (like original)."""
from __future__ import annotations

import tkinter as tk

from . import mouse


class Overlay:
    def __init__(self):
        self.win = None
        self.canvas = None
        self.mode = "sonar"   # none|sonar|track|fake
        self._trail: list[tuple[int, int]] = []
        self._sonar_r = 0
        self._job = None

    def _ensure(self):
        if self.win is not None:
            return
        self.win = tk.Toplevel()
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.attributes("-transparentcolor", "black")
        try:
            self.win.attributes("-alpha", 1.0)
        except Exception:
            pass
        sw, sh = self.win.winfo_screenwidth(), self.win.winfo_screenheight()
        self.win.geometry(f"{sw}x{sh}+0+0")
        self.win.configure(bg="black")
        self.win.wm_attributes("-disabled", True) if hasattr(self.win, "wm_attributes") else None
        # click-through: WS_EX_TRANSPARENT | LAYERED
        try:
            import ctypes
            hwnd = self.win.winfo_id()
            GWL_EXSTYLE, WS_EX_TRANSPARENT, WS_EX_LAYERED = -20, 0x20, 0x80000
            st = ctypes.windll.user32.GetWindowLongW(hwnd, GWL_EXSTYLE)
            ctypes.windll.user32.SetWindowLongW(hwnd, GWL_EXSTYLE, st | WS_EX_TRANSPARENT | WS_EX_LAYERED)
        except Exception:
            pass
        self.canvas = tk.Canvas(self.win, bg="black", highlightthickness=0)
        self.canvas.pack(fill="both", expand=True)

    def ping(self):
        """One-shot sonar at current cursor (also enables overlay loop)."""
        self._sonar_r = 8
        self.tick()

    def set_mode(self, mode: str):
        self.mode = mode
        if mode == "none":
            self.hide()
        else:
            self.tick()

    def tick(self):
        if self.mode == "none":
            return
        self._ensure()
        x, y = mouse.get_cursor()
        c = self.canvas
        c.delete("all")
        if self.mode == "sonar":
            self._sonar_r += 6
            if self._sonar_r > 90:
                self._sonar_r = 8
            for i, rr in enumerate((self._sonar_r, self._sonar_r * 0.6)):
                c.create_oval(x - rr, y - rr, x + rr, y + rr,
                              outline="#35c4ff", width=3 - i)
            c.create_oval(x - 3, y - 3, x + 3, y + 3, fill="#35c4ff", outline="")
        elif self.mode == "track":
            self._trail.append((x, y))
            self._trail = self._trail[-40:]
            for i in range(1, len(self._trail)):
                c.create_line(*self._trail[i - 1], *self._trail[i], fill="#35c4ff", width=2)
            c.create_oval(x - 4, y - 4, x + 4, y + 4, outline="#35c4ff", width=2)
        elif self.mode == "fake":
            # fake arrow cursor drawn over invisible real one
            pts = [x, y, x, y + 22, x + 6, y + 17, x + 9, y + 24,
                   x + 12, y + 22, x + 9, y + 15, x + 15, y + 15]
            c.create_polygon(pts, fill="white", outline="black")
        if self._job is None:
            self._job = self.win.after(50, self._loop)

    def _loop(self):
        self._job = None
        if self.mode != "none" and self.win is not None:
            try:
                self.tick()
            except Exception:
                pass

    def hide(self):
        if self.win is not None:
            try:
                if self._job:
                    self.win.after_cancel(self._job)
            except Exception:
                pass
            self._job = None
            self.win.destroy()
            self.win = None
            self.canvas = None
