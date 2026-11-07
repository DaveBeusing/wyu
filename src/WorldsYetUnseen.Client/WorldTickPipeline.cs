using Groundwork.Core;
using Groundwork.Ecs;

namespace WorldsYetUnseen.Client;

internal sealed class WorldTickPipeline(EcsWorld world, InvalidOperationException? injectedFault, bool terrainEnabled,
    Action<TickContext>? onPublished = null, bool escapeCloses = true) : ITickPipeline
{
    private static readonly BarrierOutcome Stopped = new(BarrierStatus.Stopped, null);
    private readonly WorldPublicationPorts _ports = world.Safety.Publication;

    internal ulong PublishedTick { get; private set; }
    internal ulong GameplaySteps { get; private set; }
    internal bool CloseRequested { get; private set; }
    internal bool TerrainVoxelRemoved { get; private set; }

    public ValueTask<BarrierOutcome> BeginTickAsync(TickContext tick, CancellationToken cancellationToken) =>
        RunBarrierAsync(tick.Tick, BarrierKind.BeginTick, cancellationToken);

    public ValueTask<BarrierOutcome> CommitResultsAsync(TickContext tick, CancellationToken cancellationToken) =>
        RunBarrierAsync(tick.Tick, BarrierKind.CommitResults, cancellationToken);

    public ValueTask RunGameplayAsync(TickContext tick, FixedStep gameplayStep, CancellationToken cancellationToken)
    {
        GameplaySteps++;
        return ValueTask.CompletedTask;
    }

    public ValueTask RunBaseAsync(TickContext tick, CancellationToken cancellationToken)
    {
        if (injectedFault is not null && tick.Tick == 2)
            throw injectedFault;
        return ValueTask.CompletedTask;
    }

    public ObserverPublication TryPublishState(TickContext tick)
    {
        var generation = world.Safety.State.Read().PendingFaultGeneration;
        var result = world.PublishObserver(_ports.Committed.Read().Stamp, generation);
        if (result == ObserverPublication.Published)
        {
            PublishedTick = tick.Tick;
            if (terrainEnabled)
            {
                foreach (var input in tick.Input.Events)
                {
                    if (input is { Kind: InputEventKind.Pressed, Device: InputDevice.Keyboard, Control: InputControl.T })
                        TerrainVoxelRemoved = !TerrainVoxelRemoved;
                }
            }
            CloseRequested |= escapeCloses && (tick.Input.HeldControls.Contains(InputControl.Escape) ||
                tick.Input.Events.Any(static input => input.Kind == InputEventKind.Pressed && input.Control == InputControl.Escape));
            if (!CloseRequested)
                onPublished?.Invoke(tick);
        }
        return result;
    }

    private async ValueTask<BarrierOutcome> RunBarrierAsync(ulong tick, BarrierKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new BarrierRequest(_ports.Committed.Read().Stamp, tick, kind);
        var drain = await _ports.Borrow.CloseAndDrainAsync(request, world.Safety.CreateBorrowedDrainDeadline()).ConfigureAwait(false);
        if (drain == BorrowDrainStatus.Stopped)
            return Stopped;
        if (drain != BorrowDrainStatus.Drained)
            throw new InvalidOperationException($"{kind} borrow drain failed ({drain}).");

        var seal = _ports.Candidates.SealCandidates(request);
        if (seal.Status == CandidateSealStatus.Stopped)
            return Stopped;
        if (seal.Status != CandidateSealStatus.Sealed || seal.Candidates is null)
            throw new InvalidOperationException($"{kind} candidate seal failed ({seal.Status}).");

        try
        {
            using var plan = world.Stage(seal.Candidates);
            var preparation = plan.Prepare(world.Safety.State.Read().PendingFaultGeneration);
            return preparation switch
            {
                BarrierPreparationStatus.Prepared => plan.FinalizePublication(),
                BarrierPreparationStatus.Stopped => Stopped,
                _ => throw new InvalidOperationException($"{kind} preparation failed ({preparation})."),
            };
        }
        catch (ObjectDisposedException) when (world.Safety.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop))
        {
            return Stopped;
        }
    }
}
