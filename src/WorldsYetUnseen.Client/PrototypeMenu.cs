using Groundwork.Core;
using Groundwork.Persistence;

namespace WorldsYetUnseen.Client;

internal sealed class PrototypeMenu(PrototypeSave save)
{
    private readonly SessionMenu _menu = new();
    private PendingLeave? _pendingLeave;
    private string? _notice;

    internal bool QuitRequested { get; private set; }

    internal string Caption => _menu.Caption(save.IsSaving
        ? _pendingLeave is null ? "Saving…" : "Saving before leaving… | Esc cancels leaving"
        : _notice ?? SaveStatus());

    internal void Handle(InputSnapshot input, PrototypeSession session, PrototypeCheckpoint checkpoint)
    {
        // An Escape on the completion tick must still be able to cancel the leave.
        var action = _menu.Handle(input, !session.Ready || save.Checkpoint != checkpoint || save.RetryPending,
            save.IsSaving || _pendingLeave is not null, _pendingLeave is not null);
        session.Paused = _menu.IsOpen;
        switch (action)
        {
            case SessionMenuAction.Resume:
                _notice = null;
                break;
            case SessionMenuAction.Save:
                StartSave(session, checkpoint, leave: false);
                break;
            case SessionMenuAction.SaveAndLeave:
                StartSave(session, checkpoint, leave: true);
                break;
            case SessionMenuAction.Leave:
                QuitRequested = true;
                break;
            case SessionMenuAction.CancelPendingLeave:
                _pendingLeave = null;
                _notice = "Leave canceled";
                break;
        }

        if (_pendingLeave is not { } pending || save.IsSaving)
            return;

        _pendingLeave = null;
        if (save.HasConfirmedSave(pending.Identity, pending.Checkpoint))
        {
            if (session.Ready && checkpoint == pending.Checkpoint)
                QuitRequested = true;
            else
                _notice = "Progress changed. Save again before leaving.";
        }
        else if (save.LastSave?.Status is SaveSlotStatus.Committed or SaveSlotStatus.AlreadyCommitted)
        {
            _notice = "The requested checkpoint was not confirmed. Select Save again.";
        }
    }

    private void StartSave(PrototypeSession session, PrototypeCheckpoint checkpoint, bool leave)
    {
        if (!save.IsWritable)
        {
            _notice = "Read-only recovery. You can resume or leave without saving.";
            return;
        }
        if (!session.Ready)
        {
            _notice = "Wait for terrain, then select Save again.";
            return;
        }
        if (leave && save.RequestSaveForLeave(checkpoint, out var identity))
        {
            _pendingLeave = new(identity, checkpoint);
            _notice = null;
        }
        else if (!leave && save.RequestSave(checkpoint))
        {
            _notice = null;
        }
        else
        {
            _notice = save.RetryPending
                ? "Select Save to retry the original checkpoint before leaving."
                : "Save unavailable. Resume or try again.";
        }
    }

    private string SaveStatus() => save.RetryPending
        ? "Save uncertain. Select Save to retry the original checkpoint."
        : save.LastSave?.Status switch
        {
            SaveSlotStatus.Committed or SaveSlotStatus.AlreadyCommitted => $"Saved generation {save.Generation}",
            { } failed => $"Save failed: {failed}. Resume or select Save to retry.",
            _ => save.Generation > 0 ? $"Checkpoint {save.Generation}" : "No checkpoint yet"
        };

    internal static bool Pressed(InputSnapshot input, InputControl control) => !input.TransitionLoss &&
        input.Events.Any(e => e.Kind == InputEventKind.Pressed && e.Device == InputDevice.Keyboard && e.Control == control);

    private readonly record struct PendingLeave(SaveFrameIdentity Identity, PrototypeCheckpoint Checkpoint);
}
