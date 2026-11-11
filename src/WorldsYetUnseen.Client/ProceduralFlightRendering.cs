using System.Numerics;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

// A renderer Stop fences its owning runtime. Each successive graphics owner therefore
// has its own bounded lifetime while the world and the HWND keep their original authority.
internal sealed class ProceduralRenderHost(WindowsHostModule window, bool warp, RuntimeTimingLimits timing,
    ResourceQuota quota)
{
    private readonly ProcessResourceBudget _budget = new(new ProcessResourceLimits(1, quota));
    private RuntimeSafetyAuthority? _runtime;
    internal D3D12Renderer? Current { get; private set; }
    internal D3D12Renderer Active => Current ?? throw new InvalidOperationException("No active planet renderer.");

    internal Task NearAsync(VolumeStreamingCoordinator terrain, CancellationToken token) =>
        StartAsync(owner => D3D12Renderer.CreateWithVolumeTerrain(window, owner, terrain, terrain.View.RendererEpoch,
            new(true, true), warp, timing.Shutdown, timing.Shutdown), token);

    internal Task FarAsync(RenderMeshPair globe, CancellationToken token) =>
        StartAsync(owner => D3D12Renderer.CreateWithMeshes(window, owner, globe, new(true, true), warp,
            timing.Shutdown, timing.Shutdown), token);

    private async Task StartAsync(Func<IRuntimeOwnership, D3D12Renderer> create, CancellationToken token)
    {
        if (_runtime is not null || Current is not null)
            throw new InvalidOperationException("The previous graphics owner must settle before handoff.");
        using var reservation = _budget.TryReserveRuntime(quota)
            ?? throw new InvalidOperationException("Graphics runtime admission failed.");
        var clock = TimeProvider.System;
        _runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
        Current = create(_runtime.RuntimeOwnership);
        var context = new EngineModuleContext(clock, timing, _runtime.Reporter, _runtime.StopControl);
        await Current.InitializeAsync(context, token).ConfigureAwait(false);
        await Current.StartAsync(token).ConfigureAwait(false);
    }

    internal async Task StopAsync(bool qualifyHandoff = false)
    {
        if (_runtime is not { } runtime)
            return;
        var renderer = Current;
        var deadline = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        if (renderer is not null)
            await renderer.DisposeAsync().ConfigureAwait(false);
        var settlement = runtime.SettleStop();
        while (settlement == ShutdownSettlement.Pending &&
            deadline.GetRemainingForSettlement(TimeProvider.System) > TimeSpan.Zero)
        {
            await Task.Delay(5).ConfigureAwait(false);
            settlement = runtime.SettleStop();
        }
        if (settlement != ShutdownSettlement.Released ||
            renderer is not null && renderer.Read() is not { Lifecycle: RendererLifecycle.Stopped, FirstFault: null,
                FirstCleanupFault: null, IncompleteCleanup: false })
            throw new InvalidOperationException("The planet renderer did not settle for handoff.");
        _runtime = null;
        Current = null;
        if (qualifyHandoff && renderer is not null)
            DesktopSmoke.VerifyAdapterSettlement(renderer, requireDebugLayer: true, requireNvidia: !warp, useWarp: warp);
    }
}

internal static class ProceduralGlobe
{
    private const int Level = 2;
    private const int TilesPerAxis = 1 << Level;
    private static readonly Vector4 Space = new(0.01f, 0.018f, 0.04f, 1);

    internal static RenderMeshPair Build(ReferencePlanet planet, PlanetVector pad, CancellationToken token)
    {
        var faces = Enum.GetValues<PlanetFace>();
        var heights = new Dictionary<PlanetSampleKey, double>();
        return new(new(BuildHalf(faces.AsSpan(0, 3), false), Vector3.Zero),
            new(BuildHalf(faces.AsSpan(3), true), Vector3.Zero));

        RenderMesh BuildHalf(ReadOnlySpan<PlanetFace> charts, bool marker)
        {
            var vertices = new List<Vector3>();
            var indices = new List<uint>();
            var colors = new List<uint>();
            foreach (var face in charts)
            for (var y = 0; y < TilesPerAxis; y++)
            for (var x = 0; x < TilesPerAxis; x++)
            {
                token.ThrowIfCancellationRequested();
                var patch = PlanetPatchMesh.Build(new(face, Level, x, y), planet.Descriptor.RadiusMeters, Height);
                var offset = (uint)vertices.Count;
                foreach (var position in patch.Positions)
                {
                    vertices.Add(Float(position));
                    var sample = planet.Descriptor.SampleAt(position).Vertex;
                    var material = planet.Field.SampleMaterial(sample);
                    colors.Add(material == (ushort)ReferenceTerrainMaterial.Basalt ? 0xff6f806au : 0xff76a3c4u);
                }
                foreach (var index in patch.Indices)
                    indices.Add(offset + index);
            }
            if (marker)
                AddMarker(vertices, indices, colors, pad);
            return RenderMesh.Create(vertices.ToArray(), indices.ToArray(), colors.ToArray());
        }

        double Height(PlanetSampleKey key)
        {
            if (heights.TryGetValue(key, out var height))
                return height;
            var radial = PlanetCoordinates.Direction(key);
            var radius = planet.Descriptor.RadiusMeters;
            for (var pass = 0; pass < 3; pass++)
            {
                var sample = planet.Descriptor.SampleAt(Scale(radial, radius + height));
                height = planet.Field.SampleLandform(sample.Vertex).Elevation / (double)PlanetDensityField.UnitsPerCell;
            }
            heights.Add(key, height);
            return height;
        }
    }

    private static void AddMarker(List<Vector3> vertices, List<uint> indices, List<uint> colors, PlanetVector pad)
    {
        var up = Vector3.Normalize(Float(pad));
        var axis = MathF.Abs(up.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        var east = Vector3.Normalize(Vector3.Cross(up, axis));
        var north = Vector3.Cross(up, east);
        var center = Float(pad) + up * 8;
        var offset = (uint)vertices.Count;
        vertices.AddRange([center + east * 8, center + north * 8, center - east * 8, center - north * 8]);
        indices.AddRange([offset, offset + 1, offset + 2, offset, offset + 2, offset + 3]);
        colors.AddRange([0xff19eaff, 0xff19eaff, 0xff19eaff, 0xff19eaff]);
    }

    internal static RenderFrame Frame(ulong sequence, PlanetVector position, PlanetVector heading)
    {
        var up = Vector3.Normalize(Float(position));
        var tangent = Float(heading) - up * Vector3.Dot(Float(heading), up);
        if (tangent.LengthSquared() < 0.01f)
            tangent = Vector3.Cross(up, MathF.Abs(up.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
        var forward = Vector3.Normalize(-up + Vector3.Normalize(tangent) * 0.65f);
        return new(sequence, Space, new RenderScene(Matrix4x4.Identity, Vector4.One,
            Matrix4x4.CreateLookTo(Float(position), forward, up), 1.05f, 1f, 10_000f));
    }

    internal static Vector3 Float(PlanetVector vector) => new((float)vector.X, (float)vector.Y, (float)vector.Z);
}
