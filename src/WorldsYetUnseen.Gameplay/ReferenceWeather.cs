using System.Numerics;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay;

public readonly record struct ReferenceAtmosphere(Vector4 Tint, Vector4 Sky, float FogStart, float FogEnd, float FogOpacity);

/// <summary>Presentation of the fixed-step clock and the local weather stencil.</summary>
public sealed class ReferenceWeather(ReferencePlanet planet)
{
    public const string RecipeId = "wyu.reference-weather.v1";
    public const ulong InitialTick = 9_000;
    private const int RegionSize = 256;
    private const int SegmentSteps = 60;
    private readonly ReferenceBiomes _biomes = new(planet);
    private readonly Entry[] _entries = new Entry[16];

    public ReferenceAtmosphere Evaluate(PlanetVector position, ulong tick, double fraction = 0)
    {
        var radius = Math.Sqrt(position.X * position.X + position.Y * position.Y + position.Z * position.Z);
        var angle = (tick % 36_000 + fraction) * (Math.Tau / 36_000);
        var elevation = (position.Y * Math.Sin(angle) + position.Z * Math.Cos(angle)) / radius;
        var daylight = (float)Smooth(-0.15, 0.20, elevation);
        var ambient = 0.18f + 0.82f * daylight;
        var sky = Vector4.Lerp(new(0.025f, 0.035f, 0.075f, 1), new(0.18f, 0.27f, 0.41f, 1), daylight);
        var altitude = radius - planet.Descriptor.RadiusMeters;
        var density = altitude >= 72 ? 0f : (float)Density(position, tick, fraction);
        var opacity = density * (float)(1 - Smooth(32, 72, altitude));
        return new(new(ambient, ambient, ambient, 1), sky, 128 - 104 * density, 768 - 640 * density, opacity);
    }

    private double Density(PlanetVector position, ulong tick, double fraction)
    {
        var x = position.X / RegionSize - 0.5;
        var y = position.Y / RegionSize - 0.5;
        var z = position.Z / RegionSize - 0.5;
        var ix = (long)Math.Floor(x);
        var iy = (long)Math.Floor(y);
        var iz = (long)Math.Floor(z);
        var segment = tick / SegmentSteps;
        var time = (tick % SegmentSteps + fraction) / SegmentSteps;
        for (var i = 0; i < _entries.Length; i++)
        {
            ref var entry = ref _entries[i];
            var key = entry.Key;
            if (entry.Occupied && (key.X < ix || key.X > ix + 1 || key.Y < iy || key.Y > iy + 1 ||
                key.Z < iz || key.Z > iz + 1 || key.Segment < segment || key.Segment > segment + 1))
                entry = default;
        }
        x -= ix;
        y -= iy;
        z -= iz;
        var density = 0d;
        for (var dz = 0; dz < 2; dz++)
        for (var dy = 0; dy < 2; dy++)
        for (var dx = 0; dx < 2; dx++)
        {
            var key = new RegionKey(ix + dx, iy + dy, iz + dz, segment);
            var current = Target(key);
            var next = Target(key with { Segment = segment + 1 });
            density += (current + (next - current) * time) *
                (dx == 0 ? 1 - x : x) * (dy == 0 ? 1 - y : y) * (dz == 0 ? 1 - z : z);
        }
        return Math.Clamp(density, 0, 1);
    }

    private double Target(RegionKey key)
    {
        var available = -1;
        for (var i = 0; i < _entries.Length; i++)
        {
            ref readonly var entry = ref _entries[i];
            if (entry.Occupied && entry.Key == key)
                return entry.Density;
            if (!entry.Occupied)
                available = i;
        }
        var x = (key.X + 0.5) * RegionSize;
        var y = (key.Y + 0.5) * RegionSize;
        var z = (key.Z + 0.5) * RegionSize;
        var scale = planet.Descriptor.RadiusMeters / Math.Sqrt(x * x + y * y + z * z);
        var point = new PlanetVector(x * scale, y * scale, z * scale);
        var height = planet.Field.SampleLandform(planet.Descriptor.SampleAt(point).Vertex).Elevation /
            (double)PlanetDensityField.UnitsPerCell;
        scale *= 1 + height / planet.Descriptor.RadiusMeters;
        var meadow = _biomes.Evaluate(new(x * scale, y * scale, z * scale)).Meadow;
        var seed = planet.Descriptor.Seed;
        var noise = Mix(Mix(Mix(Mix(seed ^ unchecked((ulong)key.X)) ^ unchecked((ulong)key.Y)) ^
            unchecked((ulong)key.Z)) ^ key.Segment);
        var u = (noise >> 40) / 16777216d;
        var density = Smooth(0.20, 0.85, 0.65 * u + 0.20 * meadow / 255 +
            0.15 * Math.Clamp((64 - height) / 128, 0, 1));
        _entries[available] = new(key, density, true);
        return density;
    }

    private static double Smooth(double start, double end, double value)
    {
        var t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }

    private readonly record struct RegionKey(long X, long Y, long Z, ulong Segment);
    private readonly record struct Entry(RegionKey Key, double Density, bool Occupied);
}
