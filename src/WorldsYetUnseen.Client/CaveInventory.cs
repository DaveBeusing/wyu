using System.Buffers.Binary;
using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

/// <summary>The immutable cave inventory is composed into the same root as terrain depletion.</summary>
internal sealed record CaveInventory(WorldId World, int Ore = 0, ulong OreInventoryRevision = 1) : IVolumeOreInventoryState
{
    private const int Bytes = 20;

    public ComponentTarget OreInventoryTarget => new(new(World, 1, 1), new(1));

    public IWorldStateRoot? StageOreDepletion(in VolumeOreDepletion depletion)
    {
        if (depletion.Material != CavePlanet.Ore || depletion.Deposits <= 0 ||
            depletion.Deposits > int.MaxValue - Ore || OreInventoryRevision == ulong.MaxValue)
            return null;
        return this with { Ore = Ore + depletion.Deposits, OreInventoryRevision = OreInventoryRevision + 1 };
    }

    internal int Write(Span<byte> bytes)
    {
        if (bytes.Length < Bytes)
            return -1;
        "WYUCAVE1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], Ore);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[12..], OreInventoryRevision);
        return Bytes;
    }

    internal static SaveGameValidation Read(VolumeCheckpoint checkpoint, out int ore, out ulong revision)
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
