using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal static class TerrainScene
{
    internal static RenderScene Camera { get; } = new(
        Matrix4x4.Identity, new Vector4(0.30f, 0.74f, 0.47f, 1),
        Matrix4x4.CreateLookAt(new Vector3(12, 25, -25), new Vector3(16, 2, 8), Vector3.UnitY),
        MathF.PI / 3, 0.1f, 100);

    internal static RenderMeshPair CreateMeshes()
    {
        var low = CreateChunk(0);
        var high = CreateChunk(1);
        var pair = ChunkMesher.BuildPair(low, high);
        if (!pair.Matches(low, high))
            throw new InvalidOperationException("The terrain mesh pair does not match the captured chunks.");

        return new RenderMeshPair(ToInstance(pair.Low), ToInstance(pair.High));
    }

    private static ChunkSnapshot CreateChunk(int chunkX)
    {
        const int size = ChunkSnapshot.Size;
        Span<byte> cells = stackalloc byte[ChunkSnapshot.CellCount];
        for (var z = 0; z < size; z++)
        {
            var height = chunkX == 1 && z >= 8 ? 8 : 4;
            for (var y = 0; y < size; y++)
                cells.Slice(size * (y + size * z), size).Fill(y < height ? (byte)1 : (byte)0);
        }

        return ChunkSnapshot.Create(new ChunkCoordinate(chunkX, 0, 0), 1, cells);
    }

    private static RenderMeshInstance ToInstance(ChunkMesh chunk) =>
        new(RenderMesh.Create(chunk.Positions, chunk.Indices), chunk.Coordinate.GetOrigin());
}
