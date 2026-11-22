using System.Text;

namespace WorldsYetUnseen.Client;

internal enum ConsolePresetPinResult
{
    Pinned,
    Unpinned,
    LimitReached,
    NotFound,
    Unavailable,
}

internal sealed class ConsolePinnedPresets(string directory, Func<string, string?> readName)
{
    private const int EntryLimit = 16;
    private const int EntryLengthLimit = 80;
    private const string FileName = "console-pinned-presets.txt";
    private static ReadOnlySpan<byte> Header => "wyu-pinned-presets 1\n"u8;
    private string DataPath => Path.Combine(directory, FileName);

    internal bool TryRead(out string[] entries)
    {
        entries = [];
        try
        {
            if (ReadEntries() is not { } stored)
                return false;

            entries = stored.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value).ToArray();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal ConsolePresetPinResult Pin(string encoded) => ReadEntryName(encoded) is { } name
        ? Update(name, encoded) : ConsolePresetPinResult.Unavailable;

    internal ConsolePresetPinResult Unpin(string name) => Update(name, null);

    private ConsolePresetPinResult Update(string name, string? encoded)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Keep the lock file in place so every writer locks the same file.
            using var writer = new FileStream(Path.Combine(directory, FileName + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (ReadEntries() is not { } stored)
                return ConsolePresetPinResult.Unavailable;

            if (encoded is null)
            {
                if (!stored.Remove(name))
                    return ConsolePresetPinResult.NotFound;

                WriteEntries(stored);
                return ConsolePresetPinResult.Unpinned;
            }

            if (stored.TryGetValue(name, out var existing))
            {
                if (existing == encoded)
                    return ConsolePresetPinResult.Pinned;
            }
            else if (stored.Count >= EntryLimit)
                return ConsolePresetPinResult.LimitReached;

            stored[name] = encoded;
            WriteEntries(stored);
            return ConsolePresetPinResult.Pinned;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ConsolePresetPinResult.Unavailable;
        }
    }

    private Dictionary<string, string>? ReadEntries()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        FileStream stream;
        try
        {
            stream = new FileStream(DataPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return entries;
        }

        using (stream)
        {
            // The extra byte detects an overlong file without allocating from its reported length.
            Span<byte> buffer = stackalloc byte[Header.Length + EntryLimit * (EntryLengthLimit + 1) + 1];
            var length = stream.ReadAtLeast(buffer, buffer.Length, false);
            ReadOnlySpan<byte> remaining = buffer[..length];
            if (length == buffer.Length || !remaining.StartsWith(Header))
                return null;

            remaining = remaining[Header.Length..];
            while (!remaining.IsEmpty)
            {
                var end = remaining.IndexOf((byte)'\n');
                if (end is < 1 or > EntryLengthLimit || entries.Count == EntryLimit)
                    return null;

                var encoded = Encoding.Latin1.GetString(remaining[..end]);
                if (ReadEntryName(encoded) is not { } name || !entries.TryAdd(name, encoded))
                    return null;

                remaining = remaining[(end + 1)..];
            }
        }

        return entries;
    }

    private string? ReadEntryName(string encoded) =>
        encoded.Length is >= 1 and <= EntryLengthLimit && encoded.All(character => character is >= ' ' and <= '~')
            ? readName(encoded) : null;

    private void WriteEntries(Dictionary<string, string> entries)
    {
        var temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(Header);
                Span<byte> buffer = stackalloc byte[EntryLengthLimit + 1];
                foreach (var entry in entries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    var count = Encoding.ASCII.GetBytes(entry.Value.AsSpan(), buffer);
                    buffer[count] = (byte)'\n';
                    stream.Write(buffer[..(count + 1)]);
                }
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, DataPath, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
