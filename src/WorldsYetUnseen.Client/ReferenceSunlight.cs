using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static class ReferenceSunlight
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("WYU_WP15");
    internal static bool Enabled => Mode == "on";
    internal static bool OrbitEnabled => Mode != "off";

    // The legacy globe has vertex colors but no normals. Approximate its tint using the local radial normal.
    internal static Vector4 GlobeTint(PlanetVector position, RenderSunlight sun)
    {
        var up = Vector3.Normalize(ProceduralGlobe.Float(position));
        var light = sun.Ambient + Math.Max(0, Vector3.Dot(up, sun.Direction)) * sun.Direct;
        return new(light, 1);
    }

    internal static VolumeCamera Apply(VolumeCamera camera, ulong tick, double fraction, bool orbitDemo = false)
    {
        var sun = camera.Sky?.SunDirection ??
            (orbitDemo ? OrbitDemoPreset.SunDirection(tick, fraction) : ReferenceWeather.SunDirection(tick, fraction));
        var up = Vector3.Normalize(ProceduralGlobe.Float(camera.Position));
        var light = ReferenceDaylight.Evaluate(Vector3.Dot(sun, up));
        return camera with
        {
            Tint = Vector4.One,
            Sunlight = new(sun, light.Direct, light.Ambient),
            TerrainShadows = light.Direct.X > 0,
        };
    }
}
