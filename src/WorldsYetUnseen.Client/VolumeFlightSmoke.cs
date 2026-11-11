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
        var player = await OpenTerrainAsync(terrain, renderHost.Active, token).ConfigureAwait(false);
        if (phase is "flight" or "abort")
        {
            player = await CommitInitialSmokePoseAsync(terrain, player, token).ConfigureAwait(false);
            var anchor = new PlanetVector(21, 4105, 16);
            var center = FlightCoverageCenter(player.Position);
            await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, terrain.View.Frame, token,
                coverageCenter: center).ConfigureAwait(false);
            if (!TryFlightPad(terrain, anchor, out var before, out _, out var blocked))
                throw new InvalidOperationException($"Unedited comparison pad unavailable ({blocked}).");
            if (!await EditAsync(terrain, renderHost.Active, player, new(21, 4105, 16), VolumeBrushOperation.Add,
                    (ushort)ReferenceTerrainMaterial.OchreStone, token).ConfigureAwait(false))
                throw new InvalidOperationException("The flight fixture could not edit its landing pad.");
            await LoadAsync(terrain, renderHost.Active, player.Position, player.Heading, terrain.View.Frame, token,
                coverageCenter: center).ConfigureAwait(false);
            if (!TryFlightPad(terrain, anchor, out var after, out _, out blocked) || Length(after) - Length(before) < 0.5)
                throw new InvalidOperationException($"The landing footprint did not move with the terrain edit ({blocked}).");
            Console.WriteLine(FormattableString.Invariant(
                $"WP11 FLIGHT PAD before={Length(before):R} edited={Length(after):R} revision={_published.TerrainRevision}"));
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
        if (checkpoint.Generation != 2 || checkpoint.Player != player ||
            ProceduralLanding.Read(checkpoint, out var decoded) != SaveGameValidation.Valid || decoded is not { } landed)
            throw new InvalidOperationException("Landed checkpoint differs from the current player or flight state.");
        if (phase == "resume")
        {
            using var lease = terrain.TryRead() ?? throw new InvalidOperationException("Missing restored terrain.");
            var outer = planet.Descriptor.SampleAt(Scale(Unit(landed.Foot), FlightBoundary + 1));
            if (lease.TryReadDensity(outer, out _))
                throw new InvalidOperationException("The restore fixture must omit the upper flight corridor.");
        }
        var digest = ConfirmRestoredLanding(terrain, landed);
        var foot = landed.Foot;
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 LANDED generation={checkpoint.Generation} tick={landed.FlightTick} revision={landed.Revision} overlay={digest} craft={foot.X:R},{foot.Y:R},{foot.Z:R} player={player.Position.X:R},{player.Position.Y:R},{player.Position.Z:R}"));
    }

    private void PrintPreflightCheckpoint(PlanetWalkState player)
    {
        var checkpoint = slot?.Checkpoint ?? throw new InvalidOperationException("Missing preflight checkpoint.");
        if (checkpoint.Generation != 1 || checkpoint.Player != player ||
            ProceduralLanding.Read(checkpoint, out var landing) != SaveGameValidation.Valid || landing is not null)
            throw new InvalidOperationException("An aborted flight changed its preflight checkpoint.");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(checkpoint.Overlay));
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 PREFLIGHT generation={checkpoint.Generation} tick={checkpoint.Tick} revision={checkpoint.TerrainRevision} overlay={hash} player={player.Position.X:R},{player.Position.Y:R},{player.Position.Z:R}"));
    }
}
