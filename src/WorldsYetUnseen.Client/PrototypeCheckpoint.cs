using System.Buffers.Binary;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal readonly record struct PrototypeCheckpoint(VoxelPose Pose, PrototypeProgress Progress)
{
    internal const uint Schema = 2;
    private const int PayloadSize = 20;

    internal SaveCheckpoint Create(SaveFrameIdentity identity)
    {
        Span<byte> payload = stackalloc byte[PayloadSize];
        BinaryPrimitives.WriteInt32LittleEndian(payload, Pose.X);
        BinaryPrimitives.WriteInt32LittleEndian(payload[4..], Pose.Y);
        BinaryPrimitives.WriteInt32LittleEndian(payload[8..], Pose.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[12..], (uint)Progress.Stone);
        payload[16] = (byte)((1 << Progress.InstalledModules) - 1);
        payload[17] = Progress.BeaconOnline ? (byte)1 : (byte)0;
        payload[18..].Clear();
        return new SaveCheckpoint(identity, PrototypeWorld.Recipe, PrototypeWorld.SavedTerrain(Progress), Schema, payload);
    }

    internal static SaveGameValidation Validate(SaveCheckpoint checkpoint) => Decode(checkpoint, out _);

    internal static SaveGameValidation Decode(SaveCheckpoint checkpoint, out PrototypeCheckpoint state)
    {
        state = default;
        if (checkpoint.Recipe != PrototypeWorld.Recipe || checkpoint.GameSchemaVersion != Schema)
            return SaveGameValidation.Unsupported;

        var payload = checkpoint.GamePayload;
        if (payload.Length != PayloadSize || payload[17] > 1 || payload[18] != 0 || payload[19] != 0)
            return SaveGameValidation.Malformed;

        var installedModules = payload[16] switch
        {
            0b000 => 0,
            0b001 => 1,
            0b011 => 2,
            0b111 => 3,
            _ => -1
        };
        if (!TryReadDeposits(checkpoint.Terrain, out var minedDeposits) ||
            !PrototypeProgress.TryRestore(minedDeposits, installedModules, payload[17] == 1, out var progress) ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]) != (uint)progress.Stone)
            return SaveGameValidation.Malformed;

        var pose = new VoxelPose(BinaryPrimitives.ReadInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[4..]), BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
        if (!IsSupported(pose, progress))
            return SaveGameValidation.Malformed;

        state = new PrototypeCheckpoint(pose, progress);
        return SaveGameValidation.Valid;
    }

    private static bool TryReadDeposits(ReadOnlySpan<TerrainChunkDelta> terrain, out DepositMask minedDeposits)
    {
        minedDeposits = DepositMask.None;
        foreach (var deposit in PrototypeWorld.Deposits)
        {
            if (terrain.IsEmpty)
                break;
            var cell = PrototypeWorld.DepositCell(deposit);
            var coordinate = PrototypeWorld.ChunkOf(cell);
            var chunk = terrain[0];
            if (chunk.Coordinate != coordinate)
                continue;
            if (chunk.Cells is not [var removal] ||
                removal.Index != PrototypeWorld.LocalIndex(cell, coordinate) || removal.Occupancy != 0)
                return false;
            minedDeposits |= PrototypeWorld.DepositBit(deposit);
            terrain = terrain[1..];
        }

        return terrain.IsEmpty;
    }

    private static bool IsSupported(VoxelPose pose, PrototypeProgress progress)
    {
        try
        {
            var result = VoxelCollision.Sweep(new SavedOccupancy(progress), pose, 0, -1, 0);
            return result.Grounded && !result.StreamingBlocked && result.Pose == pose;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class SavedOccupancy(PrototypeProgress progress) : IVoxelOccupancy
    {
        private readonly TerrainChunkDelta[] _terrain = PrototypeWorld.EffectiveTerrain(progress);
        private readonly Dictionary<ChunkCoordinate, ChunkSnapshot> _chunks = [];

        public VoxelOccupancy GetCell(int x, int y, int z)
        {
            var coordinate = PrototypeWorld.ChunkOf((x, 0, z));
            if (!PrototypeWorld.Recipe.Bounds.Contains(coordinate))
                return VoxelOccupancy.Unavailable;
            if (y < 0)
                return VoxelOccupancy.Solid;
            if (y >= ChunkSnapshot.Size)
                return VoxelOccupancy.Air;

            if (!_chunks.TryGetValue(coordinate, out var chunk))
            {
                chunk = PrototypeWorld.Snapshot(coordinate, _terrain);
                _chunks.Add(coordinate, chunk);
            }

            return chunk[x - coordinate.X * ChunkSnapshot.Size, y, z - coordinate.Z * ChunkSnapshot.Size] == 0
                ? VoxelOccupancy.Air : VoxelOccupancy.Solid;
        }
    }
}
