using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal static class PrototypePadMarker
{
    private const float Radius = 0.29f;
    private const uint LightFace = 0xFFFFFFFF;
    private const uint DarkFace = 0xFFC87520;
    private static readonly Vector3[] Rim = [Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitZ];

    internal static bool TryAppend(ChunkCoordinate coordinate, RenderMesh terrain, out RenderMesh mesh)
    {
        mesh = terrain;
        var center = PrototypeWorld.PadAnchor - coordinate.GetOrigin();
        if (center.X < 0 || center.X >= ChunkSnapshot.Size ||
            center.Y < 0 || center.Y >= ChunkSnapshot.Size ||
            center.Z < 0 || center.Z >= ChunkSnapshot.Size)
            return false;

        const int markerVertexCount = 24;
        var start = terrain.Positions.Length;
        var startIndex = terrain.Indices.Length;
        if (start > RenderMesh.MaximumVertexCount - markerVertexCount ||
            startIndex > RenderMesh.MaximumIndexCount - markerVertexCount)
            return false;

        var positions = new Vector3[start + markerVertexCount];
        var colors = new uint[positions.Length];
        var indices = new uint[startIndex + markerVertexCount];
        terrain.Positions.CopyTo(positions);
        terrain.Colors.CopyTo(colors);
        terrain.Indices.CopyTo(indices);
        for (var side = 0; side < Rim.Length; side++)
        {
            var first = Rim[side];
            var next = Rim[(side + 1) % Rim.Length];
            var upper = side % 2 == 0 ? LightFace : DarkFace;
            var lower = side % 2 == 0 ? DarkFace : LightFace;
            WriteTriangle(Vector3.UnitY, next, first, upper);
            WriteTriangle(-Vector3.UnitY, first, next, lower);
        }

        mesh = RenderMesh.Create(positions, indices, colors);
        return true;

        void WriteTriangle(Vector3 first, Vector3 second, Vector3 third, uint color)
        {
            positions[start] = center + first * Radius;
            positions[start + 1] = center + second * Radius;
            positions[start + 2] = center + third * Radius;
            colors.AsSpan(start, 3).Fill(color);
            for (var corner = 0; corner < 3; corner++)
                indices[startIndex++] = (uint)start++;
        }
    }
}
