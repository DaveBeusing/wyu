namespace WorldsYetUnseen.Gameplay;

public enum ConstructionStatus
{
    Applied,
    Invalid,
    Limit,
    Overlap,
    ToolRequired,
    InsufficientOre,
    OverloadConfirmation,
    CapacityExceeded,
    SaveBudget,
    NotFound
}

public readonly record struct ConstructionChange(ConstructionStatus Status, ConstructionState Construction,
    InventoryState Inventory, InventoryLoad? ProjectedLoad = null);

/// <summary>Stages both ownership sides. A caller can publish them only as one world change.</summary>
public static class ConstructionOperations
{
    public static ConstructionChange Place(ConstructionState construction, InventoryState inventory, ConstructionTarget target)
    {
        if (!CanEdit(construction) || !target.Anchor.IsValidFrame() || !target.TryGetGridBounds(out var bounds))
            return Reject(construction, inventory, ConstructionStatus.Invalid);
        if (!HasTool(inventory))
            return Reject(construction, inventory, ConstructionStatus.ToolRequired);

        var newAnchor = target.Anchor.Id == 0;
        var idsNeeded = newAnchor ? 2u : 1u;
        if (construction.Blocks.Length == ConstructionState.MaximumBlocks ||
            newAnchor && construction.Anchors.Length == ConstructionState.MaximumAnchors ||
            construction.NextId > uint.MaxValue - idsNeeded)
            return Reject(construction, inventory, ConstructionStatus.Limit);
        if (!newAnchor && (!construction.TryGetAnchor(target.Anchor.Id, out var existing) || existing != target.Anchor))
            return Reject(construction, inventory, ConstructionStatus.NotFound);
        if (construction.Intersects(target.Anchor, bounds))
            return Reject(construction, inventory, ConstructionStatus.Overlap);

        var cost = target.Type == ConstructionBlockType.Long ? 2 : 1;
        var spent = InventoryOperations.SpendOre(inventory, cost);
        if (spent.Status != InventoryChangeStatus.Applied)
            return Reject(construction, inventory, spent.Status == InventoryChangeStatus.NothingFits
                ? ConstructionStatus.InsufficientOre : InventoryStatus(spent.Status));

        var anchor = newAnchor ? target.Anchor with { Id = construction.NextId } : target.Anchor;
        var block = target.Block(construction.NextId + idsNeeded - 1, anchor.Id);
        var changed = construction with
        {
            Revision = construction.Revision + 1,
            NextId = construction.NextId + idsNeeded,
            Anchors = newAnchor ? construction.Anchors.Add(anchor) : construction.Anchors,
            Blocks = construction.Blocks.Add(block)
        };
        return new(ConstructionStatus.Applied, changed, spent.State);
    }

    public static ConstructionChange Remove(ConstructionState construction, InventoryState inventory, uint blockId,
        bool allowOverload = false)
    {
        if (!CanEdit(construction))
            return Reject(construction, inventory, ConstructionStatus.Invalid);
        if (!HasTool(inventory))
            return Reject(construction, inventory, ConstructionStatus.ToolRequired);
        if (!construction.TryGetBlock(blockId, out var block))
            return Reject(construction, inventory, ConstructionStatus.NotFound);
        if (!block.TryGetGridBounds(out _) || !construction.TryGetAnchor(block.AnchorId, out var anchor))
            return Reject(construction, inventory, ConstructionStatus.Invalid);

        var cost = block.Type == ConstructionBlockType.Long ? 2 : 1;
        var refund = InventoryOperations.AcquireOre(inventory, cost, allowOverload);
        if (refund.Status != InventoryChangeStatus.Applied)
            return new(InventoryStatus(refund.Status), construction, inventory, refund.ProjectedLoad);

        var blocks = construction.Blocks.Remove(block);
        var keepAnchor = blocks.Any(other => other.AnchorId == block.AnchorId);
        var changed = construction with { Revision = construction.Revision + 1, Blocks = blocks };
        if (!keepAnchor)
            changed = changed with { Anchors = construction.Anchors.Remove(anchor) };
        return new(ConstructionStatus.Applied, changed, refund.State, refund.ProjectedLoad);
    }

    private static bool HasTool(InventoryState inventory) =>
        inventory.Equipped.Any(static stack => stack is { Item: InventoryItem.Tool, Quantity: 1 });

    private static bool CanEdit(ConstructionState state) => state.HasBoundedRecords && state.Revision < ulong.MaxValue;

    private static ConstructionChange Reject(ConstructionState construction, InventoryState inventory, ConstructionStatus status) =>
        new(status, construction, inventory);

    private static ConstructionStatus InventoryStatus(InventoryChangeStatus status) => status switch
    {
        InventoryChangeStatus.OverloadConfirmation => ConstructionStatus.OverloadConfirmation,
        InventoryChangeStatus.CapacityExceeded => ConstructionStatus.CapacityExceeded,
        InventoryChangeStatus.SaveBudget => ConstructionStatus.SaveBudget,
        _ => ConstructionStatus.Invalid
    };
}
