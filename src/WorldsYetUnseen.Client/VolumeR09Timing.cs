using System.Diagnostics;
using Groundwork.Core;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    internal ProceduralRenderHost? R09RenderHost { get; init; }
    private bool MeasureR09 => R09RenderHost is not null;
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
        ulong generation, ulong frame, VolumeViewIdentity view, long submitted, long presented,
        long handoffStarted, CancellationToken token)
    {
        // The scripted flight does not consume ground input at handoff. Drive the next
        // authoritative tick explicitly to observe when the presented coverage becomes usable.
        var begin = await BeginAsync(token).ConfigureAwait(false);
        if (!terrain.BeginTick(begin))
            throw new InvalidOperationException($"R09 did not consume the {timing.Path} receipt at BeginTick.");
        Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));

        VolumeStreamingEvent? receipt = null, opened = null;
        while (terrain.TryReadEvent(out var entry))
        {
            if (entry.Generation != generation || entry.Frame != frame || entry.View != view)
                continue;
            if (entry.Kind == VolumeEventKind.Accepted && receipt is null)
                receipt = entry;
            else if (entry.Kind == VolumeEventKind.Opened && opened is null)
                opened = entry;
            else if (entry.Kind is VolumeEventKind.Accepted or VolumeEventKind.Opened)
                throw new InvalidOperationException($"R09 observed duplicate presentation evidence for {timing.Path}.");
        }
        if (receipt is not { Stamp: { } accepted, MonotonicTimestamp: > 0 } presentedFrame ||
            opened is not { Stamp: { } consumedAt, MonotonicTimestamp: > 0 } openedFrame ||
            consumedAt != begin || accepted.World != begin.World || accepted.Tick >= begin.Tick ||
            presentedFrame.MonotonicTimestamp < submitted || openedFrame.MonotonicTimestamp < presented ||
            openedFrame.MonotonicTimestamp < presentedFrame.MonotonicTimestamp || terrain.Statistics.MovementHeld)
            throw new InvalidOperationException($"R09 could not match the {timing.Path} receipt to the consuming BeginTick.");

        var consumed = openedFrame.MonotonicTimestamp;
        timing.Write("submit-to-observed-present", submitted, presented);
        timing.Write("observed-present-to-begin-tick", presented, consumed);
        timing.Write("authorized-receipt-to-begin-tick", presentedFrame.MonotonicTimestamp, consumed);
        timing.Write("setup-to-begin-tick", timing.Started, consumed);
        if (handoffStarted != 0)
            timing.Write("handoff-to-begin-tick", handoffStarted, consumed);
        Console.WriteLine(FormattableString.Invariant(
            $"WYU R09 IDENTITY {timing.Path} {generation} {frame} {view.View} {view.RendererEpoch} {accepted.Tick} {begin.Tick}"));
    }
}
