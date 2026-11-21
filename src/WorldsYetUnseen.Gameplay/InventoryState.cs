using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay;

public enum InventoryItem : ushort
{
    Ore = 1,
    Chest = 2,
    Backpack = 3,
    Coat = 4,
    Tool = 5,
    Weapon = 6
}

public enum InventoryContainerKind : byte
{
    Chest = 1,
    Drop = 2,
    ReliefDrop = 3
}

public readonly record struct InventoryItemDefinition(int Grams, int Milliliters, ushort StackLimit,
    ushort? EquipmentSlot = null, bool PackedVolumeOnly = false);

public static class InventoryItems
{
    public static bool TryGet(InventoryItem item, out InventoryItemDefinition definition)
    {
        definition = item switch
        {
            InventoryItem.Ore => new(250, 100, 40),
            InventoryItem.Chest => new(2_500, 4_000, 1),
            InventoryItem.Backpack => new(800, 2_500, 1, 0, PackedVolumeOnly: true),
            InventoryItem.Coat => new(1_250, 3_000, 1, 1, PackedVolumeOnly: true),
            InventoryItem.Tool => new(1_500, 2_000, 1, 2),
            InventoryItem.Weapon => new(2_000, 3_000, 1, 3),
            _ => default
        };
        return definition.StackLimit != 0;
    }

    internal static InventoryItemDefinition Get(InventoryItem item) =>
        TryGet(item, out var definition) ? definition : throw new ArgumentOutOfRangeException(nameof(item));
}

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "A stack is the gameplay unit occupying one inventory slot.")]
public readonly record struct InventoryStack(ushort Slot, InventoryItem Item, ushort Quantity);

public sealed record InventoryContainer(ulong Id, InventoryContainerKind Kind, PlanetVector Position,
    ImmutableArray<InventoryStack> Stacks);

public readonly record struct InventoryLoad(long Weight, long Volume, long WeightCapacity, long VolumeCapacity)
{
    public double Relative => Math.Max((double)Weight / WeightCapacity, (double)Volume / VolumeCapacity);
    public bool Overloaded => Weight > WeightCapacity || Volume > VolumeCapacity;
    public bool MovementHeld => !Fits(Weight, Volume, WeightCapacity, VolumeCapacity, overload: true);
    public double SpeedScale => MovementHeld ? 0 : Overloaded ? 1 - 2 * (Relative - 1) : 1;

    internal static bool Fits(long weight, long volume, long weightCapacity, long volumeCapacity, bool overload) =>
        overload
            ? (Int128)weight * 5 <= (Int128)weightCapacity * 6 &&
              (Int128)volume * 5 <= (Int128)volumeCapacity * 6
            : weight <= weightCapacity && volume <= volumeCapacity;
}

/// <summary>An immutable ownership snapshot. Container ID zero denotes the carried inventory.</summary>
public sealed record InventoryState(ulong Revision, ulong NextContainerId, ImmutableArray<InventoryStack> Carried,
    ImmutableArray<InventoryStack> Equipped, ImmutableArray<InventoryContainer> Containers)
{
    public const int BaseSlots = 16;
    public const int BackpackSlots = 8;
    public const int ChestSlots = 24;
    public const int MaximumCarriedRecords = 256;
    public const int EquipmentSlots = 4;
    public const int DropSlots = MaximumCarriedRecords + EquipmentSlots;
    public const int SectionHeaderBytes = 24;
    public const int StackBytes = 6;
    public const int ContainerBytes = 36;
    public const int RegularBytesLimit = 30 * 1024;
    public const int ReliefBytesLimit = 2 * 1024;
    public const int SectionBytesLimit = RegularBytesLimit + ReliefBytesLimit;
    public const int MaximumContainers = SectionBytesLimit / ContainerBytes;

    public static InventoryState Empty { get; } = new(1, 1, [], [], []);
    public static InventoryState Starter { get; } = Empty with
    {
        Carried =
        [
            new(0, InventoryItem.Chest, 1),
            new(1, InventoryItem.Backpack, 1),
            new(2, InventoryItem.Coat, 1),
            new(3, InventoryItem.Tool, 1)
        ]
    };

    public bool HasBackpack => !Equipped.IsEmpty && Equipped[0].Item == InventoryItem.Backpack;
    public int CarriedSlots => BaseSlots + (HasBackpack ? BackpackSlots : 0);
    public int OreCount
    {
        get
        {
            var ore = 0;
            foreach (var stack in Carried)
                if (stack.Item == InventoryItem.Ore)
                    ore = checked(ore + stack.Quantity);
            return ore;
        }
    }

    public InventoryLoad Load
    {
        get
        {
            var backpack = HasBackpack;
            var packed = Totals(Carried);
            var equipped = Totals(Equipped, worn: true);
            return new(checked(packed.Weight + equipped.Weight), checked(packed.Volume + equipped.Volume),
                backpack ? 35_000 : 20_000, backpack ? 55_000 : 30_000);
        }
    }

    public int EncodedBytes
    {
        get
        {
            var budget = Measure();
            return checked(budget.Regular + budget.Relief);
        }
    }

    public bool IsValid()
    {
        if (Revision == 0 || NextContainerId == 0 || Containers.IsDefault || Containers.Length > MaximumContainers ||
            !ValidStacks(Carried, MaximumCarriedRecords) || !ValidStacks(Equipped, EquipmentSlots, equipment: true))
            return false;
        ulong previousId = 0;
        foreach (var container in Containers)
        {
            if (container is null || container.Id <= previousId || container.Id >= NextContainerId ||
                !ValidPosition(container.Position))
                return false;
            if (container.Kind == InventoryContainerKind.Chest)
            {
                if (!ValidStacks(container.Stacks, ChestSlots))
                    return false;
                var totals = Totals(container.Stacks);
                if (totals.Weight > 80_000 || totals.Volume > 120_000)
                    return false;
            }
            else if (container.Kind is not (InventoryContainerKind.Drop or InventoryContainerKind.ReliefDrop) ||
                     !ValidStacks(container.Stacks, DropSlots) || container.Stacks.IsEmpty)
                return false;
            previousId = container.Id;
        }
        var budget = Measure();
        return FitsBudget(budget.Regular, budget.Relief, !Carried.IsEmpty || !Equipped.IsEmpty);
    }

    public static bool TryFromLegacyOre(int ore, ulong revision, out InventoryState state)
    {
        state = Empty;
        if (ore < 0 || ore > MaximumCarriedRecords * InventoryItems.Get(InventoryItem.Ore).StackLimit || revision == 0)
            return false;
        var limit = InventoryItems.Get(InventoryItem.Ore).StackLimit;
        var stacks = ImmutableArray.CreateBuilder<InventoryStack>((ore + limit - 1) / limit);
        for (var remaining = ore; remaining > 0; remaining -= limit)
            stacks.Add(new((ushort)stacks.Count, InventoryItem.Ore, (ushort)Math.Min(limit, remaining)));
        state = Empty with { Revision = revision, Carried = stacks.MoveToImmutable() };
        return true;
    }

    internal (int Regular, int Relief) Measure()
    {
        var regular = checked(SectionHeaderBytes + (Carried.Length + Equipped.Length) * StackBytes);
        var relief = 0;
        foreach (var container in Containers)
        {
            regular = checked(regular + container.Stacks.Length * StackBytes);
            if (container.Kind == InventoryContainerKind.ReliefDrop)
                relief = checked(relief + ContainerBytes);
            else
                regular = checked(regular + ContainerBytes);
        }
        return (regular, relief);
    }

    internal static bool FitsBudget(int regular, int relief, bool reservesRelief) =>
        regular <= RegularBytesLimit && relief <= ReliefBytesLimit - (reservesRelief ? ContainerBytes : 0);

    internal static bool ValidPosition(PlanetVector position) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) && double.IsFinite(position.Z) && position != default;

    internal static (long Weight, long Volume) Totals(ImmutableArray<InventoryStack> stacks, bool worn = false)
    {
        long weight = 0;
        long volume = 0;
        foreach (var stack in stacks)
        {
            var definition = InventoryItems.Get(stack.Item);
            weight = checked(weight + (long)definition.Grams * stack.Quantity);
            if (!worn || !definition.PackedVolumeOnly)
                volume = checked(volume + (long)definition.Milliliters * stack.Quantity);
        }
        return (weight, volume);
    }

    private static bool ValidStacks(ImmutableArray<InventoryStack> stacks, int slots, bool equipment = false)
    {
        if (stacks.IsDefault || stacks.Length > slots)
            return false;
        var previousSlot = -1;
        foreach (var stack in stacks)
        {
            if (stack.Slot <= previousSlot || stack.Slot >= slots ||
                !InventoryItems.TryGet(stack.Item, out var item) || stack.Quantity == 0 ||
                stack.Quantity > item.StackLimit ||
                (equipment && (item.EquipmentSlot != stack.Slot || stack.Quantity != 1)))
                return false;
            previousSlot = stack.Slot;
        }
        return true;
    }
}
