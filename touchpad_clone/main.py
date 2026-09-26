"""Entry point: wire pad + overlay + assist + tray + settings together."""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from touchpad_clone.assist import AssistPad
from touchpad_clone.config import Settings
from touchpad_clone.layout import list_layout_names, parse_preset_file
from touchpad_clone.modestrip import ModeStrip
from touchpad_clone.overlay import Overlay
from touchpad_clone.pad import PadWindow
from touchpad_clone.tray import Tray


def resolve_presets(settings: Settings) -> dict:
    bundled = os.path.join(os.path.dirname(__file__), "presets", "Default.ini")
    path = settings.preset_file or bundled
    try:
        return parse_preset_file(path)
    except Exception as e:
        print("preset load failed:", e)
        return parse_preset_file(bundled)


def _startup_marker() -> None:
    """One log line proving WHICH code is running (answers 'did you rerun?')."""
    if os.environ.get("TOUCHPAD_DEBUG") != "1":
        return
    try:
        base = os.environ.get("TEMP") or os.environ.get("TMP") or "."
        here = os.path.dirname(os.path.abspath(__file__))
        import time as _t
        parts = []
        for fn in ("main.py", "pad.py", "modestrip.py", "mouse.py",
                   "layout.py", "config.py"):
            p = os.path.join(here, fn)
            if os.path.exists(p):
                parts.append(f"{fn}={_t.strftime('%H:%M:%S', _t.localtime(os.path.getmtime(p)))}")
        with open(os.path.join(base, "touchpad_debug.log"), "a",
                  encoding="utf-8") as f:
            f.write(f"=== TouchPadClone START pid={os.getpid()} "
                    f"at {_t.strftime('%H:%M:%S')} code[{';'.join(parts)}] ===\n")
    except Exception:
        pass


def main():
    _startup_marker()
    settings = Settings.load()
    presets = resolve_presets(settings)
    if settings.layout not in presets:
        settings.layout = "floatpad" if "floatpad" in presets else next(iter(presets))

    overlay = Overlay()
    assist = AssistPad()
    refs: dict = {}

    def get_layout():
        return presets.get(settings.layout) or next(iter(presets.values()))

    def apply(live: bool):
        pad.set_opacity(settings.opacity)
        overlay.set_mode(settings.visualize)
        if not live:
            nonlocal presets
            presets = resolve_presets(settings)
            if settings.layout not in presets:
                settings.layout = next(iter(presets))
            pad.render()
        strip.set_label(settings.layout)

    def open_settings():
        from touchpad_clone.settings_ui import SettingsWindow
        names = list_layout_names(presets)
        SettingsWindow(settings, names, lambda live: apply(live))

    pad = PadWindow(
        settings, get_layout,
        callbacks={
            "on_settings": open_settings,
            "on_assist": assist.toggle,
            "on_sonar": overlay.ping,
            "on_move": (lambda: overlay.tick()) if settings.visualize != "none" else None,
            "on_fullscreen": lambda: toggle_fullscreen(),
            "on_hide": lambda: None,
        },
    )
    refs["pad"] = pad
    overlay.set_mode(settings.visualize)

    def toggle_fullscreen():
        settings.fullscreen = not settings.fullscreen
        pad.hide() if False else None
        # simplest: resize to fullscreen / restore
        if settings.fullscreen:
            sw, sh = pad.root.winfo_screenwidth(), pad.root.winfo_screenheight()
            pad.root.geometry(f"{sw}x{sh}+0+0")
        else:
            pad.root.geometry(f"{settings.pad_width}x{settings.pad_height}+100+100")
        pad.render()

    def cycle_layout(step: int):
        names = list_layout_names(presets)
        if settings.layout in names:
            i = (names.index(settings.layout) + step) % len(names)
        else:
            i = 0
        settings.layout = names[i]
        settings.save()
        pad.render()
        strip.set_label(settings.layout)

    strip = ModeStrip(
        on_prev=lambda: cycle_layout(-1),
        on_next=lambda: cycle_layout(1),
        on_menu=open_settings,
        on_fullscreen=lambda: toggle_fullscreen(),
    )
    strip.set_label(settings.layout)

    if os.environ.get("TOUCHPAD_DEBUG") == "1":
        # Raw-contact ground truth next to the emulated-mouse log lines.
        from touchpad_clone.pad import _debug_log
        from touchpad_clone.touchhook import attach
        pad.root.update_idletasks()
        attach(pad.root.winfo_id(), _debug_log)

    tray = Tray(on_show=pad.show, on_hide=pad.hide,
                on_settings=open_settings,
                on_quit=lambda: (tray.stop(), assist.close(), overlay.hide(),
                                 strip.destroy(), pad.root.destroy()))
    tray.run_detached()
    print("TouchPad Clone running. Tray icon / pad window를 사용하세요. 종료는 트레이->종료.")
    pad.run()


if __name__ == "__main__":
    main()
