using System.Diagnostics;
using Groundwork.Core;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    internal bool MeasureR09 { get; init; }
    private bool _measureCoverage;
    private readonly HashSet<VolumeWorkClass> _r09Paths = [];

    private R09Timing? StartR09(VolumeWorkClass path, long started) =>
        MeasureR09 && (path != VolumeWorkClass.Movement || _measureCoverage) && _r09Paths.Add(path)
            ? new(path, started) : null;

    private sealed record R09Timing(VolumeWorkClass Path, long Started)
    {
        internal void Write(string phase, long start, long end, int attempts = 0, int denials = 0) =>
            Console.WriteLine(FormattableString.Invariant(
                $"WYU R09 {Path} {phase} {start} {end} {Stopwatch.Frequency} {attempts} {denials}"));
    }

    private async Task FinishR09Async(R09Timing timing, VolumeStreamingCoordinator terrain,
        long submitted, long presented, long handoffStarted, CancellationToken token)
    {
        // The scripted flight does not consume ground input at handoff. Drive the next
        // authoritative tick explicitly to observe when the presented coverage becomes usable.
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        var consumed = Stopwatch.GetTimestamp();
        Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
        if (terrain.Statistics.MovementHeld)
            throw new InvalidOperationException($"R09 movement remained held after {timing.Path} presentation.");
        timing.Write("submit-to-observed-present", submitted, presented);
        timing.Write("observed-present-to-begin-tick", presented, consumed);
        timing.Write("setup-to-begin-tick", timing.Started, consumed);
        if (handoffStarted != 0)
            timing.Write("handoff-to-begin-tick", handoffStarted, consumed);
    }
}
