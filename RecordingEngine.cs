using System.Diagnostics;
using System.IO;
using ScreenRecorderLib;

namespace WINREC;

public sealed class RecordingRequest
{
    public required SourceKind Kind { get; init; }
    public required AppSettings Settings { get; init; }
    public required string OutputPath { get; init; }
    public MonitorInfo? Monitor { get; init; }          // Monitor + Region
    public PxRect Region { get; init; }                 // fyzické px virtuální plochy
    public IntPtr WindowHandle { get; init; }
    public int WindowProcessId { get; init; }
    public string? MicDeviceId { get; init; }
    public string? SystemDeviceId { get; init; }
}

/// <summary>Obal nad ScreenRecorderLib: sestaví volby, řídí záznam a měří čas.</summary>
public sealed class RecordingEngine : IDisposable
{
    private Recorder? _rec;
    private CaptureAudioSource? _micSource;
    private AudioSourceBase? _systemSource;
    private readonly Stopwatch _clock = new();
    private double _micVolume = 1, _systemVolume = 1;
    private bool _micMuted;

    public event Action<RecorderStatus>? StatusChanged;
    public event Action<string>? Completed;
    public event Action<string>? Failed;

    public RecorderStatus Status { get; private set; } = RecorderStatus.Idle;
    public TimeSpan Elapsed => _clock.Elapsed;
    public string? OutputPath { get; private set; }
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public long VideoBitrate { get; private set; }
    public bool HasMic => _micSource != null;
    public bool HasSystemAudio => _systemSource != null;
    /// <summary>Knihovna aspoň jednou ohlásila stav Recording.</summary>
    public bool HasStarted { get; private set; }

    // ── Hlídání, že se opravdu nahrává ──────────────────────────────────────
    private long _lastFrameTick, _lastAudioTick, _micSignalTick, _lastGrowTick, _resumedTick;
    private long _frames, _audioPackets, _lastSize;
    private volatile bool _micIdSeen;
    private string _packetIds = "";

    public long FramesRecorded => Interlocked.Read(ref _frames);
    public long AudioPackets => Interlocked.Read(ref _audioPackets);
    public string DebugAudioIds => $"mic={_micSource?.ID ?? "-"} pakety=[{_packetIds}] micId shoda={_micIdSeen}";

    public sealed record HealthReport(long FileBytes, IReadOnlyList<string> Problems)
    {
        public bool IsOk => Problems.Count == 0;
    }

    private void ResetHealthClock()
    {
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastFrameTick, now);
        Interlocked.Exchange(ref _lastAudioTick, now);
        Interlocked.Exchange(ref _micSignalTick, now);
        _lastGrowTick = now;
        _resumedTick = now;
    }

    /// <summary>Volat zhruba 1× za sekundu. Vrací velikost souboru a seznam problémů.</summary>
    public HealthReport CheckHealth()
    {
        long now = Environment.TickCount64;
        long size = 0;
        try { if (OutputPath != null && File.Exists(OutputPath)) size = new FileInfo(OutputPath).Length; } catch { }
        if (size > _lastSize)
        {
            _lastSize = size;
            _lastGrowTick = now;
        }

        var problems = new List<string>();
        if (Status != RecorderStatus.Recording || now - _resumedTick < 5000) return new(size, problems);

        if (now - Interlocked.Read(ref _lastFrameTick) > 3000)
            problems.Add("Nepřicházejí snímky obrazu!");
        if (now - _lastGrowTick > 20000)
            problems.Add("Soubor na disku neroste!");
        if ((_micSource != null || _systemSource != null) && now - Interlocked.Read(ref _lastAudioTick) > 3000)
            problems.Add("Nepřichází zvuk!");
        if (_micSource != null && !_micMuted && _micVolume >= 0.01 && _micIdSeen && now - Interlocked.Read(ref _micSignalTick) > 10000)
            problems.Add("Mikrofon nedává signál (Mute na mikrofonu?)");
        return new(size, problems);
    }

    private void OnFrame(object? sender, FrameRecordedEventArgs e)
    {
        Interlocked.Exchange(ref _frames, e.FrameNumber);
        Interlocked.Exchange(ref _lastFrameTick, Environment.TickCount64);
    }

    private void OnAudioPacket(object? sender, AudioDataRecordedEventArgs e)
    {
        long now = Environment.TickCount64;
        Interlocked.Increment(ref _audioPackets);
        Interlocked.Exchange(ref _lastAudioTick, now);
        var sources = e.AudioData?.Sources;
        var mic = _micSource;
        if (sources == null || mic == null) return;
        foreach (var src in sources)
        {
            if (_packetIds.Length == 0) _packetIds = string.Join(",", sources.Select(x => x.Id));
            if (src.Id != mic.ID) continue;
            _micIdSeen = true;
            if (src.Gain > 0) Interlocked.Exchange(ref _micSignalTick, now);
        }
    }

    // ── Výpočty ─────────────────────────────────────────────────────────────
    private static int Even(double v) => Math.Max(2, (int)Math.Round(v) & ~1);

    public static (int W, int H) ScaledSize(int srcW, int srcH, OutputScale scale)
    {
        int target = scale switch { OutputScale.P1440 => 1440, OutputScale.P1080 => 1080, OutputScale.P720 => 720, _ => 0 };
        if (target == 0 || srcH <= target) return (Even(srcW), Even(srcH));
        return (Even(srcW * (double)target / srcH), Even(target));
    }

    /// <summary>Datový tok podle počtu pixelů za sekundu (≈ 50 Mbit/s pro 4K60 v režimu Podcast).</summary>
    public static long ComputeBitrate(int w, int h, int fps, QualityPreset q, VideoCodec codec)
    {
        double bitsPerPixel = q switch
        {
            QualityPreset.Podcast => 0.100,
            QualityPreset.High => 0.065,
            QualityPreset.Medium => 0.040,
            _ => 0.022
        };
        long min = q switch
        {
            QualityPreset.Podcast => 10_000_000,
            QualityPreset.High => 6_000_000,
            QualityPreset.Medium => 4_000_000,
            _ => 2_000_000
        };
        if (codec == VideoCodec.H265) bitsPerPixel *= 0.6;
        double bps = Math.Clamp(w * (double)h * fps * bitsPerPixel, min, 120_000_000);
        return (long)(Math.Round(bps / 500_000) * 500_000);
    }

    // ── Záznam ──────────────────────────────────────────────────────────────
    public void Start(RecordingRequest r)
    {
        if (_rec != null) throw new InvalidOperationException("Záznam už běží.");
        var s = r.Settings;
        // Monitor/oblast: Desktop Duplication (výchozí) nebo WGC. Obě metody bez „živé plochy“ (CaptureKeepAlive)
        // zamrzají: při nehybném obrazu knihovna každých 100 snímků vyprázdní enkodér (DD ~0,2 s, WGC při zavření
        // okna ~1 s). S ní obě přesně 60 fps — ověřeno testy K0–K3 a G/H v SelfTest. Okno jde nahrávat jen přes WGC.
        var api = s.UseGraphicsCaptureForScreen ? RecorderApi.WindowsGraphicsCapture : RecorderApi.DesktopDuplication;

        RecordingSourceBase source;
        int srcW, srcH;
        switch (r.Kind)
        {
            case SourceKind.Monitor:
            {
                var m = r.Monitor ?? throw new InvalidOperationException("Není vybrán monitor.");
                source = new DisplayRecordingSource(m.DeviceName)
                {
                    RecorderApi = api,
                    IsBorderRequired = false,
                    IsCursorCaptureEnabled = s.ShowCursor
                };
                (srcW, srcH) = (m.Bounds.Width, m.Bounds.Height);
                break;
            }
            case SourceKind.Region:
            {
                var m = r.Monitor ?? throw new InvalidOperationException("Oblast neleží na žádném monitoru.");
                var reg = r.Region.Intersect(m.Bounds);
                if (reg.Width < 32 || reg.Height < 32) throw new InvalidOperationException("Vybraná oblast je příliš malá.");
                srcW = reg.Width & ~1;   // H.264/H.265 vyžaduje sudé rozměry
                srcH = reg.Height & ~1;
                source = new DisplayRecordingSource(m.DeviceName)
                {
                    RecorderApi = api,
                    IsBorderRequired = false,
                    IsCursorCaptureEnabled = s.ShowCursor,
                    SourceRect = new ScreenRect(reg.Left - m.Bounds.Left, reg.Top - m.Bounds.Top, srcW, srcH)
                };
                break;
            }
            case SourceKind.Window:
            {
                if (r.WindowHandle == IntPtr.Zero || !Native.IsWindow(r.WindowHandle))
                    throw new InvalidOperationException("Vybrané okno už neexistuje. Vyberte ho prosím znovu.");
                if (Native.IsIconic(r.WindowHandle))
                    throw new InvalidOperationException("Vybrané okno je minimalizované. Obnovte ho a spusťte záznam znovu.");
                var b = Native.GetWindowBounds(r.WindowHandle);
                (srcW, srcH) = (b.Width, b.Height);
                source = new WindowRecordingSource(r.WindowHandle)
                {
                    IsBorderRequired = false,
                    IsCursorCaptureEnabled = s.ShowCursor
                };
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(r));
        }

        (OutputWidth, OutputHeight) = ScaledSize(srcW, srcH, s.Scale);
        VideoBitrate = ComputeBitrate(OutputWidth, OutputHeight, s.Fps, s.Quality, s.Codec);

        IVideoEncoder encoder = s.Codec == VideoCodec.H265
            ? new H265VideoEncoder { BitrateMode = H265BitrateControlMode.CBR, EncoderProfile = H265Profile.Main }
            : new H264VideoEncoder { BitrateMode = H264BitrateControlMode.CBR, EncoderProfile = H264Profile.High };

        // Zvuk
        var audioSources = new List<AudioSourceBase>();
        _micSource = null;
        _systemSource = null;
        _micVolume = s.MicVolume;
        _systemVolume = s.SystemVolume;
        _micMuted = false;

        if (s.Audio is AudioMode.SystemOnly or AudioMode.SystemAndMic)
        {
            if (s.AppAudioOnly && r.Kind == SourceKind.Window && r.WindowProcessId > 0)
                _systemSource = new ProcessAudioSource(r.WindowProcessId);
            else if (!string.IsNullOrEmpty(r.SystemDeviceId))
                _systemSource = new LoopbackAudioSource(r.SystemDeviceId);
            else
                _systemSource = LoopbackAudioSource.Default;
            _systemSource.Volume = (float)_systemVolume;
            audioSources.Add(_systemSource);
        }
        if (s.Audio is AudioMode.MicOnly or AudioMode.SystemAndMic)
        {
            if (string.IsNullOrEmpty(r.MicDeviceId))
                throw new InvalidOperationException("Není vybraný mikrofon. Připojte mikrofon nebo přepněte zvuk na „Jen systémový zvuk“.");
            _micSource = new CaptureAudioSource(r.MicDeviceId)
            {
                Volume = (float)_micVolume,
                ForceMono = s.MicMono
            };
            audioSources.Add(_micSource);
        }

        double uiScale = srcH / 1080.0;
        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = { source } },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(OutputWidth, OutputHeight),
                Stretch = StretchMode.Uniform
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = encoder,
                Bitrate = (int)VideoBitrate,
                Framerate = s.Fps,
                // Konstantní fps: Premiere a další střihové programy s proměnnou fps rozhodí synchronizaci zvuku.
                IsFixedFramerate = true,
                IsHardwareEncodingEnabled = true,
                IsFragmentedMp4Enabled = s.CrashSafeMp4,
                IsMp4FastStartEnabled = false,
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioSources.Count > 0,
                AudioSources = audioSources,
                Bitrate = AudioBitrate.bitrate_192kbps,
                Channels = AudioChannels.Stereo
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = s.ShowCursor,
                // Celá obrazovka / oblast: efekty kreslí WINREC sám (ClickEffectsController).
                // Samotné okno: překryvná okna nejsou v záznamu okna vidět → vestavěná tečka knihovny.
                IsMouseClicksDetected = s.HighlightClicks && r.Kind == SourceKind.Window,
                MouseLeftClickDetectionColor = s.ClickColorLeft,
                MouseRightClickDetectionColor = s.ClickColorRight,
                MouseClickDetectionRadius = (int)Math.Round(22 * Math.Max(1, uiScale)),
                MouseClickDetectionDuration = 160,
                MouseClickDetectionMode = MouseDetectionMode.Polling
            },
            LogOptions = new LogOptions
            {
                IsLogEnabled = s.DebugLog,
                LogFilePath = AppPaths.RecorderLogFile,
                LogSeverityLevel = ScreenRecorderLib.LogLevel.Debug
            }
        };
        if (s.DebugLog) Directory.CreateDirectory(AppPaths.LogDir);

        Directory.CreateDirectory(Path.GetDirectoryName(r.OutputPath)!);
        OutputPath = r.OutputPath;
        _clock.Reset();

        Log.Info($"Start: {r.Kind} {OutputWidth}x{OutputHeight}@{s.Fps} {s.Codec} {VideoBitrate / 1_000_000.0:0.#} Mbit/s, " +
                 $"zvuk={s.Audio}, api={api}, fMP4={s.CrashSafeMp4} → {r.OutputPath}");

        HasStarted = false;
        _frames = 0;
        _audioPackets = 0;
        _lastSize = 0;
        _micIdSeen = false;
        _packetIds = "";
        ResetHealthClock();

        _rec = Recorder.CreateRecorder(options);
        _rec.OnStatusChanged += OnStatus;
        _rec.OnFrameRecorded += OnFrame;
        _rec.OnAudioPacketRecorded += OnAudioPacket;
        _rec.OnRecordingComplete += (_, e) =>
        {
            _clock.Stop();
            Log.Info($"Hotovo: {e.FilePath} ({Elapsed})");
            Completed?.Invoke(e.FilePath);
        };
        _rec.OnRecordingFailed += (_, e) =>
        {
            _clock.Stop();
            Log.Error($"Záznam selhal: {e.Error} ({e.FilePath})");
            Failed?.Invoke(string.IsNullOrWhiteSpace(e.Error) ? "Neznámá chyba nahrávání." : e.Error);
        };
        _rec.Record(r.OutputPath);
    }

    private void OnStatus(object? sender, RecordingStatusEventArgs e)
    {
        var previous = Status;
        Status = e.Status;
        if (e.Status == RecorderStatus.Recording)
        {
            HasStarted = true;
            if (previous != RecorderStatus.Recording) ResetHealthClock();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
        StatusChanged?.Invoke(e.Status);
    }

    public void Pause() => _rec?.Pause();
    public void Resume() => _rec?.Resume();
    public void Stop() => _rec?.Stop();

    public bool MicMuted => _micMuted;

    public void SetMicMuted(bool muted)
    {
        if (_rec == null || _micSource == null) return;
        _micMuted = muted;
        _micSource.Volume = muted ? 0f : (float)_micVolume;
        _rec.GetDynamicOptionsBuilder().SetUpdatedAudioSource(_micSource).Apply();
    }

    public void SetVolumes(double system, double mic)
    {
        if (_rec == null) return;
        _systemVolume = system;
        _micVolume = mic;
        var b = _rec.GetDynamicOptionsBuilder();
        if (_systemSource != null) { _systemSource.Volume = (float)system; b.SetUpdatedAudioSource(_systemSource); }
        if (_micSource != null) { _micSource.Volume = _micMuted ? 0f : (float)mic; b.SetUpdatedAudioSource(_micSource); }
        b.Apply();
    }

    public void Dispose()
    {
        try { _rec?.Dispose(); } catch (Exception ex) { Log.Error("Dispose recorderu", ex); }
        _rec = null;
        _micSource = null;
        _systemSource = null;
    }
}
