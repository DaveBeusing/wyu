using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

// A nonrectangular window region requests compositor clipping while the
// client and swap-chain dimensions stay identical in both replay variants.
internal sealed partial class FlipProbeClip(nint window) : IDisposable
{
    internal static FlipProbeClip? ApplyIfRequested()
    {
        if (Environment.GetEnvironmentVariable("WYU_WP15_FLIP") != "composed")
            return null;

        using var process = Process.GetCurrentProcess();
        var handle = process.MainWindowHandle;
        if (handle == 0 || !GetWindowRect(handle, out var bounds))
            throw new InvalidOperationException("The WP15 window bounds are unavailable.");
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var region = CreateRoundRectRgn(0, 0, width, height, 240, 240);
        if (region == 0)
            throw new InvalidOperationException("Could not allocate the WP15 window region.");
        if (SetWindowRgn(handle, region, true) == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            DeleteObject(region);
            throw new Win32Exception(error);
        }

        // SetWindowRgn owns the region after a successful call.
        return new(handle);
    }

    public void Dispose() => SetWindowRgn(window, 0, true);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect bounds);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);
}
