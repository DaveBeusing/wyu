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
        var skyRoute = Environment.GetEnvironmentVariable("WYU_PACKAGE_G_SMOKE") == "1";
        const ulong twilightTick = 35_500;
        if (skyRoute && phase == "flight")
            _environmentTick = twilightTick;
        var player = await OpenTerrainAsync(terrain, renderHost.Active, token).ConfigureAwait(false);
        if (phase is "flight" or "abort")
        {
            player = await CommitInitialSmokePoseAsync(terrain, player, token).ConfigureAwait(false);
            var anchor = skyRoute ? (PlanetVector?)null : new PlanetVector(21, 4105, 16);
            if (skyRoute)
            {
                var twilight = Camera(terrain.View, player.Position, player.Heading);
                if (twilight.Sky is null || twilight.Tint.X is <= 0.18f or >= 1f)
                    throw new InvalidOperationException("The sky flight fixture did not start at twilight.");
                Console.WriteLine(FormattableString.Invariant($"PACKAGE G twilight seed={Descriptor.Seed} tick={_environmentTick} tint={twilight.Tint.X:R}"));
            }
            else
            {
                var center = FlightCoverageCenter(player.Position);
                await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, terrain.View.Frame, token,
                    coverageCenter: center, supportCells: FlightSupportCells).ConfigureAwait(false);
                if (!TryFlightPad(terrain, anchor!.Value, out var before, out _, out var blocked))
                    throw new InvalidOperationException($"Unedited comparison pad unavailable ({blocked}).");
                if (!await EditAsync(terrain, renderHost.Active, player, new(21, 4105, 16), VolumeBrushOperation.Add,
                        (ushort)ReferenceTerrainMaterial.OchreStone, token).ConfigureAwait(false))
                    throw new InvalidOperationException("The flight fixture could not edit its landing pad.");
                await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, terrain.View.Frame, token,
                    coverageCenter: center, supportCells: FlightSupportCells).ConfigureAwait(false);
                if (!TryFlightPad(terrain, anchor.Value, out var after, out _, out blocked) || Length(after) - Length(before) < 0.5)
                    throw new InvalidOperationException($"The landing footprint did not move with the terrain edit ({blocked}).");
                Console.WriteLine(FormattableString.Invariant(
                    $"WP11 FLIGHT PAD before={Length(before):R} edited={Length(after):R} revision={_published.TerrainRevision}"));
            }
            RequireSmokeSave(await SaveAsync(terrain, token).ConfigureAwait(false), 1);
            var abort = phase == "abort";
            var flight = await FlyAsync(terrain, renderHost, player, anchor, true, token, abortAtFar: abort).ConfigureAwait(false);
            if (flight.Quit != abort)
                throw new InvalidOperationException("The scripted route exited before touchdown.");
            if (abort)
            {
                PrintPreflightCheckpoint(player);
                return;
            }
            player = flight.Player;
            RequireSmokeSave(await SaveAsync(terrain, token).ConfigureAwait(false), 2);
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
            $"PACKAGE E LANDED tick={environmentTick} tint={camera.Tint.X:R} fog={camera.Fog?.MaxOpacity:R} band={camera.Fog?.StartDepthMeters:R},{camera.Fog?.EndDepthMeters:R}"));
        if (skyRoute && camera.Sky is { } sky)
            Console.WriteLine(FormattableString.Invariant(
                $"PACKAGE G landed seed={Descriptor.Seed} tick={environmentTick} sun={sky.SunDirection.Y:R},{sky.SunDirection.Z:R} eye={sky.EyeMeters.X:R},{sky.EyeMeters.Y:R},{sky.EyeMeters.Z:R}"));
    }

    private static void VerifyEnvironmentPayload(VolumeCheckpoint checkpoint, ProceduralLanding landed)
    {
        Span<byte> legacy = stackalloc byte[44];
        "WYUVOL02"u8.CopyTo(legacy);
        checkpoint.Game[17..].CopyTo(legacy[8..]);
        if (ProceduralLanding.Read(checkpoint, legacy, out var oldLanding, out var oldTick) != SaveGameValidation.Valid ||
            oldLanding != landed || oldTick != ReferenceWeather.InitialTick ||
            ProceduralLanding.Read(checkpoint, "WYUVOL01"u8, out oldLanding, out oldTick) != SaveGameValidation.Valid ||
            oldLanding is not null || oldTick != ReferenceWeather.InitialTick)
            throw new InvalidOperationException("Legacy environment payload migration failed.");

        Span<byte> malformed = stackalloc byte[53];
        checkpoint.Game.CopyTo(malformed);
        malformed[16] = 2;
        if (ProceduralLanding.Read(checkpoint, malformed, out _, out _) != SaveGameValidation.Malformed ||
            ProceduralLanding.Read(checkpoint, checkpoint.Game[..^1], out _, out _) != SaveGameValidation.Malformed)
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
