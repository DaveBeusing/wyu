using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Platform.Windows;

namespace WorldsYetUnseen.Client;

internal sealed class PrototypeMenu(PrototypeSave save)
{
    private int _selection;
    private bool _confirmQuit;
    private string? _notice;

    internal bool QuitRequested { get; private set; }

    internal string Caption
    {
        get
        {
            if (_confirmQuit)
                return "WYU | Unsaved progress | Enter quits | Esc cancels";
            var status = save.IsSaving ? "Saving…"
                : save.RetryPending ? "Save uncertain | Select Save to retry the original checkpoint"
                : _notice ?? (save.LastSave?.Status switch
                {
                    SaveSlotStatus.Committed or SaveSlotStatus.AlreadyCommitted => $"Saved generation {save.Generation}",
                    { } failed => $"Save failed: {failed}",
                    _ => save.Generation > 0 ? $"Checkpoint {save.Generation}" : "No checkpoint yet"
                });
            return $"WYU paused | {Option(0, "Resume")} | {Option(1, "Save")} | {Option(2, "Quit")} | {status}";
        }
    }

    internal static async Task<bool> ChooseStartAsync(WindowsHostModule window, PrototypeSave save,
        PrototypeCaption caption, CancellationToken cancellationToken)
    {
        var canStart = save.InitialLoad.Status == SaveSlotStatus.Empty;
        var canContinue = save.Checkpoint is not null;
        var selected = canStart ? 0 : canContinue ? 1 : 2;
        while (window.Read().Phase == HostWindowPhase.Open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var details = canStart ? "Empty slot" : canContinue
                ? save.InitialLoad.Recovery is { } recovery
                    ? $"Recovered generation {save.Generation}: {recovery.FileName} ({recovery.Reason})"
                    : $"Checkpoint {save.Generation}"
                : $"Cannot load slot: {save.InitialLoad.Status}";
            caption.Update($"WYU | {details} | {Option(0, "New Game", canStart)} | {Option(1, "Continue", canContinue)} | {Option(2, "Quit", true)} | Arrows / Enter");
            var input = window.Capture();
            if (Pressed(input, InputControl.Escape))
                return false;
            selected = Select(input, selected);
            if (Pressed(input, InputControl.Enter))
            {
                if (selected == 2)
                    return false;
                if ((selected == 0 && canStart) || (selected == 1 && canContinue))
                    return true;
            }
            await Task.Delay(16, cancellationToken).ConfigureAwait(false);
        }
        return false;

        string Option(int index, string text, bool enabled) =>
            $"{(selected == index ? "> " : "")}{text}{(enabled ? "" : " (unavailable)")}";
    }

    internal void Handle(InputSnapshot input, PrototypeSession session, PrototypeCheckpoint checkpoint)
    {
        if (_confirmQuit)
        {
            if (Pressed(input, InputControl.Escape))
                _confirmQuit = false;
            else if (Pressed(input, InputControl.Enter))
                QuitRequested = true;
            return;
        }
        if (Pressed(input, InputControl.Escape))
        {
            session.Paused = !session.Paused;
            _selection = 0;
            _notice = null;
            return;
        }
        if (!session.Paused)
            return;
        _selection = Select(input, _selection);
        if (!Pressed(input, InputControl.Enter))
            return;
        switch (_selection)
        {
            case 0:
                session.Paused = false;
                _notice = null;
                break;
            case 1:
                _notice = !session.Ready ? "Wait for terrain"
                    : save.IsSaving ? "Save already in progress"
                    : save.RequestSave(checkpoint) ? null
                    : "Save unavailable";
                break;
            case 2:
                _confirmQuit = save.IsSaving || save.Checkpoint != checkpoint;
                QuitRequested = !_confirmQuit;
                break;
        }
    }

    private string Option(int index, string text) => _selection == index ? $"> {text}" : text;

    internal static bool Pressed(InputSnapshot input, InputControl control) => !input.TransitionLoss &&
        input.Events.Any(e => e.Kind == InputEventKind.Pressed && e.Device == InputDevice.Keyboard && e.Control == control);

    private static int Select(InputSnapshot input, int selected)
    {
        if (Pressed(input, InputControl.Up))
            return (selected + 2) % 3;
        return Pressed(input, InputControl.Down) ? (selected + 1) % 3 : selected;
    }
}
