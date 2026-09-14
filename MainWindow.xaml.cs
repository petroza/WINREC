using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ScreenRecorderLib;

namespace WINREC;

internal sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>Malé odznaky přes ikonu na hlavním panelu.</summary>
internal static class Badges
{
    public static readonly ImageSource Rec = Make(Color.FromRgb(0xEF, 0x44, 0x44), null);
    public static readonly ImageSource RecDim = Make(Color.FromRgb(0x7F, 0x1D, 0x1D), null);
    public static readonly ImageSource Pause = Make(Color.FromRgb(0xF5, 0x9E, 0x0B), "pause");
    public static readonly ImageSource Warn = Make(Color.FromRgb(0xF9, 0x73, 0x16), "warn");

    private static ImageSource Make(Color fill, string? glyph)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(fill), new Pen(Brushes.White, 2.5), new Point(16, 16), 13.5, 13.5);
            if (glyph == "pause")
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(10.5, 9, 4, 14));
                dc.DrawRectangle(Brushes.White, null, new Rect(17.5, 9, 4, 14));
            }
            else if (glyph == "warn")
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(14, 7.5, 4, 11));
                dc.DrawEllipse(Brushes.White, null, new Point(16, 22.5), 2.3, 2.3);
            }
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}

public partial class MainWindow : Window
{
    private enum UiState { Idle, Countdown, Recording, Paused, Finishing }

    private const int HkStartStop = 1, HkPause = 2, HkMute = 3;
    private const uint VK_F9 = 0x78, VK_F10 = 0x79, VK_F11 = 0x7A;

    private readonly AppSettings _s;
    private readonly AudioHub _audio;
    private readonly DispatcherTimer _ui;
    private readonly DispatcherTimer _deviceDebounce;
    private readonly Dictionary<int, string> _processNames = [];

    private RecordingEngine? _engine;
    private RecordingFrame? _frame;
    private RecordingIndicator? _indicator;
    private ClickEffectsController? _clicks;
    private CaptureKeepAlive? _keepAlive;
    private string _healthDetail = "spouštím…";
    private string? _lastProblems;
    private bool _healthWarn;
    private DateTime _micClipUntil, _sysClipUntil;
    private const double ClipLevel = 0.985; // ≈ −0,13 dBFS
    private CancellationTokenSource? _countdown;
    private PxRect? _region;
    private List<AudioDevice> _mics = [];
    private IntPtr _hwnd;
    private UiState _state = UiState.Idle;
    private bool _starting;
    private bool _closed;
    private DateTime _stopRequestedAt, _lastTickError;
    private readonly DispatcherTimer _saveDebounce;

    private bool _loading = true;
    private bool _forceClose, _closeAfterStop, _minimizedByUs, _firstRecordingStatus, _micWavWanted, _lowSpaceStopped;
    private string? _pendingWavPath, _lastFile;
    private double _sysLevel, _micLevel;
    private DateTime _lastSlowTick;

    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush ErrBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush RecBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush PauseBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush IdleDotBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55));

    public MainWindow(AppSettings settings)
    {
        _s = settings;
        InitializeComponent();

        var v = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = v == null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";

        _audio = new AudioHub();
        _audio.DevicesChanged += () => Dispatcher.BeginInvoke(() => { _deviceDebounce!.Stop(); _deviceDebounce.Start(); });

        _deviceDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _deviceDebounce.Tick += (_, _) => { _deviceDebounce.Stop(); OnDevicesChanged(); };

        _ui = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _ui.Tick += (_, _) => UiTick();

        // Nastavení se neukládá při každém pohybu posuvníku, ale až chvíli po poslední změně.
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _s.Save(); };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateMicMonitoring();
        IsVisibleChanged += (_, _) => UpdateMicMonitoring();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Start okna
    // ════════════════════════════════════════════════════════════════════════
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        var failed = new List<string>();
        uint mods = Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT;
        if (!Native.RegisterHotKey(_hwnd, HkStartStop, mods, VK_F9)) failed.Add("Ctrl+Shift+F9");
        if (!Native.RegisterHotKey(_hwnd, HkPause, mods, VK_F10)) failed.Add("Ctrl+Shift+F10");
        if (!Native.RegisterHotKey(_hwnd, HkMute, mods, VK_F11)) failed.Add("Ctrl+Shift+F11");
        if (failed.Count > 0)
        {
            HotkeyText.Text += $"\nPozor: zkratky {string.Join(", ", failed)} používá jiný program.";
            Log.Warn("Klávesové zkratky obsazené: " + string.Join(", ", failed));
        }

        if (_s.HideAppFromRecording) Native.ExcludeFromCapture(_hwnd);
        MaxHeight = SystemParameters.WorkArea.Height - 16;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplySettingsToUi();
            RefreshMonitors();
            RefreshWindows();
            RefreshAudioDevices();
        }
        catch (Exception ex)
        {
            Log.Error("Inicializace okna", ex);
            SetStatus("Chyba při načítání zařízení: " + ex.Message, ErrBrush);
        }
        _loading = false;

        UpdateSourceUi();
        UpdateAudioUi();
        UpdateVideoInfo();
        UpdateFreeSpace();
        SetUiState(UiState.Idle);
        _ui.Start();
    }

    private void ApplySettingsToUi()
    {
        (_s.Source switch { SourceKind.Window => SrcWindow, SourceKind.Region => SrcRegion, _ => SrcMonitor }).IsChecked = true;

        SetChoices(AudioModeCombo, [
            new("Systémový zvuk + mikrofon", AudioMode.SystemAndMic),
            new("Jen mikrofon", AudioMode.MicOnly),
            new("Jen systémový zvuk", AudioMode.SystemOnly),
            new("Bez zvuku", AudioMode.None)
        ], _s.Audio);

        SetChoices(QualityCombo, [
            new("Podcast — nejvyšší", QualityPreset.Podcast),
            new("Vysoká", QualityPreset.High),
            new("Střední", QualityPreset.Medium),
            new("Úsporná", QualityPreset.Small)
        ], _s.Quality);

        SetChoices(FpsCombo, [new("60 fps — plynulé", 60), new("30 fps — menší zátěž", 30)], _s.Fps);

        SetChoices(ScaleCombo, [
            new("Nativní — nejostřejší", OutputScale.Native),
            new("Zmenšit na 1440p", OutputScale.P1440),
            new("Zmenšit na 1080p", OutputScale.P1080),
            new("Zmenšit na 720p", OutputScale.P720)
        ], _s.Scale);

        SetChoices(CodecCombo, [
            new("H.264 — pro střih", VideoCodec.H264),
            new("H.265 — menší soubory", VideoCodec.H265)
        ], _s.Codec);

        SetChoices(CountdownCombo, [
            new("Bez odpočtu", 0), new("3 sekundy", 3), new("5 sekund", 5), new("10 sekund", 10)
        ], _s.CountdownSeconds);

        SetChoices(IndicatorCombo, [
            new("Vpravo nahoře", IndicatorCorner.TopRight), new("Vlevo nahoře", IndicatorCorner.TopLeft),
            new("Vpravo dole", IndicatorCorner.BottomRight), new("Vlevo dole", IndicatorCorner.BottomLeft),
            new("Nezobrazovat", IndicatorCorner.Hidden)
        ], _s.Indicator);

        SystemVolume.Value = _s.SystemVolume;
        MicVolume.Value = _s.MicVolume;
        MicMonoCheck.IsChecked = _s.MicMono;
        SaveWavCheck.IsChecked = _s.SaveMicWav;
        AppAudioOnlyCheck.IsChecked = _s.AppAudioOnly;
        CursorCheck.IsChecked = _s.ShowCursor;
        ClicksCheck.IsChecked = _s.HighlightClicks;
        MinimizeCheck.IsChecked = _s.MinimizeOnStart;
        HideSelfCheck.IsChecked = _s.HideAppFromRecording;
        GpuPriorityCheck.IsChecked = _s.HighGpuPriority;
        CrashSafeCheck.IsChecked = _s.CrashSafeMp4;
        DdCheck.IsChecked = _s.UseGraphicsCaptureForScreen;
        DebugLogCheck.IsChecked = _s.DebugLog;
        OutputFolderBox.Text = _s.OutputFolder;
        UpdateClickEffectButton();

        if (_s.LastRegion is { Length: 4 } r)
        {
            var rect = new PxRect(r[0], r[1], r[2], r[3]);
            if (Native.GetMonitors().Any(m => !m.Bounds.Intersect(rect).IsEmpty)) _region = rect;
        }
    }

    private void ReadUiToSettings()
    {
        if (_loading) return;
        _s.Source = CurrentKind();
        _s.MonitorDevice = SelectedMonitor()?.DeviceName ?? _s.MonitorDevice;
        _s.Audio = Selected(AudioModeCombo, _s.Audio);
        _s.SystemDeviceId = OutputCombo.SelectedItem is Choice<string?> o ? o.Value : _s.SystemDeviceId;
        _s.MicDeviceId = SelectedMic()?.Id ?? _s.MicDeviceId;
        _s.SystemVolume = Math.Round(SystemVolume.Value, 2);
        _s.MicVolume = Math.Round(MicVolume.Value, 2);
        _s.MicMono = MicMonoCheck.IsChecked == true;
        _s.SaveMicWav = SaveWavCheck.IsChecked == true;
        _s.AppAudioOnly = AppAudioOnlyCheck.IsChecked == true;
        _s.Quality = Selected(QualityCombo, _s.Quality);
        _s.Fps = Selected(FpsCombo, _s.Fps);
        _s.Scale = Selected(ScaleCombo, _s.Scale);
        _s.Codec = Selected(CodecCombo, _s.Codec);
        _s.ShowCursor = CursorCheck.IsChecked == true;
        _s.HighlightClicks = ClicksCheck.IsChecked == true;
        _s.CountdownSeconds = Selected(CountdownCombo, _s.CountdownSeconds);
        _s.Indicator = Selected(IndicatorCombo, _s.Indicator);
        _s.MinimizeOnStart = MinimizeCheck.IsChecked == true;
        _s.HideAppFromRecording = HideSelfCheck.IsChecked == true;
        _s.HighGpuPriority = GpuPriorityCheck.IsChecked == true;
        _s.CrashSafeMp4 = CrashSafeCheck.IsChecked == true;
        _s.UseGraphicsCaptureForScreen = DdCheck.IsChecked == true;
        _s.DebugLog = DebugLogCheck.IsChecked == true;
        _s.OutputFolder = OutputFolderBox.Text;
        _s.LastRegion = _region is { } r ? [r.Left, r.Top, r.Width, r.Height] : null;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private static void SetChoices<T>(ComboBox cb, IReadOnlyList<Choice<T>> items, T selected)
    {
        cb.Items.Clear();
        foreach (var i in items) cb.Items.Add(i);
        cb.SelectedItem = items.FirstOrDefault(i => EqualityComparer<T>.Default.Equals(i.Value, selected)) ?? items.FirstOrDefault();
    }

    private static T Selected<T>(ComboBox cb, T fallback) => cb.SelectedItem is Choice<T> c ? c.Value : fallback;

    // ════════════════════════════════════════════════════════════════════════
    //  Zdroj obrazu
    // ════════════════════════════════════════════════════════════════════════
    private SourceKind CurrentKind() =>
        SrcWindow.IsChecked == true ? SourceKind.Window :
        SrcRegion.IsChecked == true ? SourceKind.Region : SourceKind.Monitor;

    private MonitorInfo? SelectedMonitor() => (MonitorCombo.SelectedItem as Choice<MonitorInfo>)?.Value;
    private TopWindow? SelectedWindow() => (WindowCombo.SelectedItem as Choice<TopWindow>)?.Value;

    private void RefreshMonitors()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Recorder.GetDisplays())
                if (!string.IsNullOrWhiteSpace(d.FriendlyName)) names[d.DeviceName] = d.FriendlyName;
        }
        catch (Exception ex)
        {
            Log.Error("Recorder.GetDisplays", ex);
        }

        var current = SelectedMonitor()?.DeviceName ?? _s.MonitorDevice;
        var monitors = Native.GetMonitors().OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
        MonitorCombo.Items.Clear();
        Choice<MonitorInfo>? select = null;
        int n = 1;
        foreach (var m in monitors)
        {
            var name = names.TryGetValue(m.DeviceName, out var fn) ? fn : $"Monitor {n}";
            var item = new Choice<MonitorInfo>(
                $"{name}  —  {m.Bounds.Width} × {m.Bounds.Height}{(m.IsPrimary ? "  (hlavní)" : "")}", m);
            MonitorCombo.Items.Add(item);
            if (m.DeviceName == current || (select == null && m.IsPrimary)) select = item;
            n++;
        }
        MonitorCombo.SelectedItem = select ?? MonitorCombo.Items.OfType<object>().FirstOrDefault();
    }

    private string ProcessName(int pid)
    {
        if (_processNames.TryGetValue(pid, out var n)) return n;
        try
        {
            using var p = Process.GetProcessById(pid);
            n = p.ProcessName;
        }
        catch { n = "?"; }
        return _processNames[pid] = n;
    }

    private void RefreshWindows(IntPtr? prefer = null)
    {
        var keep = prefer ?? SelectedWindow()?.Handle ?? IntPtr.Zero;
        var list = Native.GetTopWindows([_hwnd]);
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            WindowCombo.Items.Clear();
            foreach (var w in list)
            {
                var title = w.Title.Length > 70 ? w.Title[..67] + "…" : w.Title;
                var item = new Choice<TopWindow>($"{title}   ·   {ProcessName(w.ProcessId)}", w);
                WindowCombo.Items.Add(item);
                if (w.Handle == keep) WindowCombo.SelectedItem = item;
            }
            if (WindowCombo.SelectedItem == null && WindowCombo.Items.Count > 0) WindowCombo.SelectedIndex = 0;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void Source_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (CurrentKind() == SourceKind.Window) RefreshWindows();
        UpdateSourceUi();
        UpdateAudioUi();
        UpdateVideoInfo();
        ReadUiToSettings();
    }

    private void UpdateSourceUi()
    {
        var kind = CurrentKind();
        MonitorPanel.Visibility = kind == SourceKind.Monitor ? Visibility.Visible : Visibility.Collapsed;
        WindowPanel.Visibility = kind == SourceKind.Window ? Visibility.Visible : Visibility.Collapsed;
        RegionPanel.Visibility = kind == SourceKind.Region ? Visibility.Visible : Visibility.Collapsed;

        if (_region is { } r)
        {
            var mon = Native.GetMonitors().FirstOrDefault(m => m.Bounds.Contains(r.Left + r.Width / 2, r.Top + r.Height / 2));
            RegionText.Text = $"{r.Width} × {r.Height} px   ·   od bodu {r.Left - (mon?.Bounds.Left ?? 0)}, {r.Top - (mon?.Bounds.Top ?? 0)}";
            RegionText.Foreground = Brushes.White;
        }
        else
        {
            RegionText.Text = "Zatím nevybráno — klikněte na „Vybrat myší“";
            RegionText.Foreground = MutedBrush;
        }
    }

    private void WindowCombo_DropDownOpened(object? sender, EventArgs e) => RefreshWindows();

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateVideoInfo();
    }

    private async void PickWindow_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await Task.Delay(180);
        TopWindow? picked = null;
        try { picked = await OverlayPicker.PickWindowAsync([_hwnd]); }
        catch (Exception ex) { Log.Error("Výběr okna", ex); }
        finally
        {
            Show();
            Activate();
        }
        if (picked == null) return;
        SrcWindow.IsChecked = true;
        RefreshWindows(picked.Handle);
        UpdateSourceUi();
        UpdateAudioUi();
        UpdateVideoInfo();
        ReadUiToSettings();
    }

    private async void PickRegion_Click(object sender, RoutedEventArgs e) => await PickRegionAsync();

    private async Task PickRegionAsync()
    {
        Hide();
        await Task.Delay(180);
        PxRect? picked = null;
        try { picked = await OverlayPicker.PickRegionAsync(); }
        catch (Exception ex) { Log.Error("Výběr oblasti", ex); }
        finally
        {
            Show();
            Activate();
        }
        if (picked == null) return;
        _region = picked;
        SrcRegion.IsChecked = true;
        UpdateSourceUi();
        UpdateVideoInfo();
        ReadUiToSettings();
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Zvuk
    // ════════════════════════════════════════════════════════════════════════
    private AudioMode SelectedAudioMode() => Selected(AudioModeCombo, AudioMode.SystemAndMic);
    private AudioDevice? SelectedMic() => (MicCombo.SelectedItem as Choice<AudioDevice>)?.Value;
    private string? SelectedOutputId() => (OutputCombo.SelectedItem as Choice<string?>)?.Value;

    private void RefreshAudioDevices()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            var outputs = _audio.GetOutputs();
            var def = outputs.FirstOrDefault(o => o.IsDefault);
            var outId = OutputCombo.SelectedItem is Choice<string?> cur ? cur.Value : _s.SystemDeviceId;
            OutputCombo.Items.Clear();
            OutputCombo.Items.Add(new Choice<string?>(def != null ? $"Výchozí výstup Windows  ({def.Name})" : "Výchozí výstup Windows", null));
            foreach (var o in outputs) OutputCombo.Items.Add(new Choice<string?>(o.Name, o.Id));
            OutputCombo.SelectedItem = OutputCombo.Items.OfType<Choice<string?>>().FirstOrDefault(c => c.Value == outId)
                                       ?? OutputCombo.Items[0];

            var micId = SelectedMic()?.Id ?? _s.MicDeviceId;
            _mics = _audio.GetMics();
            MicCombo.Items.Clear();
            foreach (var m in _mics)
            {
                var label = m.Name + (m.IsDefault ? "  (výchozí)" : "") + (m.IsNotRealMic ? "  — není mikrofon" : "");
                MicCombo.Items.Add(new Choice<AudioDevice>(label, m));
            }
            var best = AudioHub.PickBestMic(_mics, micId);
            MicCombo.SelectedItem = MicCombo.Items.OfType<Choice<AudioDevice>>().FirstOrDefault(c => c.Value.Id == best?.Id);
        }
        finally
        {
            _loading = wasLoading;
        }
        _audio.SelectOutput(SelectedOutputId());
        UpdateAudioUi();
    }

    private void OnDevicesChanged()
    {
        if (_engine != null)
        {
            // Během nahrávání seznam neměníme, jen upozorníme.
            if (_audio.MicError != null) SetStatus("Pozor: " + _audio.MicError, ErrBrush);
            return;
        }
        var before = SelectedMic()?.Id;
        RefreshAudioDevices();
        var after = SelectedMic();
        if (after != null && after.Id != before && !after.IsNotRealMic)
            SetStatus($"Mikrofon připojen: {after.Name}", OkBrush);
    }

    private void RefreshAudio_Click(object sender, RoutedEventArgs e) => RefreshAudioDevices();

    private void AudioMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateAudioUi();
        UpdateVideoInfo();
        ReadUiToSettings();
    }

    private void OutputCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _audio.SelectOutput(SelectedOutputId());
        ReadUiToSettings();
    }

    private void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateAudioUi();
        ReadUiToSettings();
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || SystemVolText == null || MicVolText == null) return;
        SystemVolText.Text = $"{SystemVolume.Value * 100:0} %";
        MicVolText.Text = $"{MicVolume.Value * 100:0} %";
        try { _engine?.SetVolumes(SystemVolume.Value, MicVolume.Value); }
        catch (Exception ex) { Log.Error("Změna hlasitosti za běhu", ex); }
        ReadUiToSettings();
    }

    private void UpdateAudioUi()
    {
        var mode = SelectedAudioMode();
        bool sys = mode is AudioMode.SystemOnly or AudioMode.SystemAndMic;
        bool mic = mode is AudioMode.MicOnly or AudioMode.SystemAndMic;
        SystemAudioPanel.Visibility = sys ? Visibility.Visible : Visibility.Collapsed;
        MicPanel.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        AppAudioOnlyCheck.Visibility = sys && CurrentKind() == SourceKind.Window ? Visibility.Visible : Visibility.Collapsed;
        SystemVolText.Text = $"{SystemVolume.Value * 100:0} %";
        MicVolText.Text = $"{MicVolume.Value * 100:0} %";

        string? warn = null;
        var sel = SelectedMic();
        if (mic)
        {
            if (_mics.Count == 0)
                warn = "Není připojený žádný mikrofon. Jakmile ho připojíte, objeví se tu sám.";
            else if (sel == null)
                warn = "Není připojený žádný skutečný mikrofon. Připojte mikrofon (seznam se obnoví sám), nebo vyberte zařízení ručně.";
            else if (sel.IsNotRealMic)
                warn = mode == AudioMode.SystemAndMic
                    ? $"„{sel.Name}“ není mikrofon, ale kopie zvuku z reproduktorů — systémový zvuk by v nahrávce byl dvakrát (ozvěna). Připojte mikrofon."
                    : $"„{sel.Name}“ není mikrofon, ale kopie zvuku z reproduktorů.";
            else if (_audio.MicError != null)
                warn = _audio.MicError;
        }
        MicPlaceholder.Visibility = sel == null ? Visibility.Visible : Visibility.Collapsed;
        MicPlaceholder.Text = _mics.Count == 0 ? "— mikrofon není připojený —" : "— vyberte mikrofon —";
        MicWarningText.Text = warn ?? "";
        MicWarning.Visibility = warn == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateMicMonitoring();
    }

    private void UpdateMicMonitoring()
    {
        if (_loading) return;
        var mode = SelectedAudioMode();
        bool micMode = mode is AudioMode.MicOnly or AudioMode.SystemAndMic;
        bool active = micMode && (_engine != null || (IsVisible && WindowState != WindowState.Minimized));
        _audio.SetMic(SelectedMic()?.Id, active);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Video / výstup
    // ════════════════════════════════════════════════════════════════════════
    private void AnySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateVideoInfo();
        ReadUiToSettings();
        UpdateClickEffectButton();
        // Skrytí z nahrávky platí hned (dřív až po restartu a vypnout nešlo).
        if (_hwnd != IntPtr.Zero) Native.ExcludeFromCapture(_hwnd, HideSelfCheck.IsChecked == true);
    }

    private void UpdateClickEffectButton()
    {
        if (ClickEffectText == null) return;
        ClickEffectText.Text = "Efekt: " + EffectRenderer.Name(_s.ClickEffect) + (_s.CursorHalo ? " + kruh u kurzoru" : "");
        ClickColorDot.Fill = new SolidColorBrush(ClickStyle.ParseColor(_s.ClickColorLeft, Colors.Red));
        ClickEffectBtn.IsEnabled = ClicksCheck.IsChecked == true || _s.CursorHalo;
    }

    private void ClickEffect_Click(object sender, RoutedEventArgs e)
    {
        var gallery = new ClickEffectGallery(_s) { Owner = this };
        if (gallery.ShowDialog() != true) return;
        gallery.ApplyTo(_s);
        _loading = true;
        ClicksCheck.IsChecked = _s.HighlightClicks;
        _loading = false;
        _s.Save();
        UpdateClickEffectButton();
    }

    private (int W, int H)? SourcePixelSize() => CurrentKind() switch
    {
        SourceKind.Monitor when SelectedMonitor() is { } m => (m.Bounds.Width, m.Bounds.Height),
        SourceKind.Window when SelectedWindow() is { } w => (Native.GetWindowBounds(w.Handle).Width, Native.GetWindowBounds(w.Handle).Height),
        SourceKind.Region when _region is { } r => (r.Width, r.Height),
        _ => null
    };

    private long BytesPerHour()
    {
        var size = SourcePixelSize() ?? (1920, 1080);
        var (w, h) = RecordingEngine.ScaledSize(size.W, size.H, Selected(ScaleCombo, OutputScale.Native));
        var bps = RecordingEngine.ComputeBitrate(w, h, Selected(FpsCombo, 60), Selected(QualityCombo, QualityPreset.Podcast), Selected(CodecCombo, VideoCodec.H264));
        return (bps + 192_000) * 3600 / 8;
    }

    private void UpdateVideoInfo()
    {
        if (VideoInfoText == null) return;
        var size = SourcePixelSize();
        if (size == null)
        {
            VideoInfoText.Text = CurrentKind() == SourceKind.Region
                ? "Nejdřív vyberte oblast — pak tu uvidíte rozlišení a velikost souboru."
                : "Vyberte okno — pak tu uvidíte rozlišení a velikost souboru.";
            UpdateFreeSpace();
            return;
        }
        var scale = Selected(ScaleCombo, OutputScale.Native);
        var fps = Selected(FpsCombo, 60);
        var codec = Selected(CodecCombo, VideoCodec.H264);
        var (w, h) = RecordingEngine.ScaledSize(size.Value.W, size.Value.H, scale);
        var bps = RecordingEngine.ComputeBitrate(w, h, fps, Selected(QualityCombo, QualityPreset.Podcast), codec);
        double gbHour = (bps + 192_000) * 3600 / 8 / 1e9;
        string audio = SelectedAudioMode() == AudioMode.None ? "bez zvuku" : "AAC 192 kbps";
        VideoInfoText.Text =
            $"{w} × {h}  ·  {fps} fps  ·  {(codec == VideoCodec.H265 ? "H.265" : "H.264 High")} {bps / 1e6:0.#} Mbit/s  ·  {audio}  ·  ≈ {gbHour:0.#} GB za hodinu";
        UpdateFreeSpace();
    }

    private long? FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(OutputFolderBox.Text));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    private void UpdateFreeSpace()
    {
        if (FreeSpaceText == null) return;
        var free = FreeBytes();
        if (free == null)
        {
            FreeSpaceText.Text = "Volné místo nelze zjistit.";
            return;
        }
        double hours = free.Value / (double)Math.Max(1, BytesPerHour());
        FreeSpaceText.Text = $"Volné místo: {FormatSize(free.Value)}  ·  vystačí zhruba na {FormatHours(hours)} nahrávání";
        FreeSpaceText.Foreground = hours < 0.5 ? ErrBrush : MutedBrush;
    }

    private static string FormatHours(double h) =>
        h >= 1 ? $"{h:0.#} h" : $"{Math.Max(0, h * 60):0} min";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} kB"
    };

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Kam ukládat nahrávky",
            InitialDirectory = Directory.Exists(OutputFolderBox.Text) ? OutputFolderBox.Text : AppPaths.DefaultOutputFolder
        };
        if (dlg.ShowDialog(this) != true) return;
        OutputFolderBox.Text = dlg.FolderName;
        UpdateFreeSpace();
        ReadUiToSettings();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(OutputFolderBox.Text);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputFolderBox.Text}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("Složku nejde otevřít: " + ex.Message, ErrBrush); }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\"") { UseShellExecute = true });
    }

    private void PlayLast_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile == null || !File.Exists(_lastFile)) return;
        try { Process.Start(new ProcessStartInfo(_lastFile) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Video nejde otevřít: " + ex.Message, ErrBrush); }
    }

    private void ShowLast_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile == null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFile}\"") { UseShellExecute = true });
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Nahrávání
    // ════════════════════════════════════════════════════════════════════════
    private async void Record_Click(object sender, RoutedEventArgs e) => await ToggleRecordingAsync();

    private async Task ToggleRecordingAsync()
    {
        if (_countdown != null)
        {
            _countdown.Cancel();
            return;
        }
        if (_engine != null)
        {
            StopRecording();
            return;
        }
        // Start už probíhá (otevřený dotaz na mikrofon, výběr oblasti…) — druhé stisknutí nebo zkratku ignorovat,
        // jinak by vznikly dva dialogy a nakonec dvě nahrávání přes sebe.
        if (_starting) return;
        _starting = true;
        try
        {
            await StartRecordingAsync();
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task StartRecordingAsync()
    {
        if (_state != UiState.Idle) return;
        ReadUiToSettings();
        var s = _s.Clone();
        var kind = CurrentKind();

        MonitorInfo? monitor = null;
        PxRect region = default;
        TopWindow? window = null;

        switch (kind)
        {
            case SourceKind.Monitor:
                monitor = SelectedMonitor();
                if (monitor == null) { Warn("Není vybraný žádný monitor."); return; }
                break;

            case SourceKind.Region:
                if (_region == null)
                {
                    await PickRegionAsync();
                    if (_region == null) return;
                }
                region = _region.Value;
                monitor = Native.GetMonitors().FirstOrDefault(m => m.Bounds.Contains(region.Left + region.Width / 2, region.Top + region.Height / 2));
                if (monitor == null) { Warn("Vybraná oblast už neleží na žádném monitoru. Vyberte ji prosím znovu."); return; }
                break;

            case SourceKind.Window:
                window = SelectedWindow();
                if (window == null || !Native.IsWindow(window.Handle))
                {
                    RefreshWindows();
                    Warn("Vyberte okno, které chcete nahrávat.");
                    return;
                }
                var wb = Native.GetWindowBounds(window.Handle);
                monitor = Native.GetMonitors().FirstOrDefault(m => m.Bounds.Contains(wb.Left + wb.Width / 2, wb.Top + wb.Height / 2));
                break;
        }

        // Mikrofon
        string? micId = null;
        if (s.Audio is AudioMode.MicOnly or AudioMode.SystemAndMic)
        {
            var mic = SelectedMic();
            if (mic == null)
            {
                bool onlyMic = s.Audio == AudioMode.MicOnly;
                var answer = MessageBox.Show(this,
                    onlyMic
                        ? "Není vybraný žádný mikrofon.\n\nNahrávat video bez zvuku?"
                        : "Není vybraný žádný mikrofon.\n\nNahrávat jen se systémovým zvukem?",
                    "WINREC", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                s.Audio = onlyMic ? AudioMode.None : AudioMode.SystemOnly;
            }
            else
            {
                micId = mic.Id;
            }
        }

        // Výstup
        string folder = s.OutputFolder;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Warn($"Do složky „{folder}“ nejde ukládat:\n{ex.Message}");
            return;
        }
        var free = FreeBytes();
        if (free is { } f && f < 5L * 1024 * 1024 * 1024)
        {
            var answer = MessageBox.Show(this,
                $"Na disku zbývá jen {FormatSize(f)} (zhruba {FormatHours(f / (double)BytesPerHour())} nahrávání).\n\nPřesto spustit?",
                "WINREC — málo místa", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        string baseName = $"WINREC_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        string path = Path.Combine(folder, baseName + ".mp4");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName}_{i}.mp4");
        _pendingWavPath = Path.ChangeExtension(path, null) + "_mikrofon.wav";
        _micWavWanted = s.SaveMicWav && micId != null;
        _lowSpaceStopped = false;

        // Odpočet
        if (s.MinimizeOnStart)
        {
            _minimizedByUs = true;
            WindowState = WindowState.Minimized;
        }
        if (s.CountdownSeconds > 0 && monitor != null)
        {
            _countdown = new CancellationTokenSource();
            SetUiState(UiState.Countdown);
            bool completed = await CountdownWindow.RunAsync(monitor, s.CountdownSeconds, _countdown.Token);
            _countdown.Dispose();
            _countdown = null;
            if (_closed) return;   // okno zavřeno během odpočtu
            if (!completed)
            {
                RestoreIfMinimizedByUs();
                SetUiState(UiState.Idle);
                SetStatus("Odpočet zrušen.", MutedBrush);
                return;
            }
        }

        // Start
        if (kind != SourceKind.Window && monitor != null)
        {
            // Bez neustálé drobné změny plochy knihovna při nehybném obrazu zahazuje ~0,2 s videa
            _keepAlive = new CaptureKeepAlive(monitor, excludeFromCapture: true);
            _keepAlive.Show();
        }
        if (_micWavWanted || micId != null) _audio.SetMic(micId, true);
        var engine = new RecordingEngine();
        engine.StatusChanged += st => Dispatcher.BeginInvoke(() => OnEngineStatus(engine, st));
        engine.Completed += p => Dispatcher.BeginInvoke(() => OnEngineFinished(engine, p, null));
        engine.Failed += err => Dispatcher.BeginInvoke(() => OnEngineFinished(engine, null, err));
        _engine = engine;
        _firstRecordingStatus = true;

        try
        {
            engine.Start(new RecordingRequest
            {
                Kind = kind,
                Settings = s,
                OutputPath = path,
                Monitor = monitor,
                Region = region,
                WindowHandle = window?.Handle ?? IntPtr.Zero,
                WindowProcessId = window?.ProcessId ?? 0,
                MicDeviceId = micId,
                SystemDeviceId = SelectedOutputId()
            });
        }
        catch (Exception ex)
        {
            Log.Error("Start nahrávání", ex);
            _engine = null;
            engine.Dispose();
            _keepAlive?.Close();
            _keepAlive = null;
            RestoreIfMinimizedByUs();
            SetUiState(UiState.Idle);
            SetStatus("Nahrávání se nespustilo: " + ex.Message, ErrBrush);
            MessageBox.Show(this, ex.Message, "WINREC — nahrávání se nespustilo", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Native.KeepAwake(true);
        if (kind == SourceKind.Region)
        {
            _frame = new RecordingFrame(region.Intersect(monitor!.Bounds));   // nahrává se jen část na tomto monitoru
            _frame.Show();
        }
        if ((s.HighlightClicks || s.CursorHalo) && kind != SourceKind.Window)
        {
            try { _clicks = new ClickEffectsController(ClickStyle.From(s)); }
            catch (Exception ex) { Log.Error("Efekty kliknutí", ex); }
        }
        _healthDetail = "spouštím…";
        _healthWarn = false;
        _lastProblems = null;
        if (s.Indicator != IndicatorCorner.Hidden)
        {
            var im = monitor ?? Native.GetMonitors().FirstOrDefault(m => m.IsPrimary);
            if (im != null)
            {
                _indicator = new RecordingIndicator(im, s.Indicator);
                _indicator.Show();
            }
        }
        SetUiState(UiState.Recording);
        SetStatus($"Nahrávám do {Path.GetFileName(path)}", MutedBrush);
        LastFilePanel.Visibility = Visibility.Collapsed;
    }

    private void OnEngineStatus(RecordingEngine engine, RecorderStatus status)
    {
        if (engine != _engine) return;
        switch (status)
        {
            case RecorderStatus.Recording:
                if (_firstRecordingStatus)
                {
                    _firstRecordingStatus = false;
                    if (_micWavWanted && _pendingWavPath != null && !_audio.BeginMicFile(_pendingWavPath))
                        SetStatus("Video se nahrává, ale samostatný WAV mikrofonu nejde vytvořit.", ErrBrush);
                }
                else
                {
                    _audio.SetMicFilePaused(false);
                }
                _frame?.SetPaused(false);
                SetUiState(UiState.Recording);
                break;

            case RecorderStatus.Paused:
                _audio.SetMicFilePaused(true);
                _frame?.SetPaused(true);
                SetUiState(UiState.Paused);
                break;

            case RecorderStatus.Finishing:
                SetUiState(UiState.Finishing);
                break;
        }
    }

    private void OnEngineFinished(RecordingEngine engine, string? path, string? error)
    {
        if (engine != _engine) return;
        _engine = null;
        _stopRequestedAt = default;
        var elapsed = engine.Elapsed;
        var wav = _audio.EndMicFile();
        Native.KeepAwake(false);
        _frame?.Close();
        _frame = null;
        _indicator?.Close();
        _indicator = null;
        _clicks?.Dispose();
        _clicks = null;
        _keepAlive?.Close();
        _keepAlive = null;
        Dispatcher.BeginInvoke(engine.Dispose, DispatcherPriority.Background);

        if (!_closeAfterStop) RestoreIfMinimizedByUs();
        SetUiState(UiState.Idle);

        long savedBytes = path != null && File.Exists(path) ? new FileInfo(path).Length : 0;
        if (error == null && (!engine.HasStarted || savedBytes == 0))
        {
            // Stop dřív, než se nahrávání rozběhlo: knihovna hlásí „hotovo“, ale soubor je prázdný.
            Log.Warn($"Nahrávání zastaveno před rozběhnutím ({path}, {savedBytes} B).");
            SetStatus("Nahrávání bylo zastaveno dřív, než se rozběhlo — nic se neuložilo.", PauseBrush);
            LastFilePanel.Visibility = Visibility.Collapsed;
        }
        else if (error == null && path != null)
        {
            _lastFile = path;
            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            string extra = wav != null ? "  +  samostatný WAV mikrofonu" : "";
            SetStatus($"Uloženo: {Path.GetFileName(path)}  ·  {elapsed:hh\\:mm\\:ss}  ·  {FormatSize(size)}{extra}", OkBrush);
            LastFilePanel.Visibility = Visibility.Visible;
            if (_lowSpaceStopped)
                MessageBox.Show(this, "Nahrávání bylo zastaveno, protože docházelo místo na disku. Video je uložené.",
                    "WINREC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            SetStatus("Nahrávání selhalo: " + error, ErrBrush);
            if (!_closeAfterStop)
                MessageBox.Show(this, $"Nahrávání selhalo:\n\n{error}\n\nPodrobnosti: {AppPaths.AppLogFile}\n" +
                                      "Tip: v „Další nastavení“ zkuste záložní metodu snímání nebo zapněte podrobný log.",
                    "WINREC — chyba", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateMicMonitoring();
        UpdateFreeSpace();
        if (_closeAfterStop) ForceClose();
    }

    private void StopRecording()
    {
        if (_engine == null) return;
        SetUiState(UiState.Finishing);
        _stopRequestedAt = DateTime.Now;
        try { _engine.Stop(); }
        catch (Exception ex)
        {
            Log.Error("Stop", ex);
            OnEngineFinished(_engine, null, ex.Message);
        }
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (_engine == null) return;
        try
        {
            if (_engine.Status == RecorderStatus.Recording) _engine.Pause();
            else if (_engine.Status == RecorderStatus.Paused) _engine.Resume();
        }
        catch (Exception ex) { Log.Error("Pauza", ex); }
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (_engine is not { HasMic: true }) return;
        try
        {
            _engine.SetMicMuted(!_engine.MicMuted);
            UpdateMuteButton();
        }
        catch (Exception ex) { Log.Error("Ztlumení mikrofonu", ex); }
    }

    private void UpdateMuteButton()
    {
        bool muted = _engine?.MicMuted == true;
        MuteText.Text = muted ? "Zapnout mikrofon" : "Ztlumit mikrofon";
        MuteIcon.Text = muted ? "" : "";
        MuteButton.Foreground = muted ? PauseBrush : (Brush)FindResource("Text");
    }

    private void RestoreIfMinimizedByUs()
    {
        if (!_minimizedByUs) return;
        _minimizedByUs = false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SetUiState(UiState st)
    {
        _state = st;
        bool idle = st == UiState.Idle;
        bool active = st is UiState.Recording or UiState.Paused;

        foreach (var el in new UIElement[]
                 {
                     SourceCard, VideoCard, OutputCard, AdvancedCard, AudioModeCombo, OutputCombo, MicCombo,
                     RefreshAudioBtn, MicMonoCheck, SaveWavCheck, AppAudioOnlyCheck
                 })
            el.IsEnabled = idle;

        RecordButton.IsEnabled = st != UiState.Finishing;
        (RecordButtonIcon.Text, RecordButtonText.Text) = st switch
        {
            UiState.Countdown => ("", "Zrušit odpočet"),
            UiState.Recording or UiState.Paused => ("", "Zastavit a uložit"),
            UiState.Finishing => ("", "Ukládám…"),
            _ => ("", "Nahrávat")
        };

        PauseButton.IsEnabled = active;
        PauseText.Text = st == UiState.Paused ? "Pokračovat" : "Pauza";
        PauseIcon.Text = st == UiState.Paused ? "" : "";
        MuteButton.IsEnabled = active && _engine?.HasMic == true;
        UpdateMuteButton();

        TimerPill.Visibility = active || st == UiState.Finishing ? Visibility.Visible : Visibility.Collapsed;
        RecDot.Fill = st switch
        {
            UiState.Recording => RecBrush,
            UiState.Paused => PauseBrush,
            UiState.Countdown => PauseBrush,
            _ => IdleDotBrush
        };
        RecDot.Opacity = 1;

        // Ikona na hlavním panelu: červený pruh se plní každou minutu, blikající tečka, pauza, vykřičník
        Taskbar.ProgressState = st switch
        {
            UiState.Recording => TaskbarItemProgressState.Error,
            UiState.Paused or UiState.Countdown => TaskbarItemProgressState.Paused,
            UiState.Finishing => TaskbarItemProgressState.Indeterminate,
            _ => TaskbarItemProgressState.None
        };
        if (st is UiState.Countdown) Taskbar.ProgressValue = 1;
        Taskbar.Overlay = st switch
        {
            UiState.Recording => Badges.Rec,
            UiState.Paused or UiState.Countdown => Badges.Pause,
            _ => null
        };
        Taskbar.Description = st switch
        {
            UiState.Idle => "WINREC — připraveno",
            UiState.Countdown => "WINREC — odpočet před nahráváním",
            UiState.Finishing => "WINREC — ukládám video…",
            _ => Taskbar.Description
        };
        if (idle) Title = "WINREC";
        if (st == UiState.Paused) SetStatus("Pozastaveno — pokračujte tlačítkem nebo Ctrl+Shift+F10.", PauseBrush);
        if (st == UiState.Finishing) SetStatus("Dokončuji a ukládám video…", MutedBrush);
    }

    private void SetStatus(string text, Brush brush)
    {
        StatusText.Text = text;
        StatusText.Foreground = brush;
    }

    private void Warn(string text) =>
        MessageBox.Show(this, text, "WINREC", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ════════════════════════════════════════════════════════════════════════
    //  Časovač UI: měřiče, čas, místo na disku
    // ════════════════════════════════════════════════════════════════════════
    private static double ToMeter(double peak) =>
        peak <= 0.00001 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1);

    private static double Smooth(double current, double target) =>
        target >= current ? target : Math.Max(target, current - 0.035);

    private void UiTick()
    {
        try
        {
            UiTickCore();
        }
        catch (Exception ex)
        {
            // Chyba v časovači (běží 12× za sekundu) nesmí vyvolat smršť chybových oken — jen log, nejvýš 1× za 10 s.
            if ((DateTime.Now - _lastTickError).TotalSeconds > 10)
            {
                _lastTickError = DateTime.Now;
                Log.Error("Časovač UI", ex);
            }
        }
    }

    private void UiTickCore()
    {
        var now = DateTime.Now;
        bool visible = IsVisible && WindowState != WindowState.Minimized;
        bool micMuted = _engine?.MicMuted == true;

        // Úrovně tak, jak půjdou do nahrávky (včetně posuvníku hlasitosti)
        double micIn = _audio.TakeMicPeak();
        double micOut = micMuted ? 0 : micIn * MicVolume.Value;
        double sysOut = _audio.GetSystemPeak() * SystemVolume.Value;
        _micLevel = Smooth(_micLevel, ToMeter(micOut));
        _sysLevel = Smooth(_sysLevel, ToMeter(sysOut));
        if (!micMuted && (micIn >= ClipLevel || micOut >= ClipLevel)) _micClipUntil = now.AddSeconds(2);
        if (sysOut >= ClipLevel) _sysClipUntil = now.AddSeconds(2);
        bool micClip = now < _micClipUntil, sysClip = now < _sysClipUntil;

        if (visible)
        {
            MicMeter.Value = _micLevel;
            SystemMeter.Value = _sysLevel;
            MicMeter.BorderBrush = micClip ? RecBrush : (Brush)FindResource("FieldBorder");
            SystemMeter.BorderBrush = sysClip ? RecBrush : (Brush)FindResource("FieldBorder");
            MicVolText.Text = micClip ? "PŘEBUZ." : $"{MicVolume.Value * 100:0} %";
            MicVolText.Foreground = micClip ? RecBrush : MutedBrush;
            SystemVolText.Text = sysClip ? "PŘEBUZ." : $"{SystemVolume.Value * 100:0} %";
            SystemVolText.Foreground = sysClip ? RecBrush : MutedBrush;
        }

        if (_engine == null) return;
        bool active = _state is UiState.Recording or UiState.Paused;
        bool paused = _state == UiState.Paused;
        bool blinkOn = now.Millisecond < 550;
        string t = _engine.Elapsed.ToString(@"hh\:mm\:ss");
        if (active)
        {
            TimerText.Text = t;
            RecDot.Opacity = paused || blinkOn ? 1 : 0.25;
            Title = (paused ? "❚❚ " : "● ") + t + " — WINREC";
            if (!paused)
            {
                Taskbar.ProgressValue = Math.Max(0.03, _engine.Elapsed.TotalSeconds % 60 / 60);
                Taskbar.Overlay = _healthWarn ? Badges.Warn : blinkOn ? Badges.Rec : Badges.RecDim;
            }
        }

        if ((now - _lastSlowTick).TotalSeconds >= 1)
        {
            _lastSlowTick = now;
            var health = _engine.CheckHealth();
            var problems = string.Join("  ", health.Problems);
            if (problems != (_lastProblems ?? ""))
            {
                if (problems.Length > 0) Log.Warn($"Kontrola záznamu ({t}): {problems}");
                else if (!string.IsNullOrEmpty(_lastProblems)) Log.Info($"Kontrola záznamu ({t}): opět v pořádku");
                _lastProblems = problems;
            }
            _healthWarn = !health.IsOk;
            _healthDetail = health.IsOk ? $"{FormatSize(health.FileBytes)}  ·  zápis OK" : health.Problems[0];
            if (active)
                Taskbar.Description = $"{(paused ? "❚❚ PAUZA" : "● REC")} {t}  ·  {FormatSize(health.FileBytes)}  ·  " +
                                      (health.IsOk ? "zápis OK" : "POZOR: " + problems);

            if (active)
            {
                if (!health.IsOk)
                    SetStatus("POZOR: " + problems, ErrBrush);
                else if (_audio.MicError != null)
                    SetStatus("Pozor: " + _audio.MicError, ErrBrush);
                else
                    SetStatus($"{(paused ? "Pozastaveno" : "Nahrávám")}: {Path.GetFileName(_engine.OutputPath)}  ·  {FormatSize(health.FileBytes)}  ·  " +
                              $"{_engine.OutputWidth} × {_engine.OutputHeight}, {_s.Fps} fps", paused ? PauseBrush : MutedBrush);
            }

            // Pojistka: knihovna nepotvrdila dokončení → aplikace nesmí navždy viset v „Ukládám…“.
            if (_state == UiState.Finishing && _stopRequestedAt != default)
            {
                double waited = (now - _stopRequestedAt).TotalSeconds;
                double limit = _engine.HasStarted ? 90 : 10;
                if (waited > limit)
                {
                    Log.Error($"Knihovna nepotvrdila uložení do {limit:0} s (rozběhnuto={_engine.HasStarted}) — přestávám čekat.");
                    OnEngineFinished(_engine, null, _engine.HasStarted
                        ? "Uložení videa se nepotvrdilo ani po 90 sekundách. Soubor nemusí být kompletní."
                        : "Nahrávání se nestihlo rozběhnout a nepodařilo se ho korektně ukončit.");
                    return;
                }
                if (waited > 15) SetStatus($"Ukládání trvá neobvykle dlouho ({waited:0} s)…", PauseBrush);
            }

            if (FreeBytes() is { } free && free < 1L * 1024 * 1024 * 1024 && !_lowSpaceStopped && _state == UiState.Recording)
            {
                Log.Warn($"Dochází místo ({free} B) — zastavuji.");
                _lowSpaceStopped = true;
                StopRecording();
            }
        }

        if (_indicator != null && _engine != null)
        {
            string detail = _state switch
            {
                UiState.Paused => "pozastaveno",
                UiState.Finishing => "ukládám video…",
                _ when !_healthWarn && micClip => "Mikrofon je PŘEBUZENÝ!",
                _ when !_healthWarn && sysClip => "Systémový zvuk je PŘEBUZENÝ!",
                _ => _healthDetail
            };
            _indicator.Update(t, blinkOn, paused, _state == UiState.Finishing, detail, _healthWarn && _state == UiState.Recording,
                _engine.HasMic ? _micLevel : null, micClip, _engine.HasSystemAudio ? _sysLevel : null, sysClip);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Klávesové zkratky a zavírání
    // ════════════════════════════════════════════════════════════════════════
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        handled = true;
        switch (wParam.ToInt32())
        {
            case HkStartStop:
                if (_state is UiState.Idle or UiState.Countdown or UiState.Recording or UiState.Paused)
                    _ = ToggleRecordingAsync();
                break;
            case HkPause:
                TogglePause();
                break;
            case HkMute:
                ToggleMute();
                break;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            _countdown?.Cancel();
            if (_engine != null)
            {
                e.Cancel = true;
                var r = MessageBox.Show(this, "Právě se nahrává.\n\nZastavit nahrávání, uložit video a zavřít WINREC?",
                    "WINREC", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                {
                    _closeAfterStop = true;
                    StopRecording();
                }
                return;
            }
            ReadUiToSettings();
            _saveDebounce.Stop();
            _s.Save();
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Windows se odhlašuje nebo vypíná: nahrávání dokončit synchronně (MP4 bez dokončení je nepřehratelné).
    /// </summary>
    public void EmergencyStop()
    {
        var engine = _engine;
        if (engine == null) return;
        Log.Warn("Windows ukončuje relaci během nahrávání — dokončuji video.");
        var done = new ManualResetEventSlim();   // nedisponovat: pozdní událost z knihovny by jinak spadla
        engine.Completed += _ => done.Set();
        engine.Failed += _ => done.Set();
        try { engine.Stop(); }
        catch (Exception ex) { Log.Error("Nouzové zastavení", ex); }
        bool ok = done.Wait(10_000);
        _audio.EndMicFile();
        Native.KeepAwake(false);
        Log.Info("Nouzové zastavení: " + (ok ? "video uloženo" : "uložení nepotvrzeno do 10 s"));
        _forceClose = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_saveDebounce.IsEnabled)
        {
            _saveDebounce.Stop();
            _s.Save();
        }
        _ui.Stop();
        if (_hwnd != IntPtr.Zero)
        {
            Native.UnregisterHotKey(_hwnd, HkStartStop);
            Native.UnregisterHotKey(_hwnd, HkPause);
            Native.UnregisterHotKey(_hwnd, HkMute);
        }
        _frame?.Close();
        _indicator?.Close();
        _clicks?.Dispose();
        _keepAlive?.Close();
        _engine?.Dispose();
        _audio.Dispose();
        Native.KeepAwake(false);
        base.OnClosed(e);
    }
}
