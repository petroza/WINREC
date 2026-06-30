using System;
using System.Runtime.InteropServices;

namespace WINREC;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    public delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFOEX
    {
        public int Size;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    public static (int Left, int Top) GetMonitorOrigin(string deviceName)
    {
        var (l, t, _, _) = GetMonitorRect(deviceName);
        return (l, t);
    }

    public static (int Left, int Top, int Width, int Height) GetMonitorRect(string deviceName)
    {
        int left = 0, top = 0, width = 0, height = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr lp) =>
            {
                var mi = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMon, ref mi) &&
                    string.Equals(mi.DeviceName.TrimEnd('\0'), deviceName.TrimEnd('\0'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    left   = mi.Monitor.Left;
                    top    = mi.Monitor.Top;
                    width  = mi.Monitor.Right  - mi.Monitor.Left;
                    height = mi.Monitor.Bottom - mi.Monitor.Top;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        return (left, top, width, height);
    }

    // Returns the DeviceName of the monitor that contains the given window (nearest).
    public static string? GetMonitorDeviceNameForWindow(IntPtr hwnd)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return null;

        string? result = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr h, IntPtr hdc, ref RECT rc, IntPtr lp) =>
            {
                if (h != hMon) return true;
                var mi = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref mi))
                    result = mi.DeviceName.TrimEnd('\0');
                return false;
            }, IntPtr.Zero);
        return result;
    }
}
