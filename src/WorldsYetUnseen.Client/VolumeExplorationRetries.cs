using Groundwork.Core;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    internal bool VerifyTerrainRetries { get; init; }
    internal VolumeWorkClass? ExhaustTerrainRetry { get; init; }
    private bool _retryExhausted;
    private readonly HashSet<VolumeWorkClass> _retryProbes = [];

    private readonly record struct TerrainAdmissionRequest(VolumeWorkClass Path, VolumeViewIdentity View,
        ulong Revision, ulong TargetRevision, VolumeStreamingSelection Selection, VolumeBrushIntent? Edit);

    private async Task<(VolumeStreamingWork? Work, VolumeAdmission Result)> AdmitTerrainAsync(
        VolumeStreamingCoordinator terrain, TerrainAdmissionRequest request, long started, CancellationToken token, R09Timing? timing = null)
    {
        var exhaust = ExhaustTerrainRetry == request.Path && request.Revision != 0 && _retryProbes.Add(request.Path);
        var probe = exhaust || VerifyTerrainRetries && request.Revision != 0 && _retryProbes.Add(request.Path);
        var baseline = exhaust ? terrain.Statistics : default;
        // The edit head owns FirstEdit. A probe may charge recovery beside it without consuming the sealed edit.
        using var reader = probe ? terrain.TryRead() : null;
        using var firstOccupant = probe ? ReserveProbe() : null;
        var occupant = firstOccupant;
        VolumeStreamingWork? work = null;
        var result = VolumeAdmission.Backpressure;
        var retry = new TerrainHostRetry<TerrainAdmissionRequest>(request.Path, request, candidate =>
        {
            if (probe && candidate.Edit is not null && occupant is { IsCancellationRequested: false })
                return TerrainHostAdmissionResult.Busy; // Pre-admission injection. The sealed head stays pending.
            result = candidate.Edit is { } brush
                ? terrain.TryStageEdit(brush.Tick, brush.Id, candidate.Selection, MeshBytes, Charge(candidate.Selection), out work)
                : terrain.TryStage(candidate.Path, candidate.Selection, candidate.Revision, candidate.TargetRevision,
                    MeshBytes, Charge(candidate.Selection), Field, out work);
            return TerrainHostAdmission.Classify(result);
        }, startedTimestamp: started);

        ulong progress = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = _ports.Committed.Read().Root as VolumeWorldRoot;
            if (terrain.View != request.View || (current?.Terrain.TerrainRevision ?? 0) != request.Revision)
            {
                retry.Supersede();
                return (null, VolumeAdmission.Stale);
            }
            using var replenished = exhaust && retry.Attempts != 0 ? ReserveProbe() : null;
            occupant = replenished ?? firstOccupant;
            var before = terrain.Statistics;
            var state = retry.Advance(_tick, progress);
            if (exhaust && state != TerrainHostRetryState.WaitingForProgress)
            {
                var expected = request.Edit is null ? 3 : 2;
                occupant!.Dispose();
                reader?.Dispose();
                var released = terrain.Statistics;
                if (state != TerrainHostRetryState.AttemptsExhausted || retry.Attempts != expected ||
                    retry.TransientDenials != expected || work is not null ||
                    !released.Used.Fits(baseline.Used) || released.Readers != baseline.Readers ||
                    released.Generations > baseline.Generations)
                {
                    work?.Dispose();
                    throw new InvalidOperationException($"Unexpected terminal state or retained terrain charges ({request.Path}, {state}).");
                }
                _retryExhausted = true;
                Console.WriteLine($"WP11 HOST RETRY EXHAUSTED path={request.Path} attempts={retry.Attempts} denied={retry.TransientDenials} released=true");
                return (null, VolumeAdmission.Backpressure);
            }
            if (state == TerrainHostRetryState.Admitted)
            {
                if (probe)
                {
                    if (retry.Attempts != 2 || retry.TransientDenials != 1 || work is null)
                    {
                        work?.Dispose();
                        throw new InvalidOperationException("The terrain retry probe did not see exactly one denial and one admission.");
                    }
                    Console.WriteLine($"WP11 HOST RETRY path={request.Path} attempts={retry.Attempts} denied={retry.TransientDenials} tick={_tick}");
                }
                timing?.Write("admission", started, System.Diagnostics.Stopwatch.GetTimestamp(), retry.Attempts, retry.TransientDenials);
                return (work, result);
            }
            if (state != TerrainHostRetryState.WaitingForProgress)
                return (null, result);

            if (probe)
            {
                if ((!exhaust && retry.Attempts != 1) || occupant is null || work is not null)
                    throw new InvalidOperationException("The pressure probe did not block admission before allocation.");
                occupant.Dispose();
            }
            // Wait outside both authority and GPU locks. Polling alone does not authorize another attempt.
            while (!retry.IsDeadlineElapsed && terrain.View == request.View)
            {
                token.ThrowIfCancellationRequested();
                var currentResources = terrain.Statistics;
                if (currentResources.Stopped)
                    return (null, VolumeAdmission.Closed);
                if (currentResources.Used.Fits(before.Used) && currentResources.Used != before.Used)
                    break;
                await Task.Delay(8, token).ConfigureAwait(false);
            }
            if (retry.IsDeadlineElapsed)
            {
                retry.Advance(_tick, progress);
                return (null, VolumeAdmission.Backpressure);
            }
            if (terrain.View != request.View)
            {
                retry.Supersede();
                return (null, VolumeAdmission.Stale);
            }

            // Only the host owner advances the authoritative tick. These barriers do not
            // consume movement or resubmit the sealed edit.
            await BeginAsync(token).ConfigureAwait(false);
            Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
            Drain(terrain);
            progress++;
        }

        VolumeStreamingWork ReserveProbe()
        {
            var path = request.Edit is null ? request.Path : VolumeWorkClass.Recovery;
            var status = terrain.TryStage(path, request.Selection, request.Revision, request.Revision,
                MeshBytes, Charge(request.Selection), Field, out var pressure);
            return status == VolumeAdmission.Accepted && pressure is not null ? pressure
                : throw new InvalidOperationException($"Could not charge terrain retry probe ({request.Path}, {status}).");
        }
    }
}
