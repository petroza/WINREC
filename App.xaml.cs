using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WINREC;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Fatal("Neošetřená chyba", ex.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            Fatal("Chyba v aplikaci", ex.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Nesledovaná chyba úlohy", ex.Exception);
            ex.SetObserved();
        };

        Log.Info($"WINREC {typeof(App).Assembly.GetName().Version} start, OS {Environment.OSVersion}, args: {string.Join(' ', e.Args)}");

        if (!CheckEnvironment())
        {
            Shutdown(2);
            return;
        }

        var args = e.Args;
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = SelfTest.Run(args[1], args.Length > 2 ? args[2] : null);
            Shutdown(code);
            return;
        }

        _singleInstance = new Mutex(true, "WINREC_2_SingleInstance", out bool created);
        if (!created && !(args.Length >= 2 && args[0] == "--screenshot"))
        {
            MessageBox.Show("WINREC už běží.", "WINREC", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            using var p = Process.GetCurrentProcess();
            p.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex) { Log.Warn("Prioritu procesu nelze nastavit: " + ex.Message); }

        var settings = AppSettings.Load();
        if (settings.HighGpuPriority)
            Log.Info("Vysoká priorita GPU: " + (Native.SetHighGpuPriority() ? "zapnuta" : "nepodařilo se"));

        var main = new MainWindow(settings);
        MainWindow = main;
        // Odhlášení / vypnutí Windows: nahrávání dokončit, jinak by MP4 zůstalo nepřehratelné.
        SessionEnding += (_, _) => main.EmergencyStop();

        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -20000;
            main.Top = 0;
            main.ContentRendered += async (_, _) =>
            {
                await Task.Delay(700);
                SaveScreenshot(main, args[1]);

                var mon = Native.GetMonitors().First();
                var ind = new RecordingIndicator(mon, IndicatorCorner.TopRight);
                ind.Show();
                var states = new (string Name, Action Apply)[]
                {
                    ("ok", () => ind.Update("00:12:34", true, false, false, "1,2 GB  ·  zápis OK", false, 0.62, false, 0.48, false)),
                    ("clip", () => ind.Update("00:12:35", true, false, false, "Mikrofon je PŘEBUZENÝ!", false, 1.0, true, 0.5, false)),
                    ("warn", () => ind.Update("00:12:36", true, false, false, "Soubor na disku neroste!", true, 0.6, false, 0.4, false))
                };
                foreach (var (name, apply) in states)
                {
                    apply();
                    await Task.Delay(250);
                    SaveElement((FrameworkElement)ind.Content, ind, Path.ChangeExtension(args[1], null) + $"_indicator_{name}.png");
                }
                ind.Close();

                var gallery = new ClickEffectGallery(settings)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = 0,
                    ShowActivated = false
                };
                gallery.Show();
                await Task.Delay(1100);
                SaveElement((FrameworkElement)gallery.Content, gallery, Path.ChangeExtension(args[1], null) + "_gallery.png");
                gallery.Close();
                main.ForceClose();
            };
        }
        main.Show();
    }

    private static void SaveScreenshot(Window w, string path) => SaveElement((FrameworkElement)w.Content, w, path);

    private static void SaveElement(FrameworkElement root, Window w, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Windows N/KN nemají Media Foundation → nahrávání by tiše selhalo.</summary>
    private static bool CheckEnvironment()
    {
        if (!Native.CanLoad("mfplat.dll") || !Native.CanLoad("mfreadwrite.dll"))
        {
            Log.Error("Chybí Media Foundation");
            var r = MessageBox.Show(
                "Ve Windows chybí Media Foundation (součást „Media Feature Pack“), bez které nejde kódovat video.\n\n" +
                "Týká se to edicí Windows N / KN. Doinstalujte ji v Nastavení → Systém → Volitelné funkce → " +
                "Přidat funkci → „Media Feature Pack“ a restartujte počítač.\n\nOtevřít Volitelné funkce?",
                "WINREC — chybí Media Foundation", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (r == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:optionalfeatures") { UseShellExecute = true }); } catch { }
            }
            return false;
        }
        return true;
    }

    private static bool _fatalShowing;
    private static DateTime _lastFatalShown;

    private static void Fatal(string title, Exception? ex)
    {
        Log.Error(title, ex);
        // Opakující se chyba nesmí zaplavit obrazovku okny — nejvýš jedno okno za 30 s, vše ostatní jen do logu.
        if (_fatalShowing || (DateTime.Now - _lastFatalShown).TotalSeconds < 30) return;
        _fatalShowing = true;
        try
        {
            string hint = ex is FileNotFoundException or DllNotFoundException or BadImageFormatException
                ? "\n\nPravděpodobně chybí některá součást Windows (Visual C++ runtime nebo Media Foundation)."
                : "";
            MessageBox.Show($"{title}:\n\n{ex?.Message}{hint}\n\nPodrobnosti jsou v logu:\n{AppPaths.AppLogFile}",
                "WINREC — chyba", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        finally
        {
            _fatalShowing = false;
            _lastFatalShown = DateTime.Now;
        }
    }
}
