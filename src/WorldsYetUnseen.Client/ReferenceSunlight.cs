using System.Numerics;
using Groundwork.Rendering.D3D12;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static class ReferenceSunlight
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("WYU_WP15") != "off";

    internal static VolumeCamera Apply(VolumeCamera camera, ulong tick, double fraction)
    {
        var sun = camera.Sky?.SunDirection ?? ReferenceWeather.SunDirection(tick, fraction);
        var up = Vector3.Normalize(ProceduralGlobe.Float(camera.Position));
        var horizon = Math.Clamp(Vector3.Dot(sun, up) / 0.12f, 0, 1);
        var direct = 0.65f * camera.Tint.X * horizon * horizon * (3 - 2 * horizon);
        var ambient = camera.Tint.X - direct;
        return camera with
        {
            Tint = Vector4.One,
            Sunlight = new(sun, new(direct), new(ambient)),
            TerrainShadows = direct > 0,
        };
    }
}
