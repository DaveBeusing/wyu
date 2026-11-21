using System.Collections.Immutable;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay;

public enum InventoryChangeStatus
{
    Applied,
    NothingFits,
    Invalid,
    NotFound,
    OverloadConfirmation,
    CapacityExceeded,
    SaveBudget,
    AwaitingSupport
}

public readonly record struct InventoryAmount(InventoryItem Item, int Quantity);

public sealed record InventoryChange(InventoryChangeStatus Status, InventoryState State,
    ImmutableArray<InventoryAmount> Moved, ImmutableArray<InventoryAmount> Remaining,
    InventoryLoad? ProjectedLoad = null);

/// <summary>A command calculates one unpublished state. The caller owns revision checking and publication.</summary>
public static class InventoryOperations
{
    public static InventoryChange MoveAll(InventoryState state, ulong sourceId, ulong destinationId,
        bool allowOverload = false)
    {
        var status = TransferPlan.Open(state, sourceId, destinationId, out var plan);
        if (plan is null)
            return Reject(state, status);
        var rejection = InventoryChangeStatus.NothingFits;
        foreach (var stack in plan.Original)
        {
            for (var quantity = (int)stack.Quantity; quantity > 0; quantity--)
            {
                var attempt = plan.Stage(stack, (ushort)quantity);
                if (attempt == InventoryChangeStatus.Applied)
                    break;
                if (attempt == InventoryChangeStatus.SaveBudget)
                    rejection = attempt;
            }
        }
        return plan.Complete(rejection, allowOverload);
    }

    public static InventoryChange Transfer(InventoryState state, ulong sourceId, ulong destinationId, ushort slot,
        ushort quantity, bool allowOverload = false)
    {
        var status = TransferPlan.Open(state, sourceId, destinationId, out var plan);
        if (plan is null)
            return Reject(state, status);
        var stack = Find(plan.Original, slot);
        if (quantity == 0 || quantity > stack.Quantity)
            return Reject(state, InventoryChangeStatus.Invalid);
        return plan.Complete(plan.Stage(stack, quantity), allowOverload);
    }

    public static InventoryChange AcquireOre(InventoryState state, int quantity, bool allowOverload = false)
    {
        if (quantity <= 0 || !CanEdit(state))
            return Reject(state, InventoryChangeStatus.Invalid);
        var load = state.Load;
        var ore = InventoryItems.Get(InventoryItem.Ore);
        if (!InventoryLoad.Fits(checked(load.Weight + (long)quantity * ore.Grams),
                checked(load.Volume + (long)quantity * ore.Milliliters), load.WeightCapacity, load.VolumeCapacity,
                overload: true))
            return Reject(state, InventoryChangeStatus.CapacityExceeded);
        var carried = state.Carried.ToList();
        if (quantity > Available(carried, state.CarriedSlots, InventoryItem.Ore, out _))
            return Reject(state, InventoryChangeStatus.CapacityExceeded);
        Insert(carried, state.CarriedSlots, InventoryItem.Ore, quantity);
        return AdmitIntake(state, state with { Carried = [.. carried] }, [new(InventoryItem.Ore, quantity)], [],
            allowOverload);
    }

    public static InventoryChange Equip(InventoryState state, ushort slot)
    {
        var stack = Find(state.Carried, slot);
        if (!CanEdit(state) || stack.Quantity != 1 || !InventoryItems.TryGet(stack.Item, out var item) ||
            item.EquipmentSlot is not { } equipmentSlot)
            return Reject(state, InventoryChangeStatus.Invalid);
        if (Find(state.Equipped, equipmentSlot).Quantity != 0)
            return Reject(state, InventoryChangeStatus.CapacityExceeded);
        var carried = state.Carried.ToList();
        Remove(carried, stack, 1);
        var equipped = state.Equipped.Add(stack with { Slot = equipmentSlot }).Sort(SlotOrder);
        return Commit(state, state with { Carried = [.. carried], Equipped = equipped }, [new(stack.Item, 1)], []);
    }

    public static InventoryChange Unequip(InventoryState state, ushort slot)
    {
        var stack = Find(state.Equipped, slot);
        if (!CanEdit(state) || stack.Quantity != 1)
            return Reject(state, InventoryChangeStatus.Invalid);
        var carried = state.Carried.ToList();
        // Worn items remain owned even when removing a backpack shrinks the usable slots.
        var slots = stack.Item == InventoryItem.Backpack ? InventoryState.MaximumCarriedRecords : state.CarriedSlots;
        if (Available(carried, slots, stack.Item, out _) == 0)
            return Reject(state, InventoryChangeStatus.CapacityExceeded);
        Insert(carried, slots, stack.Item, 1);
        var equipped = state.Equipped.Remove(stack);
        return Commit(state, state with { Carried = [.. carried], Equipped = equipped }, [new(stack.Item, 1)], []);
    }

    public static InventoryChange PlaceChest(InventoryState state, ushort slot, PlanetVector? supportedPosition)
    {
        if (!CanCreateContainer(state))
            return Reject(state, InventoryChangeStatus.SaveBudget);
        var stack = Find(state.Carried, slot);
        if (stack is not { Item: InventoryItem.Chest, Quantity: 1 })
            return Reject(state, InventoryChangeStatus.Invalid);
        if (supportedPosition is not { } position || !InventoryState.ValidPosition(position))
            return Reject(state, InventoryChangeStatus.AwaitingSupport);
        var chest = new InventoryContainer(state.NextContainerId, InventoryContainerKind.Chest, position, []);
        var staged = AddContainer(state with { Carried = state.Carried.Remove(stack) }, chest);
        return Commit(state, staged, [new(InventoryItem.Chest, 1)], []);
    }

    public static InventoryChange Drop(InventoryState state, ushort slot, ushort quantity, PlanetVector? supportedPosition)
    {
        if (!CanCreateContainer(state))
            return Reject(state, InventoryChangeStatus.SaveBudget);
        var stack = Find(state.Carried, slot);
        if (quantity == 0 || quantity > stack.Quantity)
            return Reject(state, InventoryChangeStatus.Invalid);
        if (supportedPosition is not { } position || !InventoryState.ValidPosition(position))
            return Reject(state, InventoryChangeStatus.AwaitingSupport);
        var carried = state.Carried.ToList();
        Remove(carried, stack, quantity);
        var drop = new InventoryContainer(state.NextContainerId, InventoryContainerKind.Drop, position,
            [stack with { Slot = 0, Quantity = quantity }]);
        var staged = AddContainer(state with { Carried = [.. carried] }, drop);
        var moved = ImmutableArray.Create(new InventoryAmount(stack.Item, quantity));
        var result = Commit(state, staged, moved, []);
        if (result.Status == InventoryChangeStatus.SaveBudget && quantity == stack.Quantity && state.Load.MovementHeld)
            result = Commit(state, AddContainer(state with { Carried = [.. carried] },
                drop with { Kind = InventoryContainerKind.ReliefDrop }), moved, []);
        return result;
    }

    public static InventoryChange Relieve(InventoryState state, PlanetVector? supportedPosition)
    {
        if (!CanCreateContainer(state))
            return Reject(state, InventoryChangeStatus.SaveBudget);
        if (state.Carried.IsEmpty && state.Equipped.IsEmpty)
            return Reject(state, InventoryChangeStatus.NothingFits);
        if (supportedPosition is not { } position || !InventoryState.ValidPosition(position))
            return Reject(state, InventoryChangeStatus.AwaitingSupport);
        var stacks = state.Carried.Concat(state.Equipped)
            .Select(static (stack, index) => stack with { Slot = (ushort)index }).ToImmutableArray();
        var drop = new InventoryContainer(state.NextContainerId, InventoryContainerKind.ReliefDrop, position, stacks);
        return Commit(state, AddContainer(state with { Carried = [], Equipped = [] }, drop), Amounts(stacks), []);
    }

    public static InventoryChange Dismantle(InventoryState state, ulong chestId, PlanetVector? supportedPosition)
    {
        if (!CanEdit(state))
            return Reject(state, InventoryChangeStatus.Invalid);
        var chest = FindContainer(state, chestId);
        if (chest?.Kind != InventoryContainerKind.Chest)
            return Reject(state, InventoryChangeStatus.NotFound);
        if (supportedPosition is not { } position || !InventoryState.ValidPosition(position))
            return Reject(state, InventoryChangeStatus.AwaitingSupport);
        var stacks = chest.Stacks.Add(new(InventoryState.ChestSlots, InventoryItem.Chest, 1));
        var drop = chest with { Kind = InventoryContainerKind.Drop, Position = position, Stacks = stacks };
        return Commit(state, state with { Containers = state.Containers.Replace(chest, drop) }, Amounts(stacks), []);
    }

    private static bool CanEdit(InventoryState state) => state.Revision < ulong.MaxValue;

    private static bool CanCreateContainer(InventoryState state) =>
        CanEdit(state) && state.NextContainerId < ulong.MaxValue && state.Containers.Length < InventoryState.MaximumContainers;

    private static InventoryContainer? FindContainer(InventoryState state, ulong id) =>
        state.Containers.FirstOrDefault(owner => owner.Id == id);

    private static InventoryStack Find(ImmutableArray<InventoryStack> stacks, ushort slot)
    {
        foreach (var stack in stacks)
            if (stack.Slot == slot)
                return stack;
        return default;
    }

    private static readonly Comparison<InventoryStack> SlotOrder = static (left, right) => left.Slot.CompareTo(right.Slot);
    private static readonly IComparer<InventoryStack> SlotComparer = Comparer<InventoryStack>.Create(SlotOrder);

    private static InventoryState AddContainer(InventoryState state, InventoryContainer container) =>
        state with { NextContainerId = checked(state.NextContainerId + 1), Containers = state.Containers.Add(container) };

    private static InventoryChange AdmitIntake(InventoryState before, InventoryState candidate,
        ImmutableArray<InventoryAmount> moved, ImmutableArray<InventoryAmount> remaining, bool allowOverload)
    {
        var result = Commit(before, candidate, moved, remaining);
        if (result.Status != InventoryChangeStatus.Applied)
            return result;
        var load = candidate.Load;
        return !allowOverload && load.Overloaded
            ? new(InventoryChangeStatus.OverloadConfirmation, before, moved, remaining, load)
            : result with { ProjectedLoad = load };
    }

    private static InventoryChange Commit(InventoryState before, InventoryState candidate,
        ImmutableArray<InventoryAmount> moved, ImmutableArray<InventoryAmount> remaining)
    {
        var budget = candidate.Measure();
        if (!InventoryState.FitsBudget(budget.Regular, budget.Relief,
                !candidate.Carried.IsEmpty || !candidate.Equipped.IsEmpty))
            return Reject(before, InventoryChangeStatus.SaveBudget);
        if (!candidate.IsValid())
            return Reject(before, InventoryChangeStatus.Invalid);
        return new(InventoryChangeStatus.Applied, candidate with { Revision = checked(before.Revision + 1) }, moved, remaining);
    }

    private static InventoryChange Reject(InventoryState state, InventoryChangeStatus status) => new(status, state, [], []);

    private static ImmutableArray<InventoryAmount> Amounts(IEnumerable<InventoryStack> stacks) =>
        [.. stacks.GroupBy(static stack => stack.Item)
            .Select(static group => new InventoryAmount(group.Key, group.Sum(static stack => (int)stack.Quantity)))];

    private static int Available(List<InventoryStack> stacks, int slots, InventoryItem item, out int merge)
    {
        var limit = InventoryItems.Get(item).StackLimit;
        var occupied = 0;
        merge = 0;
        foreach (var stack in stacks)
        {
            if (stack.Slot >= slots)
                break;
            occupied++;
            if (stack.Item == item)
                merge += limit - stack.Quantity;
        }
        return merge + (slots - occupied) * limit;
    }

    private static void Insert(List<InventoryStack> stacks, int slots, InventoryItem item, int quantity)
    {
        var limit = InventoryItems.Get(item).StackLimit;
        for (var index = 0; index < stacks.Count && quantity > 0; index++)
        {
            var stack = stacks[index];
            if (stack.Slot >= slots)
                break;
            if (stack.Item != item || stack.Quantity == limit)
                continue;
            var add = Math.Min(quantity, limit - stack.Quantity);
            stacks[index] = stack with { Quantity = (ushort)(stack.Quantity + add) };
            quantity -= add;
        }
        for (var slot = 0; quantity > 0; slot++)
        {
            var index = stacks.BinarySearch(new((ushort)slot, item, 0), SlotComparer);
            if (index >= 0)
                continue;
            var add = Math.Min(quantity, limit);
            stacks.Insert(~index, new((ushort)slot, item, (ushort)add));
            quantity -= add;
        }
    }

    private static void Remove(List<InventoryStack> stacks, InventoryStack stack, ushort quantity)
    {
        var index = stacks.BinarySearch(stack, SlotComparer);
        if (quantity == stack.Quantity)
            stacks.RemoveAt(index);
        else
            stacks[index] = stack with { Quantity = (ushort)(stack.Quantity - quantity) };
    }

    private sealed class TransferPlan(InventoryState state, InventoryContainer? source, InventoryContainer? destination)
    {
        public ImmutableArray<InventoryStack> Original { get; } = source?.Stacks ?? state.Carried;
        private readonly List<InventoryStack> _source = (source?.Stacks ?? state.Carried).ToList();
        private readonly List<InventoryStack> _destination = (destination?.Stacks ?? state.Carried).ToList();
        private readonly List<InventoryStack> _moved = [];
        private readonly int _slots = destination is null ? state.CarriedSlots : InventoryState.ChestSlots;
        private readonly InventoryLoad _targetLoad = DestinationLoad(state, destination);
        private readonly (int Regular, int Relief) _budget = state.Measure();
        private long _addedWeight;
        private long _addedVolume;
        private int _regularDelta;
        private int _reliefDelta;

        private static InventoryLoad DestinationLoad(InventoryState state, InventoryContainer? destination)
        {
            if (destination is null)
                return state.Load;
            var totals = InventoryState.Totals(destination.Stacks);
            return new(totals.Weight, totals.Volume, 80_000, 120_000);
        }

        public static InventoryChangeStatus Open(InventoryState state, ulong sourceId, ulong destinationId, out TransferPlan? plan)
        {
            plan = null;
            if (sourceId == destinationId || !CanEdit(state))
                return InventoryChangeStatus.Invalid;
            var source = FindContainer(state, sourceId);
            var destination = FindContainer(state, destinationId);
            if ((sourceId != 0 && source is null) || (destinationId != 0 && destination is null))
                return InventoryChangeStatus.NotFound;
            if (destination is not null && destination.Kind != InventoryContainerKind.Chest)
                return InventoryChangeStatus.Invalid;
            plan = new(state, source, destination);
            return InventoryChangeStatus.Applied;
        }

        public InventoryChangeStatus Stage(InventoryStack stack, ushort quantity)
        {
            var item = InventoryItems.Get(stack.Item);
            var weight = checked(_addedWeight + (long)item.Grams * quantity);
            var volume = checked(_addedVolume + (long)item.Milliliters * quantity);
            if (!InventoryLoad.Fits(checked(_targetLoad.Weight + weight), checked(_targetLoad.Volume + volume),
                    _targetLoad.WeightCapacity, _targetLoad.VolumeCapacity,
                    overload: destination is null) ||
                quantity > Available(_destination, _slots, stack.Item, out var merge))
                return InventoryChangeStatus.CapacityExceeded;

            var wholeStack = quantity == stack.Quantity;
            var addedStacks = (Math.Max(0, quantity - merge) + item.StackLimit - 1) / item.StackLimit;
            var regular = _budget.Regular + _regularDelta + InventoryState.StackBytes * (addedStacks - (wholeStack ? 1 : 0));
            var relief = _budget.Relief + _reliefDelta;
            if (_source.Count == 1 && wholeStack && source is { Kind: not InventoryContainerKind.Chest })
            {
                if (source.Kind == InventoryContainerKind.ReliefDrop)
                    relief -= InventoryState.ContainerBytes;
                else
                    regular -= InventoryState.ContainerBytes;
            }
            var reservesRelief = destination is null || !state.Equipped.IsEmpty ||
                                 (source is null && (_source.Count > 1 || !wholeStack));
            if (!InventoryState.FitsBudget(regular, relief, reservesRelief))
                return InventoryChangeStatus.SaveBudget;

            Remove(_source, stack, quantity);
            Insert(_destination, _slots, stack.Item, quantity);
            _moved.Add(stack with { Quantity = quantity });
            (_addedWeight, _addedVolume) = (weight, volume);
            (_regularDelta, _reliefDelta) = (regular - _budget.Regular, relief - _budget.Relief);
            return InventoryChangeStatus.Applied;
        }

        public InventoryChange Complete(InventoryChangeStatus rejection, bool allowOverload)
        {
            var remaining = Amounts(_source);
            if (_moved.Count == 0)
                return new(rejection, state, [], remaining);

            var owners = ImmutableArray.CreateBuilder<InventoryContainer>(state.Containers.Length);
            foreach (var owner in state.Containers)
            {
                if (owner.Id == source?.Id)
                {
                    if (_source.Count > 0 || owner.Kind == InventoryContainerKind.Chest)
                        owners.Add(owner with { Stacks = [.. _source] });
                }
                else
                    owners.Add(owner.Id == destination?.Id ? owner with { Stacks = [.. _destination] } : owner);
            }
            var candidate = state with
            {
                Carried = source is null ? [.. _source] : destination is null ? [.. _destination] : state.Carried,
                Containers = owners.ToImmutable()
            };
            var moved = Amounts(_moved);
            return destination is null
                ? AdmitIntake(state, candidate, moved, remaining, allowOverload)
                : Commit(state, candidate, moved, remaining);
        }
    }
}
