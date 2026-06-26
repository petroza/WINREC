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

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

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
        int left = 0, top = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr lp) =>
        {
            var mi = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi) &&
                string.Equals(mi.DeviceName.TrimEnd('\0'), deviceName.TrimEnd('\0'),
                    StringComparison.OrdinalIgnoreCase))
            {
                left = mi.Monitor.Left;
                top = mi.Monitor.Top;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return (left, top);
    }
}
