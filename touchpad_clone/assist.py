"""Assist pad: Ctrl / Shift / Alt / Space hold buttons (original virtualctrls)."""
from __future__ import annotations

import tkinter as tk

from . import mouse

KEYS = [("Ctrl", 0x11), ("Shift", 0x10), ("Alt", 0x12), ("Space", 0x20)]


class AssistPad:
    def __init__(self):
        self.win = None

    def toggle(self):
        if self.win is not None:
            self.close()
        else:
            self.open()

    def open(self):
        self.win = tk.Toplevel()
        self.win.title("AssistPad")
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        try:
            self.win.attributes("-alpha", 0.85)
        except Exception:
            pass
        sw, sh = self.win.winfo_screenwidth(), self.win.winfo_screenheight()
        self.win.geometry(f"300x70+40+{sh - 160}")
        for i, (label, vk) in enumerate(KEYS):
            b = tk.Button(self.win, text=label, bg="#2c3340", fg="white",
                          activebackground="#2e6be6", relief="flat")
            b.pack(side="left", fill="both", expand=True, padx=2, pady=6)

            def down(e, v=vk):
                mouse.hold_key(v, True)

            def up(e, v=vk):
                mouse.hold_key(v, False)

            b.bind("<ButtonPress-1>", down)
            b.bind("<ButtonRelease-1>", up)
        x = tk.Button(self.win, text="✕", command=self.close, bg="#3a3f4b",
                      fg="white", relief="flat", width=3)
        x.pack(side="left", padx=2)

    def close(self):
        for _, vk in KEYS:
            try:
                mouse.hold_key(vk, False)
            except Exception:
                pass
        if self.win is not None:
            self.win.destroy()
            self.win = None
