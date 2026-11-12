using System.Diagnostics;

namespace WorldsYetUnseen.Client;

// Opt-in output for repeatable Windows smoke replays.
internal static class BiomeMeasurements
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("WYU_WP13_MEASURE");
    internal static bool Enabled => Mode is "on" or "off";
    internal static bool UseBiomes => Mode != "off";
    internal static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void Write(string phase, long start)
    {
        if (start != 0)
            Console.WriteLine(FormattableString.Invariant($"WP13 METRIC {phase} {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F6}"));
    }
}
