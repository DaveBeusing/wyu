using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed class TerrainScene
{
    private const int BoundaryX = ChunkSnapshot.Size - 1;
    private const int BoundaryY = 3;
    private const int BoundaryZ = 4;
    private static readonly Vector3 CameraPosition = new(15.5f, 11, 4);
    private static readonly Vector3 CameraTarget = new(15.5f, 4, 4.5f);
    private ChunkSnapshot _low = CreateChunk(0);
    private readonly ChunkSnapshot _high = CreateChunk(1);

    // Clip below the seam's top surface so the notch stays visible with a flat tint.
    internal static RenderScene Camera { get; } = new(
        Matrix4x4.Identity, new Vector4(0.30f, 0.74f, 0.47f, 1),
        Matrix4x4.CreateLookAt(CameraPosition, CameraTarget, Vector3.UnitY),
        MathF.PI / 3, 0.1f, 7.55f);

    internal RenderMeshPair CreateMeshes() => ToRenderPair(ChunkMesher.BuildPair(_low, _high));

    internal (ChunkSnapshot Low, ChunkSnapshot High) ToggleBoundary()
    {
        var revision = checked(_low.Revision + 1);
        const int size = ChunkSnapshot.Size;
        Span<byte> cells = stackalloc byte[ChunkSnapshot.CellCount];
        for (var z = 0; z < size; z++)
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    cells[Index(x, y, z)] = _low[x, y, z];

        var boundary = Index(BoundaryX, BoundaryY, BoundaryZ);
        cells[boundary] = cells[boundary] == 0 ? (byte)1 : (byte)0;
        _low = ChunkSnapshot.Create(_low.Coordinate, revision, cells);
        return (_low, _high);
    }

    internal bool Matches(ChunkMeshPair pair) => pair.Matches(_low, _high);

    internal static RenderMeshPair ToRenderPair(ChunkMeshPair pair) => new(ToInstance(pair.Low), ToInstance(pair.High));

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

    private static int Index(int x, int y, int z) => x + ChunkSnapshot.Size * (y + ChunkSnapshot.Size * z);

    private static RenderMeshInstance ToInstance(ChunkMesh chunk) =>
        new(RenderMesh.Create(chunk.Positions, chunk.Indices), chunk.Coordinate.GetOrigin());
}
