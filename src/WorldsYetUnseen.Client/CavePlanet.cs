using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

/// <summary>The shipped V2 geology recipe and bounded entrance route.</summary>
internal sealed class CavePlanet
{
    internal const ulong Seed = 42;
    internal const string Name = "Ember Caves";
    internal const ushort ShallowRock = 21;
    internal const ushort MiddleRock = 22;
    internal const ushort DeepRock = 24;
    internal const ushort Ore = 26;
    internal const double Radius = 4148;
    internal static readonly PlanetVector SurfaceProbe = new(Radius, 55, 35);
    internal static readonly PlanetVector Entrance = new(Radius, 52, 35);
    internal static readonly PlanetVector Heading = new(0, -1, 0);
    private static readonly Guid PlanetId = new("a15674a1-cc47-49fc-8c0a-abd0ae90e036");
    private static readonly PlanetGeologyRecipe Recipe = new(
        new PlanetLandformRecipe(61, 0, 0, new(17, 0), new(11, 0), new(5, 0), 13, 7, 19),
        4, 92, 19, 1, 7, 0, 8, 104, 65536, 65536, ShallowRock, MiddleRock, DeepRock, Ore);

    internal CavePlanet(Guid world, ulong seed)
    {
        if (seed != Seed)
            throw new ArgumentOutOfRangeException(nameof(seed), "Ember Caves uses the verified seed 42.");
        Descriptor = new(world, PlanetId, seed, PlanetGeologyField.GeneratorVersion,
            PlanetDensityField.TerrainSchemaVersion, 2, Recipe.ParameterSha256, Radius);
        Field = new(Descriptor, Recipe);
    }

    internal PlanetDescriptor Descriptor { get; }
    internal PlanetGeologyField Field { get; }

    internal static bool SupportsMaterial(ushort material) => material is ShallowRock or MiddleRock or DeepRock or Ore;
}
