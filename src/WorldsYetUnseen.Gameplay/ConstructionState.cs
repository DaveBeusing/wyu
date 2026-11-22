using System.Collections.Immutable;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay;

public readonly record struct ConstructionBlock(uint Id, uint AnchorId, short X, short Y, short Z, ConstructionBlockType Type,
    byte Orientation)
{
    public bool TryGetGridBounds(out ConstructionGridBounds bounds)
    {
        bounds = default;
        if (Orientation >= ConstructionOrientation.Count || Type is not (ConstructionBlockType.Cube or ConstructionBlockType.Long))
            return false;

        // Both block types pivot on the origin corner. A disk orientation is a signed permutation,
        // so the transformed opposite corner supplies all extrema on the construction grid.
        var opposite = ConstructionOrientation.Transform(Orientation, new(Type == ConstructionBlockType.Long ? 2 : 1, 1, 1));
        var x = checked(X + (int)opposite.X);
        var y = checked(Y + (int)opposite.Y);
        var z = checked(Z + (int)opposite.Z);
        bounds = new(Math.Min(X, x), Math.Min(Y, y), Math.Min(Z, z), Math.Max(X, x), Math.Max(Y, y), Math.Max(Z, z));
        return bounds.MinX >= short.MinValue && bounds.MinY >= short.MinValue && bounds.MinZ >= short.MinValue &&
               bounds.MaxX <= short.MaxValue + 1 && bounds.MaxY <= short.MaxValue + 1 && bounds.MaxZ <= short.MaxValue + 1;
    }

    public ConstructionBox Box(ConstructionAnchor anchor) => TryGetGridBounds(out var bounds)
        ? bounds.Box(anchor.Frame) : throw new ArgumentException("Invalid construction block bounds.");
}

/// <summary>Immutable ownership of the canonical, rebase-independent construction geometry.</summary>
public sealed record ConstructionState(ulong Revision, uint NextId, ImmutableArray<ConstructionAnchor> Anchors,
    ImmutableArray<ConstructionBlock> Blocks)
{
    public const int MaximumAnchors = 32;
    public const int MaximumBlocks = 1440;
    public const int MaximumOccupiedCells = 2 * MaximumBlocks;
    public static ConstructionState Empty { get; } = new(1, 1, [], []);

    internal bool HasBoundedRecords => Revision != 0 && NextId != 0 && !Anchors.IsDefault && !Blocks.IsDefault &&
        Anchors.Length <= MaximumAnchors && Blocks.Length <= MaximumBlocks;

    public bool TryGetAnchor(uint id, out ConstructionAnchor anchor)
    {
        var index = AnchorIndex(id);
        anchor = index >= 0 ? Anchors[index] : default;
        return index >= 0;
    }

    public bool TryGetBlock(uint id, out ConstructionBlock block)
    {
        var index = BlockIndex(id);
        block = index >= 0 ? Blocks[index] : default;
        return index >= 0;
    }

    public ImmutableArray<ConstructionBox> BuildBoxes()
    {
        if (!HasBoundedRecords)
            throw new InvalidOperationException("Invalid construction record bounds.");
        Span<ConstructionFrame> frames = stackalloc ConstructionFrame[Anchors.Length];
        for (var i = 0; i < Anchors.Length; i++)
            frames[i] = Anchors[i].Frame;

        var result = ImmutableArray.CreateBuilder<ConstructionBox>(Blocks.Length);
        foreach (var block in Blocks)
        {
            var anchor = AnchorIndex(block.AnchorId);
            if (anchor < 0 || !block.TryGetGridBounds(out var bounds))
                throw new InvalidOperationException("Invalid construction geometry.");
            result.Add(bounds.Box(frames[anchor]));
        }
        return result.MoveToImmutable();
    }

    public bool IsValid()
    {
        if (!HasBoundedRecords)
            return false;

        Span<ConstructionFrame> frames = stackalloc ConstructionFrame[Anchors.Length];
        uint previousId = 0;
        for (var i = 0; i < Anchors.Length; i++)
        {
            var anchor = Anchors[i];
            if (anchor.Id <= previousId || anchor.Id >= NextId || !anchor.IsValidFrame())
                return false;
            previousId = anchor.Id;
            frames[i] = anchor.Frame;
        }

        var geometry = new BlockGeometry[Blocks.Length];
        ulong usedAnchors = 0;
        previousId = 0;
        for (var i = 0; i < Blocks.Length; i++)
        {
            var block = Blocks[i];
            var anchor = AnchorIndex(block.AnchorId);
            if (block.Id <= previousId || block.Id >= NextId || AnchorIndex(block.Id) >= 0 ||
                anchor < 0 || !block.TryGetGridBounds(out var bounds))
                return false;
            var box = bounds.Box(frames[anchor]);
            for (var j = 0; j < i; j++)
            {
                var other = geometry[j];
                if (anchor == other.Anchor ? bounds.Intersects(other.Bounds) : box.Intersects(other.Box))
                    return false;
            }
            geometry[i] = new(anchor, bounds, box);
            usedAnchors |= 1UL << anchor;
            previousId = block.Id;
        }

        return usedAnchors == (1UL << Anchors.Length) - 1;
    }

    internal bool Intersects(ConstructionAnchor anchor, ConstructionGridBounds bounds)
    {
        var box = bounds.Box(anchor.Frame);
        Span<ConstructionFrame> frames = stackalloc ConstructionFrame[Anchors.Length];
        uint cached = 0;
        foreach (var block in Blocks)
        {
            if (!block.TryGetGridBounds(out var other))
                throw new InvalidOperationException("Invalid construction block bounds.");
            if (block.AnchorId == anchor.Id)
            {
                if (bounds.Intersects(other))
                    return true;
                continue;
            }

            var index = AnchorIndex(block.AnchorId);
            if (index < 0)
                throw new InvalidOperationException("Missing construction anchor.");
            var bit = 1u << index;
            if ((cached & bit) == 0)
            {
                frames[index] = Anchors[index].Frame;
                cached |= bit;
            }
            if (box.Intersects(other.Box(frames[index])))
                return true;
        }
        return false;
    }

    private int AnchorIndex(uint id)
    {
        var lower = 0;
        var upper = Anchors.Length - 1;
        while (lower <= upper)
        {
            var mid = lower + (upper - lower) / 2;
            var candidate = Anchors[mid].Id;
            if (candidate == id)
                return mid;
            if (candidate < id)
                lower = mid + 1;
            else
                upper = mid - 1;
        }
        return -1;
    }

    private int BlockIndex(uint id)
    {
        var lower = 0;
        var upper = Blocks.Length - 1;
        while (lower <= upper)
        {
            var mid = lower + (upper - lower) / 2;
            var candidate = Blocks[mid].Id;
            if (candidate == id)
                return mid;
            if (candidate < id)
                lower = mid + 1;
            else
                upper = mid - 1;
        }
        return -1;
    }

    private readonly record struct BlockGeometry(int Anchor, ConstructionGridBounds Bounds, ConstructionBox Box);
}

/// <summary>A target from the authoritative snapshot or a proposed, not-yet-owned anchor with ID zero.</summary>
public readonly record struct ConstructionTarget(ConstructionAnchor Anchor, short X, short Y, short Z,
    ConstructionBlockType Type, byte Orientation)
{
    public ConstructionBox Box => Block(0, Anchor.Id).Box(Anchor);

    public bool TryGetGridBounds(out ConstructionGridBounds bounds) => Block(0, Anchor.Id).TryGetGridBounds(out bounds);

    internal ConstructionBlock Block(uint id, uint anchorId) => new(id, anchorId, X, Y, Z, Type, Orientation);
}
