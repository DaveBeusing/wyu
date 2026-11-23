using Groundwork.Core;
using Groundwork.Persistence;

namespace WorldsYetUnseen.Client;

// The host serializes admission and completion. The tracked worker alone accesses the slot.
internal sealed class PrototypeSave(SaveSlot slot, IRuntimeOwnership ownership, SaveSlotResult initialLoad)
{
    private readonly Guid _lineage = initialLoad.Checkpoint?.Identity.WorldLineageId ?? Guid.NewGuid();
    private PendingSave? _active;
    private SaveRequest? _retry;
    private bool _stopped;

    internal SaveSlotResult InitialLoad { get; } = initialLoad;
    internal PrototypeCheckpoint? Checkpoint { get; private set; } = ReadInitialCheckpoint(initialLoad);
    internal ulong Generation { get; private set; } = initialLoad.Checkpoint?.Identity.Generation ?? 0;
    internal Guid Lineage => _lineage;
    internal bool IsWritable => InitialLoad.Status is SaveSlotStatus.Empty or SaveSlotStatus.Loaded;
    internal bool IsSaving => _active is not null;
    internal bool RetryPending => _retry is not null;
    internal SaveSlotResult? LastSave { get; private set; }

    internal static Task<PrototypeSave> OpenAsync(string directory, IRuntimeOwnership ownership,
        CancellationToken cancellationToken, Action? beforeResolve = null, Action<CancellationToken>? beforeLoad = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = new SaveSlot(directory, (id, version) =>
        {
            beforeResolve?.Invoke();
            return PrototypeWorld.ResolveGenerator(id, version);
        }, PrototypeCheckpoint.Validate);
        var lease = ownership.TryTrack(slot)
            ?? throw new InvalidOperationException("Core could not track the WP07 checkpoint load.");
        try
        {
            return Task.Run(() =>
            {
                using (lease)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    beforeLoad?.Invoke(cancellationToken);
                    var result = slot.Load();
                    cancellationToken.ThrowIfCancellationRequested();
                    return new PrototypeSave(slot, ownership, result);
                }
            });
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal bool RequestSave(PrototypeCheckpoint checkpoint) =>
        RequestSave(checkpoint, out _, requireMatchingRetry: false);

    internal bool RequestSaveForLeave(PrototypeCheckpoint checkpoint, out SaveFrameIdentity identity) =>
        RequestSave(checkpoint, out identity, requireMatchingRetry: true);

    internal bool HasConfirmedSave(SaveFrameIdentity identity, PrototypeCheckpoint checkpoint) =>
        LastSave is { Status: SaveSlotStatus.Committed or SaveSlotStatus.AlreadyCommitted, Identity: var committed } &&
        committed == identity && Checkpoint == checkpoint;

    private bool RequestSave(PrototypeCheckpoint checkpoint, out SaveFrameIdentity identity, bool requireMatchingRetry)
    {
        identity = default;
        // A normal Save can recover an uncertain older request. Only a matching request can authorize leaving.
        if (_stopped || IsSaving || Generation == ulong.MaxValue || !IsWritable ||
            requireMatchingRetry && _retry is { } retry && retry.Checkpoint != checkpoint)
            return false;

        var generation = Generation;
        var request = _retry ?? new SaveRequest(checkpoint,
            checkpoint.Create(new SaveFrameIdentity(generation + 1, _lineage, Guid.NewGuid())));
        if (ownership.TryTrack(request) is not { } lease)
            return false;

        var cancellation = new CancellationTokenSource();
        try
        {
            var work = Task.Run(() =>
            {
                using (lease)
                    return slot.Save(generation, request.Frame, cancellation.Token);
            });
            _active = new PendingSave(request, cancellation, work);
            identity = request.Frame.Identity;
            LastSave = null;
            return true;
        }
        catch
        {
            cancellation.Dispose();
            lease.Dispose();
            throw;
        }
    }

    internal void Advance()
    {
        if (_active is { Work.IsCompleted: true } active)
            Complete(active);
    }

    internal void Stop()
    {
        _stopped = true;
        _active?.Cancellation.Cancel();
    }

    internal async Task SettleAsync(CancellationToken cancellationToken)
    {
        if (IsSaving)
            await WaitForSaveAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SaveSlotResult> WaitForSaveAsync(CancellationToken cancellationToken)
    {
        var active = _active ?? throw new InvalidOperationException("No WP07 save is in progress.");
        try
        {
            await active.Work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (active.Work.IsFaulted)
        {
            // Consume the fault through completion to release the request cancellation source.
        }

        return Complete(active);
    }

    private SaveSlotResult Complete(PendingSave active)
    {
        _active = null;
        active.Cancellation.Dispose();
        var result = active.Work.GetAwaiter().GetResult();
        LastSave = result;
        if (result.Status is SaveSlotStatus.Committed or SaveSlotStatus.AlreadyCommitted)
        {
            Checkpoint = active.Request.Checkpoint;
            Generation = active.Request.Frame.Identity.Generation;
            _retry = null;
        }
        else if (result.Status == SaveSlotStatus.Indeterminate)
            _retry = active.Request;
        else if (result.Status is not (SaveSlotStatus.Busy or SaveSlotStatus.Canceled))
            _retry = null;

        return result;
    }

    private static PrototypeCheckpoint? ReadInitialCheckpoint(SaveSlotResult load)
    {
        if (load.Status != SaveSlotStatus.Loaded)
            return null;
        if (load.Checkpoint is not { } frame || PrototypeCheckpoint.Decode(frame, out var checkpoint) != SaveGameValidation.Valid)
            throw new InvalidDataException("A verified WP07 slot returned an invalid checkpoint.");

        return checkpoint;
    }

    private sealed record SaveRequest(PrototypeCheckpoint Checkpoint, SaveCheckpoint Frame);
    private sealed record PendingSave(SaveRequest Request, CancellationTokenSource Cancellation, Task<SaveSlotResult> Work);
}
