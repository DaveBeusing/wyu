using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

internal static partial class AlphaWindowSize
{
    private const uint KeepOrderAndActivation = 0x0014;
    private const uint NearestMonitor = 2;

    internal static bool Fits((int Width, int Height) size) =>
        TryMeasure(size, out _, out _, out _, out _);

    internal static bool TryApply((int Width, int Height) size)
    {
        if (!TryMeasure(size, out var window, out var outer, out var work, out var requested))
            return false;

        var x = Math.Clamp(outer.Left, work.Left, work.Right - requested.Width);
        var y = Math.Clamp(outer.Top, work.Top, work.Bottom - requested.Height);
        if (!SetWindowPos(window, 0, x, y, requested.Width, requested.Height, KeepOrderAndActivation))
            return false;

        if (GetClientRect(window, out var client) && client.Width == size.Width && client.Height == size.Height)
            return true;

        SetWindowPos(window, 0, outer.Left, outer.Top, outer.Width, outer.Height, KeepOrderAndActivation);
        return false;
    }

    private static bool TryMeasure((int Width, int Height) size, out nint window, out Rect outer, out Rect work,
        out (int Width, int Height) requested)
    {
        using var process = Process.GetCurrentProcess();
        window = process.MainWindowHandle;
        outer = work = default;
        requested = default;
        if (window == 0 || IsZoomed(window) || IsIconic(window) || !GetWindowRect(window, out outer) ||
            !GetClientRect(window, out var client))
            return false;

        var monitor = MonitorFromWindow(window, NearestMonitor);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return false;

        work = info.Work;
        requested = (size.Width + outer.Width - client.Width, size.Height + outer.Height - client.Height);
        return requested.Width <= work.Width && requested.Height <= work.Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor, Work;
        internal uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
