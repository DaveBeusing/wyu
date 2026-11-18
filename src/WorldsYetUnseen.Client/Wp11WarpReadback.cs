using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed class Wp11WarpReadback(string fixture, bool markerOnly)
{
    private static readonly string[] Checkpoints =
        ["near-before-ascent", "far-after-ascent", "orbit-globe", "far-before-return", "near-after-return", "edited-final-approach"];
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, WriteIndented = true };
    private readonly string[] _checkpoints = markerOnly ? ["landing-marker"] : Checkpoints;
    private readonly string _output = Path.Combine(Path.GetFullPath(fixture), "warp-present-readback");
    private readonly List<Present> _completed = [];
    private Request? _pending;
    private int _owner;
    internal VolumeTerrainColorEvaluator? ColorEvaluator { get; set; }
    internal bool MarkerOnly { get; } = markerOnly;

    internal void Attach(D3D12Renderer renderer)
    {
        // The stock package stays pinned. Only the separately built qualification provider exposes this method.
        var configure = typeof(D3D12Renderer).GetMethod("ConfigureQualificationPresentReadback",
            [typeof(Func<ulong, bool>), typeof(Action<ulong, int, int, byte[], float[]>)])
            ?? throw new InvalidOperationException("The WARP present readback requires the qualification provider.");
        var attach = configure.CreateDelegate<Action<Func<ulong, bool>, Action<ulong, int, int, byte[], float[]>>>(renderer);
        var owner = ++_owner;
        attach(sequence => Volatile.Read(ref _pending) is { } pending && pending.Owner == owner && pending.Sequence == sequence,
            (sequence, width, height, color, depth) =>
            {
                if (Volatile.Read(ref _pending) is { } pending && pending.Owner == owner && pending.Sequence == sequence)
                    pending.Pixels.SetResult(new(width, height, color, depth));
            });
    }

    internal void Arm(string checkpoint, ulong sequence)
    {
        if (_completed.Count == _checkpoints.Length || checkpoint != _checkpoints[_completed.Count] ||
            _completed.Count > 0 && sequence <= _completed[^1].Sequence ||
            Interlocked.CompareExchange(ref _pending, new(checkpoint, _owner, sequence), null) is not null)
            throw new InvalidOperationException($"Unexpected WARP readback checkpoint {checkpoint} for sequence {sequence}.");
    }

    internal async Task CompleteAsync(D3D12Renderer renderer, VolumeStreamingCoordinator terrain,
        VolumePublicationInfo publication, Pose pose, RenderMeshPair? globe, CancellationToken token)
    {
        var pending = Volatile.Read(ref _pending) ?? throw new InvalidOperationException("No WARP readback was armed.");
        var image = await pending.Pixels.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        var state = renderer.Read();
        var diagnostics = renderer.ReadDiagnostics();
        if (pending.Owner != _owner || state.LastPresentedSequence != pending.Sequence ||
            diagnostics is not { Adapter.IsSoftware: true, DebugLayerEnabled: true, DredEnabled: true } ||
            state.AppliedWidth != image.Width || state.AppliedHeight != image.Height)
            throw new InvalidOperationException($"WARP did not retire the exact requested Present for {pending.Checkpoint}.");

        Directory.CreateDirectory(_output);
        var color = WriteBuffer(pending.Checkpoint + ".rgba8", image.Color);
        var depth = WriteBuffer(pending.Checkpoint + ".d32f", MemoryMarshal.AsBytes(image.Depth.AsSpan()));
        var scene = pose.Scene;
        var samples = Wp11WarpReadbackSamples.Inspect(pending.Checkpoint, image.Width, image.Height, image.Color, image.Depth,
            scene, pose.Origin, terrain, publication, globe, pose.Pad, ColorEvaluator);
        var present = new Present(pending.Checkpoint, _owner, pending.Sequence, pose.FlightTick,
            publication.Generation, publication.TerrainRevision, publication.View.RendererEpoch, pose.Overlay);
        WriteJson(pending.Checkpoint + ".json", new
        {
            present,
            pose,
            publication,
            renderer = globe is not null ? "far-fixed-generated-globe" : "near-published-volume",
            diagnostics.Adapter,
            diagnostics.DebugLayerEnabled,
            diagnostics.DredEnabled,
            image.Width,
            image.Height,
            color,
            depth,
            samples,
        });
        _completed.Add(present);
        Volatile.Write(ref _pending, null);
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 WARP READBACK {present.Checkpoint} owner={present.Owner} present={present.Sequence} tick={present.FlightTick} generation={present.Generation} revision={present.Revision}"));
    }

    internal void VerifyComplete()
    {
        if (_completed.Count != _checkpoints.Length || _completed.Any(p => p.Revision != 2 || p.Overlay != _completed[0].Overlay))
            throw new InvalidOperationException("The WARP samples do not identify the expected checkpoints and committed edit.");

        if (MarkerOnly ? _owner != 2 || _completed[0].Owner != 2 :
            _owner != 3 ||
            !_completed.Select(p => p.Owner).SequenceEqual([1, 2, 2, 2, 3, 3]) ||
            _completed[0].FlightTick != _completed[1].FlightTick ||
            _completed[3].FlightTick != _completed[4].FlightTick ||
            _completed[4].TerrainRendererEpoch <= _completed[0].TerrainRendererEpoch ||
            _completed[4].Generation != _completed[5].Generation)
            throw new InvalidOperationException("The WARP samples do not identify the expected renderer owners and handoffs.");

        WriteJson("present-manifest.json", _completed);
        WriteJson("runtime-assemblies.json", new
        {
            ClientSha256 = AssemblyHash(typeof(Wp11WarpReadback)),
            ProviderSha256 = AssemblyHash(typeof(D3D12Renderer)),
        });
    }

    private static string AssemblyHash(Type type)
    {
        using var stream = File.OpenRead(type.Assembly.Location);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private BufferFile WriteBuffer(string name, ReadOnlySpan<byte> bytes)
    {
        using var stream = File.Open(Path.Combine(_output, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        return new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private void WriteJson(string name, object value)
    {
        using var stream = File.Open(Path.Combine(_output, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, Json);
    }

    internal readonly record struct Pose(ulong Seed, int FlightTick, ulong PresentationTick, double PresentationFraction,
        string Overlay, PlanetVector Pad, PlanetLocalFrame? Origin, RenderScene Scene, bool AimedAtEdit, bool AimedAtMarker = false);
    private sealed record Present(string Checkpoint, int Owner, ulong Sequence, int FlightTick,
        ulong Generation, ulong Revision, ulong TerrainRendererEpoch, string Overlay);
    private sealed record BufferFile(string Name, int Length, string Sha256);
    private sealed record Image(int Width, int Height, byte[] Color, float[] Depth);

    private sealed record Request(string Checkpoint, int Owner, ulong Sequence)
    {
        internal TaskCompletionSource<Image> Pixels { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
