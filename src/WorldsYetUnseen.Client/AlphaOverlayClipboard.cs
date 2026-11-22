using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

internal sealed partial class AlphaOverlay
{
    private const uint MoveableMemory = 0x0002;
    private const uint UnicodeText = 13;

    internal bool TryCopyText(string text)
    {
        if (GetForegroundWindow() != _owner)
            return false;

        var characters = (text.ReplaceLineEndings("\r\n") + '\0').ToCharArray();
        var memory = GlobalAlloc(MoveableMemory, (nuint)characters.Length * sizeof(char));
        if (memory == 0)
            return false;

        try
        {
            var address = GlobalLock(memory);
            if (address == 0)
                return false;
            try
            {
                Marshal.Copy(characters, 0, address, characters.Length);
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

    internal string? ReadPresetText()
    {
        const int Limit = 80;
        if (GetForegroundWindow() != _owner || OpenClipboard(_owner) == 0)
            return null;

        try
        {
            var memory = GetClipboardData(UnicodeText);
            if (memory == 0)
                return null;
            var count = (int)Math.Min(GlobalSize(memory) / sizeof(char), (nuint)(Limit + 1));
            var address = GlobalLock(memory);
            if (address == 0)
                return null;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    if (Marshal.ReadInt16(address, i * sizeof(char)) == 0)
                        return Marshal.PtrToStringUni(address, i);
                }
                return null;
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }
        }
        finally
        {
            _ = CloseClipboard();
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
    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);
    [LibraryImport("user32.dll")]
    private static partial int OpenClipboard(nint window);
    [LibraryImport("user32.dll")]
    private static partial int EmptyClipboard();
    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);
    [LibraryImport("user32.dll")]
    private static partial int CloseClipboard();
}
