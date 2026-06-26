using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenRecorderLib;

namespace WINREC;

public class WindowPicker : Window
{
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    public RecordableWindow? PickedWindow { get; private set; }

    public WindowPicker()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Focusable = true;

        Content = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(210, 15, 23, 42)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 12, 20, 12),
            Child = new TextBlock
            {
                Text = "Klikněte na okno, které chcete nahrávat\n(ESC = zrušit)",
                Foreground = Brushes.White,
                FontSize = 16,
                FontFamily = new FontFamily("Segoe UI"),
                TextAlignment = TextAlignment.Center
            }
        };

        MouseLeftButtonDown += OnClick;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Focus();
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        var screenPt = PointToScreen(e.GetPosition(this));
        Hide();
        Thread.Sleep(150);

        var pt = new POINT { X = (int)screenPt.X, Y = (int)screenPt.Y };
        var hwnd = GetAncestor(WindowFromPoint(pt), 2); // GA_ROOT

        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, 512);
        var title = sb.ToString();

        foreach (var w in Recorder.GetWindows())
        {
            if (w.Handle == hwnd || (!string.IsNullOrEmpty(title) && w.Title == title))
            {
                PickedWindow = w;
                break;
            }
        }

        Close();
    }
}
