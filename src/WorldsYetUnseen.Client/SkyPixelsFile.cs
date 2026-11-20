using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

internal sealed partial class SkyPixels
{
    internal sealed class CaptureWindow : IDisposable
    {
        private readonly nint _window;

        internal CaptureWindow()
        {
            using var process = Process.GetCurrentProcess();
            _window = process.MainWindowHandle;
            if (_window == 0 || !SetCaptureWindowPos(_window, new(-1), 0, 0, 0, 0, 0x13))
                throw new Win32Exception();
        }

        internal void RequireUnobstructed(int width, int height)
        {
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
                    throw new InvalidOperationException("The twilight capture window is obstructed or outside the desktop.");
            }
        }

        public void Dispose() => _ = SetCaptureWindowPos(_window, new(-2), 0, 0, 0, 0, 0x13);
    }

    private static void PhysicalClientBounds(nint window, ref int width, ref int height, out Point origin)
    {
        origin = new Point();
        var end = new Point { X = width, Y = height };
        if (ClientToScreen(window, ref origin) == 0 || ClientToScreen(window, ref end) == 0 ||
            !LogicalToPhysicalPointForPerMonitorDPI(window, ref origin) ||
            !LogicalToPhysicalPointForPerMonitorDPI(window, ref end))
            throw new Win32Exception();
        width = end.X - origin.X;
        height = end.Y - origin.Y;
        if (width is < 1 or > 8192 || height is < 1 or > 8192)
            throw new InvalidOperationException("Invalid physical client capture dimensions.");
    }

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogicalToPhysicalPointForPerMonitorDPI(nint window, ref Point point);
}
