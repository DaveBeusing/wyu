using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Platform.Windows;
using static WorldsYetUnseen.Client.PrototypeMenu;

namespace WorldsYetUnseen.Client;

internal sealed class AlphaStartMenu(WindowsHostModule window, PrototypeSave save, AlphaSettingsStore store)
{
    private static readonly string[] Guidance =
    [
        "Move with WASD. Look and aim with the mouse.",
        "Follow the objective marker. Aim at orange stone and press E to mine.",
        "Follow the build marker. Aim at the marked pad and press E to build and activate.",
        "Esc opens Pause. Select Save to keep progress. Continue resumes it from this menu."
    ];

    private readonly PrototypeCaption _caption = new();
    private string? _notice;
    internal AlphaSettings Settings { get; private set; } = AlphaSettings.Defaults;

    internal async Task<bool> ChooseAsync(CancellationToken cancellationToken)
    {
        (Settings, _notice) = store.Load();
        if (!AlphaWindowSize.TryApply(Settings.Size) && Settings.SizeIndex != 0)
        {
            Settings = Settings with { SizeIndex = 0 };
            _notice = "Saved window size does not fit this display";
            AlphaWindowSize.TryApply(Settings.Size);
        }

        var canStart = save.InitialLoad.Status == SaveSlotStatus.Empty;
        var canContinue = save.Checkpoint is not null;
        var selected = canStart ? 0 : canContinue ? 1 : 2;
        while (IsOpen)
        {
            _caption.Update($"WYU | {Option(0, "Start test mission", canStart)} | {Option(1, "Continue", canContinue)} | " +
                $"{Option(2, "Settings")} | {Option(3, "Controls")} | {Option(4, "Exit")} | {_notice ?? SlotStatus()} | Arrows / Enter");
            var input = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (Pressed(input, InputControl.Escape))
                return false;
            selected = Navigate(input, selected, 5);
            if (!Pressed(input, InputControl.Enter))
                continue;

            switch (selected)
            {
                case 0 when canStart:
                    await ShowGuidanceAsync(cancellationToken).ConfigureAwait(false);
                    return IsOpen;
                case 1 when canContinue:
                    return true;
                case 2:
                    await EditSettingsAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case 3:
                    await ShowGuidanceAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case 4:
                    return false;
            }
        }
        return false;

        string Option(int index, string label, bool enabled = true) =>
            $"{(index == selected ? "> " : "")}{label}{(enabled ? "" : " (unavailable)")}";
        string SlotStatus() => canStart ? "Empty slot" : save.Checkpoint is { Progress.BeaconOnline: true }
            ? "Rescue signal sent; Continue to flight"
            : canContinue
            ? save.InitialLoad.Recovery is { } recovery
                ? $"Recovered checkpoint {save.Generation} ({recovery.FileName})"
                : $"Checkpoint {save.Generation}"
            : $"Cannot load slot: {save.InitialLoad.Status}";
    }

    private bool IsOpen => window.Read().Phase == HostWindowPhase.Open;

    private async Task<InputSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(16, cancellationToken).ConfigureAwait(false);
        return window.Capture();
    }

    private async Task ShowGuidanceAsync(CancellationToken cancellationToken)
    {
        for (var page = 0; IsOpen && page < Guidance.Length;)
        {
            _caption.Update($"WYU | Controls {page + 1}/{Guidance.Length} | {Guidance[page]} | Enter next / Esc skip");
            var input = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (Pressed(input, InputControl.Escape))
                return;
            if (Pressed(input, InputControl.Enter))
                page++;
        }
    }

    private async Task EditSettingsAsync(CancellationToken cancellationToken)
    {
        var draft = Settings;
        var row = 0;
        string? error = null;
        while (IsOpen)
        {
            var (width, height) = draft.Size;
            string[] labels =
            [
                $"Window {width}x{height}", $"Mouse {draft.Sensitivity}%", $"Invert {(draft.InvertVertical ? "On" : "Off")}",
                "Apply", "Cancel", "Reset"
            ];
            labels[row] = "> " + labels[row];
            _caption.Update($"WYU Settings | {string.Join(" | ", labels)} | {error ?? "Arrows edit / Enter / Esc"}");
            var input = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (Pressed(input, InputControl.Escape))
                return;
            row = Navigate(input, row, labels.Length);
            var direction = Pressed(input, InputControl.Left) ? -1 : Pressed(input, InputControl.Right) ? 1 : 0;
            if (direction != 0)
            {
                draft = row switch
                {
                    0 => SelectSize(draft, direction),
                    1 => draft with { Sensitivity = Math.Clamp(draft.Sensitivity + 25 * direction, 25, 300) },
                    2 => draft with { InvertVertical = !draft.InvertVertical },
                    _ => draft
                };
            }
            if (!Pressed(input, InputControl.Enter))
                continue;
            if (row == 4)
                return;
            if (row == 5)
            {
                draft = AlphaSettings.Defaults;
                error = null;
            }
            if (row == 3 && TryApply(draft, out error))
                return;
        }
    }

    private bool TryApply(AlphaSettings draft, out string? error)
    {
        var resize = draft.SizeIndex != Settings.SizeIndex;
        if (resize && !AlphaWindowSize.TryApply(draft.Size))
        {
            error = "Resize rejected; previous settings kept";
            return false;
        }
        try
        {
            store.Publish(draft);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error = resize && !AlphaWindowSize.TryApply(Settings.Size)
                ? "Save failed; previous window size could not be restored" : "Settings could not be saved";
            return false;
        }
        Settings = draft;
        _notice = "Settings saved";
        error = null;
        return true;
    }

    private static AlphaSettings SelectSize(AlphaSettings settings, int direction)
    {
        for (var index = settings.SizeIndex + direction; index >= 0 && index < AlphaSettings.SizeCount; index += direction)
        {
            var candidate = settings with { SizeIndex = index };
            if (AlphaWindowSize.Fits(candidate.Size))
                return candidate;
        }
        return settings;
    }

    private static int Navigate(InputSnapshot input, int selected, int count) =>
        Pressed(input, InputControl.Up) ? (selected + count - 1) % count
        : Pressed(input, InputControl.Down) ? (selected + 1) % count : selected;
}
