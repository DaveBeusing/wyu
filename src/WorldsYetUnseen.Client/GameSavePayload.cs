using System.Buffers.Binary;
using Groundwork.Persistence;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal readonly record struct SavedItem(uint ItemId, uint Quantity);

internal static class GameSavePayload
{
    internal const uint Schema = 1;
    private const int HeaderLength = 16;
    private const int ItemLength = 8;
    private const int MaximumItems = 256;
    private const uint Stone = 1;

    internal static byte[] Encode(VoxelPose pose, ReadOnlySpan<SavedItem> inventory)
    {
        if (inventory.Length > MaximumItems)
            throw new InvalidDataException("The inventory exceeds the checkpoint limit.");

        var sorted = inventory.ToArray();
        Array.Sort(sorted, static (first, second) => first.ItemId.CompareTo(second.ItemId));
        var bytes = new byte[HeaderLength + sorted.Length * ItemLength];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, pose.X);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), pose.Y);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), pose.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)sorted.Length);
        var offset = HeaderLength;
        foreach (var item in sorted)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), item.ItemId);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), item.Quantity);
            offset += ItemLength;
        }

        return bytes;
    }

    internal static SaveGameValidation Decode(ReadOnlySpan<byte> bytes, out VoxelPose pose, out SavedItem[] inventory)
    {
        pose = default;
        inventory = [];
        if (bytes.Length < HeaderLength)
            return SaveGameValidation.Malformed;

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (count > MaximumItems || bytes.Length != HeaderLength + (int)count * ItemLength)
            return SaveGameValidation.Malformed;

        var items = new SavedItem[(int)count];
        var offset = HeaderLength;
        var previous = 0U;
        var supported = true;
        for (var i = 0; i < items.Length; i++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            var quantity = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            if (id <= previous || quantity == 0)
                return SaveGameValidation.Malformed;

            supported &= id == Stone;
            items[i] = new(id, quantity);
            previous = id;
            offset += ItemLength;
        }

        if (!supported)
            return SaveGameValidation.Unsupported;

        pose = new(BinaryPrimitives.ReadInt32LittleEndian(bytes), BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]));
        inventory = items;
        return SaveGameValidation.Valid;
    }
}
