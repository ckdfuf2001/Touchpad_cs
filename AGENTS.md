# AGENTS.md — working notes for TouchPadCloneV2

Fork/clone of **TouchMousePointer** (`C:\Program Files\TouchMousePointer`, v3.0.1.2,
LovesummerTrue). C# / .NET 10 / WPF, in `cs/TouchPadCloneV2/`.

The single most useful rule from this work: **do not guess what the reference does,
measure it.** Its behaviour that matters is not in readable form (the gesture
semantics live in registry blobs: `GesSetNormal2..5`, `GesSetFull2..5`, `General`,
`AutoSave`, `HKCU\Software\LoveSummerTrue\TouchMousePointer`). Its *layouts* are
readable (`Pad.ini`, `Preset *.ini`) and we already parse all of them.

## Build / test / publish

```bat
cd cs\TouchPadCloneRaw
dotnet build -c Release

REM debug log only (raw log always, v2 detail with the sentinel)
set TOUCHPAD_DEBUG=1
bin\Release\net10.0-windows\TouchPadCloneRaw.exe

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
dist\TouchPadCloneRaw.exe
```

**Testing policy (user's instruction): no selftest - it was deleted (user's order).
The user does the real-device testing.** Verify with device logs
(`%TEMP%\touchpad_raw.log`), never with synthetic handler tests.

Remove `TOUCHPAD_DEBUG` before launching the real app, or it runs
with debug logging on.

## Instruments — measure, don't infer

- `tools/RefInputProbe.ps1` — low-level mouse+keyboard hook, logs every event with
  `dwExtraInfo` to `%TEMP%\ref_probe.log`. `mouse_event`/`SendInput` tag their events
  (`0xFF515700 | tid`) so our injections read `injected` and the reference (which uses
  `mouse_event` with no tag) reads `real`; the physical mouse reads `real` too, so
  compare *patterns* and positions, not the source column.
- `tools/CursorProbe.ps1` — samples `GetCursorInfo` every 50 ms (`0x1` visible,
  `0x2` suppressed) to `%TEMP%\cursor_probe.log`. This is how the "is one real cursor
  possible" question was answered.

Launch them in a window the user can see, tell them exactly which gestures to perform
and for how long, then read the log.

## The reference's event model (measured, ~2026-09)

```
 40375 move   (263,855)          cursor moves, visible (flags 0x1) during touch
 56563 L-down (1601,157)  ... 57750 L-up (1077,700)   press -> move -> drop
 59829 R-down (1076,696) / R-up               two-finger tap = RIGHT CLICK
 67625 wheel -84,-96,-108,-60,60,72,...       two-finger move = proportional wheel
 18875 key 166 (VK_BROWSER_BACK)              two-finger horizontal swipe
```

- **Right click = two-finger tap.** There is no single-finger long-press right click.
- **A held press = drag**: the button goes down on the hold, then moves drag, release
  drops. `SecondHold` default is `"drag"` (`GrabHold(id, pushClick:false)`); `"drag_hold"`
  is the click-then-hold variant (adds one click).
- **Wheel deltas are proportional**, not quantised to 120 (`DoWheel` accumulates
  fractional amounts in separate h/v carries).
- **The real cursor is used and never handed back.** `CURSOR_SUPPRESSED (0x2)` is
  transient: it clears by itself while the finger is still down, so one real cursor is
  viable. `RestorePhysicalCursor` and the re-park timer are therefore no-ops now —
  they used to oscillate the cursor between the aim and the parked physical position on
  every action, which split drags between two windows ("works sometimes and not
  others"). Preserve mode still means the physical mouse does not *steer* the virtual
  cursor; it just no longer re-parks it.

## Open bug

**Only ONE touch contact ever reaches the window from the device** (`WM_POINTER` path):
every `TOUCHDOWN` logs `n=1`, never `n=2`, so two-finger gestures do nothing on real
hardware while the selftest passes. Raw input is registered (`RAWINPUT register …= True`,
`RAWINPUT types: … hid=…` messages arrive) but the HID report parsing never produced a
contact — start there: `Core/RawTouchInput.cs`, and check
`RAWINPUT device caps` / `RAWINPUT contact` lines in `%TEMP%\touchpad_v2.log`.

## Environment / tooling traps (all cost real time)

- **PowerShell 5.1 `Add-Type` compiles C# 5**: no expression-bodied members (`=>`), no
  `Environment.TickCount64`. Use `Environment.TickCount`.
- A script's type reference must match the class name exactly (`[CurProbe]::Run` for
  `class CurProbe`) — a mismatch gives "type not found" with no compile error shown.
- Editing these files with PowerShell `Get-Content`/`Set-Content` **corrupted UTF-8**
  (Korean XAML glyphs) twice. Use the editor tools, or
  `[IO.File]::ReadAllText/WriteAllText(..., UTF8Encoding(false))`.
- The working tree mixes LF and CRLF; `edit` oldString matching can fail on line
  endings. Prefer small unique anchors.
- A stale `.git/index.lock` appears after some commits — delete it before retrying.
- `git add` needs **`-f`**: `.git/info/exclude` contains `/*` (a managed block), so
  everything is ignored; `dist/`, `bin/`, `obj/` must stay out of commits.
- Remote: `https://github.com/ckdfuf2001/Touchpad_cs` (branch `main`).
- `GetCursorInfo` shows `0x2` (suppressed) after a touch; `ShowCursor` cannot clear it,
  and the old restore loop called it 500 times trying, draining the shared counter so
  the pointer could never be hidden again. `RestoreCursor` now gives up on suppressed
  and tries at most 8 times.
