using System.Collections.Immutable;
using System.Diagnostics;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay.Tests;

internal static class InventoryTests
{
    private static readonly PlanetVector Position = new(4_096, 0, 0);

    internal static void LoadAndRecovery()
    {
        var empty = InventoryState.Empty;
        var normal = Applied(InventoryOperations.AcquireOre(empty, 80));
        Require(normal.Load.Relative == 1 && normal.Load.SpeedScale == 1, "80 ore is exactly the normal mass limit.");

        var warning = InventoryOperations.AcquireOre(normal, 16);
        Require(warning.Status == InventoryChangeStatus.OverloadConfirmation && ReferenceEquals(warning.State, normal),
            "The overload notice cannot publish a credit.");
        Require(warning.ProjectedLoad is { Weight: 24_000, MovementHeld: false }, "Exactly 120% remains admissible.");
        var ceiling = Applied(InventoryOperations.AcquireOre(normal, 16, allowOverload: true));
        Require(Math.Abs(ceiling.Load.SpeedScale - 0.6) < 1e-12, "Exactly 120% moves at 60% speed.");
        Require(InventoryOperations.AcquireOre(ceiling, 1, allowOverload: true).Status ==
            InventoryChangeStatus.CapacityExceeded, "Ore above the physical limit cannot enter.");

        var coats = Enumerable.Range(0, 10).Select(slot => new InventoryStack((ushort)slot, InventoryItem.Coat, 1));
        var volumeLimited = empty with
        {
            Carried = [.. coats],
            Containers = [Chest(1, [new(0, InventoryItem.Coat, 1), new(1, InventoryItem.Coat, 1), new(2, InventoryItem.Ore, 1)])],
            NextContainerId = 2
        };
        Require(volumeLimited.Load is { Weight: 12_500, Volume: 30_000, Relative: 1 }, "Volume can independently set the normal limit.");
        var volumePreview = InventoryOperations.MoveAll(volumeLimited, 1, 0);
        Require(volumePreview.Status == InventoryChangeStatus.OverloadConfirmation &&
                volumePreview.ProjectedLoad is { Weight: 15_000, Volume: 36_000, Relative: 1.2, MovementHeld: false } &&
                volumePreview.Remaining.SequenceEqual([new(InventoryItem.Ore, 1)]),
            "Volume can independently set the overload limit and prevent further intake.");

        var equip = Applied(InventoryOperations.Equip(InventoryState.Starter, 1));
        var equipped = Applied(InventoryOperations.AcquireOre(equip, 110));
        var removed = Applied(InventoryOperations.Unequip(equipped, 0));
        Require(removed.Load.MovementHeld && removed.Load.SpeedScale == 0 && removed.OreCount == equipped.OreCount,
            "Removing the pack holds movement without discarding owned ore.");
        var relief = Applied(InventoryOperations.Drop(removed,
            removed.Carried.First(static stack => stack.Item == InventoryItem.Ore).Slot, 40, Position));
        Require(!relief.Load.MovementHeld, "A permitted transfer out can restore movement capacity.");
        Require(Owned(removed, InventoryItem.Ore) == Owned(relief, InventoryItem.Ore), "Recovery preserves ore ownership.");

        var worn = Applied(InventoryOperations.Equip(InventoryState.Starter, 2));
        Require(worn.Load.Weight == InventoryState.Starter.Load.Weight && worn.Load.Volume == InventoryState.Starter.Load.Volume - 3_000,
            "Worn clothing still weighs the same but consumes no pack volume.");
    }

    internal static void TransfersAndSupport()
    {
        var carried = Applied(InventoryOperations.AcquireOre(InventoryState.Empty, 91, allowOverload: true));
        var before = carried with
        {
            Containers = [Chest(1, [new(0, InventoryItem.Ore, 3), new(1, InventoryItem.Backpack, 1), new(2, InventoryItem.Ore, 4)])],
            NextContainerId = 2
        };
        var preview = InventoryOperations.MoveAll(before, 1, 0);
        Require(preview.Status == InventoryChangeStatus.OverloadConfirmation && ReferenceEquals(preview.State, before),
            "The preview does not publish either side.");
        var result = InventoryOperations.MoveAll(before, 1, 0, allowOverload: true);
        var after = Applied(result);
        Require(result.Moved.SequenceEqual([new(InventoryItem.Ore, 5)]) && result.Remaining.SequenceEqual(
                [new(InventoryItem.Backpack, 1), new(InventoryItem.Ore, 2)]),
            "Move All skips a nonfitting stack and takes the fitting portion of the later stack.");
        Require(after.Revision == before.Revision + 1 && after.OreCount == 96 && after.Carried[^1].Quantity == 16,
            "One revision publishes the whole transfer, merging existing stacks first.");
        Require(Owned(before, InventoryItem.Ore) == Owned(after, InventoryItem.Ore) &&
                Owned(before, InventoryItem.Backpack) == Owned(after, InventoryItem.Backpack),
            "Source and target ownership balance.");

        var pending = InventoryOperations.Dismantle(after, 1, null);
        Require(pending.Status == InventoryChangeStatus.AwaitingSupport && ReferenceEquals(pending.State, after),
            "An unsupported drop leaves the intact chest.");
        var dismantled = Applied(InventoryOperations.Dismantle(after, 1, Position));
        Require(dismantled.Containers is [{ Kind: InventoryContainerKind.Drop, Stacks.Length: 3 }] &&
                Owned(after, InventoryItem.Chest) == Owned(dismantled, InventoryItem.Chest) &&
                Owned(after, InventoryItem.Ore) == Owned(dismantled, InventoryItem.Ore),
            "One replacement bundle owns the empty chest and all its contents.");
        var empty = Applied(InventoryOperations.PlaceChest(InventoryState.Starter, 0, Position));
        Require(empty.Containers is [{ Kind: InventoryContainerKind.Chest, Stacks.IsEmpty: true }] &&
                Owned(empty, InventoryItem.Chest) == Owned(InventoryState.Starter, InventoryItem.Chest),
            "Placing a chest changes its form without creating a second owner.");
    }

    internal static void SaveBudgetRelief()
    {
        const int chests = 852;
        var before = InventoryState.Empty with
        {
            Carried = [new(0, InventoryItem.Ore, 40), new(1, InventoryItem.Ore, 40), new(2, InventoryItem.Ore, 40),
                new(3, InventoryItem.Ore, 40)],
            Containers = [.. Enumerable.Range(1, chests).Select(id => Chest((ulong)id, []))],
            NextContainerId = chests + 1
        };
        Require(before.IsValid() && before.EncodedBytes == InventoryState.RegularBytesLimit && before.Load.MovementHeld,
            "Fixture must fill the regular save budget at a held load.");
        var partial = InventoryOperations.Drop(before, 0, 20, Position);
        Require(partial.Status == InventoryChangeStatus.SaveBudget && ReferenceEquals(partial.State, before),
            "A split that adds a stack record cannot use protected relief bytes.");
        var safe = Applied(InventoryOperations.Relieve(before, Position));
        Require(!safe.Load.MovementHeld && safe.Carried.IsEmpty && safe.Containers[^1].Kind == InventoryContainerKind.ReliefDrop &&
                safe.EncodedBytes == before.EncodedBytes + InventoryState.ContainerBytes &&
                Owned(before, InventoryItem.Ore) == Owned(safe, InventoryItem.Ore),
            "One reserved owner row makes the complete safe relief possible at the regular byte limit.");

        Require(InventoryState.TryFromLegacyOre(120, 4, out var restored) && restored.OreCount == 120 &&
                restored.Revision == 4 && restored.Load.MovementHeld, "Legacy ore imports preserve ownership and revision even when overloaded.");
        Require(!InventoryState.TryFromLegacyOre(int.MaxValue, 4, out _), "An unrepresentable old count fails safely.");
        Require(!(before with { Carried = [new(0, (InventoryItem)65_535, 1)] }).IsValid() &&
                !(before with { Carried = [new(0, InventoryItem.Ore, 41)] }).IsValid(),
            "Unknown item IDs and oversized stacks are rejected before publication.");
    }

    internal static void MeasureMoves()
    {
        const int iterations = 500;
        Span<long> durations = stackalloc long[iterations];
        foreach (var (ownerCount, stackCount) in new[] { (1, 4), (128, 24), (800, 24) })
        {
            var stacks = Enumerable.Range(0, stackCount)
                .Select(slot => new InventoryStack((ushort)slot, InventoryItem.Ore, 2)).ToImmutableArray();
            var sample = InventoryState.Empty with
            {
                Containers = [Chest(1, stacks), .. Enumerable.Range(2, ownerCount - 1).Select(id => Chest((ulong)id, []))],
                NextContainerId = (ulong)ownerCount + 1
            };
            Require(sample.IsValid(), "Inventory measurement fixture is outside limits.");
            for (var warmup = 0; warmup < 30; warmup++)
                _ = Applied(InventoryOperations.MoveAll(sample, 1, 0));
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                var started = Stopwatch.GetTimestamp();
                _ = Applied(InventoryOperations.MoveAll(sample, 1, 0));
                durations[iteration] = Stopwatch.GetTimestamp() - started;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            durations.Sort();
            using var process = Process.GetCurrentProcess();
            Console.WriteLine(FormattableString.Invariant(
                $"WP75 MoveAll owners={ownerCount} source-stacks={stackCount} bytes={sample.EncodedBytes} samples={iterations} p50-us={1_000_000.0 * durations[iterations / 2] / Stopwatch.Frequency:F2} p95-us={1_000_000.0 * durations[iterations * 95 / 100] / Stopwatch.Frequency:F2} allocated-B/op={allocated / iterations} process-peak-B={process.PeakWorkingSet64}"));
        }
    }

    private static InventoryContainer Chest(ulong id, ImmutableArray<InventoryStack> stacks) =>
        new(id, InventoryContainerKind.Chest, Position, stacks);

    private static int Owned(InventoryState state, InventoryItem item) =>
        state.Carried.Concat(state.Equipped).Concat(state.Containers.SelectMany(static owner => owner.Stacks))
            .Where(stack => stack.Item == item).Sum(static stack => (int)stack.Quantity) +
            (item == InventoryItem.Chest ? state.Containers.Count(static owner => owner.Kind == InventoryContainerKind.Chest) : 0);

    private static InventoryState Applied(InventoryChange change)
    {
        Require(change.Status == InventoryChangeStatus.Applied && change.State.IsValid(),
            $"Expected a valid applied inventory change, found {change.Status}.");
        return change.State;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
