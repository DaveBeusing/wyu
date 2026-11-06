using System.Runtime.ExceptionServices;
using Groundwork.Core;
using Groundwork.Rendering;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

// Only the simulation coordinator calls these methods. The worker owns its
// captured snapshots and runtime lease until it returns.
internal sealed class TerrainEdits(TerrainScene scene, IRenderMeshReplacement renderer, IRuntimeOwnership ownership,
    Func<ulong, Task>? beforeCompletion = null)
{
    private ulong _desired = 1;
    private BuildRequest? _pending;
    private Task<BuildResult>? _active;
    private bool _stopped;

    internal ulong LastDiscardedGeneration { get; private set; }

    internal void Toggle()
    {
        if (_stopped)
            return;

        var generation = checked(_desired + 1);
        var (low, high) = scene.ToggleBoundary();
        var outcome = renderer.TryDeclareMeshGeneration(generation);
        if (outcome == MeshReplacementSubmissionResult.Closed)
        {
            Stop();
            return;
        }
        if (outcome != MeshReplacementSubmissionResult.Accepted)
            throw new InvalidOperationException($"Terrain mesh intent {generation} was rejected ({outcome}).");

        _desired = generation;
        _pending = new BuildRequest(generation, low, high);
    }

    internal void Advance()
    {
        if (_stopped || _active is { IsCompleted: false })
            return;

        if (_active is { } completed)
        {
            _active = null;
            Submit(completed.GetAwaiter().GetResult());
        }

        if (_pending is { } request && ownership.TryTrack(request) is { } lease)
        {
            _pending = null;
            var completionHook = beforeCompletion;
            try
            {
                _active = Task.Run(() => BuildAsync(request, lease, completionHook));
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    internal void Stop()
    {
        _stopped = true;
        _pending = null;
        _active = null;
    }

    private void Submit(BuildResult result)
    {
        if (result.Generation != _desired)
        {
            LastDiscardedGeneration = result.Generation;
            return;
        }

        result.Error?.Throw();
        if (!scene.Matches(result.Chunks!))
        {
            LastDiscardedGeneration = result.Generation;
            return;
        }

        var outcome = renderer.TrySubmitMeshPair(result.Generation, result.Meshes);
        if (outcome == MeshReplacementSubmissionResult.Closed)
            Stop();
        else if (outcome != MeshReplacementSubmissionResult.Accepted)
            throw new InvalidOperationException($"Terrain mesh generation {result.Generation} was rejected ({outcome}).");
    }

    private static async Task<BuildResult> BuildAsync(BuildRequest request, IRuntimeWorkLease lease, Func<ulong, Task>? completionHook)
    {
        // Represent worker failures in the result so stopping can discard the
        // task without leaving an unobserved fault.
        try
        {
            using (lease)
            {
                var chunks = ChunkMesher.BuildPair(request.Low, request.High);
                var meshes = TerrainScene.ToRenderPair(chunks);
                if (completionHook is not null)
                    await completionHook(request.Generation).ConfigureAwait(false);
                return new BuildResult(request.Generation, chunks, meshes, null);
            }
        }
        catch (Exception error)
        {
            return new BuildResult(request.Generation, null, null, ExceptionDispatchInfo.Capture(error));
        }
    }

    private sealed record BuildRequest(ulong Generation, ChunkSnapshot Low, ChunkSnapshot High);
    private sealed record BuildResult(ulong Generation, ChunkMeshPair? Chunks, RenderMeshPair? Meshes, ExceptionDispatchInfo? Error);
}
