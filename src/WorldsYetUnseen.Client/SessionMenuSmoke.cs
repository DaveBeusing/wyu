using System.Collections.Immutable;
using Groundwork.Core;

namespace WorldsYetUnseen.Client;

internal static class SessionMenuSmoke
{
    internal static void Run()
    {
        var menu = new SessionMenu();
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Escape, InputControl.Enter));
        if (!menu.IsOpen)
            throw new InvalidOperationException("Escape must open the menu and consume same-frame Enter.");
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Down));
        Expect(menu, SessionMenuAction.Save, Keys(InputControl.Enter));
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Down));
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Enter));
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Escape, InputControl.Enter));
        if (!menu.IsOpen)
            throw new InvalidOperationException("Cancelling discard must leave the session paused.");
        Expect(menu, SessionMenuAction.Resume, Keys(InputControl.Escape));
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Escape));
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Up));
        Expect(menu, SessionMenuAction.SaveAndLeave, Keys(InputControl.Enter));
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Enter), saving: true, leaveAfterSave: true);
        Expect(menu, SessionMenuAction.CancelPendingLeave, Keys(InputControl.Escape), saving: true, leaveAfterSave: true);
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Escape), saving: true);
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Enter) with { TransitionLoss = true });
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Enter) with
        {
            Events = [new(InputEventKind.FocusCleared, InputDevice.Keyboard, InputControl.Escape, 2, 0, default, 0),
                .. Keys(InputControl.Enter).Events]
        });
        Expect(menu, SessionMenuAction.None, Keys(InputControl.Up));
        Expect(menu, SessionMenuAction.Leave, Keys(InputControl.Enter), hasUnsavedProgress: false);
    }

    private static InputSnapshot Keys(params InputControl[] controls) => new(1,
        controls.Select((control, index) => new InputEvent(InputEventKind.Pressed, InputDevice.Keyboard, control,
            (ulong)index + 1, 0, default, 0)).ToImmutableArray(), controls.ToImmutableArray(), null, false);

    private static void Expect(SessionMenu menu, SessionMenuAction expected, InputSnapshot input,
        bool hasUnsavedProgress = true, bool saving = false, bool leaveAfterSave = false)
    {
        var observed = menu.Handle(input, hasUnsavedProgress, saving, leaveAfterSave);
        if (observed != expected)
            throw new InvalidOperationException($"Session menu expected {expected} but returned {observed}.");
    }
}
