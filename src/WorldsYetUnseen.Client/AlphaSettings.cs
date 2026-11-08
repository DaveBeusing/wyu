using System.Text.Json;

namespace WorldsYetUnseen.Client;

internal readonly record struct AlphaSettings(int SizeIndex, int Sensitivity, bool InvertVertical)
{
    private static readonly (int Width, int Height)[] Sizes = [(960, 540), (1280, 720), (1600, 900)];
    internal static AlphaSettings Defaults => new(0, 100, false);
    internal static int SizeCount => Sizes.Length;
    internal (int Width, int Height) Size => Sizes[SizeIndex];
    internal bool IsValid => SizeIndex >= 0 && SizeIndex < SizeCount && Sensitivity is >= 25 and <= 300;
}

internal sealed class AlphaSettingsStore(string directory)
{
    private string PathName => Path.Combine(directory, "settings.json");

    internal (AlphaSettings Value, string? Notice) Load()
    {
        try
        {
            using var stream = File.OpenRead(PathName);
            var settings = Read(stream);
            return settings is { } valid ? (valid, null) : (AlphaSettings.Defaults, "Invalid settings; using defaults");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return (AlphaSettings.Defaults, null);
        }
        catch (JsonException)
        {
            return (AlphaSettings.Defaults, "Invalid settings; using defaults");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (AlphaSettings.Defaults, "Settings unreadable; using defaults");
        }
    }

    internal void Publish(AlphaSettings settings)
    {
        if (!settings.IsValid)
            throw new ArgumentOutOfRangeException(nameof(settings));

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new
                {
                    version = 1,
                    sizeIndex = settings.SizeIndex,
                    sensitivity = settings.Sensitivity,
                    invertVertical = settings.InvertVertical
                });
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(PathName) && Load().Notice is not null)
                File.Copy(PathName, Path.Combine(directory, $"settings-rejected-{Guid.NewGuid():N}.json"));
            File.Move(temporary, PathName, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static AlphaSettings? Read(Stream stream)
    {
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !ReadInt("version", out var version) || version != 1 ||
            !ReadInt("sizeIndex", out var size) || !ReadInt("sensitivity", out var sensitivity) ||
            !root.TryGetProperty("invertVertical", out var invert) ||
            invert.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;

        var settings = new AlphaSettings(size, sensitivity, invert.GetBoolean());
        return settings.IsValid ? settings : null;

        bool ReadInt(string name, out int value)
        {
            value = 0;
            return root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt32(out value);
        }
    }
}
