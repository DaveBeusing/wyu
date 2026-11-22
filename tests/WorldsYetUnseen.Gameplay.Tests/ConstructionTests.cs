using System.Diagnostics;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay.Tests;

internal static class ConstructionTests
{
    private static readonly ConstructionAnchor Draft = new(0, 4_096_000, 0, 0, ConstructionAnchor.CurrentFrameVersion);

    internal static void GridAndOrientations()
    {
        var pending = new Queue<byte>();
        var visited = new HashSet<byte> { 0 };
        pending.Enqueue(0);
        while (pending.TryDequeue(out var orientation))
        {
            foreach (var next in new[] { ConstructionOrientation.RotateAroundX(orientation), ConstructionOrientation.RotateAroundY(orientation) })
                if (visited.Add(next))
                    pending.Enqueue(next);
        }
        Require(visited.Count == ConstructionOrientation.Count, "The two keyboard rotations reach every orientation.");

        var frames = new HashSet<(PlanetVector, PlanetVector, PlanetVector)>();
        foreach (var orientation in visited)
        {
            var x = ConstructionOrientation.Transform(orientation, new(1, 0, 0));
            var y = ConstructionOrientation.Transform(orientation, new(0, 1, 0));
            var z = ConstructionOrientation.Transform(orientation, new(0, 0, 1));
            Require(frames.Add((x, y, z)) && z == new PlanetVector(x.Y * y.Z - x.Z * y.Y,
                x.Z * y.X - x.X * y.Z, x.X * y.Y - x.Y * y.X), "Every basis is unique and right-handed.");
            var block = new ConstructionBlock(1, 1, -2, 3, -4, ConstructionBlockType.Long, orientation);
            Require(block.TryGetGridBounds(out var bounds) && (bounds.MaxX - bounds.MinX) * (bounds.MaxY - bounds.MinY) *
                (bounds.MaxZ - bounds.MinZ) == 2, "Every orientation occupies exactly two integral cells.");
        }

        var edge = new ConstructionBlock(1, 1, short.MaxValue, 0, 0, ConstructionBlockType.Cube, 0);
        Require(edge.TryGetGridBounds(out var maximum) && maximum.MaxX == short.MaxValue + 1,
            "Last signed cell is usable and its outer face is exclusive.");
        Require(!(edge with { Type = ConstructionBlockType.Long }).TryGetGridBounds(out _), "Positive footprint overflow is rejected.");
        var flip = ConstructionOrientation.RotateAroundY(ConstructionOrientation.RotateAroundY(0));
        Require(!(edge with { X = short.MinValue, Orientation = flip }).TryGetGridBounds(out _), "Negative footprint overflow is rejected.");
        Require(!(edge with { Orientation = ConstructionOrientation.Count }).TryGetGridBounds(out _) &&
                !(edge with { Type = (ConstructionBlockType)255 }).TryGetGridBounds(out _), "Unknown orientation and type are rejected.");

        Require(ConstructionAnchor.TryCreate(new(4096.0004, 291.9996, -76), out var draft) && draft.XMillimeters == 4096000 &&
            draft.YMillimeters == 292000 && draft.ZMillimeters == -76000, "An anchor is fixed at canonical millimetre precision.");
        Require(!ConstructionAnchor.TryCreate(new(double.PositiveInfinity, 0, 0), out _) &&
            !ConstructionAnchor.TryCreate(default, out _), "An anchor requires finite, nonzero radial coordinates.");
        var frame = draft.Frame;
        var local = new PlanetVector(123.25, -0.5, 4.75);
        var restoredLocal = frame.ToLocal(frame.ToWorld(local));
        Require(Math.Abs(local.X - restoredLocal.X) < 1e-10 && Math.Abs(local.Y - restoredLocal.Y) < 1e-10 &&
                Math.Abs(local.Z - restoredLocal.Z) < 1e-10, "The frozen radial frame reconstructs the same grid location.");
    }

    internal static void AtomicOwnershipAndOverlap()
    {
        var tool = EquippedTool();
        var carried = InventoryOperations.AcquireOre(tool, 3).State;
        var target = new ConstructionTarget(Draft, 0, 0, 0, ConstructionBlockType.Long, 0);
        var empty = ConstructionState.Empty;
        CheckRejected(ConstructionOperations.Place(empty, InventoryState.Starter, target), empty, InventoryState.Starter,
            ConstructionStatus.ToolRequired);
        CheckRejected(ConstructionOperations.Place(empty, tool, target), empty, tool, ConstructionStatus.InsufficientOre);

        var placed = ConstructionOperations.Place(empty, carried, target);
        Require(placed.Status == ConstructionStatus.Applied && placed.Inventory.OreCount == 1 &&
            placed.Inventory.Revision == carried.Revision + 1 && placed.Construction.Revision == 2 &&
            placed.Construction is { Anchors: [{ Id: 1 }], Blocks: [{ Id: 2, AnchorId: 1 }] } && placed.Construction.IsValid(),
            "One staged root owns both paid cells and advances both revisions.");
        var existing = placed.Construction.Anchors[0];
        var attachedTarget = target with { Anchor = existing, Type = ConstructionBlockType.Cube };
        CheckRejected(ConstructionOperations.Place(placed.Construction, placed.Inventory, attachedTarget with { X = 1 }),
            placed.Construction, placed.Inventory, ConstructionStatus.Overlap);
        CheckRejected(ConstructionOperations.Place(placed.Construction, placed.Inventory, target with { Type = ConstructionBlockType.Cube }),
            placed.Construction, placed.Inventory, ConstructionStatus.Overlap);
        // Along the radial axis, another anchor exactly one cube higher touches the existing top face.
        var touching = ConstructionOperations.Place(placed.Construction, placed.Inventory,
            target with { Anchor = Draft with { XMillimeters = Draft.XMillimeters + 250 }, Type = ConstructionBlockType.Cube });
        Require(touching.Status == ConstructionStatus.Applied && touching.Construction.Anchors.Length == 2 &&
            touching.Construction.IsValid(), "Cross-anchor face contact is legal.");
        var penetrates = target with { Anchor = Draft with { XMillimeters = Draft.XMillimeters + 249 }, Type = ConstructionBlockType.Cube };
        CheckRejected(ConstructionOperations.Place(placed.Construction, placed.Inventory, penetrates),
            placed.Construction, placed.Inventory, ConstructionStatus.Overlap);

        var attached = ConstructionOperations.Place(placed.Construction, placed.Inventory, attachedTarget with { X = 2 });
        Require(attached.Status == ConstructionStatus.Applied && attached.Inventory.OreCount == 0 &&
            attached.Construction is { Anchors.Length: 1, Blocks.Length: 2 } && attached.Construction.IsValid(),
            "The neighboring cell shares a face without occupying either paid cell of the long block.");
        var refund = ConstructionOperations.Remove(attached.Construction, attached.Inventory, 2);
        Require(refund.Status == ConstructionStatus.Applied && refund.Inventory.OreCount == 2 &&
                refund.Construction is { Anchors.Length: 1, Blocks: [{ Id: 3 }] }, "Both long-block cells are returned together without moving the neighbor.");
        CheckRejected(ConstructionOperations.Remove(refund.Construction, refund.Inventory, 2), refund.Construction, refund.Inventory,
            ConstructionStatus.NotFound);

        var normal = InventoryOperations.AcquireOre(tool, 74).State;
        var pending = ConstructionOperations.Remove(placed.Construction, normal, 2);
        CheckRejected(pending, placed.Construction, normal, ConstructionStatus.OverloadConfirmation);
        Require(pending.ProjectedLoad is { Weight: 20_500 }, "Confirmation identifies the projected overload.");
        var confirmed = ConstructionOperations.Remove(placed.Construction, normal, 2, allowOverload: true);
        Require(confirmed.Status == ConstructionStatus.Applied && confirmed.Inventory.OreCount == 76 &&
                confirmed.Construction is { NextId: 3, Anchors.IsEmpty: true, Blocks.IsEmpty: true } && confirmed.Construction.IsValid(),
            "Confirmation returns the full cost, retires the empty anchor and preserves the ID watermark.");

        var tooFull = InventoryOperations.AcquireOre(tool, 89, allowOverload: true).State;
        CheckRejected(ConstructionOperations.Remove(placed.Construction, tooFull, 2, allowOverload: true),
            placed.Construction, tooFull, ConstructionStatus.CapacityExceeded);

        var fullBudget = tool with
        {
            Containers = [.. Enumerable.Range(1, 852).Select(id => new InventoryContainer((ulong)id,
                InventoryContainerKind.Chest, Draft.Position, id == 1
                    ? [new(0, InventoryItem.Ore, 1), new(1, InventoryItem.Ore, 1), new(2, InventoryItem.Ore, 1)] : []))],
            NextContainerId = 853
        };
        Require(fullBudget.EncodedBytes == InventoryState.RegularBytesLimit && fullBudget.IsValid(), "Inventory budget fixture is exact.");
        CheckRejected(ConstructionOperations.Remove(placed.Construction, fullBudget, 2), placed.Construction, fullBudget, ConstructionStatus.SaveBudget);
    }

    internal static void RecordLimitsAndRestoreValidation()
    {
        var atAnchorLimit = Fixture(ConstructionState.MaximumAnchors, ConstructionState.MaximumAnchors);
        var supplies = InventoryOperations.AcquireOre(EquippedTool(), 2).State;
        Require(atAnchorLimit.IsValid(), "Exactly 32 anchors are valid.");
        var target = new ConstructionTarget(Draft with { XMillimeters = Draft.XMillimeters + 250_000 }, 0, 0, 0, ConstructionBlockType.Cube, 0);
        CheckRejected(ConstructionOperations.Place(atAnchorLimit, supplies, target), atAnchorLimit, supplies, ConstructionStatus.Limit);

        var belowLimit = Fixture(ConstructionState.MaximumAnchors, ConstructionState.MaximumBlocks - 1);
        var accepted = ConstructionOperations.Place(belowLimit, supplies, target with { Anchor = belowLimit.Anchors[0], X = 2000 });
        Require(accepted.Status == ConstructionStatus.Applied && accepted.Construction.Blocks.Length == ConstructionState.MaximumBlocks &&
                accepted.Construction.IsValid(), "1,439 to 1,440 blocks succeeds at the encoded limit.");
        CheckRejected(ConstructionOperations.Place(accepted.Construction, accepted.Inventory, target with { Anchor = belowLimit.Anchors[0], X = 2001 }),
            accepted.Construction, accepted.Inventory, ConstructionStatus.Limit);

        var small = Fixture(1, 2);
        Require(!(small with { Anchors = small.Anchors.Add(Draft with { Id = small.NextId }), NextId = small.NextId + 1 }).IsValid(),
            "A restored state cannot carry an empty anchor.");
        Require(!(small with { Blocks = small.Blocks.SetItem(0, small.Blocks[0] with { Id = 1 }) }).IsValid(),
            "Anchor and block records share one stable ID space.");
        Require(!(small with { Blocks = small.Blocks.SetItem(1, small.Blocks[1] with { X = 1 }) }).IsValid(),
            "Restoring a multi-cell overlap is refused.");
        var crossAnchor = Fixture(2, 2);
        var nudged = crossAnchor.Anchors.SetItem(1, crossAnchor.Anchors[1] with { XMillimeters = Draft.XMillimeters + 249 });
        Require(!(crossAnchor with { Anchors = nudged }).IsValid(), "Cross-anchor overlap is also refused during restoration.");
    }

    internal static void MeasureStaging()
    {
        const int warmup = 20;
        const int samples = 100;
        var supply = InventoryOperations.AcquireOre(EquippedTool(), 2).State;
        foreach (var (size, anchorCount) in new[] { (16, 1), (256, 8), (ConstructionState.MaximumBlocks, 32) })
        {
            // The scene fixture has both approved shapes and goes from size - 1 to size on placement.
            var baseline = GC.GetTotalMemory(forceFullCollection: true);
            var fixture = Fixture(anchorCount, size - 1);
            var before = fixture with
            {
                Blocks = [.. fixture.Blocks.Select((block, index) => index % 2 == 0
                    ? block with { Type = ConstructionBlockType.Cube } : block)]
            };
            var target = new ConstructionTarget(before.Anchors[0], 1000, 0, 0, ConstructionBlockType.Long, 0);
            var accepted = ConstructionOperations.Place(before, supply, target);
            Require(accepted.Status == ConstructionStatus.Applied && accepted.Construction.Blocks.Length == size,
                "A staging benchmark must measure a successful placement at its declared size.");
            var block = accepted.Construction.Blocks[^1];
            Measure("stage-place", () => ConstructionOperations.Place(before, supply, target), ConstructionStatus.Applied);
            Measure("stage-remove", () => ConstructionOperations.Remove(accepted.Construction, accepted.Inventory, block.Id),
                ConstructionStatus.Applied);
            if (size == ConstructionState.MaximumBlocks)
            {
                var additional = target with { X = 1002 };
                Measure("reject-next-block", () => ConstructionOperations.Place(accepted.Construction, supply, additional),
                    ConstructionStatus.Limit);
            }

            void Measure(string name, Func<ConstructionChange> action, ConstructionStatus expected)
            {
                for (var i = 0; i < warmup; i++)
                    Require(action().Status == expected, "Benchmark warm-up changed the expected result.");

                var times = new long[samples];
                long allocated = 0;
                var peak = GC.GetTotalMemory(forceFullCollection: false);
                for (var i = 0; i < samples; i++)
                {
                    var bytes = GC.GetAllocatedBytesForCurrentThread();
                    var start = Stopwatch.GetTimestamp();
                    var result = action();
                    times[i] = Stopwatch.GetTimestamp() - start;
                    allocated += GC.GetAllocatedBytesForCurrentThread() - bytes;
                    peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
                    Require(result.Status == expected, "Benchmark sample changed the expected result.");
                }
                Array.Sort(times);
                var microseconds = 1_000_000d / Stopwatch.Frequency;
                Console.WriteLine(FormattableString.Invariant(
                    $"construction-domain {name} blocks={size} anchors={anchorCount} n={samples} p50={times[49] * microseconds:F2} us p95={times[94] * microseconds:F2} us alloc={allocated / samples} B/op sampled-managed-heap-peak-delta={Math.Max(0, peak - baseline)} B"));
            }
        }
    }

    private static InventoryState EquippedTool() => InventoryOperations.Equip(InventoryState.Empty with
    {
        Carried = [new(0, InventoryItem.Tool, 1)]
    }, 0).State;

    private static ConstructionState Fixture(int anchors, int blocks) => new(1, checked((uint)(anchors + blocks + 1)),
        [.. Enumerable.Range(0, anchors).Select(index => Draft with { Id = (uint)index + 1, XMillimeters = Draft.XMillimeters + 1000 * index })],
        [.. Enumerable.Range(0, blocks).Select(index => new ConstructionBlock((uint)(anchors + index + 1), (uint)(index % anchors + 1),
            (short)(3 * (index / anchors)), 0, 0, ConstructionBlockType.Long, 0))]);

    private static void CheckRejected(ConstructionChange result, ConstructionState construction, InventoryState inventory,
        ConstructionStatus status) => Require(result.Status == status && ReferenceEquals(result.Construction, construction) &&
        ReferenceEquals(result.Inventory, inventory), $"Expected unchanged ownership on {status}, got {result.Status}.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
