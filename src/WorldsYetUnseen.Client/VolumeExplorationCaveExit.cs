using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    // Fixed shaft route for Ember Caves. Every segment is checked against the current terrain.
    private static readonly PlanetVector CaveExitAnchor = new(4137.228939746989, 49.20778064671379, 34.914613340334895);
    private static readonly PlanetVector[] CaveExitRoute =
    [
        default,
        new(2.5, 2.5, 0),
        new(2.75, 2.5, -0.25),
        new(3, 2.75, -0.25),
        new(3.75, 2.75, -1),
        new(5.25, 4.25, -1),
        new(6.75, 4.25, -2.5),
        new(8.5, 6, -2.5),
        new(11.25, 6, -2.5),
        new(11.25, 11, -2.5),
    ];

    private int _caveExitWaypoint = -1;

    private static bool AtCaveExitRope(PlanetWalkState state) =>
        Length(Subtract(state.Position, CaveExitAnchor)) <= 1.2;

    private PlanetVector? NextCaveClimb(PlanetWalkState state)
    {
        while (_caveExitWaypoint >= 0 && _caveExitWaypoint < CaveExitRoute.Length)
        {
            var remaining = Subtract(Add(CaveExitAnchor, CaveExitRoute[_caveExitWaypoint]), state.Position);
            var distance = Length(remaining);
            if (distance > 0.000001)
                return Scale(remaining, Math.Min(1, 0.12 / distance));
            _caveExitWaypoint++;
        }
        _caveExitWaypoint = -1;
        return null;
    }
}
