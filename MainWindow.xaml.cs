using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenRecorderLib;

namespace WINREC;

public partial class MainWindow : Window
{
    private Recorder? _recorder;
    private DispatcherTimer? _timer;
    private TimeSpan _elapsed;
    private bool _isRecording;
    private Rect? _selectedRegion;

    // Persisted last-used directory
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WINREC");
    private static readonly string LastDirFile = Path.Combine(SettingsDir, "lastdir.txt");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SetDefaultOutputPath();
            PopulateSources();
        };
    }

    // ── Output path ────────────────────────────────────────────────────────────

    private string LoadLastDir()
    {
        try
        {
            if (File.Exists(LastDirFile))
            {
                var dir = File.ReadAllText(LastDirFile).Trim();
                if (Directory.Exists(dir)) return dir;
            }
        }
        catch { /* ignore */ }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    private void SaveLastDir(string path)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(LastDirFile, path);
        }
        catch { /* ignore */ }
    }

    private void SetDefaultOutputPath()
    {
        var dir = LoadLastDir();
        var name = $"recording_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
        OutputPathBox.Text = Path.Combine(dir, name);
    }

    // ── Source population ──────────────────────────────────────────────────────

    private void PopulateSources()
    {
        if (SourceCombo == null || SourceLabel == null) return;
        SourceCombo.Items.Clear();

        bool isWindow = SourceTypeCombo.SelectedIndex == 1;
        PickWindowBtn.Visibility = isWindow ? Visibility.Visible : Visibility.Collapsed;

        if (isWindow)
        {
            SourceLabel.Text = "Window";
            foreach (var w in Recorder.GetWindows())
                SourceCombo.Items.Add(new SourceItem(w.Title, w));
        }
        else
        {
            SourceLabel.Text = "Monitor";
            foreach (var d in Recorder.GetDisplays())
                SourceCombo.Items.Add(new SourceItem(d.FriendlyName ?? d.DeviceName, d));
        }

        if (SourceCombo.Items.Count > 0)
            SourceCombo.SelectedIndex = 0;
    }

    private void SourceTypeCombo_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => PopulateSources();

    private void RefreshSources_Click(object sender, RoutedEventArgs e) => PopulateSources();

    // ── Window picker ──────────────────────────────────────────────────────────

    private void PickWindow_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        var picker = new WindowPicker();
        picker.Closed += (_, _) =>
        {
            Show();
            Activate();
            if (picker.PickedWindow == null) return;

            foreach (SourceItem item in SourceCombo.Items)
            {
                if (item.Source is RecordableWindow w && w.Handle == picker.PickedWindow.Handle)
                {
                    SourceCombo.SelectedItem = item;
                    return;
                }
            }
            var newItem = new SourceItem(picker.PickedWindow.Title, picker.PickedWindow);
            SourceCombo.Items.Insert(0, newItem);
            SourceCombo.SelectedIndex = 0;
        };
        picker.Show();
    }

    // ── Region picker ──────────────────────────────────────────────────────────

    private void PickRegion_Click(object sender, RoutedEventArgs e)
    {
        var picker = new RegionPicker();
        picker.Closed += (_, _) =>
        {
            Activate();
            if (picker.SelectedRegion == null) return;

            _selectedRegion = picker.SelectedRegion;
            var r = _selectedRegion.Value;
            RegionBox.Text = $"{(int)r.X}, {(int)r.Y}  —  {(int)r.Width} × {(int)r.Height} px";
            RegionBox.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            ClearRegionBtn.Visibility = Visibility.Visible;
        };
        picker.Show();
    }

    private void ClearRegion_Click(object sender, RoutedEventArgs e)
    {
        _selectedRegion = null;
        RegionBox.Text = "Full screen / full window";
        RegionBox.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        ClearRegionBtn.Visibility = Visibility.Collapsed;
    }

    // ── Browse output ──────────────────────────────────────────────────────────

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var currentDir = Path.GetDirectoryName(OutputPathBox.Text) ?? LoadLastDir();
        var dlg = new SaveFileDialog
        {
            Filter = "MP4 video (*.mp4)|*.mp4",
            FileName = Path.GetFileName(OutputPathBox.Text),
            InitialDirectory = currentDir
        };
        if (dlg.ShowDialog() == true)
        {
            OutputPathBox.Text = dlg.FileName;
            SaveLastDir(Path.GetDirectoryName(dlg.FileName) ?? currentDir);
        }
    }

    // ── Start recording ────────────────────────────────────────────────────────

    private void StartRecording_Click(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not SourceItem selected)
        {
            MessageBox.Show("Select a recording source.", "WINREC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputPath = OutputPathBox.Text.Trim();
        if (string.IsNullOrEmpty(outputPath))
        {
            MessageBox.Show("Specify an output file.", "WINREC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Save the directory for next time
        SaveLastDir(Path.GetDirectoryName(outputPath)!);

        var bitrate = int.Parse(
            ((System.Windows.Controls.ComboBoxItem)QualityCombo.SelectedItem).Tag!.ToString()!);
        var fps = int.Parse(
            ((System.Windows.Controls.ComboBoxItem)FpsCombo.SelectedItem).Tag!.ToString()!);
        var codec = ((System.Windows.Controls.ComboBoxItem)CodecCombo.SelectedItem).Tag!.ToString()!;

        // Build recording source
        RecordingSourceBase source = selected.Source switch
        {
            RecordableDisplay d => BuildDisplaySource(d),
            RecordableWindow  w => BuildWindowSource(w),
            _ => throw new InvalidOperationException("Unknown source type.")
        };

        // Determine output frame size for cropped recording.
        // H.264/H.265 (yuv420p) require EVEN width/height — round down to even.
        ScreenSize outputSize = new ScreenSize(0, 0);
        if (_selectedRegion.HasValue)
        {
            int ow = (int)_selectedRegion.Value.Width  & ~1;
            int oh = (int)_selectedRegion.Value.Height & ~1;
            outputSize = new ScreenSize(ow, oh);
        }

        // Build encoder
        IVideoEncoder encoder = codec == "h265"
            ? new H265VideoEncoder
              {
                  BitrateMode = H265BitrateControlMode.CBR,
                  EncoderProfile = H265Profile.Main
              }
            : new H264VideoEncoder
              {
                  BitrateMode = H264BitrateControlMode.CBR,
                  EncoderProfile = H264Profile.Main
              };

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = { source } },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = encoder,
                Bitrate = bitrate * 1000,
                Framerate = fps,
                IsFixedFramerate = false,
                IsHardwareEncodingEnabled = true
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = AudioOutputCheck.IsChecked == true || AudioInputCheck.IsChecked == true,
                AudioOutputDevice = AudioOutputCheck.IsChecked == true
                    ? Recorder.GetSystemAudioDevices(AudioDeviceSource.OutputDevices)
                        .FirstOrDefault()?.DeviceName ?? ""
                    : null,
                AudioInputDevice = AudioInputCheck.IsChecked == true
                    ? Recorder.GetSystemAudioDevices(AudioDeviceSource.InputDevices)
                        .FirstOrDefault()?.DeviceName
                    : null
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = outputSize
            }
        };

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;
        _recorder.Record(outputPath);

        _isRecording = true;
        StartTimer();
        UpdateUI(recording: true);
        SetStatus($"Recording → {Path.GetFileName(outputPath)}", "#22C55E");
    }

    // ── Build recording sources ────────────────────────────────────────────────

    private RecordingSourceBase BuildDisplaySource(RecordableDisplay d)
    {
        var src = new DisplayRecordingSource(d)
        {
            RecorderApi = RecorderApi.WindowsGraphicsCapture
        };
        if (_selectedRegion.HasValue)
        {
            var r = _selectedRegion.Value;
            var (monLeft, monTop) = NativeMethods.GetMonitorOrigin(d.DeviceName);
            src.SourceRect = new ScreenRect(
                (int)(r.X - monLeft), (int)(r.Y - monTop),
                (int)r.Width & ~1, (int)r.Height & ~1);
        }
        return src;
    }

    private RecordingSourceBase BuildWindowSource(RecordableWindow w)
    {
        // Use display-crop approach: find the monitor the window is on, crop to window rect.
        // This avoids the yellow highlight appearing on the whole screen and is more reliable
        // on multi-monitor setups.
        if (NativeMethods.GetWindowRect(w.Handle, out var wr) &&
            NativeMethods.GetMonitorDeviceNameForWindow(w.Handle) is string devName)
        {
            var (monLeft, monTop, monW, monH) = NativeMethods.GetMonitorRect(devName);

            // Find matching RecordableDisplay
            RecordableDisplay? display = null;
            foreach (var d in Recorder.GetDisplays())
            {
                if (string.Equals(d.DeviceName.TrimEnd('\0'), devName.TrimEnd('\0'),
                        StringComparison.OrdinalIgnoreCase))
                { display = d; break; }
            }

            if (display != null)
            {
                // Clamp window rect to monitor bounds
                int sx = Math.Max(wr.Left,  monLeft)  - monLeft;
                int sy = Math.Max(wr.Top,   monTop)   - monTop;
                int ex = Math.Min(wr.Right,  monLeft + monW) - monLeft;
                int ey = Math.Min(wr.Bottom, monTop  + monH) - monTop;
                int sw = ex - sx;
                int sh = ey - sy;

                if (sw > 8 && sh > 8)
                {
                    var src = new DisplayRecordingSource(display)
                    {
                        RecorderApi = RecorderApi.WindowsGraphicsCapture
                    };

                    // If the user also picked a region, apply it relative to the window
                    if (_selectedRegion.HasValue)
                    {
                        var r = _selectedRegion.Value;
                        src.SourceRect = new ScreenRect(
                            (int)(r.X - monLeft), (int)(r.Y - monTop),
                            (int)r.Width, (int)r.Height);
                    }
                    else
                    {
                        src.SourceRect = new ScreenRect(sx, sy, sw, sh);
                    }
                    return src;
                }
            }
        }

        // Fallback: use WindowRecordingSource directly
        var fallback = new WindowRecordingSource(w);
        if (_selectedRegion.HasValue)
        {
            var r = _selectedRegion.Value;
            fallback.SourceRect = new ScreenRect(0, 0, (int)r.Width, (int)r.Height);
        }
        return fallback;
    }

    // ── Stop ───────────────────────────────────────────────────────────────────

    private void StopRecording_Click(object sender, RoutedEventArgs e) => StopRecording();

    private void StopRecording()
    {
        if (_recorder == null) return;
        _recorder.Stop();
        _isRecording = false;
        StopTimer();
        UpdateUI(recording: false);
        SetStatus("Recording stopped.", "#94A3B8");
        SetDefaultOutputPath();
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _isRecording = false;
            StopTimer();
            UpdateUI(recording: false);
            SetStatus($"Saved: {e.FilePath}", "#22C55E");
            SetDefaultOutputPath();
        });
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _isRecording = false;
            StopTimer();
            UpdateUI(recording: false);
            SetStatus($"Error: {e.Error}", "#EF4444");
        });
    }

    // ── Timer ──────────────────────────────────────────────────────────────────

    private void StartTimer()
    {
        _elapsed = TimeSpan.Zero;
        TimerText.Visibility = Visibility.Visible;
        TimerText.Text = "00:00:00";
        RecordingIndicator.Visibility = Visibility.Visible;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        bool blink = true;
        _timer.Tick += (_, _) =>
        {
            _elapsed = _elapsed.Add(TimeSpan.FromSeconds(1));
            TimerText.Text = _elapsed.ToString(@"hh\:mm\:ss");
            RecordingIndicator.Fill = blink
                ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))
                : new SolidColorBrush(Colors.Transparent);
            blink = !blink;
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
        TimerText.Visibility = Visibility.Collapsed;
        RecordingIndicator.Visibility = Visibility.Collapsed;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void UpdateUI(bool recording)
    {
        StartButton.IsEnabled     = !recording;
        StopButton.IsEnabled      = recording;
        SourceTypeCombo.IsEnabled = !recording;
        SourceCombo.IsEnabled     = !recording;
        PickWindowBtn.IsEnabled   = !recording;
        AudioOutputCheck.IsEnabled = !recording;
        AudioInputCheck.IsEnabled  = !recording;
        QualityCombo.IsEnabled    = !recording;
        FpsCombo.IsEnabled        = !recording;
        CodecCombo.IsEnabled      = !recording;
        OutputPathBox.IsEnabled   = !recording;
    }

    private void SetStatus(string message, string hex)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_isRecording) StopRecording();
        _recorder?.Dispose();
        base.OnClosed(e);
    }
}

internal class SourceItem(string label, object source)
{
    public string Label { get; } = label;
    public object Source { get; } = source;
    public override string ToString() => Label;
}
