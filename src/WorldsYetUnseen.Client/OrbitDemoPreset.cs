using System.Numerics;
using Groundwork.Rendering;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static class OrbitDemoPreset
{
    internal const string DisplayName = "Orbit Demo";
    internal const ulong Seed = 42;
    internal const float Pitch = -0.12f;
    internal static bool BoulderEnabled => Environment.GetEnvironmentVariable("WYU_ORBIT_BOULDER") != "off";

    private static readonly Vector3 Sunset = Vector3.Normalize(new(0.68f, 0.18f, 0.70f));
    private static readonly Vector3 SolarTangent = Vector3.Normalize(Vector3.Cross(Sunset, Vector3.UnitY));

    internal static Vector3 SunDirection(ulong tick, double fraction)
    {
        var phase = ((double)(tick % 36_000) - ReferenceWeather.InitialTick + fraction) * (Math.Tau / 36_000);
        var (sin, cos) = Math.SinCos(phase);
        return (float)cos * Sunset + (float)sin * SolarTangent;
    }

    internal static RenderMesh LoadBoulder(int lod)
    {
        var limit = lod == 0 ? 2_500 : 500;
        var name = $"WorldsYetUnseen.Client.Assets.OrbitDemo.boulder_01_lod{lod}.bin";
        using var stream = typeof(OrbitDemoPreset).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Missing Orbit boulder mesh: {name}.");
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x42524f57 || reader.ReadUInt32() != 1)
            throw new InvalidDataException("Invalid Orbit boulder mesh header.");
        var vertices = reader.ReadInt32();
        var indices = reader.ReadInt32();
        if (indices < 3 || indices > 3 * limit || indices % 3 != 0 ||
            vertices < 3 || vertices > indices || stream.Length != 16L + 12L * vertices + 4L * indices)
            throw new InvalidDataException("Invalid Orbit boulder mesh size.");

        var positions = new Vector3[vertices];
        for (var i = 0; i < positions.Length; i++)
        {
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!float.IsFinite(position.LengthSquared()) || position.LengthSquared() > 1.35f * 1.35f)
                throw new InvalidDataException("Orbit boulder vertex exceeds the instancing bounds.");
            positions[i] = position;
        }
        var triangles = new uint[indices];
        for (var i = 0; i < triangles.Length; i++)
        {
            var index = reader.ReadUInt32();
            if (index >= (uint)vertices)
                throw new InvalidDataException("Invalid Orbit boulder mesh index.");
            triangles[i] = index;
        }
        return RenderMesh.Create(positions, triangles);
    }
}
