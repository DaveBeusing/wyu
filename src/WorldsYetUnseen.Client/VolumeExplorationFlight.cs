using System.Buffers.Binary;
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

        var anchor = scriptedAnchor ?? Add(player.Position, Scale(player.Heading, 5));
        var center = FlightCoverageCenter(player.Position);
        await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, terrain.View.Frame, token,
            coverageCenter: center, supportCells: FlightSupportCells).ConfigureAwait(false);
        if (!TryFlightPad(terrain, anchor, out var pad, out var digest, out var blocked))
        {
            if (scripted)
                throw new InvalidOperationException($"Takeoff blocked: {blocked}.");
            Notice($"Takeoff blocked: {blocked}");
            return (player, false);
        }
        var revision = _published.TerrainRevision;
        var flight = new ProceduralFlight(planet.Descriptor.RadiusMeters, pad, player.Heading);
        if (scripted)
            FlightTrace("launch", flight, revision, digest);
        var globeTask = Task.Run(() => ProceduralGlobe.Build(planet, pad, token), token);
        var far = false;
        var usedFar = false;
        var previousPhase = flight.Phase;
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

                var steps = scripted ? 120 : 12;
                for (var step = 0; step < steps; step++)
                {
                    var next = flight.Propose();
                    if (far && Length(next.Position) <= FlightBoundary)
                    {
                        await renderHost.StopAsync(qualifyHandoff: scripted).ConfigureAwait(false);
                        var view = terrain.View;
                        terrain.ChangeView(view.Frame, checked(view.RendererEpoch + 1));
                        await renderHost.NearAsync(terrain, token).ConfigureAwait(false);
                        await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, view.Frame, token,
                            coverageCenter: center, flightCamera: flight.State.Position, supportCells: FlightSupportCells)
                            .ConfigureAwait(false);
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
                    flight.Accept(next);

                    if (!far && !usedFar && Length(next.Position) > FlightBoundary)
                    {
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
                var sequence = ++_sequence;
                var renderer = renderHost.Active;
                if (far)
                    RequireFrame(renderer.TrySubmit(ProceduralGlobe.Frame(sequence, flight.State.Position, player.Heading)));
                else
                    RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence,
                        FlightCamera(terrain.View, flight.State.Position, player.Heading)));
                if (scripted || flight.Phase == ProceduralFlightPhase.Landed)
                    await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token,
                        pauseWhileMinimized: true).ConfigureAwait(false);
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
            // The worker has no GPU or terrain lease and is joined even on window close.
            await ((Task)globeTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        if (!flight.CompletedOrbit || !usedFar || far)
            throw new InvalidOperationException("Flight did not complete the near/far/near orbital route.");
        var landing = new ProceduralLanding(flight.State.Position, revision, flight.Tick);
        var position = await PublishLandingAsync(terrain, player, landing, token).ConfigureAwait(false);
        diagnostics?.Write("touchdown", FormattableString.Invariant(
            $"tick={landing.FlightTick} revision={revision} overlay={digest}"));
        Notice("Touchdown confirmed. F5 saves this landing.");
        if (scripted)
            FlightTrace("touchdown", flight, revision, digest);
        return (position, false);
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

    private static VolumeCamera FlightCamera(VolumeViewIdentity view, PlanetVector position, PlanetVector heading)
    {
        var up = Vector3.Normalize(ProceduralGlobe.Float(position));
        var forward = Vector3.Normalize(-up + ProceduralGlobe.Float(heading) * 0.35f);
        return new(view, Add(position, Scale(Unit(position), 2.6)), forward, up, 0.95f, 0.08f, 210, Vector4.One, Sky);
    }

    private static void FlightTrace(string phase, ProceduralFlight flight, ulong revision, string digest)
    {
        var p = flight.State.Position;
        var v = flight.State.Velocity;
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 FLIGHT {phase} tick={flight.Tick} revision={revision} overlay={digest} p={p.X:R},{p.Y:R},{p.Z:R} v={v.X:R},{v.Y:R},{v.Z:R} drift={flight.OrbitDrift:R}"));
    }
}
