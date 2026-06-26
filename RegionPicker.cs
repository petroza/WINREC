using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WINREC;

public class RegionPicker : Window
{
    public Rect? SelectedRegion { get; private set; }

    private Point _dragStart;
    private bool _dragging;
    private readonly Rectangle _selRect;
    private readonly Canvas _canvas;
    private readonly TextBlock _sizeLabel;

    public RegionPicker()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(130, 0, 0, 0));
        Topmost = true;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Focusable = true;

        _selRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(30, 0x22, 0xC5, 0x5E)),
            Visibility = Visibility.Collapsed
        };

        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(200, 15, 23, 42)),
            FontSize = 12,
            FontFamily = new FontFamily("Segoe UI"),
            Padding = new Thickness(6, 3, 6, 3),
            Visibility = Visibility.Collapsed
        };

        _canvas = new Canvas();
        _canvas.Children.Add(_selRect);
        _canvas.Children.Add(_sizeLabel);

        var hint = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 30, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(210, 15, 23, 42)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 10, 20, 10),
            Child = new TextBlock
            {
                Text = "Táhněte myší pro výběr oblasti záznamu  •  ESC = zrušit",
                Foreground = Brushes.White,
                FontSize = 14,
                FontFamily = new FontFamily("Segoe UI")
            }
        };

        var grid = new Grid();
        grid.Children.Add(_canvas);
        grid.Children.Add(hint);
        Content = grid;

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Focus();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_canvas);
        _dragging = true;
        _selRect.Visibility = Visibility.Visible;
        _sizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_selRect, _dragStart.X);
        Canvas.SetTop(_selRect, _dragStart.Y);
        _selRect.Width = 0;
        _selRect.Height = 0;
        _canvas.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = e.GetPosition(_canvas);
        var x = Math.Min(cur.X, _dragStart.X);
        var y = Math.Min(cur.Y, _dragStart.Y);
        var w = Math.Abs(cur.X - _dragStart.X);
        var h = Math.Abs(cur.Y - _dragStart.Y);
        Canvas.SetLeft(_selRect, x);
        Canvas.SetTop(_selRect, y);
        _selRect.Width = w;
        _selRect.Height = h;

        _sizeLabel.Text = $"{(int)w} × {(int)h}";
        Canvas.SetLeft(_sizeLabel, x + w + 6);
        Canvas.SetTop(_sizeLabel, y);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _canvas.ReleaseMouseCapture();

        var w = _selRect.Width;
        var h = _selRect.Height;
        if (w > 16 && h > 16)
        {
            var topLeft = PointToScreen(new Point(
                Canvas.GetLeft(_selRect), Canvas.GetTop(_selRect)));
            SelectedRegion = new Rect(topLeft.X, topLeft.Y, w, h);
        }
        Close();
    }
}
