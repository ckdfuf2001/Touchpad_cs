"""System mouse/keyboard control via Win32 SendInput (no driver, like the original)."""
from __future__ import annotations

import ctypes
from ctypes import wintypes

user32 = ctypes.windll.user32

MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010
MOUSEEVENTF_MIDDLEDOWN = 0x0020
MOUSEEVENTF_MIDDLEUP = 0x0040
MOUSEEVENTF_WHEEL = 0x0800
MOUSEEVENTF_HWHEEL = 0x1000

KEYEVENTF_KEYUP = 0x0002

_BUTTON = {
    "left": (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    "right": (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
    "middle": (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
}


def get_cursor() -> tuple[int, int]:
    pt = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def move_relative(dx: int, dy: int) -> None:
    if dx == 0 and dy == 0:
        return
    user32.mouse_event(MOUSEEVENTF_MOVE, int(dx), int(dy), 0, 0)


def click(button: str = "left") -> None:
    down, up = _BUTTON.get(button, _BUTTON["left"])
    user32.mouse_event(down, 0, 0, 0, 0)
    user32.mouse_event(up, 0, 0, 0, 0)


def button_down(button: str = "left") -> None:
    user32.mouse_event(_BUTTON.get(button, _BUTTON["left"])[0], 0, 0, 0, 0)


def button_up(button: str = "left") -> None:
    user32.mouse_event(_BUTTON.get(button, _BUTTON["left"])[1], 0, 0, 0, 0)


def wheel(delta: int, horizontal: bool = False) -> None:
    user32.mouse_event(MOUSEEVENTF_HWHEEL if horizontal else MOUSEEVENTF_WHEEL,
                       0, 0, int(delta), 0)


def tap_key(vk: int | None, char: str | None = None) -> None:
    if vk:
        user32.keybd_event(vk, 0, 0, 0)
        user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    elif char:
        user32.keybd_event(0, 0, 0, 0)  # noop guard


def tap_combo(vk: int, ctrl: bool = False, shift: bool = False, alt: bool = False) -> None:
    """Press a key with optional Ctrl/Shift/Alt modifiers (ArtistPad combos, WASD extras)."""
    mods = []
    if ctrl:
        mods.append(0x11)
    if shift:
        mods.append(0x10)
    if alt:
        mods.append(0x12)
    for m in mods:
        user32.keybd_event(m, 0, 0, 0)
    user32.keybd_event(vk, 0, 0, 0)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    for m in reversed(mods):
        user32.keybd_event(m, 0, KEYEVENTF_KEYUP, 0)


def hold_key(vk: int, down: bool) -> None:
    user32.keybd_event(vk, 0, 0 if down else KEYEVENTF_KEYUP, 0)
