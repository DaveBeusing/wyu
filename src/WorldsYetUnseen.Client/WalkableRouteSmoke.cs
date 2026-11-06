using System.Diagnostics;
using Groundwork.Rendering;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed class WalkableRouteSmoke
{
    private const int Unit = VoxelCollision.UnitsPerVoxel;
    private const int Speed = Unit / 8;
    private const int Floor = 4 * Unit;
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(10);
    private static readonly VoxelPose Spawn = new(-20 * Unit - Unit / 2, Floor, 7 * Unit + Unit / 2);
    private static readonly ChunkCoordinate WestEdge = new(-4, 0, 0);
    private static readonly ChunkCoordinate EastEdge = new(4, 0, 0);
    private static readonly int[] ExpectedAnchors = [-2, -1, 0, 1, 2, 1, 0, -1, -2];
    private static readonly RoutePhase[] Phases =
    [
        new("outbound", 424, Speed, 0, Spawn with { X = 32 * Unit + Unit / 2 }),
        new("wall", 24, Speed, 0, Spawn with { X = 33 * Unit + 3 * Unit / 4 }, WallClamps: 14),
        new("along wall", 32, 0, Speed, new(33 * Unit + 3 * Unit / 4, Floor, 11 * Unit + Unit / 2)),
        new("return", 434, -Speed, 0, Spawn with { Z = 11 * Unit + Unit / 2 }),
    ];

    private VoxelPose _pose = Spawn;
    private int _phaseIndex;
    private int _phaseTicks;
    private int _motionTicks;
    private int _holdTicks;
    private int _wallClamps;
    private int _anchorCount;
    private ulong _lastPublishedTick;
    private ulong _westLoadIdentity;
    private ulong _westLoadFingerprint;
    private long? _holdStarted;
    private ulong _lastPresentSequence;
    private ulong _lastPresentGeneration;
    private long _lastPresentTimestamp;
    private int _presentIntervalSamples;
    private double _presentIntervalTotalMs;
    private double _presentIntervalMinMs = double.PositiveInfinity;
    private double _presentIntervalMaxMs;

    internal bool Completed => _phaseIndex == Phases.Length;

    internal float CameraYaw => _phaseIndex switch
    {
        0 or 1 => 0,
        2 => MathF.PI / 2,
        _ => MathF.PI,
    };

    internal (int X, int Z) NextMovement(bool ready) =>
        ready && !Completed ? (Phases[_phaseIndex].X, Phases[_phaseIndex].Z) : default;

    internal void RecordPublishedTick(ulong tick, bool ready, VoxelMotionResult motion, RegionReadLease? occupancy,
        MeshReplacementSnapshot renderer, RegionUsage highWater, ulong westLoadFingerprint)
    {
        if (Completed)
            return;

        Require(tick != 0 && (_lastPublishedTick == 0 || tick == _lastPublishedTick + 1),
            "expected each consecutive published base tick");
        _lastPublishedTick = tick;
        var next = motion.State.Pose;
        if (next.Y != Floor)
            throw RouteFailure($"left the floor at tick {tick}");
        if (occupancy is not null)
            VerifyFootprint(occupancy, next);

        if (!ready)
        {
            if (next != _pose)
                throw RouteFailure($"moved while coverage was pending at tick {tick}");
            _holdTicks++;
            _holdStarted ??= Stopwatch.GetTimestamp();
            CheckReadinessTimeout(_holdStarted.Value, tick);
            return;
        }

        if (_holdStarted is { } holdStarted)
        {
            CheckReadinessTimeout(holdStarted, tick);
            _holdStarted = null;
        }

        if (occupancy is null)
            throw RouteFailure("ready tick has no active occupancy");
        var anchor = occupancy.Anchor;
        if (anchor != new ChunkCoordinate(_pose.X >> 14, 0, _pose.Z >> 14) ||
            renderer.LastPresentedGeneration != occupancy.Generation)
            throw RouteFailure($"active and presented coverage disagree at tick {tick}");
        RecordAnchor(occupancy, westLoadFingerprint);

        if (!motion.Grounded || motion.StreamingBlocked || motion.State.VerticalVelocity != 0)
            throw RouteFailure($"movement lacked grounded support at tick {tick}");
        var phase = Phases[_phaseIndex];
        var clamps = phase.WallClamps > 0 && _phaseTicks >= phase.Ticks - phase.WallClamps;
        var expected = _pose with { X = _pose.X + (clamps ? 0 : phase.X), Z = _pose.Z + phase.Z };
        if (next != expected)
            throw RouteFailure($"unexpected {phase.Name} position at tick {tick}. Expected {expected}, observed {next}");
        if (clamps)
            _wallClamps++;
        _pose = next;
        _motionTicks++;
        _phaseTicks++;

        if (_phaseTicks != phase.Ticks)
            return;

        Require(next == phase.End, $"incorrect {phase.Name} checkpoint");
        Console.WriteLine($"WP05 route checkpoint {phase.Name}: base={tick}, motion={_motionTicks}, holds={_holdTicks}, " +
            $"pose={next}, desired={renderer.DesiredGeneration}, installed={renderer.InstalledGeneration}, " +
            $"presented={renderer.LastPresentedGeneration}.");
        if (_phaseIndex == 0)
        {
            Require(!occupancy.TryGetLoadIdentity(WestEdge, out _) && occupancy.TryGetLoadIdentity(EastEdge, out _),
                "west data did not leave the active footprint at the east checkpoint");
        }

        _phaseTicks = 0;
        _phaseIndex++;
        if (Completed)
            VerifyCompletion(occupancy, highWater, westLoadFingerprint);
    }

    internal void ObservePresentation(RendererSnapshot frame, MeshReplacementSnapshot geometry)
    {
        if (Completed || frame.LastPresentedSequence == 0 || geometry.LastPresentedGeneration == 0 ||
            (frame.LastPresentedSequence == _lastPresentSequence && geometry.LastPresentedGeneration == _lastPresentGeneration))
            return;

        var timestamp = Stopwatch.GetTimestamp();
        if (_lastPresentSequence != 0)
        {
            var interval = Stopwatch.GetElapsedTime(_lastPresentTimestamp, timestamp).TotalMilliseconds;
            _presentIntervalSamples++;
            _presentIntervalTotalMs += interval;
            _presentIntervalMinMs = Math.Min(_presentIntervalMinMs, interval);
            _presentIntervalMaxMs = Math.Max(_presentIntervalMaxMs, interval);
        }

        _lastPresentTimestamp = timestamp;
        _lastPresentSequence = frame.LastPresentedSequence;
        _lastPresentGeneration = geometry.LastPresentedGeneration;
    }

    private void RecordAnchor(RegionReadLease occupancy, ulong westLoadFingerprint)
    {
        var anchor = occupancy.Anchor.X;
        if (_anchorCount > 0 && ExpectedAnchors[_anchorCount - 1] == anchor)
            return;

        Require(_anchorCount < ExpectedAnchors.Length && ExpectedAnchors[_anchorCount] == anchor,
            $"unexpected anchor {anchor} after {_anchorCount} anchor observations");
        if (_anchorCount == 0)
        {
            Require(occupancy.TryGetLoadIdentity(WestEdge, out _westLoadIdentity) && _westLoadIdentity != 0 &&
                westLoadFingerprint != 0, "initial west load identity or fingerprint is missing");
            _westLoadFingerprint = westLoadFingerprint;
        }

        _anchorCount++;
        Console.WriteLine($"WP05 route presented anchor {anchor}: motion={_motionTicks}, holds={_holdTicks}, " +
            $"generation={occupancy.Generation}.");
    }

    private void VerifyCompletion(RegionReadLease occupancy, RegionUsage highWater, ulong westLoadFingerprint)
    {
        Require(_motionTicks == 914 && _wallClamps == 14 && _anchorCount == ExpectedAnchors.Length,
            "motion count, wall clamp count or anchor crossings differ from the fixed route");
        Require(!occupancy.TryGetLoadIdentity(EastEdge, out _), "east data did not leave the active footprint on return");
        Require(occupancy.TryGetLoadIdentity(WestEdge, out var westReloadIdentity) &&
            westReloadIdentity != 0 && westReloadIdentity != _westLoadIdentity && westLoadFingerprint == _westLoadFingerprint,
            "west data did not reload with matching occupancy and a fresh identity");
        Require(highWater.SnapshotSlots <= WalkableRegion.MaximumSnapshotSlots &&
            highWater.Loads <= WalkableRegion.MaximumLoads && highWater.MeshWork <= 1 &&
            highWater.MeshBytes <= WalkableRegion.MaximumCpuMeshBytes, $"exceeded the region budget: {highWater}");
        Require(_presentIntervalSamples > 0, "no window presentation timing samples were observed");

        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"WP05 route complete: seed=0x57505535, generator=1, motion={_motionTicks}, holds={_holdTicks}, " +
            $"wall-clamps={_wallClamps}, anchors={string.Join(",", ExpectedAnchors)}, " +
            $"west-loads={_westLoadIdentity},{westReloadIdentity}, west-fingerprint=0x{westLoadFingerprint:X16}, high-water={highWater}.");
        Console.WriteLine(FormattableString.Invariant(
            $"WP05 observed present intervals: clock=Stopwatch, samples={_presentIntervalSamples}, min-ms={_presentIntervalMinMs:F3}, mean-ms={_presentIntervalTotalMs / _presentIntervalSamples:F3}, max-ms={_presentIntervalMaxMs:F3}, working-set-bytes={process.WorkingSet64}, peak-working-set-bytes={process.PeakWorkingSet64}."));
    }

    private static void VerifyFootprint(RegionReadLease occupancy, VoxelPose pose)
    {
        var minimumX = (pose.X - VoxelCollision.HalfWidth) >> 10;
        var maximumX = (pose.X + VoxelCollision.HalfWidth - 1) >> 10;
        var minimumZ = (pose.Z - VoxelCollision.HalfWidth) >> 10;
        var maximumZ = (pose.Z + VoxelCollision.HalfWidth - 1) >> 10;
        var maximumY = (pose.Y + VoxelCollision.Height - 1) >> 10;
        for (var x = minimumX; x <= maximumX; x++)
        for (var z = minimumZ; z <= maximumZ; z++)
        {
            if (occupancy.GetCell(x, 3, z) != VoxelOccupancy.Solid)
                throw RouteFailure($"missing floor at ({x}, {z})");
            for (var y = 4; y <= maximumY; y++)
                if (occupancy.GetCell(x, y, z) != VoxelOccupancy.Air)
                    throw RouteFailure($"body intersects occupied or unknown cell ({x}, {y}, {z})");
        }
    }

    private static void CheckReadinessTimeout(long holdStarted, ulong tick)
    {
        if (Stopwatch.GetElapsedTime(holdStarted) >= ReadinessTimeout)
            throw RouteFailure($"coverage wait exceeded {ReadinessTimeout.TotalSeconds:F0} seconds at tick {tick}");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw RouteFailure(reason);
    }

    private static InvalidOperationException RouteFailure(string reason) => new($"WP05 route: {reason}.");

    private readonly record struct RoutePhase(string Name, int Ticks, int X, int Z, VoxelPose End, int WallClamps = 0);
}
