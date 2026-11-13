using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static partial class GameHost
{
    private static async Task<VolumeWorldSlot> OpenVolumeSmokeSlotAsync(string root, string phase, CancellationToken token,
        ulong seed = 42)
    {
        var v2 = VolumeSmokeNamespace(root);
        if (phase == "init")
        {
            if (Directory.Exists(v2))
                throw new InvalidOperationException("Volume save fixture must start with a fresh v2 namespace.");
            return VolumeWorldSlot.Create(v2, seed);
        }

        var id = VolumeWorldSlot.List(v2).Single();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var slot = VolumeWorldSlot.Open(v2, id, out var status);
            if (slot is not null)
                return slot;
            if (status is not (VolumeSlotStatus.Busy or VolumeSlotStatus.Indeterminate))
                throw new InvalidOperationException($"Volume save fixture could not continue ({status}).");
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        throw new TimeoutException("Volume save fixture remained locked after writer exit.");
    }

    internal static void VerifyProtectedVolumeSmokeSlot(string root)
    {
        var v2 = VolumeSmokeNamespace(root);
        using var slot = VolumeWorldSlot.Open(v2, Guid.ParseExact("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "N"), out var status);
        if (slot is not null || status != VolumeSlotStatus.Incompatible)
            throw new InvalidOperationException($"Old volume slot was not protected ({status}).");
        Console.WriteLine("PASS: incompatible volume slot rejected without opening a writer.");
    }

    private static string VolumeSmokeNamespace(string root)
    {
        var fixture = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(fixture, "wyu-volume-smoke.fixture")))
            throw new InvalidOperationException("Volume save smoke requires its isolated fixture directory.");
        return Path.Combine(fixture, "v2");
    }
}

internal sealed partial class VolumeExploration
{
    internal async Task RunSaveSmokeAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        string phase, CancellationToken token)
    {
        var state = await OpenTerrainAsync(terrain, renderer, token).ConfigureAwait(false);
        if (phase == "init")
        {
            state = await CommitInitialSmokePoseAsync(terrain, state, token).ConfigureAwait(false);
            var before = _published;
            if (await EditAsync(terrain, renderer, state, new(1000, 4103, 32), VolumeBrushOperation.Remove, 0, token)
                .ConfigureAwait(false) || _published != before)
                throw new InvalidOperationException("An edit outside admitted coverage changed the terrain.");
            await RequireSmokeEditAsync(terrain, renderer, state, new(16, 4103, 32), VolumeBrushOperation.Remove, token)
                .ConfigureAwait(false);
            await RequireSmokeEditAsync(terrain, renderer, state, new(21, 4106, 35), VolumeBrushOperation.Add, token)
                .ConfigureAwait(false);
            var away = state.Position with { Z = -16 };
            await LoadAsync(terrain, renderer, state.Position, state.Heading, terrain.View.Frame, token, coverageCenter: away)
                .ConfigureAwait(false);
            using (var unloaded = terrain.TryRead() ?? throw new InvalidOperationException("Missing shifted collision window."))
            {
                if (unloaded.TryReadDensity(planet.Descriptor.SampleAt(new PlanetVector(16, 4103, 33)), out _) ||
                    unloaded.TryReadDensity(planet.Descriptor.SampleAt(new PlanetVector(21, 4106, 35)), out _))
                    throw new InvalidOperationException("The edited samples remained in the shifted window.");
            }
            await LoadAsync(terrain, renderer, state.Position, state.Heading, terrain.View.Frame, token)
                .ConfigureAwait(false);
            RequireSmokeSave(await SaveAsync(terrain, token).ConfigureAwait(false), 1);
            RequireSmokeSave(await SaveAsync(terrain, token).ConfigureAwait(false), 2);
        }
        else if (phase == "interrupt")
        {
            await RequireSmokeEditAsync(terrain, renderer, state, new(24, 4106, 35), VolumeBrushOperation.Add, token)
                .ConfigureAwait(false);
            var write = await SaveAsync(terrain, token).ConfigureAwait(false);
            if (write is not { Status: VolumeSlotStatus.Indeterminate, Member: "volume.a" } ||
                write.Error?.Message != "NTFS checkpoint rename failed." || !slot!.HasPendingWrite)
                throw new InvalidOperationException($"Expected a blocked NTFS publication ({write.Status}, {write.Member}).", write.Error);
            Console.WriteLine("WP11 INTERRUPT READY: real NTFS rename blocked; writer retained generation 3.");
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return;
        }

        using var lease = terrain.TryRead() ?? throw new InvalidOperationException("Missing checkpoint collision.");
        var checkpoint = slot?.Checkpoint ?? throw new InvalidOperationException("Missing checkpoint.");
        if (checkpoint.Generation != 2 || checkpoint.Player != state ||
            _published.TerrainRevision != checkpoint.TerrainRevision ||
            ProceduralLanding.Read(checkpoint, out var landing, out var environmentTick) != SaveGameValidation.Valid ||
            landing is not null || environmentTick != _environmentTick)
            throw new InvalidOperationException("Checkpoint generation, terrain revision or pose changed.");
        var bytes = new byte[lease.EditOverlayBytes];
        if (!lease.TryWriteEditOverlay(bytes, out var written) || written != bytes.Length ||
            !bytes.AsSpan().SequenceEqual(checkpoint.Overlay))
            throw new InvalidOperationException("Rebuilt terrain overlay differs from the committed checkpoint.");
        if (!lease.TryReadDensity(planet.Descriptor.SampleAt(new PlanetVector(16, 4103, 32)), out var cut) || cut != 128 ||
            !lease.TryReadDensity(planet.Descriptor.SampleAt(new PlanetVector(21, 4106, 35)), out var fill) || fill != -128)
            throw new InvalidOperationException("Saved cut or added terrain is missing.");
        var hits = new VolumeRayHit[8];
        var add = lease.Raycast(new(21, 4112, 35), new(21, 4103, 35), hits);
        if (add.Status != VolumeQueryStatus.Contact || hits[0].Material != (ushort)ReferenceTerrainMaterial.OchreStone ||
            hits[0].Position.Y < 4107)
            throw new InvalidOperationException("Added material or collision did not restore.");
        var addedHeight = hits[0].Position.Y;
        var removed = lease.Raycast(new(16, 4110, 32), new(16, 4098, 32), hits);
        if (removed.Status != VolumeQueryStatus.Contact || hits[0].Position.Y >= 4103)
            throw new InvalidOperationException("Removed terrain collision did not restore.");
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 CHECKPOINT gen={checkpoint.Generation} tick={checkpoint.Tick} rev={checkpoint.TerrainRevision} overlay={Convert.ToHexStringLower(SHA256.HashData(bytes))} pose={state.Position.X:R},{state.Position.Y:R},{state.Position.Z:R} add={addedHeight:R} cut={hits[0].Position.Y:R}"));
    }

    private async Task<PlanetWalkState> CommitInitialSmokePoseAsync(VolumeStreamingCoordinator terrain,
        PlanetWalkState state, CancellationToken token)
    {
        var begin = await BeginAsync(token).ConfigureAwait(false);
        if (!terrain.BeginTick(begin))
            throw new InvalidOperationException("Smoke spawn did not receive presentation acknowledgement.");
        var draft = terrain.TryPlanMovement(state, 0, 0, out var status)
            ?? throw new InvalidOperationException($"Smoke spawn pose rejected ({status}).");
        var candidates = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        var prepared = terrain.TryPrepareMovement(draft, candidates, [])
            ?? throw new InvalidOperationException("Smoke spawn pose could not prepare.");
        Finish(candidates, prepared);
        return draft.Result.State;
    }

    private async Task RequireSmokeEditAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState state, LatticeCoordinate center, VolumeBrushOperation operation, CancellationToken token)
    {
        if (!await EditAsync(terrain, renderer, state, center, operation,
                (ushort)ReferenceTerrainMaterial.OchreStone, token).ConfigureAwait(false))
            throw new InvalidOperationException($"Smoke brush {operation} at {center} did not publish.");
    }

    private static void RequireSmokeSave(VolumeSlotResult result, ulong generation)
    {
        if (result.Status != VolumeSlotStatus.Committed || result.Checkpoint?.Generation != generation)
            throw new InvalidOperationException($"Smoke save generation {generation} failed ({result.Status}).", result.Error);
    }
}
