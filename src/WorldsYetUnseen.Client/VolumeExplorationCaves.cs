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
        var probe = CavePlanet.SurfaceProbe;
        await LoadAsync(terrain, renderer, probe, CavePlanet.Heading, PlanetLocalFrame.Create(probe), token)
            .ConfigureAwait(false);
        using var lease = terrain.TryRead() ?? throw new PlanetLoadException("Cave entrance terrain is unavailable.");
        var up = Unit(probe);
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        var floor = lease.Raycast(probe with { X = probe.X + 4 }, probe with { X = probe.X - 12 }, hits, CaveQueries);
        if (floor.Status != VolumeQueryStatus.Contact || Dot(hits[0].Normal, up) <= 0)
            throw new PlanetLoadException($"The cave entrance has no verified surface spawn ({floor.Status}, support {Dot(hits[0].Normal, up):F3}).");
        var foot = hits[0].Position with { X = hits[0].Position.X + 0.055 };
        if (lease.Sweep(CaveBody(foot), default, CaveQueries).Status != VolumeQueryStatus.Clear)
            throw new PlanetLoadException("The cave entrance spawn has insufficient clearance.");
        if (lease.Sweep(CaveBody(foot), Scale(up, -0.1), CaveQueries).Status != VolumeQueryStatus.Contact)
            throw new PlanetLoadException("The cave entrance spawn has no verified floor support.");
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
            _caveAim = Aim(terrain, state, VolumeBrushOperation.Remove, out var material) is null
                ? "Aim at terrain 3.5–10 m away"
                : material switch
                {
                    CavePlanet.Ore => "Target: Ember ore",
                    CavePlanet.MiddleRock => "Target: Slate",
                    CavePlanet.DeepRock => "Target: Deep basalt",
                    _ => "Target: Surface basalt",
                };
        }
        return below
            ? $"Ember Caves | Ore {CaveOre}\n{_caveAim}\nE mine | Q fill\nF5 save | F10 save and quit"
            : FormattableString.Invariant($"Ember Caves | Ore {CaveOre}\nCave mouth {Length(Subtract(CavePlanet.Entrance, state.Position)):F1} m\nFollow the ramp ahead | W to walk");
    }

    private static VolumeMovementDraft? PlanCaveMovement(VolumeStreamingCoordinator terrain, PlanetWalkState state,
        double forward, double right, out VolumeQueryStatus status)
    {
        var draft = terrain.TryPlanMovement(state, forward, right, out status, CaveQueries);
        for (var attempt = 0; attempt < 3 && draft is null && status == VolumeQueryStatus.BudgetExceeded &&
            (forward != 0 || right != 0); attempt++)
        {
            // Use shorter motion along the same input if a near-tangent sweep runs out of iterations.
            forward *= 0.5;
            right *= 0.5;
            draft = terrain.TryPlanMovement(state, forward, right, out status, CaveQueries);
        }
        return draft;
    }

    internal async Task RunCaveSmokeAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        string phase, CancellationToken token)
    {
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
            if (!await EditAsync(terrain, renderer, state, target, VolumeBrushOperation.Remove, 0, token).ConfigureAwait(false) ||
                CaveOre <= 0)
                throw new InvalidOperationException("The visible cave deposit did not award ore.");
            RequireSmokeSave(await SaveAsync(terrain, token).ConfigureAwait(false), 1);
        }

        using var lease = terrain.TryRead() ?? throw new InvalidOperationException("Missing cave collision after Continue.");
        var checkpoint = slot?.Checkpoint ?? throw new InvalidOperationException("Missing cave checkpoint.");
        if (checkpoint.Generation != 1 || checkpoint.Player != state || checkpoint.TerrainRevision != _published.TerrainRevision ||
            CaveOre <= 0 || !HasCaveRoof(terrain, state))
            throw new InvalidOperationException("The saved cave inventory, revision or underground pose did not restore.");
        var eye = Camera(terrain.View, state.Position, state.Heading).Position;
        if (Length(eye) >= CavePlanet.Radius || !lease.TryReadDensity(Descriptor.SampleAt(eye), out var caveAir) || caveAir <= 0)
            throw new InvalidOperationException("The checkpoint is not inside generated air below the surface.");
        var overlayBytes = new byte[lease.EditOverlayBytes];
        if (!lease.TryWriteEditOverlay(overlayBytes, out var written) || written != overlayBytes.Length ||
            !overlayBytes.AsSpan().SequenceEqual(checkpoint.Overlay) ||
            lease.Sweep(CaveBody(state.Position), default, CaveQueries).Status != VolumeQueryStatus.Clear)
            throw new InvalidOperationException("The saved cave edit or collision did not restore.");
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
        }
        var marker = phase == "init" ? "saved" : "continued";
        var hash = Convert.ToHexStringLower(SHA256.HashData(overlayBytes));
        Console.WriteLine(FormattableString.Invariant(
            $"WP12 cave {marker}: ore={CaveOre} generation={checkpoint.Generation} revision={checkpoint.TerrainRevision} overlay={hash} pose={state.Position.X:R},{state.Position.Y:R},{state.Position.Z:R}"));
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
            if (Aim(terrain, state, VolumeBrushOperation.Remove, out var material) is { } target &&
                material == CavePlanet.Ore && IncludesNaturalOre(target))
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
