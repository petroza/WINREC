"""Entry point: python -m winrec  or  winrec  (after pip install)."""
import os
import sys
import traceback
from datetime import datetime

_LOG_DIR = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "WINREC")
_LOG_FILE = os.path.join(_LOG_DIR, "crash.log")


def _log_crash(exc: BaseException) -> str:
    os.makedirs(_LOG_DIR, exist_ok=True)
    text = (
        f"[{datetime.now().isoformat()}]\n"
        f"Python: {sys.version}\n"
        f"{traceback.format_exc()}\n"
    )
    try:
        with open(_LOG_FILE, "a", encoding="utf-8") as f:
            f.write(text + "\n")
    except Exception:
        pass
    return text


def _show_error(text: str) -> None:
    try:
        import tkinter as tk
        from tkinter import messagebox
        root = tk.Tk()
        root.withdraw()
        messagebox.showerror(
            "WINREC — startup error",
            f"WINREC failed to start.\n\nDetails saved to:\n{_LOG_FILE}\n\n{text[:800]}"
        )
        root.destroy()
    except Exception:
        print(text, file=sys.stderr)


def main():
    try:
        # Apply per-monitor DPI awareness on Windows 10/11 (best-effort)
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(2)
        except Exception:
            pass

        from .gui import App
        app = App()
        app.mainloop()

    except Exception as exc:  # noqa: BLE001 — top-level guard, must not leak silently
        text = _log_crash(exc)
        _show_error(text)
        sys.exit(1)


if __name__ == "__main__":
    main()
