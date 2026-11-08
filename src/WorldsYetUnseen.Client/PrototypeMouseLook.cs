using System.Diagnostics;
using System.Runtime.InteropServices;
using Groundwork.Core;

namespace WorldsYetUnseen.Client;

/// <summary>Relative mouse look for the foreground prototype window.</summary>
internal sealed partial class PrototypeMouseLook(IHostWindowStateSource windowState, AlphaSettings settings) : IDisposable
{
    internal const float MaximumPitch = 1.4f;
    private const float RadiansPerPixel = 0.0025f;
    private const int ClassCursor = -12;
    private const int CrossCursor = 32515;
    private const uint SetCursorMessage = 0x0020;
    private const uint MouseMoveMessage = 0x0200;
    private const uint ClientHit = 1;
    private const uint AbortIfHung = 0x0002;
    private const uint NativeMoveOrMenu = 0x0002 | 0x0004 | 0x0008 | 0x0010;
    private nint _window;
    private nint _originalCursor;
    private nint _activeWindow;
    private ClientArea _activeArea;
    private ulong _activeRevision;
    internal bool Focused => windowState.Read().Focused;

    internal void Update(bool enabled, ref float yaw, ref float pitch)
    {
        var snapshot = windowState.Read();
        if (!enabled || snapshot is not { Phase: HostWindowPhase.Open, Focused: true, ClientWidth: > 0, ClientHeight: > 0 })
        {
            Release();
            return;
        }

        if (_window == 0)
        {
            using var process = Process.GetCurrentProcess();
            _window = process.MainWindowHandle;
        }
        var window = _window;
        if (window == 0 || !CanAim(window) || !TryReadArea(window, out var area))
        {
            Release();
            return;
        }

        if (_activeWindow != window || _activeArea != area || _activeRevision != snapshot.Revision)
        {
            Release();
            Begin(window, area, snapshot.Revision);
            return;
        }

        if (!GetCursorPosition(out var pointer))
        {
            Release();
            return;
        }
        var center = area.Center;
        if (pointer.X == center.X && pointer.Y == center.Y)
            return;
        if (!Center(window, center))
        {
            Release();
            return;
        }

        var scale = RadiansPerPixel * (settings.Sensitivity / 100f);
        yaw = MathF.IEEERemainder(yaw + (pointer.X - center.X) * scale, MathF.Tau);
        var vertical = (pointer.Y - center.Y) * scale;
        pitch = Math.Clamp(pitch + (settings.InvertVertical ? vertical : -vertical), -MaximumPitch, MaximumPitch);
    }

    public void Dispose() => Release();

    private void Begin(nint window, ClientArea area, ulong revision)
    {
        var cross = LoadCursor(0, CrossCursor);
        if (cross == 0 || !OwnsWindow(window))
            return;
        var original = SetClassValue(window, ClassCursor, cross);
        if (original == 0 && Marshal.GetLastPInvokeError() != 0)
            return;
        _originalCursor = original;
        _activeWindow = window;
        _activeArea = area;
        _activeRevision = revision;

        // Recenter without turning the camera when entering or re-entering mouse look.
        if (!Center(window, area.Center))
        {
            Release();
            return;
        }
        RefreshCursor(window);
    }

    private void Release()
    {
        var window = _activeWindow;
        if (window == 0)
            return;
        _activeWindow = 0;
        if (!OwnsWindow(window))
            return;
        SetClassValue(window, ClassCursor, _originalCursor);
        if (GetForegroundWindow() == window && GetCursorPosition(out var pointer) &&
            TryReadArea(window, out var area) && area.Contains(pointer))
            RefreshCursor(window);
    }

    private static bool Center(nint window, NativePoint center) =>
        GetForegroundWindow() == window && OwnsWindow(window) && SetCursorPosition(center.X, center.Y);

    private static bool CanAim(nint window)
    {
        if (GetForegroundWindow() != window)
            return false;
        var gui = new NativeGuiThread { Size = (uint)Marshal.SizeOf<NativeGuiThread>() };
        return GetGUIThreadInfo(0, ref gui) && gui.Focus == window &&
            (gui.Flags & NativeMoveOrMenu) == 0 && gui.Capture == 0;
    }

    private static bool TryReadArea(nint window, out ClientArea area)
    {
        area = default;
        if (!GetClientRect(window, out var bounds))
            return false;
        NativePoint origin = default;
        if (!ClientToScreen(window, ref origin))
            return false;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0)
            return false;
        area = new(origin.X, origin.Y, width, height);
        return true;
    }

    private static bool OwnsWindow(nint window) =>
        GetWindowThreadProcessId(window, out var owner) != 0 && owner == Environment.ProcessId;

    private static void RefreshCursor(nint window) =>
        SendMessageTimeout(window, SetCursorMessage, (nuint)window, (nint)((MouseMoveMessage << 16) | ClientHit),
            AbortIfHung, 10, out _);

    private readonly record struct ClientArea(int X, int Y, int Width, int Height)
    {
        internal NativePoint Center => new() { X = X + Width / 2, Y = Y + Height / 2 };
        internal bool Contains(NativePoint point) =>
            point.X >= X && point.X < X + Width && point.Y >= Y && point.Y < Y + Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGuiThread
    {
        internal uint Size;
        internal uint Flags;
        internal nint Active;
        internal nint Focus;
        internal nint Capture;
        internal nint MenuOwner;
        internal nint MoveSize;
        internal nint Caret;
        internal NativeRect CaretBounds;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint threadId, ref NativeGuiThread gui);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out NativeRect bounds);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPosition(out NativePoint point);

    [LibraryImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPosition(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static partial nint SetClassValue(nint window, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
