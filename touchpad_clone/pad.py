"""Floating virtual touchpad window (tkinter, topmost, translucent).

Renders the active INI Layout section as tiles on a Canvas:
  pad -> relative cursor move (+tap=click, double-tap, swipe->wheel/nav)
  lbtn/rbtn/mbtn (+dragframe_*) -> click / press-and-hold drag
  wheel/hwheel -> vertical/horizontal scroll by drag
  VK_*/single-letter -> tap_key / hold while pressed
  movegrip -> drag window, menu -> settings, minimize/closebtn -> hide/close
"""
from __future__ import annotations

import math
import time
import tkinter as tk

from . import mouse
from .config import Settings
from .layout import Layout, tile_action

TAP_MS = 220
TAP_PX = 12
# Fuse: no single event may fling the cursor further than this, no matter
# what gain/exponent is configured. Bounds any future feedback divergence.
MAX_STEP_PX = 256


def _debug_log(msg: str) -> None:
    """File log only when TOUCHPAD_DEBUG=1 (input-event tracing)."""
    import os
    if os.environ.get("TOUCHPAD_DEBUG") != "1":
        return
    try:
        base = os.environ.get("TEMP") or os.environ.get("TMP") or "."
        with open(os.path.join(base, "touchpad_debug.log"), "a",
                  encoding="utf-8") as f:
            f.write(f"{time.strftime('%H:%M:%S')} {msg}\n")
    except Exception:
        pass


class PadWindow:
    def __init__(self, settings: Settings, get_layout, callbacks=None):
        self.s = settings
        self.get_layout = get_layout          # () -> Layout
        self.cb = callbacks or {}             # on_settings, on_hide, on_assist, on_fullscreen
        self.root = tk.Tk()
        self.root.title("TouchPad Clone")
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        try:
            self.root.attributes("-alpha", float(settings.opacity))
        except Exception:
            pass
        w, h = int(settings.pad_width), int(settings.pad_height)
        x = self.root.winfo_screenwidth() - w - 40
        y = self.root.winfo_screenheight() - h - 120
        self.root.geometry(f"{w}x{h}+{x}+{y}")
        if settings.fullscreen:
            self._go_fullscreen()
        self.canvas = tk.Canvas(self.root, highlightthickness=0, bg="#1b1e24")
        self.canvas.pack(fill="both", expand=True)
        self._rects = {}   # item -> Tile
        self._press = None  # (tile, x, y, t, moved, last_x, last_y, hold_button)
        self._tap_count = 0
        self._last_tap_t = 0.0
        self.canvas.bind("<ButtonPress-1>", self._on_press)
        # NOTE: most stacks deliver drags as <B1-Motion>; some touch stacks
        # deliver plain <Motion> instead - or BOTH for the same movement
        # (measured ~2x cursor speed from double delivery). So Motion is only
        # a fallback: it is ignored while a B1-Motion stream is alive.
        self.canvas.bind("<B1-Motion>", lambda e: self._on_drag(e, "b1"))
        self.canvas.bind("<Motion>", lambda e: self._on_drag(e, "m"))
        self._last_b1_t = 0.0
        self.canvas.bind("<ButtonRelease-1>", self._on_release)
        self.canvas.bind("<ButtonPress-3>", lambda e: mouse.click("right"))
        self.canvas.bind("<Configure>", lambda e: self.render())
        self.render()

    # ---------- layout / rendering ----------
    def current_layout(self) -> Layout:
        return self.get_layout()

    def render(self):
        c = self.canvas
        c.delete("all")
        self._rects.clear()
        W = c.winfo_width() or self.s.pad_width
        H = c.winfo_height() or self.s.pad_height
        lay = self.current_layout()
        palette = {
            "pad": ("#242932", "trackpad  ·  drag=move  tap=click"),
            "frame": ("#10131a", ""),
            "click": ("#2e6be6", ""),
            "wheel": ("#3a4150", "wheel"),
            "key": ("#313845", ""),
            "grip": ("#454e60", "⋮⋮"),
            "sys": ("#3a3f4b", ""),
            "blank": ("#242932", ""),
        }
        for t in lay.tiles:
            x0, y0 = t.x / 100 * W, t.y / 100 * H
            x1, y1 = (t.x + t.w) / 100 * W, (t.y + t.h) / 100 * H
            act, payload = tile_action(t)
            base = {"pad": "pad", "frame": "frame", "click": "click", "drag": "click",
                    "wheel": "wheel", "key": "key", "grip": "grip",
                    "sys": "sys"}.get(act, "blank")
            fill, hint = palette[base]
            if act == "frame":
                c.create_rectangle(x0, y0, x1, y1, fill=fill, outline="#5a6478")
                continue
            label = self._tile_label(t, act, payload, hint)
            rect = c.create_rectangle(x0, y0, x1, y1, fill=fill,
                                      outline="#7c8aa5" if base in ("click", "key") else "#4a5265")
            txt = c.create_text((x0 + x1) / 2, (y0 + y1) / 2, text=label,
                                fill="#e8ecf4", font=("Segoe UI", max(8, int(min(t.w, t.h) * 0.28))),
                                width=max(10, x1 - x0 - 6))
            self._rects[rect] = t
            self._rects[txt] = t

        # redraw fake cursor hint if enabled handled by overlay; keep title bar hint
        c.create_text(8, 8, anchor="nw", text=f"{lay.name}  ({int(self.s.speed*10)/10}x)",
                      fill="#9aa6bd", font=("Segoe UI", 8))
        # live input-event readout (bottom-left): proves at a glance which
        # tile/action each touch produces. Updated by _status().
        self._status_item = c.create_text(8, H - 8, anchor="sw", text="ready",
                                          fill="#7fe0a8", font=("Segoe UI", 8))

    def _status(self, msg: str) -> None:
        _debug_log(msg)
        try:
            self.canvas.itemconfig(self._status_item, text=msg[-90:])
        except Exception:
            pass

    @staticmethod
    def _tile_label(t, act, payload, hint):
        rk = t.raw_kind
        if act == "click":
            return {"left": "◀ Left", "right": "Right ▶", "middle": "● Mid"}.get(payload, rk)
        if act == "drag":
            return f"⠿ {payload} drag"
        if act == "wheel":
            return "⟳ Wheel" if payload == "v" else "⟷ H-Wheel"
        if act == "key":
            m = {"0x11": "Ctrl", "0x10": "Shift", "0x12": "Alt", "0x20": "Space",
                 "0x0D": "Enter", "0x08": "⌫", "0x09": "Tab", "0x1B": "Esc"}
            if isinstance(payload, int) and str(payload) in m:
                return m[str(payload)]
            if isinstance(payload, int) and payload < 256 and chr(payload).isprintable():
                return chr(payload)
            return rk.replace("VK_", "")
        if act == "grip":
            return "⠿ move"
        if act == "sys":
            return {"menu": "☰", "minimize": "–", "closebtn": "✕",
                    "assistpad": "Ctrl+", "tabtip": "⌨"}.get(payload, rk)
        if act == "pad":
            return hint
        return ""

    def _tile_at(self, x, y):
        lay = self.current_layout()
        W = self.canvas.winfo_width() or self.s.pad_width
        H = self.canvas.winfo_height() or self.s.pad_height
        pad_hit = None
        top_hit = None
        for t in lay.tiles:  # later tiles on top (original z-order)
            x0, y0 = t.x / 100 * W, t.y / 100 * H
            x1, y1 = (t.x + t.w) / 100 * W, (t.y + t.h) / 100 * H
            if x0 <= x <= x1 and y0 <= y <= y1:
                act, _ = tile_action(t)
                if act == "frame":
                    continue
                # 'pad' is the transparent background layer: real tiles
                # (buttons/keys/wheel/…) always win over it, like original.
                if act == "pad":
                    pad_hit = t
                else:
                    top_hit = t
        return top_hit if top_hit is not None else pad_hit

    # ---------- input handling ----------
    def _emit_click(self, button: str = "left") -> None:
        """Click, unless the cursor is over our own pad window.

        A synthetic click landing on the pad would re-enter as a new tap
        (~1ms press/release flood measured in the wild) and self-sustain
        forever, chopping every drag and spraying clicks system-wide.
        """
        try:
            cx, cy = mouse.get_cursor()
            rx, ry = self.root.winfo_rootx(), self.root.winfo_rooty()
            rw, rh = self.root.winfo_width(), self.root.winfo_height()
            if rx <= cx <= rx + rw and ry <= cy <= ry + rh:
                self._status("self-click suppressed (cursor over pad)")
                return
        except Exception:
            pass
        mouse.click(button)

    def _on_press(self, e):
        now = time.time()
        if now - getattr(self, "_last_press_t", 0) < 0.010:
            # Anti-storm floor: no human taps at 100Hz. The only source of
            # sub-10ms press spacing is our own synthetic click re-entering
            # the pad (see _emit_click). Drop it before it sets _press.
            return
        self._last_press_t = now
        t = self._tile_at(e.x, e.y)
        self._press = {"tile": t, "x0": e.x, "y0": e.y, "lx": e.x, "ly": e.y,
                       "t0": time.time(), "moved": False, "hold": None, "wheel_acc": 0.0,
                       # Own-motion feedback compensation (see _on_drag): the
                       # last displacement WE applied, not yet seen by an event.
                       "ox": 0, "oy": 0}
        if t is None:
            self._status(f"press @{e.x},{e.y}: no tile")
            return
        act, payload = tile_action(t)
        self._status(f"press {t.raw_kind} -> {act} {payload}")
        if act in ("click", "drag"):
            btn = payload
            if self.s.swap_buttons and btn in ("left", "right"):
                btn = "right" if btn == "left" else "left"
            mouse.button_down(btn)
            self._press["hold"] = btn
        elif act == "key":
            from .layout import key_combo
            vk, ctrl, shift, alt = key_combo(t)
            if vk is None:
                return
            mods = []
            if ctrl:
                mods.append(0x11)
            if shift:
                mods.append(0x10)
            if alt:
                mods.append(0x12)
            for m in mods:
                mouse.hold_key(m, True)
            mouse.hold_key(vk, True)
            self._press["hold"] = ("combo", vk, mods)

    def _on_drag(self, e, src="b1"):
        p = self._press
        if not p or p["tile"] is None:
            return
        now0 = time.time()
        if src == "b1":
            self._last_b1_t = now0
        elif now0 - self._last_b1_t < 0.12:
            return  # B1 stream alive: this Motion is a duplicate delivery
        # Hover (<Motion> with no button held) must not move the cursor.
        # B1-Motion and in-contact touch drags always carry the Button1
        # mask (0x100), so this only filters true hover events.
        if not (getattr(e, "state", 0x100) & 0x100):
            return
        t = p["tile"]
        act, payload = tile_action(t)
        dx, dy = e.x - p["lx"], e.y - p["ly"]
        p["lx"], p["ly"] = e.x, e.y
        if abs(e.x - p["x0"]) + abs(e.y - p["y0"]) > TAP_PX:
            p["moved"] = True
        if act == "pad" and (dx or dy):
            # --- Own-motion feedback compensation ---
            # Event coords track the OS cursor, which OUR last move_relative
            # already displaced by (ox, oy). The raw delta therefore contains
            # our own fling on top of the finger's real motion:
            #   raw = finger + own_last
            # With gain > 1 this is positive feedback: each fling inflates
            # the next delta, the cursor slams into screen corners, edge
            # clamping flips the sign, and the cursor ping-pongs corner to
            # corner (measured: exact repeating full-screen diagonals).
            # Subtract our outstanding displacement to recover the finger.
            # Consume-once: zero (ox, oy) as we read it. A duplicated or
            # extra message reporting an already-seen position must NOT
            # subtract the same displacement twice (that injected phantom
            # kicks of -ox from zero-motion events and self-sustained).
            # Coalesced messages may then under-subtract slightly; the fuse
            # below bounds that residue instead of the corrector exploding.
            fx, fy = dx - p["ox"], dy - p["oy"]
            p["ox"], p["oy"] = 0, 0
            k = self.s.speed
            ax = math.copysign(abs(fx * k) ** self.s.acceleration if self.s.acceleration != 1 else abs(fx * k), fx) if fx else 0
            ay = math.copysign(abs(fy * k) ** self.s.acceleration if self.s.acceleration != 1 else abs(fy * k), fy) if fy else 0
            # acceleration=1 fast path keeps linearity
            if self.s.acceleration == 1:
                ax, ay = fx * k, fy * k
            ax = max(-MAX_STEP_PX, min(MAX_STEP_PX, int(ax)))
            ay = max(-MAX_STEP_PX, min(MAX_STEP_PX, int(ay)))
            p["ox"], p["oy"] = ax, ay
            mouse.move_relative(ax, ay)
            _debug_log(f"move {t.raw_kind}: d=({dx},{dy}) -> ({int(ax)},{int(ay)})")
            now = time.time()
            if now - getattr(self, "_last_status_t", 0) > 0.15:
                self._last_status_t = now
                try:
                    self.canvas.itemconfig(
                        self._status_item,
                        text=f"move {t.raw_kind}: d=({dx},{dy}) -> ({int(ax)},{int(ay)})"[-90:])
                except Exception:
                    pass
            if self.cb.get("on_move"):
                self.cb["on_move"]()
        elif act == "wheel" and (dx or dy):
            step = self.s.wheel_step / 4
            if payload == "h":
                p["wheel_acc"] += dx * step / 8
            else:
                p["wheel_acc"] += -dy * step / 8
            while abs(p["wheel_acc"]) >= self.s.wheel_step:
                d = self.s.wheel_step if p["wheel_acc"] > 0 else -self.s.wheel_step
                mouse.wheel(d, horizontal=(payload == "h"))
                p["wheel_acc"] -= d
        elif act == "grip":
            self.root.geometry(f"+{self.root.winfo_x() + dx}+{self.root.winfo_y() + dy}")

    def _on_release(self, e):
        p = self._press
        self._press = None
        if not p or p["tile"] is None:
            return
        t = p["tile"]
        act, payload = tile_action(t)
        dt = (time.time() - p["t0"]) * 1000
        is_tap = (not p["moved"]) and dt < TAP_MS
        self._status(f"release {t.raw_kind}: {'tap' if is_tap else 'gesture'} {int(dt)}ms")
        if act in ("click", "drag"):
            if p.get("hold"):
                mouse.button_up(p["hold"])
            # tap on lbtn/rbtn tile with quick release already sent down+up = click. nothing extra.
        elif act == "key":
            hold = p.get("hold")
            if isinstance(hold, tuple) and hold[0] == "combo":
                _, vk, mods = hold
                mouse.hold_key(vk, False)
                for m in mods:
                    mouse.hold_key(m, False)
                if is_tap and len(mods) == 0 and vk in (0x11, 0x10, 0x12):
                    mouse.tap_key(vk)  # lone modifier tap still registers
            elif is_tap and isinstance(payload, int):
                mouse.tap_key(payload)
            elif is_tap and len(t.raw_kind) == 1:
                mouse.tap_key(ord(t.raw_kind.upper()))
        elif act == "sys":
            if is_tap or True:
                self._sys_action(payload)
        elif act == "pad":
            if is_tap and self.s.tap_to_click:
                now = time.time()
                if now - self._last_tap_t < 0.35:
                    self._do_gesture(self.s.gestures.double_tap)
                    self._last_tap_t = 0.0
                else:
                    self._emit_click("right" if self.s.swap_buttons else "left")
                    self._last_tap_t = now
                    if self.cb.get("on_tap"):
                        self.cb["on_tap"]()
            elif p["moved"]:
                dist = math.hypot(e.x - p["x0"], e.y - p["y0"])
                if dist > 60:  # swipe
                    self._swipe(e.x - p["x0"], e.y - p["y0"])

    def _swipe(self, dx, dy):
        g = self.s.gestures
        if abs(dx) > abs(dy):
            self._do_gesture(g.swipe_right if dx > 0 else g.swipe_left)
        else:
            self._do_gesture(g.swipe_down if dy > 0 else g.swipe_up)

    def _do_gesture(self, name: str):
        import threading
        if name in ("left_click",):
            self._emit_click("left")
        elif name == "right_click":
            self._emit_click("right")
        elif name == "middle_click":
            self._emit_click("middle")
        elif name == "wheel_up":
            mouse.wheel(self.s.wheel_step)
        elif name == "wheel_down":
            mouse.wheel(-self.s.wheel_step)
        elif name in ("browser_back", "browser_forward"):
            vk = 0xA6 if name == "browser_back" else 0xA7
            mouse.tap_key(vk)
        elif name == "sonar":
            if self.cb.get("on_sonar"):
                self.cb["on_sonar"]()
        elif name == "fullscreen":
            if self.cb.get("on_fullscreen"):
                self.cb["on_fullscreen"]()
        elif name == "shrink":
            self._toggle_shrink()
        # show/hide assist pad etc.
        if name in ("assist", "show_assist"):
            if self.cb.get("on_assist"):
                self.cb["on_assist"]()

    def _sys_action(self, kind):
        if kind == "menu":
            if self.cb.get("on_settings"):
                self.cb["on_settings"]()
        elif kind == "minimize":
            self.hide()
        elif kind == "closebtn":
            self.hide()
        elif kind == "assistpad":
            if self.cb.get("on_assist"):
                self.cb["on_assist"]()
        elif kind == "tabtip":
            import os as _os
            try:
                _os.startfile("TabTip.exe")
            except Exception:
                mouse.tap_key(0x09)

    def _toggle_shrink(self):
        w, h = self.root.winfo_width(), self.root.winfo_height()
        if w > 160:
            self._prev_size = (w, h)
            self.root.geometry(f"140x100+{self.root.winfo_x()}+{self.root.winfo_y()}")
        else:
            w0, h0 = getattr(self, "_prev_size", (self.s.pad_width, self.s.pad_height))
            self.root.geometry(f"{w0}x{h0}+{self.root.winfo_x()}+{self.root.winfo_y()}")
        self.render()

    def _go_fullscreen(self):
        self.root.overrideredirect(False)
        try:
            self.root.state("zoomed")
        except Exception:
            sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
            self.root.geometry(f"{sw}x{sh}+0+0")

    # ---------- external control ----------
    def set_opacity(self, v: float):
        try:
            self.root.attributes("-alpha", float(v))
        except Exception:
            pass

    def hide(self):
        self.root.withdraw()
        if self.cb.get("on_hide"):
            self.cb["on_hide"]()

    def show(self):
        self.root.deiconify()
        self.root.attributes("-topmost", True)
        self.render()

    def run(self):
        self.root.mainloop()
