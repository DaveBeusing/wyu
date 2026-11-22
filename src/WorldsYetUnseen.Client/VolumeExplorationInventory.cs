using System.Collections.Immutable;
using Groundwork.Core;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const double InventoryReach = 3;
    private readonly Guid _inventorySession = Guid.NewGuid();
    private bool _inventoryOpen;
    private int _inventoryPage;
    private int _inventoryRow;
    private int _inventoryNearby;
    private int _inventoryQuantity = int.MaxValue;
    private string? _inventoryFeedback;
    private InventoryRequest? _inventoryConfirmation;
    private InventoryReceipt? _inventoryReceipt;

    private CaveInventory? ReadInventory() => _ports.Committed.Read().Root switch
    {
        VolumeWorldRoot { State: CaveInventory inventory } => inventory,
        CaveInventory inventory => inventory,
        _ => null,
    };

    private async Task<bool> HandleInventoryAsync(VolumeStreamingCoordinator terrain, PlanetWalkState player,
        InputSnapshot input, CancellationToken token)
    {
        bool Pressed(InputControl control) => PrototypeMenu.Pressed(input, control);
        if (!_inventoryOpen && !Pressed(InputControl.I))
            return false;
        if (_inventoryOpen && (Pressed(InputControl.I) || Pressed(InputControl.Escape)))
        {
            if (Pressed(InputControl.Escape) && _inventoryConfirmation is not null)
            {
                _inventoryConfirmation = null;
                _inventoryFeedback = "Transfer canceled.";
                return true;
            }
            _inventoryOpen = false;
            _inventoryConfirmation = null;
            if (overlay is not null)
                overlay.Menu = null;
            return true;
        }

        _inventoryOpen = true;
        if (ReadInventory() is not { } root || overlay is null)
            return true;

        if (_inventoryConfirmation is { } pending)
        {
            if (!Pressed(InputControl.Enter))
                return true;
            _inventoryConfirmation = null;
            _inventoryFeedback = await SubmitInventoryAsync(terrain, pending, token).ConfigureAwait(false);
            overlay.Menu = _inventoryFeedback;
            return true;
        }

        var near = root.Items.Containers.Where(owner => InReach(player.Position, owner.Position)).ToArray();
        if (Pressed(InputControl.Tab) && near.Length > 0)
        {
            if (_inventoryPage == 2)
                _inventoryNearby = (_inventoryNearby + 1) % near.Length;
            SelectInventoryPage(2);
        }
        if (Pressed(InputControl.Left)) SelectInventoryPage((_inventoryPage + 2) % 3);
        if (Pressed(InputControl.Right)) SelectInventoryPage((_inventoryPage + 1) % 3);
        _inventoryNearby = Math.Clamp(_inventoryNearby, 0, Math.Max(0, near.Length - 1));
        var target = near.Length == 0 ? null : near[_inventoryNearby];
        var stacks = _inventoryPage switch
        {
            0 => root.Items.Carried,
            1 => root.Items.Equipped,
            _ => target?.Stacks ?? [],
        };
        if (Pressed(InputControl.Up) || Pressed(InputControl.Down))
        {
            _inventoryRow += Pressed(InputControl.Up) ? -1 : 1;
            _inventoryQuantity = int.MaxValue;
        }
        _inventoryRow = Math.Clamp(_inventoryRow, 0, Math.Max(0, stacks.Length - 1));
        var selected = stacks.Length == 0 ? default : stacks[_inventoryRow];
        var quantity = Math.Min(_inventoryQuantity, selected.Quantity);
        if (selected.Quantity != 0 && (Pressed(InputControl.Minus) || Pressed(InputControl.Equals)))
        {
            quantity = Math.Clamp(quantity + (Pressed(InputControl.Minus) ? -1 : 1), 1, selected.Quantity);
            _inventoryQuantity = quantity;
        }

        var source = _inventoryPage == 2 ? target?.Id ?? 0 : 0;
        var destination = _inventoryPage == 0 ? target?.Id ?? 0 : 0;
        var canTransfer = target is not null && (_inventoryPage == 2 || _inventoryPage == 0 && target.Kind == InventoryContainerKind.Chest);
        var canEquip = _inventoryPage == 0 && InventoryItems.TryGet(selected.Item, out var item) && item.EquipmentSlot is not null;
        var canPlace = _inventoryPage == 0 && selected.Item == InventoryItem.Chest;
        InventoryAction? action = null;
        if (canTransfer && Pressed(InputControl.A)) action = InventoryAction.MoveAll;
        else if (canTransfer && quantity > 0 && Pressed(InputControl.T)) action = InventoryAction.Transfer;
        else if (_inventoryPage == 0 && quantity > 0 && Pressed(InputControl.G)) action = InventoryAction.Drop;
        else if (canPlace && Pressed(InputControl.B)) action = InventoryAction.PlaceChest;
        else if (quantity > 0 && Pressed(InputControl.E) && (canEquip || _inventoryPage == 1))
            action = canEquip ? InventoryAction.Equip : InventoryAction.Unequip;
        else if (_inventoryPage == 2 && target?.Kind == InventoryContainerKind.Chest && Pressed(InputControl.X)) action = InventoryAction.Dismantle;
        else if (root.Items.Load.MovementHeld && Pressed(InputControl.R)) action = InventoryAction.Relieve;

        if (action is { } operation)
        {
            var request = new InventoryRequest(_inventorySession, checked((_inventoryReceipt?.Request.Number ?? 0) + 1),
                root.Items.Revision, _published.TerrainRevision, operation, source, destination, selected.Slot, (ushort)quantity, false);
            if (slot?.ReadOnly != false)
                _inventoryFeedback = "This world is read-only.";
            else
            {
                var preview = operation switch
                {
                    InventoryAction.Transfer => InventoryOperations.Transfer(root.Items, source, destination, request.Slot, request.Quantity),
                    InventoryAction.MoveAll => InventoryOperations.MoveAll(root.Items, source, destination),
                    _ => null,
                };
                if (preview is { Status: InventoryChangeStatus.OverloadConfirmation, ProjectedLoad: { } projected })
                {
                    _inventoryConfirmation = request with { AllowOverload = true };
                    overlay.Menu = $"Confirm overloaded transfer\n\n{InventoryLoadText(projected)}\nMovement will slow.\n\nEnter to transfer\nEsc to cancel";
                    return true;
                }
                _inventoryFeedback = await SubmitInventoryAsync(terrain, request, token).ConfigureAwait(false);
                // Repaint rows from the new immutable state on the next input tick.
                overlay.Menu = _inventoryFeedback;
                return true;
            }
        }

        var panel = _inventoryPage switch { 0 => "Carried", 1 => "Equipped", _ => "Nearby" };
        var nearby = target is null ? "No container or bundle in reach" :
            $"{(target.Kind == InventoryContainerKind.Chest ? "Chest" : "Bundle")} {_inventoryNearby + 1}/{near.Length}";
        var row = selected.Quantity == 0 ? "Empty" : $"{_inventoryRow + 1}/{stacks.Length}  {selected.Item} ×{selected.Quantity} ({quantity} selected)";
        var actions = string.Join(" · ", new[]
        {
            canEquip ? "E equip" : null,
            canPlace ? "B place chest" : null,
            _inventoryPage == 0 && quantity > 0 ? "G drop" : null,
            _inventoryPage == 1 && quantity > 0 ? "E unequip" : null,
            _inventoryPage == 2 && target?.Kind == InventoryContainerKind.Chest ? "X dismantle chest" : null,
        }.OfType<string>());
        overlay.Menu = $"Inventory · {panel}\n{InventoryLoadText(root.Items.Load)}\n{nearby}\n\n{row}\n\n" +
            "Left/Right view · Up/Down item · +/- amount\nTab nearby · I/Esc close\n" +
            (canTransfer ? "T transfer · A move all\n" : "") + actions +
            (root.Items.Load.MovementHeld ? "\nMovement held · R set down pack and equipment" : "") + $"\n\n{_inventoryFeedback}";
        return true;
    }

    private void SelectInventoryPage(int page)
    {
        _inventoryPage = page;
        _inventoryRow = 0;
        _inventoryQuantity = int.MaxValue;
    }

    private async Task<string> SubmitInventoryAsync(VolumeStreamingCoordinator terrain, InventoryRequest request, CancellationToken token)
    {
        if (request.Session != _inventorySession)
            return "Inventory request belongs to a previous session.";
        if (_inventoryReceipt is { } prior && request.Number <= prior.Request.Number)
            return request == prior.Request ? prior.Message : "Inventory request is out of date.";
        if (request.Number != (_inventoryReceipt?.Request.Number ?? 0) + 1)
            return "Inventory request is out of order.";

        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        var message = "Inventory or terrain changed. Review the current selection and try again.";
        if (slot?.ReadOnly == false && ReadInventory() is { } current && current.Items.Revision == request.Revision &&
            _published.TerrainRevision == request.TerrainRevision)
        {
            var plan = PlanInventory(terrain, current, request, out message);
            if (plan?.Status == InventoryChangeStatus.Applied)
            {
                message = CommitInventoryRoot(cut, current, current with { Items = plan.State, OreApproval = null }, token)
                    ? InventoryResultText(plan, request.Action) : token.IsCancellationRequested
                        ? "Inventory action canceled. No items moved." : "Inventory commit held. No items moved.";
                _inventoryReceipt = new(request, message);
                return message;
            }
            if (plan is not null)
                message = InventoryResultText(plan, request.Action);
        }
        Finish(cut);
        _inventoryReceipt = new(request, message);
        return message;
    }

    private InventoryChange? PlanInventory(VolumeStreamingCoordinator terrain, CaveInventory current, InventoryRequest request,
        out string failure)
    {
        failure = "Storage is blocked or out of reach. Move closer or clear the path.";
        var state = current.Items;
        var source = state.Containers.FirstOrDefault(owner => owner.Id == request.Source);
        var destination = state.Containers.FirstOrDefault(owner => owner.Id == request.Destination);
        var requiresSite = request.Action is InventoryAction.Drop or InventoryAction.PlaceChest or InventoryAction.Dismantle or InventoryAction.Relieve;
        PlanetVector? position = null;
        if (requiresSite || request.Source != 0 || request.Destination != 0)
        {
            using var lease = terrain.TryRead();
            if (lease is null || lease.Info != _published || terrain.Statistics.MovementHeld ||
                _ports.Committed.Read().Root is not VolumeWorldRoot { Player: { } player } worldRoot || worldRoot.Terrain != lease.Info)
            {
                failure = "Terrain coverage is pending. Try again when the ground is ready.";
                return null;
            }
            var construction = current.Construction.Blocks.IsEmpty ? null : Collision(current.Construction);
            if (request.Source != 0 && !CanAccessInventory(lease, player.Position, source, construction) ||
                request.Destination != 0 && !CanAccessInventory(lease, player.Position, destination, construction))
                return null;
            if (requiresSite)
            {
                var occupied = state.Containers.Where(owner => request.Action != InventoryAction.Dismantle || owner.Id != request.Source)
                    .Select(owner => owner.Position).ToArray();
                var origin = request.Action == InventoryAction.Dismantle ? source!.Position : player.Position;
                var status = InventoryDropSite.Find(lease, origin, player.Position, occupied, out var site, construction);
                if (status != InventoryDropSiteStatus.Found)
                {
                    failure = status switch
                    {
                        InventoryDropSiteStatus.CoverageUnavailable => "Safe drop held. Terrain coverage is pending.",
                        InventoryDropSiteStatus.QueryBudgetExceeded => "Safe drop held. Try again from another position.",
                        _ => "No supported free spot within 2 m. All items were kept.",
                    };
                    return null;
                }
                position = site;
            }
        }

        return request.Action switch
        {
            InventoryAction.Transfer => InventoryOperations.Transfer(state, request.Source, request.Destination,
                request.Slot, request.Quantity, request.AllowOverload),
            InventoryAction.MoveAll => InventoryOperations.MoveAll(state, request.Source, request.Destination, request.AllowOverload),
            InventoryAction.Equip => InventoryOperations.Equip(state, request.Slot),
            InventoryAction.Unequip => InventoryOperations.Unequip(state, request.Slot),
            InventoryAction.Drop => InventoryOperations.Drop(state, request.Slot, request.Quantity, position),
            InventoryAction.PlaceChest => InventoryOperations.PlaceChest(state, request.Slot, position),
            InventoryAction.Relieve => InventoryOperations.Relieve(state, position),
            InventoryAction.Dismantle => InventoryOperations.Dismantle(state, request.Source, position),
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    private static bool InReach(PlanetVector playerFoot, PlanetVector objectFoot) => Length(Subtract(playerFoot, objectFoot)) <= InventoryReach;

    private static bool CanAccessInventory(VolumeStreamingLease lease, PlanetVector playerFoot, InventoryContainer? owner,
        ConstructionCollision? construction)
    {
        if (owner is null || !InReach(playerFoot, owner.Position))
            return false;
        var eye = Add(playerFoot, Scale(Unit(playerFoot), 1.2));
        var target = Add(owner.Position, Scale(Unit(owner.Position), 0.3));
        var ray = Subtract(target, eye);
        if (construction?.Raycast(eye, Unit(ray), Length(ray)) is not null)
            return false;
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[1];
        return lease.Raycast(eye, target, hits).Status == VolumeQueryStatus.Clear;
    }

    private async Task<bool> PublishInventoryAsync(CaveInventory expected, InventoryState items, CancellationToken token)
    {
        await BeginAsync(token).ConfigureAwait(false);
        var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        return CommitInventoryRoot(cut, expected, expected with { Items = items, OreApproval = null }, token);
    }

    private bool CommitInventoryRoot(CandidateSet cut, CaveInventory expected, CaveInventory next, CancellationToken token)
    {
        PreparedBarrierPublication? prepared = null;
        if (!token.IsCancellationRequested && _ports.Committed.Read().Root is VolumeWorldRoot { State: CaveInventory current } root &&
            ReferenceEquals(current, expected))
        {
            var changed = root.WithState(next);
            var permit = _ports.RootChanges.TryReserve(cut, changed);
            if (permit is not null)
            {
                if (!token.IsCancellationRequested)
                    prepared = _ports.Preparation.TryPrepare(new BarrierPublicationDraft(cut, changed,
                        world.State.Read().PendingFaultGeneration, [], permit)).Publication;
                if (prepared is null)
                    permit.Revoke();
            }
        }
        var outcome = Finish(cut, prepared);
        return prepared is not null && outcome.Status == BarrierStatus.Committed;
    }

    private static string InventoryResultText(InventoryChange result, InventoryAction action)
    {
        static string Format(ImmutableArray<InventoryAmount> amounts) => amounts.Length == 0 ? "none" :
            string.Join(", ", amounts.Select(amount => $"{amount.Item} ×{amount.Quantity}"));
        return result.Status switch
        {
            InventoryChangeStatus.Applied when action is InventoryAction.Transfer or InventoryAction.MoveAll =>
                $"Moved {Format(result.Moved)}.\nRemaining at source: {Format(result.Remaining)}.",
            InventoryChangeStatus.Applied when action is InventoryAction.Dismantle => "Chest and contents set down in one bundle.",
            InventoryChangeStatus.Applied when action is InventoryAction.Relieve => "Carried load set down in one bundle.",
            InventoryChangeStatus.Applied => "Inventory updated.",
            InventoryChangeStatus.NothingFits => $"Nothing fits at the destination.\nRemaining at source: {Format(result.Remaining)}.",
            InventoryChangeStatus.OverloadConfirmation => "Load changed. Review the overload warning again.",
            InventoryChangeStatus.CapacityExceeded => "The weight or volume limit would be exceeded.",
            InventoryChangeStatus.SaveBudget => "The inventory save budget would be exceeded. All items were kept.",
            _ => "This inventory action is unavailable for the selected item or storage.",
        };
    }

    private static string InventoryLoadText(InventoryLoad load) => FormattableString.Invariant(
        $"{load.Weight / 1000d:0.##}/{load.WeightCapacity / 1000d:0.##} kg · {load.Volume / 1000d:0.##}/{load.VolumeCapacity / 1000d:0.##} L\nLoad {load.Relative:P0} · {(load.Weight * load.VolumeCapacity >= load.Volume * load.WeightCapacity ? "weight" : "volume")} limits");

    private enum InventoryAction { Transfer, MoveAll, Equip, Unequip, Drop, PlaceChest, Dismantle, Relieve }

    private readonly record struct InventoryRequest(Guid Session, ulong Number, ulong Revision, ulong TerrainRevision,
        InventoryAction Action, ulong Source, ulong Destination, ushort Slot, ushort Quantity, bool AllowOverload);

    private sealed record InventoryReceipt(InventoryRequest Request, string Message);
}
