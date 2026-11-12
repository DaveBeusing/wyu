using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay;

public enum ReferenceBiome : byte { Meadow, Steppe, Highland }

public readonly record struct ReferenceBiomeBlend(ReferenceBiome Dominant, byte Meadow, byte Steppe, byte Highland);

/// <summary>Versioned visual zones in canonical planet space, shared by near and orbital meshes.</summary>
public sealed class ReferenceBiomes
{
    public const string RecipeId = "wyu.reference-biomes.v1";
    private const int TableSize = 1024;
    private const int Midpoint = TableSize / 2;
    private const int BlendWidth = 32;
    private static readonly ColorEntry[] Palette = CreatePalette();
    private readonly double _x;
    private readonly double _y;
    private readonly double _z;

    public ReferenceBiomes(ReferencePlanet planet)
    {
        ArgumentNullException.ThrowIfNull(planet);
        var seed = Mix(planet.Descriptor.Seed);
        var angle = (seed & 0xffff) * (Math.Tau / 65536);
        var scale = 256 / planet.Descriptor.RadiusMeters;
        _x = Math.Cos(angle) * scale;
        _y = ((int)((seed >> 16) & 0xff) - 128) / 512.0 * scale;
        _z = Math.Sin(angle) * scale;
    }

    public ReferenceBiomeBlend Evaluate(PlanetVector position) => Palette[Index(position)].Blend;

    public uint Color(PlanetVector position, ushort material)
    {
        // The generated radial relief contributes to the canonical position. The
        // existing material adds the same tint to both representations.
        ref readonly var entry = ref Palette[Index(position)];
        return material == (ushort)ReferenceTerrainMaterial.OchreStone ? entry.Ochre : entry.Basalt;
    }

    private int Index(PlanetVector position) =>
        Math.Clamp((int)(Midpoint + position.X * _x + position.Y * _y + position.Z * _z), 0, TableSize - 1);

    private static ColorEntry[] CreatePalette()
    {
        var table = new ColorEntry[TableSize];
        for (var i = 0; i < table.Length; i++)
        {
            var steppe = Transition(i, 500);
            var highland = Transition(i, 615);
            var meadow = 255 - steppe;
            steppe -= highland;
            var dominant = meadow >= steppe && meadow >= highland ? ReferenceBiome.Meadow :
                steppe >= highland ? ReferenceBiome.Steppe : ReferenceBiome.Highland;
            var red = (76 * meadow + 174 * steppe + 102 * highland) / 255;
            var green = (128 * meadow + 126 * steppe + 116 * highland) / 255;
            var blue = (77 * meadow + 65 * steppe + 145 * highland) / 255;
            var basalt = 0xff000000u | (uint)(red | (green << 8) | (blue << 16));
            table[i] = new(new(dominant, (byte)meadow, (byte)steppe, (byte)highland), basalt, basalt + 12u);
        }
        return table;
    }

    private static int Transition(int index, int center)
    {
        var t = Math.Clamp((index - center + BlendWidth / 2) / (double)BlendWidth, 0, 1);
        return (int)Math.Round(255 * t * t * (3 - 2 * t));
    }

    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    private readonly record struct ColorEntry(ReferenceBiomeBlend Blend, uint Basalt, uint Ochre);
}
