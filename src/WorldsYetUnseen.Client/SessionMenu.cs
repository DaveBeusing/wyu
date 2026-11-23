using Groundwork.Core;

namespace WorldsYetUnseen.Client;

internal enum SessionMenuAction { None, Resume, Save, SaveAndLeave, Leave, CancelPendingLeave }

internal sealed class SessionMenu
{
    private const int OptionCount = 4;
    private int _selection;
    private bool _confirmDiscard;

    internal bool IsOpen { get; private set; }

    internal string Caption(string? status)
    {
        if (_confirmDiscard)
            return "WYU paused | Leave without saving? Enter leaves | Esc cancels";
        var options = $"WYU paused | {Option(0, "Resume")} | {Option(1, "Save")} | {Option(2, "Leave")} | {Option(3, "Save and Leave")}";
        return status is null ? options : $"{options} | {status}";
    }

    internal SessionMenuAction Handle(InputSnapshot input, bool hasUnsavedProgress, bool saving, bool leaveAfterSave)
    {
        if (input.TransitionLoss || input.Events.Any(static e => e.Kind == InputEventKind.FocusCleared))
            return SessionMenuAction.None;

        if (Pressed(input, InputControl.Escape))
        {
            if (_confirmDiscard)
            {
                _confirmDiscard = false;
                return SessionMenuAction.None;
            }
            if (IsOpen && saving)
                return leaveAfterSave ? SessionMenuAction.CancelPendingLeave : SessionMenuAction.None;
            if (IsOpen)
                return Resume();

            IsOpen = true;
            _selection = 0;
            return SessionMenuAction.None;
        }

        if (!IsOpen || saving)
            return SessionMenuAction.None;
        var confirm = Pressed(input, InputControl.Enter);
        if (_confirmDiscard)
            return confirm ? SessionMenuAction.Leave : SessionMenuAction.None;

        if (Pressed(input, InputControl.Up))
            _selection = (_selection + OptionCount - 1) % OptionCount;
        else if (Pressed(input, InputControl.Down))
            _selection = (_selection + 1) % OptionCount;
        if (!confirm)
            return SessionMenuAction.None;

        switch (_selection)
        {
            case 0: return Resume();
            case 1: return SessionMenuAction.Save;
            case 2 when hasUnsavedProgress:
                _confirmDiscard = true;
                return SessionMenuAction.None;
            case 2: return SessionMenuAction.Leave;
            default: return SessionMenuAction.SaveAndLeave;
        }
    }

    private SessionMenuAction Resume()
    {
        IsOpen = false;
        return SessionMenuAction.Resume;
    }

    private string Option(int index, string text) => _selection == index ? $"> {text}" : text;

    private static bool Pressed(InputSnapshot input, InputControl control) =>
        input.Events.Any(e => e is { Kind: InputEventKind.Pressed, Device: InputDevice.Keyboard } && e.Control == control);
}
