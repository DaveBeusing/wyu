using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal static class FlightLandingPalette
{
    // RGBA8, with red in the least significant byte.
    private const uint Ground = 0xFF435636;
    private const uint Outline = 0xFF203646;
    private const uint Amber = 0xFF20C8FF;
    internal static readonly Func<PlanetSampleKey, uint> Near = ColorNear;
    internal static readonly Func<PlanetSampleKey, uint> Far = static _ => Ground;

    private static uint ColorNear(PlanetSampleKey sample)
    {
        var direction = PlanetCoordinates.Direction(sample);
        var pad = FlightLandingSite.Direction;
        if (direction.X * pad.X + direction.Z * pad.Z <= 0)
            return Ground;

        // A diamond in the pad's east/north tangent axes gives the approach
        // silhouette four tips and a dark margin on the surrounding terrain.
        var east = -direction.X * pad.Z + direction.Z * pad.X;
        var diamond = FlightLandingSite.Radius * (Math.Abs(east) + Math.Abs(direction.Y));
        return diamond <= 2.35 ? Amber : diamond <= 2.95 ? Outline : Ground;
    }
}
