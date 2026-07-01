"""Screen recording engine: mss capture → PyAV H.264/AAC → MP4."""
from __future__ import annotations
import threading
import time
import queue
from fractions import Fraction
from typing import Optional, Callable
from dataclasses import dataclass, field

import mss
import av
import numpy as np

try:
    import soundcard as sc
    _HAS_SOUNDCARD = True
except Exception:
    _HAS_SOUNDCARD = False

from .sources import DisplaySource, WindowSource


@dataclass
class RecordingConfig:
    output_path: str
    source: DisplaySource | WindowSource
    region: Optional[dict] = None   # {"left","top","width","height"} in screen px
    fps: int = 30
    video_bitrate: int = 4_000_000
    codec: str = "h264"          # "h264" or "h265"
    capture_system_audio: bool = True
    capture_mic: bool = False
    sample_rate: int = 44100
    channels: int = 2


class Recorder:
    def __init__(self, config: RecordingConfig):
        self.config = config
        self._stop = threading.Event()
        self._error: Optional[Exception] = None
        self._mux_lock = threading.Lock()
        self._container: Optional[av.container.OutputContainer] = None
        self._video_stream = None
        self._audio_out_stream = None
        self._start_mono: float = 0.0
        self.on_complete: Optional[Callable[[str], None]] = None
        self.on_error: Optional[Callable[[str], None]] = None

    # ── Public API ─────────────────────────────────────────────────────────────

    def start(self) -> None:
        cfg = self.config
        self._stop.clear()
        self._error = None
        self._start_mono = time.monotonic()

        # Resolve capture region
        mon = self._resolve_monitor()

        # Open output container
        self._container = av.open(cfg.output_path, "w", format="mp4")

        w = mon["width"] & ~1   # must be even for yuv420p
        h = mon["height"] & ~1

        # Video stream
        codec_name = "hevc" if cfg.codec == "h265" else "h264"
        try:
            vs = self._container.add_stream(codec_name, rate=cfg.fps)
        except Exception:
            # Fall back to H.264 if the HEVC encoder isn't available (e.g. no libx265)
            codec_name = "h264"
            vs = self._container.add_stream(codec_name, rate=cfg.fps)
        vs.width = w
        vs.height = h
        vs.pix_fmt = "yuv420p"
        vs.options = {
            "preset": "ultrafast",
            "tune": "zerolatency",
            "crf": "23",
        }
        if cfg.video_bitrate:
            vs.bit_rate = cfg.video_bitrate
        self._video_stream = vs

        # Audio stream
        need_audio = _HAS_SOUNDCARD and (cfg.capture_system_audio or cfg.capture_mic)
        if need_audio:
            aus = self._container.add_stream("aac", rate=cfg.sample_rate)
            aus.layout = "stereo"   # sets channels implicitly (PyAV 12+)
            self._audio_out_stream = aus

        # Start threads
        self._video_thread = threading.Thread(
            target=self._video_loop, args=(mon, w, h), daemon=True)
        self._video_thread.start()

        if need_audio:
            self._audio_thread = threading.Thread(
                target=self._audio_loop, daemon=True)
            self._audio_thread.start()

    def stop(self) -> None:
        self._stop.set()
        if hasattr(self, "_video_thread"):
            self._video_thread.join(timeout=5)
        if hasattr(self, "_audio_thread"):
            self._audio_thread.join(timeout=5)
        self._finalize()

    # ── Internal ───────────────────────────────────────────────────────────────

    def _resolve_monitor(self) -> dict:
        cfg = self.config
        if cfg.region:
            return cfg.region
        if isinstance(cfg.source, DisplaySource):
            return cfg.source.as_mss_monitor()
        if isinstance(cfg.source, WindowSource):
            rect = cfg.source.get_rect()
            if rect:
                return rect
        # fallback: primary monitor
        with mss.mss() as sct:
            m = sct.monitors[1]
        return {"left": m["left"], "top": m["top"],
                "width": m["width"], "height": m["height"]}

    def _video_loop(self, mon: dict, w: int, h: int) -> None:
        cfg = self.config
        frame_dur = 1.0 / cfg.fps
        time_base = Fraction(1, cfg.fps)
        pts = 0

        try:
            with mss.mss() as sct:
                while not self._stop.is_set():
                    t0 = time.monotonic()

                    # Dynamic rect for window sources (window may move)
                    capture_mon = mon
                    if isinstance(cfg.source, WindowSource) and not cfg.region:
                        rect = cfg.source.get_rect()
                        if rect:
                            capture_mon = rect

                    raw = np.array(sct.grab(capture_mon))  # BGRA uint8
                    raw = raw[:h, :w, :]                   # crop to even size

                    frame = av.VideoFrame.from_ndarray(raw[:, :, :3], format="bgr24")
                    frame = frame.reformat(format="yuv420p")
                    frame.pts = pts
                    frame.time_base = time_base
                    pts += 1

                    with self._mux_lock:
                        for pkt in self._video_stream.encode(frame):
                            self._container.mux(pkt)

                    elapsed = time.monotonic() - t0
                    wait = frame_dur - elapsed
                    if wait > 0:
                        time.sleep(wait)

            # Flush
            with self._mux_lock:
                for pkt in self._video_stream.encode(None):
                    self._container.mux(pkt)

        except Exception as exc:
            self._error = exc
            self._stop.set()

    def _audio_loop(self) -> None:
        cfg = self.config
        chunk = 1024
        time_base = Fraction(1, cfg.sample_rate)
        pts = 0

        try:
            device = None
            if cfg.capture_system_audio:
                loopbacks = [m for m in sc.all_microphones(include_loopback=True)
                             if m.isloopback]
                if loopbacks:
                    device = loopbacks[0]
            if device is None and cfg.capture_mic:
                device = sc.default_microphone()
            if device is None:
                return

            with device.recorder(samplerate=cfg.sample_rate,
                                  channels=cfg.channels,
                                  blocksize=chunk) as rec:
                while not self._stop.is_set():
                    data = rec.record(numframes=chunk)      # float32 (frames, channels)
                    data = np.clip(data, -1.0, 1.0)
                    data = (data * 32767).astype(np.int16)  # → int16

                    aframe = av.AudioFrame.from_ndarray(
                        data.T,                             # (channels, samples)
                        format="s16",
                        layout="stereo")
                    aframe.sample_rate = cfg.sample_rate
                    aframe.pts = pts
                    aframe.time_base = time_base
                    pts += chunk

                    with self._mux_lock:
                        for pkt in self._audio_out_stream.encode(aframe):
                            self._container.mux(pkt)

            with self._mux_lock:
                for pkt in self._audio_out_stream.encode(None):
                    self._container.mux(pkt)

        except Exception as exc:
            # Audio failure is non-fatal — log and continue
            print(f"[WINREC] audio error: {exc}")

    def _finalize(self) -> None:
        if self._container:
            try:
                self._container.close()
            except Exception:
                pass
            self._container = None

        if self._error and self.on_error:
            self.on_error(str(self._error))
        elif self.on_complete:
            self.on_complete(self.config.output_path)
