"""Settings window: speed / opacity / layout / gestures / visualize."""
from __future__ import annotations

import tkinter as tk
from tkinter import filedialog, ttk

GESTURE_ACTIONS = ["left_click", "right_click", "middle_click", "wheel_up",
                   "wheel_down", "browser_back", "browser_forward", "sonar",
                   "assist", "fullscreen", "shrink", "none"]


class SettingsWindow:
    def __init__(self, settings, layout_names, on_apply):
        self.s = settings
        self.names = layout_names
        self.on_apply = on_apply
        self.win = tk.Toplevel()
        self.win.title("TouchPad Clone - 설정")
        self.win.geometry("380x560")
        self.win.attributes("-topmost", True)
        self._build()

    def _build(self):
        f = ttk.Frame(self.win, padding=12)
        f.pack(fill="both", expand=True)
        row = 0

        def lab(t):
            nonlocal row
            ttk.Label(f, text=t).grid(row=row, column=0, sticky="w", pady=2)
            row += 1

        lab("커서 속도 (0.2 ~ 5.0)")
        self.speed = tk.DoubleVar(value=self.s.speed)
        ttk.Scale(f, from_=0.2, to=5.0, variable=self.speed,
                  command=lambda *_: self._apply_live()).grid(row=row, column=0, sticky="ew")
        row += 1
        lab("패드 투명도 (0.2 ~ 1.0)")
        self.op = tk.DoubleVar(value=self.s.opacity)
        ttk.Scale(f, from_=0.2, to=1.0, variable=self.op,
                  command=lambda *_: self._apply_live()).grid(row=row, column=0, sticky="ew")
        row += 1
        lab("레이아웃 (원본 INI 섹션)")
        self.layout = tk.StringVar(value=self.s.layout)
        ttk.Combobox(f, textvariable=self.layout, values=self.names,
                     state="readonly").grid(row=row, column=0, sticky="ew")
        row += 1
        lab("커서 시각화")
        self.vis = tk.StringVar(value=self.s.visualize)
        ttk.Combobox(f, textvariable=self.vis,
                     values=["none", "sonar", "track", "fake"],
                     state="readonly").grid(row=row, column=0, sticky="ew")
        row += 1
        self.tap = tk.BooleanVar(value=self.s.tap_to_click)
        ttk.Checkbutton(f, text="탭 = 좌클릭", variable=self.tap).grid(row=row, column=0, sticky="w")
        row += 1
        self.swap = tk.BooleanVar(value=self.s.swap_buttons)
        ttk.Checkbutton(f, text="좌/우 버튼 반전", variable=self.swap).grid(row=row, column=0, sticky="w")
        row += 1
        lab("제스처 매핑")
        self.gvars = {}
        for gname in ("tap", "double_tap", "swipe_up", "swipe_down",
                      "swipe_left", "swipe_right"):
            ttk.Label(f, text=gname).grid(row=row, column=0, sticky="w")
            v = tk.StringVar(value=getattr(self.s.gestures, gname))
            ttk.Combobox(f, textvariable=v, values=GESTURE_ACTIONS,
                         state="readonly", width=18).grid(row=row, column=0, sticky="e")
            self.gvars[gname] = v
            row += 1
        btns = ttk.Frame(f)
        btns.grid(row=row, column=0, pady=10, sticky="ew")
        ttk.Button(btns, text="프리셋 INI 열기…", command=self._pick).pack(side="left")
        ttk.Button(btns, text="적용·저장", command=self._apply_save).pack(side="right")
        row += 1
        ttk.Separator(f).grid(row=row, column=0, sticky="ew", pady=6)
        row += 1
        lab("게임 프리셋용 vJoy (가상 조이스틱 드라이버)")
        from . import vjoy as _vjoy
        status = "설치됨" if _vjoy.is_installed() else "미설치"
        ttk.Label(f, text=f"상태: {status}").grid(row=row, column=0, sticky="w")
        row += 1
        vrow = ttk.Frame(f)
        vrow.grid(row=row, column=0, sticky="ew")
        ttk.Button(vrow, text="다운로드 페이지",
                   command=lambda: _vjoy.open_download_page("download")).pack(side="left")
        ttk.Button(vrow, text="Win10/11 포크",
                   command=lambda: _vjoy.open_download_page("win11")).pack(side="left", padx=4)
        row += 1
        ttk.Label(f, text="원본 WASD 프리셋은 vJoy 필요. 클론은 키보드 입력으로 대체 동작.",
                  wraplength=340, foreground="gray").grid(row=row, column=0, sticky="w")

    def _pick(self):
        p = filedialog.askopenfilename(filetypes=[("TouchMousePointer preset", "*.ini")])
        if p:
            self.s.preset_file = p
            self._apply_save()

    def _apply_live(self):
        self.s.speed = float(self.speed.get())
        self.s.opacity = float(self.op.get())
        self.on_apply(live=True)

    def _apply_save(self):
        self.s.speed = float(self.speed.get())
        self.s.opacity = float(self.op.get())
        self.s.layout = self.layout.get()
        self.s.visualize = self.vis.get()
        self.s.tap_to_click = bool(self.tap.get())
        self.s.swap_buttons = bool(self.swap.get())
        for k, v in self.gvars.items():
            setattr(self.s.gestures, k, v.get())
        self.on_apply(live=False)
        self.s.save()
