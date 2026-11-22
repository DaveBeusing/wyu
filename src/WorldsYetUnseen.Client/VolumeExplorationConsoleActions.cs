using System.Globalization;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private int? _queuedConsoleOre;

    private string QueueGive(string value, bool measuring)
    {
        if (!freeTestWorld)
            return "Give: Free Test World only.";
        if (measuring || IsSpectating)
            return "Stop capture or spectate first.";
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || fields[0] != "ore" ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count is not (>= 1 and <= 64))
            return "Usage: give ore <1–64>";
        if (HasQueuedConsoleAction)
            return "Console action already queued.";
        _queuedConsoleOre = count;
        return "Console action queued.";
    }

    private async Task<string> GiveConsoleOreAsync(int count, CancellationToken token)
    {
        if (slot?.ReadOnly != false || ReadInventory() is not { } inventory)
            return "Inventory unavailable.";
        var change = InventoryOperations.AcquireOre(inventory.Items, count);
        if (change.Status != InventoryChangeStatus.Applied)
            return $"Ore grant rejected.\n{change.Status}";
        if (!await PublishInventoryAsync(inventory, change.State, token).ConfigureAwait(false))
            return "Ore grant held.";
        return FormattableString.Invariant($"Granted {count} ore.");
    }

    private static bool TryReadConsolePoint(string value, out PlanetVector point)
    {
        point = default;
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || !TryCoordinate(fields[0], out var x) || !TryCoordinate(fields[1], out var y) ||
            !TryCoordinate(fields[2], out var z))
            return false;
        point = new(x, y, z);
        return true;

        static bool TryCoordinate(string value, out double result) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    }
}
