using System.Numerics;
using System.Runtime.CompilerServices;
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
    ResourceQuota quota, bool measure) : IDisposable
{
    private readonly ProcessResourceBudget _budget = new(new ProcessResourceLimits(1, quota));
    private RuntimeSafetyAuthority? _runtime;
    private readonly D3D12DeviceSession _device = new(new(!measure, true), warp, reuseDirectQueue: true);
    internal D3D12Renderer? Current { get; private set; }
    private VolumeTerrainColorEvaluator? _colorEvaluator;
    internal ReferenceBiomes? Biomes { get; private set; }

    internal void SetBiomes(ReferenceBiomes biomes)
    {
        Biomes = biomes;
        _colorEvaluator = [MethodImpl(MethodImplOptions.AggressiveOptimization)] (position, material) => biomes.Color(position, material);
        RuntimeHelpers.PrepareDelegate(_colorEvaluator);
        _ = _colorEvaluator(default, (ushort)ReferenceTerrainMaterial.Basalt);
    }
    internal D3D12Renderer Active => Current ?? throw new InvalidOperationException("No active planet renderer.");

    internal Task NearAsync(VolumeStreamingCoordinator terrain, CancellationToken token) =>
        StartAsync(owner => D3D12Renderer.CreateWithSessionVolumeTerrain(window, owner, terrain,
            terrain.View.RendererEpoch, _device, _colorEvaluator, timing.Shutdown, timing.Shutdown), token);

    internal Task FarAsync(RenderMeshPair globe, CancellationToken token) =>
        StartAsync(owner => D3D12Renderer.CreateWithSessionMeshes(window, owner, globe, _device,
            timing.Shutdown, timing.Shutdown), token);

    public void Dispose() => _device.Dispose();

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
            DesktopSmoke.VerifyAdapterSettlement(renderer, requireDebugLayer: !measure, requireNvidia: !warp,
                useWarp: warp, allowRetainedSessionQueue: true);
    }
}

internal static class ProceduralGlobe
{
    private const int Level = 2;
    private const int TilesPerAxis = 1 << Level;
    private static readonly Vector4 Space = new(0.01f, 0.018f, 0.04f, 1);

    private static readonly Lazy<SamplingPlan> Plan = new(CreatePlan);

    // Directions and topology depend only on the fixed globe LOD.
    internal static void Prepare() => _ = Plan.Value;

    internal static RenderMeshPair Build(ReferencePlanet planet, PlanetVector pad, ReferenceBiomes? biomes, CancellationToken token)
    {
        var plan = Plan.Value;
        var samples = new GlobeSample[plan.Directions.Length];
        const int batchSize = 512;
        var batches = (samples.Length + batchSize - 1) / batchSize;
        // Disjoint canonical ranges let the runtime rebalance slow batches without
        // changing output order or letting the globe monopolize the CPU.
        Parallel.For(0, batches, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token }, batch =>
        {
            var start = batch * batchSize;
            var end = Math.Min(start + batchSize, samples.Length);
            for (var i = start; i < end; i++)
                samples[i] = Sample(planet, plan.Directions[i], biomes);
        });

        token.ThrowIfCancellationRequested();
        return new(new(BuildHalf(plan.First, samples, pad, false), Vector3.Zero),
            new(BuildHalf(plan.Second, samples, pad, true), Vector3.Zero));
    }

    private static GlobeSample Sample(ReferencePlanet planet, PlanetVector direction, ReferenceBiomes? biomes)
    {
        var descriptor = planet.Descriptor;
        var radius = descriptor.RadiusMeters;
        var height = 0d;
        var position = Scale(direction, radius);
        var vertex = descriptor.SampleAt(position).Vertex;
        for (var pass = 0; pass < 3; pass++)
        {
            var next = planet.Field.SampleLandform(vertex).Elevation / (double)PlanetDensityField.UnitsPerCell;
            if (next == height)
                break;
            height = next;
            position = Scale(direction, radius + height);
            vertex = descriptor.SampleAt(position).Vertex;
        }

        var material = planet.Field.SampleMaterial(vertex);
        var color = biomes?.Color(position, material) ??
            (material == (ushort)ReferenceTerrainMaterial.Basalt ? 0xff6f806au : 0xff76a3c4u);
        return new(Float(position), color);
    }

    private static RenderMesh BuildHalf(HalfPlan half, GlobeSample[] samples, PlanetVector pad, bool marker)
    {
        var count = half.SampleIndices.Length;
        var vertices = new Vector3[count + (marker ? 4 : 0)];
        var colors = new uint[vertices.Length];
        for (var i = 0; i < count; i++)
        {
            var sample = samples[half.SampleIndices[i]];
            vertices[i] = sample.Position;
            colors[i] = sample.Color;
        }

        if (!marker)
            return RenderMesh.Create(vertices, half.Indices, colors);

        var indices = new uint[half.Indices.Length + 6];
        half.Indices.CopyTo(indices, 0);
        AddMarker(vertices.AsSpan(count), indices.AsSpan(half.Indices.Length), colors.AsSpan(count), pad, (uint)count);
        return RenderMesh.Create(vertices, indices, colors);
    }

    private static SamplingPlan CreatePlan()
    {
        const int intervals = PlanetCoordinates.IntervalsPerPatch;
        const int stride = intervals + 1;
        const int intervalsPerFace = intervals * TilesPerAxis;
        const int sampleCount = 6 * intervalsPerFace * intervalsPerFace + 2;
        var faces = Enum.GetValues<PlanetFace>();
        var directions = new List<PlanetVector>(sampleCount);
        var canonical = new Dictionary<PlanetSampleKey, int>(sampleCount);
        var first = MapHalf(faces.AsSpan(0, 3));
        var second = MapHalf(faces.AsSpan(3));
        return new(directions.ToArray(), first, second);

        HalfPlan MapHalf(ReadOnlySpan<PlanetFace> charts)
        {
            var patchCount = charts.Length * TilesPerAxis * TilesPerAxis;
            var sampleIndices = new int[patchCount * stride * stride];
            var indices = new uint[patchCount * intervals * intervals * 6];
            var vertexOffset = 0;
            var indexOffset = 0;
            foreach (var face in charts)
            for (var y = 0; y < TilesPerAxis; y++)
            for (var x = 0; x < TilesPerAxis; x++)
            {
                var patch = PlanetPatchMesh.Build(new(face, Level, x, y), 1, static _ => 0);
                for (var i = 0; i < patch.Samples.Length; i++)
                {
                    var key = patch.Samples[i];
                    if (!canonical.TryGetValue(key, out var sampleIndex))
                    {
                        sampleIndex = directions.Count;
                        canonical.Add(key, sampleIndex);
                        directions.Add(patch.Positions[i]);
                    }
                    sampleIndices[vertexOffset + i] = sampleIndex;
                }
                foreach (var index in patch.Indices)
                    indices[indexOffset++] = (uint)vertexOffset + index;
                vertexOffset += patch.Samples.Length;
            }
            return new(sampleIndices, indices);
        }
    }

    private sealed record SamplingPlan(PlanetVector[] Directions, HalfPlan First, HalfPlan Second);
    private sealed record HalfPlan(int[] SampleIndices, uint[] Indices);
    private readonly record struct GlobeSample(Vector3 Position, uint Color);

    private static void AddMarker(Span<Vector3> vertices, Span<uint> indices, Span<uint> colors, PlanetVector pad, uint offset)
    {
        var up = Vector3.Normalize(Float(pad));
        var axis = MathF.Abs(up.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        var east = Vector3.Normalize(Vector3.Cross(up, axis));
        var north = Vector3.Cross(up, east);
        var center = Float(pad) + up * 8;
        vertices[0] = center + east * 8;
        vertices[1] = center + north * 8;
        vertices[2] = center - east * 8;
        vertices[3] = center - north * 8;
        ReadOnlySpan<uint> order = [0, 1, 2, 0, 2, 3];
        for (var i = 0; i < order.Length; i++)
            indices[i] = offset + order[i];
        colors.Fill(0xff19eaffu);
    }

    internal const float FieldOfView = 1.05f;

    internal static (Vector3 Forward, Vector3 Up) ViewBasis(PlanetVector position, PlanetVector heading)
    {
        var up = Vector3.Normalize(Float(position));
        var tangent = Float(heading) - up * Vector3.Dot(Float(heading), up);
        if (tangent.LengthSquared() < 0.01f)
            tangent = Vector3.Cross(up, MathF.Abs(up.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
        return (Vector3.Normalize(-up + Vector3.Normalize(tangent) * 1.5f), up);
    }

    internal static RenderFrame Frame(ulong sequence, VolumeCamera camera) =>
        new(sequence, camera.Sky is not null || camera.Fog is not null ? camera.ClearColor : Space,
            new RenderScene(Matrix4x4.Identity, camera.Tint,
                Matrix4x4.CreateLookTo(Float(camera.Position), camera.Forward, camera.Up), FieldOfView, 1f, 10_000f))
        { Sky = camera.Sky };

    internal static Vector3 Float(PlanetVector vector) => new((float)vector.X, (float)vector.Y, (float)vector.Z);
}
