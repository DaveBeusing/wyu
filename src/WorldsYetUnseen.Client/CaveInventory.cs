using System.Buffers.Binary;
using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

/// <summary>The immutable game state is composed into the same root as terrain depletion.</summary>
internal sealed record CaveInventory(WorldId World, InventoryState Items, ProceduralLanding? Landing = null) : IVolumeOreInventoryState
{
    private const int Bytes = 20;
    internal CaveInventory(WorldId world, int ore = 0, ulong revision = 1)
        : this(world, InventoryState.TryFromLegacyOre(ore, revision, out var items)
            ? items : throw new ArgumentException("Invalid legacy ore state.")) { }

    internal int Ore => Items.OreCount;
    internal ConstructionState Construction { get; init; } = ConstructionState.Empty;
    public ulong OreInventoryRevision => Items.Revision;
    internal InventoryOreApproval? OreApproval { get; init; }

    public ComponentTarget OreInventoryTarget => new(new(World, 1, 1), new(1));

    public IWorldStateRoot? StageOreDepletion(in VolumeOreDepletion depletion)
    {
        if (depletion.Material != CavePlanet.Ore || depletion.Deposits <= 0)
            return null;
        var change = InventoryOperations.AcquireOre(Items, depletion.Deposits,
            OreApproval == new InventoryOreApproval(depletion.Edit, Items.Revision, depletion.Deposits));
        return change.Status == InventoryChangeStatus.Applied ? this with { Items = change.State, OreApproval = null } : null;
    }

    internal static SaveGameValidation Read(VolumeCheckpoint checkpoint, out int ore, out ulong revision)
    {
        var status = InventoryCheckpoint.Read(checkpoint, out var inventory, out _, out _);
        ore = inventory?.OreCount ?? 0;
        revision = inventory?.Revision ?? 1;
        return status;
    }

    internal static SaveGameValidation ReadLegacy(VolumeCheckpoint checkpoint, out int ore, out ulong revision)
    {
        ore = 0;
        revision = 0;
        var bytes = checkpoint.Game;
        if (bytes.Length < 8 || !bytes[..8].SequenceEqual("WYUCAVE1"u8))
            return SaveGameValidation.Unsupported;
        if (bytes.Length != Bytes || checkpoint.Player is null)
            return SaveGameValidation.Malformed;
        ore = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        revision = BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]);
        return ore >= 0 && revision > 0 && revision - 1 <= (ulong)ore && (ore == 0 || revision > 1)
            ? SaveGameValidation.Valid : SaveGameValidation.Malformed;
    }
}

internal readonly record struct InventoryOreApproval(VolumeBrushIntent Edit, ulong InventoryRevision, int Deposits);
