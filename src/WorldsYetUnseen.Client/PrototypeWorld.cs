using System.Numerics;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal readonly record struct PrototypeTarget(Deposit? Deposit, bool IsPad);

internal static class PrototypeWorld
{
    private const int Unit = VoxelCollision.UnitsPerVoxel;
    private const int Size = ChunkSnapshot.Size;
    private const float Reach = 4;
    private const float PadFocusRadius = 0.35f;
    private static readonly ISaveTerrainGenerator Generator = new PrototypeGenerator();
    private static readonly Deposit[] OrderedDeposits = [Deposit.West, Deposit.Seam, Deposit.East];

    internal static readonly SaveWorldRecipe Recipe = new(2, 1, 0x57505537, WalkableTerrain.Recipe.Bounds);
    internal static readonly VoxelPose Spawn = WalkableTerrain.Spawn;
    internal static readonly Vector3 PadAnchor = new(-17.5f, 5, 9.5f);
    internal static ReadOnlySpan<Deposit> Deposits => OrderedDeposits;

    internal static ISaveTerrainGenerator? ResolveGenerator(uint id, uint version) =>
        id == Recipe.GeneratorId && version == Recipe.GeneratorVersion ? Generator : null;

    internal static (int X, int Y, int Z) DepositCell(Deposit deposit) => deposit switch
    {
        Deposit.West => (-10, 4, 7),
        Deposit.Seam => (0, 4, 7),
        Deposit.East => (18, 4, 7),
        _ => throw new ArgumentOutOfRangeException(nameof(deposit))
    };

    internal static DepositMask DepositBit(Deposit deposit) => deposit switch
    {
        Deposit.West => DepositMask.West,
        Deposit.Seam => DepositMask.Seam,
        Deposit.East => DepositMask.East,
        _ => throw new ArgumentOutOfRangeException(nameof(deposit))
    };

    internal static Vector3 DepositCenter(Deposit deposit)
    {
        var (x, y, z) = DepositCell(deposit);
        return new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
    }

    internal static (int X, int Y, int Z) ModuleCell(BeaconModule module) => module switch
    {
        BeaconModule.Foundation => (-19, 4, 9),
        BeaconModule.Mast => (-19, 5, 9),
        BeaconModule.SignalHead => (-19, 6, 9),
        _ => throw new ArgumentOutOfRangeException(nameof(module))
    };

    internal static ChunkSnapshot Snapshot(ChunkCoordinate coordinate, ReadOnlySpan<TerrainChunkDelta> terrain)
    {
        foreach (var chunk in terrain)
            if (chunk.Coordinate == coordinate)
                return TerrainOverlay.CreateSnapshot(Recipe, coordinate, Recipe.GeneratorVersion, chunk.Cells, Generator);

        return TerrainOverlay.CreateSnapshot(Recipe, coordinate, Recipe.GeneratorVersion, [], Generator);
    }

    internal static TerrainChunkDelta[] EffectiveTerrain(PrototypeProgress progress) => Terrain(progress, includeModules: true);

    internal static TerrainChunkDelta[] SavedTerrain(PrototypeProgress progress) => Terrain(progress, includeModules: false);

    private static TerrainChunkDelta[] Terrain(PrototypeProgress progress, bool includeModules)
    {
        // All sockets are in chunk -2. Each deposit is in a distinct chunk to its east.
        // Traversal in this order produces canonical signed chunk and cell ordering.
        var changes = new List<TerrainChunkDelta>(4);
        if (includeModules && progress.InstalledModules > 0)
        {
            var chunk = ChunkOf(ModuleCell(BeaconModule.Foundation));
            Span<TerrainCellDelta> sockets = stackalloc TerrainCellDelta[3];
            for (var index = 0; index < progress.InstalledModules; index++)
                sockets[index] = new TerrainCellDelta(LocalIndex(ModuleCell((BeaconModule)index), chunk), 1);

            changes.Add(new TerrainChunkDelta(chunk, sockets[..progress.InstalledModules]));
        }

        foreach (var deposit in Deposits)
        {
            if ((progress.MinedDeposits & DepositBit(deposit)) == 0)
                continue;

            var cell = DepositCell(deposit);
            var chunk = ChunkOf(cell);
            changes.Add(new TerrainChunkDelta(chunk, [new TerrainCellDelta(LocalIndex(cell, chunk), 0)]));
        }

        return changes.ToArray();
    }

    internal static Vector3 Eye(VoxelPose pose) =>
        new((float)pose.X / Unit, (float)pose.Y / Unit + 1.6f, (float)pose.Z / Unit);

    internal static Vector3 Direction(float yaw, float pitch)
    {
        var (sinYaw, cosYaw) = MathF.SinCos(yaw);
        var (sinPitch, cosPitch) = MathF.SinCos(pitch);
        return new Vector3(cosYaw * cosPitch, sinPitch, sinYaw * cosPitch);
    }

    internal static PrototypeTarget Target(IVoxelOccupancy occupancy, VoxelPose pose, float yaw, float pitch)
    {
        if (!float.IsFinite(yaw) || !float.IsFinite(pitch))
            return default;

        var eye = Eye(pose);
        var aim = Direction(yaw, pitch);
        var blocker = FirstBlocked(occupancy, eye, aim, Reach);
        var toPad = PadAnchor - eye;
        var padDistance = toPad.Length();
        var padAlongAim = Vector3.Dot(toPad, aim);
        if (padDistance <= Reach && padAlongAim > 0 &&
            Vector3.DistanceSquared(toPad, aim * padAlongAim) <= PadFocusRadius * PadFocusRadius &&
            (blocker is null || padAlongAim < blocker.Value.Distance) &&
            FirstBlocked(occupancy, eye, toPad / padDistance, padDistance) is null)
            return new PrototypeTarget(null, true);

        if (blocker is { Occupancy: VoxelOccupancy.Solid } hit)
            foreach (var deposit in Deposits)
                if (DepositCell(deposit) == hit.Cell)
                    return new PrototypeTarget(deposit, false);

        return default;
    }

    internal static bool IsSocketOccupied(BeaconModule module, VoxelPose pose)
    {
        var (x, y, z) = ModuleCell(module);
        const int half = VoxelCollision.HalfWidth;
        return pose.X + half > x * Unit && pose.X - half < (x + 1) * Unit &&
            pose.Y + VoxelCollision.Height > y * Unit && pose.Y < (y + 1) * Unit &&
            pose.Z + half > z * Unit && pose.Z - half < (z + 1) * Unit;
    }

    private static RayHit? FirstBlocked(IVoxelOccupancy occupancy, Vector3 eye, Vector3 aim, float range)
    {
        var length = Math.Sqrt((double)aim.X * aim.X + (double)aim.Y * aim.Y + (double)aim.Z * aim.Z);
        var x = new RayAxis(eye.X, aim.X / length);
        var y = new RayAxis(eye.Y, aim.Y / length);
        var z = new RayAxis(eye.Z, aim.Z / length);
        var distance = 0d;
        while (distance <= range)
        {
            var cell = occupancy.GetCell(x.Cell, y.Cell, z.Cell);
            // Unloaded coverage cannot authorize an action behind it.
            if (cell != VoxelOccupancy.Air)
                return new RayHit((x.Cell, y.Cell, z.Cell), distance, cell);

            distance = Math.Min(x.Next, Math.Min(y.Next, z.Next));
            // Cross all tied grid planes together. Cells touched only at the crossing
            // point do not lie along the ray and cannot win focus.
            if (x.Next == distance)
                x.Step();
            if (y.Next == distance)
                y.Step();
            if (z.Next == distance)
                z.Step();
        }

        return null;
    }

    internal static ChunkCoordinate ChunkOf((int X, int Y, int Z) cell) =>
        new(FloorChunk(cell.X), FloorChunk(cell.Y), FloorChunk(cell.Z));

    private static int FloorChunk(int cell) => (int)Math.Floor((double)cell / Size);

    internal static ushort LocalIndex((int X, int Y, int Z) cell, ChunkCoordinate chunk) =>
        (ushort)(cell.X - chunk.X * Size + Size * (cell.Y - chunk.Y * Size + Size * (cell.Z - chunk.Z * Size)));

    private readonly record struct RayHit((int X, int Y, int Z) Cell, double Distance, VoxelOccupancy Occupancy);

    private struct RayAxis
    {
        private readonly int _step;
        private readonly double _stride;

        internal RayAxis(float origin, double direction)
        {
            Cell = (int)MathF.Floor(origin);
            _step = Math.Sign(direction);
            if (_step < 0 && Cell == origin)
                Cell--;

            _stride = direction == 0 ? double.PositiveInfinity : Math.Abs(1d / direction);
            Next = _step switch
            {
                1 => (Cell + 1d - origin) * _stride,
                -1 => (origin - Cell) * _stride,
                _ => double.PositiveInfinity
            };
        }

        internal int Cell { get; private set; }
        internal double Next { get; private set; }

        internal void Step()
        {
            Cell += _step;
            Next += _stride;
        }
    }

    private sealed class PrototypeGenerator : ISaveTerrainGenerator
    {
        public void Generate(SaveWorldRecipe recipe, ChunkCoordinate coordinate, Span<byte> cells)
        {
            if (recipe.GeneratorId != Recipe.GeneratorId || recipe.GeneratorVersion != Recipe.GeneratorVersion)
                throw new ArgumentException("The saved recipe does not identify the prototype generator.", nameof(recipe));

            WalkableTerrain.GenerateSurface(recipe.Seed, coordinate, cells);
            foreach (var deposit in Deposits)
            {
                var cell = DepositCell(deposit);
                if (ChunkOf(cell) == coordinate)
                    cells[LocalIndex(cell, coordinate)] = 1;
            }

            // A pair of posts marks the west approach without filling any build socket.
            (int X, int Y, int Z) firstPost = (-20, 4, 8);
            (int X, int Y, int Z) secondPost = (-20, 4, 10);
            if (ChunkOf(firstPost) == coordinate)
            {
                cells[LocalIndex(firstPost, coordinate)] = 1;
                cells[LocalIndex(secondPost, coordinate)] = 1;
            }
        }
    }
}
