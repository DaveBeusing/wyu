using System.Collections.Immutable;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static System.Buffers.Binary.BinaryPrimitives;

namespace WorldsYetUnseen.Client;

/// <summary>Versioned game state inside the volumetric checkpoint. Terrain and player are separate fields.</summary>
internal static class InventoryCheckpoint
{
    internal const int HeaderBytes = 16;
    internal const int ConstructionBytesLimit = ConstructionCheckpoint.Limit;
    internal const int OtherBytesLimit = 8 * 1024;
    private const int EnvironmentBytes = 17;
    private const int EnvironmentWithLandingBytes = 53;

    internal static bool IsCurrent(ReadOnlySpan<byte> bytes) => bytes.StartsWith("WYUGAME1"u8);

    internal static SaveGameValidation Validate(VolumeCheckpoint checkpoint)
    {
        if (IsCurrent(checkpoint.Game))
            return Read(checkpoint, out _, out _, out _);
        if (IsLegacyCave(checkpoint))
            return CaveInventory.ReadLegacy(checkpoint, out _, out _);
        return ProceduralLanding.Read(checkpoint, checkpoint.Game, out _, out _);
    }

    internal static SaveGameValidation Read(VolumeCheckpoint checkpoint, out InventoryState? inventory,
        out ProceduralLanding? landing, out ulong environmentTick) =>
        Read(checkpoint, out inventory, out _, out landing, out environmentTick);

    internal static SaveGameValidation Read(VolumeCheckpoint checkpoint, out InventoryState? inventory,
        out ConstructionState? construction, out ProceduralLanding? landing, out ulong environmentTick)
    {
        inventory = null;
        construction = null;
        landing = null;
        environmentTick = ReferenceWeather.InitialTick;
        var bytes = checkpoint.Game;
        if (!IsCurrent(bytes))
        {
            if (IsLegacyCave(checkpoint))
            {
                var cave = CaveInventory.ReadLegacy(checkpoint, out var ore, out var revision);
                if (cave != SaveGameValidation.Valid)
                    return cave;
                if (!InventoryState.TryFromLegacyOre(ore, revision, out var restored))
                    return SaveGameValidation.Unsupported;
                inventory = restored;
                construction = ConstructionState.Empty;
                return SaveGameValidation.Valid;
            }
            var legacy = ProceduralLanding.Read(checkpoint, bytes, out landing, out environmentTick);
            if (legacy == SaveGameValidation.Valid)
            {
                inventory = InventoryState.Empty;
                construction = ConstructionState.Empty;
            }
            return legacy;
        }

        if (bytes.Length < HeaderBytes || ReadUInt16LittleEndian(bytes[14..]) != 0)
            return SaveGameValidation.Malformed;
        var inventoryLength = ReadUInt16LittleEndian(bytes[8..]);
        var constructionLength = ReadUInt16LittleEndian(bytes[10..]);
        var otherLength = ReadUInt16LittleEndian(bytes[12..]);
        if (inventoryLength < InventoryState.SectionHeaderBytes || inventoryLength > InventoryState.SectionBytesLimit ||
            constructionLength > ConstructionBytesLimit || HeaderBytes + otherLength > OtherBytesLimit ||
            bytes.Length != HeaderBytes + inventoryLength + constructionLength + otherLength)
            return SaveGameValidation.Malformed;
        var inventoryEnd = HeaderBytes + inventoryLength;
        var constructionStatus = ConstructionCheckpoint.Read(bytes.Slice(inventoryEnd, constructionLength), out var buildings);
        if (constructionStatus != SaveGameValidation.Valid)
            return constructionStatus;
        if (otherLength is not (EnvironmentBytes or EnvironmentWithLandingBytes))
            return SaveGameValidation.Malformed;
        var other = bytes[(inventoryEnd + constructionLength)..];
        if (!other.StartsWith("WYUVOL03"u8))
            return SaveGameValidation.Unsupported;
        var environment = ProceduralLanding.Read(checkpoint, other, out landing, out environmentTick);
        if (environment != SaveGameValidation.Valid)
            return environment;
        var inventoryStatus = ReadInventory(bytes.Slice(HeaderBytes, inventoryLength), out inventory);
        if (inventoryStatus == SaveGameValidation.Valid)
            construction = buildings;
        return inventoryStatus;
    }

    internal static int Write(InventoryState inventory, ProceduralLanding? landing, PlanetWalkState? player,
        ulong terrainRevision, ulong environmentTick, Span<byte> destination) =>
        Write(inventory, ConstructionState.Empty, landing, player, terrainRevision, environmentTick, destination);

    internal static int Write(InventoryState inventory, ConstructionState construction, ProceduralLanding? landing,
        PlanetWalkState? player, ulong terrainRevision, ulong environmentTick, Span<byte> destination)
    {
        if (player is null || !inventory.IsValid() || !construction.IsValid())
            return -1;
        var inventoryLength = inventory.EncodedBytes;
        var constructionLength = ConstructionCheckpoint.Measure(construction);
        if (constructionLength > ConstructionBytesLimit)
            return -1;
        var inventoryEnd = HeaderBytes + inventoryLength;
        var constructionEnd = inventoryEnd + constructionLength;
        Span<byte> other = stackalloc byte[EnvironmentWithLandingBytes];
        var otherLength = ProceduralLanding.Write(landing, player, terrainRevision, environmentTick, other);
        var length = constructionEnd + otherLength;
        if (length > destination.Length || length > VolumeCheckpoint.MaximumGameBytes)
            return -1;
        "WYUGAME1"u8.CopyTo(destination);
        WriteUInt16LittleEndian(destination[8..], (ushort)inventoryLength);
        WriteUInt16LittleEndian(destination[10..], (ushort)constructionLength);
        WriteUInt16LittleEndian(destination[12..], (ushort)otherLength);
        WriteUInt16LittleEndian(destination[14..], 0);
        WriteInventory(inventory, destination.Slice(HeaderBytes, inventoryLength));
        ConstructionCheckpoint.Write(construction, destination.Slice(inventoryEnd, constructionLength));
        other[..otherLength].CopyTo(destination[constructionEnd..]);
        return length;
    }

    private static bool IsLegacyCave(VolumeCheckpoint checkpoint) =>
        checkpoint.Planet.GeneratorVersion is PlanetGeologyField.GeneratorVersion or PlanetGeologyField.ConnectedGeneratorVersion;

    private static SaveGameValidation ReadInventory(ReadOnlySpan<byte> bytes, out InventoryState? inventory)
    {
        inventory = null;
        var revision = ReadUInt64LittleEndian(bytes);
        var nextId = ReadUInt64LittleEndian(bytes[8..]);
        var carried = ReadUInt16LittleEndian(bytes[16..]);
        var equipped = ReadUInt16LittleEndian(bytes[18..]);
        var owners = ReadUInt16LittleEndian(bytes[20..]);
        if (revision == 0 || nextId == 0 || ReadUInt16LittleEndian(bytes[22..]) != 0 ||
            owners > InventoryState.MaximumContainers)
            return SaveGameValidation.Malformed;

        // Scan sizes, slot order, IDs, counts, and budgets before materializing collections.
        var remaining = bytes[InventoryState.SectionHeaderBytes..];
        var validation = ValidateStacks(ref remaining, carried, InventoryState.MaximumCarriedRecords);
        if (validation != SaveGameValidation.Valid)
            return validation;
        validation = ValidateStacks(ref remaining, equipped, InventoryState.EquipmentSlots, equipment: true);
        if (validation != SaveGameValidation.Valid)
            return validation;
        var relief = 0;
        ulong previousId = 0;
        for (var i = 0; i < owners; i++)
        {
            if (remaining.Length < InventoryState.ContainerBytes)
                return SaveGameValidation.Malformed;
            var id = ReadUInt64LittleEndian(remaining);
            var kind = (InventoryContainerKind)remaining[8];
            var count = ReadUInt16LittleEndian(remaining[10..]);
            var position = ReadPosition(remaining);
            if (id <= previousId || id >= nextId || remaining[9] != 0 ||
                !double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z) ||
                position == default)
                return SaveGameValidation.Malformed;
            if (kind is not (InventoryContainerKind.Chest or InventoryContainerKind.Drop or InventoryContainerKind.ReliefDrop))
                return SaveGameValidation.Unsupported;
            remaining = remaining[InventoryState.ContainerBytes..];
            if (kind != InventoryContainerKind.Chest && count == 0)
                return SaveGameValidation.Malformed;
            validation = ValidateStacks(ref remaining, count, kind == InventoryContainerKind.Chest
                ? InventoryState.ChestSlots : InventoryState.DropSlots, chest: kind == InventoryContainerKind.Chest);
            if (validation != SaveGameValidation.Valid)
                return validation;
            if (kind == InventoryContainerKind.ReliefDrop)
                relief += InventoryState.ContainerBytes;
            previousId = id;
        }
        var reserve = carried != 0 || equipped != 0 ? InventoryState.ContainerBytes : 0;
        if (!remaining.IsEmpty || bytes.Length - relief > InventoryState.RegularBytesLimit ||
            relief + reserve > InventoryState.ReliefBytesLimit)
            return SaveGameValidation.Malformed;

        remaining = bytes[InventoryState.SectionHeaderBytes..];
        var carriedStacks = ReadStacks(ref remaining, carried);
        var equippedStacks = ReadStacks(ref remaining, equipped);
        var containers = ImmutableArray.CreateBuilder<InventoryContainer>(owners);
        for (var i = 0; i < owners; i++)
        {
            var id = ReadUInt64LittleEndian(remaining);
            var kind = (InventoryContainerKind)remaining[8];
            var count = ReadUInt16LittleEndian(remaining[10..]);
            var position = ReadPosition(remaining);
            remaining = remaining[InventoryState.ContainerBytes..];
            containers.Add(new(id, kind, position, ReadStacks(ref remaining, count)));
        }
        var candidate = new InventoryState(revision, nextId, carriedStacks, equippedStacks, containers.MoveToImmutable());
        if (!candidate.IsValid())
            return SaveGameValidation.Malformed;
        inventory = candidate;
        return SaveGameValidation.Valid;
    }

    private static SaveGameValidation ValidateStacks(ref ReadOnlySpan<byte> bytes, int count, int slotLimit,
        bool equipment = false, bool chest = false)
    {
        if (count > slotLimit || bytes.Length < count * InventoryState.StackBytes)
            return SaveGameValidation.Malformed;
        var previousSlot = -1;
        long weight = 0;
        long volume = 0;
        for (var i = 0; i < count; i++)
        {
            var stack = ReadStack(bytes);
            if (!InventoryItems.TryGet(stack.Item, out var definition))
                return SaveGameValidation.Unsupported;
            if (stack.Slot <= previousSlot || stack.Slot >= slotLimit || stack.Quantity == 0 ||
                stack.Quantity > definition.StackLimit || equipment && definition.EquipmentSlot != stack.Slot)
                return SaveGameValidation.Malformed;
            if (chest)
            {
                weight = checked(weight + (long)definition.Grams * stack.Quantity);
                volume = checked(volume + (long)definition.Milliliters * stack.Quantity);
            }
            previousSlot = stack.Slot;
            bytes = bytes[InventoryState.StackBytes..];
        }
        return weight > 80_000 || volume > 120_000 ? SaveGameValidation.Malformed : SaveGameValidation.Valid;
    }

    private static InventoryStack ReadStack(ReadOnlySpan<byte> bytes) => new(ReadUInt16LittleEndian(bytes),
        (InventoryItem)ReadUInt16LittleEndian(bytes[2..]), ReadUInt16LittleEndian(bytes[4..]));

    private static ImmutableArray<InventoryStack> ReadStacks(ref ReadOnlySpan<byte> bytes, int count)
    {
        var result = ImmutableArray.CreateBuilder<InventoryStack>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(ReadStack(bytes));
            bytes = bytes[InventoryState.StackBytes..];
        }
        return result.MoveToImmutable();
    }

    private static PlanetVector ReadPosition(ReadOnlySpan<byte> bytes) => new(ReadDoubleLittleEndian(bytes[12..]),
        ReadDoubleLittleEndian(bytes[20..]), ReadDoubleLittleEndian(bytes[28..]));

    private static void WriteInventory(InventoryState inventory, Span<byte> bytes)
    {
        WriteUInt64LittleEndian(bytes, inventory.Revision);
        WriteUInt64LittleEndian(bytes[8..], inventory.NextContainerId);
        WriteUInt16LittleEndian(bytes[16..], (ushort)inventory.Carried.Length);
        WriteUInt16LittleEndian(bytes[18..], (ushort)inventory.Equipped.Length);
        WriteUInt16LittleEndian(bytes[20..], (ushort)inventory.Containers.Length);
        WriteUInt16LittleEndian(bytes[22..], 0);
        bytes = bytes[InventoryState.SectionHeaderBytes..];
        WriteStacks(inventory.Carried, ref bytes);
        WriteStacks(inventory.Equipped, ref bytes);
        foreach (var container in inventory.Containers)
        {
            WriteUInt64LittleEndian(bytes, container.Id);
            bytes[8] = (byte)container.Kind;
            bytes[9] = 0;
            WriteUInt16LittleEndian(bytes[10..], (ushort)container.Stacks.Length);
            WriteDoubleLittleEndian(bytes[12..], container.Position.X);
            WriteDoubleLittleEndian(bytes[20..], container.Position.Y);
            WriteDoubleLittleEndian(bytes[28..], container.Position.Z);
            bytes = bytes[InventoryState.ContainerBytes..];
            WriteStacks(container.Stacks, ref bytes);
        }
    }

    private static void WriteStacks(ImmutableArray<InventoryStack> stacks, ref Span<byte> bytes)
    {
        foreach (var stack in stacks)
        {
            WriteUInt16LittleEndian(bytes, stack.Slot);
            WriteUInt16LittleEndian(bytes[2..], (ushort)stack.Item);
            WriteUInt16LittleEndian(bytes[4..], stack.Quantity);
            bytes = bytes[InventoryState.StackBytes..];
        }
    }
}
