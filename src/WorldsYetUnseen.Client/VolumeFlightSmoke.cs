using System.Numerics;
using Groundwork.Core;
using Groundwork.Rendering.D3D12;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    internal async Task RunFlightSmokeAsync(VolumeStreamingCoordinator terrain, ProceduralRenderHost renderHost,
        string phase, CancellationToken token)
    {
        try
        {
            await RunFlightRouteAsync(terrain, renderHost, phase, token).ConfigureAwait(false);
        }
        catch (PlanetLoadException) when (_retryExhausted &&
            ExhaustTerrainRetry is VolumeWorkClass.Movement or VolumeWorkClass.Recovery)
        {
            return;
        }
        if (ExhaustTerrainRetry is not null && !_retryExhausted)
            throw new InvalidOperationException("The selected path did not exhaust its host retries.");
    }

    private async Task ObserveStockBoulderAfterFlightAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState player, CancellationToken token)
    {
        var materialHost = OrbitMaterialHost ?? throw new InvalidOperationException("Orbit Boulder evidence requires the production material host.");
        var props = Props ?? throw new InvalidOperationException("Orbit Boulder evidence requires the production prop stream.");
        var camera = Camera(_published.View, player.Position, player.Heading);
        using var lease = terrain.TryRead() ?? throw new InvalidOperationException("No landed terrain for the Orbit Boulder checkpoint.");
        if (lease.Info != _published)
            throw new InvalidOperationException("Orbit Boulder checkpoint must use the published landed terrain.");

        // This extra presentation runs only after the measured flight and does not move the avatar
        // or add any object. Look at an accepted, unoccluded placement in the stock scene.
        for (var attempt = 0; attempt < OrbitTerrainMaterials.MaximumPreparationFrames; attempt++)
        {
            props.Pump();
            var view = camera;
            var stockTargetSelected = props.TryFindVisibleBoulder(lease, camera.Position, out var target);
            if (stockTargetSelected)
            {
                var offset = Subtract(target, camera.Position);
                var forward = Vector3.Normalize(new((float)offset.X, (float)offset.Y, (float)offset.Z));
                view = ApplyNearEnvironment(camera with { Forward = forward }, _environmentTick, 0);
            }
            var sequence = ++_sequence;
            materialHost.RetryMaterials(renderer, sequence);
            var mapsResidentBefore = materialHost.MaterialsResident(renderer, _published.View);
            RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, sequence, view));
            await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token).ConfigureAwait(false);
            await WaitAsync(renderer, () => renderer.TryReadResourceDiagnostics(out var retired) &&
                retired.FrameSequence == sequence && retired.RetiredPresentSequence == sequence, token).ConfigureAwait(false);
            if (stockTargetSelected && mapsResidentBefore && materialHost.MaterialsResident(renderer, _published.View) &&
                renderer.TryReadResourceDiagnostics(out var gpu) &&
                R09TwilightPressure.TryWriteBoulderDraw(gpu, _published.View.RendererEpoch, sequence))
                return;
        }
        throw new InvalidOperationException("Orbit Combined did not draw a visible stock Boulder at the landed checkpoint.");
    }

    private async Task RunFlightRouteAsync(VolumeStreamingCoordinator terrain, ProceduralRenderHost renderHost,
        string phase, CancellationToken token)
    {
        var skyRoute = Environment.GetEnvironmentVariable("WYU_PACKAGE_G_SMOKE") == "1";
        const ulong twilightTick = 35_500;
        if (skyRoute && phase == "flight")
            _environmentTick = twilightTick;
        var player = await OpenTerrainAsync(terrain, renderHost.Active, token).ConfigureAwait(false);
        var readback = phase is "readback" or "marker-readback";
        if (readback && (skyRoute || MeasureR09 || BiomeMeasurements.Enabled))
            throw new InvalidOperationException("The WARP readback requires the unmeasured edited-pad fixture.");
        if (readback || phase is "flight" or "abort")
        {
            player = await CommitInitialSmokePoseAsync(terrain, player, token).ConfigureAwait(false);
            var anchor = skyRoute ? (PlanetVector?)null : new PlanetVector(21, 4105, 16);
            if (skyRoute)
            {
                var twilight = Camera(terrain.View, player.Position, player.Heading);
                var daylight = Atmosphere(twilight.Position, _environmentTick)?.Tint.X;
                if (twilight.Sky is null || daylight is null or <= 0.18f or >= 1f)
                    throw new InvalidOperationException("The sky flight fixture did not start at twilight.");
                Console.WriteLine(FormattableString.Invariant($"PACKAGE G twilight seed={Descriptor.Seed} tick={_environmentTick} tint={daylight:R}"));
            }
            else
            {
                var center = FlightCoverageCenter(player.Position);
                _measureCoverage = MeasureR09;
                await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, terrain.View.Frame, token,
                    coverageCenter: center, supportCells: FlightSupportCells, earlyPrebuild: _r09Lookahead?.Movement)
                    .ConfigureAwait(false);
                if (!TryFlightPad(terrain, anchor!.Value, out var before, out _, out var blocked))
                    throw new InvalidOperationException($"Unedited comparison pad unavailable ({blocked}).");
                var revisionBeforeEdit = _published.TerrainRevision;
                var edited = await EditAsync(terrain, renderHost.Active, player, new(21, 4105, 16), VolumeBrushOperation.Add,
                    (ushort)ReferenceTerrainMaterial.OchreStone, token, _r09Lookahead?.FirstEdit).ConfigureAwait(false);
                if (_retryExhausted && ExhaustTerrainRetry == VolumeWorkClass.FirstEdit)
                {
                    var begin = await BeginAsync(token).ConfigureAwait(false);
                    terrain.BeginTick(begin);
                    Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
                    if (edited == TerrainPublicationOutcome.Presented || _published.TerrainRevision != revisionBeforeEdit || terrain.Statistics.MovementHeld)
                        throw new InvalidOperationException("The exhausted edit changed terrain or did not restore movement.");
                    Console.WriteLine($"WP11 HOST RETRY EDIT RECOVERED revision={revisionBeforeEdit}");
                    return;
                }
                if (edited != TerrainPublicationOutcome.Presented)
                    throw new InvalidOperationException("The flight fixture could not edit its landing pad.");
                await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, terrain.View.Frame, token,
                    coverageCenter: center, supportCells: FlightSupportCells).ConfigureAwait(false);
                if (!TryFlightPad(terrain, anchor.Value, out var after, out _, out blocked) || Length(after) - Length(before) < 0.5)
                    throw new InvalidOperationException($"The landing footprint did not move with the terrain edit ({blocked}).");
                Console.WriteLine(FormattableString.Invariant(
                    $"WP11 FLIGHT PAD before={Length(before):R} edited={Length(after):R} revision={_published.TerrainRevision}"));
            }
            if (!readback)
                RequireSmokeSave(await SaveAsync(terrain, token).ConfigureAwait(false), 1);
            if (SkyMeasurements.Enabled)
                await MeasureSkyPhaseAsync(terrain, renderHost.Active,
                    Camera(terrain.View, player.Position, player.Heading), "surface", false, _environmentTick, token).ConfigureAwait(false);
            var abort = phase == "abort";
            var flight = await FlyAsync(terrain, renderHost, player, anchor, true, token, abortAtFar: abort).ConfigureAwait(false);
            if (readback)
            {
                if (!flight.Quit || renderHost.WarpReadback is not { } qualification)
                    throw new InvalidOperationException("The WARP readback did not end at its qualification checkpoint.");
                qualification.VerifyComplete();
                return;
            }
            if (flight.Quit != abort)
                throw new InvalidOperationException("The scripted route exited before touchdown.");
            if (abort)
            {
                PrintPreflightCheckpoint(player);
                return;
            }
            player = flight.Player;
            RequireSmokeSave(await SaveAsync(terrain, token).ConfigureAwait(false), 2);
            if (MeasureR09 && R09IndependentTexturePressure.OrbitRequested)
                await ObserveStockBoulderAfterFlightAsync(terrain, renderHost.Active, player, token).ConfigureAwait(false);
        }
        if (phase == "preflight")
        {
            PrintPreflightCheckpoint(player);
            return;
        }

        var checkpoint = slot?.Checkpoint ?? throw new InvalidOperationException("Missing landed checkpoint.");
        var installed = phase == "resume-installed";
        if (checkpoint.Generation != (installed ? 3UL : 2UL) || checkpoint.Player != player ||
            ProceduralLanding.Read(checkpoint, out var decoded, out var environmentTick) != SaveGameValidation.Valid ||
            decoded is not { } landed || environmentTick != (skyRoute ? twilightTick : ReferenceWeather.InitialTick) +
                (ulong)landed.FlightTick + (installed ? 60UL : 0UL) ||
            environmentTick != _environmentTick)
            throw new InvalidOperationException("Landed checkpoint differs from the current player or flight state.");
        var camera = Camera(terrain.View, player.Position, player.Heading);
        if (phase is "interrupt" or "interrupt-installed")
        {
            _environmentTick = checked(_environmentTick + 60);
            var write = await SaveAsync(terrain, token).ConfigureAwait(false);
            if (phase == "interrupt-installed")
                throw new InvalidOperationException($"The after-install probe did not suspend the writer ({write.Status}).");
            if (write is not { Status: VolumeSlotStatus.Indeterminate, Member: "volume.a" } ||
                write.Error?.Message != "NTFS checkpoint rename failed." || !slot!.HasPendingWrite)
                throw new InvalidOperationException($"Expected blocked landed checkpoint publication ({write.Status}, {write.Member}).");
            Console.WriteLine("WP11 LANDED INTERRUPT READY");
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return;
        }
        if (phase is "resume" or "resume-installed")
        {
            VerifyEnvironmentPayload(checkpoint, landed);
            using var lease = terrain.TryRead() ?? throw new InvalidOperationException("Missing restored terrain.");
            var outer = planet.Descriptor.SampleAt(Scale(Unit(landed.Foot), FlightBoundary + 1));
            if (!skyRoute && lease.TryReadDensity(outer, out _))
                throw new InvalidOperationException("The restore fixture must omit the upper flight corridor.");
        }
        var digest = ConfirmRestoredLanding(terrain, landed);
        var foot = landed.Foot;
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 LANDED generation={checkpoint.Generation} tick={landed.FlightTick} revision={landed.Revision} overlay={digest} craft={foot.X:R},{foot.Y:R},{foot.Z:R} player={player.Position.X:R},{player.Position.Y:R},{player.Position.Z:R}"));
        Console.WriteLine(FormattableString.Invariant(
            $"PACKAGE E LANDED tick={environmentTick} tint={Atmosphere(camera.Position, environmentTick)?.Tint.X:R} fog={camera.Fog?.MaxOpacity:R} band={camera.Fog?.StartDepthMeters:R},{camera.Fog?.EndDepthMeters:R}"));
        if (camera.Sunlight is { } light)
            Console.WriteLine(FormattableString.Invariant(
                $"WP15 LANDED seed={Descriptor.Seed} tick={environmentTick} sun={light.Direction.Y:R},{light.Direction.Z:R} direct={light.Direct.X:R} ambient={light.Ambient.X:R} shadows={camera.TerrainShadows}"));
        if (skyRoute && camera.Sky is { } sky)
            Console.WriteLine(FormattableString.Invariant(
                $"PACKAGE G landed seed={Descriptor.Seed} tick={environmentTick} sun={sky.SunDirection.Y:R},{sky.SunDirection.Z:R} eye={sky.EyeMeters.X:R},{sky.EyeMeters.Y:R},{sky.EyeMeters.Z:R}"));
    }

    private static void VerifyEnvironmentPayload(VolumeCheckpoint checkpoint, ProceduralLanding landed)
    {
        Span<byte> environment = stackalloc byte[53];
        if (ProceduralLanding.Write(landed, checkpoint.Player, checkpoint.TerrainRevision, ReferenceWeather.InitialTick,
                environment) != environment.Length)
            throw new InvalidOperationException("Could not construct the legacy landing fixture.");
        Span<byte> legacy = stackalloc byte[44];
        "WYUVOL02"u8.CopyTo(legacy);
        environment[17..].CopyTo(legacy[8..]);
        if (ProceduralLanding.Read(checkpoint, legacy, out var oldLanding, out var oldTick) != SaveGameValidation.Valid ||
            oldLanding != landed || oldTick != ReferenceWeather.InitialTick ||
            ProceduralLanding.Read(checkpoint, "WYUVOL01"u8, out oldLanding, out oldTick) != SaveGameValidation.Valid ||
            oldLanding is not null || oldTick != ReferenceWeather.InitialTick)
            throw new InvalidOperationException("Legacy environment payload migration failed.");

        if (ProceduralLanding.Read(checkpoint, environment[..^1], out _, out _) != SaveGameValidation.Malformed)
            throw new InvalidOperationException("Truncated environment payload was accepted.");
        environment[16] = 2;
        if (ProceduralLanding.Read(checkpoint, environment, out _, out _) != SaveGameValidation.Malformed)
            throw new InvalidOperationException("Malformed environment payload was accepted.");
    }

    private void PrintPreflightCheckpoint(PlanetWalkState player)
    {
        var checkpoint = slot?.Checkpoint ?? throw new InvalidOperationException("Missing preflight checkpoint.");
        if (checkpoint.Generation != 1 || checkpoint.Player != player ||
            ProceduralLanding.Read(checkpoint, out var landing, out var environmentTick) != SaveGameValidation.Valid ||
            landing is not null || environmentTick != ReferenceWeather.InitialTick || environmentTick != _environmentTick)
            throw new InvalidOperationException("An aborted flight changed its preflight checkpoint.");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(checkpoint.Overlay));
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 PREFLIGHT generation={checkpoint.Generation} tick={checkpoint.Tick} revision={checkpoint.TerrainRevision} overlay={hash} player={player.Position.X:R},{player.Position.Y:R},{player.Position.Z:R}"));
    }
}
