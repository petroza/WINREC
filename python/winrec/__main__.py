"""Entry point: python -m winrec  or  winrec  (after pip install)."""
import sys

def main():
    # Apply dark title bar on Windows 10/11
    try:
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # Per-monitor DPI aware
    except Exception:
        pass

    from .gui import App
    app = App()
    app.mainloop()


if __name__ == "__main__":
    main()
