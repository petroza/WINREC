using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WINREC;

/// <summary>Okno umístěné přesně ve fyzických pixelech (správně i při různém škálování monitorů).</summary>
public class PhysicalWindow : Window
{
    private readonly bool _clickThrough;
    private readonly bool _excludeFromCapture;
    protected IntPtr Hwnd { get; private set; }
    public PxRect Target { get; private set; }

    public PhysicalWindow(PxRect target, bool clickThrough, bool excludeFromCapture)
    {
        Target = target;
        _clickThrough = clickThrough;
        _excludeFromCapture = excludeFromCapture;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = !clickThrough;
        Left = target.Left;
        Top = target.Top;
        Width = Math.Max(1, target.Width);
        Height = Math.Max(1, target.Height);

        SourceInitialized += (_, _) =>
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            if (_clickThrough) Native.MakeClickThrough(Hwnd);
            else Native.HideFromAltTab(Hwnd);
            if (_excludeFromCapture) Native.ExcludeFromCapture(Hwnd);
            Place();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place, DispatcherPriority.Background);
    }

    protected double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    protected void Place()
    {
        if (Hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, Target.Left, Target.Top, Target.Width, Target.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public void MoveTo(PxRect target)
    {
        Target = target;
        Place();
    }

    protected Point ToLocal(int x, int y) => new((x - Target.Left) / Scale, (y - Target.Top) / Scale);
}

// ════════════════════════════════════════════════════════════════════════════
//  Výběr oblasti tažením / výběr okna kliknutím — přes všechny monitory
// ════════════════════════════════════════════════════════════════════════════
public sealed class OverlayPicker
{
    private enum Mode { Region, Window }

    private readonly Mode _mode;
    private readonly List<PickerOverlay> _overlays = [];
    private readonly List<TopWindow> _windows;
    private readonly DispatcherTimer _timer;
    private readonly TaskCompletionSource<object?> _tcs = new();
    private MonitorInfo? _dragMonitor;
    private int _startX, _startY;
    private bool _dragging;
    private TopWindow? _hover;
    private bool _done;

    private OverlayPicker(Mode mode, ICollection<IntPtr> skipWindows)
    {
        _mode = mode;
        _windows = mode == Mode.Window ? Native.GetTopWindows(skipWindows) : [];
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Tick();
    }

    public static async Task<PxRect?> PickRegionAsync()
    {
        var p = new OverlayPicker(Mode.Region, []);
        var r = await p.RunAsync();
        return r is PxRect rect ? rect : null;
    }

    public static async Task<TopWindow?> PickWindowAsync(ICollection<IntPtr> skipWindows)
    {
        var p = new OverlayPicker(Mode.Window, skipWindows);
        return await p.RunAsync() as TopWindow;
    }

    private Task<object?> RunAsync()
    {
        Native.GetCursorPos(out var pt);
        foreach (var m in Native.GetMonitors())
        {
            var o = new PickerOverlay(this, m, _mode == Mode.Region);
            _overlays.Add(o);
            o.Show();
        }
        (_overlays.FirstOrDefault(o => o.Monitor.Bounds.Contains(pt.X, pt.Y)) ?? _overlays.FirstOrDefault())?.Activate();
        _timer.Start();
        return _tcs.Task;
    }

    private void Tick()
    {
        Native.GetCursorPos(out var p);
        if (_mode == Mode.Region)
        {
            PxRect sel = default;
            if (_dragging && _dragMonitor != null)
            {
                var b = _dragMonitor.Bounds;
                int x = Math.Clamp(p.X, b.Left, b.Right), y = Math.Clamp(p.Y, b.Top, b.Bottom);
                sel = new PxRect(Math.Min(x, _startX), Math.Min(y, _startY), Math.Abs(x - _startX), Math.Abs(y - _startY));
            }
            string? label = sel.IsEmpty ? null : $"{sel.Width & ~1} × {sel.Height & ~1}";
            foreach (var o in _overlays) o.Render(sel, label, p);
        }
        else
        {
            _hover = _windows.FirstOrDefault(w => w.Bounds.Contains(p.X, p.Y));
            foreach (var o in _overlays) o.Render(_hover?.Bounds ?? default, _hover?.Title, p);
        }
    }

    internal void MouseDown(PickerOverlay overlay)
    {
        Native.GetCursorPos(out var p);
        if (_mode == Mode.Window)
        {
            _hover = _windows.FirstOrDefault(w => w.Bounds.Contains(p.X, p.Y));
            if (_hover != null) Finish(_hover);
            return;
        }
        var b = overlay.Monitor.Bounds;
        _dragMonitor = overlay.Monitor;
        _startX = Math.Clamp(p.X, b.Left, b.Right);
        _startY = Math.Clamp(p.Y, b.Top, b.Bottom);
        _dragging = true;
        overlay.CaptureMouse();
    }

    internal void MouseUp(PickerOverlay overlay)
    {
        if (_mode != Mode.Region || !_dragging || _dragMonitor == null) return;
        _dragging = false;
        overlay.ReleaseMouseCapture();
        Native.GetCursorPos(out var p);
        var b = _dragMonitor.Bounds;
        int x = Math.Clamp(p.X, b.Left, b.Right), y = Math.Clamp(p.Y, b.Top, b.Bottom);
        var sel = new PxRect(Math.Min(x, _startX), Math.Min(y, _startY), Math.Abs(x - _startX) & ~1, Math.Abs(y - _startY) & ~1);
        if (sel.Width >= 32 && sel.Height >= 32) Finish(sel);
    }

    internal void Cancel() => Finish(null);

    private void Finish(object? result)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        foreach (var o in _overlays) o.Close();
        _tcs.TrySetResult(result);
    }
}

internal sealed class PickerOverlay : PhysicalWindow
{
    private readonly OverlayPicker _picker;
    private readonly Path _dim;
    private readonly Rectangle _frame;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private PxRect _last = new(-1, -1, -1, -1);
    private string? _lastLabel;

    public MonitorInfo Monitor { get; }

    public PickerOverlay(OverlayPicker picker, MonitorInfo monitor, bool regionMode)
        : base(monitor.Bounds, clickThrough: false, excludeFromCapture: true)
    {
        _picker = picker;
        Monitor = monitor;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // alfa 1 = okno přijímá myš
        Cursor = regionMode ? Cursors.Cross : Cursors.Hand;
        Focusable = true;

        var accent = Color.FromRgb(0xEF, 0x44, 0x44);
        _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(regionMode ? (byte)120 : (byte)70, 0, 0, 0)), IsHitTestVisible = false };
        _frame = new Rectangle
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontFamily = new FontFamily("Segoe UI Semibold") };
        _label = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _labelText
        };
        var hint = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 36, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(230, 15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22, 12, 22, 12),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = regionMode
                    ? "Táhněte myší přes oblast, kterou chcete nahrávat   •   Esc / pravé tlačítko = zrušit"
                    : "Klikněte na okno, které chcete nahrávat   •   Esc / pravé tlačítko = zrušit",
                Foreground = Brushes.White,
                FontSize = 15,
                FontFamily = new FontFamily("Segoe UI")
            }
        };

        var root = new Grid();
        root.Children.Add(_dim);
        root.Children.Add(_frame);
        root.Children.Add(_label);
        root.Children.Add(hint);
        Content = root;

        MouseLeftButtonDown += (_, e) => { e.Handled = true; _picker.MouseDown(this); };
        MouseLeftButtonUp += (_, e) => { e.Handled = true; _picker.MouseUp(this); };
        MouseRightButtonDown += (_, e) => { e.Handled = true; _picker.Cancel(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _picker.Cancel(); };
        Deactivated += (_, _) => { };
    }

    public void Render(PxRect hole, string? label, Native.POINT cursor)
    {
        if (hole == _last && label == _lastLabel && ActualWidth > 0 && _dim.Data != null) return;
        _last = hole;
        _lastLabel = label;

        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        var inter = hole.IsEmpty ? default : hole.Intersect(Monitor.Bounds);
        if (inter.IsEmpty)
        {
            _dim.Data = full;
            _frame.Visibility = Visibility.Collapsed;
            _label.Visibility = Visibility.Collapsed;
            return;
        }

        var tl = ToLocal(inter.Left, inter.Top);
        var br = ToLocal(inter.Right, inter.Bottom);
        var local = new Rect(tl, br);
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(full);
        group.Children.Add(new RectangleGeometry(local));
        _dim.Data = group;

        _frame.Margin = new Thickness(local.X, local.Y, 0, 0);
        _frame.Width = local.Width;
        _frame.Height = local.Height;
        _frame.Visibility = Visibility.Visible;

        if (string.IsNullOrEmpty(label))
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }
        _labelText.Text = label.Length > 80 ? label[..77] + "…" : label;
        double y = local.Bottom + 6;
        if (y + 30 > ActualHeight) y = Math.Max(0, local.Bottom - 32);
        _label.Margin = new Thickness(Math.Max(0, local.X), y, 0, 0);
        _label.Visibility = Visibility.Visible;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  Rámeček kolem nahrávané oblasti (vidíte ho vy, nahrávka ne)
// ════════════════════════════════════════════════════════════════════════════
public sealed class RecordingFrame : PhysicalWindow
{
    private const int Pad = 4;
    private readonly Border _border;

    public RecordingFrame(PxRect region)
        : base(new PxRect(region.Left - Pad, region.Top - Pad, region.Width + 2 * Pad, region.Height + 2 * Pad),
               clickThrough: true, excludeFromCapture: true)
    {
        _border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(230, 0xEF, 0x44, 0x44)),
            BorderThickness = new Thickness(3)
        };
        Content = _border;
        Loaded += (_, _) => UpdateThickness();
        DpiChanged += (_, _) => UpdateThickness();
    }

    private void UpdateThickness() => _border.BorderThickness = new Thickness(3 / Scale);

    public void SetPaused(bool paused) =>
        _border.BorderBrush = new SolidColorBrush(paused ? Color.FromArgb(230, 0xF5, 0x9E, 0x0B) : Color.FromArgb(230, 0xEF, 0x44, 0x44));
}

// ════════════════════════════════════════════════════════════════════════════
//  Indikátor v rohu obrazovky: blikající tečka, čas, kontrola zápisu
//  (vidíte ho vy, nahrávka ne; kliknutí jím procházejí)
// ════════════════════════════════════════════════════════════════════════════
public sealed class RecordingIndicator : PhysicalWindow
{
    private const double W = 250, H = 84, BarW = 68;
    private static readonly SolidColorBrush Red = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush Amber = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush Green = new(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush Soft = new(Color.FromRgb(0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush NormalBorder = new(Color.FromArgb(120, 0x7F, 0x1D, 0x1D));

    private readonly Ellipse _dot;
    private readonly TextBlock _time;
    private readonly TextBlock _detail;
    private readonly Border _pill;
    private readonly Grid _meters;
    private readonly TextBlock _micLabel, _sysLabel;
    private readonly Border _micBack, _sysBack, _micFill, _sysFill;

    private static (TextBlock Label, Border Back, Border Fill) MakeMeter(string name)
    {
        var label = new TextBlock { Text = name, Foreground = Soft, FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 26 };
        var gradient = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(BarW, 0) };
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x16, 0xA3, 0x4A), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.72));   // −12 dB
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xEA, 0xB3, 0x08), 0.86));   // −6 dB
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 0.97));   // −2 dB
        var fill = new Border { Background = gradient, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(2) };
        var back = new Border
        {
            Width = BarW + 2,
            Height = 8,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = fill
        };
        return (label, back, fill);
    }

    private static void SetMeter(TextBlock label, Border back, Border fill, double? level, bool clip)
    {
        var vis = level.HasValue ? Visibility.Visible : Visibility.Collapsed;
        label.Visibility = vis;
        back.Visibility = vis;
        if (level is double l) fill.Width = Math.Clamp(l, 0, 1) * BarW;
        label.Foreground = clip ? Red : Soft;
        back.BorderBrush = clip ? Red : Brushes.Transparent;
    }

    public RecordingIndicator(MonitorInfo monitor, IndicatorCorner corner)
        : base(Place(monitor, corner), clickThrough: true, excludeFromCapture: true)
    {
        _dot = new Ellipse { Width = 16, Height = 16, Fill = Red, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _time = new TextBlock
        {
            Text = "00:00:00",
            Foreground = Brushes.White,
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Cascadia Mono, Consolas")
        };
        _detail = new TextBlock { Text = "spouštím…", Foreground = Soft, FontSize = 11.5, FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis };

        var rec = new TextBlock { Text = "REC", Foreground = Red, FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 3, 0, 0) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_time);
        top.Children.Add(rec);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(top);
        text.Children.Add(_detail);

        (_micLabel, _micBack, _micFill) = MakeMeter("MIC");
        (_sysLabel, _sysBack, _sysFill) = MakeMeter("SYS");
        _meters = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(8), GridLength.Auto, GridLength.Auto })
            _meters.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        Grid.SetColumn(_micBack, 1);
        Grid.SetColumn(_sysLabel, 3);
        Grid.SetColumn(_sysBack, 4);
        _meters.Children.Add(_micLabel);
        _meters.Children.Add(_micBack);
        _meters.Children.Add(_sysLabel);
        _meters.Children.Add(_sysBack);
        text.Children.Add(_meters);

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_dot, Dock.Left);
        row.Children.Add(_dot);
        row.Children.Add(text);

        _pill = new Border
        {
            Width = W,
            Height = H,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(215, 12, 17, 29)),
            BorderBrush = NormalBorder,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(14, 6, 12, 6),
            Child = row
        };
        Content = new Viewbox { Child = _pill };
    }

    private static PxRect Place(MonitorInfo m, IndicatorCorner corner)
    {
        int w = (int)(W * m.Scale), h = (int)(H * m.Scale), pad = (int)(14 * m.Scale);
        var a = m.WorkArea.IsEmpty ? m.Bounds : m.WorkArea;
        int x = corner is IndicatorCorner.TopLeft or IndicatorCorner.BottomLeft ? a.Left + pad : a.Right - w - pad;
        int y = corner is IndicatorCorner.TopLeft or IndicatorCorner.TopRight ? a.Top + pad : a.Bottom - h - pad;
        return new PxRect(x, y, w, h);
    }

    public void Update(string time, bool blinkOn, bool paused, bool finishing, string detail, bool warning,
        double? micLevel, bool micClip, double? sysLevel, bool sysClip)
    {
        _time.Text = time;
        _detail.Text = detail;
        SetMeter(_micLabel, _micBack, _micFill, micLevel, micClip);
        SetMeter(_sysLabel, _sysBack, _sysFill, sysLevel, sysClip);
        _meters.Visibility = micLevel.HasValue || sysLevel.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (!warning && !finishing && (micClip || sysClip))
        {
            _dot.Fill = Red;
            _dot.Opacity = blinkOn ? 1 : 0.2;
            _detail.Foreground = Red;
            _pill.BorderBrush = Red;
            return;
        }
        if (finishing)
        {
            _dot.Fill = Soft;
            _dot.Opacity = 1;
            _detail.Foreground = Soft;
            _pill.BorderBrush = NormalBorder;
            return;
        }
        if (warning)
        {
            _dot.Fill = Amber;
            _dot.Opacity = blinkOn ? 1 : 0.35;
            _detail.Foreground = Amber;
            _pill.BorderBrush = Amber;
            return;
        }
        _dot.Fill = paused ? Amber : Red;
        _dot.Opacity = paused || blinkOn ? 1 : 0.2;
        _detail.Foreground = paused ? Amber : Green;
        _pill.BorderBrush = NormalBorder;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  „Živá“ plocha: 2×2 px téměř průhledné okno v rohu monitoru mění každý snímek
//  průhlednost o 1/255. ScreenRecorderLib při nehybném obrazu každých 100 snímků
//  vyprázdní enkodér a zahodí tím ~0,2 s videa (naměřeno); neustálá drobná změna
//  plochy tomu zabrání. Okem ani ve videu to není vidět.
// ════════════════════════════════════════════════════════════════════════════
public sealed class CaptureKeepAlive : PhysicalWindow
{
    private readonly SolidColorBrush _a = new(Color.FromArgb(1, 0, 0, 0));
    private readonly SolidColorBrush _b = new(Color.FromArgb(2, 0, 0, 0));
    private readonly Border _pixel;
    private bool _flip;

    public CaptureKeepAlive(MonitorInfo monitor, bool excludeFromCapture)
        : base(new PxRect(monitor.Bounds.Right - 2, monitor.Bounds.Bottom - 2, 2, 2), clickThrough: true, excludeFromCapture)
    {
        _a.Freeze();
        _b.Freeze();
        _pixel = new Border { Background = _a };
        Content = _pixel;
        CompositionTarget.Rendering += Tick;
        Closed += (_, _) => CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? sender, EventArgs e)
    {
        _flip = !_flip;
        _pixel.Background = _flip ? _b : _a;
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  Odpočet před startem
// ════════════════════════════════════════════════════════════════════════════
public sealed class CountdownWindow : PhysicalWindow
{
    private readonly TextBlock _number;

    private CountdownWindow(MonitorInfo monitor)
        : base(Centered(monitor), clickThrough: true, excludeFromCapture: true)
    {
        _number = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 110,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -8, 0, 0)
        };
        var caption = new TextBlock
        {
            Text = "nahrávání začne za",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 26, 0, 0)
        };
        var grid = new Grid { Width = 240, Height = 240 };
        grid.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(225, 15, 23, 42)), Stroke = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), StrokeThickness = 5 });
        grid.Children.Add(caption);
        grid.Children.Add(_number);
        Content = new Viewbox { Child = grid };
    }

    private static PxRect Centered(MonitorInfo m)
    {
        int size = (int)(240 * m.Scale);
        return new PxRect(m.Bounds.Left + (m.Bounds.Width - size) / 2, m.Bounds.Top + (m.Bounds.Height - size) / 2, size, size);
    }

    /// <returns>true, pokud odpočet doběhl; false při zrušení.</returns>
    public static async Task<bool> RunAsync(MonitorInfo monitor, int seconds, CancellationToken ct)
    {
        var w = new CountdownWindow(monitor);
        w.Show();
        try
        {
            for (int i = seconds; i > 0; i--)
            {
                w._number.Text = i.ToString();
                await Task.Delay(1000, ct);
            }
            return true;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
        finally
        {
            w.Close();
        }
    }
}
