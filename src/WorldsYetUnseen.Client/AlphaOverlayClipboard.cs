using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

internal sealed partial class AlphaOverlay
{
    private const uint MoveableMemory = 0x0002;
    private const uint UnicodeText = 13;

    internal bool TryCopyReport(string report)
    {
        if (GetForegroundWindow() != _owner)
            return false;

        var text = (report.ReplaceLineEndings("\r\n") + '\0').ToCharArray();
        var memory = GlobalAlloc(MoveableMemory, (nuint)text.Length * sizeof(char));
        if (memory == 0)
            return false;

        try
        {
            var address = GlobalLock(memory);
            if (address == 0)
                return false;
            try
            {
                Marshal.Copy(text, 0, address, text.Length);
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }

            if (OpenClipboard(_owner) == 0)
                return false;
            try
            {
                if (EmptyClipboard() == 0 || SetClipboardData(UnicodeText, memory) == 0)
                    return false;

                // Windows now owns the movable memory.
                memory = 0;
                return true;
            }
            finally
            {
                _ = CloseClipboard();
            }
        }
        finally
        {
            if (memory != 0)
                _ = GlobalFree(memory);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")]
    private static partial int GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
    [LibraryImport("user32.dll")]
    private static partial int OpenClipboard(nint window);
    [LibraryImport("user32.dll")]
    private static partial int EmptyClipboard();
    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32.dll")]
    private static partial int CloseClipboard();
}
