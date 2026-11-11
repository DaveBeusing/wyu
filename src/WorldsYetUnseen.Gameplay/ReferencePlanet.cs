using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay;

public enum ReferenceTerrainMaterial : ushort
{
    Basalt = 1,
    OchreStone = 2,
}

public readonly record struct ReferenceSpawn(PlanetVector Foot, VolumePublicationInfo Terrain);

public sealed class ReferencePlanet
{
    public const string RecipeId = "wyu.reference-planet.v1";
    public const uint MaterialCatalogVersion = 1;
    public const int RadiusMeters = 4096;
    public const string DisplayName = "Far Horizon";
    public static Guid PlanetId { get; } = new("144ad5db-7b9f-48fc-b316-beb39905c48e");

    private const int Units = PlanetDensityField.UnitsPerCell;
    private static readonly PlanetLandformRecipe Recipe = new(1024, -24 * Units, 32 * Units,
        new(192, 24 * Units), new(96, 12 * Units), new(24, 2 * Units), 48,
        (ushort)ReferenceTerrainMaterial.Basalt, (ushort)ReferenceTerrainMaterial.OchreStone);

    private static readonly (int X, int Z)[] SearchOrder =
        [(16, 16), (48, 16), (16, 48), (-16, 16), (16, -16), (-48, 16), (16, -48), (-16, -16)];

    public ReferencePlanet(Guid worldId, ulong seed)
    {
        Descriptor = new(worldId, PlanetId, seed, PlanetDensityField.GeneratorVersion,
            PlanetDensityField.TerrainSchemaVersion, MaterialCatalogVersion, Recipe.ParameterSha256, RadiusMeters);
        Field = new PlanetDensityField(Descriptor, Recipe);
    }

    public PlanetDescriptor Descriptor { get; }
    public PlanetDensityField Field { get; }

    /// <summary>Bounded candidate search on the positive-Y face. A host must confirm collision coverage before spawning.</summary>
    public IEnumerable<PlanetVector> FindSpawnCandidates()
    {
        foreach (var (x, z) in SearchOrder)
        {
            var height = FindSurface(x, z);
            if (height.HasValue)
                yield return new PlanetVector(x, height.Value, z);
        }
    }

    /// <summary>
    /// Confirms the spawn and landing footprint against the coordinator's published collision.
    /// Use the coordinator for this planet's initial terrain and revalidate the returned terrain at spawn publication.
    /// </summary>
    public bool TryConfirmSpawn(VolumeStreamingCoordinator coordinator, PlanetVector candidate, out ReferenceSpawn spawn)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        spawn = default;
        using var terrain = coordinator.TryRead();
        if (terrain is null || terrain.EditLineage is { } lineage &&
            (lineage != Descriptor || terrain.EditOverlayBytes != 8) || !FindSpawnCandidates().Contains(candidate))
            return false;
        var key = Descriptor.SampleAt(candidate);
        if (!terrain.TryReadDensity(key, out var density) || density != Field.SampleDensity(key))
            return false;

        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[4];
        Span<double> heights = stackalloc double[9];
        var index = 0;
        // Nine points across a 4 m by 4 m footprint, with an unobstructed approach from 12 m above.
        for (var z = -2; z <= 2; z += 2)
        for (var x = -2; x <= 2; x += 2)
        {
            var px = candidate.X + x;
            var pz = candidate.Z + z;
            var result = terrain.Raycast(new(px, candidate.Y + 12, pz), new(px, candidate.Y - 2, pz), hits);
            if (result.Status != VolumeQueryStatus.Contact || result.Count != 1 || hits[0].Normal.Y < 0.94)
                return false;
            heights[index++] = hits[0].Position.Y;
        }

        var center = heights[4];
        foreach (var height in heights)
            if (Math.Abs(height - center) > 0.5)
                return false;

        var lower = new PlanetVector(candidate.X, center + 0.8, candidate.Z);
        var upper = new PlanetVector(candidate.X, center + 2.0, candidate.Z);
        if (terrain.Sweep(new VolumeCapsule(lower, upper, 0.5), new(0, 8, 0)).Status != VolumeQueryStatus.Clear)
            return false;

        spawn = new ReferenceSpawn(new PlanetVector(candidate.X, center + 0.3, candidate.Z), terrain.Info);
        return true;
    }

    private double? FindSurface(int x, int z)
    {
        var above = Field.SampleDensity(new LatticeCoordinate(x, RadiusMeters + 64, z));
        if (above < 0)
            return null;
        for (var y = RadiusMeters + 63; y >= RadiusMeters - 48; y--)
        {
            var density = Field.SampleDensity(new LatticeCoordinate(x, y, z));
            if (density < 0)
                return y + (double)-density / (above - density);
            above = density;
        }
        return null;
    }
}
