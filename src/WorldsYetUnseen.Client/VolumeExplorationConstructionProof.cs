using Groundwork.Core;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    /// <summary>Runs the isolated build fixture through the ordinary terrain, movement, and renderer authorities.</summary>
    private async Task RunConstructionMotionProofAsync(VolumeStreamingCoordinator terrain, ProceduralRenderHost renderHost,
        PlanetWalkState state, CancellationToken token)
    {
        CheckConstructionProof(ConstructionRenderer is not null, "construction frame receipts must be available");
        var original = ReadInventory() ?? throw new InvalidOperationException("The motion proof needs a saved construction fixture.");
        var building = original.Construction;
        CheckConstructionProof(building.Anchors.Length == 1 && building.Blocks.Length == 2,
            "fixture must contain one grid, one floor and one wall");
        var anchor = building.Anchors[0];
        var floor = building.Blocks.Single(block => block.Type == ConstructionBlockType.Long);
        var wall = building.Blocks.Single(block => block.Type == ConstructionBlockType.Cube);
        var frame = anchor.Frame;
        var start = frame.ToLocal(state.Position);
        CheckConstructionProof(Within(start.X, 0.125) && Within(start.Y, 0.25) && Within(start.Z, 0.125) &&
            Dot(state.Heading, frame.XAxis) > 0.999, "player starts on the fixture floor facing the wall");
        CheckConstructionProof(floor is { X: 0, Y: 0, Z: 0, Orientation: 0 } &&
            wall is { X: 2, Y: 1, Z: 0, Orientation: 0 }, "unexpected floor or wall placement");

        var firstPresent = await PresentConstructionProofAsync(terrain, renderHost, state, token).ConfigureAwait(false);
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        CheckConstructionProof(!terrain.Statistics.MovementHeld, "startup consumed the visible terrain frame");
        var terrainOnly = terrain.TryPlanMovement(state, 0, 0, out var ordinaryStatus, CaveQueries);
        CheckConstructionProof(ordinaryStatus == VolumeQueryStatus.Clear && terrainOnly is { Result.Grounded: false } &&
            Dot(Subtract(terrainOnly.Result.State.Position, state.Position), frame.YAxis) < 0,
            "terrain-only movement would fall through the construction height");
        var supported = terrain.TryPlanMovementWithStaticObstacles(state, 0, 0, Collision(building).Query, out var supportStatus, CaveQueries);
        CheckConstructionProof(supportStatus == VolumeQueryStatus.Clear && supported is { Result.Grounded: true } &&
            supported.Result.State.Position == state.Position, "built floor holds the same player pose");
        var standing = supported ?? throw new InvalidOperationException("Missing supported construction step.");
        state = (await CommitConstructionMotionAsync(terrain, standing, token).ConfigureAwait(false)).State;
        WriteConstructionMotionProof("floor", FormattableString.Invariant(
            $"revision={building.Revision} present={firstPresent} grounded=true y={frame.ToLocal(state.Position).Y:G17}"));

        var blockedAt = 0;
        for (var step = 1; step <= 12; step++)
        {
            var previous = state;
            var walk = await StepConstructionMotionAsync(terrain, building, state, 1, token).ConfigureAwait(false);
            state = walk.State;
            CheckConstructionProof(walk.Grounded, "movement remains grounded while approaching the wall");
            if (state.Position == previous.Position)
            {
                blockedAt = step;
                break;
            }
        }
        var wallPosition = frame.ToLocal(state.Position);
        CheckConstructionProof(blockedAt > 1 && wallPosition.X > start.X &&
            wallPosition.X + PlanetSurfaceWalker.BodyRadius <= 0.50001, "wall stops forward movement before penetration");
        WriteConstructionMotionProof("wall", FormattableString.Invariant(
            $"revision={building.Revision} present={firstPresent} blockedStep={blockedAt} x={wallPosition.X:G17}"));

        var priorOrigin = terrain.View.Frame;
        var changedOrigin = priorOrigin.TryRebase(Add(state.Position, Scale(frame.XAxis, 24)), out var rebased);
        CheckConstructionProof(changedOrigin && rebased.Revision > priorOrigin.Revision, "fixture must force a new render origin");
        var loaded = await LoadAsync(terrain, renderHost.Active, state.Position, state.Heading, rebased, token).ConfigureAwait(false);
        CheckConstructionProof(loaded && terrain.View.Frame.Origin != priorOrigin.Origin &&
            terrain.View.Frame.Revision == rebased.Revision, "terrain uses the rebased frame");
        var secondPresent = await PresentConstructionProofAsync(terrain, renderHost, state, token).ConfigureAwait(false);
        var retained = ReadInventory();
        CheckConstructionProof(secondPresent > firstPresent && retained is not null &&
            retained.Construction.Revision == building.Revision && retained.Construction.NextId == building.NextId &&
            retained.Construction.Anchors.SequenceEqual(building.Anchors) && retained.Construction.Blocks.SequenceEqual(building.Blocks) &&
            retained.Items.Revision == original.Items.Revision && retained.Ore == original.Ore,
            "rebase retains exact build identity, occupancy, and inventory");
        var repeated = await StepConstructionMotionAsync(terrain, building, state, 1, token).ConfigureAwait(false);
        CheckConstructionProof(repeated.Grounded && repeated.State.Position == state.Position,
            "wall and floor collision are unchanged after the first rebased Present");
        state = repeated.State;
        WriteConstructionMotionProof("rebase", FormattableString.Invariant(
            $"originRevision={rebased.Revision} terrainRevision={_published.TerrainRevision} revision={building.Revision} present={secondPresent} x={wallPosition.X:G17}"));

        var removal = ConstructionOperations.Remove(original.Construction, original.Items, floor.Id);
        CheckConstructionProof(removal.Status == ConstructionStatus.Applied && removal.Construction.Blocks.SequenceEqual([wall]),
            "only the floor is removed and the unsupported wall remains");
        var removalBegin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(removalBegin);
        CheckConstructionProof(!terrain.Statistics.MovementHeld, "floor removal BeginTick admitted");
        var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        var before = ReadInventory() ?? throw new InvalidOperationException("Missing committed inventory at floor removal.");
        CheckConstructionProof(before.Construction.Revision == building.Revision && before.Items.Revision == original.Items.Revision,
            "the removal candidate still matches the committed inventory and building revisions");
        var candidate = before with { Items = removal.Inventory, Construction = removal.Construction, OreApproval = null };
        CheckConstructionProof(CommitInventoryRoot(cut, before, candidate, token), "floor and refund committed atomically");
        var committed = ReadInventory();
        CheckConstructionProof(committed is not null && ReferenceEquals(committed, candidate) && committed.Ore == original.Ore + 2 &&
            committed.Construction.Blocks.SequenceEqual([wall]) &&
            _ports.Committed.Read().Root is VolumeWorldRoot owned && owned.Player == state,
            "the same committed root owns the refund, unsupported wall, and prior player pose");
        CheckConstructionProof(UpdateConstructionPresentation(terrain, renderHost.Active, state, requireEmpty: true, dropSubmissionForProof: true),
            "movement holds while the removed floor's frame is pending");
        var lostFrame = _constructionFrame;
        WriteConstructionMotionProof("pending-floor-removal", FormattableString.Invariant(
            $"revision={removal.Construction.Revision} priorPresent={secondPresent} droppedSequence={lostFrame} movementHeld=true"));
        var thirdPresent = await PresentConstructionProofAsync(terrain, renderHost, state, token).ConfigureAwait(false);
        CheckConstructionProof(thirdPresent > lostFrame,
            $"replacement frame {thirdPresent} follows dropped sequence {lostFrame}");
        CheckConstructionProof(ReferenceEquals(ReadInventory(), candidate),
            "the replacement frame presents the original removal commit");
        var fall = await StepConstructionMotionAsync(terrain, removal.Construction, state, 0, token).ConfigureAwait(false);
        CheckConstructionProof(!fall.Grounded && Dot(Subtract(fall.State.Position, state.Position), frame.YAxis) < 0 &&
            Dot(fall.State.Velocity, frame.YAxis) < 0, "the player falls after the committed floor removal becomes visible");
        WriteConstructionMotionProof("removed-floor", FormattableString.Invariant(
            $"revision={removal.Construction.Revision} present={thirdPresent} refund={candidate.Ore - original.Ore} remaining={candidate.Construction.Blocks.Length} fall={Dot(Subtract(fall.State.Position, state.Position), frame.YAxis):G17}"));
    }

    private async Task<ulong> PresentConstructionProofAsync(VolumeStreamingCoordinator terrain, ProceduralRenderHost renderHost,
        PlanetWalkState state, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (UpdateConstructionPresentation(terrain, renderHost.Active, state, requireEmpty: true))
        {
            RequireHealthyRenderer(renderHost.Active.Read());
            await Task.Delay(8, deadline.Token).ConfigureAwait(false);
        }
        CheckConstructionProof(ReferenceEquals(_presentedConstruction, ReadInventory()?.Construction) &&
            _presentedConstructionTerrain == _published, "a matching complete construction frame was presented");
        return renderHost.Active.Read().LastPresentedSequence;
    }

    private async Task<PlanetWalkResult> StepConstructionMotionAsync(VolumeStreamingCoordinator terrain, ConstructionState buildings,
        PlanetWalkState player, double forward, CancellationToken token)
    {
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        CheckConstructionProof(!terrain.Statistics.MovementHeld, "movement BeginTick admitted");
        var draft = terrain.TryPlanMovementWithStaticObstacles(player, forward, 0, Collision(buildings).Query, out var status, CaveQueries)
            ?? throw new InvalidOperationException($"Construction movement held ({status}).");
        CheckConstructionProof(status == VolumeQueryStatus.Clear, "combined movement has confirmed coverage");
        return await CommitConstructionMotionAsync(terrain, draft, token).ConfigureAwait(false);
    }

    private async Task<PlanetWalkResult> CommitConstructionMotionAsync(VolumeStreamingCoordinator terrain, VolumeMovementDraft draft,
        CancellationToken token)
    {
        var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        var prepared = terrain.TryPrepareMovement(draft, cut, [])
            ?? throw new InvalidOperationException("Construction movement preparation was held.");
        CheckConstructionProof(Finish(cut, prepared).Status == BarrierStatus.Committed &&
            _ports.Committed.Read().Root is VolumeWorldRoot root && root.Player == draft.Result.State,
            "the committed root owns the collision-checked player pose");
        Drain(terrain);
        return draft.Result;
    }

    private void WriteConstructionMotionProof(string stage, string result)
    {
        diagnostics?.Write("construction-motion-proof", $"{stage} {result}");
        Console.WriteLine($"PASS construction-motion-proof {stage} {result}");
    }

    private static bool Within(double x, double expected) => Math.Abs(x - expected) <= 0.0001;

    private static void CheckConstructionProof(bool success, string detail)
    {
        if (!success)
            throw new InvalidOperationException($"Construction motion proof failed: {detail}.");
    }
}
