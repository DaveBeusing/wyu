using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static class ConstructionCollisionVerification
{
    private const double Epsilon = 0.00001;
    private static readonly ConstructionAnchor Anchor = new(1, 0, 2_048_000, 0, ConstructionAnchor.CurrentFrameVersion);
    private static readonly ConstructionBlock Cube = new(2, Anchor.Id, 0, 0, 0, ConstructionBlockType.Cube, 0);

    internal static int Run()
    {
        (string Name, Action Test)[] cases =
        [
            (nameof(StationaryTouchAndWithdrawal), StationaryTouchAndWithdrawal),
            (nameof(WallAndFloorContacts), WallAndFloorContacts),
            (nameof(TangentFaceAndDiagonalClearance), TangentFaceAndDiagonalClearance),
            (nameof(NearestRayAndRebase), NearestRayAndRebase),
            (nameof(CrossAnchorOverlap), CrossAnchorOverlap),
            (nameof(InventoryContainerClearance), InventoryContainerClearance),
        ];

        var failures = 0;
        foreach (var (name, test) in cases)
        {
            try
            {
                test();
                Console.WriteLine($"PASS: {name}");
            }
            catch (InvalidOperationException error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name} · {error.Message}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static void StationaryTouchAndWithdrawal()
    {
        var scene = Scene(Cube);
        var penetrating = Capsule(-0.06, 0.03, 0.125, 0.08);
        RequireContact(scene.Sweep(penetrating, penetrating), VolumeQueryStatus.InitialOverlap, 0,
            new(-1, 0, 0), "stationary capsule protrudes 2 cm into the wall");

        var touching = Capsule(-0.08, 0.03, 0.125, 0.08);
        RequireClear(scene.Sweep(touching, touching), "stationary capsule on the wall plane");
        RequireClear(scene.Sweep(touching, Capsule(-0.25, 0.03, 0.125, 0.08)),
            "withdrawal from an initially touching wall");
        RequireContact(scene.Sweep(touching, penetrating), VolumeQueryStatus.Contact, 0,
            new(-1, 0, 0), "inward movement from an initially touching wall");
    }

    private static void WallAndFloorContacts()
    {
        var scene = Scene(Cube);
        var wall = scene.Sweep(Capsule(-0.30, 0.03, 0.125, 0.08), Capsule(0.70, 0.03, 0.125, 0.08));
        RequireContact(wall, VolumeQueryStatus.Contact, 0.22, new(-1, 0, 0), "first wall face in a 1 m sweep");

        var floor = scene.Sweep(Capsule(0.125, 0.55, 0.125, 0.10), Capsule(0.125, 0.15, 0.125, 0.10));
        RequireContact(floor, VolumeQueryStatus.Contact, 0.5, new(0, 1, 0), "fall onto the top face");
        var standing = Capsule(0.125, 0.35, 0.125, 0.10);
        RequireClear(scene.Sweep(standing, standing), "reconstructed pose after the floor contact");
        RequireContact(scene.Sweep(standing, Capsule(0.125, 0.33, 0.125, 0.10)), VolumeQueryStatus.Contact, 0,
            new(0, 1, 0), "downward support probe at the resting pose");
        RequireClear(scene.Sweep(standing, Capsule(0.195, 0.35, 0.125, 0.10)), "horizontal walking on the top face");
    }

    private static void TangentFaceAndDiagonalClearance()
    {
        var scene = Scene(Cube);
        // The capsule's swept bounds intersect the cube, but its circular section misses the corner.
        RequireClear(scene.Sweep(Capsule(-0.065, 0.40, -0.065, 0.075), Capsule(-0.065, -0.40, -0.065, 0.075)),
            "vertical sweep past the diagonal corner outside the capsule radius");
        // This sweep grazes the front face after coming around the corner. Touching should not hold motion.
        RequireClear(scene.Sweep(Capsule(-0.50, 0.03, -0.10, 0.10), Capsule(0.75, 0.03, -0.10, 0.10)),
            "complete tangential sweep past the face and both corners");
    }

    private static void NearestRayAndRebase()
    {
        // Give the farther cube the lower ID so enumeration alone cannot pass the nearest-face check.
        var scene = Scene(Cube with { X = 4 }, Cube with { Id = 3 });
        var origin = Anchor.Frame.ToWorld(new(-0.50, 0.125, 0.125));
        var direction = Anchor.Frame.XAxis;
        var hit = scene.Raycast(origin, direction, 4);
        Verify(hit is { BlockId: 3 }, "ray selects the nearer block");
        Near(hit.GetValueOrDefault().Distance, 0.50, "near face distance");
        Near(hit.GetValueOrDefault().Point, Anchor.Frame.ToWorld(new(0, 0.125, 0.125)), "near face point");
        Near(hit.GetValueOrDefault().Normal, new(-1, 0, 0), "near face normal");
        Verify(scene.Raycast(origin, direction, 0.49) is null, "out-of-range face is rejected");

        var frame = PlanetLocalFrame.Create(Anchor.Position);
        Verify(frame.TryRebase(Anchor.Frame.ToWorld(new(24, 0, 0)), out var rebased), "a new render origin was selected");
        var localBefore = frame.ToLocalDouble(origin);
        var localAfter = rebased.ToLocalDouble(origin);
        Verify(localBefore != localAfter, "the rebase changed render-local coordinates");
        var reconstructed = rebased.ToPlanetDouble(localAfter);
        var after = scene.Raycast(reconstructed, direction, 4);
        Verify(after == hit, "reconstructing planet coordinates after rebase preserves the exact hit");
        var start = Capsule(-0.25, 0.0625, 0.125, 0.0625);
        var end = Capsule(0.75, 0.0625, 0.125, 0.0625);
        Verify(scene.Sweep(start, end) == scene.Sweep(ThroughFrame(start, rebased), ThroughFrame(end, rebased)),
            "planet-space collision result survives render-origin translation");
    }

    private static void CrossAnchorOverlap()
    {
        var scene = Scene(Cube);
        var tiltedAnchor = new ConstructionAnchor(3, 100, Anchor.YMillimeters, 0, ConstructionAnchor.CurrentFrameVersion);
        var overlapping = (Cube with { Id = 4, AnchorId = tiltedAnchor.Id }).Box(tiltedAnchor);
        Verify(overlapping.YAxis != Cube.Box(Anchor).YAxis, "both anchors have genuinely distinct radial bases");
        Verify(scene.Intersects(overlapping), "different anchors cannot duplicate physical volume");

        var adjacent = (Cube with { X = 1 }).Box(Anchor);
        Verify(!scene.Intersects(adjacent), "shared cell face is admissible");
        var clearAnchor = tiltedAnchor with { XMillimeters = 1_000 };
        var clear = (Cube with { Id = 4, AnchorId = clearAnchor.Id }).Box(clearAnchor);
        Verify(!scene.Intersects(clear), "distinct and disjoint radial anchors remain admissible");
    }

    private static void InventoryContainerClearance()
    {
        var scene = Scene(Cube);
        var occupied = InventoryDropSite.ContainerBody(Anchor.Frame.ToWorld(new(0.125, 0, 0.125)));
        var clear = InventoryDropSite.ContainerBody(Anchor.Frame.ToWorld(new(0.60, 0, 0.125)));
        Verify(scene.Intersects(occupied) && ConstructionCollision.Intersects(Cube.Box(Anchor), occupied),
            "the existing #75 container footprint rejects either placement order");
        Verify(!scene.Intersects(clear) && !ConstructionCollision.Intersects(Cube.Box(Anchor), clear),
            "an adjacent free location remains available to the safe-drop search");
    }

    private static ConstructionCollision Scene(params ConstructionBlock[] blocks) =>
        new(new ConstructionState(1, blocks[^1].Id + 1, [Anchor], [.. blocks]));

    private static VolumeCapsule Capsule(double x, double y, double z, double radius) =>
        new(Anchor.Frame.ToWorld(new(x, y, z)), Anchor.Frame.ToWorld(new(x, y + 0.25, z)), radius);

    private static VolumeCapsule ThroughFrame(VolumeCapsule capsule, PlanetLocalFrame frame) =>
        new(frame.ToPlanetDouble(frame.ToLocalDouble(capsule.A)),
            frame.ToPlanetDouble(frame.ToLocalDouble(capsule.B)), capsule.Radius);

    private static void RequireClear(VolumeSweepResult result, string because) =>
        Verify(result.Status == VolumeQueryStatus.Clear && result.Fraction == 1,
            $"{because} · expected Clear at 1, got {result.Status} at {result.Fraction:G17}");

    private static void RequireContact(VolumeSweepResult result, VolumeQueryStatus expected, double fraction,
        PlanetVector normal, string because)
    {
        Verify(result.Status == expected, $"{because} · expected {expected}, got {result.Status}");
        Near(result.Fraction, fraction, because);
        Near(result.Normal, normal, because);
    }

    private static void Near(PlanetVector observed, PlanetVector expected, string reason)
    {
        Near(observed.X, expected.X, reason);
        Near(observed.Y, expected.Y, reason);
        Near(observed.Z, expected.Z, reason);
    }

    private static void Near(double observed, double expected, string reason) =>
        Verify(Math.Abs(observed - expected) <= Epsilon, $"{reason} · expected {expected:G17}, got {observed:G17}");

    private static void Verify(bool success, string reason)
    {
        if (!success)
            throw new InvalidOperationException(reason);
    }
}
