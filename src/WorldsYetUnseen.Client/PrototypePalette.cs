using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static class PrototypePalette
{
    internal static RenderMesh CreateMesh(ChunkCoordinate coordinate, ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices, out DepositMask coloredDeposits)
    {
        coloredDeposits = DepositMask.None;
        if (positions.Length % 4 != 0)
            throw new InvalidOperationException("The prototype palette requires the voxel mesher's independent quads.");

        var colors = new uint[positions.Length];
        var origin = coordinate.GetOrigin();
        for (var vertex = 0; vertex < positions.Length; vertex += 4)
        {
            var corner = positions[vertex];
            var normal = Vector3.Cross(positions[vertex + 1] - corner, positions[vertex + 3] - corner);
            // The mesher writes outward-wound unit quads. A half-step inward from the face center
            // identifies the solid voxel that owns it, including negative and cross-chunk coordinates.
            var center = origin + (corner + positions[vertex + 2] - normal) * 0.5f;
            var cell = ((int)MathF.Floor(center.X), (int)MathF.Floor(center.Y), (int)MathF.Floor(center.Z));
            var color = ColorOf(cell, normal, ref coloredDeposits);
            colors.AsSpan(vertex, 4).Fill(color);
        }

        return RenderMesh.Create(positions, indices, colors);
    }

    private static uint ColorOf((int X, int Y, int Z) cell, Vector3 normal, ref DepositMask coloredDeposits)
    {
        foreach (var deposit in PrototypeWorld.Deposits)
        {
            if (cell == PrototypeWorld.DepositCell(deposit))
            {
                coloredDeposits |= PrototypeWorld.DepositBit(deposit);
                return Pack(255, 192, 65);
            }
        }

        if (cell == PrototypeWorld.FirstPadPost || cell == PrototypeWorld.SecondPadPost)
            return Pack(57, 213, 224);
        if (cell == PrototypeWorld.ModuleCell(BeaconModule.Foundation))
            return Pack(82, 145, 232);
        if (cell == PrototypeWorld.ModuleCell(BeaconModule.Mast))
            return Pack(132, 185, 249);
        if (cell == PrototypeWorld.ModuleCell(BeaconModule.SignalHead))
            return Pack(222, 145, 255);

        // Keep walkable tops and rock sides muted so that action targets carry the strongest contrast.
        return normal.Y > 0 ? Pack(104, 126, 97)
            : normal.Y < 0 ? Pack(60, 64, 60)
            : Pack(77, 91, 80);
    }

    private static uint Pack(byte red, byte green, byte blue) =>
        0xff000000u | ((uint)blue << 16) | ((uint)green << 8) | red;
}
