using Groundwork.Persistence;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private readonly record struct SaveVerification(VolumeCheckpointSlot Storage, VolumeCheckpoint Expected);

    private SaveVerification? _queuedSaveVerification;
    private bool HasQueuedSaveVerification => _queuedSaveVerification is not null;

    private string QueueSaveVerification(bool measuring)
    {
        if (measuring)
            return "Stop capture before verifying.";
        if (HasQueuedConsoleAction)
            return "Console action already queued.";
        if (slot is null)
            return "No save slot.";
        if (slot.ReadOnly)
            return "Save slot needs recovery.";
        if (slot.HasPendingWrite)
            return "Save write pending.\nF5/F10 to retry.";
        if (slot.Checkpoint is not { } expected)
            return "No committed checkpoint.";

        _queuedSaveVerification = new(slot.Storage, expected);
        return "Save verification queued.";
    }

    private async Task<string> VerifyQueuedSaveAsync(CancellationToken token)
    {
        if (_queuedSaveVerification is not { } verification)
            return "No save verification queued.";
        _queuedSaveVerification = null;

        if (slot is null || slot.ReadOnly || slot.HasPendingWrite ||
            !ReferenceEquals(verification.Storage, slot.Storage) ||
            !ReferenceEquals(verification.Expected, slot.Checkpoint))
            return "Save changed before verification.\nRun save verify again.";

        VolumeSlotResult result;
        try
        {
            result = await Task.Run(() => verification.Storage.VerifyCommitted(verification.Expected), token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return "Save verification canceled.";
        }

        return result.Status switch
        {
            VolumeSlotStatus.Verified => FormattableString.Invariant(
                $"Save verified.\nGeneration {verification.Expected.Generation} matches."),
            VolumeSlotStatus.Mismatch when result.Checkpoint is { } actual => FormattableString.Invariant(
                $"Save verification mismatch.\nExpected {verification.Expected.Generation} · disk {actual.Generation}"),
            VolumeSlotStatus.Mismatch => "Save verification mismatch.",
            VolumeSlotStatus.Busy => "Save slot busy.\nTry again.",
            VolumeSlotStatus.Indeterminate => "Save verification inconclusive.\nSlot state unresolved.",
            VolumeSlotStatus.Empty => "No committed checkpoint on disk.",
            VolumeSlotStatus.ReadOnlyRecovery or VolumeSlotStatus.Corrupt => "Save needs recovery.\nVerification held.",
            VolumeSlotStatus.Incompatible or VolumeSlotStatus.InvalidCheckpoint => "Incompatible save data.\nVerification held.",
            VolumeSlotStatus.Canceled => "Save verification canceled.",
            _ => "Save verification unavailable.",
        };
    }
}
