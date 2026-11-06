using Groundwork.Persistence;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal static class WalkableTerrain
{
    private const int Unit = VoxelCollision.UnitsPerVoxel;
    private const int EditX = -1;
    private const int EditY = 3;
    private const int EditZ = 4;

    internal static readonly SaveWorldRecipe Recipe = new(1, 1, 0x57505535, new RegionBounds(-4, 4, -2, 2));
    internal static readonly VoxelPose Spawn = new(-20 * Unit - Unit / 2, 4 * Unit, 7 * Unit + Unit / 2);
    internal static readonly ChunkCoordinate EditChunk = new(-1, 0, 0);
    internal const ushort EditIndex = (ChunkSnapshot.Size - 1) + ChunkSnapshot.Size * (EditY + ChunkSnapshot.Size * EditZ);

    internal static ISaveTerrainGenerator Generator { get; } = new RouteGenerator();

    internal static ChunkSnapshot Snapshot(ChunkCoordinate coordinate, ReadOnlySpan<TerrainChunkDelta> terrain)
    {
        foreach (var chunk in terrain)
            if (chunk.Coordinate == coordinate)
                return TerrainOverlay.CreateSnapshot(Recipe, coordinate, Recipe.GeneratorVersion, chunk.Cells, Generator);

        return TerrainOverlay.CreateSnapshot(Recipe, coordinate, Recipe.GeneratorVersion, [], Generator);
    }

    internal static bool TryToggle(VoxelPose pose, TerrainChunkDelta[] terrain, out TerrainChunkDelta[] changed)
    {
        changed = terrain;
        const int half = VoxelCollision.HalfWidth;
        // Leave the player's current support and body clearance intact through the edit.
        if (pose.X + half > EditX * Unit && pose.X - half < (EditX + 1) * Unit &&
            pose.Z + half > EditZ * Unit && pose.Z - half < (EditZ + 1) * Unit)
            return false;

        var chunks = new List<TerrainChunkDelta>(terrain);
        var chunkIndex = chunks.FindIndex(chunk => chunk.Coordinate == EditChunk);
        var cells = chunkIndex < 0 ? new List<TerrainCellDelta>() : new(chunks[chunkIndex].Cells.ToArray());
        var cellIndex = cells.FindIndex(cell => cell.Index == EditIndex);
        if (cellIndex < 0)
        {
            if (terrain.Sum(chunk => chunk.Cells.Length) >= SaveCodec.MaximumEditedCells)
                return false;

            cells.Add(new TerrainCellDelta(EditIndex, 0));
            cells.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        }
        else
        {
            cells.RemoveAt(cellIndex);
        }

        if (chunkIndex < 0)
        {
            chunks.Add(new TerrainChunkDelta(EditChunk, cells.ToArray()));
            chunks.Sort(static (left, right) => SortKey(left.Coordinate).CompareTo(SortKey(right.Coordinate)));
        }
        else if (cells.Count == 0)
        {
            chunks.RemoveAt(chunkIndex);
        }
        else
        {
            chunks[chunkIndex] = new TerrainChunkDelta(EditChunk, cells.ToArray());
        }

        changed = chunks.ToArray();
        return true;
    }

    private static (int X, int Y, int Z) SortKey(ChunkCoordinate coordinate) => (coordinate.X, coordinate.Y, coordinate.Z);

    private sealed class RouteGenerator : ISaveTerrainGenerator
    {
        public void Generate(SaveWorldRecipe recipe, ChunkCoordinate coordinate, Span<byte> cells)
        {
            if (recipe != Recipe)
                throw new ArgumentException("The saved recipe does not identify the walkable route generator.", nameof(recipe));

            const int size = ChunkSnapshot.Size;
            for (var z = 0; z < size; z++)
            {
                var worldZ = coordinate.Z * size + z;
                for (var x = 0; x < size; x++)
                {
                    var worldX = coordinate.X * size + x;
                    var marker = unchecked(recipe.Seed ^ ((uint)worldX * 73856093u) ^ ((uint)worldZ * 19349663u));
                    var height = worldX == 34 && worldZ is >= 7 and <= 9 ? 8 : worldZ >= 13 && marker % 17 == 0 ? 6 : 4;
                    for (var y = 0; y < size; y++)
                        cells[x + size * (y + size * z)] = y < height ? (byte)1 : (byte)0;
                }
            }
        }
    }
}
