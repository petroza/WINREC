using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WINREC;

public enum SourceKind { Monitor, Window, Region }
public enum AudioMode { None, SystemOnly, MicOnly, SystemAndMic }
public enum QualityPreset { Podcast, High, Medium, Small }
public enum OutputScale { Native, P1440, P1080, P720 }
public enum VideoCodec { H264, H265 }
public enum IndicatorCorner { TopRight, TopLeft, BottomRight, BottomLeft, Hidden }

public sealed class AppSettings
{
    // Zdroj
    public SourceKind Source { get; set; } = SourceKind.Monitor;
    public string? MonitorDevice { get; set; }
    public int[]? LastRegion { get; set; }

    // Zvuk
    public AudioMode Audio { get; set; } = AudioMode.SystemAndMic;
    public string? SystemDeviceId { get; set; }          // null = výchozí výstup Windows
    public string? MicDeviceId { get; set; }
    public double SystemVolume { get; set; } = 1.0;
    public double MicVolume { get; set; } = 1.0;
    public bool MicMono { get; set; }
    public bool AppAudioOnly { get; set; }
    public bool SaveMicWav { get; set; } = true;

    // Video — výchozí: nejvyšší kvalita pro videopodcast, 60 fps, nativní rozlišení
    public QualityPreset Quality { get; set; } = QualityPreset.Podcast;
    public int Fps { get; set; } = 60;
    public OutputScale Scale { get; set; } = OutputScale.Native;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public bool ShowCursor { get; set; } = true;
    public bool HighlightClicks { get; set; } = true;
    public ClickEffect ClickEffect { get; set; } = ClickEffect.Ripple;
    public string ClickColorLeft { get; set; } = "#E12028";
    public string ClickColorRight { get; set; } = "#2F7BF5";
    public double ClickSize { get; set; } = 1.0;
    public int ClickDurationMs { get; set; } = 650;
    public bool CursorHalo { get; set; }

    // Chování
    public int CountdownSeconds { get; set; } = 3;
    public IndicatorCorner Indicator { get; set; } = IndicatorCorner.TopRight;
    public bool MinimizeOnStart { get; set; } = true;
    public bool HideAppFromRecording { get; set; } = true;
    public bool CrashSafeMp4 { get; set; }
    // Výchozí je Desktop Duplication: Windows Graphics Capture při zavření libovolného okna zamrzne na ~1 s (naměřeno).
    public bool UseGraphicsCaptureForScreen { get; set; }
    public bool HighGpuPriority { get; set; } = true;
    public bool DebugLog { get; set; }

    // Výstup
    public string OutputFolder { get; set; } = AppPaths.DefaultOutputFolder;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Nastavení nešlo načíst, používám výchozí", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), Encoding.UTF8);
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Nastavení nešlo uložit", ex);
        }
    }
}

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WINREC");

    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string AppLogFile => Path.Combine(LogDir, "winrec.log");
    public static string RecorderLogFile => Path.Combine(LogDir, "recorder.log");

    public static string DefaultOutputFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "WINREC");
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var f = new FileInfo(AppPaths.AppLogFile);
                if (f.Exists && f.Length > 5 * 1024 * 1024)
                    File.Move(f.FullName, f.FullName + ".old", overwrite: true);
                File.AppendAllText(AppPaths.AppLogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logování nesmí nikdy shodit nahrávání
        }
    }
}
