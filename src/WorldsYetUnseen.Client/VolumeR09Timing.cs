using System.Diagnostics;
using System.Runtime.CompilerServices;
using Groundwork.Core;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    internal ProceduralRenderHost? R09RenderHost { get; init; }
    private bool MeasureR09 => R09RenderHost is not null;
    private bool _measureCoverage;
    private readonly HashSet<VolumeWorkClass> _r09Paths = [];

    private R09Timing? StartR09(VolumeWorkClass path, long started, bool recoveryAtHandoff = false) =>
        MeasureR09 && (path != VolumeWorkClass.Movement || _measureCoverage) && _r09Paths.Add(path)
            ? new(path, started, recoveryAtHandoff) : null;

    private sealed record R09Timing(VolumeWorkClass Path, long Started, bool RecoveryAtHandoff)
    {
        internal Action? WriteWorkers { get; set; }

        internal long FarRendererStopStarted { get; set; }
        internal long FarRendererStopped { get; set; }
        internal long GroundRendererStartupStarted { get; set; }
        private long GroundRendererStarted { get; set; }
        internal R09WorkerMeasurement? RecoveryPrivateWorker { get; set; }
        internal (ulong Sequence, long Submitted) RecoveryMask { get; set; }

        internal void RecordGroundRendererStarted() => GroundRendererStarted = Stopwatch.GetTimestamp();

        internal void WriteRecoveryRenderer(long handoffStarted, long submitted)
        {
            if (handoffStarted > FarRendererStopStarted || FarRendererStopStarted > FarRendererStopped ||
                FarRendererStopped > GroundRendererStartupStarted || GroundRendererStartupStarted > GroundRendererStarted ||
                GroundRendererStarted > submitted ||
                RecoveryPrivateWorker is not { } worker || worker.Started < Started ||
                worker.Finished < worker.Started || worker.Finished > submitted)
                throw new InvalidOperationException("R09 Recovery renderer switch and private build timings do not match the handoff.");

            Write("far-renderer-stop", FarRendererStopStarted, FarRendererStopped);
            Write("ground-renderer-startup", GroundRendererStartupStarted, GroundRendererStarted);
            Write("ground-renderer-switch", FarRendererStopStarted, GroundRendererStarted);
            var overlapStart = Math.Max(FarRendererStopStarted, worker.Started);
            var overlapEnd = Math.Min(GroundRendererStarted, worker.Finished);
            if (overlapEnd <= overlapStart)
                overlapStart = overlapEnd = FarRendererStopStarted;
            Write("private-build-renderer-overlap", overlapStart, overlapEnd);
        }

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

        WriteR09Completion(timing, terrain, generation, frame, view, submitted, presented, handoffStarted, begin);
    }

    // Compile the reporting code only after the measured BeginTick consumes the receipt.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteR09Completion(R09Timing timing, VolumeStreamingCoordinator terrain,
        ulong generation, ulong frame, VolumeViewIdentity view, long submitted, long presented,
        long handoffStarted, BarrierStamp begin)
    {
        VolumeStreamingEvent? receipt = null, opened = null, maskReceipt = null;
        while (terrain.TryReadEvent(out var entry))
        {
            if (handoffStarted != 0 && entry.Kind == VolumeEventKind.Accepted && entry.Generation == 0 &&
                entry.Frame == timing.RecoveryMask.Sequence && entry.View == view)
            {
                if (maskReceipt is not null)
                    throw new InvalidOperationException("R09 observed duplicate Recovery mask presentation evidence.");
                maskReceipt = entry;
                continue;
            }
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
        {
            if (timing.Path != VolumeWorkClass.Recovery || handoffStarted > consumed ||
                (timing.RecoveryAtHandoff ? timing.Started != handoffStarted : timing.Started >= handoffStarted))
                throw new InvalidOperationException("R09 Recovery and flight handoff timings do not match the chosen route.");
            if (timing.RecoveryMask.Submitted == 0 || maskReceipt is not { } presentedMask ||
                presentedMask.MonotonicTimestamp < timing.RecoveryMask.Submitted || presentedMask.MonotonicTimestamp > submitted)
                throw new InvalidOperationException("R09 could not match the Recovery mask submission to its presentation receipt.");
            timing.Write("mask-submit-to-receipt", timing.RecoveryMask.Submitted, presentedMask.MonotonicTimestamp);
            timing.Write("handoff-to-begin-tick", handoffStarted, consumed);
            if (timing.RecoveryAtHandoff)
                timing.Write("recovery-no-flight-to-begin-tick", timing.Started, consumed);
            else
                timing.Write("flight-overlap-window", timing.Started, handoffStarted);
            timing.WriteRecoveryRenderer(handoffStarted, submitted);
        }
        Console.WriteLine(FormattableString.Invariant(
            $"WYU R09 IDENTITY {timing.Path} {generation} {frame} {view.View} {view.RendererEpoch} {accepted.Tick} {begin.Tick}"));
        timing.WriteWorkers?.Invoke();
        timing.WriteWorkers = null;
    }

    private readonly record struct R09PresentationWait(VolumeWorkClass Path, string Stage,
        VolumeStreamingCoordinator Terrain, VolumeViewIdentity View, ulong Generation, ulong Sequence);

    private void WriteR09PresentationWaitFailure(R09PresentationWait wait, D3D12Renderer renderer,
        RendererSnapshot? observed = null)
    {
        try
        {
            var state = observed ?? renderer.Read();
            var hostWindow = window.Read();
            var currentView = wait.Terrain.View;
            Console.Error.WriteLine(FormattableString.Invariant(
                $"WYU R09 PRESENT_WAIT_FAILURE path={wait.Path} stage={wait.Stage} expected={wait.Sequence} accepted={state.LastAcceptedSequence} presented={state.LastPresentedSequence} generation={wait.Generation} view={wait.View.View} epoch={wait.View.RendererEpoch} currentView={currentView.View} currentEpoch={currentView.RendererEpoch} lifecycle={state.Lifecycle} surface={state.Surface} windowPhase={hostWindow.Phase} focused={hostWindow.Focused} windowWidth={hostWindow.ClientWidth} windowHeight={hostWindow.ClientHeight} appliedWidth={state.AppliedWidth} appliedHeight={state.AppliedHeight} skipped={state.SkippedFrames} replaced={state.ReplacedFrames}"));
        }
        catch
        {
            // Failure diagnostics must never replace the presentation wait's exception.
        }
    }
}
