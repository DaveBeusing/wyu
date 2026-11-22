using System.Collections.Immutable;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static System.Buffers.Binary.BinaryPrimitives;

namespace WorldsYetUnseen.Client;

internal static class InventoryCheckpointVerification
{
    internal static int Run()
    {
        RoundTripAndMalformedPayloads();
        CheckSharedByteLimits();
        ConstructionRoundTripAndCompatibility();
        VerifyLegacyMapping();
        VerifyOreAdmission();
        Console.WriteLine("Inventory checkpoint proof passed: round trip, malformed data, shared limits, legacy mapping and ore admission.");
        Console.WriteLine("Construction checkpoint proof passed: 32 anchors, 1,440 blocks, mixed round trip, format limits and compatibility.");
        return 0;
    }

    private static void RoundTripAndMalformedPayloads()
    {
        var planet = new ReferencePlanet(Guid.NewGuid(), 42).Descriptor;
        var player = Pose(planet);
        var state = InventoryState.Starter with
        {
            Revision = 7,
            NextContainerId = 4,
            Containers =
            [
                new(1, InventoryContainerKind.Chest, player.Position, [new(0, InventoryItem.Ore, 15)]),
                new(3, InventoryContainerKind.ReliefDrop, player.Position with { X = 2 }, [new(2, InventoryItem.Ore, 2)])
            ]
        };
        var landing = new ProceduralLanding(player.Position, 5, 100);
        Span<byte> bytes = stackalloc byte[256];
        var length = InventoryCheckpoint.Write(state, landing, player, 5, 123, bytes);
        Check(length == InventoryCheckpoint.HeaderBytes + state.EncodedBytes + 53, "incorrect measured byte count");
        Check(InventoryCheckpoint.Write(state, landing, player, 5, 123, bytes[..(length - 1)]) == -1, "small writer buffer accepted");
        var saved = Checkpoint(planet, player, bytes[..length]);
        Check(InventoryCheckpoint.Read(saved, out var loaded, out var emptyConstruction, out var loadedLanding, out var tick) ==
            SaveGameValidation.Valid && loaded is not null && emptyConstruction == ConstructionState.Empty &&
            loadedLanding == landing && tick == 123 && loaded.Revision == state.Revision, "checkpoint round trip with a legacy empty construction section");
        Span<byte> reencoded = stackalloc byte[256];
        var reencodedLength = InventoryCheckpoint.Write(loaded!, loadedLanding, player, 5, tick, reencoded);
        Check(reencodedLength == length && saved.Game.SequenceEqual(reencoded[..reencodedLength]), "noncanonical round trip");
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes[..(length - 1)])) == SaveGameValidation.Malformed,
            "truncated checkpoint accepted");

        var firstStack = InventoryCheckpoint.HeaderBytes + InventoryState.SectionHeaderBytes;
        WriteUInt16LittleEndian(bytes[(firstStack + 4)..], 0);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes[..length])) == SaveGameValidation.Malformed,
            "invalid item quantity accepted");
        WriteUInt16LittleEndian(bytes[(firstStack + 4)..], 1);
        WriteUInt16LittleEndian(bytes[(firstStack + 2)..], ushort.MaxValue);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes[..length])) == SaveGameValidation.Unsupported,
            "unknown item ID not protected as incompatible");
        bytes[7] = (byte)'2';
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes[..length])) == SaveGameValidation.Unsupported,
            "future game schema not protected as incompatible");
    }

    private static void CheckSharedByteLimits()
    {
        var planet = new ReferencePlanet(Guid.NewGuid(), 42).Descriptor;
        var player = Pose(planet);
        Check(InventoryState.SectionBytesLimit + InventoryCheckpoint.ConstructionBytesLimit +
            InventoryCheckpoint.OtherBytesLimit == VolumeCheckpoint.MaximumGameBytes, "budget partitions do not fill the shared limit");
        var full = new byte[VolumeCheckpoint.MaximumGameBytes];
        "WYUGAME1"u8.CopyTo(full);
        WriteUInt16LittleEndian(full.AsSpan(8), InventoryState.SectionBytesLimit);
        WriteUInt16LittleEndian(full.AsSpan(10), InventoryCheckpoint.ConstructionBytesLimit);
        WriteUInt16LittleEndian(full.AsSpan(12), InventoryCheckpoint.OtherBytesLimit - InventoryCheckpoint.HeaderBytes);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, full)) == SaveGameValidation.Unsupported,
            "well-framed 64 KiB envelope must reach the reserved section compatibility check");
        WriteUInt16LittleEndian(full.AsSpan(12), InventoryCheckpoint.OtherBytesLimit - InventoryCheckpoint.HeaderBytes - 1);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, full.AsSpan(0, full.Length - 1))) == SaveGameValidation.Unsupported,
            "well-framed envelope one byte below 64 KiB did not reach the compatibility check");
        WriteUInt16LittleEndian(full.AsSpan(8), InventoryState.SectionBytesLimit + 1);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, full)) == SaveGameValidation.Malformed,
            "section exceeded its allotment while remaining within the shared limit");
        WriteUInt16LittleEndian(full.AsSpan(8), InventoryState.SectionBytesLimit - 1);
        WriteUInt16LittleEndian(full.AsSpan(10), InventoryCheckpoint.ConstructionBytesLimit + 1);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, full.AsSpan(0, full.Length - 1))) == SaveGameValidation.Malformed,
            "construction section exceeded its allotment below the shared limit");

        var oversizedRejected = false;
        try { _ = Checkpoint(planet, player, new byte[VolumeCheckpoint.MaximumGameBytes + 1]); }
        catch (InvalidDataException) { oversizedRejected = true; }
        Check(oversizedRejected, "outer checkpoint accepted more than 64 KiB of game payload");
    }

    private static void ConstructionRoundTripAndCompatibility()
    {
        var planet = new ReferencePlanet(Guid.NewGuid(), 42).Descriptor;
        var player = Pose(planet);
        var inventory = InventoryState.Starter with { Carried = [new(0, InventoryItem.Ore, 16)] };
        var landing = new ProceduralLanding(player.Position, 5, 100);
        var construction = MaxConstruction();
        var bytes = new byte[VolumeCheckpoint.MaximumGameBytes];
        var length = InventoryCheckpoint.Write(inventory, construction, landing, player, 5, 123, bytes);
        var sectionStart = InventoryCheckpoint.HeaderBytes + inventory.EncodedBytes;
        const int expectedSectionBytes = 24_080;
        Check(length == sectionStart + expectedSectionBytes + 53 &&
            ReadUInt16LittleEndian(bytes.AsSpan(10)) == expectedSectionBytes, "maximal construction section size is incorrect");
        var saved = Checkpoint(planet, player, bytes.AsSpan(0, length));
        Check(InventoryCheckpoint.Read(saved, out var loadedInventory, out var loaded, out var loadedLanding, out var tick) ==
            SaveGameValidation.Valid && loadedInventory?.OreCount == 16 && loaded is not null && loaded.NextId == construction.NextId &&
            loaded.Anchors.SequenceEqual(construction.Anchors) && loaded.Blocks.SequenceEqual(construction.Blocks) &&
            loadedLanding == landing && tick == 123, "maximal construction, inventory and environment did not round trip together");
        var rewritten = new byte[length];
        Check(InventoryCheckpoint.Write(loadedInventory!, loaded!, loadedLanding, player, 5, tick, rewritten) == length &&
            saved.Game.SequenceEqual(rewritten), "canonical construction bytes changed after load");

        var anchorStart = sectionStart + ConstructionCheckpoint.HeaderBytes;
        var blockStart = anchorStart + construction.Anchors.Length * ConstructionCheckpoint.AnchorBytes;
        var secondBlock = blockStart + ConstructionCheckpoint.BlockBytes;
        WriteUInt16LittleEndian(bytes.AsSpan(sectionStart + 4), 2);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes.AsSpan(0, length))) == SaveGameValidation.Unsupported,
            "future construction schema not protected as incompatible");
        Check(ConstructionCheckpoint.Read(bytes.AsSpan(sectionStart, 6), out _) == SaveGameValidation.Unsupported,
            "future construction schema was rejected based on the current header length");
        saved.Game.CopyTo(bytes);
        WriteUInt16LittleEndian(bytes.AsSpan(sectionStart + 6), ConstructionState.MaximumAnchors + 1);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes.AsSpan(0, length))) == SaveGameValidation.Malformed,
            "excess construction anchors accepted");
        saved.Game.CopyTo(bytes);
        WriteUInt16LittleEndian(bytes.AsSpan(sectionStart + 8), ConstructionState.MaximumBlocks + 1);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes.AsSpan(0, length))) == SaveGameValidation.Malformed,
            "excess construction blocks accepted");
        saved.Game.CopyTo(bytes);
        WriteUInt32LittleEndian(bytes.AsSpan(blockStart + 4), uint.MaxValue);
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes.AsSpan(0, length))) == SaveGameValidation.Malformed,
            "missing construction anchor accepted");
        saved.Game.CopyTo(bytes);
        bytes[blockStart + 15] = 24;
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes.AsSpan(0, length))) == SaveGameValidation.Malformed,
            "invalid construction orientation accepted");
        saved.Game.CopyTo(bytes);
        bytes.AsSpan(blockStart + 4, ConstructionCheckpoint.BlockBytes - 4).CopyTo(bytes.AsSpan(secondBlock + 4));
        Check(InventoryCheckpoint.Validate(Checkpoint(planet, player, bytes.AsSpan(0, length))) == SaveGameValidation.Malformed,
            "overlapping construction occupancy accepted");

        var retired = ConstructionState.Empty with { NextId = 3 };
        var retiredLength = InventoryCheckpoint.Write(inventory, retired, null, player, 5, 123, bytes);
        Check(ReadUInt16LittleEndian(bytes.AsSpan(10)) == ConstructionCheckpoint.HeaderBytes &&
            InventoryCheckpoint.Read(Checkpoint(planet, player, bytes.AsSpan(0, retiredLength)), out _, out var retiredLoaded,
                out _, out _) == SaveGameValidation.Valid && retiredLoaded?.NextId == 3 && retiredLoaded.Blocks.IsEmpty,
            "last block removal lost construction ID history");
    }

    private static ConstructionState MaxConstruction()
    {
        var anchors = ImmutableArray.CreateBuilder<ConstructionAnchor>(ConstructionState.MaximumAnchors);
        var blocks = ImmutableArray.CreateBuilder<ConstructionBlock>(ConstructionState.MaximumBlocks);
        const int blocksPerAnchor = ConstructionState.MaximumBlocks / ConstructionState.MaximumAnchors;
        uint nextId = ConstructionState.MaximumAnchors + 1;
        for (var i = 0; i < ConstructionState.MaximumAnchors; i++)
        {
            var anchorId = (uint)i + 1;
            anchors.Add(new(anchorId, 8_000_000L + 2_000L * i, 0, 0, 1));
            for (var b = 0; b < blocksPerAnchor; b++)
                blocks.Add(new(nextId++, anchorId, (short)(b * 3), 0, 0,
                    b % 2 == 0 ? ConstructionBlockType.Cube : ConstructionBlockType.Long, 0));
        }
        return new(1, nextId, anchors.MoveToImmutable(), blocks.MoveToImmutable());
    }

    private static void VerifyLegacyMapping()
    {
        var cave = new CavePlanet(Guid.NewGuid(), CavePlanet.Seed).Descriptor;
        var cavePlayer = Pose(cave);
        Span<byte> oreBytes = stackalloc byte[20];
        "WYUCAVE1"u8.CopyTo(oreBytes);
        WriteInt32LittleEndian(oreBytes[8..], 55);
        WriteUInt64LittleEndian(oreBytes[12..], 8);
        var legacy = Checkpoint(cave, cavePlayer, oreBytes);
        Check(InventoryCheckpoint.Read(legacy, out var inventory, out var landing, out var tick) == SaveGameValidation.Valid &&
            inventory?.OreCount == 55 && inventory.Revision == 8 && inventory.Carried.Length == 2 &&
            landing is null && tick == ReferenceWeather.InitialTick, "legacy ore or revision discarded");
        Span<byte> migrated = stackalloc byte[160];
        var length = InventoryCheckpoint.Write(inventory!, null, cavePlayer, legacy.TerrainRevision, tick, migrated);
        var newCut = Checkpoint(cave, cavePlayer, migrated[..length]);
        Check(newCut.Operation.SlotId != legacy.Operation.SlotId && newCut.Lineage != legacy.Lineage &&
            legacy.Game.SequenceEqual(oreBytes) && InventoryCheckpoint.Read(newCut, out var newInventory, out _, out _) == SaveGameValidation.Valid &&
            newInventory?.OreCount == 55 && newInventory.Revision == 8, "new cave cut changed legacy ownership or metadata");
        WriteInt32LittleEndian(oreBytes[8..], int.MaxValue);
        var oversized = Checkpoint(cave, cavePlayer, oreBytes);
        Check(InventoryCheckpoint.Validate(oversized) == SaveGameValidation.Valid &&
            InventoryCheckpoint.Read(oversized, out _, out _, out _) == SaveGameValidation.Unsupported,
            "valid legacy cave that exceeds migration capacity was not protected");

        var planet = new ReferencePlanet(Guid.NewGuid(), 42).Descriptor;
        var player = Pose(planet);
        var confirmed = new ProceduralLanding(player.Position, 5, 12);
        Span<byte> weather = stackalloc byte[53];
        var weatherLength = ProceduralLanding.Write(confirmed, player, 5, 456, weather);
        Check(InventoryCheckpoint.Read(Checkpoint(planet, player, weather[..weatherLength]), out _, out landing, out tick)
            == SaveGameValidation.Valid && landing == confirmed && tick == 456, "legacy touchdown or weather discarded");
        Check(InventoryCheckpoint.Read(Checkpoint(planet, player, "WYUVOL01"u8), out _, out landing, out tick)
            == SaveGameValidation.Valid && landing is null && tick == ReferenceWeather.InitialTick, "old reference save rejected");
    }

    private static PlanetWalkState Pose(PlanetDescriptor planet) =>
        new(new(0, planet.RadiusMeters, 0), new(1, 0, 0), default);

    private static void VerifyOreAdmission()
    {
        var world = new Groundwork.Core.WorldId(1);
        var source = new CaveInventory(world, 80, 2);
        var edit = new VolumeBrushIntent(5, 1, 3, new(0, 0, 0), new(2, 2, 2), VolumeBrushOperation.Remove);
        var ore = new VolumeOreDepletion(edit, CavePlanet.Ore, 16);
        Check(source.StageOreDepletion(ore) is null && source.Ore == 80, "unconfirmed overload credited ore");
        var confirmed = source with { OreApproval = new(edit, source.Items.Revision, ore.Deposits) };
        Check(confirmed.StageOreDepletion(ore with { Deposits = 1 }) is null &&
            confirmed.StageOreDepletion(ore with { Edit = edit with { Tick = 6 } }) is null,
            "ore confirmation escaped its count or edit");
        Check(confirmed.StageOreDepletion(ore) is CaveInventory { Ore: 96, OreApproval: null } after &&
            after.Items.Revision == source.Items.Revision + 1 && !after.Items.Load.MovementHeld && confirmed.Ore == 80,
            "exact 120% acquisition failed or altered the original snapshot");
        var denied = ore with { Deposits = 17 };
        Check((source with { OreApproval = new(edit, source.Items.Revision, denied.Deposits) }).StageOreDepletion(denied) is null,
            "confirmed ore exceeded the 120% ceiling");
    }

    private static VolumeCheckpoint Checkpoint(PlanetDescriptor planet, PlanetWalkState player, ReadOnlySpan<byte> bytes) =>
        new(new(Guid.NewGuid(), 0, Guid.NewGuid()), Guid.NewGuid(), planet, 7, 5, new byte[8], bytes, player: player);

    private static void Check(bool condition, string error)
    {
        if (!condition)
            throw new InvalidOperationException($"Inventory checkpoint proof failed: {error}");
    }
}
