"""vJoy virtual-joystick install guidance.

The original TouchMousePointer ships VJoy64.dll (Shaul Efraim vJoy SDK,
2013) so that the "WASD gaming" preset can feed a virtual joystick device.
vJoy is a *driver* - it cannot be bundled, it must be installed separately
with its signed installer. This module only detects presence and points the
user at the official download pages (same links the original installer help
refers to).
"""
from __future__ import annotations

import webbrowser

# Official vJoy project pages (Shaul Efraim, MIT license).
VJOY_SITE = "http://vjoystick.sourceforge.net"
VJOY_DOWNLOAD = "https://sourceforge.net/projects/vjoystick/files/"
VJOY_GITHUB = "https://github.com/shauleiz/vJoy"
# Maintained fork for newer Windows 10/11 builds (original 2.1.9.1 predates Win11).
VJOY_WIN11_FORK = "https://github.com/jshafer817/vJoy"


def is_installed() -> bool:
    """True if the vJoy driver/service is present on this machine."""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                            r"SYSTEM\CurrentControlSet\Services\vjoy"):
            return True
    except Exception:
        pass
    try:
        import ctypes
        # vJoyInterface DLL exposes GetvJoyVersion; present only with driver pkg.
        for name in ("vJoyInterface.dll", "vJoyInterface64.dll"):
            try:
                lib = ctypes.WinDLL(name)
                if hasattr(lib, "GetvJoyVersion"):
                    return True
            except Exception:
                continue
    except Exception:
        pass
    return False


def open_download_page(which: str = "download") -> None:
    pages = {"site": VJOY_SITE, "download": VJOY_DOWNLOAD,
             "github": VJOY_GITHUB, "win11": VJOY_WIN11_FORK}
    webbrowser.open(pages.get(which, VJOY_DOWNLOAD))
