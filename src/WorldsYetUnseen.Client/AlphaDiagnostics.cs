using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WorldsYetUnseen.Client;

internal sealed partial class AlphaDiagnostics(string directory, string? smokeCase)
{
    private const int LogLimit = 64 * 1024;
    private static readonly string Build = typeof(AlphaDiagnostics).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private string LogPath => Path.Combine(directory, "client.log");
    private string Phase { get; set; } = "Startup";
    private bool GraphicsFailure { get; set; }

    internal void Enter(string phase)
    {
        Phase = phase;
        Write("enter");
    }

    internal void CheckStartup() => Inject("startup");

    internal void CheckGraphics() => Inject("graphics");
    internal bool LimitPlanetStreaming => smokeCase == "budget";

    private void Inject(string point)
    {
        if (smokeCase == point)
            throw new InvalidOperationException($"WP10 controlled {point} failure.");
    }

    internal Action<CancellationToken>? BeforeSaveLoad => smokeCase == "close" ? HoldSaveLoad : null;

    private void HoldSaveLoad(CancellationToken token)
    {
        Write("load-ready");
        token.WaitHandle.WaitOne();
        token.ThrowIfCancellationRequested();
    }

    internal void MarkGraphicsFailure() => GraphicsFailure = true;

    internal void Report(Exception failure)
    {
        var graphics = GraphicsFailure || Phase == "GraphicsStartup";
        var path = Write("fault", $"{(graphics ? "graphics" : "client")} {failure}") ? $"\n\nLog: {LogPath}"
            : "\n\nThe local log could not be written.";
        var terrain = !graphics ? failure as PlanetLoadException : null;
        var title = graphics ? "Worlds Yet Unseen — Graphics error" : terrain is not null
            ? "Worlds Yet Unseen — Planet loading error" : "Worlds Yet Unseen — Client error";
        var help = graphics ? "The Direct3D 12 renderer could not start or continue. Check that a supported GPU and current driver are available."
            : terrain?.Message ?? $"The client could not continue during {Phase}. Close the game and try again. If it persists, share the log with support.";
        ShowMessage(0, help + path, title, 0x00010010);
    }

    internal bool Write(string eventName, string? detail = null)
    {
        // Limit both the number of files and the size of each entry. A competing
        // process may skip a record rather than block the game or its shutdown.
        try
        {
            Directory.CreateDirectory(directory);
            using var gate = new FileStream(Path.Combine(directory, "client.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            if (detail is { Length: > 2048 })
                detail = detail[..2048];
            var entry = Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O} build={Build} pid={Environment.ProcessId} " +
                $"phase={Phase} event={eventName} {detail?.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length + entry.Length > LogLimit)
                File.Move(LogPath, Path.Combine(directory, "client.previous.log"), overwrite: true);
            using var output = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            output.Write(entry);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ShowMessage(nint window, string text, string caption, uint type);
}
