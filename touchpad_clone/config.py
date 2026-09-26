"""Settings dataclass + JSON persistence. Preset INI files are loaded separately."""
from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, field


def _config_path() -> str:
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    d = os.path.join(base, "TouchPadClone")
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, "settings.json")


@dataclass
class GestureMap:
    tap: str = "left_click"            # single tap on pad
    double_tap: str = "left_click"
    two_finger_tap: str = "right_click"
    swipe_up: str = "wheel_up"
    swipe_down: str = "wheel_down"
    swipe_left: str = "browser_back"
    swipe_right: str = "browser_forward"
    pinch_in: str = "shrink"
    pinch_out: str = "fullscreen"


@dataclass
class Settings:
    speed: float = 1.6                 # cursor multiplier
    # NOTE: 1.0 (linear) is the default on purpose. Per-event exponential
    # acceleration makes cursor speed depend on OS event chunking (coalesced
    # touch/mouse moves arrive as fewer, larger deltas and the exponent
    # inflates them: e.g. 40px at once -> (64)^1.25 ~= 181px). Linear gain
    # moves exactly dx*speed regardless of chunking. Set >1 only if you
    # want the old "fast flicks fly" feel.
    acceleration: float = 1.0
    wheel_step: int = 120
    opacity: float = 0.55              # pad window 0.2..1.0
    pad_width: int = 340
    pad_height: int = 260
    layout: str = "floatpad"           # INI section name
    preset_file: str = ""              # full path or "" = bundled
    visualize: str = "sonar"           # none|sonar|track|fake
    fullscreen: bool = False
    swap_buttons: bool = False
    tap_to_click: bool = True
    gestures: GestureMap = field(default_factory=GestureMap)

    def save(self, path: str = "") -> str:
        path = path or _config_path()
        with open(path, "w", encoding="utf-8") as f:
            json.dump(asdict(self), f, indent=2, ensure_ascii=False)
        return path

    @classmethod
    def load(cls, path: str = "") -> "Settings":
        path = path or _config_path()
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
            g = data.pop("gestures", {})
            s = cls(**{k: v for k, v in data.items() if k in cls.__dataclass_fields__})
            for k, v in g.items():
                if hasattr(s.gestures, k):
                    setattr(s.gestures, k, v)
            return s
        except Exception:
            return cls()
