using System.Diagnostics;

namespace WorldsYetUnseen.Client;

// Opt-in output for repeatable Windows smoke replays.
internal static class BiomeMeasurements
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("WYU_WP13_MEASURE");
    internal static bool Enabled => Mode is "on" or "off";
    internal static bool UseBiomes => Mode != "off";
    internal static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static long AllocatedBytes(long start) => start != 0 ? GC.GetAllocatedBytesForCurrentThread() : 0;

    internal static void WriteBuild(string phase, long start, long allocatedBefore)
    {
        if (start == 0)
            return;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Write(phase, start);
        Console.WriteLine(FormattableString.Invariant($"WP13 ALLOC {phase} {bytes}"));
    }

    internal static void Write(string phase, long start)
    {
        if (start != 0)
        {
            var end = Stopwatch.GetTimestamp();
            Console.WriteLine(FormattableString.Invariant($"WP13 METRIC {phase} {Stopwatch.GetElapsedTime(start, end).TotalMilliseconds:F6}"));
            Console.WriteLine(FormattableString.Invariant($"WP13 QPC {Environment.ProcessId} {phase} {start} {end}"));
        }
    }
}
