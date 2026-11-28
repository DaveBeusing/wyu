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
    ResourceQuota quota, bool measure, bool qualifyR09 = false) : IDisposable
{
    private readonly ProcessResourceBudget _budget = new(new ProcessResourceLimits(1, quota));
    private RuntimeSafetyAuthority? _runtime;
    private D3D12DeviceSession? _device;
    private D3D12NativePresentTrace? _nativePresentTrace = SkyHandoffDiagnostics.Enabled &&
        Environment.GetEnvironmentVariable("WYU_NATIVE_PRESENT_TRACE") == "1"
        ? new D3D12NativePresentTrace()
        : null;
    private D3D12DeviceSession Device => _device ??= new(new D3D12DiagnosticsOptions(!measure || TexturePressure is not null, true)
    {
        EnableResourceSnapshots = qualifyR09 || SkyMeasurements.Enabled,
        ObservedPropModel = qualifyR09 && R09IndependentTexturePressure.OrbitRequested ? OrbitDemoPreset.BoulderModel : 0,
        ObservedPropMaterial = OrbitDemoPreset.BoulderMaterial,
        TextureCopyQualification = TexturePressure?.Qualification,
        NativePresentTrace = _nativePresentTrace,
    }, warp, reuseDirectQueue: true);
    internal D3D12Renderer? Current { get; private set; }
    internal Wp11WarpReadback? WarpReadback { get; init; }
    internal R09NativeUploadGate? R09UploadGate { get; private set; }
    internal R09IndependentTexturePressure? TexturePressure { get; } = qualifyR09 && R09IndependentTexturePressure.Requested
        ? new() : null;
    private VolumeTerrainColorEvaluator? _colorEvaluator;
    private D3D12TextureHandoff? _materialStream;
    private D3D12TextureView? _materialCut;
    private bool _materialRequestsPending;
    private bool _orbitalMaterialCut;
    internal ReferenceProps? Props { get; set; }
    internal ConstructionVisuals? ConstructionVisuals { get; init; }
    internal ReferenceBiomes? Biomes { get; private set; }
    internal OrbitTerrainMaterials? OrbitMaterials { private get; set; }

    internal bool BuildsOrbitalMaterials => OrbitMaterials is not null;

    internal bool MaterialsResident(D3D12Renderer renderer, VolumeViewIdentity? nearView) =>
        ReferenceEquals(renderer, Current) && OrbitMaterials is { } materials &&
        _materialStream is { } stream && _materialCut is { } cut &&
        (!nearView.HasValue || nearView.Value == cut.View) && materials.IsResident(stream, cut, _orbitalMaterialCut);

    internal void SetBiomes(ReferenceBiomes biomes)
    {
        Biomes = biomes;
        _colorEvaluator = [MethodImpl(MethodImplOptions.AggressiveOptimization)] (position, material) => biomes.Color(position, material);
        if (WarpReadback is { } readback)
            readback.ColorEvaluator = _colorEvaluator;
        RuntimeHelpers.PrepareDelegate(_colorEvaluator);
        _ = _colorEvaluator(default, (ushort)ReferenceTerrainMaterial.Basalt);
    }
    internal D3D12Renderer Active => Current ?? throw new InvalidOperationException("No active planet renderer.");

    internal Task NearAsync(VolumeStreamingCoordinator terrain, CancellationToken token, Action? onStarted = null) =>
        StartAsync(owner =>
        {
            D3D12Renderer renderer;
            if (OrbitMaterials is { } materials)
            {
                renderer = D3D12Renderer.CreateWithTexturedSessionVolumeTerrain(window, owner, terrain,
                    terrain.View.RendererEpoch, Device, _colorEvaluator, timing.Shutdown, timing.Shutdown);
                var stream = materials.CreateStream(TexturePressure is not null);
                renderer.AttachTextureHandoff(stream);
                _materialStream = stream;
                TexturePressure?.AttachOrbit(renderer, stream, materials);
            }
            else
            {
                renderer = D3D12Renderer.CreateWithSessionVolumeTerrain(window, owner, terrain,
                    terrain.View.RendererEpoch, Device, _colorEvaluator, timing.Shutdown, timing.Shutdown);
            }
            if (qualifyR09)
                R09UploadGate = R09NativeUploadGate.Attach(renderer);
            if (TexturePressure is { } pressure)
            {
                if (R09UploadGate?.Twilight is null)
                    throw new InvalidOperationException("Independent R09 texture pressure requires Direct Twilight.");
                if (OrbitMaterials is null)
                    pressure.Attach(renderer);
            }
            if (ConstructionVisuals is { } buildings)
                buildings.Attach(renderer, Props);
            else
                Props?.Attach(renderer);
            return renderer;
        }, token, onStarted);

    internal void PublishSurfaceMaterials(D3D12Renderer renderer, Guid world, in VolumePublicationInfo publication, bool presented)
    {
        if (OrbitMaterials is not { } materials || _materialStream is not { } stream ||
            !ReferenceEquals(renderer, Current) || (!presented && !_materialCut.HasValue))
            return;

        var view = new D3D12TextureView(world, publication.View, publication.TerrainRevision, publication.Generation);
        if (_materialCut == view && !_materialRequestsPending)
            return;
        QueueMaterialCut(materials, stream, view);
    }

    // A superseded copy can temporarily occupy a slot. Retry at a bounded cadence
    // without gating a protected terrain frame on optional upload admission.
    internal void RetryMaterials(D3D12Renderer renderer, ulong sequence)
    {
        if (_materialRequestsPending && sequence % OrbitTerrainMaterials.RetryFrameInterval == 0 && ReferenceEquals(renderer, Current) &&
            OrbitMaterials is { } materials && _materialStream is { } stream && _materialCut is { } cut)
            QueueMaterialCut(materials, stream, cut);
    }

    private void QueueMaterialCut(OrbitTerrainMaterials materials, D3D12TextureHandoff stream, D3D12TextureView cut)
    {
        _materialCut = cut;
        _materialRequestsPending = materials.Publish(stream, cut, _orbitalMaterialCut) == OrbitTerrainMaterials.Scheduling.Retry;
    }

    internal Task FarAsync(ProceduralGlobe.Geometry globe, Guid world, VolumePublicationInfo near, CancellationToken token) =>
        StartAsync(owner =>
        {
            if (TexturePressure is { } pressure && OrbitMaterials is null)
            {
                if (globe.Textured)
                    throw new InvalidOperationException("R09 Far Horizon copy pressure requires its original untextured geometry.");
                var farView = new VolumeViewIdentity(checked(near.View.RendererEpoch + 1), 1, PlanetLocalFrame.Create(default));
                return pressure.AttachFar(window, owner, globe.Meshes, Device,
                    new(world, farView, near.TerrainRevision, 1), timing.Shutdown);
            }
            if (!globe.Textured || OrbitMaterials is not { } materials)
            {
                if (TexturePressure is not null)
                    throw new InvalidOperationException("R09 Orbit Combined requires the textured production globe.");
                return D3D12Renderer.CreateWithSessionMeshes(window, owner, globe.Meshes, Device,
                    timing.Shutdown, timing.Shutdown);
            }

            var view = new VolumeViewIdentity(checked(near.View.RendererEpoch + 1), 1, PlanetLocalFrame.Create(default));
            var cut = new D3D12TextureView(world, view, near.TerrainRevision, 1);
            var stream = materials.CreateStream(TexturePressure is not null);
            _materialStream = stream;
            _orbitalMaterialCut = true;
            QueueMaterialCut(materials, stream, cut);
            var region = new RenderMeshRegion([globe.Meshes.First, globe.Meshes.Second]);
            var renderer = D3D12Renderer.CreateWithSessionTexturedMeshes(window, owner, region, Device, stream, cut,
                timing.Shutdown, timing.Shutdown);
            TexturePressure?.AttachOrbit(renderer, stream, materials, cut);
            return renderer;
        }, token);

    public void Dispose()
    {
        _device?.Dispose();
        var trace = Interlocked.Exchange(ref _nativePresentTrace, null);
        if (trace is null)
            return;

        var records = trace.Read(out var dropped);
        SkyMeasurements.Write(new { kind = "native-present-inventory",
            frequency = D3D12NativePresentTrace.QpcFrequency, retained = records.Length, dropped });
        foreach (var record in records)
            SkyMeasurements.Write(new { kind = "native-present", present = record });
    }

    private async Task StartAsync(Func<IRuntimeOwnership, D3D12Renderer> create, CancellationToken token,
        Action? onStarted = null)
    {
        SkyHandoffDiagnostics.Mark("owner-start-entry");
        if (_runtime is not null || Current is not null)
            throw new InvalidOperationException("The previous graphics owner must settle before handoff.");
        using var reservation = _budget.TryReserveRuntime(quota)
            ?? throw new InvalidOperationException("Graphics runtime admission failed.");
        var clock = TimeProvider.System;
        _runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
        SkyHandoffDiagnostics.Mark("owner-create-begin");
        Current = create(_runtime.RuntimeOwnership);
        SkyHandoffDiagnostics.Mark("owner-create-end");
        SkyMeasurements.Attach(Current);
        WarpReadback?.Attach(Current);
        var context = new EngineModuleContext(clock, timing, _runtime.Reporter, _runtime.StopControl);
        SkyHandoffDiagnostics.Mark("renderer-initialize-begin");
        await Current.InitializeAsync(context, token).ConfigureAwait(false);
        SkyHandoffDiagnostics.Mark("renderer-initialize-end");
        SkyHandoffDiagnostics.Mark("renderer-start-begin");
        await Current.StartAsync(token).ConfigureAwait(false);
        SkyHandoffDiagnostics.Mark("renderer-start-end");
        onStarted?.Invoke();
    }

    internal async Task StopAsync(bool qualifyHandoff = false)
    {
        if (_runtime is not { } runtime)
            return;
        SkyHandoffDiagnostics.Mark("previous-stop-begin");
        if (Props is { } props)
            await props.StopAsync().ConfigureAwait(false);
        SkyHandoffDiagnostics.Mark("previous-props-stopped");
        var renderer = Current;
        var deadline = runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
        SkyHandoffDiagnostics.Mark("previous-renderer-dispose-begin");
        if (renderer is not null)
            await renderer.DisposeAsync().ConfigureAwait(false);
        SkyHandoffDiagnostics.Mark("previous-renderer-disposed");
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
        SkyHandoffDiagnostics.Mark("previous-runtime-settled");
        if (renderer is not null)
        {
            SkyMeasurements.Flush(renderer);
            TexturePressure?.Detach(renderer);
        }
        _runtime = null;
        Current = null;
        R09UploadGate = null;
        _materialStream = null;
        _materialCut = null;
        _materialRequestsPending = false;
        _orbitalMaterialCut = false;
        if (qualifyHandoff && renderer is not null)
            DesktopSmoke.VerifyAdapterSettlement(renderer, requireDebugLayer: !measure, requireNvidia: !warp,
                useWarp: warp, allowRetainedSessionQueue: true);
        SkyHandoffDiagnostics.Mark("previous-stop-end");
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

    internal sealed record Geometry(RenderMeshPair Meshes, bool Textured);

    internal static Geometry Build(ReferencePlanet planet, PlanetVector pad, ReferenceBiomes? biomes, CancellationToken token,
        bool textured = false) =>
        Build(planet.Descriptor, planet.Field.SampleLandform, planet.Field.SampleMaterial, pad, biomes, token, textured);

    internal static Geometry Build(PlanetDescriptor descriptor, Func<LatticeCoordinate, PlanetLandform> landform,
        Func<LatticeCoordinate, ushort> material, PlanetVector pad, ReferenceBiomes? biomes, CancellationToken token,
        bool textured = false)
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
                samples[i] = Sample(descriptor, landform, material, plan.Directions[i], biomes);
        });

        token.ThrowIfCancellationRequested();
        var first = BuildHalf(plan.First, samples, pad, marker: false, textured, out var texturedFirst);
        var second = BuildHalf(plan.Second, samples, pad, marker: true, textured, out var texturedSecond);
        return texturedFirst is not null && texturedSecond is not null
            ? new(new(new(texturedFirst, Vector3.Zero), new(texturedSecond, Vector3.Zero)), true)
            : new(new(new(first, Vector3.Zero), new(second, Vector3.Zero)), false);
    }

    internal static GlobeSample Sample(PlanetDescriptor descriptor, Func<LatticeCoordinate, PlanetLandform> landform,
        Func<LatticeCoordinate, ushort> sampleMaterial, PlanetVector direction, ReferenceBiomes? biomes)
    {
        var radius = descriptor.RadiusMeters;
        var height = 0d;
        var position = Scale(direction, radius);
        var vertex = descriptor.SampleAt(position).Vertex;
        for (var pass = 0; pass < 3; pass++)
        {
            var next = landform(vertex).Elevation / (double)PlanetDensityField.UnitsPerCell;
            if (next == height)
                break;
            height = next;
            position = Scale(direction, radius + height);
            vertex = descriptor.SampleAt(position).Vertex;
        }

        var material = sampleMaterial(vertex);
        var color = biomes?.Color(position, material) ??
            (material == (ushort)ReferenceTerrainMaterial.Basalt ? 0xff6f806au : 0xff76a3c4u);
        return new(position, material, color);
    }

    private static RenderMesh BuildHalf(HalfPlan half, GlobeSample[] samples, PlanetVector pad, bool marker,
        bool textured, out RenderMesh? mapped)
    {
        var count = half.SampleIndices.Length;
        var vertices = new Vector3[count + (marker ? 4 : 0)];
        var colors = new uint[vertices.Length];
        var canonical = textured ? new PlanetVector[vertices.Length] : [];
        var materials = textured ? new ushort[vertices.Length] : [];
        for (var i = 0; i < count; i++)
        {
            var sample = samples[half.SampleIndices[i]];
            vertices[i] = Float(sample.Position);
            colors[i] = sample.Color;
            if (textured)
            {
                canonical[i] = sample.Position;
                materials[i] = sample.Material;
            }
        }

        var indices = half.Indices;
        if (marker)
        {
            indices = new uint[half.Indices.Length + 6];
            half.Indices.CopyTo(indices, 0);
            AddMarker(vertices.AsSpan(count), indices.AsSpan(half.Indices.Length), colors.AsSpan(count), pad, (uint)count);
            for (var i = count; textured && i < vertices.Length; i++)
            {
                var point = vertices[i];
                canonical[i] = new(point.X, point.Y, point.Z);
                materials[i] = OrbitTerrainMaterials.OrbitalMarkerMaterial;
            }
        }

        var mesh = RenderMesh.Create(vertices, indices, colors);
        mapped = null;
        if (textured)
            D3D12Renderer.TryBuildOrbitalTerrainMesh(mesh, canonical, materials, out mapped);
        return mesh;
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
    internal readonly record struct GlobeSample(PlanetVector Position, ushort Material, uint Color);

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

    internal static Vector3 MarkerAimPoint(RenderMeshPair globe)
    {
        var markerMesh = globe.Second;
        var positions = markerMesh.Mesh.Positions;
        var triangle = markerMesh.Mesh.Indices[^6..^3];
        // Aim inside one triangle to avoid rasterization ties along the diamond's diagonal.
        return (positions[(int)triangle[0]] + positions[(int)triangle[1]] + positions[(int)triangle[2]]) / 3 +
            markerMesh.Translation;
    }

    internal const float FieldOfView = 1.05f;

    internal static (Vector3 Forward, Vector3 Up) ViewBasis(PlanetVector position, PlanetVector heading, float tilt = 1.5f)
    {
        var up = Vector3.Normalize(Float(position));
        var tangent = Float(heading) - up * Vector3.Dot(Float(heading), up);
        if (tangent.LengthSquared() < 0.01f)
            tangent = Vector3.Cross(up, MathF.Abs(up.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
        return (Vector3.Normalize(-up + Vector3.Normalize(tangent) * tilt), up);
    }

    internal static RenderFrame Frame(ulong sequence, VolumeCamera camera) =>
        new(sequence, camera.Sky is not null || camera.Fog is not null ? camera.ClearColor : Space,
            new RenderScene(Matrix4x4.Identity, camera.Tint,
                Matrix4x4.CreateLookTo(Float(camera.Position), camera.Forward, camera.Up), FieldOfView, 1f, 10_000f)
            { Sunlight = camera.Sunlight })
        { Sky = camera.Sky };

    internal static RenderFrame LegacyFrame(ulong sequence, PlanetVector position, PlanetVector heading, ReferenceAtmosphere? atmosphere)
    {
        var (forward, up) = ViewBasis(position, heading, 0.65f);
        return new(sequence, atmosphere?.Sky ?? Space, new RenderScene(Matrix4x4.Identity, atmosphere?.Tint ?? Vector4.One,
            Matrix4x4.CreateLookTo(Float(position), forward, up), FieldOfView, 1f, 10_000f));
    }

    internal static Vector3 Float(PlanetVector vector) => new((float)vector.X, (float)vector.Y, (float)vector.Z);
}
