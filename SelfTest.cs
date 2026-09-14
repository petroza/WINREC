using System.Diagnostics;
using System.IO;
using System.Text;
using ScreenRecorderLib;

namespace WINREC;

/// <summary>
/// Diagnostika bez okna: <c>WINREC.exe --selftest &lt;složka&gt;</c> nahraje několik krátkých
/// testovacích videí (monitor, oblast, okno, různé kodeky a zvuky) a zapíše selftest.txt.
/// </summary>
public static class SelfTest
{
    public static int Run(string dir, string? only = null)
    {
        bool Want(string id) => only == null || only.Contains(id, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        void W(string line)
        {
            report.AppendLine(line);
            File.WriteAllText(Path.Combine(dir, "selftest.txt"), report.ToString());
        }

        int failures = 0;
        try
        {
            var monitors = Native.GetMonitors();
            var main = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First();
            W("Monitory: " + string.Join("; ", monitors.Select(m => $"{m.DeviceName} {m.Bounds} scale={m.Scale}")));
            W("Recorder.GetDisplays: " + string.Join("; ", Recorder.GetDisplays().Select(d => $"{d.FriendlyName} {d.DeviceName}")));

            using var audio = new AudioHub();
            var mics = audio.GetMics();
            var outs = audio.GetOutputs();
            W("Mikrofony: " + string.Join("; ", mics.Select(m => $"{m.Name} (výchozí={m.IsDefault}, neníMic={m.IsNotRealMic})")));
            W("Výstupy: " + string.Join("; ", outs.Select(m => $"{m.Name} (výchozí={m.IsDefault})")));
            var micId = (AudioHub.PickBestMic(mics, null) ?? mics.FirstOrDefault())?.Id;
            var window = Native.GetTopWindows().FirstOrDefault(w => !w.Title.Contains("WINREC"));
            W($"Testovací okno: {window?.Title}");
            W($"GPU priorita: {Native.SetHighGpuPriority()}");

            if (Want("A"))
                failures += Case(W, dir, audio, "A_monitor_nativni_60fps_h264_system+mic_wav",
                    new AppSettings { Audio = micId != null ? AudioMode.SystemAndMic : AudioMode.SystemOnly },
                    SourceKind.Monitor, main, default, null, micId, wav: micId != null, seconds: 6, pause: true);

            if (Want("B"))
                failures += Case(W, dir, audio, "B_oblast_1281x721_30fps_h265_system",
                    new AppSettings { Audio = AudioMode.SystemOnly, Fps = 30, Codec = VideoCodec.H265, Quality = QualityPreset.High },
                    SourceKind.Region, main, new PxRect(main.Bounds.Left + 101, main.Bounds.Top + 77, 1281, 721), null, null, false, 4, false);

            if (window != null && Want("C"))
                failures += Case(W, dir, audio, "C_okno_1080p_bez_zvuku",
                    new AppSettings { Audio = AudioMode.None, Scale = OutputScale.P1080, Quality = QualityPreset.Medium },
                    SourceKind.Window, null, default, window, null, false, 4, false);

            if (Want("F"))
                failures += StaticWindowCase(W, dir, audio);

            if (Want("G"))
                failures += WindowEventsCase(W, dir, audio, main, desktopDuplication: false);

            if (Want("H"))
                failures += WindowEventsCase(W, dir, audio, main, desktopDuplication: true);

            // Porovnání „živé plochy“ — jen na výslovné vyžádání (--selftest dir K0K1K2K3)
            bool Explicit(string id) => only != null && only.Contains(id, StringComparison.OrdinalIgnoreCase);
            if (Explicit("K0")) failures += KeepAliveCase(W, dir, audio, main, "K0", dd: true, mode: 0);
            if (Explicit("K1")) failures += KeepAliveCase(W, dir, audio, main, "K1", dd: true, mode: 1);
            if (Explicit("K2")) failures += KeepAliveCase(W, dir, audio, main, "K2", dd: true, mode: 2);
            if (Explicit("K3")) failures += KeepAliveCase(W, dir, audio, main, "K3", dd: false, mode: 2);

            if (Want("E"))
                failures += Case(W, dir, audio, "E_monitor_30s_kontrola_zapisu",
                    new AppSettings { Audio = micId != null ? AudioMode.SystemAndMic : AudioMode.SystemOnly },
                    SourceKind.Monitor, main, default, null, micId, false, 30, false);

            if (micId != null && Want("D"))
                failures += Case(W, dir, audio, "D_monitor_DD_1080p_60fps_jen_mic",
                    new AppSettings { Audio = AudioMode.MicOnly, UseGraphicsCaptureForScreen = false, Scale = OutputScale.P1080 },
                    SourceKind.Monitor, main, default, null, micId, false, 4, false);
        }
        catch (Exception ex)
        {
            W("VÝJIMKA: " + ex);
            failures++;
        }
        W($"HOTOVO, chyb: {failures}");
        return failures;
    }

    /// <summary>
    /// Nahrává monitor a během záznamu otevírá/zavírá okno vyjmuté z nahrávání (3–5 s)
    /// a běžné okno (7–9 s). Zamrzlé snímky ve výsledném MP4 pak ukážou, co zasekávání způsobuje.
    /// </summary>
    private static int WindowEventsCase(Action<string> W, string dir, AudioHub audio, MonitorInfo main, bool desktopDuplication)
    {
        System.Windows.Threading.Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            new CaptureKeepAlive(main, excludeFromCapture: true).Show();   // stejně jako při skutečném nahrávání
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait(5000);

        var events = new List<string>();
        var clock = Stopwatch.StartNew();
        System.Windows.Window? excluded = null, normal = null;

        System.Windows.Window MakeWindow(string title, bool exclude, int left)
        {
            var w = new System.Windows.Window
            {
                Title = title,
                Width = 420,
                Height = 300,
                Left = main.Bounds.Left + left,
                Top = main.Bounds.Top + 300,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                ShowActivated = false,
                Topmost = true,
                Background = new System.Windows.Media.SolidColorBrush(exclude
                    ? System.Windows.Media.Color.FromRgb(120, 30, 30)
                    : System.Windows.Media.Color.FromRgb(30, 90, 50))
            };
            if (exclude)
                w.SourceInitialized += (_, _) => Native.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(w).Handle);
            w.Show();
            return w;
        }

        var timers = new List<Timer>();
        void Schedule(int ms, string label, Action action) =>
            timers.Add(new Timer(_ => dispatcher!.Invoke(() =>
            {
                action();
                lock (events) events.Add($"{label}@{clock.Elapsed.TotalSeconds:0.00}s");
            }), null, ms, Timeout.Infinite));

        // Třikrát běžné okno (otevřít → za 1 s zavřít), jednou okno vyjmuté z nahrávání.
        foreach (var at in new[] { 1500, 4500, 7500 })
        {
            Schedule(at, "běžné otevřeno", () => normal = MakeWindow("WINREC test běžné", false, 700));
            Schedule(at + 1000, "běžné ZAVŘENO", () => normal?.Close());
        }
        Schedule(10000, "vyjmuté otevřeno", () => excluded = MakeWindow("WINREC test vyjmuté", true, 200));
        Schedule(11000, "vyjmuté ZAVŘENO", () => excluded?.Close());

        var name = desktopDuplication ? "H_udalosti_oken_DD" : "G_udalosti_oken_WGC";
        long recordStartTick = 0;
        int result = Case(W, dir, audio, name,
            new AppSettings { Audio = AudioMode.None, Scale = OutputScale.P1080, UseGraphicsCaptureForScreen = !desktopDuplication },
            SourceKind.Monitor, main, default, null, null, false, 13, false,
            onRecording: () => Interlocked.CompareExchange(ref recordStartTick, clock.ElapsedTicks, 0));
        if (recordStartTick != 0)
            W($"    záznam skutečně začal v {recordStartTick / (double)Stopwatch.Frequency:0.00} s času testu " +
              "(čas události minus tato hodnota = čas ve videu)");

        foreach (var t in timers) t.Dispose();
        lock (events) W($"    události (od startu testu, záznam začal ~0,1 s po něm): {string.Join(", ", events)}");
        dispatcher?.InvokeShutdown();
        return result;
    }

    /// <param name="mode">0 = bez „živé plochy“, 1 = viditelná pro nahrávání, 2 = vyjmutá z nahrávání</param>
    private static int KeepAliveCase(Action<string> W, string dir, AudioHub audio, MonitorInfo main, string id, bool dd, int mode)
    {
        System.Windows.Threading.Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            if (mode > 0) new CaptureKeepAlive(main, excludeFromCapture: mode == 2).Show();
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait(5000);
        Thread.Sleep(400);

        string variant = mode switch { 0 => "bez", 1 => "zivaPlocha_viditelna", _ => "zivaPlocha_vyjmuta" };
        int result = Case(W, dir, audio, $"{id}_{(dd ? "DD" : "WGC")}_{variant}_15s",
            new AppSettings { Audio = AudioMode.None, UseGraphicsCaptureForScreen = !dd },
            SourceKind.Monitor, main, default, null, null, false, 15, false);
        dispatcher?.InvokeShutdown();
        return result;
    }

    /// <summary>Úplně statické okno ve vlastním UI vlákně — ověří, že nahrávka nezamrzá, když se obraz nemění.</summary>
    private static int StaticWindowCase(Action<string> W, string dir, AudioHub audio)
    {
        IntPtr hwnd = IntPtr.Zero;
        System.Windows.Threading.Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var w = new System.Windows.Window
            {
                Title = "WINREC statický test",
                Width = 800,
                Height = 600,
                Left = 140,
                Top = 140,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                ShowActivated = false,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 40, 60)),
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = "Statický obsah — test WINREC",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 36,
                    Margin = new System.Windows.Thickness(40)
                }
            };
            w.SourceInitialized += (_, _) => hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            w.ContentRendered += (_, _) => ready.Set();
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            w.Show();
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!ready.Wait(10_000) || hwnd == IntPtr.Zero)
        {
            W("[F_staticke_okno] testovací okno se neotevřelo");
            return 1;
        }
        Thread.Sleep(700);
        var win = new TopWindow(hwnd, "WINREC statický test", Native.GetWindowBounds(hwnd), Environment.ProcessId);
        int result = Case(W, dir, audio, "F_staticke_okno_10s", new AppSettings { Audio = AudioMode.None }, SourceKind.Window,
            null, default, win, null, false, 10, false);
        dispatcher?.InvokeShutdown();
        return result;
    }

    private static int Case(Action<string> W, string dir, AudioHub audio, string name, AppSettings s, SourceKind kind,
        MonitorInfo? monitor, PxRect region, TopWindow? window, string? micId, bool wav, int seconds, bool pause,
        Action? onRecording = null)
    {
        s.DebugLog = true;
        var path = Path.Combine(dir, name + ".mp4");
        var wavPath = Path.Combine(dir, name + "_mikrofon.wav");
        foreach (var f in new[] { path, wavPath }) if (File.Exists(f)) File.Delete(f);

        using var engine = new RecordingEngine();
        using var done = new ManualResetEventSlim();
        string? error = null;
        var statuses = new List<string>();
        bool first = true;
        var samples = new List<string>();
        void Sample(int ms)
        {
            long end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end)
            {
                Thread.Sleep(Math.Min(1000, (int)Math.Max(1, end - Environment.TickCount64)));
                var h = engine.CheckHealth();
                samples.Add($"{h.FileBytes / 1024}k{(h.IsOk ? "" : "!" + string.Join("|", h.Problems))}");
            }
        }

        if (wav) audio.SetMic(micId, true);
        engine.StatusChanged += st =>
        {
            lock (statuses) statuses.Add(st.ToString());
            if (st == RecorderStatus.Recording && first)
            {
                first = false;
                onRecording?.Invoke();
                if (wav) audio.BeginMicFile(wavPath);
            }
        };
        engine.Completed += _ => done.Set();
        engine.Failed += e => { error = e; done.Set(); };

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
                MicDeviceId = micId
            });
        }
        catch (Exception ex)
        {
            W($"[{name}] START SELHAL: {ex.Message}");
            return 1;
        }

        Sample(seconds * 500);
        if (pause)
        {
            engine.Pause();
            audio.SetMicFilePaused(true);
            Thread.Sleep(1000);
            engine.Resume();
            audio.SetMicFilePaused(false);
        }
        if (engine.HasMic)
        {
            engine.SetMicMuted(true);
            Thread.Sleep(300);
            engine.SetMicMuted(false);
        }
        Sample(seconds * 500);

        var stopWatch = Stopwatch.StartNew();
        engine.Stop();
        bool finished = done.Wait(60_000);
        audio.EndMicFile();
        audio.SetMic(null, false);

        long size = File.Exists(path) ? new FileInfo(path).Length : -1;
        long wavSize = File.Exists(wavPath) ? new FileInfo(wavPath).Length : -1;
        string st;
        lock (statuses) st = string.Join(">", statuses);
        W($"[{name}] dokončeno={finished} stop={stopWatch.ElapsedMilliseconds} ms čas={engine.Elapsed:mm\\:ss\\.ff} " +
          $"výstup={engine.OutputWidth}x{engine.OutputHeight} bitrate={engine.VideoBitrate} velikost={size} wav={wavSize} " +
          $"stavy={st} chyba={error ?? "-"}");
        W($"    snímky={engine.FramesRecorded} zvukPakety={engine.AudioPackets} {engine.DebugAudioIds}");
        W($"    kontrola zápisu: {string.Join(" ", samples)}");
        return finished && error == null && size > 0 ? 0 : 1;
    }
}
