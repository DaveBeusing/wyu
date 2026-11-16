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
        D3D12Renderer renderer, ulong generation, VolumeStreamingLease retained, VolumeStreamingSelection selection,
        CancellationToken token)
    {
        var gate = R09RenderHost?.R09UploadGate ?? throw new InvalidOperationException("The active R09 renderer has no native upload gate.");
        using var hold = gate.Arm();
        var uploadStarted = Stopwatch.GetTimestamp();
        RequireMesh(renderer.TryStageVolumeTerrain(generation));
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        pressure.RequireSaturated();
        var revision = retained.Info.TerrainRevision;
        var overlapClass = timing.Path == VolumeWorkClass.Recovery ? VolumeWorkClass.Movement : VolumeWorkClass.Recovery;
        var admission = terrain.TryStage(overlapClass, selection, revision, revision, MeshBytes,
            Charge(selection), Field, out var overlap);
        using var ownedOverlap = overlap;
        if (admission != VolumeAdmission.Accepted || ownedOverlap is null)
            throw new InvalidOperationException($"R09 could not admit a private overlap build ({admission}).");
        if (!await pressure.RunProtectedAsync(ownedOverlap, null, token).ConfigureAwait(false) ||
            renderer.IsVolumeTerrainReady(generation) || !terrain.Statistics.MovementHeld ||
            (_ports.Committed.Read().Root as VolumeWorldRoot)?.Terrain != retained.Info)
            throw new InvalidOperationException("R09 overlap lost the held upload or retained terrain.");

        var uploadReleased = hold.Release();
        await WaitAsync(renderer, () => renderer.IsVolumeTerrainReady(generation), token).ConfigureAwait(false);
        var ready = Stopwatch.GetTimestamp();
        pressure.RequireSaturated();
        var peak = terrain.Statistics.Peak;
        timing.Write("upload-to-observed-ready", uploadStarted, ready);
        timing.Write("held-upload-release-to-ready", uploadReleased, ready);
        Console.WriteLine(FormattableString.Invariant(
            $"WYU R09 PRESSURE {timing.Path} {peak.CpuBytes} {peak.NativeBytes} {peak.UploadBytes} {peak.RetirementBytes}"));
        await pressure.SettleAsync(token).ConfigureAwait(false);
    }

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

        internal async Task<bool> RunProtectedAsync(VolumeStreamingWork work, R09Timing? timing, CancellationToken token)
        {
            RequireSaturated();
            long started = 0, finished = 0;
            var completed = false;
            owner._r09Protected = cancellation =>
            {
                started = Stopwatch.GetTimestamp();
                completed = work.Execute(cancellation);
                finished = Stopwatch.GetTimestamp();
                return ValueTask.CompletedTask;
            };
            using var handle = Submit(R09JobScope.Protected);
            // Groundwork dispatch is explicit. The accepted job cannot start before this marker.
            var queued = Stopwatch.GetTimestamp();
            await scheduler.DispatchReadyAsync(phase, token).ConfigureAwait(false);
            JobExecutionReport result;
            try
            {
                // Only the private overlap build runs under the native gate watchdog.
                var deadline = TimeSpan.FromSeconds(timing is null ? 7 : 25);
                result = await handle.ExecutionCompletion.WaitAsync(deadline, token).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"R09 {timing?.Path.ToString() ?? "overlap"} job did not settle. " +
                    $"Execute entered={Volatile.Read(ref started) != 0}. {scheduler.ReadMetrics()}", error);
            }
            var observed = Stopwatch.GetTimestamp();
            if (result.Status != JobExecutionStatus.Succeeded || !completed)
                return false;
            await WaitForWorkersAsync(1, token).ConfigureAwait(false);
            owner._r09Protected = null;
            timing?.Write("worker-queue", queued, started);
            timing?.Write("worker", started, finished);
            timing?.Write("worker-completion-observation", finished, observed);
            return true;
        }

        internal void RequireSaturated()
        {
            var refused = scheduler.TrySubmit(Submission(R09JobScope.Speculative), phase);
            refused.Handle?.Dispose();
            if (refused.Status != JobAdmissionStatus.CapacityExceeded ||
                _running?.ExecutionCompletion.IsCompleted != false || _queued?.ExecutionCompletion.IsCompleted != false)
                throw new InvalidOperationException("R09 did not observe an occupied speculative worker and full speculative queue.");
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
