using System.Numerics;
using Groundwork.Core;
using Groundwork.Rendering;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

// The host serializes coordinator calls. Workers only use their reserved inputs and Core lease.
internal sealed class WalkableScene(IRuntimeOwnership ownership)
{
    internal const uint Seed = 0x57505535;
    internal const ulong GeneratorVersion = 1;
    private const int Unit = VoxelCollision.UnitsPerVoxel;
    private static readonly RegionBounds World = new(-4, 4, -2, 2);
    private static readonly ChunkCoordinate WestEdge = new(-4, 0, 0);

    private readonly WalkableRegion _region = new(World);
    private readonly List<PendingLoad> _loads = new(WalkableRegion.MaximumLoads);
    private readonly Dictionary<ulong, Coverage> _coverage = new(3);
    private VoxelMotionState _motion = new(new VoxelPose(-20 * Unit - Unit / 2, 4 * Unit, 7 * Unit + Unit / 2));
    private PendingMesh? _mesh;
    private IRenderMeshRegionReplacement? _renderer;
    private ulong _westIdentity;
    private ulong _westFingerprint;
    private bool _stopped;

    internal VoxelPose Pose => _motion.Pose;
    internal RegionUsage Usage => _region.Usage;
    internal RegionUsage HighWater => _region.HighWater;

    internal async Task<RenderMeshRegion> StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                DrainWorkers();
                if (_region.PublishTick(Pose, published: true))
                {
                    using var read = ReadPublishedRegion();
                    return Capture(read);
                }

                DispatchWorkers();
                await WaitForWorkAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The initial walkable region did not settle within 10 seconds.", error);
        }
    }

    internal void Attach(IRenderMeshRegionReplacement renderer) => _renderer = renderer;

    internal void Advance(int deltaX, int deltaZ, ulong tick, WalkableRouteSmoke? route = null, bool drawable = true)
    {
        if (_stopped)
            return;

        var renderer = _renderer ?? throw new InvalidOperationException("The walkable scene has no renderer.");
        DrainWorkers();
        var status = renderer.ReadMeshReplacement();
        RetainCoverage(status);
        using (var read = ReadPublishedRegion())
        {
            var ready = drawable && read.Anchor == _region.DesiredAnchor && read.Generation == status.LastPresentedGeneration;
            if (route is not null)
                (deltaX, deltaZ) = route.NextMovement(ready);

            var motion = drawable && _coverage.TryGetValue(status.LastPresentedGeneration, out var presented)
                ? VoxelCollision.Step(new PresentedOccupancy(read, presented), _motion, deltaX, deltaZ)
                : new VoxelMotionResult(new VoxelMotionState(Pose), Grounded: false, StreamingBlocked: true);
            _motion = motion.State;
            if (route is not null)
            {
                var fingerprint = read.TryGetLoadIdentity(WestEdge, out var identity) && identity == _westIdentity
                    ? _westFingerprint : 0;
                route.RecordPublishedTick(tick, ready, motion, read, status, HighWater, fingerprint);
            }
        }

        var previousIntent = _region.DesiredGeneration;
        var activated = _region.PublishTick(Pose, published: true);
        if (previousIntent != _region.DesiredGeneration &&
            !CheckSubmission(renderer.TryDeclareMeshGeneration(_region.DesiredGeneration)))
            return;

        if (activated)
        {
            using var read = ReadPublishedRegion();
            var meshes = Capture(read);
            if (!CheckSubmission(renderer.TrySubmitMeshRegion(read.Generation, meshes)))
                return;
        }

        RetainCoverage(renderer.ReadMeshReplacement());
        DispatchWorkers();
    }

    internal RenderScene GetScene(float yaw)
    {
        var eye = new Vector3((float)Pose.X / Unit, (float)Pose.Y / Unit + 1.6f, (float)Pose.Z / Unit);
        var forward = new Vector3(MathF.Cos(yaw), -0.12f, MathF.Sin(yaw));
        return new RenderScene(Matrix4x4.Identity, new Vector4(0.30f, 0.74f, 0.47f, 1),
            Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY), MathF.PI / 3, 0.05f, 40);
    }

    internal void Stop()
    {
        _stopped = true;
        _coverage.Clear();
        _region.Stop();
    }

    internal async Task SettleAsync(CancellationToken cancellationToken)
    {
        if (!_stopped)
            throw new InvalidOperationException("Stop the walkable scene before settling its workers.");

        while (true)
        {
            DrainWorkers();
            if (_region.IsSettled)
                return;
            await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void DrainWorkers()
    {
        for (var index = 0; index < _loads.Count;)
        {
            var load = _loads[index];
            if (!load.Task.IsCompleted)
            {
                index++;
                continue;
            }

            _loads.RemoveAt(index);
            if (load.Task.IsCompletedSuccessfully)
            {
                var snapshot = load.Task.Result;
                if (_region.CompleteLoad(load.Work, snapshot) && load.Work.Coordinate == WestEdge)
                {
                    _westIdentity = load.Work.LoadIdentity;
                    _westFingerprint = Fingerprint(snapshot!);
                }
            }
            else
            {
                _region.FailLoad(load.Work, WorkerFailure(load.Task));
            }
        }

        if (_mesh is { Task.IsCompleted: true } mesh)
        {
            _mesh = null;
            if (mesh.Task.IsCompletedSuccessfully)
                _region.CompleteMesh(mesh.Work, mesh.Task.Result);
            else
                _region.FailMesh(mesh.Work, WorkerFailure(mesh.Task));
        }

        if (!_stopped && _region.Failure is { } failure)
            throw new InvalidOperationException($"Walkable region generation {failure.Generation} failed at {failure.Coordinate}.", failure.Error);
    }

    private void DispatchWorkers()
    {
        while (_region.TryTakeLoad() is { } load)
        {
            var reservation = load;
            Task<ChunkSnapshot?>? task;
            try
            {
                task = Schedule(reservation, () => Generate(reservation));
            }
            catch (Exception error)
            {
                _region.FailLoad(reservation, error);
                throw;
            }
            if (task is null)
            {
                _region.CompleteLoad(reservation, null);
                break;
            }
            _loads.Add(new PendingLoad(reservation, task));
        }

        if (_region.TryTakeMesh() is { } mesh)
        {
            Task<RegionMeshResult?>? task;
            try
            {
                task = Schedule(mesh, mesh.Build);
            }
            catch (Exception error)
            {
                _region.FailMesh(mesh, error);
                throw;
            }
            if (task is null)
                _region.CompleteMesh(mesh, null);
            else
                _mesh = new PendingMesh(mesh, task);
        }
    }

    private Task<T>? Schedule<T>(object reservation, Func<T> build)
    {
        if (ownership.TryTrack(reservation) is not { } lease)
            return null;

        try
        {
            return Task.Run(() =>
            {
                using (lease)
                    return build();
            });
        }
        catch (TaskSchedulerException)
        {
            lease.Dispose();
            return null;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        var pending = new List<Task>(WalkableRegion.MaximumLoads + 1);
        foreach (var load in _loads)
            pending.Add(load.Task);
        if (_mesh is { } mesh)
            pending.Add(mesh.Task);

        if (pending.Count == 0)
        {
            if (_stopped)
                throw new InvalidOperationException("The walkable region retained an untracked reservation.");
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private RegionReadLease ReadPublishedRegion() => _region.TryRead() ??
        throw new InvalidOperationException("The walkable scene has no published region to read.");

    private RenderMeshRegion Capture(RegionReadLease read)
    {
        var instances = new RenderMeshInstance[read.MeshCount];
        var identities = new (ChunkCoordinate Coordinate, ulong Identity)[read.MeshCount];
        for (var i = 0; i < instances.Length; i++)
        {
            var coordinate = read.GetMeshCoordinate(i);
            if (!read.TryGetLoadIdentity(coordinate, out var identity))
                throw new InvalidOperationException($"Published mesh {coordinate} has no occupancy identity.");

            instances[i] = new(RenderMesh.Create(read.GetMeshPositions(i), read.GetMeshIndices(i)), coordinate.GetOrigin());
            identities[i] = (coordinate, identity);
        }
        _coverage.Add(read.Generation, new Coverage(identities));
        return new RenderMeshRegion(instances);
    }

    private void RetainCoverage(MeshReplacementSnapshot status)
    {
        // The render owner can present the installed generation while preparing the latest submission.
        // These three generations are the only ones that can still become observable in a subsequent sample.
        foreach (var generation in _coverage.Keys)
            if (generation != status.LastPresentedGeneration && generation != status.InstalledGeneration &&
                generation != status.DesiredGeneration)
                _coverage.Remove(generation);
    }

    private bool CheckSubmission(MeshReplacementSubmissionResult result)
    {
        if (result == MeshReplacementSubmissionResult.Accepted)
            return true;
        if (result != MeshReplacementSubmissionResult.Closed)
            throw new InvalidOperationException($"The renderer rejected walkable generation {_region.DesiredGeneration} ({result}).");

        Stop();
        return false;
    }

    private static ChunkSnapshot? Generate(RegionLoadWork work)
    {
        if (work.IsCancellationRequested)
            return null;

        const int size = ChunkSnapshot.Size;
        Span<byte> cells = stackalloc byte[ChunkSnapshot.CellCount];
        var coordinate = work.Coordinate;
        for (var z = 0; z < size; z++)
        {
            if (work.IsCancellationRequested)
                return null;
            var worldZ = coordinate.Z * size + z;
            for (var x = 0; x < size; x++)
            {
                var worldX = coordinate.X * size + x;
                var marker = unchecked(Seed ^ ((uint)worldX * 73856093u) ^ ((uint)worldZ * 19349663u));
                var height = worldX == 34 && worldZ is >= 7 and <= 9 ? 8 : worldZ >= 13 && marker % 17 == 0 ? 6 : 4;
                for (var y = 0; y < size; y++)
                    cells[x + size * (y + size * z)] = y < height ? (byte)1 : (byte)0;
            }
        }

        return ChunkSnapshot.Create(coordinate, GeneratorVersion, cells);
    }

    private static ulong Fingerprint(ChunkSnapshot snapshot)
    {
        var hash = 14695981039346656037ul;
        for (var z = 0; z < ChunkSnapshot.Size; z++)
            for (var y = 0; y < ChunkSnapshot.Size; y++)
                for (var x = 0; x < ChunkSnapshot.Size; x++)
                    hash = unchecked((hash ^ snapshot[x, y, z]) * 1099511628211ul);
        return hash;
    }

    private static Exception WorkerFailure(Task task) => task.Exception?.InnerException ??
        new InvalidOperationException("A reserved walkable region worker did not return a result.");

    private sealed record PendingLoad(RegionLoadWork Work, Task<ChunkSnapshot?> Task);
    private sealed record PendingMesh(RegionMeshWork Work, Task<RegionMeshResult?> Task);

    private sealed record Coverage((ChunkCoordinate Coordinate, ulong Identity)[] Identities);

    private sealed class PresentedOccupancy(RegionReadLease active, Coverage presented) : IVoxelOccupancy
    {
        public VoxelOccupancy GetCell(int x, int y, int z)
        {
            var coordinate = new ChunkCoordinate(ChunkOf(x), 0, ChunkOf(z));
            if (!World.Contains(coordinate))
                return active.GetCell(x, y, z);

            foreach (var (presentedCoordinate, identity) in presented.Identities)
                if (presentedCoordinate == coordinate && active.TryGetLoadIdentity(coordinate, out var current) && current == identity)
                    return active.GetCell(x, y, z);

            return VoxelOccupancy.Unavailable;
        }

        private static int ChunkOf(int cell) => cell >= 0 ? cell / ChunkSnapshot.Size : (cell + 1) / ChunkSnapshot.Size - 1;
    }
}
