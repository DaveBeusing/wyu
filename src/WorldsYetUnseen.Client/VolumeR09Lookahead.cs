using System.Diagnostics;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private R09Lookahead? _r09Lookahead;

    internal async ValueTask<IAsyncDisposable?> StartR09LookaheadAsync(VolumeStreamingCoordinator terrain,
        string? phase, CancellationToken token)
    {
        if (!MeasureR09 || phase != "flight" || cave is not null ||
            Environment.GetEnvironmentVariable("WYU_PACKAGE_G_SMOKE") == "1")
            return null;

        var position = slot?.Checkpoint?.Player?.Position;
        var heading = slot?.Checkpoint?.Player?.Heading ?? East;
        if (position is null)
        {
            using var candidates = planet.FindSpawnCandidates().GetEnumerator();
            if (!candidates.MoveNext())
                return null;
            position = candidates.Current;
        }

        var lookahead = new R09Lookahead(this, terrain, token);
        try
        {
            var revision = _published.TerrainRevision;
            var target = revision == 0 ? slot?.Checkpoint?.TerrainRevision ?? 1 : revision;
            lookahead.Start(position.Value, heading, revision, target);
            _r09Lookahead = lookahead;
            return lookahead;
        }
        catch
        {
            await lookahead.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class R09Lookahead(VolumeExploration owner, VolumeStreamingCoordinator terrain, CancellationToken token)
        : IAsyncDisposable
    {
        internal R09EarlyPrebuild? Movement { get; private set; }
        internal R09EarlyPrebuild? FirstEdit { get; private set; }

        internal void Start(PlanetVector position, PlanetVector heading, ulong revision, ulong target)
        {
            // The startup pose is only a prediction. Actual publishing still selects from the verified pose.
            var forcePending = Environment.GetEnvironmentVariable("WYU_R09_TEST_FORCE_PREFETCH_MISS") == "1";
            Movement = Start(VolumeWorkClass.Recovery,
                owner.SelectTerrain(FlightCoverageCenter(position), heading, supportCells: FlightSupportCells));
            FirstEdit = Start(VolumeWorkClass.Speculative, owner.SelectTerrain(position, heading));

            R09EarlyPrebuild Start(VolumeWorkClass envelope, VolumeStreamingSelection selection)
            {
                var attempted = Stopwatch.GetTimestamp();
                var result = terrain.TryStagePrivateAhead(envelope, selection, revision, target, owner.MeshBytes,
                    owner.Charge(selection), owner.Field, out var work);
                return new(attempted, result == VolumeAdmission.Accepted ? work : null, forcePending, token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (ReferenceEquals(owner._r09Lookahead, this))
                owner._r09Lookahead = null;
            Movement?.Cancel();
            FirstEdit?.Cancel();
            if (Movement is { } movement)
                await movement.ObserveCompletionAsync().ConfigureAwait(false);
            if (FirstEdit is { } edit)
                await edit.ObserveCompletionAsync().ConfigureAwait(false);
        }
    }

    private sealed class R09EarlyPrebuild
    {
        private readonly long _attempted;
        private readonly VolumeStreamingWork? _work;
        private readonly Task<R09WorkerMeasurement?>? _measurement;
        private readonly TaskCompletionSource<R09WorkerMeasurement?>? _heldForColdProbe;

        internal bool IsSettling => _measurement is { IsCompleted: false } && _work is { IsCancellationRequested: true };

        internal R09EarlyPrebuild(long attempted, VolumeStreamingWork? work, bool forcePending, CancellationToken token)
        {
            _attempted = attempted;
            _work = work;
            if (work is null)
                return;

            if (forcePending)
            {
                // Leave a charged, undispatched producer at the handoff to exercise the cold path.
                _heldForColdProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _measurement = _heldForColdProbe.Task;
                return;
            }

            var queued = Stopwatch.GetTimestamp();
            _measurement = Task.Run<R09WorkerMeasurement?>(() =>
            {
                var started = Stopwatch.GetTimestamp();
                if (!work.ExecuteDetailedProfiled(out var profile, token))
                    return null;
                var finished = Stopwatch.GetTimestamp();
                return new(queued, started, finished, Stopwatch.GetTimestamp(), profile);
            });
        }

        internal static (VolumeStreamingWork? Work, R09WorkerMeasurement? Measurement) Offer(
            R09EarlyPrebuild? early, R09Timing timing)
        {
            var cut = Stopwatch.GetTimestamp();
            if (early is null)
            {
                WriteStatus("Unavailable", 0, 0);
                return default;
            }

            var completed = early._measurement?.IsCompleted == true;
            var measurement = completed && early._measurement!.IsCompletedSuccessfully ? early._measurement.Result : null;
            var work = early._work;
            if (work is not null && measurement is { } ready && ready.Observed <= timing.Started &&
                !work.IsCancellationRequested)
            {
                WriteStatus("Ready", early._attempted, work.Generation);
                return (work, ready);
            }

            var status = work is null ? "Rejected"
                : !completed || measurement is { } late && late.Observed > timing.Started ? "Pending" : "Unavailable";
            WriteStatus(status, early._attempted, work?.Generation ?? 0);
            early.Cancel();
            return default;

            void WriteStatus(string status, long attempted, ulong generation) =>
                Console.WriteLine(FormattableString.Invariant(
                    $"WYU R09 EARLY {timing.Path} {status} {attempted} {cut} {generation}"));
        }

        internal void Cancel()
        {
            _work?.Dispose();
            _heldForColdProbe?.TrySetResult(null);
        }

        internal async ValueTask ObserveCompletionAsync()
        {
            if (_measurement is null)
                return;
            try
            {
                await _measurement.ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Optional work can fault or be canceled. Its result is never needed for publication.
            }
        }
    }
}
