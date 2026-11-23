using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private bool _quitAfterSave;
    private ManualSaveAttempt? _lastManualSave;
    private ReferenceTerrainMaterial _material = ReferenceTerrainMaterial.Basalt;
    private string? _notice;
    private long _noticeUntil;
    private OrePreview? _oreConfirmation;
    private long _oreConfirmUntil;

    private string? CurrentNotice => Environment.TickCount64 < _noticeUntil ? _notice : null;

    private void Notice(string message)
    {
        _notice = message;
        _noticeUntil = Environment.TickCount64 + 4500;
        if (overlay is not null)
            overlay.Menu = message;
    }

    private string ReadSaveStatus()
    {
        var mode = slot switch
        {
            null => "Save unavailable (no slot)",
            { ReadOnly: true } => "Save slot read-only",
            { NeedsInventoryImport: true } => "Save slot needs import",
            _ => "Save slot writable",
        };
        var generation = slot?.Checkpoint?.Generation is { } current
            ? FormattableString.Invariant($"Slot generation {current}") : "No committed generation";
        var pending = slot?.HasPendingWrite == true ? "Write pending · F5/F10 retry" : "No pending write";
        var summary = $"{mode}\n{generation}\n{pending}";
        if (_lastManualSave is not { } last)
            return $"{summary}\nNo manual save attempt yet.";

        var retry = last.Retry ? " retry" : "";
        var result = last.Status switch
        {
            VolumeSlotStatus.Committed or VolumeSlotStatus.AlreadyCommitted when last.Generation is null => "No checkpoint confirmed",
            VolumeSlotStatus.AlreadyCommitted => "Already committed",
            VolumeSlotStatus.Empty => "No save slot",
            VolumeSlotStatus.ReadOnlyRecovery => slot is null ? "No save slot" : "Read-only recovery",
            VolumeSlotStatus.InvalidCheckpoint => "Invalid checkpoint",
            VolumeSlotStatus.StorageError => "Storage error",
            _ => last.Status.ToString(),
        };
        summary += $"\nLast {last.Command}{retry}\nResult: {result}";
        return last.Generation is { } saved
            ? FormattableString.Invariant($"{summary}\nSaved generation {saved}") : summary;
    }

    private async Task<bool> HandleWorldCommandsAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState state, InputSnapshot input, CancellationToken token)
    {
        if (cave is null && PrototypeMenu.Pressed(input, InputControl.Digit1))
        {
            _material = ReferenceTerrainMaterial.Basalt;
            Notice("Material: basalt");
        }
        if (cave is null && PrototypeMenu.Pressed(input, InputControl.Digit2))
        {
            _material = ReferenceTerrainMaterial.OchreStone;
            Notice("Material: ochre stone");
        }
        var saveAndQuit = PrototypeMenu.Pressed(input, InputControl.F10);
        if (saveAndQuit || PrototypeMenu.Pressed(input, InputControl.F5))
        {
            var completion = await RequestManualSaveAsync(terrain, token).ConfigureAwait(false);
            _quitAfterSave = saveAndQuit && completion.CanLeave;
            Notice(ManualSaveNotice(completion, saveAndQuit ? "F10" : "F5"));
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
        var target = AimTerrainEdit(terrain, state, operation);
        if (target.Status != TerrainEditAimStatus.Target)
        {
            Notice(target.Status switch
            {
                TerrainEditAimStatus.NoTarget => "Aim at visible terrain 3.5–10 m away.",
                TerrainEditAimStatus.CoveragePending => "Terrain edit held. Wait for coverage or aim closer, then try again.",
                _ => "Edit blocked. Terrain retained.",
            });
            return true;
        }
        var center = target.Center;

        if (add && ReadInventory()?.Construction is { Blocks.IsEmpty: false } buildings &&
            Collision(buildings).Intersects(ConstructionCollision.TerrainEditBox(center, Descriptor.CellSizeMeters)))
        {
            Notice("Terrain fill is blocked by a construction footprint.");
            return true;
        }

        OrePreview? approval = null;
        if (!add && cave is not null && ReadInventory() is { } inventory &&
            // A 3-vertex-wide brush can affect at most four cells on each axis.
            InventoryOperations.AcquireOre(inventory.Items, 64).Status != InventoryChangeStatus.Applied)
        {
            using var lease = terrain.TryRead();
            if (lease is null || lease.Info != _published || !InventoryOrePreview.TryCount(lease, cave.Field, center, out var deposits))
            {
                Notice("Mining held. Wait for confirmed terrain coverage.");
                return true;
            }
            if (deposits > 0)
            {
                var preview = InventoryOperations.AcquireOre(inventory.Items, deposits);
                if (preview is { Status: InventoryChangeStatus.OverloadConfirmation, ProjectedLoad: { } projected })
                {
                    var confirmation = new OrePreview(center, _published.TerrainRevision, inventory.Items.Revision, deposits);
                    if (_oreConfirmation != confirmation || Environment.TickCount64 >= _oreConfirmUntil)
                    {
                        _oreConfirmation = confirmation;
                        _oreConfirmUntil = Environment.TickCount64 + 4500;
                        Notice($"Mining would overload you\n{InventoryLoadText(projected)}\nE again at this spot to confirm");
                        return true;
                    }
                    approval = confirmation;
                }
                else if (preview.Status != InventoryChangeStatus.Applied)
                {
                    Notice($"Ore acquisition blocked. Terrain retained.\n{InventoryResultText(preview, InventoryAction.Transfer)}");
                    return true;
                }
            }
        }
        _oreConfirmation = null;
        await ApplyTerrainEditAsync(terrain, renderer, state, target, operation, approval, token).ConfigureAwait(false);
        return true;
    }

    private async Task<TerrainPublicationOutcome> ApplyTerrainEditAsync(VolumeStreamingCoordinator terrain,
        D3D12Renderer renderer, PlanetWalkState state, TerrainEditAim target, VolumeBrushOperation operation,
        OrePreview? approval, CancellationToken token)
    {
        var beforeOre = CaveOre;
        var result = await EditAsync(terrain, renderer, state, target.Center, operation,
            cave is null ? (ushort)_material : CavePlanet.ShallowRock, token, approvedOre: approval,
            expectedRevision: target.Revision).ConfigureAwait(false);
        diagnostics?.Write(result == TerrainPublicationOutcome.Presented ? "edit" : "edit-held",
            $"operation={operation} revision={_published.TerrainRevision} outcome={result}");
        var acquired = CaveOre - beforeOre;
        Notice(result switch
        {
            TerrainPublicationOutcome.Presented when acquired > 0 => $"Ore +{acquired} | Inventory {CaveOre}",
            TerrainPublicationOutcome.Presented => $"Terrain changed (revision {_published.TerrainRevision})",
            TerrainPublicationOutcome.Busy => "Terrain editing is busy. Wait for terrain work to finish, then try again.",
            TerrainPublicationOutcome.Stale => "The edit is out of date. Aim again before editing.",
            _ => "Terrain edit rejected. The terrain was kept.",
        });
        return result;
    }

    private TerrainEditAim AimTerrainEdit(VolumeStreamingCoordinator terrain, PlanetWalkState state, VolumeBrushOperation operation)
    {
        var hit = TraceTerrainAim(terrain, state, out var status, out var revision);
        if (status == VolumeQueryStatus.CoverageUnavailable)
            return new(TerrainEditAimStatus.CoveragePending);
        if (status == VolumeQueryStatus.Clear)
            return new(TerrainEditAimStatus.NoTarget);
        if (hit is not { } first)
            return new(TerrainEditAimStatus.QueryRejected);
        if (first.Distance < 3.5)
            return new(TerrainEditAimStatus.NoTarget);
        var offset = operation == VolumeBrushOperation.Add ? 0.75 : -0.75;
        var center = Descriptor.SampleAt(new PlanetVector(first.Position.X + offset * first.Normal.X,
            first.Position.Y + offset * first.Normal.Y, first.Position.Z + offset * first.Normal.Z)).Vertex;
        return new(TerrainEditAimStatus.Target, center, first.Material, revision);
    }

    private VolumeRayHit? TraceTerrainAim(VolumeStreamingCoordinator terrain, PlanetWalkState state,
        out VolumeQueryStatus status, out ulong revision)
    {
        using var lease = terrain.TryRead();
        if (lease is null || lease.Info != _published || terrain.Statistics.MovementHeld)
        {
            status = VolumeQueryStatus.CoverageUnavailable;
            revision = 0;
            return null;
        }

        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        var hit = RaycastTerrainEdit(lease, Camera(terrain.View, state.Position, state.Heading), hits);
        status = hit.Status;
        revision = lease.Info.TerrainRevision;
        return status == VolumeQueryStatus.Contact && hit.Count > 0 ? hits[0] : null;
    }

    private static VolumeRayResult RaycastTerrainEdit(VolumeStreamingLease lease, VolumeCamera camera, Span<VolumeRayHit> hits)
    {
        var start = camera.Position;
        var end = new PlanetVector(start.X + camera.Forward.X * 10, start.Y + camera.Forward.Y * 10,
            start.Z + camera.Forward.Z * 10);
        return lease.Raycast(start, end, hits);
    }

    private async Task<TerrainPublicationOutcome> EditAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer, PlanetWalkState state,
        LatticeCoordinate center, VolumeBrushOperation operation, ushort material, CancellationToken token,
        R09EarlyPrebuild? earlyPrebuild = null, OrePreview? approvedOre = null, ulong? expectedRevision = null)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        var edit = new VolumeBrushIntent(begin.Tick, 1, expectedRevision ?? _published.TerrainRevision,
            new(center.X - 1, center.Y - 1, center.Z - 1), new(center.X + 1, center.Y + 1, center.Z + 1),
            operation, operation == VolumeBrushOperation.Add ? material : (ushort)0);
        var admission = terrain.SealEditTick(begin.Tick, Descriptor, [edit]);
        var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        if (approvedOre is { } selected && admission == VolumeAdmission.Accepted)
        {
            if (ReadInventory() is not { } inventory || inventory.Items.Revision != selected.InventoryRevision ||
                edit.ExpectedRevision != selected.TerrainRevision)
            {
                Finish(cut);
                terrain.RejectEdit(edit.Tick, edit.Id);
                return TerrainPublicationOutcome.Stale;
            }
            if (!CommitInventoryRoot(cut, inventory, inventory with { OreApproval = new(edit, selected.InventoryRevision, selected.Deposits) }, token))
            {
                terrain.RejectEdit(edit.Tick, edit.Id);
                token.ThrowIfCancellationRequested();
                return TerrainPublicationOutcome.Rejected;
            }
        }
        else
            Finish(cut);
        if (admission != VolumeAdmission.Accepted)
            return EditAdmissionOutcome(admission);

        var frame = terrain.View.Frame;
        var outcome = await LoadAsync(terrain, renderer, state.Position, state.Heading, frame, token, edit,
                operationStarted: started, earlyPrebuild: earlyPrebuild).ConfigureAwait(false);
        if (outcome == TerrainPublicationOutcome.Presented)
        {
            if (!terrain.TryReadEdit(edit.Tick, edit.Id, out var status, out var receipt) || status != VolumeEditStatus.Committed ||
                receipt is null || _published.TerrainRevision != edit.ExpectedRevision + 1)
                throw new InvalidOperationException("Terrain presented without a committed edit receipt.");
            return outcome;
        }
        if (terrain.TryReadEdit(edit.Tick, edit.Id, out var settled, out _) && settled == VolumeEditStatus.Stale)
            outcome = TerrainPublicationOutcome.Stale;
        terrain.RejectEdit(edit.Tick, edit.Id);
        await LoadAsync(terrain, renderer, state.Position, state.Heading, frame, token, recovery: true).ConfigureAwait(false);
        return outcome;
    }

    private static TerrainPublicationOutcome EditAdmissionOutcome(VolumeAdmission admission) => admission switch
    {
        VolumeAdmission.Backpressure => TerrainPublicationOutcome.Busy,
        VolumeAdmission.Stale => TerrainPublicationOutcome.Stale,
        _ => TerrainPublicationOutcome.Rejected,
    };

    private async Task<ManualSaveCompletion> RequestManualSaveAsync(VolumeStreamingCoordinator terrain, CancellationToken token)
    {
        var settling = slot?.HasPendingWrite == true;
        var result = await SaveAsync(terrain, token).ConfigureAwait(false);
        return new(result, settling);
    }

    private string ManualSaveNotice(ManualSaveCompletion completion, string command, bool sessionMenu = false)
    {
        var result = completion.Result;
        var confirmed = completion.Confirmed;
        _lastManualSave = new(result.Status, confirmed?.Generation, command, completion.Settling);
        diagnostics?.Write(confirmed is not null ? "save" : "save-held",
            $"status={result.Status} generation={result.Checkpoint?.Generation ?? 0} revision={_published.TerrainRevision}");
        if (result.Error is { } error)
            diagnostics?.Write("save-error", $"{error.GetType().Name} 0x{error.HResult:X8} {error.Message}");
        if (confirmed is null)
            return SaveFailureNotice(result.Status, sessionMenu);
        if (completion.Settling)
        {
            var next = sessionMenu ? "Select Save to save current progress or Save and Leave to leave after saving."
                : "F5 to save current state · F10 to save and leave";
            return $"Previous save confirmed (generation {confirmed.Generation}).\n{next}";
        }
        return $"Saved generation {confirmed.Generation}";
    }

    private static string SaveFailureNotice(VolumeSlotStatus status, bool sessionMenu) => status switch
    {
        VolumeSlotStatus.Empty => "There is no save slot for this session.",
        VolumeSlotStatus.ReadOnlyRecovery => "This recovered world is read-only.\nSelect Leave in the session menu to exit without saving.",
        VolumeSlotStatus.Incompatible => "This save format is incompatible. The existing files were kept.\nChoose a compatible world to save.",
        VolumeSlotStatus.Busy => "Save busy. Wait for terrain work or close any other session using this save.\n" +
            (sessionMenu ? "Select Save to try again." : "Press F5 to try again."),
        VolumeSlotStatus.StorageError => "Save failed. Check storage access and free space.\n" +
            (sessionMenu ? "Select Save to retry." : "Press F5 to retry."),
        VolumeSlotStatus.Indeterminate => "Save could not be confirmed. Wait for storage recovery.\n" +
            (sessionMenu ? "Select Save to verify the pending write." : "Press F5 to check again."),
        VolumeSlotStatus.Stale or VolumeSlotStatus.Conflict => "The save request is out of date or conflicts with this slot.\nReopen the world from its last checkpoint.",
        VolumeSlotStatus.Corrupt => "Save rejected. This slot has damaged checkpoint data.",
        VolumeSlotStatus.Canceled => "Save canceled.",
        VolumeSlotStatus.InvalidCheckpoint => "Save failed. The current world state could not be saved.",
        _ => "Save failed. No new checkpoint was confirmed.",
    };

    private async Task<VolumeSlotResult> SaveAsync(VolumeStreamingCoordinator terrain, CancellationToken token)
    {
        if (slot is null)
            return new(VolumeSlotStatus.Empty);
        if (slot.ReadOnly)
            return new(VolumeSlotStatus.ReadOnlyRecovery);
        if (slot.HasPendingWrite)
            return await Task.Run(() => slot.Execute(token)).ConfigureAwait(false);
        if (_ports.Committed.Read().Root is not VolumeWorldRoot { Player: not null })
            return new(VolumeSlotStatus.InvalidCheckpoint);

        var begin = await BeginAsync(token).ConfigureAwait(false);
        using var reservation = terrain.TryReserveCheckpoint(begin.Tick);
        Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
        if (reservation?.TryTake() is not { } cut)
            return new(VolumeSlotStatus.Busy);

        var environmentTick = _environmentTick;
        return await Task.Run(() => slot.Save(cut, environmentTick, token)).ConfigureAwait(false);
    }

    private readonly record struct ManualSaveAttempt(VolumeSlotStatus Status, ulong? Generation, string Command, bool Retry);

    private readonly record struct ManualSaveCompletion(VolumeSlotResult Result, bool Settling)
    {
        internal VolumeCheckpoint? Confirmed => Result.Status is VolumeSlotStatus.Committed or VolumeSlotStatus.AlreadyCommitted
            ? Result.Checkpoint : null;
        internal bool CanLeave => Confirmed is not null && !Settling;
    }

    private enum TerrainEditAimStatus { NoTarget, CoveragePending, QueryRejected, Target }

    private readonly record struct TerrainEditAim(TerrainEditAimStatus Status, LatticeCoordinate Center = default,
        ushort Material = 0, ulong Revision = 0);

    private enum TerrainPublicationOutcome { Rejected, Presented, Busy, Stale }

    private readonly record struct OrePreview(LatticeCoordinate Center, ulong TerrainRevision, ulong InventoryRevision, int Deposits);
}
