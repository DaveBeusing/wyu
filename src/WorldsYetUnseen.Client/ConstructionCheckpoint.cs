using System.Collections.Immutable;
using Groundwork.Persistence;
using WorldsYetUnseen.Gameplay;
using static System.Buffers.Binary.BinaryPrimitives;

namespace WorldsYetUnseen.Client;

/// <summary>The versioned construction section within the shared volumetric checkpoint.</summary>
internal static class ConstructionCheckpoint
{
    internal const int HeaderBytes = 16;
    internal const int AnchorBytes = 32;
    internal const int BlockBytes = 16;
    internal const int Limit = 24 * 1024;
    private const ushort Version = 1;
    private const int VersionPrefixBytes = 6;

    internal static int Measure(ConstructionState state) =>
        state.Anchors.IsEmpty && state.Blocks.IsEmpty && state.NextId == 1 ? 0 :
            HeaderBytes + state.Anchors.Length * AnchorBytes + state.Blocks.Length * BlockBytes;

    internal static void Write(ConstructionState state, Span<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        "WYUC"u8.CopyTo(bytes);
        WriteUInt16LittleEndian(bytes[4..], Version);
        WriteUInt16LittleEndian(bytes[6..], (ushort)state.Anchors.Length);
        WriteUInt16LittleEndian(bytes[8..], (ushort)state.Blocks.Length);
        WriteUInt16LittleEndian(bytes[10..], 0);
        WriteUInt32LittleEndian(bytes[12..], state.NextId);
        var remaining = bytes[HeaderBytes..];
        foreach (var anchor in state.Anchors)
        {
            WriteUInt32LittleEndian(remaining, anchor.Id);
            WriteUInt32LittleEndian(remaining[4..], anchor.FrameVersion);
            WriteInt64LittleEndian(remaining[8..], anchor.XMillimeters);
            WriteInt64LittleEndian(remaining[16..], anchor.YMillimeters);
            WriteInt64LittleEndian(remaining[24..], anchor.ZMillimeters);
            remaining = remaining[AnchorBytes..];
        }
        foreach (var block in state.Blocks)
        {
            WriteUInt32LittleEndian(remaining, block.Id);
            WriteUInt32LittleEndian(remaining[4..], block.AnchorId);
            WriteInt16LittleEndian(remaining[8..], block.X);
            WriteInt16LittleEndian(remaining[10..], block.Y);
            WriteInt16LittleEndian(remaining[12..], block.Z);
            remaining[14] = (byte)block.Type;
            remaining[15] = block.Orientation;
            remaining = remaining[BlockBytes..];
        }
    }

    internal static SaveGameValidation Read(ReadOnlySpan<byte> bytes, out ConstructionState? state)
    {
        state = null;
        if (bytes.IsEmpty)
        {
            state = ConstructionState.Empty;
            return SaveGameValidation.Valid;
        }
        if (bytes.Length < VersionPrefixBytes || bytes.Length > Limit)
            return SaveGameValidation.Malformed;
        if (!bytes.StartsWith("WYUC"u8) || ReadUInt16LittleEndian(bytes[4..]) != Version)
            return SaveGameValidation.Unsupported;
        if (bytes.Length < HeaderBytes)
            return SaveGameValidation.Malformed;

        var anchorCount = ReadUInt16LittleEndian(bytes[6..]);
        var blockCount = ReadUInt16LittleEndian(bytes[8..]);
        var nextId = ReadUInt32LittleEndian(bytes[12..]);
        if (anchorCount > ConstructionState.MaximumAnchors || blockCount > ConstructionState.MaximumBlocks ||
            bytes.Length != HeaderBytes + anchorCount * AnchorBytes + blockCount * BlockBytes || nextId == 0 ||
            ReadUInt16LittleEndian(bytes[10..]) != 0 || blockCount == 0 && nextId == 1)
            return SaveGameValidation.Malformed;

        var anchors = bytes.Slice(HeaderBytes, anchorCount * AnchorBytes);
        var blocks = bytes[(HeaderBytes + anchors.Length)..];
        var validation = ValidateRecords(anchors, blocks, nextId);
        if (validation != SaveGameValidation.Valid)
            return validation;

        var anchorBuilder = ImmutableArray.CreateBuilder<ConstructionAnchor>(anchorCount);
        for (var offset = 0; offset < anchors.Length; offset += AnchorBytes)
            anchorBuilder.Add(ReadAnchor(anchors[offset..]));
        var blockBuilder = ImmutableArray.CreateBuilder<ConstructionBlock>(blockCount);
        for (var offset = 0; offset < blocks.Length; offset += BlockBytes)
            blockBuilder.Add(ReadBlock(blocks[offset..]));
        var candidate = new ConstructionState(1, nextId, anchorBuilder.MoveToImmutable(), blockBuilder.MoveToImmutable());
        if (!candidate.IsValid())
            return SaveGameValidation.Malformed;
        state = candidate;
        return SaveGameValidation.Valid;
    }

    private static SaveGameValidation ValidateRecords(ReadOnlySpan<byte> anchors, ReadOnlySpan<byte> blocks, uint nextId)
    {
        uint previous = 0;
        for (var offset = 0; offset < anchors.Length; offset += AnchorBytes)
        {
            var anchor = ReadAnchor(anchors[offset..]);
            if (anchor.Id <= previous || anchor.Id >= nextId)
                return SaveGameValidation.Malformed;
            if (anchor.FrameVersion != 1)
                return SaveGameValidation.Unsupported;
            if (!anchor.IsValidFrame())
                return SaveGameValidation.Malformed;
            previous = anchor.Id;
        }

        ulong usedAnchors = 0;
        previous = 0;
        for (var offset = 0; offset < blocks.Length; offset += BlockBytes)
        {
            var block = ReadBlock(blocks[offset..]);
            var anchorIndex = AnchorIndex(anchors, block.AnchorId);
            if (block.Id <= previous || block.Id >= nextId ||
                anchorIndex < 0 || AnchorIndex(anchors, block.Id) >= 0)
                return SaveGameValidation.Malformed;
            if (block.Type is not (ConstructionBlockType.Cube or ConstructionBlockType.Long))
                return SaveGameValidation.Unsupported;
            if (!block.TryGetGridBounds(out _))
                return SaveGameValidation.Malformed;
            usedAnchors |= 1UL << anchorIndex;
            previous = block.Id;
        }
        return usedAnchors == (1UL << (anchors.Length / AnchorBytes)) - 1
            ? SaveGameValidation.Valid : SaveGameValidation.Malformed;
    }

    private static int AnchorIndex(ReadOnlySpan<byte> anchors, uint id)
    {
        // Anchor rows already passed the sorted-ID check. Binary search uses the encoded keys directly.
        var low = 0;
        var high = anchors.Length / AnchorBytes;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var candidate = ReadUInt32LittleEndian(anchors[(middle * AnchorBytes)..]);
            if (candidate == id)
                return middle;
            if (candidate < id)
                low = middle + 1;
            else
                high = middle;
        }
        return -1;
    }

    private static ConstructionAnchor ReadAnchor(ReadOnlySpan<byte> row) =>
        new(ReadUInt32LittleEndian(row), ReadInt64LittleEndian(row[8..]), ReadInt64LittleEndian(row[16..]),
            ReadInt64LittleEndian(row[24..]), ReadUInt32LittleEndian(row[4..]));

    private static ConstructionBlock ReadBlock(ReadOnlySpan<byte> row) =>
        new(ReadUInt32LittleEndian(row), ReadUInt32LittleEndian(row[4..]), ReadInt16LittleEndian(row[8..]),
            ReadInt16LittleEndian(row[10..]), ReadInt16LittleEndian(row[12..]), (ConstructionBlockType)row[14], row[15]);
}
