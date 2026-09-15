using System.Runtime.InteropServices;
using System.Text;

namespace WINREC;

/// <summary>Obdélník ve fyzických pixelech virtuální plochy.</summary>
public readonly record struct PxRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public PxRect Intersect(PxRect o)
    {
        int l = Math.Max(Left, o.Left), t = Math.Max(Top, o.Top);
        int r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? default : new PxRect(l, t, r - l, b - t);
    }

    public bool IsEmpty => Width <= 0 || Height <= 0;
    public override string ToString() => $"{Width} × {Height} px  @ {Left}, {Top}";
}

public sealed record MonitorInfo(string DeviceName, PxRect Bounds, bool IsPrimary, double Scale, PxRect WorkArea);

public sealed record TopWindow(IntPtr Handle, string Title, PxRect Bounds, int ProcessId);

internal static class Native
{
    // ── Monitory ────────────────────────────────────────────────────────────
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                double scale = GetDpiForMonitor(h, 0, out var dx, out _) == 0 ? dx / 96.0 : 1.0;
                var r = mi.Monitor;
                var wa = mi.WorkArea;
                list.Add(new MonitorInfo(mi.DeviceName.TrimEnd('\0'),
                    new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                    (mi.Flags & 1) != 0, scale,
                    new PxRect(wa.Left, wa.Top, wa.Right - wa.Left, wa.Bottom - wa.Top)));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ── Kurzor / okna ───────────────────────────────────────────────────────
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rc, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    public static string GetTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Viditelné hranice okna bez neviditelného stínového okraje Windows 10/11.</summary>
    public static PxRect GetWindowBounds(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hWnd, out r);
        return new PxRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Okna nejvyšší úrovně v pořadí Z (odshora), která dává smysl nahrávat.</summary>
    public static List<TopWindow> GetTopWindows(ICollection<IntPtr>? skip = null)
    {
        var list = new List<TopWindow>();
        EnumWindows((h, _) =>
        {
            if (skip?.Contains(h) == true || !IsWindowVisible(h) || IsIconic(h)) return true;
            if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
            if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero && GetWindowTextLength(h) == 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var b = GetWindowBounds(h);
            if (b.Width < 40 || b.Height < 30) return true;
            var title = GetTitle(h);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(h, out int pid);
            list.Add(new TopWindow(h, title, b, pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ── Pomocné styly oken ──────────────────────────────────────────────────
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOZORDER = 0x4;

    public static IntPtr GetForeground() => GetForegroundWindow();

    /// <summary>Obdélník 1920×1080 (nebo menší) přesně na střed monitoru, ve fyzických pixelech.</summary>
    public static PxRect CenteredFhd(PxRect monitor, int w = 1920, int h = 1080)
    {
        int cw = Math.Min(w, monitor.Width) & ~1;
        int ch = Math.Min(h, monitor.Height) & ~1;
        int left = monitor.Left + (monitor.Width - cw) / 2;
        int top = monitor.Top + (monitor.Height - ch) / 2;
        return new PxRect(left, top, cw, ch);
    }

    /// <summary>
    /// Posadí okno tak, aby jeho VIDITELNÉ hranice (bez neviditelného stínového okraje Win11)
    /// přesně padly na cílový obdélník. Maximalizované/minimalizované okno nejdřív obnoví.
    /// </summary>
    public static bool SnapWindowVisibleBounds(IntPtr hWnd, PxRect target)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        if (IsIconic(hWnd) || IsZoomed(hWnd))
        {
            ShowWindow(hWnd, 9 /* SW_RESTORE */);
            System.Threading.Thread.Sleep(60);
        }
        if (!GetWindowRect(hWnd, out var o)) return false;
        var v = GetWindowBounds(hWnd);   // viditelné hranice přes DWM
        int dl = v.Left - o.Left, dt = v.Top - o.Top;
        int dr = o.Right - v.Right, db = o.Bottom - v.Bottom;
        return SetWindowPos(hWnd, IntPtr.Zero,
            target.Left - dl, target.Top - dt,
            target.Width + dl + dr, target.Height + dt + db,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    /// <summary>Okno bude viditelné na monitoru, ale nikdy se neobjeví v nahrávce (Windows 10 2004+).</summary>
    public static bool ExcludeFromCapture(IntPtr hWnd, bool exclude = true) =>
        SetWindowDisplayAffinity(hWnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);

    public static void MakeClickThrough(IntPtr hWnd)
    {
        var ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static void HideFromAltTab(IntPtr hWnd)
    {
        var ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
    }

    // ── Globální klávesové zkratky ──────────────────────────────────────────
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    // ── Úspora energie: nenechat PC usnout během nahrávání ──────────────────
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);

    // ── Priorita GPU (stejný trik jako OBS) ─────────────────────────────────
    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr hProcess, int priority);
    private const int D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH = 4;

    /// <summary>Snímání a kódování dostane přednost, i když GPU vytěžují jiné aplikace.</summary>
    public static bool SetHighGpuPriority()
    {
        try
        {
            return D3DKMTSetProcessSchedulingPriorityClass(
                System.Diagnostics.Process.GetCurrentProcess().Handle, D3DKMT_SCHEDULINGPRIORITYCLASS_HIGH) == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Kontrola prostředí ──────────────────────────────────────────────────
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr h);

    public static bool CanLoad(string dll)
    {
        var h = LoadLibraryEx(dll, IntPtr.Zero, 0x800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
        if (h == IntPtr.Zero) return false;
        FreeLibrary(h);
        return true;
    }
}
