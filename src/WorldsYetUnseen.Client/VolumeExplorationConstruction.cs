using System.Diagnostics;
using Groundwork.Core;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const double BuildReach = 4;
    private const double FreeBuildDistance = 3;
    private static readonly TimeSpan ConstructionFrameRetryDelay = TimeSpan.FromSeconds(1);
    private readonly Guid _buildSession = Guid.NewGuid();
    private ConstructionState? _collisionState;
    private ConstructionCollision? _collision;
    private ConstructionState? _presentedConstruction;
    private VolumePublicationInfo _presentedConstructionTerrain;
    private ulong _constructionFrame;
    private long _constructionFrameSent;
    private VolumePublicationInfo _constructionFrameTerrain;
    private ulong _lastBuildInput;
    private bool _constructionOpen;
    private bool _constructionSnap = true;
    private bool _forceNewAnchor;
    private uint _buildAnchorId;
    private byte _buildOrientation;
    private ConstructionBlockType _buildType = ConstructionBlockType.Cube;
    private BuildPreview? _buildPreview;
    private BuildCommand? _buildRefund;
    private string? _buildPanel;
    internal ConstructionVisuals? ConstructionRenderer { get; init; }

    private ConstructionCollision Collision(ConstructionState state)
    {
        if (!ReferenceEquals(_collisionState, state))
        {
            _collision = new ConstructionCollision(state);
            _collisionState = state;
        }
        return _collision!;
    }

    private bool UpdateConstructionPresentation(VolumeStreamingCoordinator terrain, D3D12Renderer renderer, PlanetWalkState player,
        bool requireEmpty = false, bool dropSubmissionForProof = false)
    {
        if (ReadInventory()?.Construction is not { } state ||
            _presentedConstruction is null && state.Blocks.IsEmpty && !requireEmpty ||
            ReferenceEquals(_presentedConstruction, state) && _presentedConstructionTerrain == _published)
            return false;

        if (ConstructionRenderer is not { } visuals)
        {
            if (overlay is not null)
                overlay.Menu = "Movement held\nThis world requires static construction support.";
            return true;
        }

        if (_constructionFrame != 0 && _constructionFrameTerrain == _published)
        {
            // The renderer can acknowledge the prop scene before it publishes the frame status.
            var lastPresented = renderer.Read().LastPresentedSequence;
            if (lastPresented >= _constructionFrame && visuals.IsPresented(_published, state.Revision, _constructionFrame))
            {
                _presentedConstruction = state;
                _presentedConstructionTerrain = _published;
                _constructionFrame = 0;
                return false;
            }
            if (lastPresented < _constructionFrame &&
                Stopwatch.GetElapsedTime(_constructionFrameSent) < ConstructionFrameRetryDelay)
                return true;
        }

        // A frame at the old revision or with deferred draws does not release the hold.
        Props?.Pump();
        Drain(terrain);
        visuals.Update(terrain.WorldIdentity, _published, state);
        var frame = ++_sequence;
        var result = dropSubmissionForProof ? RenderSubmissionResult.Accepted :
            renderer.TrySubmitVolumeCamera(_published.Generation, frame, Camera(terrain.View, player.Position, player.Heading));
        if (result is RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced)
        {
            _constructionFrame = frame;
            _constructionFrameTerrain = _published;
            _constructionFrameSent = Stopwatch.GetTimestamp();
        }
        else
            _constructionFrame = 0;
        if (overlay is not null)
            overlay.Menu = "Movement held\nPresenting the confirmed construction revision…";
        return true;
    }

    private async Task<bool> HandleConstructionAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState player, InputSnapshot input, CancellationToken token)
    {
        bool Pressed(InputControl control) => PrototypeMenu.Pressed(input, control);
        if (!_constructionOpen && !Pressed(InputControl.B))
            return false;

        if (_constructionOpen && (Pressed(InputControl.B) || Pressed(InputControl.Escape) && _buildRefund is null))
        {
            _constructionOpen = false;
            _buildPreview = null;
            _buildRefund = null;
            _buildPanel = null;
            if (overlay is not null)
            {
                overlay.Menu = null;
                overlay.Reticle = cave is not null;
            }
            return true;
        }
        if (slot?.ReadOnly != false)
        {
            Notice("This world is read-only.");
            return true;
        }
        if (ConstructionRenderer is not { } visuals)
        {
            Notice("Building is unavailable in this session.");
            return true;
        }

        _constructionOpen = true;
        if (overlay is not null)
            overlay.Reticle = true;
        if (_buildRefund is { } refund)
        {
            if (Pressed(InputControl.Escape))
            {
                _buildRefund = null;
                Notice("Dismantle canceled.");
            }
            else if (Pressed(InputControl.Enter))
            {
                _buildRefund = null;
                await CommitConstructionAsync(terrain, refund with { Input = input.LastSequence, AllowOverload = true }, token)
                    .ConfigureAwait(false);
            }
            return true;
        }

        var changed = Pressed(InputControl.Digit1) || Pressed(InputControl.Digit2) || Pressed(InputControl.R) ||
            Pressed(InputControl.Tab) || Pressed(InputControl.N);
        if (Pressed(InputControl.Digit1)) _buildType = ConstructionBlockType.Cube;
        if (Pressed(InputControl.Digit2)) _buildType = ConstructionBlockType.Long;
        if (Pressed(InputControl.Tab)) _constructionSnap = !_constructionSnap;
        if (Pressed(InputControl.N)) { _buildAnchorId = 0; _forceNewAnchor = true; }
        if (Pressed(InputControl.R))
            _buildOrientation = input.HeldControls.Contains(InputControl.LeftShift) || input.HeldControls.Contains(InputControl.RightShift)
                ? ConstructionOrientation.RotateAroundX(_buildOrientation) : ConstructionOrientation.RotateAroundY(_buildOrientation);

        // Apply only the candidate the previous frame actually displayed. Changed controls need a new preview.
        if (!changed && Pressed(InputControl.E) && _buildPreview is { Command: { } place } preview)
        {
            if (preview.Ready && preview.Box is { } shown && visuals.HasPresentedPreview(_published,
                place.Base.Construction.Revision, shown))
                await CommitConstructionAsync(terrain, place with { Input = input.LastSequence }, token).ConfigureAwait(false);
            else
                Notice(preview.Ready ? "Waiting for the shown preview. Confirm again after it appears." : preview.Reason);
            return true;
        }

        var camera = Camera(terrain.View, player.Position, player.Heading);
        var direction = new PlanetVector(camera.Forward.X, camera.Forward.Y, camera.Forward.Z);
        var current = ReadInventory();
        if (current is null)
            return false;
        using (var lease = terrain.TryRead())
        {
            if (lease is null || lease.Info != _published || terrain.Statistics.MovementHeld)
            {
                _buildPreview = new(null, null, false, "Waiting for the confirmed terrain view.");
            }
            else
            {
                var hit = AimConstruction(lease, Collision(current.Construction), camera.Position, direction, out var rayStatus);
                var targetHold = rayStatus switch
                {
                    VolumeQueryStatus.CoverageUnavailable => "Waiting for terrain coverage at the target.",
                    VolumeQueryStatus.BudgetExceeded => "Terrain target query held. Move closer and try again.",
                    _ => null,
                };
                if (Pressed(InputControl.X) && hit is not { BlockId: > 0 })
                    Notice(targetHold ?? "Aim at a visible block within 4 m to dismantle it.");
                if (Pressed(InputControl.X) && hit is { BlockId: > 0 } removal)
                {
                    var command = new BuildCommand(_buildSession, input.LastSequence, current, lease.Info, null, removal.BlockId, false);
                    var plan = ConstructionOperations.Remove(current.Construction, current.Items, removal.BlockId);
                    if (plan is { Status: ConstructionStatus.OverloadConfirmation, ProjectedLoad: { } projected })
                    {
                        _buildRefund = command;
                        _buildPanel = $"Dismantle would overload you\n{InventoryLoadText(projected)}\nEnter to confirm · Esc to cancel";
                        if (overlay is not null) overlay.Menu = _buildPanel;
                        return true;
                    }
                    _buildPreview = null;
                    // The async commit runs below after releasing the terrain lease.
                    _buildRefund = command with { AllowOverload = false };
                }
                else
                {
                    _buildPreview = _constructionSnap && hit is null && targetHold is not null
                        ? new(null, null, false, targetHold)
                        : PreviewConstruction(lease, current, player, camera.Position, direction, hit);
                }
            }
        }
        if (_buildRefund is { } immediate)
        {
            _buildRefund = null;
            await CommitConstructionAsync(terrain, immediate, token).ConfigureAwait(false);
            return true;
        }

        _buildPanel = $"Build · {_buildType} · Snap {(_constructionSnap ? "on" : "off")}\n" +
            $"{_buildPreview?.Reason}\n\n1/2 block · R/Shift+R turn · Tab snap · N new grid\nE place · X dismantle · B/Esc close" +
            (CurrentNotice is { } feedback ? $"\n\n{feedback}" : "");
        return false;
    }

    private BuildPreview PreviewConstruction(VolumeStreamingLease lease, CaveInventory current, PlanetWalkState player,
        PlanetVector origin, PlanetVector forward, BuildHit? hit)
    {
        if (_constructionSnap && hit is null)
            return new(null, null, false, "Aim at a surface within 4 m or press Tab for free placement.");
        var point = _constructionSnap && hit is { } surface ? surface.Point : Add(origin, Scale(forward, FreeBuildDistance));
        var draftAnchor = _forceNewAnchor || !current.Construction.TryGetAnchor(_buildAnchorId, out _);
        var anchorId = draftAnchor ? 0 : _buildAnchorId;
        if (!_forceNewAnchor && _constructionSnap && hit is { BlockId: > 0 } face &&
            current.Construction.TryGetBlock(face.BlockId, out var owner))
            anchorId = owner.AnchorId;

        ConstructionTarget target;
        if (anchorId != 0 && current.Construction.TryGetAnchor(anchorId, out var anchor))
        {
            var normal = _constructionSnap && hit is { } snapped ? snapped.Normal : default;
            if (!TryGridTarget(anchor, point, normal, out target))
                return new(null, null, false, "This point is outside the selected grid. Press N for a new grid.");
        }
        else
        {
            if (!ConstructionAnchor.TryCreate(point, out anchor))
                return new(null, null, false, "The construction anchor is outside the world grid.");
            target = new(anchor, 0, 0, 0, _buildType, _buildOrientation);
            if (_constructionSnap && hit is { } neighbour)
            {
                var box = target.Box;
                var half = box.HalfExtent;
                var reach = half.X * Math.Abs(Dot(box.XAxis, neighbour.Normal)) +
                    half.Y * Math.Abs(Dot(box.YAxis, neighbour.Normal)) + half.Z * Math.Abs(Dot(box.ZAxis, neighbour.Normal));
                var touchingCentre = Add(point, Scale(neighbour.Normal, reach + 0.002));
                if (!ConstructionAnchor.TryCreate(Add(anchor.Position, Subtract(touchingCentre, box.Center)), out anchor))
                    return new(null, null, false, "The new grid is outside the world.");
                target = target with { Anchor = anchor };
            }
        }

        var command = new BuildCommand(_buildSession, 0, current, lease.Info, target, 0, false);
        var plan = PlanConstruction(lease, current, player, command, out var hold);
        return new(command, target.Box, plan?.Status == ConstructionStatus.Applied, hold ?? BuildResult(plan!.Value.Status));
    }

    private bool TryGridTarget(ConstructionAnchor anchor, PlanetVector point, PlanetVector face, out ConstructionTarget target)
    {
        target = default;
        var frame = anchor.Frame;
        var local = frame.ToLocal(Add(point, Scale(face, 0.00001)));
        var box = new ConstructionTarget(anchor, 0, 0, 0, _buildType, _buildOrientation);
        if (!box.TryGetGridBounds(out var shape))
            return false;
        var nx = Dot(face, frame.XAxis); var ny = Dot(face, frame.YAxis); var nz = Dot(face, frame.ZAxis);
        var x = Math.Floor(local.X / ConstructionGridBounds.CellMeters) -
            (nx < -0.5 ? shape.MaxX - 1 : shape.MinX);
        var y = Math.Floor(local.Y / ConstructionGridBounds.CellMeters) -
            (ny < -0.5 ? shape.MaxY - 1 : shape.MinY);
        var z = Math.Floor(local.Z / ConstructionGridBounds.CellMeters) -
            (nz < -0.5 ? shape.MaxZ - 1 : shape.MinZ);
        if (x is < short.MinValue or > short.MaxValue || y is < short.MinValue or > short.MaxValue ||
            z is < short.MinValue or > short.MaxValue)
            return false;
        target = new(anchor, (short)x, (short)y, (short)z, _buildType, _buildOrientation);
        return target.TryGetGridBounds(out _);
    }

    private static BuildHit? AimConstruction(VolumeStreamingLease lease, ConstructionCollision blocks,
        PlanetVector origin, PlanetVector direction, out VolumeQueryStatus status)
    {
        var block = blocks.Raycast(origin, direction, BuildReach);
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[4];
        var ray = lease.Raycast(origin, Add(origin, Scale(direction, block?.Distance ?? BuildReach)), hits);
        status = ray.Status;
        if (ray.Status == VolumeQueryStatus.Contact)
            return new(0, hits[0].Position, hits[0].Normal);
        return ray.Status == VolumeQueryStatus.Clear && block is { } visible
            ? new(visible.BlockId, visible.Point, visible.Normal) : null;
    }

    private async Task CommitConstructionAsync(VolumeStreamingCoordinator terrain, BuildCommand command, CancellationToken token)
    {
        if (command.Session != _buildSession || command.Input <= _lastBuildInput)
            return;
        _lastBuildInput = command.Input;
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        string? reason = "Placement changed. Confirm the refreshed preview.";
        if (slot?.ReadOnly == false && ReferenceEquals(ReadInventory(), command.Base) &&
            _ports.Committed.Read().Root is VolumeWorldRoot { Player: { } player })
        {
            ConstructionChange? change;
            using (var lease = terrain.TryRead())
            {
                change = null;
                if (lease is not null && lease.Info == command.Terrain && lease.Info == _published)
                {
                    if (command.Target is not null || CurrentDismantleTarget(terrain, lease, command.Base.Construction, player) == command.RemoveId)
                        change = PlanConstruction(lease, command.Base, player, command, out reason);
                    else
                        reason = "The block is out of reach or no longer in view. Aim and try again.";
                }
            }
            if (change is { Status: ConstructionStatus.Applied } plan)
            {
                var collision = new ConstructionCollision(plan.Construction);
                var next = command.Base with { Items = plan.Inventory, Construction = plan.Construction, OreApproval = null };
                if (CommitInventoryRoot(cut, command.Base, next, token))
                {
                    _collision = collision;
                    _collisionState = plan.Construction;
                    if (command.Target is not null)
                    {
                        _buildAnchorId = plan.Construction.Blocks[^1].AnchorId;
                        _forceNewAnchor = false;
                    }
                    _buildPreview = null;
                    Notice(command.Target is null ? "Block dismantled. Ore returned." : "Block committed.");
                }
                else
                    Notice("Construction commit held. No materials or blocks changed.");
                return;
            }
            if (change is { } rejected)
                reason = BuildResult(rejected.Status);
        }
        Finish(cut);
        _buildPreview = null;
        Notice(reason ?? "Placement is waiting for confirmed terrain.");
    }

    private uint CurrentDismantleTarget(VolumeStreamingCoordinator terrain, VolumeStreamingLease lease,
        ConstructionState state, PlanetWalkState player)
    {
        var camera = Camera(terrain.View, player.Position, player.Heading);
        var direction = new PlanetVector(camera.Forward.X, camera.Forward.Y, camera.Forward.Z);
        return AimConstruction(lease, Collision(state), camera.Position, direction, out _)?.BlockId ?? 0;
    }

    private static ConstructionChange? PlanConstruction(VolumeStreamingLease lease, CaveInventory current, PlanetWalkState player,
        BuildCommand command, out string? hold)
    {
        hold = null;
        if (command.Target is not { } target)
            return ConstructionOperations.Remove(current.Construction, current.Items, command.RemoveId, command.AllowOverload);
        var staged = ConstructionOperations.Place(current.Construction, current.Items, target);
        if (staged.Status != ConstructionStatus.Applied)
            return staged;

        var box = target.Box;
        if (Length(Subtract(box.Center, WalkingEye(player.Position))) > BuildReach + Length(box.HalfExtent))
        {
            hold = "Move closer to the placement.";
            return null;
        }
        if (ConstructionCollision.Intersects(box, ConstructionCollision.Body(player)))
        {
            hold = "Clear the player from the block footprint.";
            return null;
        }
        foreach (var owner in current.Items.Containers)
        {
            if (!ConstructionCollision.Intersects(box, InventoryDropSite.ContainerBody(owner.Position)))
                continue;
            hold = "Move the chest or bundle out of the block footprint.";
            return null;
        }
        hold = ConstructionCollision.ProbeTerrain(lease, box) switch
        {
            VolumeQueryStatus.Clear => null,
            VolumeQueryStatus.CoverageUnavailable => "Waiting for terrain coverage across the block.",
            VolumeQueryStatus.BudgetExceeded => "Terrain clearance is held. Move nearer to the footprint.",
            _ => "The block intersects terrain.",
        };
        return hold is null ? staged : null;
    }

    private static string BuildResult(ConstructionStatus status) => status switch
    {
        ConstructionStatus.Applied => "Placement valid.",
        ConstructionStatus.Limit => "Construction limit reached. Remove a block or empty a grid.",
        ConstructionStatus.Overlap => "A placed block occupies the footprint.",
        ConstructionStatus.ToolRequired => "Equip the building tool in inventory.",
        ConstructionStatus.InsufficientOre => "Carry the ore required by the block.",
        ConstructionStatus.CapacityExceeded => "The full ore return would exceed carried capacity. The block was kept.",
        ConstructionStatus.SaveBudget => "The full change exceeds the save budget.",
        ConstructionStatus.OverloadConfirmation => "The ore return needs overload confirmation.",
        _ => "The selected block or placement is unavailable.",
    };

    private readonly record struct BuildHit(uint BlockId, PlanetVector Point, PlanetVector Normal);
    private readonly record struct BuildCommand(Guid Session, ulong Input, CaveInventory Base, VolumePublicationInfo Terrain,
        ConstructionTarget? Target, uint RemoveId, bool AllowOverload);
    private sealed record BuildPreview(BuildCommand? Command, ConstructionBox? Box, bool Ready, string Reason);
}
