using Groundwork.Core;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal enum TerrainEditFeedbackMode { None, Automated, Keyboard, Outcomes, OutcomesKeyboardE, OutcomesKeyboardQ }

internal sealed partial class VolumeExploration
{
    private async Task VerifyTerrainEditFeedbackAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState state, TerrainEditFeedbackMode mode, CancellationToken token)
    {
        const string noTarget = "Aim at visible terrain 3.5–10 m away.";
        const string coveragePending = "Terrain edit held. Wait for coverage or aim closer, then try again.";
        if (slot is not { ReadOnly: false, Checkpoint: not null } || !terrain.Statistics.MovementHeld)
            throw new InvalidOperationException("The terrain feedback proof needs a writable checkpoint and unconsumed terrain presentation.");

        if (mode is TerrainEditFeedbackMode.Outcomes or TerrainEditFeedbackMode.OutcomesKeyboardE or TerrainEditFeedbackMode.OutcomesKeyboardQ)
        {
            await VerifyTerrainEditOutcomesAsync(terrain, renderer, state, mode, token).ConfigureAwait(false);
            return;
        }

        var keyboardOverlay = mode == TerrainEditFeedbackMode.Keyboard
            ? overlay ?? throw new InvalidOperationException("The keyboard proof requires the native overlay.")
            : null;
        var sequence = 0UL;
        await VerifyPairAsync(state, "awaiting-coverage", coveragePending).ConfigureAwait(false);

        await ConsumeTerrainProofPresentAsync(terrain, token).ConfigureAwait(false);

        _pitch = MathF.PI / 3;
        VerifyRay(state, VolumeQueryStatus.Clear);
        if (keyboardOverlay is not null)
        {
            var cameraSequence = ++_sequence;
            RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, cameraSequence,
                Camera(terrain.View, state.Position, state.Heading)));
            await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= cameraSequence, token,
                pauseWhileMinimized: true).ConfigureAwait(false);
        }
        await VerifyPairAsync(state, "clear-ray", noTarget).ConfigureAwait(false);
        if (keyboardOverlay is not null)
            return;

        // Only the probe origin moves. The saved player and published view stay where they are.
        var uncovered = state with { Position = Add(state.Position, Scale(Unit(state.Position), 256)) };
        VerifyRay(uncovered, VolumeQueryStatus.CoverageUnavailable);
        await VerifyPairAsync(uncovered, "uncovered-ray", coveragePending).ConfigureAwait(false);

        async Task VerifyPairAsync(PlanetWalkState probe, string scenario, string expectedNotice)
        {
            foreach (var key in new[] { InputControl.E, InputControl.Q })
            {
                var root = _ports.Committed.Read().Root;
                var publication = _published;
                var ore = CaveOre;
                var checkpoint = slot.Checkpoint;
                _notice = null;
                InputSnapshot input;
                var inputEvidence = "";
                if (keyboardOverlay is null)
                {
                    input = new InputSnapshot(++sequence,
                        [new(InputEventKind.Pressed, InputDevice.Keyboard, key, sequence, 0, default, 0)], [], null, false);
                }
                else
                {
                    keyboardOverlay.Menu = null;
                    keyboardOverlay.Performance = $"Terrain feedback proof · {scenario}\nFocus the game and press {key}.";
                    diagnostics?.Write("terrain-edit-feedback-ready", $"key={key} case={scenario}");
                    input = await ReadFocusedProofKeyAsync(key, token).ConfigureAwait(false);
                    inputEvidence = $" input=windows focused=true keySequence={input.Events[0].Sequence}";
                }
                var handled = await HandleWorldCommandsAsync(terrain, renderer, probe, input, token).ConfigureAwait(false);
                if (!handled || CurrentNotice != expectedNotice || !ReferenceEquals(root, _ports.Committed.Read().Root) ||
                    publication != _published || ore != CaveOre || !ReferenceEquals(checkpoint, slot.Checkpoint))
                    throw new InvalidOperationException($"Terrain feedback mismatch for {key} ({scenario}). Notice: {CurrentNotice}");
                diagnostics?.Write("terrain-edit-feedback-proof",
                    $"key={key} case={scenario} revision={publication.TerrainRevision} ore={ore} generation={checkpoint!.Generation} rootUnchanged=true{inputEvidence} notice={CurrentNotice}");
                if (keyboardOverlay is not null)
                {
                    keyboardOverlay.Performance = $"Terrain feedback proof · {scenario}\n{key} received with game focus. Enter to continue.";
                    await ReadFocusedProofKeyAsync(InputControl.Enter, token).ConfigureAwait(false);
                }
            }
        }

        void VerifyRay(PlanetWalkState probe, VolumeQueryStatus expected)
        {
            using var lease = terrain.TryRead();
            if (lease is null || lease.Info != _published)
                throw new InvalidOperationException("The terrain feedback proof has no matching collision lease.");
            Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
            var camera = Camera(terrain.View, probe.Position, probe.Heading);
            var result = RaycastTerrainEdit(lease, camera, hits);
            if (result.Status != expected)
                throw new InvalidOperationException($"Terrain feedback proof ray expected {expected}, got {result.Status}.");
        }
    }

    private async Task VerifyTerrainEditOutcomesAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState player, TerrainEditFeedbackMode mode, CancellationToken token)
    {
        const string busy = "Terrain editing is busy. Wait for terrain work to finish, then try again.";
        const string stale = "The edit is out of date. Aim again before editing.";
        const string rejected = "Terrain edit rejected. The terrain was kept.";
        var keyboardOverlay = mode != TerrainEditFeedbackMode.Outcomes
            ? overlay ?? throw new InvalidOperationException("The keyboard proof requires the native overlay.") : null;
        var key = mode == TerrainEditFeedbackMode.OutcomesKeyboardE ? InputControl.E : InputControl.Q;
        var operation = key == InputControl.E ? VolumeBrushOperation.Remove : VolumeBrushOperation.Add;
        var checkpoint = slot!.Checkpoint!;
        var ore = CaveOre;
        var sequence = 0UL;
        var lastKeyboardSequence = 0UL;
        await ConsumeTerrainProofPresentAsync(terrain, token).ConfigureAwait(false);
        var target = FindEditTarget();
        if (keyboardOverlay is not null)
        {
            var cameraSequence = ++_sequence;
            RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, cameraSequence,
                Camera(terrain.View, player.Position, player.Heading)));
            await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= cameraSequence, token,
                pauseWhileMinimized: true).ConfigureAwait(false);
        }

        var initial = _published;
        var initialRoot = ReadRoot();
        var initialOverlay = ReadOverlay();
        var initialPresent = renderer.Read().LastPresentedSequence;
        var busyTick = _tick + 1;
        var busyInput = await ReadCaseInputAsync("Busy").ConfigureAwait(false);
        await PressEditAsync(busyInput).ConfigureAwait(false);
        var recoveryPresent = renderer.Read().LastPresentedSequence;
        if (CurrentNotice != busy || !_retryExhausted || _published.TerrainRevision != initial.TerrainRevision ||
            _published.Generation == initial.Generation || _published.View == initial.View || recoveryPresent <= initialPresent ||
            !terrain.Statistics.MovementHeld || !ReferenceEquals(ReadRoot().State, initialRoot.State) ||
            ReadRoot().Player != initialRoot.Player || !ReadOverlay().AsSpan().SequenceEqual(initialOverlay) ||
            !terrain.TryReadEdit(busyTick, 1, out var denied, out var deniedCommit) || denied != VolumeEditStatus.Rejected || deniedCommit is not null)
            throw new InvalidOperationException("Busy terrain work did not recover the confirmed terrain, pose, and inventory.");
        await ConsumeTerrainProofPresentAsync(terrain, token).ConfigureAwait(false);
        await RecordCaseAsync("busy-recovered", busyInput,
            $"attempts=2 receipt={denied} recoveryPresent={recoveryPresent} movementReleased=true",
            $"Recovery Present {recoveryPresent}. Movement released.").ConfigureAwait(false);

        var commitTick = _tick + 1;
        var retryInput = await ReadCaseInputAsync("Manual retry after recovery").ConfigureAwait(false);
        await PressEditAsync(retryInput).ConfigureAwait(false);
        var committed = _published;
        var editPresent = renderer.Read().LastPresentedSequence;
        if (committed.TerrainRevision != initial.TerrainRevision + 1 ||
            CurrentNotice != $"Terrain changed (revision {committed.TerrainRevision})" ||
            editPresent <= recoveryPresent ||
            ReadOverlay().AsSpan().SequenceEqual(initialOverlay) ||
            !terrain.TryReadEdit(commitTick, 1, out var accepted, out var stamp) || accepted != VolumeEditStatus.Committed || stamp is null)
            throw new InvalidOperationException("The explicit terrain retry did not present its own committed edit.");
        await RecordCaseAsync("retry-presented", retryInput,
            $"receipt={accepted} receiptTick={stamp.Value.Tick} present={editPresent} overlayChanged=true",
            $"Commit confirmed by Present {editPresent}.").ConfigureAwait(false);

        await ConsumeTerrainProofPresentAsync(terrain, token).ConfigureAwait(false);
        var retainedRoot = ReadRoot();
        await VerifyRejectedAsync(target, TerrainPublicationOutcome.Stale, stale).ConfigureAwait(false);
        var outside = target with { Center = target.Center with { X = target.Center.X + 1024 }, Revision = committed.TerrainRevision };
        await VerifyRejectedAsync(outside, TerrainPublicationOutcome.Rejected, rejected).ConfigureAwait(false);

        async Task<InputSnapshot> ReadCaseInputAsync(string scenario)
        {
            _notice = null;
            if (keyboardOverlay is null)
                return new InputSnapshot(++sequence,
                    [new(InputEventKind.Pressed, InputDevice.Keyboard, key, sequence, 0, default, 0)], [], null, false);

            window.Capture();
            keyboardOverlay.Menu = null;
            keyboardOverlay.Performance = $"Terrain outcome proof · {scenario}\nFocus the game and press {key}.";
            diagnostics?.Write("terrain-edit-outcome-ready", $"key={key} case={scenario}");
            var input = await ReadFocusedProofKeyAsync(key, token).ConfigureAwait(false);
            var keySequence = input.Events[0].Sequence;
            if (keySequence <= lastKeyboardSequence)
                throw new InvalidOperationException("The terrain outcome proof needs a fresh key event for each case.");
            lastKeyboardSequence = keySequence;
            return input;
        }

        async Task PressEditAsync(InputSnapshot input)
        {
            if (!await HandleWorldCommandsAsync(terrain, renderer, player, input, token).ConfigureAwait(false))
                throw new InvalidOperationException($"The terrain outcome proof's {key} input was not consumed.");
        }

        async Task VerifyRejectedAsync(TerrainEditAim requested, TerrainPublicationOutcome expected, string message)
        {
            // A fresh key submits the recorded or deliberately out-of-coverage target instead of re-aiming.
            var input = await ReadCaseInputAsync(expected.ToString()).ConfigureAwait(false);
            var outcome = await ApplyTerrainEditAsync(terrain, renderer, player, requested, operation, null, token)
                .ConfigureAwait(false);
            if (outcome != expected || CurrentNotice != message || _published != committed || !ReferenceEquals(ReadRoot(), retainedRoot))
                throw new InvalidOperationException($"Terrain outcome proof expected {expected}, got {outcome}: {CurrentNotice}");
            await RecordCaseAsync(expected.ToString(), input, "rootUnchanged=true").ConfigureAwait(false);
        }

        async Task RecordCaseAsync(string scenario, InputSnapshot input, string evidence, string? detail = null)
        {
            if (CaveOre != ore || !ReferenceEquals(slot.Checkpoint, checkpoint))
                throw new InvalidOperationException($"The terrain outcome proof changed the saved checkpoint or ore ({scenario}).");
            var keyboardEvidence = keyboardOverlay is null ? "" : $" key={key} input=windows focused=true keySequence={input.Events[0].Sequence}";
            diagnostics?.Write("terrain-edit-outcome-proof",
                $"case={scenario} revision={_published.TerrainRevision} ore={ore} checkpoint={checkpoint.Generation} {evidence}{keyboardEvidence} notice={CurrentNotice}");
            if (keyboardOverlay is not null)
            {
                keyboardOverlay.Performance = $"Terrain outcome proof · {scenario}\n{key} received with game focus. Enter to continue." +
                    (detail is null ? "" : $"\n{detail}");
                await ReadFocusedProofKeyAsync(InputControl.Enter, token).ConfigureAwait(false);
            }
        }

        VolumeWorldRoot ReadRoot() => _ports.Committed.Read().Root as VolumeWorldRoot
            ?? throw new InvalidOperationException("The terrain outcome proof has no committed terrain root.");

        TerrainEditAim FindEditTarget()
        {
            var blockers = operation == VolumeBrushOperation.Add && ReadInventory()?.Construction is { Blocks.IsEmpty: false } buildings
                ? Collision(buildings) : null;
            for (var direction = 0; direction < 12; direction++)
            {
                _yaw = direction * MathF.Tau / 12;
                foreach (var pitch in new[] { -0.15f, -0.25f, -0.4f, -0.55f, 0f, 0.2f })
                {
                    token.ThrowIfCancellationRequested();
                    _pitch = pitch;
                    var aim = AimTerrainEdit(terrain, player, operation);
                    if (aim.Status == TerrainEditAimStatus.Target &&
                        blockers?.Intersects(ConstructionCollision.TerrainEditBox(aim.Center, Descriptor.CellSizeMeters)) != true &&
                        CanEditWithoutOre(aim.Center))
                        return aim;
                }
            }
            throw new InvalidOperationException("The terrain outcome proof could not find a visible target without an ore deposit.");
        }

        bool CanEditWithoutOre(LatticeCoordinate center)
        {
            if (operation == VolumeBrushOperation.Add || cave is null)
                return true;
            using var lease = terrain.TryRead();
            return lease is not null && InventoryOrePreview.TryCount(lease, cave.Field, center, out var count) && count == 0;
        }

        byte[] ReadOverlay()
        {
            using var lease = terrain.TryRead();
            if (lease is null || lease.Info != _published)
                throw new InvalidOperationException("The terrain outcome proof has no matching published lease.");
            var bytes = new byte[lease.EditOverlayBytes];
            if (!lease.TryWriteEditOverlay(bytes, out var written) || written != bytes.Length)
                throw new InvalidOperationException("The terrain outcome proof could not read the edit overlay.");
            return bytes;
        }
    }

    private async Task ConsumeTerrainProofPresentAsync(VolumeStreamingCoordinator terrain, CancellationToken token)
    {
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
        if (terrain.Statistics.MovementHeld)
            throw new InvalidOperationException("The terrain feedback proof could not consume the terrain presentation.");
    }

    private async Task<InputSnapshot> ReadFocusedProofKeyAsync(InputControl key, CancellationToken token)
    {
        using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        while (await cadence.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            var input = window.Capture();
            if (input.TransitionLoss || window.Read() is not { Phase: HostWindowPhase.Open, Focused: true } ||
                input.Events.Any(static entry => entry.Kind == InputEventKind.FocusCleared))
                continue;
            if (PrototypeMenu.Pressed(input, InputControl.Escape))
                throw new InvalidOperationException("The terrain feedback keyboard proof was canceled.");
            foreach (var entry in input.Events)
            {
                if (entry.Kind == InputEventKind.Pressed && entry.Device == InputDevice.Keyboard && entry.Control == key)
                    return input with { Events = [entry] };
            }
        }
        throw new InvalidOperationException("The terrain feedback keyboard proof ended without the expected key.");
    }
}
