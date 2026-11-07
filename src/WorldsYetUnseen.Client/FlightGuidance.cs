using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal enum FlightPhase { Ready, Ascent, Insertion, Orbit, Return, Descent, Landed }

internal static class FlightGuidance
{
    internal const double OrbitRadius = 96;
    internal static readonly double OrbitSpeed = Math.Sqrt(PlanetFlight.GravitationalParameter / OrbitRadius);
    internal static PlanetVector Prograde(PlanetVector up) => new(-up.Z, 0, up.X);

    internal static PlanetVector Thrust(FlightPhase phase, PlanetFlightState state)
    {
        var radius = Length(state.Position);
        var up = Unit(state.Position);
        var tangent = Prograde(up);
        var radialSpeed = Dot(state.Velocity, up);
        var tangentSpeed = Dot(state.Velocity, tangent);
        var gravity = PlanetFlight.Gravity(state.Position);

        return phase switch
        {
            FlightPhase.Ascent or FlightPhase.Insertion => Subtract(Add(
                Scale(up, 3 * (Math.Clamp(0.9 * (OrbitRadius - radius), -6, 9) - radialSpeed) -
                    tangentSpeed * tangentSpeed / radius),
                Scale(tangent, 2 * ((phase == FlightPhase.Insertion ? OrbitSpeed : 0) - tangentSpeed) +
                    radialSpeed * tangentSpeed / radius)), gravity),
            FlightPhase.Return => Approach(state, gravity, 78, 12),
            FlightPhase.Descent => Approach(state, gravity, 63, 0.55),
            _ => default
        };
    }

    private static PlanetVector Approach(PlanetFlightState state, PlanetVector gravity, double radius, double speed)
    {
        var target = Scale(FlightLandingSite.Direction, radius);
        var desiredVelocity = Limit(Scale(Subtract(target, state.Position), 0.8), speed);
        return Subtract(Scale(Subtract(desiredVelocity, state.Velocity), 3), gravity);
    }
}
