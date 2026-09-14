using System.IO;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace WINREC;

public sealed record AudioDevice(string Id, string Name, bool IsDefault, bool IsNotRealMic);

/// <summary>
/// Seznam zvukových zařízení (včetně hlídání připojení mikrofonu), měřiče úrovní
/// a volitelné uložení mikrofonu do samostatného WAV pro střih.
/// </summary>
public sealed class AudioHub : IDisposable
{
    private static readonly string[] LoopbackNames =
    [
        "stereo mix", "směšovač stereo", "smesovac stereo", "stereomix", "stereo-mix",
        "what u hear", "wave out mix", "rec. playback", "loopback"
    ];

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private const long MaxWavBytes = 3_900_000_000; // WAV má limit 4 GB

    private readonly MMDeviceEnumerator _enum = new();
    private readonly Notifier _notifier;
    private readonly object _gate = new();

    private WasapiCapture? _mic;
    private string? _micId;
    private MMDevice? _output;
    private string? _outputId;
    private WaveFileWriter? _wav;
    private bool _wavPaused;
    private bool _wavFloat;
    private float _micPeak;

    public event Action? DevicesChanged;
    public string? MicError { get; private set; }
    public string? MicFilePath { get; private set; }

    public AudioHub()
    {
        _notifier = new Notifier(this);
        try { _enum.RegisterEndpointNotificationCallback(_notifier); }
        catch (Exception ex) { Log.Error("Nelze sledovat změny zvukových zařízení", ex); }
    }

    public static bool LooksLikeLoopback(string name) =>
        LoopbackNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    private string? DefaultId(DataFlow flow)
    {
        try
        {
            return _enum.HasDefaultAudioEndpoint(flow, Role.Console)
                ? _enum.GetDefaultAudioEndpoint(flow, Role.Console).ID
                : null;
        }
        catch { return null; }
    }

    private List<AudioDevice> List(DataFlow flow)
    {
        var def = DefaultId(flow);
        var result = new List<AudioDevice>();
        try
        {
            foreach (var d in _enum.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                string name;
                try { name = d.FriendlyName; } catch { name = d.ID; }
                result.Add(new AudioDevice(d.ID, name, d.ID == def, flow == DataFlow.Capture && LooksLikeLoopback(name)));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Výčet zvukových zařízení ({flow}) selhal", ex);
        }
        return result;
    }

    public List<AudioDevice> GetMics() => List(DataFlow.Capture);
    public List<AudioDevice> GetOutputs() => List(DataFlow.Render);

    /// <summary>Nejlepší kandidát na mikrofon: výchozí skutečný mikrofon, jinak první skutečný.</summary>
    public static AudioDevice? PickBestMic(IReadOnlyList<AudioDevice> mics, string? preferredId)
    {
        return mics.FirstOrDefault(m => m.Id == preferredId)
            ?? mics.FirstOrDefault(m => m.IsDefault && !m.IsNotRealMic)
            ?? mics.FirstOrDefault(m => !m.IsNotRealMic);
    }

    // ── Výstup (systémový zvuk) ─────────────────────────────────────────────
    public void SelectOutput(string? id)
    {
        try
        {
            _outputId = id;
            _output = string.IsNullOrEmpty(id)
                ? (_enum.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) ? _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : null)
                : _enum.GetDevice(id);
        }
        catch (Exception ex)
        {
            _output = null;
            Log.Error("Výběr výstupního zařízení", ex);
        }
    }

    public double GetSystemPeak()
    {
        try { return _output?.AudioMeterInformation.MasterPeakValue ?? 0; }
        catch { return 0; }
    }

    // ── Mikrofon ────────────────────────────────────────────────────────────
    /// <summary>Otevře (nebo zavře) mikrofon pro měřič a případný WAV.</summary>
    public void SetMic(string? id, bool active)
    {
        if (!active || string.IsNullOrEmpty(id))
        {
            if (_wav == null) CloseMic();
            return;
        }
        if (_mic != null && _micId == id) return;
        if (_wav != null) return; // během zápisu WAV zařízení neměníme
        CloseMic();

        try
        {
            var device = _enum.GetDevice(id);
            var cap = new WasapiCapture(device, true, 50);
            cap.DataAvailable += OnMicData;
            cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null)
                {
                    MicError = "Mikrofon přestal odpovídat (odpojen?).";
                    Log.Error("Mikrofon zastaven", e.Exception);
                    DevicesChanged?.Invoke();
                }
            };
            cap.StartRecording();
            _mic = cap;
            _micId = id;
            MicError = null;
        }
        catch (Exception ex)
        {
            MicError = "Mikrofon nejde otevřít: " + ex.Message;
            Log.Error("Otevření mikrofonu", ex);
            CloseMic();
        }
    }

    private void CloseMic()
    {
        var cap = _mic;
        _mic = null;
        _micId = null;
        if (cap == null) return;
        try
        {
            cap.DataAvailable -= OnMicData;
            cap.StopRecording();
            cap.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Zavření mikrofonu", ex);
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiCapture cap || e.BytesRecorded == 0) return;
        var fmt = cap.WaveFormat;
        float peak = 0;
        var buf = e.Buffer;
        int n = e.BytesRecorded;

        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
            || (fmt is WaveFormatExtensible x && x.SubFormat == FloatSubFormat);

        if (isFloat && fmt.BitsPerSample == 32)
            for (int i = 0; i + 3 < n; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buf, i)));
        else if (fmt.BitsPerSample == 16)
            for (int i = 0; i + 1 < n; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buf, i) / 32768f));
        else if (fmt.BitsPerSample == 24)
            for (int i = 0; i + 2 < n; i += 3) peak = Math.Max(peak, Math.Abs(((buf[i + 2] << 24) | (buf[i + 1] << 16) | (buf[i] << 8)) / 2147483648f));
        else if (fmt.BitsPerSample == 32)
            for (int i = 0; i + 3 < n; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt32(buf, i) / 2147483648f));

        lock (_gate)
        {
            _micPeak = Math.Max(_micPeak, peak);
            if (_wav != null && !_wavPaused)
            {
                if (_wav.Length + n > MaxWavBytes)
                {
                    Log.Warn("WAV mikrofonu dosáhl limitu 4 GB, zápis ukončen.");
                    _wav.Dispose();
                    _wav = null;
                }
                else
                {
                    _wav.Write(buf, 0, n);
                }
            }
        }
    }

    /// <summary>Špička mikrofonu od posledního čtení (0–1).</summary>
    public double TakeMicPeak()
    {
        lock (_gate)
        {
            var p = _micPeak;
            _micPeak = 0;
            return p;
        }
    }

    public bool IsMicOpen => _mic != null;

    // ── Samostatný WAV mikrofonu ────────────────────────────────────────────
    public bool BeginMicFile(string path)
    {
        var cap = _mic;
        if (cap == null) return false;
        lock (_gate)
        {
            try
            {
                var f = cap.WaveFormat;
                _wavFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f is WaveFormatExtensible x && x.SubFormat == FloatSubFormat);
                // Jednoduchá hlavička (bez EXTENSIBLE) = nejlepší kompatibilita se střihovými programy
                var fileFormat = _wavFloat && f.BitsPerSample == 32
                    ? WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels)
                    : f.BitsPerSample is 16 or 24 or 32 ? new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels) : f;
                _wav = new WaveFileWriter(path, fileFormat);
                _wavPaused = false;
                MicFilePath = path;
                Log.Info($"WAV mikrofonu: {path} ({f})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("WAV mikrofonu nejde vytvořit", ex);
                _wav = null;
                MicFilePath = null;
                return false;
            }
        }
    }

    public void SetMicFilePaused(bool paused)
    {
        lock (_gate) _wavPaused = paused;
    }

    public string? EndMicFile()
    {
        lock (_gate)
        {
            try { _wav?.Dispose(); } catch (Exception ex) { Log.Error("Uzavření WAV", ex); }
            _wav = null;
            var p = MicFilePath;
            MicFilePath = null;
            return p;
        }
    }

    public void Dispose()
    {
        EndMicFile();
        CloseMic();
        try { _enum.UnregisterEndpointNotificationCallback(_notifier); } catch { }
        _enum.Dispose();
    }

    private sealed class Notifier(AudioHub hub) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => hub.DevicesChanged?.Invoke();
        public void OnDeviceAdded(string pwstrDeviceId) => hub.DevicesChanged?.Invoke();
        public void OnDeviceRemoved(string deviceId) => hub.DevicesChanged?.Invoke();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Console || role == Role.Multimedia) hub.DevicesChanged?.Invoke();
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
