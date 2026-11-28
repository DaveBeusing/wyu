using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

// Explicit installed fixture: a normal empty stream for Far Horizon or an unused slot alongside Orbit materials.
internal sealed class R09IndependentTexturePressure
{
    private const int CopyBytes = 512 * 1024 * 4;
    internal static readonly D3D12TextureReservation Credit = new(CopyBytes + 256, CopyBytes, CopyBytes + 256, CopyBytes, CopyBytes);
    private readonly CookedTexture _texture = CookTexture();
    private D3D12TextureHandoff? _stream;
    private D3D12Renderer? _renderer;
    private ulong _paletteRevision;
    private OrbitTerrainMaterials? _materials;
    private bool _orbital;

    internal void AttachOrbit(D3D12Renderer renderer, D3D12TextureHandoff stream,
        OrbitTerrainMaterials materials, D3D12TextureView? farView = null)
    {
        _renderer = renderer;
        _stream = stream;
        _materials = materials;
        _orbital = farView.HasValue;
        FarView = farView;
    }

    private bool MaterialsResident(D3D12TextureHandoff stream, D3D12TextureView view) =>
        _materials?.IsResident(stream, view, _orbital) ?? true;

    private bool PublishTarget(D3D12TextureHandoff stream, D3D12TextureView view, D3D12TextureTarget? target) =>
        _materials is { } materials ? materials.PublishPressureTarget(stream, view, target)
            : stream.Publish(view, target?.Sequence ?? checked(++_paletteRevision), target is { } value ? [value] : []);

    internal static bool OrbitRequested => Requested &&
        Environment.GetEnvironmentVariable("WYU_R09_TWILIGHT_PRESSURE") == R09TwilightPressure.OrbitCombined;

    internal static bool Requested => Environment.GetEnvironmentVariable("WYU_R09_INDEPENDENT_TEXTURE_COPY") == "1";
    internal D3D12TextureCopyQualification Qualification { get; } = new();
    internal D3D12TextureView? FarView { get; private set; }

    private static D3D12TextureHandoff CreateStream() =>
        new(new(1, 1, CopyBytes + 4096, CopyBytes + 4096, CopyBytes + 4096, CopyBytes + 65536, CopyBytes + 65536, 1));

    internal void Attach(D3D12Renderer renderer)
    {
        var stream = CreateStream();
        renderer.AttachTextureHandoff(stream);
        _stream = stream;
        _renderer = renderer;
    }

    internal D3D12Renderer AttachFar(WindowsHostModule window, IRuntimeOwnership owner, RenderMeshPair meshes,
        D3D12DeviceSession device, D3D12TextureView view, TimeSpan timeout)
    {
        var stream = CreateStream();
        var renderer = D3D12Renderer.CreateWithSessionMeshesForTextureCopyQualification(window, owner, meshes, device,
            stream, view, timeout, timeout);
        _stream = stream;
        _renderer = renderer;
        FarView = view;
        return renderer;
    }

    internal void Detach(D3D12Renderer renderer)
    {
        if (!ReferenceEquals(_renderer, renderer))
            return;
        _stream = null;
        _renderer = null;
        FarView = null;
        _materials = null;
        _orbital = false;
    }

    internal Task<Copy> BeginAsync(D3D12Renderer renderer, Guid world, VolumePublicationInfo previous, CancellationToken token) =>
        BeginAsync(renderer, new(world, previous.View, previous.TerrainRevision, previous.Generation), token);

    internal Task<Copy> BeginFarAsync(D3D12Renderer renderer, CancellationToken token) =>
        BeginAsync(renderer, FarView ?? throw new InvalidOperationException("R09 Recovery requires its fixed Far Horizon view."), token);

    private async Task<Copy> BeginAsync(D3D12Renderer renderer, D3D12TextureView view, CancellationToken token)
    {
        if (!ReferenceEquals(_renderer, renderer) || _stream is not { } stream)
            throw new InvalidOperationException("The R09 optional texture stream is unavailable for this renderer owner.");
        var predecessor = renderer.Read();
        var before = stream.ReadUsage();
        if (predecessor is not { Surface: RenderSurfaceState.Drawable, LastPresentedSequence: > 0 } ||
            (_materials is null ? before != default : before.Requests != 0 ||
                !MaterialsResident(stream, view)))
            throw new InvalidOperationException("R09 texture pressure requires a drawable predecessor, settled copies and resident Orbit materials when enabled.");
        D3D12ResourceBalance originalTexture = default;
        if (_materials is not null)
        {
            if (!renderer.TryReadResourceDiagnostics(out var prior) ||
                prior.GetBalance(D3D12ResourceOwner.TextureStaging) is not { Live: 0, Retained: 0 })
                throw new InvalidOperationException("R09 Orbit pressure requires a coherent native allocation baseline without active staging.");
            originalTexture = prior.GetBalance(D3D12ResourceOwner.Texture);
            if (originalTexture.Live < OrbitTerrainMaterials.MapCount)
                throw new InvalidOperationException("R09 Orbit pressure requires all native production maps before arming the copy.");
        }
        var diagnostics = renderer.ReadDiagnostics();
        if (!diagnostics.DebugLayerEnabled || !diagnostics.DredEnabled)
            throw new InvalidOperationException("R09 texture pressure requires the native debug layer and DRED.");

        var revision = checked(++_paletteRevision);
        var target = new D3D12TextureTarget(new(ushort.MaxValue, MaterialTextureSemantic.BaseColor), revision,
            new(MaterialTextureSemantic.BaseColor, _texture.AssetId));
        IDisposable? appearanceObservation = null;
        D3D12TextureCopyQualification.Hold? hold = null;
        D3D12TextureTicket ticket = default;
        var published = false;
        var workerOwnsTicket = false;
        try
        {
            if (_orbital)
            {
                appearanceObservation = BeginOrbitCopyObservation(renderer, view);
                if (!renderer.TryReadResourceDiagnostics(out var farPredecessor) ||
                    !R09TwilightPressure.TryReadOrbitTerrain(farPredecessor, predecessor.LastPresentedSequence, out _))
                    throw new InvalidOperationException("R09 Orbit Recovery could not bind the native textured copy predecessor.");
            }
            if (!PublishTarget(stream, view, target))
                throw new InvalidOperationException("R09 could not publish the independent optional copy target.");
            published = true;
            if (stream.TryRequest(view, target, Credit, out ticket) != D3D12TextureResult.Accepted)
                throw new InvalidOperationException("R09 could not reserve the independent optional copy.");
            workerOwnsTicket = true;
            hold = Qualification.Arm(ticket);
            if (stream.TryComplete(ticket, _texture) != D3D12TextureResult.Accepted)
                throw new InvalidOperationException("R09 could not transfer the optional cooked texture.");
            workerOwnsTicket = false;
            var submission = await hold.Submitted.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (submission.Ticket != ticket || submission.CopyFence == 0 || submission.WaitQueuedAt <= 0 ||
                submission.SubmittedAt < submission.WaitQueuedAt)
                throw new InvalidOperationException("R09 observed a mismatched native optional copy submission.");
            var copy = new Copy(this, renderer, stream, view, hold, submission, predecessor.LastPresentedSequence,
                before, originalTexture, appearanceObservation);
            await PollAsync(copy.HasPendingSnapshot, token).ConfigureAwait(false);
            copy.RequirePending();
            return copy;
        }
        catch (Exception failure)
        {
            hold?.Dispose();
            if (workerOwnsTicket)
                stream.CompleteWorker(ticket);
            try
            {
                if (published && RemovePressureTarget(renderer, stream, view))
                    await AwaitBaselineAsync(renderer, stream, view, before, originalTexture).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("R09 could not roll back its optional copy after the fixture failed.", failure, cleanup);
            }
            finally
            {
                appearanceObservation?.Dispose();
            }
            throw;
        }
    }

    private static IDisposable BeginOrbitCopyObservation(D3D12Renderer renderer, D3D12TextureView view)
    {
        // Keep the pinned consumer build usable for Far Horizon. Orbit Recovery requires this newer engine ABI.
        var method = typeof(D3D12Renderer).GetMethod("BeginOrbitTextureCopyObservation",
            BindingFlags.Instance | BindingFlags.Public, [typeof(D3D12TextureView)]);
        if (method?.ReturnType != typeof(IDisposable) || method.Invoke(renderer, [view]) is not IDisposable observation)
            throw new InvalidOperationException("Orbit Combined requires the Groundwork textured copy observation binding.");
        return observation;
    }

    private bool RemovePressureTarget(D3D12Renderer renderer, D3D12TextureHandoff stream, D3D12TextureView view)
    {
        if (PublishTarget(stream, view, null))
            return true;
        if (renderer.Read().Lifecycle is RendererLifecycle.Stopping or RendererLifecycle.Stopped)
            return false; // The closing render owner drains the native stream under its existing fences.
        throw new InvalidOperationException("Could not remove the R09 fixture-only texture target.");
    }

    private Task AwaitBaselineAsync(D3D12Renderer renderer, D3D12TextureHandoff stream, D3D12TextureView view,
        D3D12TextureUsage before, D3D12ResourceBalance originalTexture) =>
        PollAsync(() => renderer.Read().Lifecycle is RendererLifecycle.Stopping or RendererLifecycle.Stopped ||
            stream.ReadUsage() == before && renderer.TryReadResourceDiagnostics(out var snapshot) && IsHealthy(snapshot) &&
            snapshot.GetBalance(D3D12ResourceOwner.TextureStaging) is { Live: 0, Retained: 0 } &&
            NativeTextureBalanceRestored(snapshot, originalTexture) && MaterialsResident(stream, view), CancellationToken.None);

    private static bool NativeTextureBalanceRestored(in D3D12ResourceSnapshot snapshot, in D3D12ResourceBalance original)
    {
        var current = snapshot.GetBalance(D3D12ResourceOwner.Texture);
        return current.Live == original.Live && current.LiveBytes == original.LiveBytes &&
            current.Retained == original.Retained && current.RetainedBytes == original.RetainedBytes;
    }

    private static bool IsHealthy(D3D12ResourceSnapshot snapshot) =>
        snapshot.Custody == D3D12CustodyStatus.None && !snapshot.LedgerOverflowed && !snapshot.LedgerHasUnknownSizes;

    private static CookedTexture CookTexture()
    {
        var source = new byte[18 + CopyBytes];
        source[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(12), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(14), 1024);
        source[16] = 32;
        source[17] = 0x28;
        source.AsSpan(18).Fill(255);
        var error = TextureCooker.CookTga32(source, new(TextureColorSpace.Srgb, TextureSemantic.Color, 1),
            new(source.Length, CopyBytes, CopyBytes + 256, 1024, 2), out var texture);
        return error == TextureCookError.None && texture is not null ? texture
            : throw new InvalidOperationException($"Could not cook the R09 optional texture ({error}).");
    }

    private static async Task PollAsync(Func<bool> observe, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        while (!observe())
            await Task.Delay(5, deadline.Token).ConfigureAwait(false);
    }

    internal sealed class Copy(R09IndependentTexturePressure owner, D3D12Renderer renderer, D3D12TextureHandoff stream,
        D3D12TextureView view, D3D12TextureCopyQualification.Hold hold,
        D3D12TextureCopyQualification.Submission submission, ulong predecessor, D3D12TextureUsage before,
        D3D12ResourceBalance originalTexture, IDisposable? appearanceObservation) : IAsyncDisposable
    {
        private long _workerStarted, _workerFinished, _released;
        private D3D12ResourceSnapshot _held;
        private OrbitMaterialPredecessor? _materialPredecessor;
        private Task? _rollback;

        internal bool HasPendingSnapshot() => renderer.TryReadResourceDiagnostics(out var pending) &&
            pending.TextureCopyFence == submission.CopyFence && pending.SubmittedFence >= submission.CopyFence &&
            pending.CompletedFence < submission.CopyFence && IsHealthy(pending) &&
            pending.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes >= CopyBytes &&
            pending.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes >= CopyBytes;

        internal void RequirePending()
        {
            if (!HasPendingSnapshot() || renderer.Read().LastPresentedSequence != predecessor ||
                !owner.MaterialsResident(stream, view))
                throw new InvalidOperationException("The native optional copy or predecessor was not held while protected work executed.");
        }

        internal void Release(long started, long finished)
        {
            RequirePending();
            if (started < submission.SubmittedAt || finished <= started || !renderer.TryReadResourceDiagnostics(out _held) ||
                _held.CompletedFence >= submission.CopyFence || _held.TextureCopyFence != submission.CopyFence)
                throw new InvalidOperationException("R09 protected worker timing did not fall inside the retained copy.");
            if (appearanceObservation is not null)
            {
                if (!R09TwilightPressure.TryReadOrbitTerrain(_held, predecessor, out var terrain) ||
                    _held.RetiredPresentSequence != predecessor || _held.RetiredPresentFence != _held.PresentFence ||
                    _held.PresentFence > _held.CompletedFence || _held.PresentFence >= submission.CopyFence)
                    throw new InvalidOperationException("Orbit Recovery lost its exact retired textured predecessor while the copy was held.");
                _materialPredecessor = new(view.View.RendererEpoch, predecessor, _held.DrawFence, _held.PresentFence,
                    submission.CopyFence, terrain.ResourceUses);
            }
            _workerStarted = started;
            _workerFinished = finished;
            _released = hold.Release();
            if (_released < finished)
                throw new InvalidOperationException("R09 released optional native copy before protected worker completion.");
        }

        internal async Task ObserveRetirementAsync(VolumeWorkClass path, ulong mask, CancellationToken token)
        {
            if (_released == 0)
                throw new InvalidOperationException("The R09 optional copy was not explicitly released.");
            D3D12ResourceSnapshot retired = default;
            await PollAsync(() => renderer.TryReadResourceDiagnostics(out retired) && IsHealthy(retired) &&
                retired.CompletedFence >= retired.PresentFence && retired.RetiredPresentSequence == mask &&
                retired.PresentedSequence == mask && retired.PresentFence > submission.CopyFence && retired.PresentTimestamp >= _released &&
                retired.GetBalance(D3D12ResourceOwner.TextureStaging) is { Live: 0, Retained: 0 }, token).ConfigureAwait(false);
            var observed = Stopwatch.GetTimestamp();
            await ClearTargetAsync(requireRunning: true).ConfigureAwait(false);
            var texture = _held.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes;
            var staging = _held.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes;
            Console.WriteLine(FormattableString.Invariant(
                $"WYU R09 TEXTURECOPY {path} {view.View.RendererEpoch} {submission.Ticket.Generation} {submission.CopyFence} {submission.WaitQueuedAt} {submission.SubmittedAt} {_workerStarted} {_workerFinished} {_released} {mask} {retired.PresentFence} {retired.PresentTimestamp} {observed} {_held.CompletedFence} {retired.CompletedFence} {texture} {staging}"));
        }

        internal async Task<RecoveryEvidence> SettleFarAsync(CancellationToken token)
        {
            if (_released == 0 || _held.PresentedSequence != predecessor || _held.PresentFence == 0 ||
                _held.PresentFence >= submission.CopyFence || _held.PresentTimestamp <= 0 ||
                _held.PresentTimestamp > submission.WaitQueuedAt)
                throw new InvalidOperationException("R09 Recovery did not bind its copy to an already presented Far Horizon frame.");
            D3D12ResourceSnapshot retired = default;
            try
            {
                await PollAsync(() => renderer.TryReadResourceDiagnostics(out retired) && IsHealthy(retired) &&
                    retired.TextureCopyFence == submission.CopyFence && retired.CompletedFence >= submission.CopyFence &&
                    retired.PresentedSequence == predecessor && retired.PresentFence == _held.PresentFence &&
                    retired.GetBalance(D3D12ResourceOwner.TextureStaging) is { Live: 0, Retained: 0 }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            {
                var staging = retired.GetBalance(D3D12ResourceOwner.TextureStaging);
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"R09 Recovery retirement timed out. Predecessor sequence/fence={predecessor}/{_held.PresentFence}; observed sequence/fence={retired.PresentedSequence}/{retired.PresentFence}; expected/observed copy fence={submission.CopyFence}/{retired.TextureCopyFence}; completed fence={retired.CompletedFence}; staging live/retained={staging.Live}/{staging.Retained}; custody={retired.Custody}."), error);
            }
            await ClearTargetAsync(requireRunning: true).ConfigureAwait(false);
            return new(view.View.RendererEpoch, predecessor, _held.PresentFence, _held.PresentTimestamp,
                submission.Ticket.Generation, submission.CopyFence, submission.WaitQueuedAt, submission.SubmittedAt,
                _workerStarted, _workerFinished, _released, Stopwatch.GetTimestamp(), _held.CompletedFence,
                retired.CompletedFence, _held.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes,
                _held.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes) { OrbitMaterial = _materialPredecessor };
        }

        private async Task ClearTargetAsync(bool requireRunning)
        {
            if (_rollback is null)
                _rollback = owner.RemovePressureTarget(renderer, stream, view)
                    ? owner.AwaitBaselineAsync(renderer, stream, view, before, originalTexture) : Task.CompletedTask;
            await _rollback.ConfigureAwait(false);
            if (requireRunning && renderer.Read().Lifecycle != RendererLifecycle.Running)
                throw new InvalidOperationException("The renderer stopped before R09 could prove copy retirement.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                hold.Dispose();
                await ClearTargetAsync(requireRunning: false).ConfigureAwait(false);
            }
            finally
            {
                appearanceObservation?.Dispose();
            }
        }
    }

    internal sealed record OrbitMaterialPredecessor(ulong Epoch, ulong Frame, ulong DrawFence, ulong PresentFence,
        ulong CopyFence, int ResourceUses);

    internal sealed record RecoveryEvidence(ulong FarEpoch, ulong FarFrame, ulong FarPresentFence, long FarPresentQpc,
        ulong Ticket, ulong CopyFence, long WaitQueuedQpc, long SubmittedQpc, long WorkerStartQpc, long WorkerEndQpc,
        long ReleasedQpc, long RetiredObservationQpc, ulong HeldCompletedFence, ulong RetiredCompletedFence,
        ulong TextureRetainedBytes, ulong StagingRetainedBytes)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OrbitMaterialPredecessor? OrbitMaterial { get; init; }

        internal void Write(long farStopStarted, ulong nearEpoch, ulong mask, in D3D12ResourceSnapshot near)
        {
            if (RetiredObservationQpc > farStopStarted || nearEpoch != checked(FarEpoch + 1) || mask <= FarFrame ||
                near.PresentedSequence != mask || near.PresentTimestamp <= farStopStarted || near.PresentFence <= near.DrawFence)
                throw new InvalidOperationException("R09 Recovery lost the far retirement or distinct near mask identity.");
            var proof = new
            {
                Far = this,
                NearEpoch = nearEpoch,
                NearMask = mask,
                NearDrawFence = near.DrawFence,
                NearPresentFence = near.PresentFence,
                NearPresentQpc = near.PresentTimestamp,
            };
            Console.WriteLine($"WYU R09 RECOVERYCOPY {JsonSerializer.Serialize(proof)}");
        }
    }
}
