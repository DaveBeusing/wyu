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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

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
