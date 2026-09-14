using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WINREC;

/// <summary>Galerie efektů zvýraznění kliknutí s živými náhledy a zkušební plochou.</summary>
public sealed class ClickEffectGallery : Window
{
    private static readonly string[] Palette =
        ["#E12028", "#F97316", "#FACC15", "#22C55E", "#06B6D4", "#2F7BF5", "#A855F7", "#FFFFFF"];

    private ClickEffect _effect;
    private Color _left, _right;
    private readonly Slider _size, _duration;
    private readonly CheckBox _halo;
    private readonly TextBlock _hint, _sizeText, _durText;
    private readonly List<(ClickEffect Effect, Border Tile, EffectAnimation Anim, double Offset)> _tiles = [];
    private readonly List<(Ellipse Swatch, Color Color, bool Left)> _swatches = [];
    private readonly Canvas _pad;
    private readonly List<EffectAnimation> _padAnims = [];
    private readonly Dictionary<bool, EffectAnimation> _padHeld = [];
    private readonly EffectCanvas _padHalo = new() { IsHalo = true, Width = 70, Height = 70, IsHitTestVisible = false };
    private readonly DateTime _opened = DateTime.Now;

    public ClickEffectGallery(AppSettings s)
    {
        Title = "WINREC — efekt zvýraznění kliknutí";
        Width = 804;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("Bg");
        Foreground = (Brush)FindResource("Text");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;

        _effect = s.ClickEffect;
        _left = ClickStyle.ParseColor(s.ClickColorLeft, Color.FromRgb(0xE1, 0x20, 0x28));
        _right = ClickStyle.ParseColor(s.ClickColorRight, Color.FromRgb(0x2F, 0x7B, 0xF5));

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = "Vyberte efekt kliknutí", FontSize = 19, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Náhledy se přehrávají samy. Efekt je v nahrávce celé obrazovky i oblasti. Při nahrávání samotného okna se místo něj použije jednoduchá tečka ve zvolené barvě.",
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 4, 0, 14)
        });

        // Dlaždice
        var wrap = new WrapPanel();
        int i = 0;
        foreach (var (effect, name, hint) in EffectRenderer.All)
        {
            var canvas = new EffectCanvas { Margin = new Thickness(0, 0, 0, 24), IsHitTestVisible = false };
            var label = new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 8),
                IsHitTestVisible = false
            };
            var grid = new Grid { Width = 166, Height = 130, Background = Brushes.Transparent };
            grid.Children.Add(canvas);
            grid.Children.Add(label);
            var tile = new Border
            {
                Child = grid,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 10, 10),
                Background = (Brush)FindResource("Card"),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                ToolTip = hint
            };
            var eff = effect;
            tile.MouseLeftButtonUp += (_, _) => { _effect = eff; RefreshSelection(); };
            _tiles.Add((effect, tile, new EffectAnimation(canvas), i * 140));
            wrap.Children.Add(tile);
            i++;
        }
        root.Children.Add(wrap);

        _hint = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new Thickness(2, 0, 0, 12) };
        root.Children.Add(_hint);

        // Nastavení
        var settings = new Grid();
        settings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        settings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 4; r++) settings.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });

        AddRow(settings, 0, "Levé tlačítko", Swatches(left: true));
        AddRow(settings, 1, "Pravé tlačítko", Swatches(left: false));

        _size = new Slider { Minimum = 0.5, Maximum = 2.2, Value = Math.Clamp(s.ClickSize, 0.5, 2.2), Width = 260, SmallChange = 0.05, LargeChange = 0.1 };
        _sizeText = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _size.ValueChanged += (_, _) => UpdateTexts();
        AddRow(settings, 2, "Velikost", Horizontal(_size, _sizeText));

        _duration = new Slider { Minimum = 300, Maximum = 1500, Value = Math.Clamp(s.ClickDurationMs, 300, 1500), Width = 260, SmallChange = 50, LargeChange = 100 };
        _durText = new TextBlock { Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _duration.ValueChanged += (_, _) => UpdateTexts();
        AddRow(settings, 3, "Délka animace", Horizontal(_duration, _durText));
        root.Children.Add(settings);

        _halo = new CheckBox
        {
            Content = "Stálý průsvitný žlutý kruh kolem kurzoru (divák lépe sleduje, kde je myš)",
            IsChecked = s.CursorHalo,
            Margin = new Thickness(112, 4, 0, 0)
        };
        _halo.Checked += (_, _) => _padHalo.Active = true;
        _halo.Unchecked += (_, _) => _padHalo.Active = false;
        root.Children.Add(_halo);

        // Zkušební plocha
        _pad = new Canvas
        {
            Height = 180,
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x16, 0x26)),
            ClipToBounds = true,
            Cursor = Cursors.Arrow
        };
        _padHalo.Color = Color.FromRgb(0xFA, 0xCC, 0x15);
        _padHalo.Active = s.CursorHalo;
        _pad.Children.Add(_padHalo);
        _pad.MouseDown += PadDown;
        _pad.MouseUp += PadUp;
        _pad.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(_pad);
            _padHalo.Radius = 26 * _size.Value;
            _padHalo.Width = _padHalo.Height = _padHalo.Radius * 2 + 6;
            Canvas.SetLeft(_padHalo, p.X - _padHalo.Width / 2);
            Canvas.SetTop(_padHalo, p.Y - _padHalo.Height / 2);
            _padHalo.InvalidateVisual();
        };
        _pad.MouseLeave += (_, _) => Canvas.SetLeft(_padHalo, -500);
        Canvas.SetLeft(_padHalo, -500);

        var padGrid = new Grid();
        padGrid.Children.Add(_pad);
        padGrid.Children.Add(new TextBlock
        {
            Text = "Vyzkoušejte: klikněte sem levým nebo pravým tlačítkem (i podržte)",
            Foreground = (Brush)FindResource("Subtle"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 0, 0),
            IsHitTestVisible = false
        });
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = (Brush)FindResource("FieldBorder"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 14, 0, 0),
            ClipToBounds = true,
            Child = padGrid
        });

        // Tlačítka
        var ok = new Button { Content = "Použít", IsDefault = true, Padding = new Thickness(26, 9, 26, 9), Background = (Brush)FindResource("AccentDark"), BorderBrush = (Brush)FindResource("Accent"), FontWeight = FontWeights.SemiBold };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Zrušit", IsCancel = true, Padding = new Thickness(20, 9, 20, 9), Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = new Border { Background = (Brush)FindResource("Bg"), Child = root };
        RefreshSelection();
        UpdateTexts();

        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    public void ApplyTo(AppSettings s)
    {
        s.ClickEffect = _effect;
        s.ClickColorLeft = ToHex(_left);
        s.ClickColorRight = ToHex(_right);
        s.ClickSize = Math.Round(_size.Value, 2);
        s.ClickDurationMs = (int)Math.Round(_duration.Value / 10) * 10;
        s.CursorHalo = _halo.IsChecked == true;
        s.HighlightClicks = true;
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static void AddRow(Grid g, int row, string label, UIElement content)
    {
        var l = new TextBlock { Text = label, Foreground = (Brush)Application.Current.FindResource("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(l, row);
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        g.Children.Add(l);
        g.Children.Add(content);
    }

    private static StackPanel Horizontal(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    private StackPanel Swatches(bool left)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var hex in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var e = new Ellipse
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 10, 0),
                Fill = new SolidColorBrush(color),
                StrokeThickness = 3,
                Cursor = Cursors.Hand
            };
            e.MouseLeftButtonUp += (_, _) =>
            {
                if (left) _left = color; else _right = color;
                RefreshSelection();
            };
            _swatches.Add((e, color, left));
            p.Children.Add(e);
        }
        return p;
    }

    private void RefreshSelection()
    {
        var accent = (Brush)FindResource("Accent");
        foreach (var t in _tiles)
            t.Tile.BorderBrush = t.Effect == _effect ? accent : (Brush)FindResource("CardBorder");
        foreach (var (swatch, color, left) in _swatches)
        {
            bool selected = color == (left ? _left : _right);
            swatch.Stroke = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            swatch.Width = swatch.Height = selected ? 28 : 24;
        }
        var info = EffectRenderer.All.First(x => x.Effect == _effect);
        _hint.Text = $"Vybráno: {info.Name} — {info.Hint}";
    }

    private void UpdateTexts()
    {
        _sizeText.Text = $"{_size.Value * 100:0} %";
        _durText.Text = $"{_duration.Value / 1000:0.0#} s";
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = (DateTime.Now - _opened).TotalMilliseconds;
        int dur = (int)_duration.Value;
        foreach (var (effect, _, anim, offset) in _tiles)
        {
            double cycle = dur + 700 + (effect == ClickEffect.HoldRing ? 700 : 0);
            double phase = (now + offset) % cycle;
            if (!anim.Busy && phase < 40)
                anim.Start(effect, _left, 24 * Math.Min(_size.Value, 1.25), dur);
            if (anim.Canvas.Held && anim.ElapsedMs > 700) anim.Release();
            anim.Canvas.Color = _left;
            anim.Tick();
        }
        for (int i = _padAnims.Count - 1; i >= 0; i--)
        {
            var a = _padAnims[i];
            a.Tick();
            if (!a.Busy)
            {
                _pad.Children.Remove(a.Canvas);
                _padAnims.RemoveAt(i);
            }
        }
    }

    private void PadDown(object sender, MouseButtonEventArgs e)
    {
        bool right = e.ChangedButton == MouseButton.Right;
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        var p = e.GetPosition(_pad);
        double radius = 40 * _size.Value;
        var canvas = new EffectCanvas { Width = radius * 4.6, Height = radius * 4.6, IsHitTestVisible = false };
        Canvas.SetLeft(canvas, p.X - canvas.Width / 2);
        Canvas.SetTop(canvas, p.Y - canvas.Height / 2);
        _pad.Children.Insert(0, canvas);
        var anim = new EffectAnimation(canvas);
        anim.Start(_effect, right ? _right : _left, radius, (int)_duration.Value);
        _padAnims.Add(anim);
        _padHeld[right] = anim;
        _pad.CaptureMouse();
        e.Handled = true;
    }

    private void PadUp(object sender, MouseButtonEventArgs e)
    {
        bool right = e.ChangedButton == MouseButton.Right;
        if (_padHeld.Remove(right, out var a)) a.Release();
        if (_padHeld.Count == 0) _pad.ReleaseMouseCapture();
    }
}
