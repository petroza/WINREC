"""Tkinter GUI for WINREC."""
from __future__ import annotations
import os
import time
import threading
import tkinter as tk
from tkinter import ttk, filedialog, messagebox
from datetime import datetime
from typing import Optional

from .sources import (DisplaySource, WindowSource,
                      get_displays, get_windows, get_hwnd_at_point)
from .recorder import Recorder, RecordingConfig

# ── Dark theme colours ─────────────────────────────────────────────────────────
BG        = "#0F172A"
BG2       = "#1E293B"
BG3       = "#334155"
FG        = "#F1F5F9"
FG_DIM    = "#94A3B8"
ACCENT    = "#2563EB"
DANGER    = "#DC2626"
SUCCESS   = "#22C55E"
FONT      = ("Segoe UI", 10)
FONT_SM   = ("Segoe UI", 9)
FONT_LG   = ("Segoe UI", 14, "bold")
FONT_MONO = ("Consolas", 18, "bold")


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("WINREC")
        self.resizable(False, False)
        self.configure(bg=BG)
        self._recorder: Optional[Recorder] = None
        self._recording = False
        self._selected_region: Optional[dict] = None
        self._timer_thread: Optional[threading.Thread] = None
        self._start_time: float = 0.0
        self._sources: list[DisplaySource | WindowSource] = []

        self._build_ui()
        self._refresh_sources()
        self._set_default_output()

    # ── UI construction ────────────────────────────────────────────────────────

    def _build_ui(self):
        pad = {"padx": 20, "pady": 6}

        # Header
        hdr = tk.Frame(self, bg=BG)
        hdr.pack(fill="x", padx=20, pady=(18, 4))
        self._dot = tk.Label(hdr, text="●", fg=DANGER, bg=BG,
                              font=("Segoe UI", 12))
        self._dot.pack(side="left")
        self._dot.pack_forget()
        tk.Label(hdr, text="WINREC", fg=FG, bg=BG,
                  font=("Segoe UI", 16, "bold")).pack(side="left")
        tk.Label(hdr, text=" — Windows Screen Recorder",
                  fg=FG_DIM, bg=BG, font=("Segoe UI", 10)).pack(side="left")

        sep = tk.Frame(self, bg=BG3, height=1)
        sep.pack(fill="x", padx=20, pady=4)

        # Source type
        self._make_row("Zdroj záznamu", self._build_source_type)
        # Source selector
        self._make_row("Zdroj", self._build_source_selector)
        # Region
        self._make_row("Oblast záznamu", self._build_region)
        # Audio
        self._make_row("Zvuk", self._build_audio)
        # Quality
        self._make_row("Kvalita videa", self._build_quality)
        # Output
        self._make_row("Výstupní soubor", self._build_output)

        # Status bar
        status_frame = tk.Frame(self, bg=BG2)
        status_frame.pack(fill="x", padx=20, pady=(8, 4))
        self._status_lbl = tk.Label(status_frame, text="Připraveno k záznamu.",
                                     fg=FG_DIM, bg=BG2, font=FONT,
                                     anchor="w", padx=10, pady=6)
        self._status_lbl.pack(side="left", fill="x", expand=True)
        self._timer_lbl = tk.Label(status_frame, text="", fg=SUCCESS,
                                    bg=BG2, font=FONT_MONO, padx=10)
        self._timer_lbl.pack(side="right")

        # Buttons
        btn_frame = tk.Frame(self, bg=BG)
        btn_frame.pack(fill="x", padx=20, pady=(4, 16))
        self._start_btn = self._btn(btn_frame, "▶  Spustit záznam",
                                    ACCENT, self._start_recording)
        self._start_btn.pack(side="left", fill="x", expand=True, ipady=8)
        tk.Frame(btn_frame, bg=BG, width=8).pack(side="left")
        self._stop_btn = self._btn(btn_frame, "■  Zastavit",
                                   DANGER, self._stop_recording)
        self._stop_btn.pack(side="right", ipadx=16, ipady=8)
        self._stop_btn.configure(state="disabled")

        self.update_idletasks()
        w = self.winfo_reqwidth()
        self.minsize(w, self.winfo_reqheight())

    def _make_row(self, label: str, builder):
        row = tk.Frame(self, bg=BG)
        row.pack(fill="x", padx=20, pady=3)
        tk.Label(row, text=label, fg=FG_DIM, bg=BG,
                  font=FONT_SM, width=16, anchor="w").pack(side="left")
        builder(row)

    def _build_source_type(self, row):
        self._src_type_var = tk.StringVar(value="display")
        f = tk.Frame(row, bg=BG)
        f.pack(side="left")
        for text, val in [("Celá obrazovka", "display"), ("Okno", "window")]:
            tk.Radiobutton(f, text=text, variable=self._src_type_var,
                           value=val, bg=BG, fg=FG, selectcolor=BG2,
                           activebackground=BG, activeforeground=FG,
                           font=FONT, command=self._refresh_sources).pack(side="left", padx=(0, 12))

    def _build_source_selector(self, row):
        self._src_var = tk.StringVar()
        self._src_combo = ttk.Combobox(row, textvariable=self._src_var,
                                        state="readonly", font=FONT, width=36)
        self._src_combo.pack(side="left")
        self._btn_sm(row, "↺ Obnovit", self._refresh_sources).pack(side="left", padx=(6, 0))
        self._pick_win_btn = self._btn_sm(row, "🎯 Vybrat kliknutím",
                                          self._pick_window)
        self._pick_win_btn.pack(side="left", padx=(6, 0))
        self._pick_win_btn.pack_forget()

    def _build_region(self, row):
        self._region_var = tk.StringVar(value="Celá plocha / celé okno")
        e = tk.Entry(row, textvariable=self._region_var, state="readonly",
                     fg=FG_DIM, bg=BG2, readonlybackground=BG2,
                     relief="flat", bd=1, highlightbackground=BG3,
                     highlightthickness=1, font=FONT_SM, width=34)
        e.pack(side="left")
        self._region_entry = e
        self._btn_sm(row, "Vybrat myší…", self._pick_region).pack(side="left", padx=(6, 0))
        self._clear_region_btn = self._btn_sm(row, "✕", self._clear_region)
        self._clear_region_btn.pack(side="left", padx=(4, 0))
        self._clear_region_btn.configure(state="disabled")

    def _build_audio(self, row):
        self._sys_audio_var = tk.BooleanVar(value=True)
        self._mic_var = tk.BooleanVar(value=False)
        tk.Checkbutton(row, text="Zvuk systému", variable=self._sys_audio_var,
                       bg=BG, fg=FG, selectcolor=BG2,
                       activebackground=BG, activeforeground=FG,
                       font=FONT).pack(side="left")
        tk.Checkbutton(row, text="Mikrofon", variable=self._mic_var,
                       bg=BG, fg=FG, selectcolor=BG2,
                       activebackground=BG, activeforeground=FG,
                       font=FONT).pack(side="left", padx=(12, 0))

    def _build_quality(self, row):
        self._quality_var = tk.StringVar(value="4000")
        f = tk.Frame(row, bg=BG)
        f.pack(side="left")
        for label, val in [("Vysoká (4000)", "4000"),
                            ("Střední (2000)", "2000"),
                            ("Nízká (1000)", "1000")]:
            tk.Radiobutton(f, text=label, variable=self._quality_var,
                           value=val, bg=BG, fg=FG, selectcolor=BG2,
                           activebackground=BG, activeforeground=FG,
                           font=FONT).pack(side="left", padx=(0, 10))

    def _build_output(self, row):
        self._output_var = tk.StringVar()
        e = tk.Entry(row, textvariable=self._output_var,
                     fg=FG, bg=BG2, insertbackground=FG,
                     relief="flat", bd=1, highlightbackground=BG3,
                     highlightthickness=1, font=FONT_SM, width=34)
        e.pack(side="left")
        self._btn_sm(row, "Procházet…", self._browse_output).pack(side="left", padx=(6, 0))

    # ── Widget helpers ─────────────────────────────────────────────────────────

    def _btn(self, parent, text, color, cmd) -> tk.Button:
        return tk.Button(parent, text=text, command=cmd,
                         bg=color, fg="white", activebackground=color,
                         activeforeground="white", relief="flat",
                         font=("Segoe UI", 11, "bold"), cursor="hand2",
                         bd=0)

    def _btn_sm(self, parent, text, cmd) -> tk.Button:
        return tk.Button(parent, text=text, command=cmd,
                         bg=BG3, fg=FG, activebackground=BG2,
                         activeforeground=FG, relief="flat",
                         font=FONT_SM, cursor="hand2", bd=0,
                         padx=8, pady=4)

    # ── Sources ────────────────────────────────────────────────────────────────

    def _refresh_sources(self):
        if self._recording:
            return
        mode = self._src_type_var.get()
        if mode == "display":
            self._sources = get_displays()
            self._pick_win_btn.pack_forget()
        else:
            self._sources = get_windows()
            self._pick_win_btn.pack(side="left", padx=(6, 0))

        names = [str(s) for s in self._sources]
        self._src_combo["values"] = names
        if names:
            self._src_combo.current(0)

    def _get_selected_source(self):
        idx = self._src_combo.current()
        if idx < 0 or idx >= len(self._sources):
            return None
        return self._sources[idx]

    # ── Window picker ──────────────────────────────────────────────────────────

    def _pick_window(self):
        self.withdraw()
        overlay = _PickerOverlay(self)
        self.wait_window(overlay)
        self.deiconify()
        self.lift()
        if overlay.result is None:
            return
        # Find or add to source list
        for i, s in enumerate(self._sources):
            if isinstance(s, WindowSource) and s.hwnd == overlay.result.hwnd:
                self._src_combo.current(i)
                return
        self._sources.insert(0, overlay.result)
        names = [str(s) for s in self._sources]
        self._src_combo["values"] = names
        self._src_combo.current(0)

    # ── Region picker ──────────────────────────────────────────────────────────

    def _pick_region(self):
        overlay = _RegionOverlay(self)
        self.wait_window(overlay)
        self.lift()
        if overlay.result is None:
            return
        r = overlay.result
        self._selected_region = r
        self._region_var.set(f"{r['left']}, {r['top']}  —  {r['width']} × {r['height']} px")
        self._region_entry.configure(fg=SUCCESS)
        self._clear_region_btn.configure(state="normal")

    def _clear_region(self):
        self._selected_region = None
        self._region_var.set("Celá plocha / celé okno")
        self._region_entry.configure(fg=FG_DIM)
        self._clear_region_btn.configure(state="disabled")

    # ── Output ─────────────────────────────────────────────────────────────────

    def _set_default_output(self):
        videos = os.path.join(os.path.expanduser("~"), "Videos")
        os.makedirs(videos, exist_ok=True)
        name = datetime.now().strftime("záznam_%Y-%m-%d_%H-%M-%S.mp4")
        self._output_var.set(os.path.join(videos, name))

    def _browse_output(self):
        path = filedialog.asksaveasfilename(
            defaultextension=".mp4",
            filetypes=[("MP4 video", "*.mp4")],
            initialfile=os.path.basename(self._output_var.get()),
            initialdir=os.path.dirname(self._output_var.get()))
        if path:
            self._output_var.set(path)

    # ── Recording ──────────────────────────────────────────────────────────────

    def _start_recording(self):
        src = self._get_selected_source()
        if src is None:
            messagebox.showwarning("WINREC", "Vyberte zdroj záznamu.")
            return
        output = self._output_var.get().strip()
        if not output:
            messagebox.showwarning("WINREC", "Zadejte výstupní soubor.")
            return

        os.makedirs(os.path.dirname(output), exist_ok=True)

        cfg = RecordingConfig(
            output_path=output,
            source=src,
            region=self._selected_region,
            fps=30,
            video_bitrate=int(self._quality_var.get()) * 1000,
            capture_system_audio=self._sys_audio_var.get(),
            capture_mic=self._mic_var.get(),
        )
        self._recorder = Recorder(cfg)
        self._recorder.on_complete = self._on_complete
        self._recorder.on_error = self._on_error

        try:
            self._recorder.start()
        except Exception as e:
            messagebox.showerror("WINREC — chyba", str(e))
            return

        self._recording = True
        self._set_ui_recording(True)
        import os as _os
        self._status("Nahrávám → " + _os.path.basename(output), SUCCESS)
        self._start_time = time.monotonic()
        self._timer_thread = threading.Thread(target=self._run_timer, daemon=True)
        self._timer_thread.start()

    def _stop_recording(self):
        if self._recorder:
            threading.Thread(target=self._recorder.stop, daemon=True).start()
        self._recording = False
        self._set_ui_recording(False)
        self._timer_lbl.configure(text="")
        self._dot.pack_forget()
        self._status("Záznam dokončen.", FG_DIM)
        self._set_default_output()

    def _on_complete(self, path: str):
        self.after(0, lambda: (
            self._set_ui_recording(False),
            self._timer_lbl.configure(text=""),
            self._dot.pack_forget(),
            self._status(f"Uloženo: {path}", SUCCESS),
            self._set_default_output()
        ))

    def _on_error(self, msg: str):
        self.after(0, lambda: (
            self._set_ui_recording(False),
            self._timer_lbl.configure(text=""),
            self._dot.pack_forget(),
            self._status(f"Chyba: {msg}", DANGER),
            messagebox.showerror("WINREC — chyba záznamu", msg)
        ))

    # ── Timer ──────────────────────────────────────────────────────────────────

    def _run_timer(self):
        blink = True
        while self._recording:
            elapsed = int(time.monotonic() - self._start_time)
            h, r = divmod(elapsed, 3600)
            m, s = divmod(r, 60)
            label = f"{h:02d}:{m:02d}:{s:02d}"
            color = DANGER if blink else BG
            self.after(0, lambda l=label, c=color: (
                self._timer_lbl.configure(text=l),
                self._dot.configure(fg=c),
                self._dot.pack(side="left") if c == DANGER else None
            ))
            blink = not blink
            time.sleep(0.5)

    # ── Helpers ────────────────────────────────────────────────────────────────

    def _set_ui_recording(self, recording: bool):
        self._recording = recording
        state = "disabled" if recording else "normal"
        for w in (self._start_btn, self._src_combo,
                  self._pick_win_btn, self._clear_region_btn):
            try:
                w.configure(state=state)
            except Exception:
                pass
        self._stop_btn.configure(state="normal" if recording else "disabled")

    def _status(self, msg: str, color: str):
        self._status_lbl.configure(text=msg, fg=color)


# ── Picker overlays ────────────────────────────────────────────────────────────

class _PickerOverlay(tk.Toplevel):
    """Fullscreen transparent window — click to pick a window."""

    def __init__(self, parent):
        super().__init__(parent)
        self.result: Optional[WindowSource] = None
        self.attributes("-fullscreen", True)
        self.attributes("-alpha", 0.01)
        self.attributes("-topmost", True)
        self.configure(bg="black", cursor="crosshair")
        self.overrideredirect(True)

        # Hint label
        hint = tk.Toplevel(self)
        hint.overrideredirect(True)
        hint.attributes("-topmost", True)
        hint.configure(bg="#0F172A")
        tk.Label(hint,
                 text="Klikněte na okno, které chcete nahrávat\n(ESC = zrušit)",
                 fg="white", bg="#0F172A", font=("Segoe UI", 14),
                 padx=24, pady=12).pack()
        hint.update_idletasks()
        sw = hint.winfo_screenwidth()
        hw = hint.winfo_reqwidth()
        hint.geometry(f"+{(sw - hw) // 2}+40")
        self._hint = hint

        self.bind("<ButtonRelease-1>", self._on_click)
        self.bind("<Escape>", lambda _: self._close())
        self.focus_force()

    def _on_click(self, event):
        sx = self.winfo_pointerx()
        sy = self.winfo_pointery()
        self._close()
        self.update()
        import time as _t; _t.sleep(0.15)
        src = get_hwnd_at_point(sx, sy)
        self.result = src

    def _close(self):
        try:
            self._hint.destroy()
        except Exception:
            pass
        self.destroy()


class _RegionOverlay(tk.Toplevel):
    """Fullscreen dark overlay — drag to select capture region."""

    def __init__(self, parent):
        super().__init__(parent)
        self.result: Optional[dict] = None
        sw = self.winfo_screenwidth()
        sh = self.winfo_screenheight()
        # Cover virtual desktop (multi-monitor)
        try:
            import ctypes
            user32 = ctypes.windll.user32
            vl = user32.GetSystemMetrics(76)   # SM_XVIRTUALSCREEN
            vt = user32.GetSystemMetrics(77)   # SM_YVIRTUALSCREEN
            vw = user32.GetSystemMetrics(78)   # SM_CXVIRTUALSCREEN
            vh = user32.GetSystemMetrics(79)   # SM_CYVIRTUALSCREEN
        except Exception:
            vl, vt, vw, vh = 0, 0, sw, sh

        self.geometry(f"{vw}x{vh}+{vl}+{vt}")
        self.overrideredirect(True)
        self.attributes("-topmost", True)
        self.attributes("-alpha", 0.55)
        self.configure(bg="#000000", cursor="crosshair")
        self._vl = vl
        self._vt = vt

        self._canvas = tk.Canvas(self, bg="#000000", highlightthickness=0)
        self._canvas.pack(fill="both", expand=True)

        # Hint
        self._canvas.create_text(
            vw // 2, 40,
            text="Táhněte myší pro výběr oblasti  •  ESC = zrušit",
            fill="white", font=("Segoe UI", 14))

        self._sx = self._sy = 0
        self._rect_id = None
        self._size_id = None

        self._canvas.bind("<ButtonPress-1>", self._on_down)
        self._canvas.bind("<B1-Motion>", self._on_move)
        self._canvas.bind("<ButtonRelease-1>", self._on_up)
        self.bind("<Escape>", lambda _: self.destroy())
        self.focus_force()

    def _on_down(self, e):
        self._sx, self._sy = e.x, e.y
        if self._rect_id:
            self._canvas.delete(self._rect_id)
        if self._size_id:
            self._canvas.delete(self._size_id)
        self._rect_id = self._canvas.create_rectangle(
            e.x, e.y, e.x, e.y,
            outline="#22C55E", width=2, fill="")
        self._size_id = self._canvas.create_text(
            e.x + 4, e.y - 14,
            text="", fill="#22C55E",
            font=("Segoe UI", 11), anchor="nw")

    def _on_move(self, e):
        if self._rect_id:
            self._canvas.coords(self._rect_id, self._sx, self._sy, e.x, e.y)
        w = abs(e.x - self._sx)
        h = abs(e.y - self._sy)
        lx = min(e.x, self._sx) + max(w, 0) + 6
        ly = min(e.y, self._sy)
        if self._size_id:
            self._canvas.coords(self._size_id, lx, ly)
            self._canvas.itemconfigure(self._size_id, text=f"{w}×{h}")

    def _on_up(self, e):
        x1, y1 = min(self._sx, e.x), min(self._sy, e.y)
        w, h = abs(e.x - self._sx), abs(e.y - self._sy)
        if w > 16 and h > 16:
            # Convert canvas coords → screen coords
            self.result = {
                "left":   x1 + self._vl,
                "top":    y1 + self._vt,
                "width":  w,
                "height": h,
            }
        self.destroy()
