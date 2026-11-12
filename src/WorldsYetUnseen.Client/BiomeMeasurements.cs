using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WorldsYetUnseen.Client;

// Opt-in output for repeatable Windows smoke replays.
internal static partial class BiomeMeasurements
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("WYU_WP13_MEASURE");
    internal static bool Enabled => Mode is "on" or "off";
    internal static bool UseBiomes => Mode != "off";
    internal static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal readonly record struct BuildSample(long Started, long Allocated, ulong Cycles, int Collections);

    internal static BuildSample StartBuild() => !Enabled ? default :
        new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread(), ThreadCycles(), Collections());

    internal static void WriteBuild(string phase, BuildSample sample)
    {
        if (sample.Started == 0)
            return;
        var end = Stopwatch.GetTimestamp();
        var cycles = ThreadCycles() - sample.Cycles;
        var collections = Collections() - sample.Collections;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - sample.Allocated;
        WriteInterval(phase, sample.Started, end);
        Console.WriteLine(FormattableString.Invariant($"WP13 ALLOC {phase} {bytes}"));
        Console.WriteLine(FormattableString.Invariant($"WP13 CPU {phase} {cycles} {collections}"));
    }

    private static int Collections() => GC.CollectionCount(0);
    private static ulong ThreadCycles() => QueryThreadCycleTime((nint)(-2), out var cycles) != 0 ? cycles : 0;

    [LibraryImport("kernel32.dll")]
    private static partial int QueryThreadCycleTime(nint thread, out ulong cycles);

    internal static void Write(string phase, long start)
    {
        if (start != 0)
            WriteInterval(phase, start, Stopwatch.GetTimestamp());
    }

    private static void WriteInterval(string phase, long start, long end)
    {
        Console.WriteLine(FormattableString.Invariant($"WP13 METRIC {phase} {Stopwatch.GetElapsedTime(start, end).TotalMilliseconds:F6}"));
        Console.WriteLine(FormattableString.Invariant($"WP13 QPC {Environment.ProcessId} {phase} {start} {end}"));
    }
}
