using System.Reflection;
using Groundwork.Rendering.D3D12;
using Groundwork.Rendering.Passes;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

// Use the existing pressure fixture and its held mask. The independent sky A/B
// hooks and renderer timing probes are deliberately inactive during this route.
internal sealed class R09TwilightPressure
{
    private const string FarHorizon = "far-horizon";
    private const string Profile = "wyu.far-horizon.atmosphere.v2";
    private readonly PropertyInfo _previewMode;
    private readonly PropertyInfo _lookupMode;
    private readonly RenderPassId _skyPass;
    private bool _nativeDirect;

    private R09TwilightPressure(Type device)
    {
        _previewMode = ReadModeProperty("TwilightPreviewEnabledForTests");
        _lookupMode = ReadModeProperty("SkyLookupPrototypeEnabledForTests");
        var plan = device.Assembly.GetType("Groundwork.Rendering.D3D12.GpuPassPlan");
        var sky = plan?.GetField("Sky", BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.NonPublic);
        _skyPass = sky?.GetValue(null) is RenderPassId { Value: > 0 } pass ? pass : throw BindingMismatch();

        PropertyInfo ReadModeProperty(string name)
        {
            var property = device.GetProperty(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic);
            return property?.GetMethod?.ReturnType == typeof(bool) ? property : throw BindingMismatch();
        }
    }

    internal static R09TwilightPressure? Create(Type device)
    {
        var composition = Read("WYU_R09_TWILIGHT_PRESSURE");
        if (string.IsNullOrEmpty(composition))
            return null;
        if (composition != FarHorizon || !ReferenceDaylight.PreviewEnabled || ReferenceSky.Profile != Profile ||
            !ReferenceSky.Enabled || !BiomeMeasurements.UseBiomes || Read("WYU_PACKAGE_E") == "off" ||
            SkyMeasurements.Enabled || SkyHandoffDiagnostics.Enabled || Read("WYU_PACKAGE_G_SMOKE") == "1" ||
            Read("WYU_ORBIT_DEMO_SMOKE") == "1" || Read("WYU_WP15") == "on" || Read("WYU_WP16_SMOKE") == "1" ||
            Read("GROUNDWORK_SKY_LOOKUP_PROTOTYPE") == "1" || Read("GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES") == "1")
            throw new InvalidOperationException("R09 Twilight pressure requires the canonical Far Horizon Direct preview flight.");
        return new(device);
    }

    internal void VerifyDevice(object device)
    {
        if (_previewMode.GetValue(device) is not true || _lookupMode.GetValue(device) is not false)
            throw BindingMismatch();
        Volatile.Write(ref _nativeDirect, true);
    }

    internal void WriteHeldSky(VolumeWorkClass path, ulong epoch, ulong mask, in D3D12ResourceSnapshot gpu)
    {
        if (!Volatile.Read(ref _nativeDirect) || mask == 0 || gpu.FrameSequence != mask || gpu.PresentedSequence != mask ||
            gpu.DrawFence == 0 || gpu.PresentFence <= gpu.DrawFence || !gpu.TryGetPass(_skyPass, out var sky) ||
            sky is not { ResourceUses: 2, Barriers: >= 2 })
            throw new InvalidOperationException($"R09 {path} did not present the Direct preview sky on its held upload mask.");
        Console.WriteLine(FormattableString.Invariant(
            $"WYU R09 TWILIGHT {path} {FarHorizon} {Profile} {epoch} {mask} {gpu.DrawFence} {gpu.PresentFence} {sky.ResourceUses}"));
    }

    private static string? Read(string name) => Environment.GetEnvironmentVariable(name);

    private static InvalidOperationException BindingMismatch() =>
        new("The Groundwork renderer did not prove native Direct Twilight preview mode for R09.");
}
