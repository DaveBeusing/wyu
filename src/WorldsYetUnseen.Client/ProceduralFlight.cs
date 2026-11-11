using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal enum ProceduralFlightPhase { Ascent, Injection, Orbit, Brake, Descent, FinalApproach, Landed }

// Flight uses the reference recipe's actual radius and the same 60 Hz step as walking.
// A bounded controller tracks the powered legs. Orbit is an unpowered Verlet integration.
internal sealed class ProceduralFlight
{
    private const double Dt = PlanetSurfaceWalker.StepSeconds;
    private const double MaximumThrust = PlanetFlight.MaximumThrust;
    private readonly double _mu;
    private readonly double _orbitRadius;
    private readonly double _angularSpeed;
    private readonly double _padRadius;
    private readonly PlanetVector _up;
    private readonly PlanetVector _east;
    private int _legTick;
    private double _angle, _orbitStart, _brakeStart, _brakeRadius;
    private int _brakeTicks;

    internal ProceduralFlight(double radius, PlanetVector pad, PlanetVector heading)
    {
        _padRadius = Length(pad);
        _up = Unit(pad);
        _east = Unit(Subtract(heading, Scale(_up, Dot(_up, heading))));
        _mu = PlanetSurfaceWalker.GravityStrength * radius * radius;
        _orbitRadius = radius + 160;
        _angularSpeed = Math.Sqrt(_mu / _orbitRadius / _orbitRadius / _orbitRadius);
        State = new PlanetFlightState(pad, default, PlanetFlightOrientation.Identity, Grounded: true);
    }

    internal PlanetFlightState State { get; private set; }
    internal ProceduralFlightPhase Phase { get; private set; }
    internal int Tick { get; private set; }
    internal bool CompletedOrbit { get; private set; }
    internal double OrbitDrift { get; private set; }
    internal double Height => Length(State.Position) - _padRadius;

    internal PlanetFlightState Propose()
    {
        var tick = _legTick + 1;
        return Phase switch
        {
            ProceduralFlightPhase.Ascent => Track(Scale(_up, Mix(_padRadius, _orbitRadius, Smooth(tick / 1440d)))),
            ProceduralFlightPhase.Injection => Track(Circle(_orbitRadius, _angularSpeed * 20 * RampIntegral(tick / 1200d))),
            ProceduralFlightPhase.Orbit => Coast(),
            ProceduralFlightPhase.Brake => Track(Circle(Mix(_brakeRadius, _orbitRadius, Smooth(tick / (double)_brakeTicks)),
                Mix(_brakeStart, 2 * Math.Tau, 2 * (tick / (double)_brakeTicks - RampIntegral(tick / (double)_brakeTicks))))),
            ProceduralFlightPhase.Descent => Track(Scale(_up, Mix(_orbitRadius, _padRadius + 2.0, Smooth(tick / 1440d)))),
            ProceduralFlightPhase.FinalApproach => Track(Add(State.Position, Scale(_up, -0.45 * Dt))),
            _ => throw new InvalidOperationException("The craft has landed.")
        };
    }

    internal void Accept(PlanetFlightState next)
    {
        var previous = Angle(State.Position);
        var delta = Math.IEEERemainder(Angle(next.Position) - previous, Math.Tau);
        _angle += delta;
        State = next;
        Tick++;
        _legTick++;
        switch (Phase)
        {
            case ProceduralFlightPhase.Ascent when _legTick == 1440:
                Change(ProceduralFlightPhase.Injection);
                break;
            case ProceduralFlightPhase.Injection when _legTick == 1200:
                _orbitStart = _angle;
                Change(ProceduralFlightPhase.Orbit);
                break;
            case ProceduralFlightPhase.Orbit:
                OrbitDrift = Math.Max(OrbitDrift, Math.Abs(Length(next.Position) - _orbitRadius));
                if (OrbitDrift > 12)
                    throw new InvalidOperationException("Procedural orbit exceeded the 12 m radial envelope.");
                CompletedOrbit |= _angle - _orbitStart >= Math.Tau;
                if (CompletedOrbit && _angle >= 2 * Math.Tau - _angularSpeed * 10)
                {
                    _brakeStart = _angle;
                    _brakeRadius = Length(next.Position);
                    var omega = Dot(next.Velocity, Unit(Subtract(Scale(_east, Math.Cos(_angle)),
                        Scale(_up, Math.Sin(_angle))))) / _brakeRadius;
                    _brakeTicks = (int)Math.Round(2 * (2 * Math.Tau - _angle) / omega / Dt);
                    Change(ProceduralFlightPhase.Brake);
                }
                break;
            case ProceduralFlightPhase.Brake when _legTick == _brakeTicks:
                Change(ProceduralFlightPhase.Descent);
                break;
            case ProceduralFlightPhase.Descent when _legTick == 1440:
                Change(ProceduralFlightPhase.FinalApproach);
                break;
            case ProceduralFlightPhase.FinalApproach when next.Grounded:
                Change(ProceduralFlightPhase.Landed);
                break;
        }
    }

    private PlanetFlightState Track(PlanetVector target)
    {
        var gravity = Gravity(State.Position);
        var velocity = Scale(Subtract(target, State.Position), 1 / Dt);
        var thrust = Limit(Subtract(Scale(Subtract(velocity, State.Velocity), 1 / Dt), gravity), MaximumThrust);
        velocity = Add(State.Velocity, Scale(Add(gravity, thrust), Dt));
        return new(Add(State.Position, Scale(velocity, Dt)), velocity, State.Orientation);
    }

    private PlanetFlightState Coast()
    {
        var first = Gravity(State.Position);
        var position = Add(State.Position, Add(Scale(State.Velocity, Dt), Scale(first, 0.5 * Dt * Dt)));
        var velocity = Add(State.Velocity, Scale(Add(first, Gravity(position)), 0.5 * Dt));
        return new(position, velocity, State.Orientation);
    }

    private PlanetVector Gravity(PlanetVector position)
    {
        var radius = Length(position);
        return Scale(position, -_mu / (radius * radius * radius));
    }

    private void Change(ProceduralFlightPhase phase) { Phase = phase; _legTick = 0; }
    private double Angle(PlanetVector position) => Math.Atan2(Dot(position, _east), Dot(position, _up));
    private PlanetVector Circle(double radius, double angle) =>
        Scale(Add(Scale(_up, Math.Cos(angle)), Scale(_east, Math.Sin(angle))), radius);
    private static double Mix(double a, double b, double t) => a + (b - a) * t;
    private static double Smooth(double t) => t * t * t * (10 + t * (-15 + 6 * t));
    private static double RampIntegral(double t) => t * t * t * (1 - 0.5 * t);
}
