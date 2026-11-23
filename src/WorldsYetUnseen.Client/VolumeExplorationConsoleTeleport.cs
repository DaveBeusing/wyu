using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const string TeleportUsage = "teleport mark <name>\nteleport <x> <y> <z>\nteleport back";
    private PlanetVector? _queuedConsoleTeleport;
    private PlanetVector? _lastTeleportDeparture;

    private bool HasQueuedConsoleAction => _queuedConsoleTeleport is not null ||
        _queuedConsoleOre is not null || HasQueuedSaveVerification;

    private string QueueConsoleTeleport(string value, WorldConsoleMarks marks, bool measuring)
    {
        if (measuring || IsSpectating)
            return "Stop capture or spectate first.";
        if (HasQueuedConsoleAction)
            return "Console action already queued.";

        PlanetVector destination;
        if (value == "back")
        {
            if (_lastTeleportDeparture is not { } departure)
                return "No previous teleport.";
            destination = departure;
        }
        else if (value.StartsWith("mark ", StringComparison.Ordinal))
        {
            var name = value[5..].TrimStart();
            if (!marks.TryGetPosition(name, out destination))
                return "Waypoint unavailable in this world.";
        }
        else if (!TryReadConsolePoint(value, out destination))
        {
            return TeleportUsage;
        }

        _queuedConsoleTeleport = destination;
        return "Teleport queued.";
    }
}
