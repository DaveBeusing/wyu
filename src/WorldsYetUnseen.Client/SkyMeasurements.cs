using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Core;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

// Opt-in qualification instrumentation. Its private engine binding fails closed
// when a package does not contain the exact owner-thread measurement hooks.
internal sealed partial class SkyMeasurements
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("WYU_PACKAGE_G_MEASURE");
    private static readonly ConditionalWeakTable<D3D12Renderer, SkyMeasurements> Owners = new();
    private static readonly Lazy<string> EngineHash = new(() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(D3D12Renderer).Assembly.Location))));
    private static int _nextOwner;
    private static Handoff? _pendingHandoff;
    private readonly D3D12Renderer _renderer;
    private Func<ulong, CancellationToken, ValueTask<RendererSnapshot>>? _presentationWait;
    private readonly int _owner = Interlocked.Increment(ref _nextOwner);
    private readonly Handoff? _handoff = _pendingHandoff;
    private MeasurementWindow? _window;
    private long _firstPresent;
    private static readonly bool GpuPassBreakdownEnabled =
        Environment.GetEnvironmentVariable("WYU_SKY_GPU_PASS_BREAKDOWN") == "1";
    private bool _lookupPrototype;
    private bool _lookupResourceReuse;

    private SkyMeasurements(D3D12Renderer renderer) => _renderer = renderer;

    internal static bool Enabled => Mode is "off" or "on";
    internal static bool Smoke => Environment.GetEnvironmentVariable("WYU_SKY_MEASURE_SMOKE") == "1";
    internal static bool CollectPhases => Enabled && Environment.GetEnvironmentVariable("WYU_SKY_MEASURE_PHASES") == "1";
    internal static int Warmup => Smoke ? 4 : 40;
    internal static int Samples => Smoke ? 8 : 400;
    internal static string Composition => Environment.GetEnvironmentVariable("WYU_SKY_MEASURE_COMPOSITION") ?? "far-horizon";
    internal static bool OrbitCombined => Composition == "orbit-combined";

    internal static VolumeCamera Submit(VolumeCamera camera) => Enabled && Mode == "off" ? camera with { Sky = null } : camera;
    internal static RenderFrame Submit(RenderFrame frame) => Enabled && Mode == "off" ? frame with { Sky = null } : frame;

    internal static void Attach(D3D12Renderer renderer)
    {
        if (!Enabled)
            return;
        if (Composition is not ("far-horizon" or "orbit-combined"))
            throw new InvalidOperationException("Unknown sky measurement composition.");
        if (Environment.GetEnvironmentVariable("WYU_R09_LATENCY") == "1")
            throw new InvalidOperationException("Sky and R09 qualification hooks must run separately; combined pressure is not qualified by this route.");
        if (OrbitCombined && (Environment.GetEnvironmentVariable("WYU_ORBIT_DEMO_SMOKE") != "1" ||
            Environment.GetEnvironmentVariable("WYU_ORBIT_PBR") != "on" ||
            Environment.GetEnvironmentVariable("WYU_ORBIT_BOULDER") != "on" ||
            Environment.GetEnvironmentVariable("WYU_WP15") != "on" ||
            Environment.GetEnvironmentVariable("WYU_WP16") != "on" ||
            Environment.GetEnvironmentVariable("WYU_WP16_SMOKE") != "1"))
            throw new InvalidOperationException("Orbit-combined sky measurement requires the Orbit preset, PBR, boulder and WP15/WP16 in both variants.");
        if (!OrbitCombined && (Environment.GetEnvironmentVariable("WYU_ORBIT_DEMO_SMOKE") == "1" ||
            Environment.GetEnvironmentVariable("WYU_WP15") == "on" ||
            Environment.GetEnvironmentVariable("WYU_WP16_SMOKE") == "1"))
            throw new InvalidOperationException("Far-horizon sky measurement cannot silently include Orbit, WP15 or props.");
        if (!ReferenceSky.Enabled || !BiomeMeasurements.UseBiomes || Environment.GetEnvironmentVariable("WYU_PACKAGE_E") != "on")
            throw new InvalidOperationException("Sky measurement requires canonical G cameras and Packages B/E enabled.");
        var type = typeof(D3D12Renderer);
        var assembly = type.Assembly;
        var hooksType = assembly.GetType("Groundwork.Rendering.D3D12.GpuRetirementTestHooks") ?? throw Mismatch();
        var schema = hooksType.GetField("SkyQualificationHookSchemaVersion", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (schema?.GetRawConstantValue() is not int version || version != 2)
            throw Mismatch();
        var gpuType = assembly.GetType("Groundwork.Rendering.D3D12.GpuDevice") ?? throw Mismatch();
        var field = type.GetField("_retirementHooks", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (field is null || !field.IsInitOnly || field.FieldType != hooksType || field.GetValue(renderer) is not null)
            throw Mismatch();
        var constructors = hooksType.GetConstructors();
        if (constructors.Length != 1)
            throw Mismatch();
        var constructor = constructors[0];
        var parameters = constructor.GetParameters();
        if (parameters.Any(parameter => !parameter.HasDefaultValue || parameter.DefaultValue is not null))
            throw Mismatch();
        var observer = new SkyMeasurements(renderer);
        var presentationWait = type.GetMethod("WaitForPresentedAsync", [typeof(ulong), typeof(CancellationToken)]) ?? throw Mismatch();
        observer._presentationWait = presentationWait.CreateDelegate<Func<ulong, CancellationToken, ValueTask<RendererSnapshot>>>(renderer);
        var arguments = new object?[parameters.Length];
        Action<object> initialized = observer.Initialize;
        Bind("AfterDeviceInitialize", Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(gpuType), initialized.Target, initialized.Method));
        Bind("AfterSkyTimingRetired", (Action<ulong, ulong, double, double, bool>)observer.Retired);
        Bind("AfterSkyRecordMeasured", (Action<ulong, ulong, double, long, int>)observer.Recorded);
        Bind("AfterSuccessfulPresent", (Action<ulong, long>)observer.Presented);
        if (SkyHandoffDiagnostics.Enabled)
        {
            Bind("AfterSkyPipelineReady", (Action<long, long, bool>)observer.PipelineReady);
            Bind("AfterSkyTimingDetailsRetired", (Action<ulong, ulong, ulong, ulong, ulong, ulong, ulong>)observer.TimingDetails);
            Bind("AfterSkyClockCalibration", (Action<ulong, ulong, ulong>)observer.ClockCalibration);
            if (GpuPassBreakdownEnabled)
            {
                Bind("AfterSkyPrepassTimingRetired", (Action<ulong, ulong, double, double, double>)observer.PrepassTiming);
                Bind("AfterSkyGpuPassDetailsRetired", (Action<ulong, ulong, ulong, ulong, ulong, ulong>)observer.GpuPassDetails);
            }
            if (Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE") == "1")
            {
                Bind("AfterSkyLookupGpuTimingRetired", (Action<ulong, ulong, double, double>)observer.LookupGpuTiming);
                Bind("AfterSkyLookupRecordMeasured", (Action<ulong, ulong, double, double>)observer.LookupRecordTiming);
                Bind("AfterSkyLookupPreparationStage", (Action<string, long, long, bool>)observer.LookupPreparationStage);
            }
        }
        field.SetValue(renderer, constructor.Invoke(arguments));
        Owners.Add(renderer, observer);
        RegisterEvidenceOwner(observer);
        observer.Buffer(new(EventKind.Owner, "startup", 0, 0, Stopwatch.GetTimestamp()));

        void Bind(string name, Delegate callback)
        {
            var index = Array.FindIndex(parameters, parameter => parameter.Name == name);
            if (index < 0 || parameters[index].ParameterType != callback.GetType())
                throw Mismatch();
            arguments[index] = callback;
        }
    }

    private void Initialize(object device)
    {
        SkyHandoffDiagnostics.Mark("gpu-device-initialized");
        var requested = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_PROTOTYPE") == "1";
        var enabled = device.GetType().GetProperty("SkyLookupPrototypeEnabledForTests",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (enabled is null)
        {
            if (requested)
                throw Mismatch();
        }
        else if (enabled.PropertyType != typeof(bool) || enabled.GetValue(device) is not bool actual || actual != requested)
            throw Mismatch();
        _lookupPrototype = requested;
        var reuseRequested = Environment.GetEnvironmentVariable("GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES") == "1";
        var reuseEnabled = device.GetType().GetProperty("SkyLookupResourceReuseEnabledForTests",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (reuseEnabled is null)
        {
            if (reuseRequested)
                throw Mismatch();
        }
        else if (reuseEnabled.PropertyType != typeof(bool) || reuseEnabled.GetValue(device) is not bool actualReuse ||
            actualReuse != reuseRequested)
            throw Mismatch();
        _lookupResourceReuse = reuseRequested;
        var enable = device.GetType().GetMethod("EnableSkyTimingForTests", BindingFlags.NonPublic | BindingFlags.Instance,
            Type.EmptyTypes) ?? throw Mismatch();
        enable.Invoke(device, null);
    }

    private void Retired(ulong sequence, ulong fence, double frameMicroseconds, double skyMicroseconds, bool hasSky)
    {
        var window = Volatile.Read(ref _window);
        if (window is not null && sequence >= window.First && sequence <= window.Last)
            Buffer(new(EventKind.Gpu, window.Phase, sequence, fence, Stopwatch.GetTimestamp(),
                frameMicroseconds, skyMicroseconds, 0, hasSky));
    }

    private void Recorded(ulong sequence, ulong fence, double recordMicroseconds, long allocatedBytes, int draws)
    {
        var window = Volatile.Read(ref _window);
        if (window is not null && sequence >= window.First && sequence <= window.Last)
            Buffer(new(EventKind.Record, window.Phase, sequence, fence, Stopwatch.GetTimestamp(),
                recordMicroseconds, draws, allocatedBytes));
    }

    private void Presented(ulong fence, long timestamp)
    {
        if (_handoff is not { } handoff || Volatile.Read(ref _firstPresent) != 0)
            return;
        if (!_renderer.TryReadResourceDiagnostics(out var snapshot) || snapshot.PresentFence != fence ||
            snapshot.PresentTimestamp != timestamp || snapshot.FrameSequence != snapshot.PresentedSequence)
            throw new InvalidOperationException("The native sky Present event does not match its resource snapshot.");
        if (snapshot.PresentedSequence == 0)
        {
            // A startup clear has no submitted view. The first nonzero sequence
            // on this new renderer owner is the requested mask/content view.
            Buffer(new(EventKind.UnboundPresent, "startup", 0, fence, timestamp));
            return;
        }
        if (Interlocked.CompareExchange(ref _firstPresent, timestamp, 0) != 0)
            return;
        SkyHandoffDiagnostics.Complete(_owner, snapshot.PresentedSequence, fence, timestamp);
        Write(new { kind = "handoff", owner = _owner, direction = handoff.Direction,
            start = handoff.Start, end = timestamp, fence, sequence = snapshot.PresentedSequence,
            sourceFrameSequence = snapshot.FrameSequence, frequency = Stopwatch.Frequency });
        FlushEvents();
    }

    internal static void BeginHandoff(string direction)
    {
        if (Enabled)
        {
            SkyHandoffDiagnostics.Prepare();
            FlushBeforeHandoff();
            var started = Stopwatch.GetTimestamp();
            _pendingHandoff = new(direction, started);
            SkyHandoffDiagnostics.Begin(direction, started);
        }
    }

    internal static void BeginPhase(D3D12Renderer renderer, string phase, ulong first, ulong last, string identity,
        ReadOnlySpan<byte> canonicalInputs)
    {
        var observer = Owners.GetValue(renderer, _ => throw Mismatch());
        Volatile.Write(ref observer._window, new(phase, first, last));
        var size = renderer.Read();
        if (!Smoke && (size.AppliedWidth != 1920 || size.AppliedHeight != 1080))
            throw new InvalidOperationException("Full sky measurements require a 1920x1080 render extent.");
        Write(new { kind = "phase", owner = observer._owner, phase, first, last, identity,
            width = size.AppliedWidth, height = size.AppliedHeight, composition = Composition });
        if (GpuPassBreakdownEnabled)
            Write(new { kind = "phase-input", owner = observer._owner, phase, identity,
                canonicalBase64 = Convert.ToBase64String(canonicalInputs) });
        if (SkyHandoffDiagnostics.Enabled)
            Write(new { kind = "clock-anchor", owner = observer._owner, phase,
                qpc = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency, utc = DateTime.UtcNow.ToString("O") });
    }

    internal static void Prepared(D3D12Renderer renderer, ulong sequence, double microseconds, long allocatedBytes)
    {
        var observer = Owners.GetValue(renderer, _ => throw Mismatch());
        var window = Volatile.Read(ref observer._window);
        if (window is not null && sequence >= window.First && sequence <= window.Last)
            observer.Buffer(new(EventKind.Prepare, window.Phase, sequence, 0, Stopwatch.GetTimestamp(),
                microseconds, 0, allocatedBytes));
    }

    internal static ValueTask<RendererSnapshot> WaitForPresentedAsync(D3D12Renderer renderer, ulong sequence, CancellationToken token)
    {
        var observer = Owners.GetValue(renderer, _ => throw Mismatch());
        return AwaitPresentationAsync(observer._presentationWait ?? throw Mismatch(), sequence, TimeSpan.FromSeconds(30), token);
    }

    internal static async ValueTask<RendererSnapshot> AwaitPresentationAsync(
        Func<ulong, CancellationToken, ValueTask<RendererSnapshot>> wait, ulong sequence, TimeSpan deadline, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var underlyingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = wait(sequence, underlyingCancellation.Token);
        RendererSnapshot state;
        try
        {
            state = pending.IsCompletedSuccessfully ? pending.Result :
                await pending.AsTask().WaitAsync(deadline, token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is TimeoutException || failure is OperationCanceledException && token.IsCancellationRequested)
        {
            try { underlyingCancellation.Cancel(); }
            catch (Exception error)
            {
                try { Console.Error.WriteLine("Presentation waiter cancellation also failed: " + error); }
                catch { /* Retain the original wait failure. */ }
            }
            throw;
        }
        if (state.FirstFault is not null || state.Lifecycle != RendererLifecycle.Running)
            throw new InvalidOperationException($"Terrain renderer failed ({state}).");
        if (state.LastPresentedSequence < sequence)
            throw new InvalidOperationException("Presentation acknowledgement did not reach the requested sequence.");
        return state;
    }

    internal static void Write(object value) => Console.WriteLine("PACKAGE G MEASURE " + JsonSerializer.Serialize(value));
    private static InvalidOperationException Mismatch() => new("The pinned Groundwork engine does not expose the expected sky measurement hook schema.");
    private sealed record MeasurementWindow(string Phase, ulong First, ulong Last);
    private sealed record Handoff(string Direction, long Start);
}

internal sealed partial class VolumeExploration
{
    private static readonly JsonSerializerOptions SkyMeasurementJson = new() { IncludeFields = true };
    private async Task MeasureSkyPhaseAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        VolumeCamera camera, string phase, bool far, ulong tick, CancellationToken token, bool texturedOrbit = false)
    {
        Exception? primaryFailure = null;
        try
        {
            await MeasureSkyPhaseCoreAsync(terrain, renderer, camera, phase, far, tick, token, texturedOrbit).ConfigureAwait(false);
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try { SkyMeasurements.Flush(renderer); }
            catch (Exception error) when (primaryFailure is not null)
            {
                try { Console.Error.WriteLine("Sky evidence flush also failed: " + error); }
                catch { /* Keep the original measurement failure. */ }
            }
        }
    }

    private async Task MeasureSkyPhaseCoreAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        VolumeCamera camera, string phase, bool far, ulong tick, CancellationToken token, bool texturedOrbit = false)
    {
        if (!SkyMeasurements.CollectPhases)
            return;
        if (SkyMeasurements.OrbitCombined && (!orbitDemo || OrbitMaterialHost is null || !far && Props is null || far && !texturedOrbit))
            throw new InvalidOperationException("Orbit-combined measurement requires the production Orbit material/prop route, without a base-color fallback.");
        var supplied = camera;
        var suppliedNear = supplied.Sunlight.HasValue ? supplied : ApplySunlight(supplied, tick, 0);
        var expected = !far || texturedOrbit ? suppliedNear : orbitDemo && suppliedNear.Sunlight is { } suppliedSun
            ? supplied with { Tint = ReferenceSunlight.GlobeTint(supplied.Position, suppliedSun) } : supplied;
        // Production cameras start with neutral environment fields. Preserve the
        // fixed pose/projection while rebuilding exactly the synchronous weather,
        // sky and sunlight chain; never include await/logging in its CPU interval.
        var baseCamera = camera with { Tint = System.Numerics.Vector4.One, ClearColor = Sky,
            Fog = null, Sky = null, Sunlight = null, TerrainShadows = false };
        camera = PrepareCamera();
        if (camera != expected)
            throw new InvalidOperationException("The held preparation does not reproduce the production atmosphere camera.");
        var canonicalScene = far ? ProceduralGlobe.Frame(1, camera).Scene : null;
        var canonical = new { seed = Descriptor.Seed, tick, camera.Position, camera.Forward, camera.Up,
            camera.FieldOfView, camera.NearPlane, camera.FarPlane, camera.Tint, camera.ClearColor, camera.Fog,
            camera.Sunlight, camera.Sky, camera.TerrainShadows, camera.View.Frame, scene = canonicalScene,
            revision = _published.TerrainRevision, profile = ReferenceSky.Profile,
            composition = SkyMeasurements.Composition, orbitDemo, texturedOrbit,
            pbr = SkyMeasurements.OrbitCombined && OrbitDemoPreset.PbrEnabled,
            boulder = SkyMeasurements.OrbitCombined && OrbitDemoPreset.BoulderEnabled,
            wp15 = ReferenceSunlight.Enabled, props = Props is not null };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, SkyMeasurementJson);
        var identity = Convert.ToHexString(SHA256.HashData(bytes));
        var count = SkyMeasurements.Warmup + SkyMeasurements.Samples;
        if (SkyMeasurements.OrbitCombined)
        {
            // Six maps each need at most coarse/fine copy turns and one final
            // retry observation. Each submitted frame awaits native presentation;
            // the measurement device also retires prior work before query reuse.
            // This is a bounded preparation attempt, not a relaxed sample window.
            var preparationFirst = _sequence + 1;
            var preparationFrames = 0;
            while (true)
            {
                var textures = renderer.ReadTextureDiagnostics();
                if (OrbitMaterialHost?.MaterialsResident(renderer, far ? null : camera.View) == true &&
                    textures.UploadedBytes > 0 && textures.StagingBytes == 0)
                    break;
                if (preparationFrames == OrbitTerrainMaterials.MaximumPreparationFrames)
                    throw new InvalidOperationException($"Orbit texture preparation did not settle after {preparationFrames} acknowledged frames: " +
                        $"uploaded={textures.UploadedBytes}, staging={textures.StagingBytes}.");
                await PresentHeldAsync(PrepareCamera(), measurePreparation: false).ConfigureAwait(false);
                preparationFrames++;
            }
            var settled = renderer.ReadTextureDiagnostics();
            SkyMeasurements.Write(new { kind = "texture-preparation", phase, composition = SkyMeasurements.Composition,
                first = preparationFirst, last = _sequence, frames = preparationFrames,
                maximumFrames = OrbitTerrainMaterials.MaximumPreparationFrames,
                targetCount = OrbitTerrainMaterials.MapCount, allTargetsResident = true,
                settled.UploadedBytes, settled.StagingBytes });
        }
        var first = _sequence + 1;
        SkyMeasurements.BeginPhase(renderer, phase, first + (ulong)SkyMeasurements.Warmup,
            first + (ulong)count - 1, identity, bytes);
        var heldTextures = renderer.ReadTextureDiagnostics();
        for (var i = 0; i < count + 1; i++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var prepared = PrepareCamera();
            var preparationMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            var preparationAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (prepared != camera)
                throw new InvalidOperationException("The held atmosphere preparation changed its canonical inputs.");
            await PresentHeldAsync(prepared, measurePreparation: true, preparationMicroseconds, preparationAllocatedBytes).ConfigureAwait(false);
            if (i == SkyMeasurements.Warmup - 1)
                heldTextures = renderer.ReadTextureDiagnostics();
            if (SkyMeasurements.OrbitCombined && i >= SkyMeasurements.Warmup)
            {
                var currentTextures = renderer.ReadTextureDiagnostics();
                if (heldTextures.UploadedBytes == 0 || currentTextures.UploadedBytes != heldTextures.UploadedBytes || currentTextures.StagingBytes != 0)
                    throw new InvalidOperationException("Orbit texture uploads did not settle before the held measurement window.");
            }
        }
        using var published = terrain.TryRead();
        if (!far && (published is null || published.Info.TerrainRevision != _published.TerrainRevision))
            throw new InvalidOperationException("Sky-only sampling changed the terrain revision.");
        if (!renderer.TryReadResourceDiagnostics(out var resources))
            throw new InvalidOperationException("Sky measurement resource snapshot is unavailable.");
        var textureSnapshot = renderer.ReadTextureDiagnostics();
        SkyMeasurements.Write(new { kind = "textures", phase, composition = SkyMeasurements.Composition,
            textureSnapshot.UploadedBytes, textureSnapshot.NativeHighWaterBytes });
        foreach (var owner in Enum.GetValues<D3D12ResourceOwner>())
        {
            var balance = resources.GetBalance(owner);
            SkyMeasurements.Write(new { kind = "resource", phase, resourceOwner = owner.ToString(),
                balance.Live, balance.LiveBytes, balance.Retained, balance.RetainedBytes,
                resources.LedgerHasUnknownSizes, resources.LedgerOverflowed });
        }

        async Task PresentHeldAsync(VolumeCamera prepared, bool measurePreparation, double preparationMicroseconds = 0,
            long preparationAllocatedBytes = 0)
        {
            var begin = await BeginAsync(token).ConfigureAwait(false);
            terrain.BeginTick(begin);
            Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
            var sequence = ++_sequence;
            if (measurePreparation)
                SkyMeasurements.Prepared(renderer, sequence, preparationMicroseconds, preparationAllocatedBytes);
            OrbitMaterialHost?.RetryMaterials(renderer, sequence);
            if (far)
                RequireFrame(renderer.TrySubmit(SkyMeasurements.Submit(ProceduralGlobe.Frame(sequence, prepared))));
            else
            {
                Props?.Pump();
                RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence, SkyMeasurements.Submit(prepared)));
            }
            await SkyMeasurements.WaitForPresentedAsync(renderer, sequence, token).ConfigureAwait(false);
            Drain(terrain);
        }

        VolumeCamera PrepareCamera()
        {
            var weather = Atmosphere(baseCamera.Position, tick);
            var environment = ApplySky(ApplyAtmosphere(baseCamera, weather), tick, 0, weather);
            var nearCamera = ApplySunlight(environment, tick, 0);
            return !far || texturedOrbit ? nearCamera : orbitDemo && nearCamera.Sunlight is { } sun
                ? environment with { Tint = ReferenceSunlight.GlobeTint(environment.Position, sun) } : environment;
        }
    }
}
