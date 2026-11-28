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
    internal const string OrbitCombined = "orbit-combined";
    private readonly string _composition;
    private const string Profile = "wyu.far-horizon.atmosphere.v2";
    private readonly PropertyInfo _previewMode;
    private readonly PropertyInfo _lookupMode;
    private readonly RenderPassId _skyPass;
    private bool _nativeDirect;

    private R09TwilightPressure(Type device, string composition)
    {
        _composition = composition;
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
        var orbit = composition == OrbitCombined;
        var orbitFlags = Read("WYU_ORBIT_DEMO_SMOKE") == "1" && Read("WYU_ORBIT_PBR") == "on" &&
            Read("WYU_ORBIT_BOULDER") == "on" && Read("WYU_WP15") == "on" && Read("WYU_WP16_SMOKE") == "1";
        if (composition is not (FarHorizon or OrbitCombined) ||
            (orbit ? !orbitFlags || !R09IndependentTexturePressure.Requested : Read("WYU_ORBIT_DEMO_SMOKE") == "1" ||
                Read("WYU_WP15") == "on" || Read("WYU_WP16_SMOKE") == "1") ||
            !ReferenceDaylight.PreviewEnabled || ReferenceSky.Profile != Profile ||
            !ReferenceSky.Enabled || !BiomeMeasurements.UseBiomes || Read("WYU_PACKAGE_E") == "off" ||
            SkyMeasurements.Enabled || SkyHandoffDiagnostics.Enabled || Read("WYU_PACKAGE_G_SMOKE") == "1" ||
            Read("GROUNDWORK_SKY_LOOKUP_PROTOTYPE") == "1" || Read("GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES") == "1")
            throw new InvalidOperationException("R09 Twilight pressure requires an explicitly configured Direct preview flight.");
        return new(device, composition);
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
            $"WYU R09 TWILIGHT {path} {_composition} {Profile} {epoch} {mask} {gpu.DrawFence} {gpu.PresentFence} {sky.ResourceUses}"));
    }

    internal static bool TryWriteOrbitMaterialFrame(D3D12Renderer renderer, ulong sequence, bool near, bool propsVisible)
    {
        if (!renderer.TryReadResourceDiagnostics(out var gpu) || gpu.FrameSequence != sequence ||
            gpu.PresentedSequence != sequence || gpu.RetiredPresentSequence != sequence ||
            gpu.PresentFence <= gpu.DrawFence || gpu.CompletedFence < gpu.PresentFence)
            return false;

        // The native terrain plan counts color, depth, geometry, prepared material constants and six maps.
        // The two possible shadow inputs cannot make a numeric fallback reach this threshold.
        if (!TryReadOrbitTerrain(gpu, sequence, out var terrain))
            return false;
        var props = gpu.TryGetPass(ReadPass("Props"), out _) || gpu.TryGetPass(ReadPass("TransparentProps"), out _);
        if (propsVisible && !props)
            throw new InvalidOperationException("Orbit Combined did not bind the visible production props on its acknowledged frame.");
        var shadows = Convert.ToInt32(gpu.TryGetPass(ReadPass("NearShadow"), out _)) +
            Convert.ToInt32(gpu.TryGetPass(ReadPass("FarShadow"), out _));
        Console.WriteLine(FormattableString.Invariant(
            $"WYU R09 ORBITMATERIALS {(near ? "near" : "far")} {sequence} {gpu.DrawFence} {gpu.PresentFence} {terrain.ResourceUses} {shadows} {Convert.ToInt32(propsVisible && props)}"));
        return true;
    }

    internal static bool TryWriteBoulderDraw(in D3D12ResourceSnapshot gpu, ulong epoch, ulong sequence)
    {
        if (gpu.Custody != D3D12CustodyStatus.None || gpu.RetiredPresentSequence != sequence ||
            gpu.RetiredPresentFence != gpu.PresentFence || gpu.CompletedFence < gpu.PresentFence ||
            !TryReadOrbitTerrain(gpu, sequence, out var terrain))
            return false;

        // Shadow draws prove the corresponding maps were bound. Cached maps may also be bound.
        var observedShadowMaps = Convert.ToInt32(gpu.TryGetPass(ReadPass("NearShadow"), out _)) +
            Convert.ToInt32(gpu.TryGetPass(ReadPass("FarShadow"), out _));
        const int terrainUsesWithoutShadows = 4 + OrbitTerrainMaterials.MapCount;
        if (terrain.ResourceUses < terrainUsesWithoutShadows + observedShadowMaps ||
            terrain.ResourceUses > terrainUsesWithoutShadows + 2)
            return false;

        var lod = gpu.ObservedNearPropDraw.Draws > 0 ? 0 : 1;
        var draw = lod == 0 ? gpu.ObservedNearPropDraw : gpu.ObservedFarPropDraw;
        if (draw.Model != OrbitDemoPreset.BoulderModel || draw.Material != OrbitDemoPreset.BoulderMaterial ||
            draw.IndicesPerInstance == 0 || draw.Draws <= 0 || draw.Instances < draw.Draws)
            return false;

        var digest = OrbitDemoPreset.BoulderDigest(lod);
        Console.WriteLine(FormattableString.Invariant(
            $"WYU R09 BOULDER boulder_01 landed-stock-los {epoch} {sequence} {gpu.DrawFence} {gpu.PresentFence} {gpu.CompletedFence} {draw.Model} {draw.Material} {lod} {draw.IndicesPerInstance} {draw.Draws} {draw.Instances} {digest} {terrain.ResourceUses} {observedShadowMaps} maps-resident"));
        return true;
    }

    internal static bool TryReadOrbitTerrain(in D3D12ResourceSnapshot gpu, ulong sequence, out D3D12PassObservation terrain)
    {
        terrain = default;
        return gpu.FrameSequence == sequence && gpu.PresentedSequence == sequence && gpu.DrawFence != 0 &&
            gpu.PresentFence > gpu.DrawFence && gpu.TryGetPass(ReadPass("Terrain"), out terrain) &&
            terrain.ResourceUses >= 4 + OrbitTerrainMaterials.MapCount;
    }

    private static RenderPassId ReadPass(string name)
    {
        var type = typeof(D3D12Renderer).Assembly.GetType("Groundwork.Rendering.D3D12.GpuPassPlan");
        var field = type?.GetField(name, BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.NonPublic);
        return field?.GetValue(null) is RenderPassId { Value: > 0 } pass ? pass : throw BindingMismatch();
    }

    private static string? Read(string name) => Environment.GetEnvironmentVariable(name);

    private static InvalidOperationException BindingMismatch() =>
        new("The Groundwork renderer did not prove native Direct Twilight preview mode for R09.");
}
