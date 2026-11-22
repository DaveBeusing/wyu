using System.Numerics;
using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private static readonly VolumeQueryLimits CaveQueries = new(16_000_000, 256);
    private const double QualificationDepth = 4.5;
    private int CaveOre => (_ports.Committed.Read().Root as VolumeWorldRoot)?.State is CaveInventory inventory
        ? inventory.Ore : 0;

    private VolumeStreamingSelection SelectTerrain(PlanetVector position, PlanetVector heading,
        VolumeBrushIntent? edit = null, int supportCells = 8)
    {
        if (cave is null)
            return VolumeStreamingSelection.Select(Descriptor, position, East, supportCells: supportCells);
        var up = Unit(position);
        var forward = Unit(Subtract(heading, Scale(up, Dot(heading, up))));
        VolumeEditDependencies? footprint = edit is { } brush &&
            VolumeStreamingCoordinator.TryGetEditDependencies(brush, out var dependencies) ? dependencies : null;
        return VolumeStreamingSelection.SelectCave(Descriptor, position, forward, CaveBody(position),
            Scale(forward, 18), footprint, supportCells: Math.Max(supportCells, 12));
    }

    private static VolumeCapsule CaveBody(PlanetVector foot)
    {
        var up = Unit(foot);
        const double radius = PlanetSurfaceWalker.BodyRadius;
        return new(Add(foot, Scale(up, radius)),
            Add(foot, Scale(up, PlanetSurfaceWalker.BodyHeight - radius)), radius);
    }

    private async Task<PlanetWalkState> OpenCaveTerrainAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        CancellationToken token)
    {
        foreach (var offset in new[] { 0, -2, 2, -4, 4, -6, 6 })
        {
            var probe = CavePlanet.SurfaceProbe with { Z = CavePlanet.SurfaceProbe.Z + offset };
            if (await TryOpenCaveSpawnAsync(terrain, renderer, probe, token).ConfigureAwait(false) is { } spawn)
                return spawn;
        }
        throw new PlanetLoadException("No verified cave spawn for this seed. Reopen the world selector and try another seed.");
    }

    private async Task<PlanetWalkState?> TryOpenCaveSpawnAsync(VolumeStreamingCoordinator terrain,
        D3D12Renderer renderer, PlanetVector probe, CancellationToken token)
    {
        await LoadAsync(terrain, renderer, probe, CavePlanet.Heading, PlanetLocalFrame.Create(probe), token)
            .ConfigureAwait(false);
        using var lease = terrain.TryRead() ?? throw new PlanetLoadException("Cave entrance terrain is unavailable.");
        var up = Unit(probe);
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        var floor = lease.Raycast(probe with { X = probe.X + 4 }, probe with { X = probe.X - 12 }, hits, CaveQueries);
        if (floor.Status != VolumeQueryStatus.Contact || Dot(hits[0].Normal, up) <= 0)
            return null;
        var foot = hits[0].Position with { X = hits[0].Position.X + 0.055 };
        if (lease.Sweep(CaveBody(foot), default, CaveQueries).Status != VolumeQueryStatus.Clear)
            return null;
        if (lease.Sweep(CaveBody(foot), Scale(up, -0.1), CaveQueries).Status != VolumeQueryStatus.Contact)
            return null;
        return new PlanetWalkState(foot, CavePlanet.Heading, default);
    }

    private long _nextCaveAim;
    private string _caveAim = "Aim at nearby terrain";

    private string CaveHint(VolumeStreamingCoordinator terrain, PlanetWalkState state)
    {
        var below = Length(state.Position) < CavePlanet.Radius - 1;
        var now = Environment.TickCount64;
        if (below && now >= _nextCaveAim)
        {
            _nextCaveAim = now + 200;
            var aim = AimTerrainEdit(terrain, state, VolumeBrushOperation.Remove);
            _caveAim = aim.Status != TerrainEditAimStatus.Target
                ? "Aim at terrain 3.5–10 m away"
                : aim.Material switch
                {
                    CavePlanet.Ore => "Target: Ember ore",
                    CavePlanet.MiddleRock => "Target: Slate",
                    CavePlanet.DeepRock => "Target: Deep basalt",
                    _ => "Target: Surface basalt",
                };
        }
        return below
            ? FormattableString.Invariant($"{PlanetName} | Ore {CaveOre}\n{_caveAim}\nE mine | Q fill\nF5 save | F10 save and quit\n{CaveExitHint(state)}")
            : FormattableString.Invariant($"{PlanetName} | Ore {CaveOre}\nCave mouth {Length(Subtract(CavePlanet.Entrance, state.Position)):F1} m\nFollow the ramp ahead | W to walk");
    }

    private static string CaveExitHint(PlanetWalkState state) =>
        AtCaveExitRope(state) ? "Space climb exit rope" :
        FormattableString.Invariant($"Exit rope {Length(Subtract(CaveExitAnchor, state.Position)):F1} m");

    private static VolumeMovementDraft? PlanCaveMovement(VolumeStreamingCoordinator terrain, PlanetWalkState state,
        double forward, double right, out VolumeQueryStatus status, ConstructionCollision? obstacles = null)
    {
        var draft = Plan(forward, right, out status);
        for (var attempt = 0; attempt < 3 && draft is null && status == VolumeQueryStatus.BudgetExceeded &&
            (forward != 0 || right != 0); attempt++)
        {
            // Use shorter motion along the same input if a near-tangent sweep runs out of iterations.
            forward *= 0.5;
            right *= 0.5;
            draft = Plan(forward, right, out status);
        }
        return draft;

        VolumeMovementDraft? Plan(double x, double z, out VolumeQueryStatus result) => obstacles is null
            ? terrain.TryPlanMovement(state, x, z, out result, CaveQueries)
            : terrain.TryPlanMovementWithStaticObstacles(state, x, z, obstacles.Query, out result, CaveQueries);
    }

    internal async Task RunCaveSmokeAsync(VolumeStreamingCoordinator terrain, ProceduralRenderHost renderHost,
        string phase, CancellationToken token)
    {
        var renderer = renderHost.Active;
        if (Connected)
        {
            phase = phase[3..];

        }
        var state = await OpenTerrainAsync(terrain, renderer, token).ConfigureAwait(false);
        if (phase == "init")
        {
            state = await CommitInitialSmokePoseAsync(terrain, state, token).ConfigureAwait(false);
            var start = state.Position;
            for (var step = 0; step < 480 && !HasCaveRoof(terrain, state); step++)
            {
                if (terrain.View.Frame.TryRebase(state.Position, out var frame))
                    await LoadAsync(terrain, renderer, state.Position, state.Heading, frame, token).ConfigureAwait(false);
                var begin = await BeginAsync(token).ConfigureAwait(false);
                terrain.BeginTick(begin);
                var draft = PlanCaveMovement(terrain, state, 1, 0, out var status);
                var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
                var prepared = draft is null ? null : terrain.TryPrepareMovement(draft, cut, []);
                if (Finish(cut, prepared).Status != BarrierStatus.Committed || draft is null)
                    throw new InvalidOperationException($"Cave descent held at step {step} ({status}, foot {state.Position}).");
                state = draft.Result.State;
                Drain(terrain);
                if (step % 40 != 0)
                    continue;
                var sequence = ++_sequence;
                RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence,
                    Camera(terrain.View, state.Position, state.Heading)));
                await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token).ConfigureAwait(false);
                Console.WriteLine(FormattableString.Invariant($"WP12 descent step={step} foot={state.Position.X:R},{state.Position.Y:R},{state.Position.Z:R}"));
            }
            if (!HasCaveRoof(terrain, state) || Length(Subtract(state.Position, start)) < 5)
                throw new InvalidOperationException($"Cave route did not descend underground (foot {state.Position}).");

            var target = FindVisibleOre(terrain, state);
            var aimSequence = ++_sequence;
            RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, aimSequence,
                Camera(terrain.View, state.Position, state.Heading)));
            await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= aimSequence, token).ConfigureAwait(false);
            if (await EditAsync(terrain, renderer, state, target, VolumeBrushOperation.Remove, 0, token).ConfigureAwait(false) !=
                TerrainPublicationOutcome.Presented ||
                CaveOre <= 0)
                throw new InvalidOperationException("The visible cave deposit did not award ore.");
            RequireSmokeSave(await SaveAsync(terrain, token).ConfigureAwait(false), 1);
        }

        using var lease = terrain.TryRead() ?? throw new InvalidOperationException("Missing cave collision after Continue.");
        var checkpoint = slot?.Checkpoint ?? throw new InvalidOperationException("Missing cave checkpoint.");
        if (checkpoint.Generation != 1 || checkpoint.Player != state || checkpoint.TerrainRevision != _published.TerrainRevision ||
            CaveOre <= 0 || !HasCaveRoof(terrain, state))
            throw new InvalidOperationException("The saved cave inventory, revision or underground pose did not restore.");
        if (Connected && IsConfirmedLaunchSurface(terrain, state.Position))
            throw new InvalidOperationException("An underground V3 checkpoint was accepted for takeoff.");
        var eye = Camera(terrain.View, state.Position, state.Heading).Position;
        if (Length(eye) >= CavePlanet.Radius || !lease.TryReadDensity(Descriptor.SampleAt(eye), out var caveAir) || caveAir <= 0)
            throw new InvalidOperationException("The checkpoint is not inside generated air below the surface.");
        var overlayBytes = new byte[lease.EditOverlayBytes];
        if (!lease.TryWriteEditOverlay(overlayBytes, out var written) || written != overlayBytes.Length ||
            !overlayBytes.AsSpan().SequenceEqual(checkpoint.Overlay) ||
            lease.Sweep(CaveBody(state.Position), default, CaveQueries).Status != VolumeQueryStatus.Clear)
            throw new InvalidOperationException("The saved cave edit or collision did not restore.");
        var resumedState = state;
        if (phase == "resume")
        {
            var begin = await BeginAsync(token).ConfigureAwait(false);
            if (!terrain.BeginTick(begin))
                throw new InvalidOperationException("The resumed cave view did not reopen movement.");
            var draft = PlanCaveMovement(terrain, state, 0, 0, out var status);
            var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
            var prepared = draft is null ? null : terrain.TryPrepareMovement(draft, cut, []);
            if (Finish(cut, prepared).Status != BarrierStatus.Committed || draft is null)
                throw new InvalidOperationException($"The resumed cave tick could not commit ({status}).");
            resumedState = draft.Result.State;
        }
        var marker = phase == "init" ? "saved" : "continued";
        var hash = Convert.ToHexStringLower(SHA256.HashData(overlayBytes));
        Console.WriteLine(FormattableString.Invariant(
            $"WP12 cave {marker}: ore={CaveOre} generation={checkpoint.Generation} revision={checkpoint.TerrainRevision} overlay={hash} pose={state.Position.X:R},{state.Position.Y:R},{state.Position.Z:R}"));
        if (phase == "resume")
        {
            lease.Dispose();
            state = resumedState;
            _caveExitWaypoint = 0;
            while (NextCaveClimb(state) is { } climb)
            {
                if (terrain.View.Frame.TryRebase(state.Position, out var frame))
                    await LoadAsync(terrain, renderer, state.Position, state.Heading, frame, token).ConfigureAwait(false);
                var begin = await BeginAsync(token).ConfigureAwait(false);
                terrain.BeginTick(begin);
                var draft = terrain.TryPlanCaveClimb(state, climb, out var status, CaveQueries);
                var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
                var prepared = draft is null ? null : terrain.TryPrepareMovement(draft, cut, []);
                if (Finish(cut, prepared).Status != BarrierStatus.Committed || draft?.Result.Status != PlanetWalkStatus.Moved)
                    throw new InvalidOperationException($"Cave exit blocked at waypoint {_caveExitWaypoint}: {state.Position} ({status}).");
                state = draft.Result.State;
                Drain(terrain);
            }
            var grounded = false;
            for (var tick = 0; tick < 90 && !grounded; tick++)
            {
                var begin = await BeginAsync(token).ConfigureAwait(false);
                terrain.BeginTick(begin);
                var draft = PlanCaveMovement(terrain, state, 0, 0, out var status);
                var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
                var prepared = draft is null ? null : terrain.TryPrepareMovement(draft, cut, []);
                if (Finish(cut, prepared).Status != BarrierStatus.Committed || draft is null)
                    throw new InvalidOperationException($"Cave exit could not settle on the surface ({status}).");
                state = draft.Result.State;
                grounded = draft.Result.Grounded;
                Drain(terrain);
            }
            if (!grounded || state.Position.Y < 60 || Length(state.Position) < CavePlanet.Radius - 0.3 ||
                CaveOre <= 0 || _published.TerrainRevision != checkpoint.TerrainRevision)
                throw new InvalidOperationException("Cave exit did not reach supported surface with the restored inventory.");
            var sequence = ++_sequence;
            RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence,
                Camera(terrain.View, state.Position, state.Heading)));
            await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token).ConfigureAwait(false);
            Console.WriteLine(FormattableString.Invariant($"WP12 cave reached supported surface: {state.Position.X:R},{state.Position.Y:R},{state.Position.Z:R}"));
            if (Connected)
            {
                var landing = await FlyAsync(terrain, renderHost, state, null, scripted: true, token).ConfigureAwait(false);
                if (landing.Quit || CaveOre <= 0 || _published.TerrainRevision != checkpoint.TerrainRevision)
                    throw new InvalidOperationException("V3 flight did not retain the edited cave and inventory.");
                Console.WriteLine("PASS: WP17 V3 orbit and return retained cave edits and ore.");

            }
        }
    }

    internal async Task VerifyConnectedProfilesAsync(VolumeStreamingCoordinator terrain,
        Groundwork.Rendering.RenderMeshPair globe, CancellationToken token)
    {
        var field = cave!.Field;
        var points = new (PlanetVector Direction, double Height)[3];
        FindFeatures(globe.First.Mesh.Positions);
        FindFeatures(globe.Second.Mesh.Positions[..^4]); // Omit the landing marker.
        string[] names = ["ridge", "main", "tributary"];
        for (var i = 0; i < names.Length; i++)
        {
            var (direction, height) = points[i];
            if (Math.Abs(height) < 0.4)
                throw new InvalidOperationException($"The generated orbit mesh does not resolve the V3 {names[i]}.");
            var position = Scale(direction, Descriptor.RadiusMeters);
            var axis = Math.Abs(direction.X) < 0.8 ? East : new PlanetVector(0, 1, 0);
            var heading = Unit(Subtract(axis, Scale(direction, Dot(axis, direction))));
            var selection = SelectTerrain(position, heading);
            var frame = PlanetLocalFrame.Create(position);
            if (frame.Revision <= terrain.View.Frame.Revision && !terrain.View.Frame.TryRebase(position, out frame))
                throw new InvalidOperationException("Could not rebase the profile fixture.");
            var view = terrain.ChangeView(frame, terrain.View.RendererEpoch);
            if (!terrain.ConfirmMask(view))
                throw new InvalidOperationException("Could not confirm the profile fixture mask.");
            var revision = i == 0 ? 0UL : 1UL;
            var admission = terrain.TryStage(VolumeWorkClass.Movement, selection, revision, 1, MeshBytes,
                Charge(selection), Field, out var work);
            if (admission != VolumeAdmission.Accepted || work is null)
                throw TerrainAdmissionFailure(admission);
            using var ownedWork = work;
            if (!await ExecuteTerrainAsync(work, token).ConfigureAwait(false))
                throw new InvalidOperationException("Could not generate the near profile mesh.");
            await BeginAsync(token).ConfigureAwait(false);
            var candidates = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
            var prepared = terrain.TryPreparePublication(work.Generation, candidates, [])
                ?? throw new InvalidOperationException("Could not prepare publication of the near profile mesh.");
            if (Finish(candidates, prepared).Status != BarrierStatus.Committed)
                throw new InvalidOperationException("Could not publish the near profile mesh.");
            Drain(terrain);
            using var lease = terrain.TryRead() ?? throw new InvalidOperationException("Missing published near terrain.");
            var hits = new VolumeRayHit[8];
            var ray = lease.Raycast(Add(position, Scale(direction, 6)), Add(position, Scale(direction, -6)), hits, CaveQueries);
            if (ray.Status != VolumeQueryStatus.Contact || ray.Count == 0)
                throw new InvalidOperationException($"Published near mesh did not resolve the V3 {names[i]} ({ray.Status}).");
            var near = Length(hits[0].Position) - Descriptor.RadiusMeters;
            if (Math.Sign(near) != Math.Sign(height) || Math.Abs(near - height) > 0.12)
                throw new InvalidOperationException($"V3 {names[i]} mesh height mismatch: {near:R} / {height:R}.");
            Console.WriteLine(FormattableString.Invariant($"WP17 mesh profile {names[i]}: near={near:F4} m orbit={height:F4} m."));
        }

        void FindFeatures(ReadOnlySpan<Vector3> vertices)
        {
            foreach (var vertex in vertices)
            {
                // Exclude the cave route around +X, where this baseline has no edits.
                if (vertex.X > 0.98 * Descriptor.RadiusMeters)
                    continue;
                var position = new PlanetVector(vertex.X, vertex.Y, vertex.Z);
                var relief = field.SampleConnectedRelief(Descriptor.SampleAt(position).Vertex);
                var height = Length(position) - Descriptor.RadiusMeters;
                var index = height > 0 ? 0 : relief.MainValley > relief.Tributary ? 1 : 2;
                if (index == 2 && relief.Tributary <= relief.MainValley)
                    continue;
                if (Math.Abs(height) > Math.Abs(points[index].Height))
                    points[index] = (Unit(position), height);
            }
        }
    }
    private static bool HasCaveRoof(VolumeStreamingCoordinator terrain, PlanetWalkState state)
    {
        if (Length(state.Position) > CavePlanet.Radius - QualificationDepth)
            return false;
        using var lease = terrain.TryRead();
        if (lease is null)
            return false;
        var up = Unit(state.Position);
        var eye = Add(state.Position, Scale(up, 1.65));
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        var roof = lease.Raycast(eye, Add(eye, Scale(up, 12)), hits, CaveQueries);
        return roof.Status == VolumeQueryStatus.Contact && roof.Count > 0 &&
            Dot(hits[0].Normal, up) < -0.2 && Length(hits[0].Position) <= CavePlanet.Radius + 0.01;
    }

    private LatticeCoordinate FindVisibleOre(VolumeStreamingCoordinator terrain, PlanetWalkState state)
    {
        for (var direction = 0; direction < 24; direction++)
        foreach (var pitch in new[] { 0f, -0.32f, 0.32f, -0.6f, 0.6f })
        {
            _yaw = direction * MathF.Tau / 24;
            _pitch = pitch;
            var aim = AimTerrainEdit(terrain, state, VolumeBrushOperation.Remove);
            if (aim is { Status: TerrainEditAimStatus.Target, Material: CavePlanet.Ore, Center: var target } && IncludesNaturalOre(target))
                return target;
        }
        throw new InvalidOperationException("The cave route reached no visible ore within mining range.");

        bool IncludesNaturalOre(LatticeCoordinate target)
        {
            for (var z = -1; z <= 0; z++)
            for (var y = -1; y <= 0; y++)
            for (var x = -1; x <= 0; x++)
                if (Field.SampleMaterial(new(target.X + x, target.Y + y, target.Z + z)) == CavePlanet.Ore)
                    return true;
            return false;
        }
    }
}
