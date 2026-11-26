using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static class ReferenceSky
{
    internal static string Profile => ReferenceDaylight.PreviewEnabled
        ? "wyu.far-horizon.atmosphere.v2" : "wyu.far-horizon.atmosphere.v1";
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("WYU_PACKAGE_G") != "off";

    internal static RenderSky Create(PlanetVector eye, Vector3 forward, Vector3 up, float fov,
        ulong seed, ulong tick, double fraction, float haze, Vector3? sunDirection = null)
    {
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        var aerosol = ReferenceDaylight.PreviewEnabled ? 1 + 0.35f * Math.Clamp(haze, 0, 1) : 1;
        return new(ProceduralGlobe.Float(eye), Vector3.Normalize(forward),
            Vector3.Normalize(Vector3.Cross(right, forward)), fov,
            ReferencePlanet.RadiusMeters, ReferencePlanet.RadiusMeters + 112, 40, 14,
            new(0.0012f, 0.0026f, 0.005f), new(0.002f * aerosol),
            new(0.0014f, 0.003f, 0.006f), new(0.0024f * aerosol),
            sunDirection ?? ReferenceWeather.SunDirection(tick, fraction), 16, 0.009f,
            unchecked((uint)seed ^ (uint)(seed >> 32)));
    }
}
