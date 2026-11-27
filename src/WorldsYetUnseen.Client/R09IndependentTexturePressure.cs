using System.Buffers.Binary;
using System.Diagnostics;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

// Only the explicit, installed Far Horizon fixture creates this stream. It supplies no scene material.
internal sealed class R09IndependentTexturePressure
{
    private const int CopyBytes = 512 * 1024 * 4;
    private static readonly D3D12TextureReservation Credit = new(CopyBytes + 256, CopyBytes, CopyBytes + 256, CopyBytes, CopyBytes);
    private readonly CookedTexture _texture = CookTexture();
    private D3D12TextureHandoff? _stream;
    private ulong _paletteRevision;

    internal static bool Requested => Environment.GetEnvironmentVariable("WYU_R09_INDEPENDENT_TEXTURE_COPY") == "1";
    internal D3D12TextureCopyQualification Qualification { get; } = new();

    internal void Attach(D3D12Renderer renderer)
    {
        _stream = new(new(1, 1, CopyBytes + 4096, CopyBytes + 4096, CopyBytes + 4096, CopyBytes + 65536, CopyBytes + 65536, 1));
        renderer.AttachTextureHandoff(_stream);
    }

    internal async Task<Copy> BeginAsync(D3D12Renderer renderer, Guid world, VolumePublicationInfo previous, CancellationToken token)
    {
        var stream = _stream ?? throw new InvalidOperationException("The R09 optional texture stream is unavailable.");
        var predecessor = renderer.Read();
        if (predecessor is not { Surface: RenderSurfaceState.Drawable, LastPresentedSequence: > 0 } || stream.ReadUsage() != default)
            throw new InvalidOperationException("R09 texture pressure requires a drawable predecessor and an idle texture stream.");
        var diagnostics = renderer.ReadDiagnostics();
        if (!diagnostics.DebugLayerEnabled || !diagnostics.DredEnabled)
            throw new InvalidOperationException("R09 texture pressure requires the native debug layer and DRED.");

        var view = new D3D12TextureView(world, previous.View, previous.TerrainRevision, previous.Generation);
        var revision = checked(++_paletteRevision);
        var target = new D3D12TextureTarget(new(ushort.MaxValue, MaterialTextureSemantic.BaseColor), revision,
            new(MaterialTextureSemantic.BaseColor, _texture.AssetId));
        if (!stream.Publish(view, revision, [target]) || stream.TryRequest(view, target, Credit, out var ticket) != D3D12TextureResult.Accepted)
            throw new InvalidOperationException("R09 could not reserve the independent optional copy.");
        D3D12TextureCopyQualification.Hold? hold = null;
        var transferred = false;
        try
        {
            hold = Qualification.Arm(ticket);
            if (stream.TryComplete(ticket, _texture) != D3D12TextureResult.Accepted)
                throw new InvalidOperationException("R09 could not transfer the optional cooked texture.");
            transferred = true;
            var submission = await hold.Submitted.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (submission.Ticket != ticket || submission.CopyFence == 0 || submission.WaitQueuedAt <= 0 ||
                submission.SubmittedAt < submission.WaitQueuedAt)
                throw new InvalidOperationException("R09 observed a mismatched native optional copy submission.");
            var copy = new Copy(this, renderer, stream, view, hold, submission, predecessor.LastPresentedSequence);
            await PollAsync(copy.HasPendingSnapshot, token).ConfigureAwait(false);
            copy.RequirePending();
            return copy;
        }
        catch
        {
            hold?.Dispose();
            if (!transferred)
                stream.CompleteWorker(ticket);
            throw;
        }
    }

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
        D3D12TextureCopyQualification.Submission submission, ulong predecessor) : IDisposable
    {
        private long _workerStarted, _workerFinished, _released;
        private D3D12ResourceSnapshot _held;

        internal bool HasPendingSnapshot() => renderer.TryReadResourceDiagnostics(out var pending) &&
            pending.TextureCopyFence == submission.CopyFence && pending.SubmittedFence >= submission.CopyFence &&
            pending.CompletedFence < submission.CopyFence && IsHealthy(pending) &&
            pending.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes >= CopyBytes &&
            pending.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes >= CopyBytes;

        internal void RequirePending()
        {
            if (!HasPendingSnapshot() || renderer.Read().LastPresentedSequence != predecessor)
                throw new InvalidOperationException("The native optional copy or predecessor was not held while protected work executed.");
        }

        internal void Release(long started, long finished)
        {
            RequirePending();
            if (started < submission.SubmittedAt || finished <= started || !renderer.TryReadResourceDiagnostics(out _held) ||
                _held.CompletedFence >= submission.CopyFence || _held.TextureCopyFence != submission.CopyFence)
                throw new InvalidOperationException("R09 protected worker timing did not fall inside the retained copy.");
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
            if (!stream.Publish(view, checked(++owner._paletteRevision), []))
                throw new InvalidOperationException("Could not remove the R09 fixture-only texture target.");
            await PollAsync(() => stream.ReadUsage() == default && renderer.TryReadResourceDiagnostics(out var snapshot) &&
                snapshot.GetBalance(D3D12ResourceOwner.Texture) is { Live: 0, Retained: 0 }, token).ConfigureAwait(false);
            var texture = _held.GetBalance(D3D12ResourceOwner.Texture).RetainedBytes;
            var staging = _held.GetBalance(D3D12ResourceOwner.TextureStaging).RetainedBytes;
            Console.WriteLine(FormattableString.Invariant(
                $"WYU R09 TEXTURECOPY {path} {view.View.RendererEpoch} {submission.Ticket.Generation} {submission.CopyFence} {submission.WaitQueuedAt} {submission.SubmittedAt} {_workerStarted} {_workerFinished} {_released} {mask} {retired.PresentFence} {retired.PresentTimestamp} {observed} {_held.CompletedFence} {retired.CompletedFence} {texture} {staging}"));
        }

        public void Dispose() => hold.Dispose();

        private static bool IsHealthy(D3D12ResourceSnapshot snapshot) =>
            snapshot.Custody == D3D12CustodyStatus.None && !snapshot.LedgerOverflowed && !snapshot.LedgerHasUnknownSizes;
    }
}
