using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const double FlightOuterHeight = 72;
    private const int FlightSupportCells = 32;
    private const double GearRadius = 0.4;
    private const double GearHeight = 1.25;
    private static readonly VolumeQueryLimits FlightQueries = new(8_000_000, 128);
    private double FlightBoundary => Descriptor.RadiusMeters + FlightOuterHeight;

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

        if (Connected && !IsConfirmedLaunchSurface(terrain, player.Position))
        {
            if (scripted)
                throw new InvalidOperationException("V3 takeoff requires a confirmed surface position.");
            Notice("Takeoff requires a confirmed surface position.");
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
        var flight = new ProceduralFlight(Descriptor.RadiusMeters, pad, player.Heading);
        if (scripted)
            FlightTrace("launch", flight, revision, digest);
        var globeTask = Task.Run(() =>
        {
            var buildSample = BiomeMeasurements.StartBuild();
            var globe = cave is { } geology
                ? ProceduralGlobe.Build(Descriptor, geology.Field.SampleLandform, geology.Field.SampleMaterial, pad, null, token)
                : ProceduralGlobe.Build(planet, pad, renderHost.Biomes, token, renderHost.BuildsOrbitalMaterials);
            BiomeMeasurements.WriteBuild("globe-build", buildSample);
            return globe;
        }, token);
        var far = false;
        var usedFar = false;
        var texturedOrbit = false;
        var warpReadback = renderHost.WarpReadback;
        var orbitReadback = false;
        var skySmoke = scripted && Environment.GetEnvironmentVariable("WYU_PACKAGE_G_SMOKE") == "1" && !SkyMeasurements.Enabled;
        var recoveryAtHandoff = MeasureR09 && scripted &&
            Environment.GetEnvironmentVariable("WYU_R09_RECOVERY_AT_HANDOFF") == "1";
        var orbitSkyChecked = false;
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
                ulong? handoffTick = null;
                SkyPixels[]? beforeSky = null;
                for (var step = 0; step < steps; step++)
                {
                    var next = flight.Propose();
                    if (far && Length(next.Position) <= FlightBoundary)
                    {
                        SkyMeasurements.BeginHandoff("to-ground");
                        transitionStarted = BiomeMeasurements.Start();
                        transition = "to-ground";
                        var handoffStarted = MeasureR09 ? Stopwatch.GetTimestamp() : 0;
                        // Freeze flight at the normal return boundary before starting measured Recovery.
                        if (recoveryAtHandoff)
                            returnTerrain = await PrepareFlightReturnAsync(terrain, renderHost, player, center, revision, texturedOrbit,
                                returnCancellation.Token, handoffStarted).ConfigureAwait(false);
                        if (skySmoke)
                        {
                            handoffTick = checked(_environmentTick + (ulong)flight.Tick);
                            beforeSky = await CaptureFlightSkyAsync(renderHost.Active, terrain.View, flight.State.Position,
                                player.Heading, handoffTick.Value, far: true, token).ConfigureAwait(false);
                        }
                        if (warpReadback is not null)
                            await CaptureHandoffAsync(warpReadback, "far-before-return", renderHost.Active, terrain, player,
                                flight, digest, pad, (await globeTask.ConfigureAwait(false)).Meshes, token).ConfigureAwait(false);
                        var recoveryTiming = returnTerrain?.Timing;
                        var stopStarted = BiomeMeasurements.Start();
                        if (recoveryTiming is not null)
                            recoveryTiming.FarRendererStopStarted = Stopwatch.GetTimestamp();
                        await renderHost.StopAsync(qualifyHandoff: scripted).ConfigureAwait(false);
                        if (recoveryTiming is not null)
                            recoveryTiming.FarRendererStopped = Stopwatch.GetTimestamp();
                        BiomeMeasurements.Write("far-stop", stopStarted);
                        var prepared = returnTerrain ?? throw new InvalidOperationException("Return terrain was not staged.");
                        if (recoveryTiming is not null)
                            recoveryTiming.GroundRendererStartupStarted = Stopwatch.GetTimestamp();
                        var nearReady = renderHost.NearAsync(terrain, token,
                            recoveryTiming is null ? null : recoveryTiming.RecordGroundRendererStarted);
                        try
                        {
                            await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, prepared.View.Frame, token,
                                coverageCenter: center, flightCamera: flight.State.Position, supportCells: FlightSupportCells,
                                rendererReady: nearReady, returnTerrain: prepared,
                                flightTick: checked(_environmentTick + (ulong)flight.Tick), handoffStarted: handoffStarted,
                                warpReturn: warpReadback is null ? null : new(warpReadback, flight.Tick, digest, pad)).ConfigureAwait(false);
                        }
                        finally
                        {
                            await nearReady.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                        }
                        far = false;
                        if (beforeSky is not null)
                        {
                            var pixels = await CaptureFlightSkyAsync(renderHost.Active, terrain.View, flight.State.Position,
                                player.Heading, handoffTick!.Value, far: false, token).ConfigureAwait(false);
                            VerifyFlightSky(beforeSky, pixels, transition);
                            beforeSky = null;
                            handoffTick = null;
                        }
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
                        if (SkyMeasurements.Enabled)
                        {
                            var measuredTick = checked(_environmentTick + (ulong)flight.Tick);
                            await MeasureSkyPhaseAsync(terrain, renderHost.Active,
                                FlightCamera(terrain.View, next.Position, player.Heading, measuredTick),
                                "ascent", false, measuredTick, token).ConfigureAwait(false);
                        }
                        SkyMeasurements.BeginHandoff("to-orbit");
                        transitionStarted = BiomeMeasurements.Start();
                        transition = "to-orbit";
                        if (skySmoke)
                        {
                            handoffTick = checked(_environmentTick + (ulong)flight.Tick);
                            beforeSky = await CaptureFlightSkyAsync(renderHost.Active, terrain.View, next.Position,
                                player.Heading, handoffTick.Value, far: false, token).ConfigureAwait(false);
                        }
                        var globe = await globeTask.ConfigureAwait(false);
                        SkyHandoffDiagnostics.Mark("globe-ready");
                        if (warpReadback is { MarkerOnly: false })
                            await CaptureHandoffAsync(warpReadback, "near-before-ascent", renderHost.Active, terrain, player,
                                flight, digest, pad, null, token).ConfigureAwait(false);
                        await renderHost.StopAsync(qualifyHandoff: scripted).ConfigureAwait(false);
                        if (terrain.Statistics is not { MovementHeld: true, Readers: 0 })
                            throw new InvalidOperationException("The near renderer retained collision readers during the far handoff.");
                        await renderHost.FarAsync(globe, terrain.WorldIdentity, _published, token).ConfigureAwait(false);
                        texturedOrbit = globe.Textured;
                        far = true;
                        usedFar = true;
                        if (warpReadback is { MarkerOnly: true })
                        {
                            await CaptureHandoffAsync(warpReadback, "landing-marker", renderHost.Active, terrain, player,
                                flight, digest, pad, globe.Meshes, token).ConfigureAwait(false);
                            return (player, true);
                        }
                        if (warpReadback is not null)
                            await CaptureHandoffAsync(warpReadback, "far-after-ascent", renderHost.Active, terrain, player,
                                flight, digest, pad, globe.Meshes, token).ConfigureAwait(false);
                        if (beforeSky is not null)
                        {
                            var pixels = await CaptureFlightSkyAsync(renderHost.Active, terrain.View, next.Position,
                                player.Heading, handoffTick!.Value, far: true, token).ConfigureAwait(false);
                            VerifyFlightSky(beforeSky, pixels, transition);
                        }
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
                OrbitMaterialHost?.RetryMaterials(renderer, sequence);
                var tick = checked(_environmentTick + (ulong)flight.Tick);
                var fraction = SkyMeasurements.Enabled || acceptedAt == 0 || flight.Phase == ProceduralFlightPhase.Landed ? 1 :
                    Math.Clamp(Stopwatch.GetElapsedTime(acceptedAt).TotalSeconds * 60, 0, 1);
                var presentationTick = fraction == 1 ? tick : tick - 1;
                var subTick = fraction == 1 ? 0 : fraction;
                var camera = FlightCamera(terrain.View, flight.State.Position, player.Heading, presentationTick, subTick);
                var nearCamera = ApplySunlight(camera, presentationTick, subTick);
                var orbitCamera = texturedOrbit ? nearCamera : orbitDemo && nearCamera.Sunlight is { } sun
                    ? camera with { Tint = ReferenceSunlight.GlobeTint(camera.Position, sun) } : camera;
                var orbit = GlobeFrame(sequence, orbitCamera, flight.State.Position, player.Heading, presentationTick, subTick);
                if (scripted && _weather is not null)
                {
                    if (camera.Fog is not { } fog || (far && fog.MaxOpacity != 0))
                        throw new InvalidOperationException("Flight weather did not fade at the orbit boundary.");
                    if (transition is not null)
                    {
                        if (nearCamera.ClearColor != orbit.ClearColor || orbitCamera.Tint != orbit.Scene!.Value.Tint ||
                            nearCamera.Sky != orbit.Sky ||
                            (orbit.Sky is { } sky && nearCamera.Sunlight is { } light && sky.SunDirection != light.Direction))
                            throw new InvalidOperationException("Daylight changed at the renderer handoff.");
                    }
                    if (flight.Phase == ProceduralFlightPhase.Landed && fog.MaxOpacity <= 0)
                        throw new InvalidOperationException("Local fog did not return after descent.");
                }
                var sampleOrbit = warpReadback is not null && !orbitReadback && far && flight.Phase == ProceduralFlightPhase.Orbit;
                if (sampleOrbit)
                    warpReadback!.Arm("orbit-globe", sequence);
                var orbitSubmit = frameStarted != 0 && orbitDemo && OrbitPbrMeasurements.Enabled
                    ? Stopwatch.GetTimestamp() : 0;
                if (far)
                {
                    SkyHandoffDiagnostics.Mark("far-frame-submit", sequence);
                    RequireFrame(renderer.TrySubmit(SkyMeasurements.Submit(orbit)));
                }
                else
                {
                    Props?.Pump();
                    SkyHandoffDiagnostics.Mark("near-frame-submit", sequence);
                    RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence, SkyMeasurements.Submit(nearCamera)));
                }
                if (scripted || flight.Phase == ProceduralFlightPhase.Landed)
                    await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token,
                        pauseWhileMinimized: true).ConfigureAwait(false);
                var orbitReceipt = orbitSubmit != 0 ? Stopwatch.GetTimestamp() : 0;
                if (SkyMeasurements.Enabled && far && !orbitSkyChecked && flight.Phase == ProceduralFlightPhase.Orbit)
                {
                    orbitSkyChecked = true;
                    await MeasureSkyPhaseAsync(terrain, renderer, camera, "orbit", true, presentationTick, token, texturedOrbit).ConfigureAwait(false);
                }
                if (sampleOrbit)
                {
                    await warpReadback!.CompleteAsync(renderer, terrain, _published,
                        new(Descriptor.Seed, flight.Tick, presentationTick, subTick, digest, pad, null, orbit.Scene!.Value, false),
                        (await globeTask.ConfigureAwait(false)).Meshes, token).ConfigureAwait(false);
                    orbitReadback = true;
                }
                if (warpReadback is not null && flight.Phase == ProceduralFlightPhase.FinalApproach)
                {
                    await CaptureEditedApproachAsync(warpReadback, renderer, terrain, flight, camera, player.Heading,
                        digest, pad, token).ConfigureAwait(false);
                    return (player, true);
                }
                if (skySmoke && (transition is not null || !orbitSkyChecked && flight.Phase == ProceduralFlightPhase.Orbit))
                {
                    var checkpoint = transition ?? "orbit";
                    if (camera.Sky is not { } sky || !orbit.IsValid || sky != orbit.Sky ||
                        (checkpoint == "orbit" && sky.EyeMeters.Length() <= sky.AtmosphereRadiusMeters))
                        throw new InvalidOperationException($"Invalid sky at {checkpoint}.");
                    orbitSkyChecked |= checkpoint == "orbit";
                    Console.WriteLine(FormattableString.Invariant(
                        $"PACKAGE G {checkpoint} seed={Descriptor.Seed} flightTick={flight.Tick} present={sequence} radius={sky.EyeMeters.Length():F3}"));
                }
                BiomeMeasurements.Write("orbit", frameStarted);
                if (orbitReceipt != 0)
                    OrbitPbrMeasurements.WriteFrame("orbit", flight.Tick, sequence, 0, orbitSubmit, orbitReceipt, renderer);
                if (transition is not null)
                    BiomeMeasurements.Write(transition, transitionStarted);
                if (far && returnTerrain is null && !recoveryAtHandoff)
                    returnTerrain = await PrepareFlightReturnAsync(terrain, renderHost, player, center, revision, texturedOrbit, returnCancellation.Token).ConfigureAwait(false);
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
                pending.Pressure?.Dispose();
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
        if (scripted && Connected)
            await GameHost.VerifyConnectedProfilesAsync(window, slot!, (await globeTask.ConfigureAwait(false)).Meshes, token).ConfigureAwait(false);
        return (position, false);
    }

    private sealed record FlightReturnTerrain(VolumeViewIdentity View, ulong Revision, VolumeStreamingSelection Selection,
        VolumeStreamingWork Work, Task<bool> Generated, R09Timing? Timing, R09Pressure? Pressure);

    private sealed record FlightReturnReadback(Wp11WarpReadback Readback, int Tick, string Digest, PlanetVector Pad);

    private RenderFrame GlobeFrame(ulong sequence, VolumeCamera camera, PlanetVector craft, PlanetVector heading,
        ulong tick, double fraction) => camera.Sky is not null || orbitDemo ? ProceduralGlobe.Frame(sequence, camera) :
        ProceduralGlobe.LegacyFrame(sequence, craft, heading, Atmosphere(FlightEye(craft), tick, fraction));

    private async Task CaptureHandoffAsync(Wp11WarpReadback readback, string checkpoint, D3D12Renderer renderer,
        VolumeStreamingCoordinator terrain, PlanetWalkState player, ProceduralFlight flight, string digest, PlanetVector pad,
        RenderMeshPair? globe, CancellationToken token)
    {
        var sequence = ++_sequence;
        var tick = checked(_environmentTick + (ulong)flight.Tick);
        var camera = FlightCamera(terrain.View, flight.State.Position, player.Heading, tick);
        readback.Arm(checkpoint, sequence);
        if (globe is not null)
        {
            if (readback.MarkerOnly)
            {
                var forward = Vector3.Normalize(ProceduralGlobe.MarkerAimPoint(globe) - ProceduralGlobe.Float(camera.Position));
                var tangent = ProceduralGlobe.Float(player.Heading);
                var up = Vector3.Normalize(tangent - forward * Vector3.Dot(forward, tangent));
                camera = ApplySky(camera with { Forward = forward, Up = up }, tick, 0,
                    ReferenceDaylight.PreviewEnabled ? Atmosphere(camera.Position, tick) : null);
            }
            var frame = readback.MarkerOnly ? ProceduralGlobe.Frame(sequence, camera) :
                GlobeFrame(sequence, camera, flight.State.Position, player.Heading, tick, 0);
            RequireFrame(renderer.TrySubmit(frame));
            await readback.CompleteAsync(renderer, terrain, _published,
                new(Descriptor.Seed, flight.Tick, tick, 0, digest, pad, null, frame.Scene!.Value, false, readback.MarkerOnly), globe, token)
                .ConfigureAwait(false);
        }
        else
        {
            camera = ApplySunlight(camera, tick, 0);
            RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence, camera));
            await CompleteNearReadbackAsync(readback, renderer, terrain, camera, flight.Tick, tick, 0, digest, pad, false, token)
                .ConfigureAwait(false);
        }
    }

    private async Task CaptureEditedApproachAsync(Wp11WarpReadback readback, D3D12Renderer renderer,
        VolumeStreamingCoordinator terrain, ProceduralFlight flight, VolumeCamera flightCamera, PlanetVector heading,
        string digest, PlanetVector pad, CancellationToken token)
    {
        // Keep the actual approach eye. The regular flight tilt can put the pad below the viewport.
        var tick = checked(_environmentTick + (ulong)flight.Tick);
        var forward = Vector3.Normalize(ProceduralGlobe.Float(Subtract(pad, flightCamera.Position)));
        var tangent = ProceduralGlobe.Float(heading);
        var up = Vector3.Normalize(tangent - forward * Vector3.Dot(forward, tangent));
        var camera = ApplySunlight(ApplySky(FlightCamera(terrain.View, flight.State.Position, heading, tick) with
        {
            Forward = forward,
            Up = up,
        }, tick, 0, ReferenceDaylight.PreviewEnabled ? Atmosphere(flightCamera.Position, tick) : null), tick, 0);
        var sequence = ++_sequence;
        readback.Arm("edited-final-approach", sequence);
        RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence, camera));
        await CompleteNearReadbackAsync(readback, renderer, terrain, camera, flight.Tick, tick, 0, digest, pad, true, token)
            .ConfigureAwait(false);
    }

    private Task CompleteNearReadbackAsync(Wp11WarpReadback readback, D3D12Renderer renderer,
        VolumeStreamingCoordinator terrain, VolumeCamera camera, int flightTick, ulong tick, double fraction,
        string digest, PlanetVector pad, bool aimedAtEdit, CancellationToken token)
    {
        var origin = camera.View.Frame;
        var view = Matrix4x4.CreateLookTo(origin.ToLocal(camera.Position), camera.Forward, camera.Up);
        var scene = new RenderScene(Matrix4x4.Identity, camera.Tint, view, camera.FieldOfView, camera.NearPlane, camera.FarPlane)
        {
            Fog = camera.Fog,
            Sunlight = camera.Sunlight,
        };
        return readback.CompleteAsync(renderer, terrain, _published,
            new(Descriptor.Seed, flightTick, tick, fraction, digest, pad, origin, scene, aimedAtEdit), null, token);
    }

    private async Task<FlightReturnTerrain> PrepareFlightReturnAsync(VolumeStreamingCoordinator terrain, ProceduralRenderHost renderHost,
        PlanetWalkState player, PlanetVector center, ulong revision, bool texturedOrbit, CancellationToken token, long? handoffStarted = null)
    {
        var started = handoffStarted ?? Stopwatch.GetTimestamp();
        var timing = StartR09(VolumeWorkClass.Recovery, started, recoveryAtHandoff: handoffStarted.HasValue);
        var previous = terrain.View;
        var frame = previous.Frame.TryRebase(player.Position, out var rebased) ? rebased : previous.Frame;
        var texturePressure = timing is null ? null : renderHost.TexturePressure;
        var farRenderer = texturePressure is null ? null : renderHost.Active;
        var farView = texturePressure?.FarView;
        if (texturePressure is not null && (farView is not { } oldFar || oldFar.World != terrain.WorldIdentity ||
            oldFar.TerrainRevision != revision || oldFar.View.RendererEpoch != checked(previous.RendererEpoch + 1)))
            throw new InvalidOperationException("R09 Recovery did not match the intervening Far Horizon owner and terrain revision.");
        // Both the ordinary textured orbit and the diagnostic fixed scene have their own intervening ownership epoch.
        var rendererEpoch = checked(previous.RendererEpoch + (texturedOrbit || farView.HasValue ? 2UL : 1UL));
        var view = terrain.ChangeView(frame, rendererEpoch);
        if (view.RendererEpoch <= previous.RendererEpoch || !terrain.Statistics.MovementHeld)
            throw new InvalidOperationException("Could not reserve the held return view.");
        var selection = SelectTerrain(center, player.Heading, supportCells: FlightSupportCells);
        var (work, admission) = await AdmitTerrainAsync(terrain,
            new(VolumeWorkClass.Recovery, view, revision, revision, selection, null), started, token, timing).ConfigureAwait(false);
        if (admission != VolumeAdmission.Accepted || work is null)
            throw TerrainAdmissionFailure(admission);
        R09Pressure? pressure = null;
        try
        {
            pressure = timing is null ? null : await StartR09PressureAsync(token).ConfigureAwait(false);
            var generated = pressure is null ? ExecuteTerrainAsync(work, token, "return-terrain-build")
                : pressure.RunProtectedAsync(work, timing!, terrain, selection, revision, token,
                    recoveryPressure: texturePressure, farRenderer: farRenderer);
            // An old-owner queue wait cannot outlive this call: the next far frame or renderer Stop would use that queue.
            if (texturePressure is not null && (!await generated.ConfigureAwait(false) || timing?.RecoveryCopy is null))
                throw new InvalidOperationException("R09 Recovery did not settle its independent Far Horizon copy.");
            return new(view, revision, selection, work, generated, timing, pressure);
        }
        catch
        {
            pressure?.Dispose();
            work.Dispose();
            throw;
        }
    }

    private async Task<PlanetWalkState> PublishLandingAsync(VolumeStreamingCoordinator terrain, PlanetWalkState player,
        ProceduralLanding landing, CancellationToken token)
    {
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        var state = ReadInventory() ?? throw new InvalidOperationException("The inventory root is unavailable on landing.");
        var draft = state.Construction.Blocks.IsEmpty
            ? terrain.TryPlanMovement(player, 0, 0, out var status)
            : terrain.TryPlanMovementWithStaticObstacles(player, 0, 0, Collision(state.Construction).Query, out status);
        if (draft is null)
            throw new InvalidOperationException($"Ground return could not be admitted ({status}).");
        var candidates = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        if (!Connected)
            state = state with { Landing = landing };
        var prepared = terrain.TryPrepareMovement(draft, candidates, [], state)
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
            lease.EditLineage != Descriptor || !HasFineLandingCoverage(landed.Foot))
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

    private bool IsConfirmedLaunchSurface(VolumeStreamingCoordinator terrain, PlanetVector foot)
    {
        using var lease = terrain.TryRead();
        if (lease is null || lease.Info != _published || lease.Info.View != terrain.View)
            return false;
        var up = Unit(foot);
        var exterior = Math.Max(Descriptor.RadiusMeters + CavePlanet.MaximumConnectedRelief, Length(foot));
        var overlay = new byte[lease.EditOverlayBytes];
        if (!lease.TryWriteEditOverlay(overlay, out var written) || written != overlay.Length)
            return false;
        for (var offset = 8; offset < overlay.Length; offset += 32)
        {
            var row = overlay.AsSpan(offset, 24);
            var vertex = new PlanetVector(BinaryPrimitives.ReadInt64LittleEndian(row),
                BinaryPrimitives.ReadInt64LittleEndian(row[8..]), BinaryPrimitives.ReadInt64LittleEndian(row[16..]));
            exterior = Math.Max(exterior, Length(Scale(vertex, Descriptor.CellSizeMeters)));
        }
        // Start beyond the edited sample envelope. Require published air and complete query coverage.
        var outer = Scale(up, exterior + 4 * Descriptor.CellSizeMeters);
        if (!lease.TryReadDensity(Descriptor.SampleAt(outer), out var density) || density <= 0)
            return false;
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        var ray = lease.Raycast(outer, Add(foot, Scale(up, -0.25)), hits, CaveQueries);
        return ray.Status == VolumeQueryStatus.Contact && ray.Count > 0 &&
            Length(Subtract(hits[0].Position, foot)) <= 0.25 && Dot(hits[0].Normal, up) >= 0.87 &&
            lease.Sweep(CaveBody(foot), default, CaveQueries).Status == VolumeQueryStatus.Clear;
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
        if (lease is null || lease.Info != _published || lease.EditLineage != Descriptor)
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
            if (Length(vertex) > Descriptor.RadiusMeters + 64)
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
        var corridorStart = Add(pad, Scale(up, 0.04));
        blocked = "a constructed block intersects the landing footprint or flight corridor";
        if (!ConstructionGearClear(pad, outer))
            return false;
        var clear = SweepGear(lease, corridorStart, outer);
        blocked = $"takeoff corridor {clear.Status}";
        return clear.Status == VolumeQueryStatus.Clear;
    }

    private bool HasFineLandingCoverage(PlanetVector pad)
    {
        if (_selection is not { } selection)
            return false;
        // Four metres enclose the local sweep, gear and its radius on every axis.
        var extent = new PlanetVector(4, 4, 4);
        var lower = Descriptor.SampleAt(Subtract(pad, extent)).Vertex.Locate().Brick;
        var upper = Descriptor.SampleAt(Add(pad, extent)).Vertex.Locate().Brick;
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
        if (!ConstructionGearClear(previous.Position, next.Position))
            throw new InvalidOperationException("Descent held by a constructed block.");
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
        return lease.Sweep(GearBody(start, Unit(start)), Subtract(end, start), FlightQueries);
    }

    private bool ConstructionGearClear(PlanetVector start, PlanetVector end)
    {
        if (ReadInventory()?.Construction is not { Blocks.IsEmpty: false } state)
            return true;
        var up = Unit(start);
        return Collision(state).Sweep(GearBody(start, up), GearBody(end, up)).Status == VolumeQueryStatus.Clear;
    }

    private static VolumeCapsule GearBody(PlanetVector foot, PlanetVector up) =>
        new(Add(foot, Scale(up, GearRadius)), Add(foot, Scale(up, GearHeight - GearRadius)), GearRadius);

    private VolumeCamera FlightCamera(VolumeViewIdentity view, PlanetVector position, PlanetVector heading, ulong tick,
        double fraction = 0)
    {
        if (!ReferenceSky.Enabled)
        {
            var legacyUp = Vector3.Normalize(ProceduralGlobe.Float(position));
            var legacyForward = Vector3.Normalize(-legacyUp + ProceduralGlobe.Float(heading) * 0.35f);
            var legacyEye = FlightEye(position);
            return ApplyAtmosphere(new(view, legacyEye, legacyForward, legacyUp, 0.95f,
                0.08f, 210, Vector4.One, Sky), Atmosphere(legacyEye, tick, fraction));
        }
        var (forward, up) = ProceduralGlobe.ViewBasis(position, heading);
        var eye = FlightEye(position);
        var weather = Atmosphere(eye, tick, fraction);
        return ApplySky(ApplyAtmosphere(new(view, eye, forward, up, ProceduralGlobe.FieldOfView,
            0.08f, 210, Vector4.One, Sky), weather), tick, fraction, weather);
    }

    private async Task<SkyPixels[]> CaptureFlightSkyAsync(D3D12Renderer renderer, VolumeViewIdentity view,
        PlanetVector position, PlanetVector heading, ulong tick, bool far, CancellationToken token)
    {
        var camera = FlightCamera(view, position, heading, tick);
        var sky = camera.Sky ?? throw new InvalidOperationException("Missing sky for pixel fixture.");
        using var captureWindow = new SkyPixels.CaptureWindow();
        var radial = Vector3.Normalize(sky.EyeMeters);
        var tangent = Vector3.Normalize(Vector3.Cross(radial, Vector3.UnitZ));
        // Keep the accepted eye fixed. Controlled lighting separates stars and the solar disk from the horizon.
        var solar = Vector3.Normalize(radial + 0.18f * tangent);
        var views = new[]
        {
            camera with { Forward = tangent, Up = radial, Sky = sky with { Forward = tangent, Up = radial } },
            camera with { Forward = radial, Up = tangent, Sky = sky with
                { Forward = radial, Up = tangent, SunDirection = solar } },
            camera with { Forward = radial, Up = tangent, Sky = sky with
                { Forward = radial, Up = tangent, SunDirection = -radial, SunRadiance = 0 } },
        };
        var captures = new SkyPixels[views.Length];
        for (var index = 0; index < views.Length; index++)
        {
            var sequence = ++_sequence;
            var sample = views[index];
            RequireFrame(far ? renderer.TrySubmit(new RenderFrame(sequence, sample.ClearColor) { Sky = sample.Sky }) :
                renderer.TrySubmitVolumeCamera(_published.Generation, sequence, sample));
            await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token).ConfigureAwait(false);
            await Task.Delay(100, token).ConfigureAwait(false);
            var surface = renderer.Read();
            captureWindow.RequireUnobstructed(surface.AppliedWidth, surface.AppliedHeight, upperTwoThirds: index == 0);
            captures[index] = SkyPixels.Capture(surface.AppliedWidth, surface.AppliedHeight,
                clientSurface: true, upperTwoThirds: index == 0);
        }
        return captures;
    }

    private void VerifyFlightSky(SkyPixels[] before, SkyPixels[] after, string transition)
    {
        SkyPixels.Verify(before[0], after[0], Descriptor.Seed, transition, "horizon");
        SkyPixels.Verify(before[1], after[1], Descriptor.Seed, transition, "sun");
        SkyPixels.Verify(before[2], after[2], Descriptor.Seed, transition, "stars");
    }

    private static void FlightTrace(string phase, ProceduralFlight flight, ulong revision, string digest)
    {
        var p = flight.State.Position;
        var v = flight.State.Velocity;
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 FLIGHT {phase} tick={flight.Tick} revision={revision} overlay={digest} p={p.X:R},{p.Y:R},{p.Z:R} v={v.X:R},{v.Y:R},{v.Z:R} drift={flight.OrbitDrift:R}"));
    }
}
