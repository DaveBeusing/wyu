using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

// The desktop compositor blends these click-through panels over the game window.
// Their native handles belong to a separate thread from the renderer and simulation.
internal sealed partial class AlphaOverlay : IDisposable
{
    private const byte PanelOpacity = 179;
    private const int ReticleSize = 21;
    private const int PanelLeft = 16, PanelRight = 12, PanelBottom = 10;
    private const uint PanelText = 0x00000810; // Word wrap, no mnemonic processing.
    private const uint CalculateText = 0x00000400;
    private const uint PopupStyle = 0x80000000;
    private const uint OverlayStyle = 0x080800A0; // No activate, layered, tool window, transparent.
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private readonly nint _owner;
    private volatile string? _menu;
    private volatile string? _performance;
    private volatile bool _reticle;

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
    internal bool Reticle { set => _reticle = value; }

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
        nint sidebar = 0, telemetry = 0, reticle = 0, terminal = 0;
        Point? reticleOrigin = null;
        try
        {
            sidebar = CreateWindowEx(OverlayStyle, "STATIC", "WYU sidebar", PopupStyle, 0, 0, 0, 0, owner, 0, 0, 0);
            telemetry = CreateWindowEx(OverlayStyle, "STATIC", "WYU performance", PopupStyle, 0, 0, 0, 0, owner, 0, 0, 0);
            terminal = CreateWindowEx(OverlayStyle, "STATIC", "WYU world console", PopupStyle, 0, 0, 0, 0, owner, 0, 0, 0);
            if (sidebar == 0 || telemetry == 0 || terminal == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var menu = new Panel(sidebar, "WYU sidebar", 18);
            var metrics = new Panel(telemetry, "WYU performance", 16, fitHeight: true);
            var console = new ConsolePanel(terminal);
            ready.SetResult();
            do
            {
                while (PeekMessage(out var message, 0, 0, 0, 1))
                    DispatchMessage(ref message);

                if (IsIconic(owner) != 0 || GetClientRect(owner, out var bounds) == 0)
                {
                    menu.Hide();
                    metrics.Hide();
                    console.Hide();
                    HideReticle();
                    continue;
                }
                var origin = new Point();
                if (ClientToScreen(owner, ref origin) == 0 || bounds.Right <= 0 || bounds.Bottom <= 0)
                    continue;

                if (_console is { } view)
                {
                    menu.Hide();
                    metrics.Hide();
                    HideReticle();
                    if (GetForegroundWindow() == owner && WorldConsole.Fits(bounds.Right, bounds.Bottom))
                    {
                        var consoleWidth = Math.Min(760, bounds.Right);
                        var consoleHeight = Math.Min(316, bounds.Bottom);
                        console.Update(view, new Point(origin.X + (bounds.Right - consoleWidth) / 2, origin.Y),
                            new Size(consoleWidth, consoleHeight));
                    }
                    else
                        console.Hide();
                    continue;
                }
                console.Hide();

                var offset = metrics.Update(_performance, origin, new Size(Math.Min(540, bounds.Right), bounds.Bottom), 10);
                menu.Update(_menu, new Point(origin.X, origin.Y + offset),
                    new Size(Math.Min(340, bounds.Right), bounds.Bottom - offset), 18);

                if (!_reticle || bounds.Right < ReticleSize || bounds.Bottom < ReticleSize)
                {
                    HideReticle();
                    continue;
                }
                if (reticle == 0)
                    reticle = CreateWindowEx(OverlayStyle, "STATIC", "WYU reticle | Aim", PopupStyle, 0, 0, 0, 0, owner, 0, 0, 0);
                var center = new Point(origin.X + bounds.Right / 2 - ReticleSize / 2,
                    origin.Y + bounds.Bottom / 2 - ReticleSize / 2);
                if (reticle != 0 && reticleOrigin != center && DrawReticle(reticle, center))
                {
                    if (reticleOrigin is null)
                        _ = ShowWindow(reticle, 4);
                    reticleOrigin = center;
                }
            } while (!_stop.Wait(32));
        }
        catch (Exception error)
        {
            if (!ready.TrySetException(error))
                Debug.WriteLine(error);
        }
        finally
        {
            if (terminal != 0)
                _ = DestroyWindow(terminal);
            if (reticle != 0)
                _ = DestroyWindow(reticle);
            if (telemetry != 0)
                _ = DestroyWindow(telemetry);
            if (sidebar != 0)
                _ = DestroyWindow(sidebar);
        }

        void HideReticle()
        {
            if (reticleOrigin is null)
                return;
            _ = ShowWindow(reticle, 0);
            reticleOrigin = null;
        }
    }

    private sealed class Panel(nint handle, string label, int fontSize, bool fitHeight = false)
    {
        private (string Text, Point Origin, Size Available, int Top)? _drawn;
        private int _height;

        internal void Hide()
        {
            if (_drawn is null)
                return;
            _ = ShowWindow(handle, 0);
            _drawn = null;
            _height = 0;
        }

        internal int Update(string? text, Point origin, Size available, int top)
        {
            if (text is null || available.Width <= PanelLeft + PanelRight || available.Height <= top)
            {
                Hide();
                return 0;
            }
            var layout = (text, origin, available, top);
            if (_drawn == layout)
                return _height;
            var height = DrawPanel(handle, text.Replace(" | ", Environment.NewLine, StringComparison.Ordinal),
                origin, available, top, fontSize, fitHeight);
            if (height == 0)
                return _height;
            SetWindowText(handle, label + " | " + text);
            if (_drawn is null)
                _ = ShowWindow(handle, 4);
            _drawn = layout;
            _height = height;
            return height;
        }
    }

    private static int DrawPanel(nint window, string text, Point origin, Size available, int top, int fontSize, bool fitHeight)
    {
        var font = CreateFont(-fontSize, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        if (font == 0)
            return 0;
        try
        {
            var height = fitHeight ? MeasurePanelHeight(text, available, top, font) : available.Height;
            if (height == 0)
                return 0;
            var size = available with { Height = height };
            return DrawLayer(window, origin, size, PanelOpacity, 2, dc =>
            {
                if (SetBkMode(dc, 1) == 0 || SetTextColor(dc, 0x00FFFFFF) == uint.MaxValue)
                    return false;
                var oldFont = SelectObject(dc, font);
                if (oldFont == 0)
                    return false;
                try
                {
                    var rect = PanelTextBounds(size.Width, top, height);
                    return DrawText(dc, text, text.Length, ref rect, PanelText) != 0;
                }
                finally
                {
                    _ = SelectObject(dc, oldFont);
                }
            }) ? height : 0;
        }
        finally
        {
            _ = DeleteObject(font);
        }
    }

    private static int MeasurePanelHeight(string text, Size available, int top, nint font)
    {
        var dc = GetDC(0);
        if (dc == 0)
            return 0;
        var oldFont = SelectObject(dc, font);
        try
        {
            if (oldFont == 0)
                return 0;
            var rect = PanelTextBounds(available.Width, top, top);
            var measured = DrawText(dc, text, text.Length, ref rect, PanelText | CalculateText);
            return measured == 0 ? 0 : Math.Min(available.Height, top + measured + PanelBottom);
        }
        finally
        {
            if (oldFont != 0)
                _ = SelectObject(dc, oldFont);
            _ = ReleaseDC(0, dc);
        }
    }

    private static Rect PanelTextBounds(int width, int top, int bottom) =>
        new() { Left = PanelLeft, Top = top, Right = width - PanelRight, Bottom = bottom };

    private static bool DrawReticle(nint window, Point origin) =>
        DrawLayer(window, origin, new Size(ReticleSize, ReticleSize), byte.MaxValue, 1, static dc =>
        {
            const uint Paint = 0x00F00021;
            const uint White = 0x00FF0062;
            var brush = CreateSolidBrush(0x002C2C2C);
            if (brush == 0)
                return false;
            var previous = SelectObject(dc, brush);
            try
            {
                // Black is transparent. The four outlined arms leave the aim point clear.
                return previous != 0 &&
                    PatBlt(dc, 9, 1, 3, 7, Paint) != 0 && PatBlt(dc, 9, 13, 3, 7, Paint) != 0 &&
                    PatBlt(dc, 1, 9, 7, 3, Paint) != 0 && PatBlt(dc, 13, 9, 7, 3, Paint) != 0 &&
                    PatBlt(dc, 10, 2, 1, 5, White) != 0 && PatBlt(dc, 10, 14, 1, 5, White) != 0 &&
                    PatBlt(dc, 2, 10, 5, 1, White) != 0 && PatBlt(dc, 14, 10, 5, 1, White) != 0;
            }
            finally
            {
                if (previous != 0)
                    _ = SelectObject(dc, previous);
                _ = DeleteObject(brush);
            }
        });

    private static bool DrawLayer(nint window, Point origin, Size size, byte opacity, uint flags, Func<nint, bool> paint)
    {
        var desktop = GetDC(0);
        if (desktop == 0)
            return false;
        var dc = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, size.Width, size.Height);
        var previous = dc != 0 && bitmap != 0 ? SelectObject(dc, bitmap) : 0;
        try
        {
            if (previous == 0 || PatBlt(dc, 0, 0, size.Width, size.Height, 0x00000042) == 0 || !paint(dc))
                return false;
            var source = new Point();
            var blend = new Blend { Opacity = opacity };
            return UpdateLayeredWindow(window, desktop, ref origin, ref size, dc, ref source, 0, ref blend, flags);
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
    private static partial nint CreateSolidBrush(uint color);
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
