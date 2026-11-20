using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

internal sealed partial class SkyPixels
{
    internal sealed class CaptureWindow : IDisposable
    {
        private readonly nint _window;
        private int? _cornerPreference;

        internal CaptureWindow()
        {
            using var dpi = new CaptureDpiScope();
            using var process = Process.GetCurrentProcess();
            _window = process.MainWindowHandle;
            if (_window == 0 || !SetCaptureWindowPos(_window, new(-1), 0, 0, 0, 0, 0x13))
                throw new Win32Exception();
            // Windows 11 rounds the client corners into the desktop. The test
            // needs an opaque client rectangle, not pixels from another window.
            if (DwmGetWindowAttribute(_window, 33, out var previous, sizeof(int)) >= 0)
            {
                _cornerPreference = previous;
                var square = 1;
                if (DwmSetWindowAttribute(_window, 33, in square, sizeof(int)) < 0)
                {
                    Dispose();
                    throw new InvalidOperationException("Could not make the capture window's client corners opaque.");
                }
            }
        }

        internal void RequireUnobstructed(int width, int height)
        {
            using var dpi = new CaptureDpiScope();
            PhysicalClientBounds(_window, ref width, ref height, out var origin);
            foreach (var point in new[]
            {
                new Point { X = origin.X + 1, Y = origin.Y + 1 },
                new Point { X = origin.X + width - 2, Y = origin.Y + 1 },
                new Point { X = origin.X + 1, Y = origin.Y + height - 2 },
                new Point { X = origin.X + width - 2, Y = origin.Y + height - 2 },
                new Point { X = origin.X + width / 2, Y = origin.Y + height / 2 },
            })
            {
                var visible = WindowFromPhysicalPoint(point);
                _ = GetWindowThreadProcessId(visible, out var owner);
                if (owner != Environment.ProcessId)
                    throw new InvalidOperationException($"The capture window is obstructed or outside the desktop: rectangle={origin.X},{origin.Y},{width},{height}; point={point.X},{point.Y}; owner={owner}; expected={Environment.ProcessId}.");
            }
        }

        public void Dispose()
        {
            if (_cornerPreference is { } previous)
            {
                _ = DwmSetWindowAttribute(_window, 33, in previous, sizeof(int));
                _cornerPreference = null;
            }
            _ = SetCaptureWindowPos(_window, new(-2), 0, 0, 0, 0, 0x13);
        }
    }

    private static void PhysicalClientBounds(nint window, ref int width, ref int height, out Point origin)
    {
        using var dpi = new CaptureDpiScope();
        origin = new Point();
        var end = new Point { X = width, Y = height };
        if (!GetClientRect(window, out var client))
            throw new Win32Exception();
        if (width > client.Right - client.Left || height > client.Bottom - client.Top)
            throw new InvalidOperationException("Requested capture extends beyond the current client surface.");
        // In a per-monitor-aware thread ClientToScreen already returns device
        // coordinates. Converting them again scales the screen origin twice.
        if (ClientToScreen(window, ref origin) == 0 || ClientToScreen(window, ref end) == 0)
            throw new Win32Exception();
        width = end.X - origin.X;
        height = end.Y - origin.Y;
        if (width is < 1 or > 8192 || height is < 1 or > 8192)
            throw new InvalidOperationException("Invalid physical client capture dimensions.");
    }

    private sealed class CaptureDpiScope : IDisposable
    {
        private readonly nint _previous = SetThreadDpiAwarenessContext(new(-4));

        internal CaptureDpiScope()
        {
            if (_previous == 0)
                throw new Win32Exception();
        }

        public void Dispose() => _ = SetThreadDpiAwarenessContext(_previous);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientRectangle { internal int Left, Top, Right, Bottom; }

    internal void SaveBmp(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0x4d42);
        writer.Write(checked(54 + Colors.Length));
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(Width);
        writer.Write(-Height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(Colors.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(Colors);
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCaptureWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPhysicalPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out ClientRectangle rectangle);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, uint attribute, in int value, int size);
}
