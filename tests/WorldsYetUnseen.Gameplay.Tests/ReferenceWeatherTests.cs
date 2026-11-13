using System.Diagnostics;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay.Tests;

internal static class ReferenceWeatherTests
{
    internal static void TransitionsAndBudget()
    {
        foreach (var seed in new ulong[] { 42, 20261111 })
        {
            var planet = new ReferencePlanet(new("aa0567e4-de48-416b-a55d-e21caf4fef6a"), seed);
            var weather = new ReferenceWeather(planet);
            var ground = new PlanetVector(0, ReferencePlanet.RadiusMeters, 0);
            var noon = weather.Evaluate(ground, 9_000);
            var night = weather.Evaluate(ground, 27_000);
            var dawn = weather.Evaluate(ground, 0);
            var dusk = weather.Evaluate(ground, 18_000);
            Require(night.Tint.X < dawn.Tint.X && dawn.Tint.X < noon.Tint.X && dawn.Tint == dusk.Tint,
                "Daylight failed on the +Y cycle.");
            foreach (var boundary in new[] { -384d, -128d, 128d, 384d })
            {
                var point = new PlanetVector(boundary, ground.Y, 0);
                var at = weather.Evaluate(point, 60);
                var left = weather.Evaluate(point with { X = boundary - 0.00001 }, 60);
                var right = weather.Evaluate(point with { X = boundary + 0.00001 }, 60);
                Require(Math.Abs(left.FogStart - at.FogStart) < 0.00002 && Math.Abs(right.FogStart - at.FogStart) < 0.00002,
                    "Regional stencil was discontinuous.");
                var last = weather.Evaluate(point, 59, 0.999999);
                Require(Math.Abs(last.FogStart - at.FogStart) < 0.00002, "Weather segment was discontinuous.");
                Require(new ReferenceWeather(planet).Evaluate(point, 60) == at, "Cache history changed weather.");
            }
            var edge = ground with { Y = ground.Y + 72 };
            Require(weather.Evaluate(edge, 9_000).FogOpacity == 0 &&
                weather.Evaluate(ground with { Y = ground.Y + 52 }, 9_000).FogOpacity > 0,
                "Altitude fade failed before the orbital handoff.");
            var start = weather.Evaluate(ground, 0);
            for (ulong tick = 0; tick <= 120; tick++)
                _ = weather.Evaluate(ground, tick);
            var paced = weather.Evaluate(ground, 120);
            Require(new ReferenceWeather(planet).Evaluate(ground, 120) == paced &&
                weather.Evaluate(ground, 0) == start, "Frame pacing or cache eviction changed the result.");

            var center = new PlanetVector(128, 3968, -128);
            var expected = seed == 42 ? (0.3540673f, 0.93092483f) : (0.5552089f, 0.5875999f);
            Require(Math.Abs(weather.Evaluate(center, 0).FogOpacity - expected.Item1) < 0.000001 &&
                Math.Abs(weather.Evaluate(center, 60).FogOpacity - expected.Item2) < 0.000001,
                "Regional weather golden samples changed.");
            var reachedClear = false;
            PlanetVector[] centers = [center, new(3968, 128, -128), new(-3968, 128, -128), new(128, -3968, -128),
                new(128, -128, 3968), new(128, -128, -3968)];
            foreach (var candidate in centers)
            for (ulong tick = 0; tick < 3600; tick += 60)
                reachedClear |= weather.Evaluate(candidate, tick).FogOpacity == 0;
            Require(reachedClear, "The golden fixture had no clear weather endpoints.");
            var beforeConstruction = GC.GetAllocatedBytesForCurrentThread();
            var fresh = new ReferenceWeather(planet);
            var residentBytes = GC.GetAllocatedBytesForCurrentThread() - beforeConstruction;
            Require(residentBytes <= 64 * 1024, "Weather cache exceeded its CPU memory budget.");
            var samples = new long[10_000];
            for (var i = 0; i < 2000; i++)
                _ = EvaluateSample(i);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples.Length; i++)
            {
                var began = Stopwatch.GetTimestamp();
                _ = EvaluateSample(i);
                samples[i] = Stopwatch.GetTimestamp() - began;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Require(allocated == 0, "Weather allocated after warm-up.");
            Array.Sort(samples);
            var p99 = 1000d * samples[9899] / Stopwatch.Frequency;
            Console.WriteLine(FormattableString.Invariant(
                $"Package E seed {seed}: weather p99 {p99:F6} ms, {residentBytes} resident managed bytes, {allocated} bytes across {samples.Length} updates."));

            ReferenceAtmosphere EvaluateSample(int i) =>
                fresh.Evaluate(ground with { X = i % 2 == 0 ? 127.9 : 128.1 }, (ulong)i * 60);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
