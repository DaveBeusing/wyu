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
internal sealed class SkyMeasurements
{
    private static readonly string? Mode = Environment.GetEnvironmentVariable("WYU_PACKAGE_G_MEASURE");
    private static readonly ConditionalWeakTable<D3D12Renderer, SkyMeasurements> Owners = new();
    private static readonly Lazy<string> EngineHash = new(() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(D3D12Renderer).Assembly.Location))));
    private static int _nextOwner;
    private static Handoff? _pendingHandoff;
    private readonly D3D12Renderer _renderer;
    private readonly int _owner = Interlocked.Increment(ref _nextOwner);
    private readonly Handoff? _handoff = _pendingHandoff;
    private MeasurementWindow? _window;
    private long _firstPresent;

    private SkyMeasurements(D3D12Renderer renderer) => _renderer = renderer;

    internal static bool Enabled => Mode is "off" or "on";
    internal static bool Smoke => Environment.GetEnvironmentVariable("WYU_SKY_MEASURE_SMOKE") == "1";
    internal static bool CollectPhases => Enabled && Environment.GetEnvironmentVariable("WYU_SKY_MEASURE_PHASES") == "1";
    internal static int Warmup => Smoke ? 4 : 40;
    internal static int Samples => Smoke ? 8 : 400;

    internal static VolumeCamera Submit(VolumeCamera camera) => Enabled && Mode == "off" ? camera with { Sky = null } : camera;
    internal static RenderFrame Submit(RenderFrame frame) => Enabled && Mode == "off" ? frame with { Sky = null } : frame;

    internal static void Attach(D3D12Renderer renderer)
    {
        if (!Enabled)
            return;
        if (!ReferenceSky.Enabled || !BiomeMeasurements.UseBiomes || Environment.GetEnvironmentVariable("WYU_PACKAGE_E") != "on")
            throw new InvalidOperationException("Sky measurement requires canonical G cameras and Packages B/E enabled.");
        var type = typeof(D3D12Renderer);
        var assembly = type.Assembly;
        var hooksType = assembly.GetType("Groundwork.Rendering.D3D12.GpuRetirementTestHooks") ?? throw Mismatch();
        var schema = hooksType.GetField("SkyQualificationHookSchemaVersion", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (schema?.GetRawConstantValue() is not int version || version != 1)
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
        var arguments = new object?[parameters.Length];
        Action<object> initialized = observer.Initialize;
        Bind("AfterDeviceInitialize", Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(gpuType), initialized.Target, initialized.Method));
        Bind("AfterSkyTimingRetired", (Action<ulong, double, double, bool>)observer.Retired);
        Bind("AfterSkyRecordMeasured", (Action<ulong, double, long, int>)observer.Recorded);
        Bind("AfterSuccessfulPresent", (Action<ulong, long>)observer.Presented);
        field.SetValue(renderer, constructor.Invoke(arguments));
        Owners.Add(renderer, observer);
        Write(new { kind = "owner", owner = observer._owner, mode = Mode, frequency = Stopwatch.Frequency,
            smoke = Smoke, engine = assembly.GetName().Version?.ToString(), engineHash = EngineHash.Value,
            wp15 = ReferenceSunlight.Enabled });

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
        var enable = device.GetType().GetMethod("EnableSkyTimingForTests", BindingFlags.NonPublic | BindingFlags.Instance,
            Type.EmptyTypes) ?? throw Mismatch();
        enable.Invoke(device, null);
    }

    private void Retired(ulong sequence, double frameMicroseconds, double skyMicroseconds, bool hasSky)
    {
        var window = Volatile.Read(ref _window);
        if (window is not null && sequence >= window.First && sequence <= window.Last)
            Write(new { kind = "gpu", owner = _owner, sequence, phase = window.Phase,
                frameMicroseconds, skyMicroseconds, hasSky });
    }

    private void Recorded(ulong sequence, double recordMicroseconds, long allocatedBytes, int draws)
    {
        var window = Volatile.Read(ref _window);
        if (window is not null && sequence >= window.First && sequence <= window.Last)
            Write(new { kind = "record", owner = _owner, sequence, phase = window.Phase,
                recordMicroseconds, allocatedBytes, draws });
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
            Write(new { kind = "unbound-present", owner = _owner, fence, timestamp });
            return;
        }
        if (Interlocked.CompareExchange(ref _firstPresent, timestamp, 0) != 0)
            return;
        Write(new { kind = "handoff", owner = _owner, direction = handoff.Direction,
            start = handoff.Start, end = timestamp, fence, sequence = snapshot.PresentedSequence,
            sourceFrameSequence = snapshot.FrameSequence, frequency = Stopwatch.Frequency });
    }

    internal static void BeginHandoff(string direction)
    {
        if (Enabled)
            _pendingHandoff = new(direction, Stopwatch.GetTimestamp());
    }

    internal static void BeginPhase(D3D12Renderer renderer, string phase, ulong first, ulong last, string identity)
    {
        var observer = Owners.GetValue(renderer, _ => throw Mismatch());
        Volatile.Write(ref observer._window, new(phase, first, last));
        var size = renderer.Read();
        if (!Smoke && (size.AppliedWidth != 1920 || size.AppliedHeight != 1080))
            throw new InvalidOperationException("Full sky measurements require a 1920x1080 render extent.");
        Write(new { kind = "phase", owner = observer._owner, phase, first, last, identity,
            width = size.AppliedWidth, height = size.AppliedHeight });
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
        VolumeCamera camera, string phase, bool far, ulong tick, CancellationToken token)
    {
        if (!SkyMeasurements.CollectPhases)
            return;
        if (!far)
            camera = ApplySunlight(camera, tick, 0);
        var canonical = new { seed = Descriptor.Seed, tick, camera.Position, camera.Forward, camera.Up,
            camera.FieldOfView, camera.Tint, camera.ClearColor, camera.Fog, revision = _published.TerrainRevision };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, SkyMeasurementJson);
        var identity = Convert.ToHexString(SHA256.HashData(bytes));
        var count = SkyMeasurements.Warmup + SkyMeasurements.Samples;
        var first = _sequence + 1;
        SkyMeasurements.BeginPhase(renderer, phase, first + (ulong)SkyMeasurements.Warmup,
            first + (ulong)count - 1, identity);
        for (var i = 0; i < count + 1; i++)
        {
            var begin = await BeginAsync(token).ConfigureAwait(false);
            terrain.BeginTick(begin);
            Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
            var sequence = ++_sequence;
            if (far)
                RequireFrame(renderer.TrySubmit(SkyMeasurements.Submit(ProceduralGlobe.Frame(sequence, camera))));
            else
                RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence, SkyMeasurements.Submit(camera)));
            await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token).ConfigureAwait(false);
            Drain(terrain);
        }
        using var published = terrain.TryRead();
        if (!far && (published is null || published.Info.TerrainRevision != _published.TerrainRevision))
            throw new InvalidOperationException("Sky-only sampling changed the terrain revision.");
        if (!renderer.TryReadResourceDiagnostics(out var resources))
            throw new InvalidOperationException("Sky measurement resource snapshot is unavailable.");
        foreach (var owner in Enum.GetValues<D3D12ResourceOwner>())
        {
            var balance = resources.GetBalance(owner);
            SkyMeasurements.Write(new { kind = "resource", phase, resourceOwner = owner.ToString(),
                balance.Live, balance.LiveBytes, balance.Retained, balance.RetainedBytes,
                resources.LedgerHasUnknownSizes, resources.LedgerOverflowed });
        }
    }
}
