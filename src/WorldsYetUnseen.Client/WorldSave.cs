using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

// The coordinator captures one immutable checkpoint after a published tick.
// Only the tracked worker touches the slot once the checkpoint is queued.
internal sealed class WorldSave(SaveSlot slot, IRuntimeOwnership ownership, Guid lineage)
{
    private SaveWork? _active;
    private SaveCheckpoint? _retry;
    private bool _stopped;

    internal VoxelPose Pose { get; private set; } = WalkableTerrain.Spawn;
    internal ReadOnlyMemory<TerrainChunkDelta> Terrain { get; private set; }
    internal ReadOnlyMemory<SavedItem> Inventory { get; private set; }
    internal ulong Generation { get; private set; }
    internal SaveRecoveryEvidence? Recovery { get; private set; }

    internal static Task<WorldSave> OpenAsync(string directory, IRuntimeOwnership ownership, CancellationToken ct,
        Action? beforeResolve = null)
    {
        ct.ThrowIfCancellationRequested();
        var slot = new SaveSlot(directory, (id, version) =>
        {
            beforeResolve?.Invoke();
            return id == WalkableTerrain.Recipe.GeneratorId && version == WalkableTerrain.Recipe.GeneratorVersion
                ? WalkableTerrain.Generator : null;
        }, ValidateCheckpoint);
        var lease = ownership.TryTrack(slot)
            ?? throw new InvalidOperationException("Core could not track the checkpoint load.");
        return Task.Run(() =>
        {
            using (lease)
            {
                ct.ThrowIfCancellationRequested();
                var result = slot.Load();
                ct.ThrowIfCancellationRequested();
                if (result.Status == SaveSlotStatus.Empty)
                    return new WorldSave(slot, ownership, Guid.NewGuid());
                if (result.Status != SaveSlotStatus.Loaded || result.Checkpoint is not { } checkpoint)
                    throw new InvalidOperationException($"Cannot resume the save slot ({result.Status}).", result.Error);

                var world = new WorldSave(slot, ownership, checkpoint.Identity.WorldLineageId) { Recovery = result.Recovery };
                world.Apply(checkpoint);
                return world;
            }
        });
    }

    internal bool RequestSave(VoxelPose pose, ReadOnlySpan<TerrainChunkDelta> terrain) =>
        RequestSave(pose, terrain, Inventory.Span);

    internal bool RequestSave(VoxelPose pose, ReadOnlySpan<TerrainChunkDelta> terrain, ReadOnlySpan<SavedItem> inventory)
    {
        if (_stopped || _active is not null || Generation == ulong.MaxValue)
            return false;

        var expectedGeneration = Generation;
        var checkpoint = _retry ?? new SaveCheckpoint(new(expectedGeneration + 1, lineage, Guid.NewGuid()),
            WalkableTerrain.Recipe, terrain, GameSavePayload.Schema, GameSavePayload.Encode(pose, inventory));
        if (ownership.TryTrack(checkpoint) is not { } lease)
            return false;

        var cancellation = new CancellationTokenSource();
        Task<SaveSlotResult> worker;
        try
        {
            worker = Task.Run(() =>
            {
                using (lease)
                    return slot.Save(expectedGeneration, checkpoint, cancellation.Token);
            });
        }
        catch
        {
            cancellation.Dispose();
            lease.Dispose();
            throw;
        }

        _active = new(checkpoint, cancellation, worker);
        if (_retry is not null)
            Console.WriteLine($"Retrying save generation {checkpoint.Identity.Generation} with its original checkpoint.");
        return true;
    }

    internal void Advance()
    {
        if (_active is { Worker.IsCompleted: true } active)
            Complete(active);
    }

    internal void Stop()
    {
        _stopped = true;
        _active?.Cancellation.Cancel();
    }

    internal async Task SettleAsync(CancellationToken ct)
    {
        if (_active is not null)
            await WaitForSaveAsync(ct).ConfigureAwait(false);
    }

    internal async Task<SaveSlotResult> WaitForSaveAsync(CancellationToken ct)
    {
        var active = _active ?? throw new InvalidOperationException("No save is in progress.");
        try
        {
            await active.Worker.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (active.Worker.IsFaulted)
        {
            // Consume the fault through the normal completion path so its resources are released.
        }

        return Complete(active);
    }

    private SaveSlotResult Complete(SaveWork active)
    {
        _active = null;
        active.Cancellation.Dispose();
        var result = active.Worker.GetAwaiter().GetResult();
        if (result.Status is SaveSlotStatus.Committed or SaveSlotStatus.AlreadyCommitted)
        {
            Apply(active.Checkpoint);
            _retry = null;
            Console.WriteLine($"Saved generation {Generation} ({result.Status}).");
        }
        else
        {
            if (result.Status == SaveSlotStatus.Indeterminate)
                _retry = active.Checkpoint;
            Console.WriteLine($"Save generation {active.Checkpoint.Identity.Generation} ended with {result.Status}.");
        }

        return result;
    }

    private void Apply(SaveCheckpoint checkpoint)
    {
        var status = GameSavePayload.Decode(checkpoint.GamePayload, out var pose, out var inventory);
        if (status != SaveGameValidation.Valid)
            throw new InvalidDataException("A verified checkpoint contains an invalid game payload.");

        Pose = pose;
        Terrain = checkpoint.Terrain.ToArray();
        Inventory = inventory;
        Generation = checkpoint.Identity.Generation;
    }

    private static SaveGameValidation ValidateCheckpoint(SaveCheckpoint checkpoint)
    {
        if (checkpoint.Recipe != WalkableTerrain.Recipe || checkpoint.GameSchemaVersion != GameSavePayload.Schema)
            return SaveGameValidation.Unsupported;

        var status = GameSavePayload.Decode(checkpoint.GamePayload, out var pose, out _);
        if (status != SaveGameValidation.Valid)
            return status;

        try
        {
            var collision = VoxelCollision.Sweep(new SavedOccupancy(checkpoint), pose, 0, -1, 0);
            return collision.Grounded && !collision.StreamingBlocked && collision.Pose == pose
                ? SaveGameValidation.Valid : SaveGameValidation.Malformed;
        }
        catch (ArgumentException)
        {
            return SaveGameValidation.Malformed;
        }
    }

    private sealed record SaveWork(SaveCheckpoint Checkpoint, CancellationTokenSource Cancellation, Task<SaveSlotResult> Worker);

    private sealed class SavedOccupancy(SaveCheckpoint checkpoint) : IVoxelOccupancy
    {
        private readonly Dictionary<ChunkCoordinate, ChunkSnapshot> _chunks = [];

        public VoxelOccupancy GetCell(int x, int y, int z)
        {
            const int size = ChunkSnapshot.Size;
            var coordinate = new ChunkCoordinate(FloorChunk(x), 0, FloorChunk(z));
            if (!checkpoint.Recipe.Bounds.Contains(coordinate) || y < 0)
                return VoxelOccupancy.Solid;
            if (y >= size)
                return VoxelOccupancy.Air;

            if (!_chunks.TryGetValue(coordinate, out var chunk))
            {
                chunk = WalkableTerrain.Snapshot(coordinate, checkpoint.Terrain);
                _chunks.Add(coordinate, chunk);
            }

            return chunk[x - coordinate.X * size, y, z - coordinate.Z * size] != 0
                ? VoxelOccupancy.Solid : VoxelOccupancy.Air;
        }

        private static int FloorChunk(int cell) => cell >= 0 ? cell / ChunkSnapshot.Size : (cell + 1) / ChunkSnapshot.Size - 1;
    }
}
