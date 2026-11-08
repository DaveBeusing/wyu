using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

// The desktop compositor blends these click-through panels over the game window.
// Their native handles belong to a separate thread from the renderer and simulation.
internal sealed partial class AlphaOverlay : IDisposable
{
    private const byte PanelOpacity = 179;
    private const uint PopupStyle = 0x80000000;
    private const uint OverlayStyle = 0x080800A0; // No activate, layered, tool window, transparent.
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private readonly nint _owner;
    private volatile string? _menu;
    private volatile string? _performance;

    internal AlphaOverlay(bool activateOwner)
    {
        using var process = Process.GetCurrentProcess();
        var owner = process.MainWindowHandle;
        if (owner == 0)
            throw new InvalidOperationException("The game window is not available for the alpha overlay.");
        _owner = owner;
        if (activateOwner)
            _ = SetForegroundWindow(owner);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(owner, ready)) { IsBackground = true, Name = "WYU overlays" };
        _thread.Start();
        try
        {
            ready.Task.GetAwaiter().GetResult();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal string? Menu { set => _menu = value; }
    internal string? Performance { set => _performance = value; }

    internal bool AllowFocusHandoff() =>
        GetForegroundWindow() == _owner && AllowSetForegroundWindow((uint)Environment.ProcessId) != 0;

    public void Dispose()
    {
        _stop.Set();
        _thread.Join();
        _stop.Dispose();
    }

    private void Run(nint owner, TaskCompletionSource ready)
    {
        nint sidebar = 0, telemetry = 0;
        try
        {
            sidebar = CreateWindowEx(OverlayStyle, "STATIC", "WYU sidebar", PopupStyle, 0, 0, 0, 0, owner, 0, 0, 0);
            telemetry = CreateWindowEx(OverlayStyle, "STATIC", "WYU performance", PopupStyle, 0, 0, 0, 0, owner, 0, 0, 0);
            if (sidebar == 0 || telemetry == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var menu = new Panel(sidebar, "WYU sidebar", 18);
            var metrics = new Panel(telemetry, "WYU performance", 16);
            ready.SetResult();
            do
            {
                while (PeekMessage(out var message, 0, 0, 0, 1))
                    DispatchMessage(ref message);

                if (GetForegroundWindow() != owner || IsIconic(owner) != 0 || GetClientRect(owner, out var bounds) == 0)
                {
                    menu.Hide();
                    metrics.Hide();
                    continue;
                }
                var origin = new Point();
                if (ClientToScreen(owner, ref origin) == 0 || bounds.Right <= 0 || bounds.Bottom <= 0)
                    continue;

                var performance = _performance;
                var width = Math.Min(340, bounds.Right);
                var height = Math.Min(124, bounds.Bottom);
                var offset = performance is null ? 0 : height;
                menu.Update(_menu, new Point(origin.X, origin.Y + offset), new Size(width, bounds.Bottom - offset), 18);
                metrics.Update(performance, origin, new Size(width, height), 10);
            } while (!_stop.Wait(32));
        }
        catch (Exception error)
        {
            if (!ready.TrySetException(error))
                Debug.WriteLine(error);
        }
        finally
        {
            if (telemetry != 0)
                _ = DestroyWindow(telemetry);
            if (sidebar != 0)
                _ = DestroyWindow(sidebar);
        }
    }

    private sealed class Panel(nint handle, string label, int fontSize)
    {
        private (string Text, Point Origin, Size Size, int Top)? _drawn;

        internal void Hide()
        {
            if (_drawn is null)
                return;
            _ = ShowWindow(handle, 0);
            _drawn = null;
        }

        internal void Update(string? text, Point origin, Size size, int top)
        {
            if (text is null || size.Height <= 0)
            {
                Hide();
                return;
            }
            var layout = (text, origin, size, top);
            if (_drawn == layout)
                return;
            if (!Draw(handle, text.Replace(" | ", Environment.NewLine, StringComparison.Ordinal), origin, size, top, fontSize))
                return;
            SetWindowText(handle, label + " | " + text);
            if (_drawn is null)
                _ = ShowWindow(handle, 4);
            _drawn = layout;
        }
    }

    private static bool Draw(nint window, string text, Point origin, Size size, int top, int fontSize)
    {
        var desktop = GetDC(0);
        if (desktop == 0)
            return false;
        var dc = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, size.Width, size.Height);
        var previous = dc != 0 && bitmap != 0 ? SelectObject(dc, bitmap) : 0;
        try
        {
            if (previous == 0 || PatBlt(dc, 0, 0, size.Width, size.Height, 0x00000042) == 0 ||
                SetBkMode(dc, 1) == 0 || SetTextColor(dc, 0x00FFFFFF) == uint.MaxValue)
                return false;
            var font = CreateFont(-fontSize, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            var oldFont = font == 0 ? 0 : SelectObject(dc, font);
            var rect = new Rect { Left = 16, Top = top, Right = size.Width - 12, Bottom = size.Height };
            DrawText(dc, text, text.Length, ref rect, 0x00000810); // Word wrap, no mnemonic processing.
            if (oldFont != 0)
                SelectObject(dc, oldFont);
            if (font != 0)
                _ = DeleteObject(font);
            var source = new Point();
            var blend = new Blend { Opacity = PanelOpacity };
            return UpdateLayeredWindow(window, desktop, ref origin, ref size, dc, ref source, 0, ref blend, 2);
        }
        finally
        {
            if (previous != 0)
                SelectObject(dc, previous);
            if (bitmap != 0)
                _ = DeleteObject(bitmap);
            if (dc != 0)
                _ = DeleteDC(dc);
            _ = ReleaseDC(0, desktop);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Size(int Width, int Height);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Blend { internal byte Operation, Flags, Opacity, Alpha; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint extended, string className, string title, uint style, int x, int y,
        int width, int height, nint owner, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint window, int command);
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial int AllowSetForegroundWindow(uint processId);
    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")]
    private static partial int IsIconic(nint window);
    [LibraryImport("user32.dll")]
    private static partial int GetClientRect(nint window, out Rect bounds);
    [LibraryImport("user32.dll")]
    private static partial int ClientToScreen(nint window, ref Point point);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Message message);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowText(nint window, string text);
    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);
    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);
    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint resource);
    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint resource);
    [LibraryImport("gdi32.dll")]
    private static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeout, uint characterSet, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitch, string face);
    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")]
    private static partial int PatBlt(nint dc, int x, int y, int width, int height, uint raster);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DrawText(nint dc, string text, int length, ref Rect rect, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateLayeredWindow(nint window, nint desktop, ref Point origin, ref Size size,
        nint sourceDc, ref Point source, uint colorKey, ref Blend blend, uint flags);
}
