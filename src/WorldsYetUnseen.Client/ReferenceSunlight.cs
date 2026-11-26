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
        var elevation = Vector3.Dot(sun, up);
        RenderSunlight sunlight;
        if (ReferenceDaylight.PreviewEnabled)
        {
            var light = ReferenceDaylight.Evaluate(elevation);
            sunlight = new(sun, light.Direct, light.Ambient);
        }
        else
        {
            var horizon = Math.Clamp(elevation / 0.12f, 0, 1);
            var direct = 0.65f * camera.Tint.X * horizon * horizon * (3 - 2 * horizon);
            var ambient = camera.Tint.X - direct;
            sunlight = orbitDemo
                ? new(sun, direct * new Vector3(1f, 0.75f, 0.50f), ambient * new Vector3(0.72f, 0.85f, 1f))
                : new(sun, new(direct), new(ambient));
        }
        return camera with
        {
            Tint = Vector4.One,
            Sunlight = sunlight,
            TerrainShadows = sunlight.Direct.X > 0,
        };
    }
}
