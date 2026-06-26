"""Screen and window source enumeration (Windows only)."""
from __future__ import annotations
from dataclasses import dataclass
from typing import Optional
import ctypes
import mss

try:
    import win32gui
    import win32con
    _HAS_WIN32 = True
except ImportError:
    _HAS_WIN32 = False


@dataclass
class DisplaySource:
    index: int        # mss monitor index (1-based)
    name: str
    left: int
    top: int
    width: int
    height: int

    def __str__(self) -> str:
        return self.name

    def as_mss_monitor(self) -> dict:
        return {"left": self.left, "top": self.top,
                "width": self.width, "height": self.height}


@dataclass
class WindowSource:
    hwnd: int
    title: str

    def __str__(self) -> str:
        return self.title

    def get_rect(self) -> Optional[dict]:
        if not _HAS_WIN32:
            return None
        try:
            left, top, right, bottom = win32gui.GetWindowRect(self.hwnd)
            return {"left": left, "top": top,
                    "width": right - left, "height": bottom - top}
        except Exception:
            return None


def get_displays() -> list[DisplaySource]:
    sources = []
    with mss.mss() as sct:
        for i, mon in enumerate(sct.monitors[1:], start=1):
            name = f"Monitor {i}  ({mon['width']}×{mon['height']})"
            sources.append(DisplaySource(
                index=i, name=name,
                left=mon["left"], top=mon["top"],
                width=mon["width"], height=mon["height"]))
    return sources


def get_windows() -> list[WindowSource]:
    if not _HAS_WIN32:
        return []
    results = []

    def _cb(hwnd, _):
        if not win32gui.IsWindowVisible(hwnd):
            return True
        title = win32gui.GetWindowText(hwnd)
        if not title or len(title) < 2:
            return True
        style = win32gui.GetWindowLong(hwnd, win32con.GWL_STYLE)
        if not (style & win32con.WS_VISIBLE):
            return True
        results.append(WindowSource(hwnd=hwnd, title=title))
        return True

    win32gui.EnumWindows(_cb, None)
    return results


def get_hwnd_at_point(x: int, y: int) -> Optional[WindowSource]:
    """Return the top-level window at screen position (x, y)."""
    if not _HAS_WIN32:
        return None
    try:
        hwnd = ctypes.windll.user32.WindowFromPoint(ctypes.wintypes.POINT(x, y))
        hwnd = ctypes.windll.user32.GetAncestor(hwnd, 2)  # GA_ROOT
        title = win32gui.GetWindowText(hwnd)
        if hwnd and title:
            return WindowSource(hwnd=hwnd, title=title)
    except Exception:
        pass
    return None
