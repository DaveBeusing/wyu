using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Gameplay.Tests;

internal static class ReferenceBiomeTests
{
    internal static void TwoSeedParity()
    {
        var points = Enum.GetValues<PlanetFace>().SelectMany(face =>
            Enumerable.Range(0, 9).SelectMany(y => Enumerable.Range(0, 9).Select(x =>
                ControlPoint(face, x, y)))).ToArray();
        ReferenceBiomeBlend[]? previous = null;
        foreach (var seed in new ulong[] { 42, 20261111 })
        {
            var recipe = new ReferenceBiomes(new ReferencePlanet(new Guid("aa0567e4-de48-416b-a55d-e21caf4fef6a"), seed));
            var serial = points.Select(recipe.Evaluate).ToArray();
            var replay = new ReferenceBiomeBlend[points.Length];
            Parallel.For(0, points.Length, i =>
            {
                var index = points.Length - 1 - i;
                replay[index] = recipe.Evaluate(points[index]);
            });
            Require(serial.SequenceEqual(replay), $"Seed {seed} differed with reordered parallel evaluation.");
            Require(serial.All(w => w.Meadow + w.Steppe + w.Highland == 255), "Blend weights were not normalized.");
            Require(serial.Select(w => w.Dominant).Distinct().Count() == 3, $"Seed {seed} omitted a zone.");
            Require(serial.Any(w => w.Meadow is > 0 and < 255 || w.Steppe is > 0 and < 255 || w.Highland is > 0 and < 255),
                $"Seed {seed} omitted blends.");
            Require(previous is null || !previous.SequenceEqual(serial), "Seeds shared identical zones.");

            var seams = points.Zip(serial).GroupBy(p => p.First).Where(group => group.Count() > 1).ToArray();
            Require(seams.Length > 0 && seams.All(group => group.Select(p => p.Second).Distinct().Count() == 1),
                "Shared cube-face samples disagreed.");

            _ = recipe.Color(points[0], 1);
            var before = GC.GetAllocatedBytesForCurrentThread();
            uint checksum = 0;
            for (var i = 0; i < points.Length; i++)
                checksum ^= recipe.Color(points[i], 1);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, $"Biome evaluation allocated {allocated} bytes.");
            Console.WriteLine($"WP13 seed {seed}: {points.Length} samples, {seams.Length} shared chart points, color XOR {checksum:x8}.");
            previous = serial;
        }
    }

    private static PlanetVector ControlPoint(PlanetFace face, int x, int y)
    {
        var direction = PlanetCoordinates.Direction(PlanetCoordinates.Sample(new(face, 0, 0, 0), x * 2, y * 2));
        return new(direction.X * 4096, direction.Y * 4096, direction.Z * 4096);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
