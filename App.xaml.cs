using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WINREC;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "WINREC_error.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            ShowError("UnhandledException", ex.ExceptionObject?.ToString() ?? "unknown");

        DispatcherUnhandledException += (s, ex) =>
        {
            ex.Handled = true;
            ShowError("DispatcherException", ex.Exception?.ToString() ?? "unknown");
        };

        try
        {
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            ShowError("OnStartup", ex.ToString());
        }
    }

    private static void ShowError(string source, string detail)
    {
        try { File.WriteAllText(LogPath, $"[{source}]\r\n{detail}"); } catch { }
        MessageBox.Show(
            $"WINREC se nepodařilo spustit.\n\nChyba: {source}\n\n{detail}\n\nLog uložen na: {LogPath}",
            "WINREC — chyba spuštění",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Current?.Shutdown(1);
    }
}
