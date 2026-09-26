"""INI preset/layout parser, compatible with original TouchMousePointer tile syntax.

Original line format:
    tile000=TYPE,x,y,w,h[,extra...]
x/y/w/h are 0..100 relative percentages of the pad window.
Sections: [floatpad], [leftpad], [rightpad], [fullscreen], [ArtistPad],
          [virtualctrls], ...  plus `scale=` and `aspect=` hints.

We parse every section into Layout { name, scale, aspect, tiles }.
Unsupported tile types degrade gracefully to blank/key tiles.
"""
from __future__ import annotations

import codecs
import configparser
from dataclasses import dataclass, field


@dataclass
class Tile:
    name: str          # tile000
    kind: str          # lower-cased: pad, lbtn, wheel, vk_control, c, movegrip ...
    raw_kind: str      # original spelling
    x: float = 0
    y: float = 0
    w: float = 100
    h: float = 100
    extras: list = field(default_factory=list)


@dataclass
class Layout:
    name: str
    scale: tuple = (100, 100)
    aspect: tuple | None = None
    tiles: list = field(default_factory=list)

    def input_tiles(self):
        """Tiles in z-order (original draws later tiles on top; `pad` layer is usually last)."""
        return self.tiles


def _decode_file(path: str) -> str:
    data = open(path, "rb").read()
    for enc in ("utf-16", "utf-16-le", "utf-8-sig", "utf-8", "cp949"):
        try:
            text = data.decode(enc)
            if "\x00" in text and enc.startswith("utf-8"):
                continue
            if "tile" in text.lower() or "[" in text:
                return text
        except Exception:
            continue
    return data.decode("utf-8", errors="ignore")


def parse_preset_file(path: str) -> dict[str, Layout]:
    text = _decode_file(path)
    cp = configparser.ConfigParser(strict=False, interpolation=None)
    cp.optionxform = str  # keep case of tile names
    cp.read_string(text)
    out: dict[str, Layout] = {}
    for section in cp.sections():
        lay = Layout(name=section)
        for key, val in cp.items(section):
            k = key.strip().lower()
            if k == "scale":
                try:
                    a, b = str(val).split(",")[:2]
                    lay.scale = (float(a), float(b))
                except Exception:
                    pass
            elif k == "aspect":
                try:
                    a, b = str(val).split(",")[:2]
                    lay.aspect = (float(a), float(b))
                except Exception:
                    pass
            elif k.startswith("tile"):
                parts = [p.strip() for p in str(val).split(",")]
                if not parts:
                    continue
                kind = parts[0]
                nums = []
                for p in parts[1:5]:
                    try:
                        nums.append(float(p))
                    except Exception:
                        nums.append(0.0)
                while len(nums) < 4:
                    nums.append(0.0)
                lay.tiles.append(Tile(
                    name=key.strip(), kind=kind.lower(), raw_kind=kind,
                    x=nums[0], y=nums[1], w=nums[2], h=nums[3],
                    extras=parts[5:],
                ))
        out[section] = lay
    return out


def list_layout_names(presets: dict[str, Layout]) -> list[str]:
    order = ["floatpad", "leftpad", "rightpad", "toppad", "bottompad",
             "fullscreen", "fullscreen_with_btns_horz", "ArtistPad",
             "ArtistPad_Medium", "virtualctrls"]
    names = list(presets.keys())
    names.sort(key=lambda n: (order.index(n) if n in order else 99, n))
    return names


# ---- tile classification helpers (mirror original semantics) ----

CLICK_TILES = {"lbtn": "left", "rbtn": "right", "mbtn": "middle",
               "dragframe_lbtn": "left", "dragframe_rbtn": "right",
               "dragframe_mbtn": "middle"}
WHEEL_TILES = {"wheel", "wheel_no_mbtn", "hwheel"}
FRAME_TILES = {"padframe"}
GRIP_TILES = {"movegrip"}
SYS_TILES = {"menu", "minimize", "closebtn", "blank"}

VK_ALIASES = {
    "vk_control": 0x11, "vk_shift": 0x10, "vk_menu": 0x12, "vk_space": 0x20,
    "vk_return": 0x0D, "vk_back": 0x08, "vk_tab": 0x09, "vk_escape": 0x1B,
    "vk_left": 0x25, "vk_up": 0x26, "vk_right": 0x27, "vk_down": 0x28,
    "vk_f1": 0x70, "vk_f2": 0x71, "vk_f3": 0x72, "vk_f4": 0x73,
    "vk_f5": 0x74, "vk_f6": 0x75, "vk_f7": 0x76, "vk_f8": 0x77,
    "vk_f9": 0x78, "vk_f10": 0x79, "vk_f11": 0x7A, "vk_f12": 0x7B,
    "vk_lwin": 0x5B, "vk_rwin": 0x5C,
    "vk_numpad0": 0x60, "vk_numpad1": 0x61, "vk_numpad2": 0x62,
    "vk_numpad3": 0x63, "vk_numpad4": 0x64, "vk_numpad5": 0x65,
    "vk_numpad6": 0x66, "vk_numpad7": 0x67, "vk_numpad8": 0x68,
    "vk_numpad9": 0x69, "vk_multiply": 0x6A, "vk_add": 0x6B,
    "vk_subtract": 0x6D, "vk_decimal": 0x6E, "vk_divide": 0x6F,
    "vk_lcontrol": 0xA2, "vk_rcontrol": 0xA3, "vk_lshift": 0xA0,
    "vk_rshift": 0xA1, "vk_lmenu": 0xA4, "vk_rmenu": 0xA5,
    "vk_oem_5": 0xDC, "vk_oem_plus": 0xBB, "vk_oem_comma": 0xBC,
    "vk_oem_minus": 0xBD, "vk_oem_period": 0xBE,
    "vk_media_play_pause": 0xB3, "vk_media_next_track": 0xB0,
    "vk_media_prev_track": 0xB1, "vk_volume_mute": 0xAD,
    "vk_volume_down": 0xAE, "vk_volume_up": 0xAF,
    "vk_home": 0x24, "vk_end": 0x23, "vk_prior": 0x21, "vk_next": 0x22,
    "vk_insert": 0x2D, "vk_delete": 0x2E,
}

# style flag observed in the wild: extras[0] == "7" accompanies
# Ctrl-combos (C/V/X/Z/A = copy/paste/cut/undo/select-all in ArtistPad).
_FLAG_CTRL = {"7"}


def key_combo(tile: Tile) -> tuple[int | None, bool, bool, bool]:
    """Return (vk_or_charcode, ctrl, shift, alt) for a key tile.

    - extras may contain URL-encoded modifier words, e.g. 'shift%20alt'.
    - extras[0] == '7' is the original's Ctrl-combo style flag.
    """
    from urllib.parse import unquote
    ctrl = shift = alt = False
    vk: int | None = None
    k = tile.kind
    if k in VK_ALIASES:
        vk = VK_ALIASES[k]
    elif len(tile.raw_kind) == 1 and tile.raw_kind.isalnum():
        vk = ord(tile.raw_kind.upper())
    for ex in tile.extras:
        low = unquote(ex).lower().replace("+", " ").replace(",", " ")
        if "ctrl" in low or "control" in low:
            ctrl = True
        if "shift" in low:
            shift = True
        if low.strip() == "alt" or " alt" in low or "alt " in low:
            alt = True
    if tile.extras and tile.extras[0].strip() in _FLAG_CTRL:
        ctrl = True
    return vk, ctrl, shift, alt


def tile_action(tile: Tile) -> tuple[str, object]:
    """Return (action, payload): click/dragframe/wheel/pad/key/grip/sys/frame/blank."""
    k = tile.kind
    if k in CLICK_TILES:
        drag = "dragframe" in k
        return ("drag" if drag else "click", CLICK_TILES[k])
    if k in WHEEL_TILES:
        return ("wheel", "h" if k == "hwheel" else "v")
    if k == "pad":
        return ("pad", None)
    if k in FRAME_TILES:
        return ("frame", None)
    if k in GRIP_TILES:
        return ("grip", None)
    if k in SYS_TILES:
        return ("sys", k)
    if k in ("assistpad", "tabtip"):
        return ("sys", k)
    if k in VK_ALIASES:
        return ("key", VK_ALIASES[k])
    if len(tile.raw_kind) == 1 and tile.raw_kind.isalnum():
        return ("key", ord(tile.raw_kind.upper()))
    if k.startswith("vk_"):
        return ("key", None)
    return ("blank", None)
