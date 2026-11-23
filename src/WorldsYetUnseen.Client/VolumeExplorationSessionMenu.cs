using Groundwork.Core;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private async Task<bool> HandleSessionMenuAsync(ExplorationSessionMenu session, VolumeStreamingCoordinator terrain,
        InputSnapshot input, bool acceptsInput, CancellationToken token)
    {
        var save = session.Save;
        if (acceptsInput)
        {
            var hasUnsavedProgress = save is not null || slot?.HasPendingWrite == true ||
                slot?.Checkpoint?.Tick != _tick;
            var action = session.Menu.Handle(input, hasUnsavedProgress, save is not null, session.LeaveAfterSave);
            switch (action)
            {
                case SessionMenuAction.Resume:
                    session.Status = null;
                    if (overlay is not null)
                        overlay.Menu = null;
                    return false;
                case SessionMenuAction.Save:
                case SessionMenuAction.SaveAndLeave:
                    if (SessionSaveHoldReason() is { } hold)
                    {
                        session.Status = hold;
                        break;
                    }
                    session.LeaveAfterSave = action == SessionMenuAction.SaveAndLeave;
                    session.Command = session.LeaveAfterSave ? "Save and Leave" : "Save";
                    session.Save = RequestManualSaveAsync(terrain, token);
                    break;
                case SessionMenuAction.Leave:
                    return true;
                case SessionMenuAction.CancelPendingLeave:
                    session.LeaveAfterSave = false;
                    break;
            }
        }

        if (save is { IsCompleted: true })
        {
            session.Save = null;
            var completion = await save.ConfigureAwait(false);
            session.Status = ManualSaveNotice(completion, session.Command, sessionMenu: true);
            var leave = session.LeaveAfterSave && completion.CanLeave;
            session.LeaveAfterSave = false;
            if (leave)
                return true;
        }
        if (overlay is not null && session.Menu.IsOpen)
        {
            var status = session.Save is not null
                ? session.LeaveAfterSave
                    ? "Saving… Esc cancels leaving."
                    : "Saving… The session will stay open."
                : session.Status ?? (slot?.ReadOnly == true
                    ? "This recovered world is read-only. You can leave without saving."
                    : slot?.HasPendingWrite == true
                        ? "Previous save is uncertain. Select Save to check the pending write."
                        : null);
            overlay.Menu = session.Menu.Caption(status);
        }
        return false;
    }

    private string? SessionSaveHoldReason()
    {
        if (slot is null || slot.ReadOnly)
            return null;
        var inventory = ReadInventory();
        if (_caveExitWaypoint >= 0 && inventory?.Items.Load.MovementHeld != true)
            return "Resume and finish the cave climb before saving.";
        if (inventory?.Construction is { } construction && NeedsConstructionPresentation(construction))
            return "Resume and wait for the construction view to update before saving.";
        return null;
    }

    private sealed class ExplorationSessionMenu(CancellationToken token) : IAsyncDisposable
    {
        internal SessionMenu Menu { get; } = new();
        internal Task<ManualSaveCompletion>? Save { get; set; }
        internal bool LeaveAfterSave { get; set; }
        internal string Command { get; set; } = "Save";
        internal string? Status { get; set; }

        public async ValueTask DisposeAsync()
        {
            // Native window close cancels the stage. Drain any admitted write before the host disposes the slot.
            if (Save is not { } writing)
                return;
            try
            {
                await writing.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }
    }
}
