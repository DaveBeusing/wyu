using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

// Desktop smoke capture of the presented client surface.
internal sealed partial class SkyPixels(int width, int height, byte[] colors)
{
    private int Width { get; } = width;
    private int Height { get; } = height;
    private byte[] Colors { get; } = colors;

    internal static SkyPixels Capture(int width, int height)
    {
        using var process = Process.GetCurrentProcess();
        var origin = new Point();
        if (ClientToScreen(process.MainWindowHandle, ref origin) == 0)
            throw new Win32Exception();
        var screen = GetDC(0);
        if (screen == 0)
            throw new Win32Exception();
        var memory = CreateCompatibleDC(screen);
        nint bitmap = 0, old = 0;
        try
        {
            var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(screen, in header, 0, out var pixels, 0, 0);
            if (memory == 0 || bitmap == 0 || (old = SelectObject(memory, bitmap)) == 0 ||
                BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020) == 0)
                throw new Win32Exception();
            var result = new byte[checked(width * height * 4)];
            Marshal.Copy(pixels, result, 0, result.Length);
            return new(width, height, result);
        }
        finally
        {
            if (old != 0) _ = SelectObject(memory, old);
            if (bitmap != 0) _ = DeleteObject(bitmap);
            if (memory != 0) _ = DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    internal static void Verify(SkyPixels before, SkyPixels after, ulong seed, string direction, string feature)
    {
        if (before.Width != after.Width || before.Height != after.Height)
            throw new InvalidOperationException("Sky capture sizes differ at handoff.");
        var different = 0;
        var lit = 0;
        var dark = 0;
        double sunXBefore = 0, sunYBefore = 0, sunXAfter = 0, sunYAfter = 0;
        var sunBefore = 0;
        var sunAfter = 0;
        for (var i = 0; i < before.Colors.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
                if (Math.Abs(before.Colors[i + c] - after.Colors[i + c]) > 1)
                    different++;
            if (before.Colors[i] > (feature == "sun" ? 210 : 12))
                lit++;
            if (before.Colors[i] < 5)
                dark++;
            if (feature == "sun")
            {
                var x = i / 4 % before.Width;
                var y = i / 4 / before.Width;
                if (before.Colors[i + 2] > 220)
                {
                    sunXBefore += x;
                    sunYBefore += y;
                    sunBefore++;
                }
                if (after.Colors[i + 2] > 220)
                {
                    sunXAfter += x;
                    sunYAfter += y;
                    sunAfter++;
                }
            }
        }
        if (different != 0 || lit < 20 || feature == "stars" && dark < 20)
            throw new InvalidOperationException($"Sky handoff {direction}/{feature}: {different} differing channels, {lit} lit pixels, {dark} dark pixels.");
        if (feature == "sun" && (sunBefore < 20 || sunAfter < 20 ||
            double.Hypot(sunXBefore / sunBefore - sunXAfter / sunAfter, sunYBefore / sunBefore - sunYAfter / sunAfter) > 1))
            throw new InvalidOperationException($"Solar disk missing or shifted at {direction}.");
        Console.WriteLine($"PACKAGE G PIXELS {direction}/{feature} seed={seed} pixels={before.Width * before.Height} lit={lit} differing={different}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, Bits;
        internal uint Compression, ImageSize;
        internal int XPixelsPerMeter, YPixelsPerMeter;
        internal uint ColorsUsed, ColorsImportant;
    }

    [LibraryImport("user32.dll")] private static partial int ClientToScreen(nint window, ref Point point);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint window);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint window, nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, in BitmapHeader header,
        uint usage, out nint pixels, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint value);
    [LibraryImport("gdi32.dll")] private static partial int BitBlt(nint dst, int x, int y, int width, int height,
        nint src, int srcX, int srcY, uint flags);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint value);
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(nint dc);
}
