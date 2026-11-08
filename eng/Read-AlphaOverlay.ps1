if (!('WyuOverlayProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WyuOverlayProbe
{
    private delegate bool Visitor(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point position);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    public static bool HasFocus(IntPtr owner) => owner != IntPtr.Zero && GetForegroundWindow() == owner;

    public static bool ClickVisibleTitle(IntPtr owner)
    {
        if (!GetWindowRect(owner, out Rect bounds) || !GetCursorPos(out Point previous)) return false;
        var target = new Point { X = (bounds.Left + bounds.Right) / 2, Y = bounds.Top + 16 };
        if (WindowFromPoint(target) != owner || !SetCursorPos(target.X, target.Y)) return false;
        try
        {
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            return true;
        }
        finally { SetCursorPos(previous.X, previous.Y); }
    }

    public static string Read(IntPtr owner, string prefix)
    {
        if (owner == IntPtr.Zero) return "";
        string result = "";
        EnumWindows((window, _) =>
        {
            if (GetWindow(window, 4) != owner || !IsWindowVisible(window)) return true;
            var title = new StringBuilder(1024);
            GetWindowText(window, title, title.Capacity);
            string value = title.ToString();
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) return true;
            result = value.Substring(prefix.Length);
            return false;
        }, IntPtr.Zero);
        return result;
    }
}
'@
}

function Read-AlphaOverlay([Diagnostics.Process]$Process, [string]$Panel = 'sidebar') {
    [WyuOverlayProbe]::Read($Process.MainWindowHandle, "WYU $Panel | ")
}

function Focus-AlphaWindow([Diagnostics.Process]$Process) {
    $Process.Refresh()
    $owner = $Process.MainWindowHandle
    if ([WyuOverlayProbe]::HasFocus($owner)) { return }
    if (![WyuOverlayProbe]::ClickVisibleTitle($owner)) { throw 'Cannot focus the visible WYU window.' }
    Start-Sleep -Milliseconds 200
    if (![WyuOverlayProbe]::HasFocus($owner)) { throw 'WYU did not receive keyboard focus.' }
}
