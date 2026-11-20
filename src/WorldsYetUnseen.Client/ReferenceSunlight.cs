using System.Numerics;
using Groundwork.Rendering.D3D12;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static class ReferenceSunlight
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("WYU_WP15") == "on";

    internal static VolumeCamera Apply(VolumeCamera camera, ulong tick, double fraction)
    {
        var sun = camera.Sky?.SunDirection ?? ReferenceWeather.SunDirection(tick, fraction);
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
