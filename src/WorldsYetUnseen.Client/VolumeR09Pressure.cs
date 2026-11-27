using System.Collections.Immutable;
using System.Diagnostics;
using Groundwork.Core;
using Groundwork.Jobs;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private JobScheduler? _r09Scheduler;
    private Func<CancellationToken, ValueTask>? _r09Speculate, _r09Protected;
    private ulong _r09JobOrdinal;

    internal async Task StopR09JobsAsync()
    {
        if (_r09Scheduler is not { } scheduler)
            return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(world.CreateBorrowedDrainDeadline(), cleanup.Token).ConfigureAwait(false);
        if (scheduler.ReadMetrics() is not { Queued: 0, OccupiedWorkerSlots: 0 })
            throw new InvalidOperationException("R09 retained job scheduler work after stop.");
    }

    private async Task<R09Pressure> StartR09PressureAsync(CancellationToken token)
    {
        if (_r09Speculate is not null || _r09Protected is not null)
            throw new InvalidOperationException("A previous R09 pressure phase did not settle.");
        var begin = await BeginAsync(token).ConfigureAwait(false);
        var phase = _ports.Phases.TryOpen(begin, TickPhase.Base).Phase
            ?? throw new InvalidOperationException("Could not open the R09 pressure phase.");
        var scheduler = _r09Scheduler ??= new JobScheduler(begin.World, new JobSchedulerLimits(2, 2), _ports.Execution,
            new R09NoInput(), world.Ownership, world.Reporter, TimeProvider.System, new Dictionary<JobHandlerId, JobHandler>
            {
                [R09JobScope.Speculative] = (_, cancellation) => _r09Speculate!(cancellation),
                [R09JobScope.Protected] = (_, cancellation) => _r09Protected!(cancellation),
            }, new JobDispatchReservation(1, 1, [R09JobScope.Protected]));
        var pressure = new R09Pressure(this, scheduler, begin, phase);
        _r09Speculate = pressure.SpeculateAsync;
        try
        {
            await pressure.StartAsync(token).ConfigureAwait(false);
            return pressure;
        }
        catch
        {
            pressure.Dispose();
            throw;
        }
    }

    private async Task StageR09UploadAsync(R09Timing timing, R09Pressure pressure, VolumeStreamingCoordinator terrain,
        D3D12Renderer renderer, ulong generation, ulong mask, VolumeStreamingLease retained, CancellationToken token)
    {
        var gate = R09RenderHost?.R09UploadGate ?? throw new InvalidOperationException("The active R09 renderer has no native upload gate.");
        using var hold = gate.Arm();
        var uploadStarted = Stopwatch.GetTimestamp();
        RequireMesh(renderer.TryStageVolumeTerrain(generation));
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        var holdObserved = Stopwatch.GetTimestamp();
        var heldJobs = pressure.RequireSaturated();
        var heldTerrain = terrain.Statistics;
        if (renderer.IsVolumeTerrainReady(generation) || !heldTerrain.MovementHeld ||
            (_ports.Committed.Read().Root as VolumeWorldRoot)?.Terrain != retained.Info)
            throw new InvalidOperationException("R09 lost the held upload or retained terrain.");
        var epoch = terrain.View.RendererEpoch;
        var held = R09ResourceMeasurement.Capture(renderer, epoch, heldTerrain, heldJobs);

        var uploadReleased = hold.Release();
        await WaitAsync(renderer, () => renderer.IsVolumeTerrainReady(generation), token).ConfigureAwait(false);
        var ready = Stopwatch.GetTimestamp();
        var readyJobs = pressure.RequireSaturated();
        var readyTerrain = terrain.Statistics;
        var nativeReady = R09ResourceMeasurement.Capture(renderer, epoch, readyTerrain, readyJobs);
        var peak = readyTerrain.Peak;
        timing.Write("upload-to-observed-ready", uploadStarted, ready);
        timing.Write("held-upload", holdObserved, uploadReleased);
        timing.Write("held-upload-release-to-ready", uploadReleased, ready);
        Console.WriteLine(FormattableString.Invariant(
            $"WYU R09 PRESSURE {timing.Path} {peak.CpuBytes} {peak.NativeBytes} {peak.UploadBytes} {peak.RetirementBytes}"));
        await pressure.SettleAsync(token).ConfigureAwait(false);
        var writeWorkers = timing.WriteWorkers;
        timing.WriteWorkers = () =>
        {
            writeWorkers?.Invoke();
            var path = timing.Path.ToString();
            held.Write(path, "held");
            nativeReady.Write(path, "native-ready");
            gate.Twilight?.WriteHeldSky(timing.Path, epoch, mask, held.Gpu);
        };
    }

    private readonly record struct R09WorkerMeasurement(long Queued, long Started, long Finished, long Observed,
        VolumeStreamingWorkDetailedProfile Profile)
    {
        internal void Write(R09Timing timing, string prefix)
        {
            timing.Write($"{prefix}-queue", Queued, Started);
            timing.Write(prefix, Started, Finished);
            timing.Write($"{prefix}-completion-observation", Finished, Observed);
            var stages = Profile.Timing;
            timing.Write($"{prefix}-overlay", stages.Started, stages.OverlayPrepared);
            timing.Write($"{prefix}-bricks", stages.OverlayPrepared, stages.BricksGenerated);
            timing.Write($"{prefix}-plan", stages.BricksGenerated, stages.PlanCreated);
            timing.Write($"{prefix}-mesh", stages.PlanCreated, stages.MeshBuilt);
            timing.Write($"{prefix}-evidence", stages.MeshBuilt, Profile.EvidenceFinished);
            timing.Write($"{prefix}-handoff", Profile.EvidenceFinished, stages.Completed);

            var role = prefix == "worker" ? "publishing" : "private";
            var brickTiming = Profile.BrickTiming;
            Console.WriteLine(FormattableString.Invariant(
                $"WYU R09 BRICKTIMING {timing.Path} {role} {brickTiming.ReservationTicks} {brickTiming.AllocationTicks} {brickTiming.DensityTicks} {brickTiming.MaterialTicks} {brickTiming.SharedVertexTicks} {brickTiming.PublishTicks}"));
            foreach (var brick in Profile.Bricks.Span)
            {
                var key = brick.Key;
                Console.WriteLine(FormattableString.Invariant(
                    $"WYU R09 BRICK {timing.Path} {role} {key.X} {key.Y} {key.Z} {brick.RequestedStride} {brick.DensitySha256} {brick.MaterialSha256} {brick.ElapsedTicks} {(brick.Reused ? 1 : 0)}"));
            }
            foreach (var mesh in Profile.Meshes.Span)
            {
                var origin = mesh.Origin;
                Console.WriteLine(FormattableString.Invariant(
                    $"WYU R09 MESH {timing.Path} {role} {origin.X} {origin.Y} {origin.Z} {mesh.LodMask} {mesh.TriangleCount} {mesh.GeometrySha256} {mesh.ElapsedTicks} {(mesh.Reused ? 1 : 0)}"));
            }
        }
    }

    private enum R09PublishedReuse { None, Bricks, Terrain }

    private sealed class R09Pressure(VolumeExploration owner, JobScheduler scheduler,
        BarrierStamp begin, JobDispatchPhase phase) : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IJobExecutionHandle? _running, _queued;
        private bool _closed;

        internal async Task StartAsync(CancellationToken token)
        {
            _running = Submit(R09JobScope.Speculative);
            await scheduler.DispatchReadyAsync(phase, token).ConfigureAwait(false);
            await _entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            _queued = Submit(R09JobScope.Speculative);
            RequireSaturated();
        }

        internal async ValueTask SpeculateAsync(CancellationToken token)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(token).ConfigureAwait(false);
        }

        internal async Task<bool> RunProtectedAsync(VolumeStreamingWork work, R09Timing timing,
            VolumeStreamingCoordinator terrain, VolumeStreamingSelection selection, ulong revision, CancellationToken token,
            R09EarlyPrebuild? earlyPrebuild = null, R09IndependentTexturePressure.Copy? independentCopy = null)
        {
            VolumeStreamingWork? privateWork = null;
            R09WorkerMeasurement? prebuild;
            try
            {
                if (timing.Path == VolumeWorkClass.Recovery)
                {
                    var admission = terrain.TryStagePrivate(VolumeWorkClass.Movement, selection, revision, revision,
                        owner.MeshBytes, owner.Charge(selection), owner.Field, out privateWork);
                    if (admission != VolumeAdmission.Accepted || privateWork is null)
                        throw new InvalidOperationException($"R09 could not admit the Recovery private prebuild ({admission}).");
                    var reuse = R09PublishedReuse.Terrain;
                    if (Environment.GetEnvironmentVariable("WYU_R09_TEST_FORCE_RECOVERY_COLD") == "1")
                        reuse = R09PublishedReuse.None;
                    else if (Environment.GetEnvironmentVariable("WYU_R09_TEST_FORCE_RECOVERY_MESH_REBUILD") == "1")
                        reuse = R09PublishedReuse.Bricks;
                    prebuild = await MeasureWorkerAsync(privateWork, null, "private prebuild", token, reuse)
                        .ConfigureAwait(false);
                    if (prebuild is null)
                        return false;
                    timing.RecoveryPrivateWorker = prebuild;
                }
                else
                {
                    (privateWork, prebuild) = R09EarlyPrebuild.Offer(earlyPrebuild, timing);
                }

                var priorTerrain = (owner._ports.Committed.Read().Root as VolumeWorldRoot)?.Terrain;
                var measurement = await MeasureWorkerAsync(work, privateWork, timing.Path.ToString(), token,
                    independentCopy: independentCopy).ConfigureAwait(false);
                if (measurement is not { } worker)
                    return false;
                if (independentCopy is not null)
                {
                    RequireSaturated();
                    if (priorTerrain is null || !terrain.Statistics.MovementHeld ||
                        (owner._ports.Committed.Read().Root as VolumeWorldRoot)?.Terrain != priorTerrain)
                        throw new InvalidOperationException("R09 lost the held terrain predecessor during independent GPU copy pressure.");
                    independentCopy.Release(worker.Started, worker.Finished);
                }
                timing.WriteWorkers = () =>
                {
                    prebuild?.Write(timing, "prebuild-worker");
                    worker.Write(timing, "worker");
                    if (timing.RecoveryPrivateWorker is { } recovery)
                    {
                        Console.WriteLine(FormattableString.Invariant(
                            $"WYU R09 RECOVERYBRICKS {recovery.Profile.ReuseSourceGeneration}"));
                        Console.WriteLine(FormattableString.Invariant(
                            $"WYU R09 RECOVERYMESH {(recovery.Profile.ReusedMeshPlan ? 1 : 0)}"));
                    }
                    Console.WriteLine(FormattableString.Invariant(
                        $"WYU R09 REUSE {timing.Path} {privateWork?.Generation ?? 0} {work.Generation} {worker.Profile.ReuseSourceGeneration}"));
                };
                return true;
            }
            finally
            {
                privateWork?.Dispose();
            }
        }

        private async Task<R09WorkerMeasurement?> MeasureWorkerAsync(VolumeStreamingWork work,
            VolumeStreamingWork? privateWork, string description, CancellationToken token,
            R09PublishedReuse reuse = R09PublishedReuse.None, R09IndependentTexturePressure.Copy? independentCopy = null)
        {
            RequireSaturated();
            long started = 0, finished = 0;
            var completed = false;
            VolumeStreamingWorkDetailedProfile profile = default;
            owner._r09Protected = cancellation =>
            {
                independentCopy?.RequirePending();
                started = Stopwatch.GetTimestamp();
                if (reuse == R09PublishedReuse.Terrain)
                    completed = work.ExecuteDetailedProfiledReusingPublishedTerrain(out profile, cancellation);
                else if (reuse == R09PublishedReuse.Bricks)
                    completed = work.ExecuteDetailedProfiledReusingPublishedBricks(out profile, cancellation);
                else
                    completed = privateWork is null ? work.ExecuteDetailedProfiled(out profile, cancellation)
                        : work.ExecuteDetailedProfiled(privateWork, out profile, cancellation);
                finished = Stopwatch.GetTimestamp();
                independentCopy?.RequirePending();
                return ValueTask.CompletedTask;
            };
            using var handle = Submit(R09JobScope.Protected);
            // Groundwork dispatch is explicit. The accepted job cannot start before this marker.
            var queued = Stopwatch.GetTimestamp();
            await scheduler.DispatchReadyAsync(phase, token).ConfigureAwait(false);
            JobExecutionReport result;
            try
            {
                var deadline = TimeSpan.FromSeconds(25);
                result = await handle.ExecutionCompletion.WaitAsync(deadline, token).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"R09 {description} job did not settle. " +
                    $"Execute entered={Volatile.Read(ref started) != 0}. {scheduler.ReadMetrics()}", error);
            }
            var observed = Stopwatch.GetTimestamp();
            if (result.Status != JobExecutionStatus.Succeeded || !completed)
                return null;
            await WaitForWorkersAsync(1, token).ConfigureAwait(false);
            owner._r09Protected = null;
            return new(queued, started, finished, observed, profile);
        }

        internal JobSchedulerMetrics RequireSaturated()
        {
            var refused = scheduler.TrySubmit(Submission(R09JobScope.Speculative), phase);
            refused.Handle?.Dispose();
            var metrics = scheduler.ReadMetrics();
            if (refused.Status != JobAdmissionStatus.CapacityExceeded ||
                _running?.ExecutionCompletion.IsCompleted != false || _queued?.ExecutionCompletion.IsCompleted != false ||
                metrics is not { Queued: 1, OccupiedWorkerSlots: 1 })
                throw new InvalidOperationException("R09 did not observe an occupied speculative worker and full speculative queue.");
            return metrics;
        }

        internal async Task SettleAsync(CancellationToken token)
        {
            _release.TrySetResult();
            if ((await _running!.ExecutionCompletion.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false)).Status
                != JobExecutionStatus.Succeeded)
                throw new InvalidOperationException("The R09 speculative worker did not complete.");
            await WaitForWorkersAsync(0, token).ConfigureAwait(false);
            await scheduler.DispatchReadyAsync(phase, token).ConfigureAwait(false);
            if ((await _queued!.ExecutionCompletion.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false)).Status
                != JobExecutionStatus.Succeeded)
                throw new InvalidOperationException("The R09 speculative queue did not settle.");
            await WaitForWorkersAsync(0, token).ConfigureAwait(false);
            Dispose();
            owner._r09Speculate = null;
        }

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            _release.TrySetResult();
            _queued?.Dispose();
            _running?.Dispose();
            owner._ports.Phases.Close(phase);
        }

        private JobSubmission Submission(JobHandlerId handler) => new(
            new SourceKey(begin.World, begin.Tick, TickPhase.Base, 83, 0, checked(++owner._r09JobOrdinal)),
            handler, JobInputKind.None, JobResultKind.Commandless, [], [], [], [], [], default);

        private IJobExecutionHandle Submit(JobHandlerId handler)
        {
            var result = scheduler.TrySubmit(Submission(handler), phase);
            return result.Status == JobAdmissionStatus.Accepted && result.Handle is IJobExecutionHandle job
                ? job : throw new InvalidOperationException($"R09 {handler} job was not admitted ({result.Status}).");
        }

        private async Task WaitForWorkersAsync(int occupied, CancellationToken token)
        {
            using var settle = CancellationTokenSource.CreateLinkedTokenSource(token);
            settle.CancelAfter(TimeSpan.FromSeconds(5));
            while (scheduler.ReadMetrics().OccupiedWorkerSlots != occupied)
                await Task.Delay(1, settle.Token).ConfigureAwait(false);
        }
    }

    private sealed class R09NoInput : IJobInputSource
    {
        public JobInputCapture TryCapture(in JobInputRequest request) =>
            throw new InvalidOperationException("R09 qualification jobs cannot capture input.");
    }
}

internal sealed class R09JobScope : IJobScopePolicy, IAdmittedJobScope
{
    internal static readonly JobHandlerId Speculative = new(1);
    internal static readonly JobHandlerId Protected = new(2);

    public ImmutableArray<ResourceKey> ReadScopes => [];
    public ImmutableArray<ResourceKey> CommandScopes => [];

    public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
        (submission.Handler == Speculative || submission.Handler == Protected) &&
        submission.InputKind == JobInputKind.None && submission.ResultKind == JobResultKind.Commandless &&
        submission.ReadScopes.IsEmpty && submission.CommandScopes.IsEmpty
            ? new(JobScopeStatus.Authorized, this) : new(JobScopeStatus.InvalidAccess, null);

    public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => ReferenceEquals(scope, this);

    public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second, in CommittedWorldView world) =>
        ReferenceEquals(first, this) && ReferenceEquals(second, this) ? JobScopeOverlap.Independent : JobScopeOverlap.Unknown;
}
