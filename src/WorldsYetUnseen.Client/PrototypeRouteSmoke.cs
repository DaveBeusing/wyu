using System.Numerics;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed class PrototypeRouteSmoke
{
    private const int Unit = VoxelCollision.UnitsPerVoxel;
    private const int Speed = Unit / 8;
    private const int Floor = 4 * Unit;
    private static readonly int[] Planes = [-16, 0, 16];
    private static readonly (int Plane, bool Eastward)[] ExpectedCrossings =
        [(-16, true), (0, true), (16, true), (16, false), (0, false), (-16, false)];
    private static readonly Step[] Route =
    [
        new(-20.5f, 7.5f, Check.OutOfReach),
        new(-11.5f, 7.5f, Check.MineWest),
        new(-1.5f, 7.5f, Check.MineSeam),
        new(-1.5f, 7.5f, Check.ClearedSeam),
        new(16.5f, 7.5f, Check.MineEast),
        new(16.5f, 11.5f),
        new(48.5f, 11.5f, Check.UnloadedSeam),
        new(1.5f, 11.5f, Check.ClearedSeam),
        new(-18.5f, 11.5f),
        new(-18.5f, 9.5f, Check.OccupiedFoundation),
        new(-16.5f, 9.5f, Check.BuildFoundation),
        new(-16.5f, 9.5f, Check.BuildMast),
        new(-16.5f, 9.5f, Check.BuildSignalHead),
        new(-16.5f, 11.5f),
        new(-21, 11.5f),
        new(-21, 9.5f, Check.OccludedPad),
        new(-21, 11.5f),
        new(-16.5f, 11.5f),
        new(-16.5f, 9.5f, Check.Activate),
    ];

    private readonly List<(int Plane, bool Eastward)> _crossings = [];
    private VoxelPose _previousPose = PrototypeWorld.Spawn;
    private VoxelPose _expectedPose = PrototypeWorld.Spawn;
    private PrototypeProgress _progressBeforeAction;
    private PrototypeProgress _expectedProgress;
    private bool _interactionIssued;
    private bool _sawPendingTerrain;
    private int _step;
    private int _movementTicks;
    private int _holdTicks;
    private int _peakSnapshots;
    private int _peakLoads;
    private int _peakMeshes;
    private long _peakMeshBytes;

    internal bool Completed => _step == Route.Length;

    internal (int X, int Z, float Yaw, float Pitch, bool Interact) NextInput(
        WalkableScene scene, PrototypeProgress progress, bool ready)
    {
        if (Completed)
            return default;

        var pose = scene.Pose;
        ObserveMovement(pose);
        ObserveBudget(scene.HighWater);

        var step = Route[_step];
        var aim = step.Check switch
        {
            Check.OutOfReach or Check.MineWest => PrototypeWorld.DepositCenter(Deposit.West),
            Check.MineSeam or Check.ClearedSeam => PrototypeWorld.DepositCenter(Deposit.Seam),
            Check.MineEast => PrototypeWorld.DepositCenter(Deposit.East),
            Check.None or Check.UnloadedSeam => PrototypeWorld.Eye(pose) +
                new Vector3(step.X - (float)pose.X / Unit, 0, step.Z - (float)pose.Z / Unit),
            _ => PrototypeWorld.PadAnchor
        };
        var (yaw, pitch) = Aim(pose, aim);
        if (!ready)
        {
            if (_interactionIssued && scene.TerrainChangePending)
            {
                Require(progress == _progressBeforeAction, "game progress changed before the staged terrain presented");
                _sawPendingTerrain = true;
            }

            _holdTicks++;
            return (0, 0, yaw, pitch, false);
        }

        using var read = scene.TryReadPresented()
            ?? throw Failure("ready state has no matching presented collision and mesh data");
        if (_interactionIssued)
        {
            Require(progress == _expectedProgress, $"unexpected rule result after {step.Check}");
            var changesTerrain = step.Check is Check.MineWest or Check.MineSeam or Check.MineEast
                or Check.BuildFoundation or Check.BuildMast or Check.BuildSignalHead;
            Require(changesTerrain == _sawPendingTerrain, $"unexpected terrain staging after {step.Check}");
            if (step.Check is Check.BuildFoundation or Check.BuildMast or Check.BuildSignalHead)
            {
                var (x, y, z) = PrototypeWorld.ModuleCell((BeaconModule)(progress.InstalledModules - 1));
                Require(read.GetCell(x, y, z) == VoxelOccupancy.Solid, $"the installed {step.Check} has no collision");
            }

            _interactionIssued = false;
            _step++;
            Console.WriteLine($"WP07 {step.Check}: pose={pose}, stone={progress.Stone}, modules={progress.InstalledModules}, online={progress.BeaconOnline}.");
            if (Completed)
                VerifyCompletion(progress);
            return (0, 0, yaw, pitch, false);
        }

        var dx = (int)(step.X * Unit) - pose.X;
        var dz = (int)(step.Z * Unit) - pose.Z;
        Require(dx == 0 || dz == 0, $"waypoint {_step} is not axis aligned");
        if (dx != 0 || dz != 0)
        {
            _movementTicks++;
            dx = Math.Clamp(dx, -Speed, Speed);
            dz = Math.Clamp(dz, -Speed, Speed);
            _expectedPose = pose with { X = pose.X + dx, Z = pose.Z + dz };
            return (dx, dz, yaw, pitch, false);
        }

        var target = PrototypeWorld.Target(read, pose, yaw, pitch);
        _expectedProgress = progress;
        switch (step.Check)
        {
            case Check.None:
                _step++;
                return (0, 0, yaw, pitch, false);
            case Check.OutOfReach:
                Require(Vector3.Distance(PrototypeWorld.Eye(pose), aim) > 4 && target == default,
                    "distant west deposit was admitted from spawn");
                break;
            case Check.MineWest:
            case Check.MineSeam:
            case Check.MineEast:
                var deposit = step.Check switch
                {
                    Check.MineWest => Deposit.West,
                    Check.MineSeam => Deposit.Seam,
                    _ => Deposit.East
                };
                Require(target.Deposit == deposit, $"visible {deposit} deposit did not focus");
                Require((scene.ReadPresentedDepositColors() & PrototypeWorld.DepositBit(deposit)) != 0,
                    $"the presented region did not color the {deposit} deposit");
                if (deposit == Deposit.Seam)
                    VerifySeam(read, occupied: true);
                Require(progress.Mine(deposit, out _expectedProgress) == ActionResult.Applied,
                    $"could not predict mining {deposit}");
                break;
            case Check.ClearedSeam:
                VerifySeam(read, occupied: false);
                Require((scene.ReadPresentedDepositColors() & DepositMask.Seam) == 0,
                    "the presented region retained the removed seam deposit color");
                _step++;
                return (0, 0, yaw, pitch, false);
            case Check.UnloadedSeam:
                Require(!read.TryGetLoadIdentity(new ChunkCoordinate(0, 0, 0), out _),
                    "edited seam never left the resident footprint");
                _step++;
                return (0, 0, yaw, pitch, false);
            case Check.OccupiedFoundation:
                Require(progress.Stone == 3 && target.IsPad, "three stone did not reach the pad");
                Require(PrototypeWorld.IsSocketOccupied(BeaconModule.Foundation, pose),
                    "player should obstruct the foundation socket");
                var clearPose = pose with { X = -21 * Unit };
                var (clearYaw, clearPitch) = Aim(clearPose, aim);
                Require(PrototypeWorld.Target(read, clearPose, clearYaw, clearPitch).IsPad,
                    "west approach to pad was not visible before construction");
                break;
            case Check.BuildFoundation:
            case Check.BuildMast:
            case Check.BuildSignalHead:
                var module = step.Check switch
                {
                    Check.BuildFoundation => BeaconModule.Foundation,
                    Check.BuildMast => BeaconModule.Mast,
                    _ => BeaconModule.SignalHead
                };
                Require(target.IsPad && !PrototypeWorld.IsSocketOccupied(module, pose), $"{module} cannot be placed");
                Require(progress.Build(module, out _expectedProgress) == ActionResult.Applied,
                    $"could not predict building {module}");
                break;
            case Check.OccludedPad:
                Require(Vector3.Distance(PrototypeWorld.Eye(pose), aim) < 4 && target == default &&
                    read.GetCell(-19, 5, 9) == VoxelOccupancy.Solid && !progress.BeaconOnline,
                    "mast did not occlude the pad within reach before activation");
                break;
            case Check.Activate:
                Require(target.IsPad, "the pad did not focus from its clear approach");
                Require(progress.Activate(out _expectedProgress) == ActionResult.Applied,
                    "could not predict signal activation");
                break;
        }

        _progressBeforeAction = progress;
        _sawPendingTerrain = false;
        _interactionIssued = true;
        return (0, 0, yaw, pitch, true);
    }

    private void ObserveMovement(VoxelPose pose)
    {
        Require(pose == _expectedPose, $"expected position {_expectedPose}, observed {pose}");
        foreach (var plane in Planes)
        {
            var boundary = plane * Unit;
            if (_previousPose.X < boundary && pose.X >= boundary)
                _crossings.Add((plane, true));
            else if (_previousPose.X >= boundary && pose.X < boundary)
                _crossings.Add((plane, false));
        }

        _previousPose = pose;
    }

    private void ObserveBudget(RegionUsage highWater)
    {
        _peakSnapshots = Math.Max(_peakSnapshots, highWater.SnapshotSlots);
        _peakLoads = Math.Max(_peakLoads, highWater.Loads);
        _peakMeshes = Math.Max(_peakMeshes, highWater.MeshWork);
        _peakMeshBytes = Math.Max(_peakMeshBytes, highWater.MeshBytes);
        Require(_peakSnapshots <= WalkableRegion.MaximumSnapshotSlots && _peakLoads <= WalkableRegion.MaximumLoads &&
            _peakMeshes <= 1 && _peakMeshBytes <= WalkableRegion.MaximumCpuMeshBytes, "exceeded the streaming budget");
    }

    private void VerifyCompletion(PrototypeProgress progress)
    {
        Require(_crossings.SequenceEqual(ExpectedCrossings), "the route did not cross all three chunk planes in both directions");
        Require(progress is { Stone: 0, InstalledModules: 3, BeaconOnline: true } &&
            progress.MinedDeposits == (DepositMask.West | DepositMask.Seam | DepositMask.East), "rescue beacon is incomplete");
        Console.WriteLine($"WP07 route complete: seed=0x57505537, generator=2/1, movement={_movementTicks}, holds={_holdTicks}, " +
            $"crossings=-16,0,16,16,0,-16, high-water={_peakSnapshots} snapshots/{_peakLoads} loads/{_peakMeshes} mesh/{_peakMeshBytes} bytes.");
    }

    private static void VerifySeam(RegionReadLease read, bool occupied)
    {
        Require(read.GetCell(-1, 4, 7) == VoxelOccupancy.Air &&
            read.GetCell(0, 4, 7) == (occupied ? VoxelOccupancy.Solid : VoxelOccupancy.Air) &&
            read.GetCell(0, 3, 7) == VoxelOccupancy.Solid, "seam occupancy or floor disagrees with the edit");

        var start = new VoxelPose(-3 * Unit / 2, Floor, 15 * Unit / 2);
        var sweep = VoxelCollision.Sweep(read, start, 3 * Unit, 0, 0);
        var expectedX = occupied ? -VoxelCollision.HalfWidth : start.X + 3 * Unit;
        Require(!sweep.StreamingBlocked && sweep.Pose.X == expectedX, "collision sweep disagrees with the seam deposit");

        var west = false;
        var east = false;
        var faceTriangles = 0;
        for (var i = 0; i < read.MeshCount; i++)
        {
            var coordinate = read.GetMeshCoordinate(i);
            west |= coordinate == new ChunkCoordinate(-1, 0, 0);
            if (coordinate != new ChunkCoordinate(0, 0, 0))
                continue;

            east = true;
            var positions = read.GetMeshPositions(i);
            var indices = read.GetMeshIndices(i);
            for (var j = 0; j < indices.Length; j += 3)
            {
                var a = positions[(int)indices[j]];
                var b = positions[(int)indices[j + 1]];
                var c = positions[(int)indices[j + 2]];
                var center = (a + b + c) / 3;
                if (a.X == 0 && b.X == 0 && c.X == 0 && center.Y is > 4 and < 5 && center.Z is > 7 and < 8)
                    faceTriangles++;
            }
        }

        Require(west && east && faceTriangles == (occupied ? 2 : 0),
            "presented chunk pair did not contain the expected seam face");
        Console.WriteLine($"WP07 seam: occupied={occupied}, face triangles={faceTriangles}, sweep X={sweep.Pose.X}.");
    }

    private static (float Yaw, float Pitch) Aim(VoxelPose pose, Vector3 target)
    {
        var direction = target - PrototypeWorld.Eye(pose);
        var pitch = MathF.Atan2(direction.Y, MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z));
        return (MathF.Atan2(direction.Z, direction.X), Math.Clamp(pitch, -1.4f, 1.4f));
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw Failure(reason);
    }

    private static InvalidOperationException Failure(string reason) => new($"WP07 route: {reason}.");

    private readonly record struct Step(float X, float Z, Check Check = Check.None);

    private enum Check
    {
        None,
        OutOfReach,
        MineWest,
        MineSeam,
        ClearedSeam,
        MineEast,
        UnloadedSeam,
        OccupiedFoundation,
        BuildFoundation,
        BuildMast,
        BuildSignalHead,
        OccludedPad,
        Activate
    }
}
