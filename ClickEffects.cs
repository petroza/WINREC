using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace WINREC;

public enum ClickEffect { Ripple, DoubleRipple, Pulse, Target, Burst, Glow, HoldRing, Dot }

public sealed record ClickStyle(bool Clicks, ClickEffect Effect, Color Left, Color Right, double Size, int DurationMs, bool Halo)
{
    public static Color ParseColor(string? hex, Color fallback)
    {
        try { return hex == null ? fallback : (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    public static ClickStyle From(AppSettings s) => new(
        s.HighlightClicks, s.ClickEffect,
        ParseColor(s.ClickColorLeft, Color.FromRgb(0xE1, 0x20, 0x28)),
        ParseColor(s.ClickColorRight, Color.FromRgb(0x2F, 0x7B, 0xF5)),
        Math.Clamp(s.ClickSize, 0.4, 2.5), Math.Clamp(s.ClickDurationMs, 200, 2000), s.CursorHalo);
}

// ════════════════════════════════════════════════════════════════════════════
//  Kreslení efektů (sdílí galerie i překryvná okna při nahrávání)
// ════════════════════════════════════════════════════════════════════════════
public static class EffectRenderer
{
    public static readonly (ClickEffect Effect, string Name, string Hint)[] All =
    [
        (ClickEffect.Ripple, "Vlnka", "Rozšiřující se kroužek s bílým středem — čisté a výrazné."),
        (ClickEffect.DoubleRipple, "Dvojitá vlna", "Dva tenké kroužky za sebou — elegantní."),
        (ClickEffect.Pulse, "Pulz", "Plný kruh, který se nafoukne a zmizí."),
        (ClickEffect.Target, "Zaměřovač", "Kroužek se stáhne přesně na místo kliknutí."),
        (ClickEffect.Burst, "Jiskry", "Paprsky do stran — hravé, dobře viditelné i v malém videu."),
        (ClickEffect.Glow, "Záře", "Měkká barevná záře bez ostrých hran."),
        (ClickEffect.HoldRing, "Kruh při držení", "Kruh drží, dokud je tlačítko stisknuté — ideální pro tažení myší."),
        (ClickEffect.Dot, "Klasická tečka", "Jednoduchá tečka s bílým okrajem.")
    ];

    public static string Name(ClickEffect e) => All.FirstOrDefault(x => x.Effect == e).Name ?? e.ToString();

    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    private static SolidColorBrush B(Color c, double a)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static Pen P(Color c, double a, double w)
    {
        var p = new Pen(B(c, a), Math.Max(0.5, w)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    /// <param name="r">základní poloměr v DIP</param>
    /// <param name="t">průběh animace 0–1 (u držení = fáze „dýchání“)</param>
    public static void Draw(DrawingContext dc, ClickEffect e, Point c, double r, double t, Color col, bool held)
    {
        t = Math.Clamp(t, 0, 1);
        switch (e)
        {
            case ClickEffect.Ripple:
            {
                double rad = r * (0.35 + 1.25 * EaseOut(t));
                dc.DrawEllipse(B(col, 0.28 * (1 - t)), P(col, 0.95 * (1 - t), r * 0.16 * (1 - t) + 1.2), c, rad, rad);
                double core = Math.Max(0, 1 - t * 2.5);
                if (core > 0) dc.DrawEllipse(B(Colors.White, 0.9 * core), null, c, r * 0.13, r * 0.13);
                break;
            }
            case ClickEffect.DoubleRipple:
            {
                for (int i = 0; i < 2; i++)
                {
                    double ti = Math.Clamp((t - i * 0.22) / 0.78, 0, 1);
                    if (ti <= 0 || ti >= 1) continue;
                    double rad = r * (0.25 + 1.35 * EaseOut(ti));
                    dc.DrawEllipse(null, P(col, 0.95 * (1 - ti), r * 0.09 + 1), c, rad, rad);
                }
                dc.DrawEllipse(B(col, 0.9 * (1 - t)), null, c, r * 0.12, r * 0.12);
                break;
            }
            case ClickEffect.Pulse:
            {
                double rad = r * (0.45 + 0.75 * EaseOut(t));
                dc.DrawEllipse(B(col, 0.55 * (1 - t)), null, c, rad, rad);
                dc.DrawEllipse(B(col, 1 - t), P(Colors.White, 0.8 * (1 - t), 1.5), c, r * 0.2, r * 0.2);
                break;
            }
            case ClickEffect.Target:
            {
                double k = EaseOut(Math.Min(1, t / 0.7));
                double a = t < 0.7 ? 1 : (1 - t) / 0.3;
                double rad = r * (1.5 - 1.05 * k);
                dc.DrawEllipse(null, P(col, a, r * 0.09 + 1), c, rad, rad);
                var pen = P(col, a, r * 0.06 + 1);
                double outer = r * (0.75 - 0.2 * k), inner = r * 0.14;
                dc.DrawLine(pen, new Point(c.X - outer, c.Y), new Point(c.X - inner, c.Y));
                dc.DrawLine(pen, new Point(c.X + inner, c.Y), new Point(c.X + outer, c.Y));
                dc.DrawLine(pen, new Point(c.X, c.Y - outer), new Point(c.X, c.Y - inner));
                dc.DrawLine(pen, new Point(c.X, c.Y + inner), new Point(c.X, c.Y + outer));
                dc.DrawEllipse(B(col, a), null, c, r * 0.06 + 1, r * 0.06 + 1);
                break;
            }
            case ClickEffect.Burst:
            {
                var pen = P(col, 1 - t, r * 0.09 + 1);
                double r1 = r * (0.25 + 0.95 * EaseOut(t));
                double len = r * 0.5 * (1 - t);
                for (int i = 0; i < 10; i++)
                {
                    double ang = i * Math.PI / 5;
                    double dx = Math.Cos(ang), dy = Math.Sin(ang);
                    dc.DrawLine(pen, new Point(c.X + dx * r1, c.Y + dy * r1), new Point(c.X + dx * (r1 + len), c.Y + dy * (r1 + len)));
                }
                double dot = r * 0.14 * (1 - t) + 1;
                dc.DrawEllipse(B(col, 1 - t), null, c, dot, dot);
                break;
            }
            case ClickEffect.Glow:
            {
                double rad = r * (0.7 + 0.7 * EaseOut(t));
                var g = new RadialGradientBrush();
                g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(210 * (1 - t)), col.R, col.G, col.B), 0));
                g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(110 * (1 - t)), col.R, col.G, col.B), 0.45));
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1));
                g.Freeze();
                dc.DrawEllipse(g, null, c, rad, rad);
                break;
            }
            case ClickEffect.HoldRing:
            {
                if (held)
                {
                    double rad = r * 0.62 * (1 + 0.07 * Math.Sin(t * Math.PI * 2));
                    dc.DrawEllipse(B(col, 0.25), P(col, 0.95, r * 0.13), c, rad, rad);
                }
                else
                {
                    double rad = r * (0.62 + 0.9 * EaseOut(t));
                    dc.DrawEllipse(B(col, 0.25 * (1 - t)), P(col, 0.95 * (1 - t), r * 0.13 * (1 - t) + 1), c, rad, rad);
                }
                break;
            }
            case ClickEffect.Dot:
            {
                double a = 1 - t * t;
                dc.DrawEllipse(B(col, 0.85 * a), P(Colors.White, 0.7 * a, 1.5), c, r * 0.42, r * 0.42);
                break;
            }
        }
    }

    public static void DrawHalo(DrawingContext dc, Point c, double r, Color col)
    {
        var g = new RadialGradientBrush();
        g.GradientStops.Add(new GradientStop(Color.FromArgb(95, col.R, col.G, col.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(80, col.R, col.G, col.B), 0.7));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1));
        g.Freeze();
        dc.DrawEllipse(g, P(col, 0.55, 1.5), c, r, r);
    }
}

public sealed class EffectCanvas : FrameworkElement
{
    public new ClickEffect Effect { get; set; }
    public Color Color { get; set; } = Colors.Red;
    public double Radius { get; set; } = 40;
    public double Progress { get; set; }
    public bool Held { get; set; }
    public bool Active { get; set; }
    public bool IsHalo { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Active) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        if (IsHalo) EffectRenderer.DrawHalo(dc, c, Radius, Color);
        else EffectRenderer.Draw(dc, Effect, c, Radius, Progress, Color, Held);
    }
}

/// <summary>Časování jedné animace (stisk → případné držení → doběh).</summary>
public sealed class EffectAnimation(EffectCanvas canvas)
{
    private long _start, _releaseStart;
    private int _duration = 650;

    public EffectCanvas Canvas { get; } = canvas;
    public bool Busy { get; private set; }
    public double ElapsedMs => (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;

    public void Start(ClickEffect effect, Color color, double radius, int durationMs)
    {
        Canvas.Effect = effect;
        Canvas.Color = color;
        Canvas.Radius = radius;
        Canvas.Held = effect == ClickEffect.HoldRing;
        Canvas.Active = true;
        Canvas.Progress = 0;
        _duration = durationMs;
        _start = Stopwatch.GetTimestamp();
        _releaseStart = 0;
        Busy = true;
        Canvas.InvalidateVisual();
    }

    public void Release()
    {
        if (!Busy || !Canvas.Held) return;
        Canvas.Held = false;
        _releaseStart = Stopwatch.GetTimestamp();
    }

    public void Tick()
    {
        if (!Busy) return;
        long now = Stopwatch.GetTimestamp();
        if (Canvas.Held)
        {
            Canvas.Progress = (now - _start) / (double)Stopwatch.Frequency % 1.0;
        }
        else
        {
            long from = _releaseStart != 0 ? _releaseStart : _start;
            int dur = _releaseStart != 0 ? Math.Max(200, _duration * 2 / 3) : _duration;
            double t = (now - from) * 1000.0 / Stopwatch.Frequency / dur;
            if (t >= 1)
            {
                Busy = false;
                Canvas.Active = false;
            }
            Canvas.Progress = Math.Min(1, t);
        }
        Canvas.InvalidateVisual();
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  Nízkoúrovňový hook myši ve vlastním vlákně (nic neunikne, myš se nezpomalí)
// ════════════════════════════════════════════════════════════════════════════
internal sealed class MouseHook : IDisposable
{
    public enum Kind { LeftDown, LeftUp, RightDown, RightUp }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

    private readonly HookProc _proc;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private int _x, _y;

    public event Action<Kind, int, int>? Button;
    public (int X, int Y) Position => (Volatile.Read(ref _x), Volatile.Read(ref _y));

    public MouseHook()
    {
        Native.GetCursorPos(out var p);
        _x = p.X;
        _y = p.Y;
        _proc = Callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "WINREC mouse hook" };
        _thread.Start();
        _started.Wait(2000);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) Log.Error($"Hook myši nejde nainstalovat (chyba {Marshal.GetLastWin32Error()})");
        _started.Set();
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            Volatile.Write(ref _x, info.x);
            Volatile.Write(ref _y, info.y);
            Kind? k = wParam.ToInt32() switch
            {
                0x201 => Kind.LeftDown,
                0x202 => Kind.LeftUp,
                0x204 => Kind.RightDown,
                0x205 => Kind.RightUp,
                _ => null
            };
            if (k != null)
            {
                try { Button?.Invoke(k.Value, info.x, info.y); } catch { }
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x12 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  Malá průhledná okna s efektem — nahrávka je zachytí, kliknutí jimi procházejí
// ════════════════════════════════════════════════════════════════════════════
internal sealed class EffectWindow : PhysicalWindow
{
    private const double SideFactor = 4.6;
    private readonly EffectAnimation _anim;
    private readonly EffectCanvas _canvas = new();
    private (int X, int Y, double Scale) _haloAt = (int.MinValue, 0, 0);

    public bool Busy => _anim.Busy;

    public EffectWindow() : base(new PxRect(-200, -200, 16, 16), clickThrough: true, excludeFromCapture: false)
    {
        Content = _canvas;
        _anim = new EffectAnimation(_canvas);
    }

    public void Play(ClickStyle st, int x, int y, bool right, double scale)
    {
        double rPx = 40 * st.Size * scale;
        int side = (int)Math.Ceiling(rPx * SideFactor);
        MoveTo(new PxRect(x - side / 2, y - side / 2, side, side));
        _anim.Start(st.Effect, right ? st.Right : st.Left, 40 * st.Size, st.DurationMs);
    }

    public void Release() => _anim.Release();
    public void Tick() => _anim.Tick();

    public void ShowHalo(ClickStyle st, int x, int y, double scale)
    {
        double rDip = 26 * st.Size;
        int side = (int)Math.Ceiling(rDip * scale * 2 + 6);
        if (_haloAt.X == x && _haloAt.Y == y && _haloAt.Scale == scale) return;
        _haloAt = (x, y, scale);
        MoveTo(new PxRect(x - side / 2, y - side / 2, side, side));
        if (!_canvas.Active)
        {
            _canvas.IsHalo = true;
            _canvas.Active = true;
            _canvas.Radius = rDip;
            _canvas.Color = Color.FromRgb(0xFA, 0xCC, 0x15);
            _canvas.InvalidateVisual();
        }
    }
}

public sealed class ClickEffectsController : IDisposable
{
    private readonly ClickStyle _st;
    private readonly MouseHook _hook;
    private readonly List<EffectWindow> _pool = [];
    private readonly Dictionary<bool, EffectWindow> _held = [];
    private readonly List<MonitorInfo> _monitors;
    private readonly EffectWindow? _halo;
    private bool _disposed;

    public ClickEffectsController(ClickStyle style)
    {
        _st = style;
        _monitors = Native.GetMonitors();
        if (style.Clicks)
            for (int i = 0; i < 3; i++) _pool.Add(NewWindow());
        if (style.Halo) _halo = NewWindow();

        _hook = new MouseHook();
        var dispatcher = Application.Current.Dispatcher;
        _hook.Button += (k, x, y) => dispatcher.BeginInvoke(() => OnButton(k, x, y));
        CompositionTarget.Rendering += OnRendering;
        Log.Info($"Efekty kliknutí: {style.Effect}, velikost {style.Size:0.##}, {style.DurationMs} ms, kruh u kurzoru={style.Halo}");
    }

    private static EffectWindow NewWindow()
    {
        var w = new EffectWindow();
        w.Show();
        return w;
    }

    private double ScaleAt(int x, int y) =>
        (_monitors.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? _monitors.FirstOrDefault())?.Scale ?? 1;

    private void OnButton(MouseHook.Kind k, int x, int y)
    {
        if (_disposed || !_st.Clicks) return;
        bool right = k is MouseHook.Kind.RightDown or MouseHook.Kind.RightUp;
        if (k is MouseHook.Kind.LeftDown or MouseHook.Kind.RightDown)
        {
            var w = _pool.FirstOrDefault(p => !p.Busy);
            if (w == null)
            {
                if (_pool.Count >= 12) w = _pool[0];
                else _pool.Add(w = NewWindow());
            }
            w.Play(_st, x, y, right, ScaleAt(x, y));
            _held[right] = w;
        }
        else if (_held.Remove(right, out var hw))
        {
            hw.Release();
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        foreach (var w in _pool) w.Tick();
        if (_halo != null)
        {
            var (x, y) = _hook.Position;
            _halo.ShowHalo(_st, x, y, ScaleAt(x, y));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= OnRendering;
        _hook.Dispose();
        foreach (var w in _pool) w.Close();
        _halo?.Close();
    }
}
