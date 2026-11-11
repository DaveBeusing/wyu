using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private bool _quitAfterSave;
    private ReferenceTerrainMaterial _material = ReferenceTerrainMaterial.Basalt;
    private string? _notice;
    private long _noticeUntil;

    private string? CurrentNotice => Environment.TickCount64 < _noticeUntil ? _notice : null;

    private void Notice(string message)
    {
        _notice = message;
        _noticeUntil = Environment.TickCount64 + 4500;
        if (overlay is not null)
            overlay.Menu = message;
    }

    private async Task<bool> HandleWorldCommandsAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState state, InputSnapshot input, CancellationToken token)
    {
        if (PrototypeMenu.Pressed(input, InputControl.Digit1))
        {
            _material = ReferenceTerrainMaterial.Basalt;
            Notice("Material: basalt");
        }
        if (PrototypeMenu.Pressed(input, InputControl.Digit2))
        {
            _material = ReferenceTerrainMaterial.OchreStone;
            Notice("Material: ochre stone");
        }
        var saveAndQuit = PrototypeMenu.Pressed(input, InputControl.F10);
        if (saveAndQuit || PrototypeMenu.Pressed(input, InputControl.F5))
        {
            var result = await SaveAsync(terrain, token).ConfigureAwait(false);
            var saved = result.Status is VolumeSlotStatus.Committed or VolumeSlotStatus.AlreadyCommitted;
            _quitAfterSave = saveAndQuit && saved;
            Notice(saved && result.Checkpoint is { } checkpoint
                ? $"Saved generation {checkpoint.Generation}" : $"Save unavailable: {result.Status}");
            return true;
        }

        var add = PrototypeMenu.Pressed(input, InputControl.Q);
        if (!add && !PrototypeMenu.Pressed(input, InputControl.E))
            return false;
        if (slot is null || slot.ReadOnly)
        {
            Notice("This recovered world is read-only.");
            return true;
        }
        var operation = add ? VolumeBrushOperation.Add : VolumeBrushOperation.Remove;
        if (Aim(terrain, state, operation) is not { } center)
        {
            Notice("Aim at visible terrain 3.5–10 m away.");
            return true;
        }

        var edited = await EditAsync(terrain, renderer, state, center, operation, (ushort)_material, token).ConfigureAwait(false);
        Notice(edited ? $"Terrain changed (revision {_published.TerrainRevision})" : "Edit blocked. Terrain retained.");
        return true;
    }

    private LatticeCoordinate? Aim(VolumeStreamingCoordinator terrain, PlanetWalkState state, VolumeBrushOperation operation)
    {
        using var lease = terrain.TryRead();
        if (lease is null || lease.Info != _published || terrain.Statistics.MovementHeld)
            return null;
        var camera = Camera(terrain.View, state.Position, state.Heading);
        var start = camera.Position;
        var end = new PlanetVector(start.X + camera.Forward.X * 10, start.Y + camera.Forward.Y * 10,
            start.Z + camera.Forward.Z * 10);
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        var hit = lease.Raycast(start, end, hits);
        if (hit.Status != VolumeQueryStatus.Contact || hits[0].Distance < 3.5)
            return null;
        var first = hits[0];
        var offset = operation == VolumeBrushOperation.Add ? 0.75 : -0.75;
        return planet.Descriptor.SampleAt(new PlanetVector(first.Position.X + offset * first.Normal.X,
            first.Position.Y + offset * first.Normal.Y, first.Position.Z + offset * first.Normal.Z)).Vertex;
    }

    private async Task<bool> EditAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer, PlanetWalkState state,
        LatticeCoordinate center, VolumeBrushOperation operation, ushort material, CancellationToken token)
    {
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        var edit = new VolumeBrushIntent(begin.Tick, 1, _published.TerrainRevision,
            new(center.X - 1, center.Y - 1, center.Z - 1), new(center.X + 1, center.Y + 1, center.Z + 1),
            operation, operation == VolumeBrushOperation.Add ? material : (ushort)0);
        var admission = terrain.SealEditTick(begin.Tick, planet.Descriptor, [edit]);
        Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
        if (admission != VolumeAdmission.Accepted)
            return false;

        var frame = terrain.View.Frame;
        if (await LoadAsync(terrain, renderer, state.Position, state.Heading, frame, token, edit).ConfigureAwait(false))
        {
            if (!terrain.TryReadEdit(edit.Tick, edit.Id, out var status, out _) || status != VolumeEditStatus.Committed)
                throw new InvalidOperationException("Terrain presented without a committed edit receipt.");
            return true;
        }
        terrain.RejectEdit(edit.Tick, edit.Id);
        await LoadAsync(terrain, renderer, state.Position, state.Heading, frame, token).ConfigureAwait(false);
        return false;
    }

    private async Task<VolumeSlotResult> SaveAsync(VolumeStreamingCoordinator terrain, CancellationToken token)
    {
        if (slot is null || slot.ReadOnly)
            return new(VolumeSlotStatus.ReadOnlyRecovery);
        if (slot.HasPendingWrite)
        {
            // Settle the original operation before capturing the state requested by this Save.
            var settled = await Task.Run(() => slot.Execute(token)).ConfigureAwait(false);
            if (settled.Status is not (VolumeSlotStatus.Committed or VolumeSlotStatus.AlreadyCommitted))
                return settled;
        }
        if (_ports.Committed.Read().Root is not VolumeWorldRoot { Player: not null })
            return new(VolumeSlotStatus.InvalidCheckpoint);

        var begin = await BeginAsync(token).ConfigureAwait(false);
        using var reservation = terrain.TryReserveCheckpoint(begin.Tick);
        Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
        if (reservation?.TryTake() is not { } cut)
            return new(VolumeSlotStatus.Busy);

        return await Task.Run(() => slot.Save(cut, token)).ConfigureAwait(false);
    }
}
