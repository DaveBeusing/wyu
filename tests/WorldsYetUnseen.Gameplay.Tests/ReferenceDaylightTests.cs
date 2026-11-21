using System.Numerics;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay.Tests;

internal static class ReferenceDaylightTests
{
    internal static void DarkNightsAndWarmTwilight()
    {
        var night = ReferenceDaylight.Evaluate(-1);
        var dawn = ReferenceDaylight.Evaluate(-0.04f);
        var low = ReferenceDaylight.Evaluate(0.08f);
        var noon = ReferenceDaylight.Evaluate(1);
        Require(night.Direct == Vector3.Zero && dawn.Direct == Vector3.Zero,
            "Below-horizon sunlight illuminated the terrain directly.");
        Require(low.Direct.X > low.Direct.Y && low.Direct.Y > low.Direct.Z &&
            noon.Direct.X == noon.Direct.Y && noon.Direct.Y == noon.Direct.Z,
            "Low sunlight must be warm and midday sunlight neutral.");
        Require(Luminance(night.Ambient) < Luminance(dawn.Ambient) &&
            Luminance(night.Tint.AsVector3()) < Luminance(noon.Tint.AsVector3()) &&
            Luminance(night.Background.AsVector3()) < Luminance(noon.Background.AsVector3()),
            "Night terrain and background must remain darker than daytime.");
    }

    internal static void ContinuousBoundedAndAllocationFree()
    {
        var previous = ReferenceDaylight.Evaluate(-1);
        for (var i = 1; i <= 4000; i++)
        {
            var next = ReferenceDaylight.Evaluate(-1 + i / 2000f);
            Require(Vector3.Distance(previous.Direct, next.Direct) < 0.01f &&
                Vector3.Distance(previous.Ambient, next.Ambient) < 0.01f &&
                Vector4.Distance(previous.Background, next.Background) < 0.01f,
                "A solar-elevation boundary caused a lighting jump.");
            Require(Bounded(next.Direct) && Bounded(next.Ambient) && Bounded(next.Background.AsVector3()),
                "The daylight profile produced an invalid colour.");
            previous = next;
        }
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -2, 2 })
        {
            try { _ = ReferenceDaylight.Evaluate(invalid); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException("An invalid solar elevation was accepted.");
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4000; i++)
            _ = ReferenceDaylight.Evaluate(-1 + i / 2000f);
        Require(GC.GetAllocatedBytesForCurrentThread() == bytes, "Lighting allocated after warm-up.");
    }

    internal static void ClockAndRestoreParity()
    {
        var position = new PlanetVector(0, ReferencePlanet.RadiusMeters, 0);
        var planet = new ReferencePlanet(new("aa0567e4-de48-416b-a55d-e21caf4fef6a"), 42);
        var weather = new ReferenceWeather(planet);
        foreach (var tick in new ulong[] { 0, 1, 9_000, 17_500, 18_000, 18_500, 27_000, 35_500, 35_999, ulong.MaxValue })
        {
            var before = weather.Evaluate(position, tick, 0.5);
            _ = weather.Evaluate(position, 9_000);
            Require(before == new ReferenceWeather(planet).Evaluate(position, tick, 0.5) &&
                before == weather.Evaluate(position, tick, 0.5), "Restored or paused lighting changed phase.");
            var sun = ReferenceWeather.SunDirection(tick, 0.5);
            Require(Vector4.Distance(before.Tint, ReferenceDaylight.Evaluate(sun.Y).Tint) < 0.000001f,
                "Weather and sunlight disagree on the solar phase.");
        }
        Require(Vector3.Distance(ReferenceWeather.SunDirection(35_999, 1), ReferenceWeather.SunDirection(36_000)) < 0.000001f,
            "The existing 36,000-tick solar cycle was discontinuous.");
    }

    internal static void PresetSunOverridesClockWithoutChangingWeather()
    {
        foreach (var seed in new ulong[] { 42, 20261111 })
        {
            var planet = new ReferencePlanet(new("aa0567e4-de48-416b-a55d-e21caf4fef6a"), seed);
            var weather = new ReferenceWeather(planet);
            var position = new PlanetVector(0, ReferencePlanet.RadiusMeters, 0);
            foreach (var tick in new ulong[] { 0, 9_000, 18_000, 27_000, 35_999 })
            {
                var clock = weather.Evaluate(position, tick, 0.5);
                foreach (var direction in new[] { Vector3.UnitY, -Vector3.UnitY,
                    Vector3.Normalize(new Vector3(0.68f, 0.18f, 0.70f)) })
                {
                    var actual = weather.Evaluate(position, tick, 0.5, direction);
                    var expected = ReferenceDaylight.Evaluate(direction.Y);
                    Require(actual.Tint == expected.Tint && actual.Sky == expected.Background,
                        "Preset sky/weather lighting ignored the shared solar direction.");
                    Require(actual.FogStart == clock.FogStart && actual.FogEnd == clock.FogEnd &&
                        actual.FogOpacity == clock.FogOpacity,
                        "A presentation-only sun override changed the weather-density recipe.");
                    Require(actual == new ReferenceWeather(planet).Evaluate(position, tick, 0.5, direction),
                        "Preset lighting changed after a fresh weather cache/Continue.");
                }
            }
        }
    }

    private static Vector3 AsVector3(this Vector4 value) => new(value.X, value.Y, value.Z);
    private static bool Bounded(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) &&
        v.X is >= 0 and <= 1 && v.Y is >= 0 and <= 1 && v.Z is >= 0 and <= 1;
    private static float Luminance(Vector3 v) => Vector3.Dot(v, new(0.2126f, 0.7152f, 0.0722f));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
