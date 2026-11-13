using System.Buffers.Binary;
using System.Diagnostics;
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
    private const double FlightOuterHeight = 72;
    private const int FlightSupportCells = 32;
    private const double GearRadius = 0.4;
    private const double GearHeight = 1.25;
    private static readonly VolumeQueryLimits FlightQueries = new(8_000_000, 128);
    private double FlightBoundary => planet.Descriptor.RadiusMeters + FlightOuterHeight;

    private async Task<(PlanetWalkState Player, bool Quit)> FlyAsync(VolumeStreamingCoordinator terrain,
        ProceduralRenderHost renderHost, PlanetWalkState player, PlanetVector? scriptedAnchor, bool scripted,
        CancellationToken token, bool abortAtFar = false)
    {
        if (slot is null || slot.ReadOnly ||
            _ports.Committed.Read().Root is not VolumeWorldRoot { Player: not null })
        {
            Notice("Flight needs a writable world and a verified player pose.");
            return (player, false);
        }

        diagnostics?.Write("flight-requested", $"revision={_published.TerrainRevision}");
        var anchors = scriptedAnchor is { } requested ? [requested] : FlightAnchors(player);
        var center = FlightCoverageCenter(player.Position);
        await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, terrain.View.Frame, token,
            coverageCenter: center, supportCells: FlightSupportCells).ConfigureAwait(false);
        var anchor = default(PlanetVector);
        var pad = default(PlanetVector);
        var digest = "";
        var blocked = "";
        foreach (var candidate in anchors)
        {
            if (!TryFlightPad(terrain, candidate, out pad, out digest, out blocked))
                continue;
            anchor = candidate;
            break;
        }
        if (anchor == default)
        {
            if (scripted)
                throw new InvalidOperationException($"Takeoff blocked: {blocked}.");
            diagnostics?.Write("flight-held", blocked);
            Notice($"Takeoff blocked: {blocked}");
            return (player, false);
        }
        var revision = _published.TerrainRevision;
        var flight = new ProceduralFlight(planet.Descriptor.RadiusMeters, pad, player.Heading);
        if (scripted)
            FlightTrace("launch", flight, revision, digest);
        var globeTask = Task.Run(() =>
        {
            var buildSample = BiomeMeasurements.StartBuild();
            var globe = ProceduralGlobe.Build(planet, pad, renderHost.Biomes, token);
            BiomeMeasurements.WriteBuild("globe-build", buildSample);
            return globe;
        }, token);
        var far = false;
        var usedFar = false;
        var previousPhase = flight.Phase;
        using var returnCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        FlightReturnTerrain? returnTerrain = null;
        try
        {
            while (flight.Phase != ProceduralFlightPhase.Landed)
            {
                token.ThrowIfCancellationRequested();
                if (flight.Tick >= 30_000)
                    throw new InvalidOperationException("The procedural flight exceeded its fixed-step horizon.");
                var input = window.Capture();
                if (!scripted && PrototypeMenu.Pressed(input, InputControl.Escape))
                    return (player, true);
                if (ReadHealthyRenderer(renderHost.Active).Surface != Groundwork.Rendering.RenderSurfaceState.Drawable ||
                    !scripted && !window.Read().Focused)
                {
                    await Task.Delay(16, token).ConfigureAwait(false);
                    continue;
                }

                var steps = scripted && !BiomeMeasurements.Enabled ? 120 : 12;
                long transitionStarted = 0;
                long acceptedAt = 0;
                string? transition = null;
                for (var step = 0; step < steps; step++)
                {
                    var next = flight.Propose();
                    if (far && Length(next.Position) <= FlightBoundary)
                    {
                        transitionStarted = BiomeMeasurements.Start();
                        transition = "to-ground";
                        var stopStarted = BiomeMeasurements.Start();
                        await renderHost.StopAsync(qualifyHandoff: scripted).ConfigureAwait(false);
                        BiomeMeasurements.Write("far-stop", stopStarted);
                        var prepared = returnTerrain ?? throw new InvalidOperationException("Return terrain was not staged.");
                        var nearReady = renderHost.NearAsync(terrain, token);
                        try
                        {
                            await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, prepared.View.Frame, token,
                                coverageCenter: center, flightCamera: flight.State.Position, supportCells: FlightSupportCells,
                                rendererReady: nearReady, returnTerrain: prepared,
                                flightTick: checked(_environmentTick + (ulong)flight.Tick)).ConfigureAwait(false);
                        }
                        finally
                        {
                            await nearReady.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        }
                        far = false;
                        if (_published.TerrainRevision != revision || !TryFlightPad(terrain, anchor, out var returned,
                                out var returnedDigest, out blocked) || returnedDigest != digest || Length(Subtract(pad, returned)) > 0.01)
                            throw new InvalidOperationException($"Landing coverage or revision changed ({blocked}).");
                        if (scripted)
                        {
                            // A ray outside the admitted volume must fail closed before the descent proceeds.
                            using var lease = terrain.TryRead() ?? throw new InvalidOperationException("No return coverage.");
                            if (SweepGear(lease, Add(pad, new(1000, 0, 0)), pad).Status != VolumeQueryStatus.CoverageUnavailable)
                                throw new InvalidOperationException("An unstreamed landing approach was admitted.");
                            FlightTrace("near-return", flight, revision, digest);
                        }
                    }

                    if (flight.Phase == ProceduralFlightPhase.FinalApproach)
                        next = ResolveTouchdown(terrain, flight.State, next, pad, revision);
                    _ = checked(_environmentTick + (ulong)flight.Tick + 1);
                    flight.Accept(next);
                    acceptedAt = Stopwatch.GetTimestamp();

                    if (!far && !usedFar && Length(next.Position) > FlightBoundary)
                    {
                        transitionStarted = BiomeMeasurements.Start();
                        transition = "to-orbit";
                        var globe = await globeTask.ConfigureAwait(false);
                        await renderHost.StopAsync(qualifyHandoff: scripted).ConfigureAwait(false);
                        if (terrain.Statistics is not { MovementHeld: true, Readers: 0 })
                            throw new InvalidOperationException("The near renderer retained collision readers during the far handoff.");
                        await renderHost.FarAsync(globe, token).ConfigureAwait(false);
                        far = true;
                        usedFar = true;
                        if (scripted)
                            FlightTrace("far-ascent", flight, revision, digest);
                        break;
                    }
                    if (flight.Phase != previousPhase)
                    {
                        previousPhase = flight.Phase;
                        diagnostics?.Write("flight", FormattableString.Invariant(
                            $"stage={flight.Phase} tick={flight.Tick} revision={revision}"));
                        if (scripted)
                            FlightTrace(flight.Phase.ToString(), flight, revision, digest);
                        break;
                    }
                }
                var frameStarted = far && flight.Phase == ProceduralFlightPhase.Orbit ? BiomeMeasurements.Start() : 0;
                var sequence = ++_sequence;
                var renderer = renderHost.Active;
                var tick = checked(_environmentTick + (ulong)flight.Tick);
                var fraction = acceptedAt == 0 || flight.Phase == ProceduralFlightPhase.Landed ? 1 :
                    Math.Clamp(Stopwatch.GetElapsedTime(acceptedAt).TotalSeconds * 60, 0, 1);
                var presentationTick = fraction == 1 ? tick : tick - 1;
                var subTick = fraction == 1 ? 0 : fraction;
                var eye = FlightEye(flight.State.Position);
                var environment = Atmosphere(eye, presentationTick, subTick);
                if (scripted && _weather is not null)
                {
                    if (environment is not { } weather || (far && weather.FogOpacity != 0))
                        throw new InvalidOperationException("Flight weather did not fade at the orbit boundary.");
                    if (transition is not null)
                    {
                        var near = FlightCamera(terrain.View, flight.State.Position, player.Heading, presentationTick, subTick);
                        var orbit = ProceduralGlobe.Frame(sequence, flight.State.Position, player.Heading, environment);
                        if (near.ClearColor != orbit.ClearColor || near.Tint != orbit.Scene!.Value.Tint)
                            throw new InvalidOperationException("Daylight changed at the renderer handoff.");
                    }
                    if (flight.Phase == ProceduralFlightPhase.Landed && weather.FogOpacity <= 0)
                        throw new InvalidOperationException("Local fog did not return after descent.");
                }
                if (far)
                    RequireFrame(renderer.TrySubmit(ProceduralGlobe.Frame(sequence, flight.State.Position, player.Heading,
                        environment)));
                else
                    RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence,
                        FlightCamera(terrain.View, flight.State.Position, player.Heading,
                            presentationTick, subTick)));
                if (scripted || flight.Phase == ProceduralFlightPhase.Landed)
                    await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token,
                        pauseWhileMinimized: true).ConfigureAwait(false);
                BiomeMeasurements.Write("orbit", frameStarted);
                if (transition is not null)
                    BiomeMeasurements.Write(transition, transitionStarted);
                if (far && returnTerrain is null)
                    returnTerrain = PrepareFlightReturn(terrain, player, center, revision, returnCancellation.Token);
                if (abortAtFar && far)
                    return (player, true);
                if (!scripted)
                {
                    if (overlay is not null)
                        overlay.Menu = FormattableString.Invariant(
                            $"Flight: {flight.Phase}\nPad altitude {flight.Height:F1} m\nSpeed {Length(flight.State.Velocity):F1} m/s\nEsc aborts flight");
                    await Task.Delay(16, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (returnTerrain is { } pending)
            {
                await returnCancellation.CancelAsync().ConfigureAwait(false);
                await ((Task)pending.Generated).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                pending.Work.Dispose();
            }
            // Both CPU workers settle before the host retires terrain or the window.
            await ((Task)globeTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        if (!flight.CompletedOrbit || !usedFar || far)
            throw new InvalidOperationException("Flight did not complete the near/far/near orbital route.");
        var landing = new ProceduralLanding(flight.State.Position, revision, flight.Tick);
        var position = await PublishLandingAsync(terrain, player, landing, token).ConfigureAwait(false);
        _environmentTick = checked(_environmentTick + (ulong)flight.Tick);
        diagnostics?.Write("touchdown", FormattableString.Invariant(
            $"tick={landing.FlightTick} revision={revision} overlay={digest}"));
        Notice("Touchdown confirmed. F5 saves this landing.");
        if (scripted)
            FlightTrace("touchdown", flight, revision, digest);
        return (position, false);
    }

    private sealed record FlightReturnTerrain(VolumeViewIdentity View, ulong Revision, VolumeStreamingSelection Selection,
        VolumeStreamingWork Work, Task<bool> Generated);

    private FlightReturnTerrain PrepareFlightReturn(VolumeStreamingCoordinator terrain, PlanetWalkState player,
        PlanetVector center, ulong revision, CancellationToken token)
    {
        var previous = terrain.View;
        var frame = previous.Frame.TryRebase(player.Position, out var rebased) ? rebased : previous.Frame;
        var view = terrain.ChangeView(frame, checked(previous.RendererEpoch + 1));
        if (view.RendererEpoch <= previous.RendererEpoch || !terrain.Statistics.MovementHeld)
            throw new InvalidOperationException("Could not reserve the held return view.");
        var selection = SelectTerrain(center, player.Heading, supportCells: FlightSupportCells);
        var admission = terrain.TryStage(VolumeWorkClass.Movement, selection, revision, revision, MeshBytes,
            Charge(selection), Field, out var work);
        if (admission != VolumeAdmission.Accepted || work is null)
            throw TerrainAdmissionFailure(admission);
        return new(view, revision, selection, work, ExecuteTerrainAsync(work, token, "return-terrain-build"));
    }

    private async Task<PlanetWalkState> PublishLandingAsync(VolumeStreamingCoordinator terrain, PlanetWalkState player,
        ProceduralLanding landing, CancellationToken token)
    {
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        var draft = terrain.TryPlanMovement(player, 0, 0, out var status)
            ?? throw new InvalidOperationException($"Ground return could not be admitted ({status}).");
        var candidates = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        var prepared = terrain.TryPrepareMovement(draft, candidates, [], new GameHost.ExplorationRoot(begin.World, landing))
            ?? throw new InvalidOperationException("Landing state could not be prepared.");
        if (Finish(candidates, prepared).Status != BarrierStatus.Committed)
            throw new InvalidOperationException("Landing state did not commit.");
        Drain(terrain);
        return draft.Result.State;
    }

    private string ConfirmRestoredLanding(VolumeStreamingCoordinator terrain, ProceduralLanding landed)
    {
        using var lease = terrain.TryRead();
        if (lease is null || lease.Info != _published || lease.Info.TerrainRevision != landed.Revision ||
            lease.EditLineage != planet.Descriptor || !HasFineLandingCoverage(landed.Foot))
            throw new InvalidDataException("The saved touchdown lacks its committed terrain revision.");
        // The checkpoint records a grounded craft. Only the local contact needs rebuilding on Continue.
        var up = Unit(landed.Foot);
        var start = Add(landed.Foot, Scale(up, 2));
        var end = Add(landed.Foot, Scale(up, -1.5));
        var contact = SweepGear(lease, start, end);
        if (contact.Status != VolumeQueryStatus.Contact || Dot(contact.Normal, up) < 0.87 ||
            Length(Subtract(Add(start, Scale(Subtract(end, start), contact.Fraction)), landed.Foot)) > 0.08)
            throw new InvalidDataException("The saved touchdown could not be revalidated against rebuilt collision.");
        var bytes = new byte[lease.EditOverlayBytes];
        if (!lease.TryWriteEditOverlay(bytes, out var written) || written != bytes.Length ||
            slot?.Checkpoint is not { } checkpoint || !bytes.AsSpan().SequenceEqual(checkpoint.Overlay))
            throw new InvalidDataException("The saved touchdown overlay differs from the rebuilt terrain.");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static PlanetVector FlightCoverageCenter(PlanetVector position) => Add(position, Scale(Unit(position), 26));

    private static PlanetVector[] FlightAnchors(PlanetWalkState player)
    {
        var up = Unit(player.Position);
        var forward = Scale(player.Heading, 5);
        var right = Scale(new PlanetVector(player.Heading.Y * up.Z - player.Heading.Z * up.Y,
            player.Heading.Z * up.X - player.Heading.X * up.Z,
            player.Heading.X * up.Y - player.Heading.Y * up.X), 5);
        return [Add(player.Position, forward), Add(player.Position, right),
            Subtract(player.Position, right), Subtract(player.Position, forward)];
    }

    private bool TryFlightPad(VolumeStreamingCoordinator terrain, PlanetVector anchor, out PlanetVector pad,
        out string digest, out string blocked)
    {
        pad = default;
        digest = "";
        blocked = "terrain is not presented";
        using var lease = terrain.TryRead();
        if (lease is null || lease.Info != _published || lease.EditLineage != planet.Descriptor)
            return false;
        var overlayBytes = new byte[lease.EditOverlayBytes];
        if (!lease.TryWriteEditOverlay(overlayBytes, out var written) || written != overlayBytes.Length)
            return false;
        digest = Convert.ToHexStringLower(SHA256.HashData(overlayBytes));
        blocked = "terrain edits exceed the flight clearance envelope";
        for (var offset = 8; offset < overlayBytes.Length; offset += 32)
        {
            var row = overlayBytes.AsSpan(offset, 24);
            var vertex = new PlanetVector(BinaryPrimitives.ReadInt64LittleEndian(row),
                BinaryPrimitives.ReadInt64LittleEndian(row[8..]), BinaryPrimitives.ReadInt64LittleEndian(row[16..]));
            if (Length(vertex) > planet.Descriptor.RadiusMeters + 64)
                return false;
        }

        var up = Unit(anchor);
        var outer = Scale(up, FlightBoundary + 1);
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        var ray = lease.Raycast(outer, Scale(up, Length(anchor) - 7), hits, FlightQueries);
        blocked = $"pad ray {ray.Status}";
        if (ray.Status != VolumeQueryStatus.Contact || ray.Count == 0 || Dot(hits[0].Normal, up) < 0.87)
            return false;
        var start = Add(hits[0].Position, Scale(up, 2));
        var end = Add(hits[0].Position, Scale(up, -1.5));
        var support = SweepGear(lease, start, end);
        blocked = $"landing footprint {support.Status}";
        if (support.Status != VolumeQueryStatus.Contact || Dot(support.Normal, up) < 0.87)
            return false;
        pad = Add(start, Scale(Subtract(end, start), support.Fraction));
        blocked = "landing footprint lacks full collision detail";
        if (!HasFineLandingCoverage(pad))
            return false;
        var clear = SweepGear(lease, Add(pad, Scale(up, 0.04)), outer);
        blocked = $"takeoff corridor {clear.Status}";
        return clear.Status == VolumeQueryStatus.Clear;
    }

    private bool HasFineLandingCoverage(PlanetVector pad)
    {
        if (_selection is not { } selection)
            return false;
        // Four metres enclose the local sweep, gear and its radius on every axis.
        var extent = new PlanetVector(4, 4, 4);
        var lower = planet.Descriptor.SampleAt(Subtract(pad, extent)).Vertex.Locate().Brick;
        var upper = planet.Descriptor.SampleAt(Add(pad, extent)).Vertex.Locate().Brick;
        for (var z = lower.Z; z <= upper.Z; z++)
        for (var y = lower.Y; y <= upper.Y; y++)
        for (var x = lower.X; x <= upper.X; x++)
            if (StrideAt(selection, new(x, y, z)) != 1)
                return false;
        return true;
    }

    private PlanetFlightState ResolveTouchdown(VolumeStreamingCoordinator terrain, PlanetFlightState previous,
        PlanetFlightState next, PlanetVector pad, ulong revision)
    {
        using var lease = terrain.TryRead();
        if (lease is null || lease.Info != _published || lease.Info.TerrainRevision != revision)
            throw new InvalidOperationException("Descent held for missing landing coverage.");
        var result = SweepGear(lease, previous.Position, next.Position);
        if (result.Status == VolumeQueryStatus.Clear)
            return next;
        var up = Unit(pad);
        var radial = Dot(next.Velocity, up);
        var lateral = Length(Subtract(next.Velocity, Scale(up, radial)));
        if (result.Status != VolumeQueryStatus.Contact || radial is < -1 or > 0 || lateral > 0.5 || Dot(result.Normal, up) < 0.87)
            throw new InvalidOperationException($"Landing step held ({result.Status}, sink {radial:F2} m/s).");
        var contact = Add(previous.Position, Scale(Subtract(next.Position, previous.Position), result.Fraction));
        if (Length(Subtract(contact, pad)) > 0.08)
            throw new InvalidOperationException("Touchdown missed the verified landing footprint.");
        return next with { Position = contact, Velocity = default, Grounded = true };
    }

    private static VolumeSweepResult SweepGear(VolumeStreamingLease lease, PlanetVector start, PlanetVector end)
    {
        var up = Unit(start);
        return lease.Sweep(new VolumeCapsule(Add(start, Scale(up, GearRadius)),
            Add(start, Scale(up, GearHeight - GearRadius)), GearRadius), Subtract(end, start), FlightQueries);
    }

    private VolumeCamera FlightCamera(VolumeViewIdentity view, PlanetVector position, PlanetVector heading, ulong tick,
        double fraction = 0)
    {
        var up = Vector3.Normalize(ProceduralGlobe.Float(position));
        var forward = Vector3.Normalize(-up + ProceduralGlobe.Float(heading) * 0.35f);
        var eye = FlightEye(position);
        return ApplyAtmosphere(new(view, eye, forward, up, 0.95f, 0.08f, 210, Vector4.One, Sky),
            Atmosphere(eye, tick, fraction));
    }

    private static void FlightTrace(string phase, ProceduralFlight flight, ulong revision, string digest)
    {
        var p = flight.State.Position;
        var v = flight.State.Velocity;
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 FLIGHT {phase} tick={flight.Tick} revision={revision} overlay={digest} p={p.X:R},{p.Y:R},{p.Z:R} v={v.X:R},{v.Y:R},{v.Z:R} drift={flight.OrbitDrift:R}"));
    }
}
