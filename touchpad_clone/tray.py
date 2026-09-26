"""System tray (pystray). Falls back to no-tray if unavailable."""
from __future__ import annotations

try:
    import pystray
    from PIL import Image, ImageDraw
    HAS_TRAY = True
except Exception:
    HAS_TRAY = False


def _icon():
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([6, 14, 58, 54], 10, fill=(46, 107, 230, 255))
    d.rounded_rectangle([14, 22, 50, 46], 6, fill=(27, 30, 36, 255))
    d.line([32, 14, 32, 22], fill="white", width=3)
    d.ellipse([28, 30, 36, 38], fill=(53, 196, 255, 255))
    return img


class Tray:
    def __init__(self, on_show, on_hide, on_settings, on_quit):
        self._tray = None
        self.cb = dict(show=on_show, hide=on_hide, settings=on_settings, quit=on_quit)

    def run_detached(self):
        if not HAS_TRAY:
            return
        import threading
        th = threading.Thread(target=self.run, daemon=True)
        th.start()

    def run(self):
        if not HAS_TRAY:
            return
        self._tray = pystray.Icon(
            "touchpad-clone", _icon(), "TouchPad Clone",
            menu=pystray.Menu(
                pystray.MenuItem("패드 보이기", lambda: self.cb["show"](), default=True),
                pystray.MenuItem("패드 숨기기", lambda: self.cb["hide"]()),
                pystray.MenuItem("설정…", lambda: self.cb["settings"]()),
                pystray.MenuItem("종료", lambda: self.cb["quit"]()),
            ))
        self._tray.run()

    def stop(self):
        try:
            if self._tray:
                self._tray.stop()
        except Exception:
            pass
