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

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SetDefaultOutputPath();
            PopulateSources();
        };
    }

    private void SetDefaultOutputPath()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var name = $"záznam_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4";
        OutputPathBox.Text = Path.Combine(videos, name);
    }

    private void PopulateSources()
    {
        if (SourceCombo == null || SourceLabel == null) return;
        SourceCombo.Items.Clear();

        bool isWindow = SourceTypeCombo.SelectedIndex == 1;
        PickWindowBtn.Visibility = isWindow ? Visibility.Visible : Visibility.Collapsed;

        if (isWindow)
        {
            SourceLabel.Text = "Okno";
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

            // Select picked window in combo
            foreach (SourceItem item in SourceCombo.Items)
            {
                if (item.Source is RecordableWindow w && w.Handle == picker.PickedWindow.Handle)
                {
                    SourceCombo.SelectedItem = item;
                    return;
                }
            }
            // Not in list yet — add it
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
            if (picker.SelectedRegion == null)
            {
                _selectedRegion = null;
                RegionBox.Text = "Celá plocha / celé okno";
                RegionBox.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                return;
            }
            _selectedRegion = picker.SelectedRegion;
            var r = _selectedRegion.Value;
            RegionBox.Text = $"{(int)r.X}, {(int)r.Y}  —  {(int)r.Width} × {(int)r.Height} px";
            RegionBox.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        };
        picker.Show();
    }

    // ── Browse output ──────────────────────────────────────────────────────────
    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "MP4 video (*.mp4)|*.mp4",
            FileName = Path.GetFileName(OutputPathBox.Text),
            InitialDirectory = Path.GetDirectoryName(OutputPathBox.Text)
        };
        if (dlg.ShowDialog() == true)
            OutputPathBox.Text = dlg.FileName;
    }

    // ── Start recording ────────────────────────────────────────────────────────
    private void StartRecording_Click(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedItem is not SourceItem selected)
        {
            MessageBox.Show("Vyberte zdroj záznamu.", "WINREC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputPath = OutputPathBox.Text.Trim();
        if (string.IsNullOrEmpty(outputPath))
        {
            MessageBox.Show("Zadejte výstupní soubor.", "WINREC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var bitrate = int.Parse(
            ((System.Windows.Controls.ComboBoxItem)QualityCombo.SelectedItem).Tag!.ToString()!);

        RecordingSourceBase source = selected.Source switch
        {
            RecordableDisplay d => BuildDisplaySource(d),
            RecordableWindow w => BuildWindowSource(w),
            _ => throw new InvalidOperationException("Neznámý typ zdroje.")
        };

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = { source } },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.CBR,
                    EncoderProfile = H264Profile.Main
                },
                Bitrate = bitrate * 1000,
                Framerate = 30,
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
                OutputFrameSize = new ScreenSize(0, 0)
            }
        };

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;
        _recorder.Record(outputPath);

        _isRecording = true;
        StartTimer();
        UpdateUI(recording: true);
        SetStatus($"Nahrávám → {Path.GetFileName(outputPath)}", "#22C55E");
    }

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
                (int)(r.X - monLeft),
                (int)(r.Y - monTop),
                (int)r.Width, (int)r.Height);
        }
        return src;
    }

    private RecordingSourceBase BuildWindowSource(RecordableWindow w)
    {
        var src = new WindowRecordingSource(w);
        if (_selectedRegion.HasValue)
        {
            var r = _selectedRegion.Value;
            src.SourceRect = new ScreenRect(0, 0, (int)r.Width, (int)r.Height);
        }
        return src;
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
        SetStatus("Záznam dokončen.", "#94A3B8");
        SetDefaultOutputPath();
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _isRecording = false;
            StopTimer();
            UpdateUI(recording: false);
            SetStatus($"Uloženo: {e.FilePath}", "#22C55E");
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
            SetStatus($"Chyba: {e.Error}", "#EF4444");
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
        StartButton.IsEnabled = !recording;
        StopButton.IsEnabled = recording;
        SourceTypeCombo.IsEnabled = !recording;
        SourceCombo.IsEnabled = !recording;
        PickWindowBtn.IsEnabled = !recording;
        AudioOutputCheck.IsEnabled = !recording;
        AudioInputCheck.IsEnabled = !recording;
        QualityCombo.IsEnabled = !recording;
        OutputPathBox.IsEnabled = !recording;
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
