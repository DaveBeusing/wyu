using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Platform.Windows;
using Groundwork.Persistence;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed class PlanetLoadException(string message) : InvalidOperationException(message);

internal sealed partial class VolumeExploration(WorldSafetyPorts world, ReferencePlanet? reference, WindowsHostModule window,
    AlphaOverlay? overlay, PrototypeMouseLook? mouse, VolumeWorldSlot? slot, AlphaDiagnostics? diagnostics)
{
    private long MeshBytes => cave is null ? 10 * 1024 * 1024 : 32 * 1024 * 1024;
    private long NativeBytes => cave is null ? 16 * 1024 * 1024 : 32 * 1024 * 1024;
    private const int RouteSteps = 480;
    private const int OverlayLimit = 8192;
    private static readonly PlanetVector East = new(1, 0, 0);
    private static readonly Vector4 Sky = new(0.18f, 0.28f, 0.42f, 1);
    private readonly WorldPublicationPorts _ports = world.Publication;
    private ulong _tick = slot?.Checkpoint?.Tick ?? 0;
    private ulong _sequence;
    private float _yaw, _pitch = -0.2f;
    private bool _debug;
    private VolumeStreamingSelection? _selection;
    private VolumePublicationInfo _published;
    private int _transitions, _views, _windows, _fineBricks;

    private CavePlanet? cave => slot?.Cave;
    private ReferencePlanet planet => reference ?? throw new InvalidOperationException("This world uses cave geology.");
    private PlanetDescriptor Descriptor => cave?.Descriptor ?? planet.Descriptor;
    private IPlanetBrickSource Field => (IPlanetBrickSource?)cave?.Field ?? planet.Field;
    private string PlanetName => cave is null ? ReferencePlanet.DisplayName : CavePlanet.Name;

    internal VolumeStreamingCoordinator CreateTerrain()
    {
        var unit = Charge(SelectTerrain(cave is null ? new(16, ReferencePlanet.RadiusMeters, 16) : CavePlanet.SurfaceProbe, East));
        var baseline = unit.Retained + unit.Retained;
        baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 16, CpuBytes = baseline.CpuBytes + 32L * OverlayLimit };
        var movement = diagnostics?.LimitPlanetStreaming == true ? unit with { CpuBytes = unit.CpuBytes - 1 } : unit;
        var limits = new VolumeStreamingLimits(baseline + unit + unit + unit + unit,
            baseline, movement, unit, unit, unit, 12, 12, 256);
        return slot?.Checkpoint is { } checkpoint
            ? checkpoint.RestoreTerrain(world, Field, limits, OverlayLimit,
                cave is null ? VolumeWorldSlot.SupportsMaterial : CavePlanet.SupportsMaterial)
            : new VolumeStreamingCoordinator(world, Field, limits, OverlayLimit);
    }

    internal static async Task<ulong?> ChooseSeedAsync(WindowsHostModule window, AlphaOverlay overlay,
        CancellationToken token)
    {
        var seed = "";
        string? error = null;
        while (true)
        {
            overlay.Menu = $"WYU\n\nNew World\n{ReferencePlanet.DisplayName}\n\nSeed\n{(seed.Length == 0 ? "Random" : seed)}\n\nType up to 20 digits\nEnter to explore\nBackspace to edit\nEsc to exit\n\n{error}";
            await Task.Delay(16, token).ConfigureAwait(false);
            foreach (var item in window.Capture().Events)
            {
                if (item.Kind != InputEventKind.Pressed)
                    continue;
                var key = item.Control;
                if (key == InputControl.Escape)
                    return null;
                if (key == InputControl.Backspace && seed.Length != 0)
                    seed = seed[..^1];
                var digit = key is >= InputControl.Digit0 and <= InputControl.Digit9 ? key - InputControl.Digit0 :
                    key is >= InputControl.Numpad0 and <= InputControl.Numpad9 ? key - InputControl.Numpad0 : -1;
                if (digit >= 0 && seed.Length < 20)
                    seed += (char)('0' + digit);
                if (key is not (InputControl.Enter or InputControl.NumpadEnter))
                    continue;
                if (seed.Length == 0)
                    return BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));
                if (ulong.TryParse(seed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
                error = "Seed must be between 0 and 18446744073709551615.";
            }
        }
    }

    private async Task<PlanetWalkState> OpenTerrainAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        CancellationToken token)
    {
        if (slot?.Checkpoint?.Player is { } saved)
        {
            // Keep both the avatar and the nearby saved landing footprint in the fine support halo.
            var landing = (_ports.Committed.Read().Root as GameHost.ExplorationRoot)?.Landing;
            var center = landing is { } landed ? new PlanetVector(
                (saved.Position.X + landed.Foot.X) / 2, (saved.Position.Y + landed.Foot.Y) / 2,
                (saved.Position.Z + landed.Foot.Z) / 2) : (PlanetVector?)null;
            await LoadAsync(terrain, renderer, saved.Position, saved.Heading, PlanetLocalFrame.Create(saved.Position), token,
                coverageCenter: center, supportCells: landing.HasValue ? 12 : 8)
                .ConfigureAwait(false);
            if (_ports.Committed.Read().Root is not VolumeWorldRoot root || root.Player != saved)
                throw new InvalidDataException("The saved player pose could not be restored.");
            return saved;
        }

        if (cave is not null)
            return await OpenCaveTerrainAsync(terrain, renderer, token).ConfigureAwait(false);

        foreach (var candidate in planet.FindSpawnCandidates())
        {
            await LoadAsync(terrain, renderer, candidate, East, PlanetLocalFrame.Create(candidate), token).ConfigureAwait(false);
            if (planet.TryConfirmSpawn(terrain, candidate, out var site) && site.Terrain == _published)
                return new PlanetWalkState(site.Foot, East, default);
        }
        throw new InvalidOperationException("The seed has no collision-verified spawn.");
    }

    internal async Task ExploreAsync(VolumeStreamingCoordinator terrain, ProceduralRenderHost renderHost, bool scripted,
        CancellationToken token)
    {
        var state = await OpenTerrainAsync(terrain, renderHost.Active, token).ConfigureAwait(false);
        diagnostics?.Enter("Exploration");
        diagnostics?.Write(slot?.Checkpoint is null ? "spawn" : "continue", FormattableString.Invariant(
            $"seed={Descriptor.Seed} generator={Descriptor.GeneratorVersion} revision={_published.TerrainRevision} generation={slot?.Checkpoint?.Generation ?? 0}"));
        if (slot?.Checkpoint is { } checkpoint && ProceduralLanding.Read(checkpoint, out var landed) == SaveGameValidation.Valid &&
            landed is { } restored)
        {
            ConfirmRestoredLanding(terrain, restored);
            diagnostics?.Write("touchdown-restored", FormattableString.Invariant($"flightTick={restored.FlightTick} revision={restored.Revision}"));
            Notice(slot.ReadOnly ? "Verified touchdown opened read-only." : "Continued from a verified touchdown.");
        }
        else if (slot?.ReadOnly == true)
            Notice("Recovered checkpoint opened read-only.");
        else if (cave is not null)
            Notice("WASD to explore | E mine | Q fill\nF5 save | F10 save and quit");
        else if (slot is not null)
            Notice("E remove | Q add | 1/2 material\nF5 save | F6 flight | F10 save and quit");
        var start = state.Position;
        var startBrick = Descriptor.SampleAt(start).Vertex.Locate().Brick;
        var nextBrick = startBrick with { Z = startBrick.Z + 1 };
        if (scripted)
        {
            if (StrideAt(_selection!, startBrick) != 1 || StrideAt(_selection!, nextBrick) != 2 || _transitions == 0)
                throw new InvalidOperationException("The scripted route must start beside a generated 2:1 boundary.");
            WriteCheckpoint("spawn", state);
        }
        var steps = 0;
        var exercisedWindow = false;
        var previousFrame = Stopwatch.GetTimestamp();
        using var cadence = new PeriodicTimer(TimeSpan.FromSeconds(1d / 60));
        while (await cadence.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            var input = window.Capture();
            var renderer = renderHost.Active;
            if (!scripted && PrototypeMenu.Pressed(input, InputControl.Escape))
                return;
            if (PrototypeMenu.Pressed(input, InputControl.F1))
                _debug = !_debug;
            var rendered = renderer.Read();
            if (!scripted && rendered is { Lifecycle: RendererLifecycle.Stopping or RendererLifecycle.Stopped,
                FirstFault: null, FirstCleanupFault: null } &&
                (token.IsCancellationRequested || window.Read().Phase is HostWindowPhase.Closing or HostWindowPhase.Closed))
                return;
            var drawable = RequireHealthyRenderer(rendered).Surface == RenderSurfaceState.Drawable;
            var focused = window.Read().Focused && !input.TransitionLoss;
            mouse?.Update(drawable && focused, ref _yaw, ref _pitch);
            if (!drawable || (!scripted && !focused))
                continue;

            var now = Stopwatch.GetTimestamp();
            var frameTime = Stopwatch.GetElapsedTime(previousFrame, now);
            previousFrame = now;
            if (scripted && steps == 120 && !exercisedWindow)
            {
                await VerifyWindowHoldAsync(renderer, token).ConfigureAwait(false);
                exercisedWindow = true;
            }
            var next = terrain.View.Frame;
            if (next.TryRebase(state.Position, out var rebased))
            {
                await LoadAsync(terrain, renderer, state.Position, state.Heading, rebased, token).ConfigureAwait(false);
                // A new tick must capture focus, surface and keys after the potentially long load.
                continue;
            }
            if (!scripted && _caveExitWaypoint < 0 && await HandleWorldCommandsAsync(terrain, renderer, state, input, token).ConfigureAwait(false))
            {
                if (_quitAfterSave)
                    return;
                continue;
            }
            if (cave is null && !scripted && PrototypeMenu.Pressed(input, InputControl.F6))
            {
                mouse?.Update(false, ref _yaw, ref _pitch);
                var result = await FlyAsync(terrain, renderHost, state, null, false, token).ConfigureAwait(false);
                if (result.Quit)
                    return;
                state = result.Player;
                continue;
            }

            if (cave is not null && _caveExitWaypoint < 0 && AtCaveExitRope(state) &&
                PrototypeMenu.Pressed(input, InputControl.Space))
                _caveExitWaypoint = 0;
            var climb = _caveExitWaypoint >= 0 ? NextCaveClimb(state) : null;
            var forward = scripted ? 0 : Axis(input, InputControl.W, InputControl.S);
            var right = scripted ? 1 : Axis(input, InputControl.D, InputControl.A);
            var angle = scripted ? 0 : _yaw;
            var x = Math.Cos(angle) * forward - Math.Sin(angle) * right;
            var z = Math.Sin(angle) * forward + Math.Cos(angle) * right;
            var divisor = Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(z)));
            var begin = await BeginAsync(token).ConfigureAwait(false);
            terrain.BeginTick(begin);
            VolumeQueryStatus status;
            VolumeMovementDraft? draft;
            if (climb is { } displacement)
                draft = terrain.TryPlanCaveClimb(state, displacement, out status, CaveQueries);
            else
                draft = cave is null
                    ? terrain.TryPlanMovement(state, x / divisor, z / divisor, out status)
                    : PlanCaveMovement(terrain, state, x / divisor, z / divisor, out status);

            var commit = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
            var prepared = draft is null ? null : terrain.TryPrepareMovement(draft, commit, []);
            var outcome = Finish(commit, prepared);
            if (climb is not null && (draft?.Result.Status != PlanetWalkStatus.Moved || outcome.Status != BarrierStatus.Committed))
            {
                _caveExitWaypoint = -1;
                Notice(status == VolumeQueryStatus.Contact ? "Exit rope blocked by terrain." : $"Exit rope unavailable ({status}).");
            }
            if (prepared is not null && outcome.Status == BarrierStatus.Committed)
            {
                state = draft!.Result.State;
                steps++;
            }
            else if (scripted)
                throw new InvalidOperationException($"Scripted movement held at step {steps} ({status}).");

            // The scripted look sweep changes the camera independently of the fixed traversal input.
            if (scripted)
                _yaw = steps is >= 60 and <= 80 ? (steps - 60) * (MathF.Tau / 20) : 0;
            var camera = Camera(terrain.View, state.Position, state.Heading);
            var frame = ++_sequence;
            RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, frame, camera));
            Drain(terrain);
            if (overlay is not null)
            {
                overlay.Menu = CurrentNotice ?? (prepared is null ? $"Movement held\n{status}" : cave is null ? null : CaveHint(terrain, state));
                var statistics = terrain.Statistics;
                overlay.Performance = _debug ? FormattableString.Invariant(
                    $"Seed {Descriptor.Seed} / gen {Descriptor.GeneratorVersion}\n{_selection!.MeshCount} regions / LOD 1/2 {_fineBricks}/{_selection.Requests.Length - _fineBricks}\nCPU {statistics.Used.CpuBytes / 1048576d:F1} / GPU {statistics.Used.NativeBytes / 1048576d:F1} MiB\nFrame {frameTime.TotalMilliseconds:F1} ms / {status}\nX {state.Position.X:F1} Y {state.Position.Y:F1} Z {state.Position.Z:F1} / origin {_published.View.Frame.Revision}") : null;
            }
            if (scripted && steps % 240 == 0)
                WriteCheckpoint("route", state);
            if (scripted && steps == RouteSteps)
            {
                await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= frame, token).ConfigureAwait(false);
                if (state.Position.Z - start.Z < 28 || _views < 3 || _windows < 2 || _transitions == 0 ||
                    Descriptor.SampleAt(state.Position).Vertex.Locate().Brick != nextBrick ||
                    StrideAt(_selection!, nextBrick) != 1)
                    throw new InvalidOperationException($"Route did not reach expected coverage ({state.Position.Z - start.Z:F1} m, {_views} views, {_windows} windows, {_transitions} transitions).");
                Console.WriteLine($"WP11 route completed: {steps} ticks, {_views} views, {_windows} windows, {_transitions} seam triangles; destination promoted to LOD 1.");
                return;
            }
        }
    }

    private async Task<bool> LoadAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer, PlanetVector position,
        PlanetVector heading, PlanetLocalFrame frame, CancellationToken token, VolumeBrushIntent? edit = null,
        PlanetVector? coverageCenter = null, PlanetVector? flightCamera = null, int supportCells = 8)
    {
        // Spawn search and candidate fallback share the same monotonic origin sequence as movement.
        if (frame.Revision <= terrain.View.Frame.Revision)
        {
            var prior = terrain.View.Frame;
            if (!prior.TryRebase(position, out frame))
                frame = prior;
        }
        var view = terrain.ChangeView(frame, terrain.View.RendererEpoch);
        if (!terrain.Statistics.MovementHeld)
            throw new InvalidOperationException("Movement remained open across a terrain view change.");
        var mask = ++_sequence;
        if (overlay is not null)
            overlay.Menu = $"Loading planet terrain\n\n{PlanetName}\nSeed {Descriptor.Seed}\nGenerator {Descriptor.GeneratorVersion}\n\nWaiting for safe collision and presentation";
        RequireFrame(renderer.TrySubmitVolumeMask(view, mask, Sky));
        var selection = SelectTerrain(coverageCenter ?? position, heading, edit, supportCells);
        if (_selection is null || selection.Minimum != _selection.Minimum)
            _windows++;
        var revision = _published.TerrainRevision;
        var targetRevision = revision == 0 ? slot?.Checkpoint?.TerrainRevision ?? 1 : revision;
        VolumeStreamingWork? staged;
        var result = edit is { } brush
            ? terrain.TryStageEdit(brush.Tick, brush.Id, selection, MeshBytes, Charge(selection), out staged)
            : terrain.TryStage(VolumeWorkClass.Movement, selection, revision, targetRevision, MeshBytes,
                Charge(selection), Field, out staged);
        if (result != VolumeAdmission.Accepted || staged is null)
        {
            if (edit is not null)
                return false;
            throw new PlanetLoadException(result == VolumeAdmission.Backpressure
                ? "The terrain streaming budget is full. The planet could not load. Close the game and try again."
                : $"The planet could not load terrain ({result}). Close the game and try again.");
        }
        using var work = staged;
        if (!await Task.Run(() => work.Execute(token), token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (edit is not null)
                return false;
            throw new InvalidOperationException("Terrain generation did not complete.");
        }
        await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= mask, token,
            pauseWhileMinimized: true).ConfigureAwait(false);
        var upload = renderer.TryStageVolumeTerrain(work.Generation);
        if (edit is not null && upload is MeshReplacementSubmissionResult.Backpressure or MeshReplacementSubmissionResult.InvalidRegion)
            return false;
        RequireMesh(upload);
        await WaitAsync(renderer, () => renderer.IsVolumeTerrainReady(work.Generation), token).ConfigureAwait(false);

        await BeginAsync(token).ConfigureAwait(false);
        var commit = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        var prepared = renderer.TryPrepareVolumePublication(work.Generation, commit, [], out var preparation);
        if (prepared is null)
        {
            if (edit is not null)
            {
                Finish(commit);
                return false;
            }
            throw new InvalidOperationException($"Terrain publication was rejected ({preparation}).");
        }
        if (Finish(commit, prepared).Status != BarrierStatus.Committed)
            throw new InvalidOperationException("Terrain publication did not commit.");
        using (var lease = terrain.TryRead() ?? throw new InvalidOperationException("Published terrain unavailable."))
        {
            _published = lease.Info;
            _transitions = CountTransitions(lease);
        }
        var sequence = ++_sequence;
        var camera = flightCamera is { } craft ? FlightCamera(view, craft, heading) : Camera(view, position, heading);
        RequireMesh(renderer.TrySubmitVolumeTerrain(work.Generation, sequence, camera));
        await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token,
            pauseWhileMinimized: true).ConfigureAwait(false);
        if (!terrain.Statistics.MovementHeld)
            throw new InvalidOperationException("Movement reopened before the next BeginTick.");
        _selection = selection;
        _fineBricks = CountFineBricks(selection);
        _views++;
        Drain(terrain);
        return true;
    }

    private static int CountFineBricks(VolumeStreamingSelection selection)
    {
        var count = 0;
        foreach (var request in selection.Requests)
            if (request.Stride == 1)
                count++;
        return count;
    }

    private static int StrideAt(VolumeStreamingSelection selection, LatticeBrick brick)
    {
        foreach (var request in selection.Requests)
            if (request.Brick == brick)
                return request.Stride;
        return 0;
    }

    private async Task VerifyWindowHoldAsync(D3D12Renderer renderer, CancellationToken token)
    {
        var before = _ports.Committed.Read().Stamp;
        DesktopSmoke.SetMinimized(true);
        try
        {
            await WaitAsync(renderer, () => window.Read().Phase == HostWindowPhase.Minimized &&
                renderer.Read().Surface != RenderSurfaceState.Drawable, token).ConfigureAwait(false);
            await Task.Delay(120, token).ConfigureAwait(false);
            if (_ports.Committed.Read().Stamp != before)
                throw new InvalidOperationException("Simulation advanced while the smoke window was minimized.");
        }
        finally
        {
            DesktopSmoke.SetMinimized(false);
        }
        await WaitAsync(renderer, () => window.Read().Phase == HostWindowPhase.Open &&
            renderer.Read().Surface == RenderSurfaceState.Drawable, token).ConfigureAwait(false);
        Console.WriteLine("WP11 minimized window held simulation; drawable surface restored.");
    }

    private static int CountTransitions(VolumeStreamingLease lease)
    {
        var count = 0;
        foreach (var mesh in lease.Meshes)
        foreach (var triangle in mesh.Triangles)
            if (triangle.IsTransition)
                count++;
        return count;
    }

    private VolumeResources Charge(VolumeStreamingSelection selection) =>
        new(VolumeStreamingCoordinator.RequiredCpuBytes(selection, MeshBytes) + NativeBytes + 64L * OverlayLimit + 4096,
            NativeBytes, NativeBytes, NativeBytes, 1, 1, 1, 64, 1);

    private async Task<BarrierStamp> BeginAsync(CancellationToken token)
    {
        _tick++;
        var candidates = await SealAsync(BarrierKind.BeginTick, token).ConfigureAwait(false);
        return Finish(candidates).Stamp ?? throw new InvalidOperationException("Missing BeginTick stamp.");
    }

    private async Task<CandidateSet> SealAsync(BarrierKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = new BarrierRequest(_ports.Committed.Read().Stamp, _tick, kind);
        var drain = await _ports.Borrow.CloseAndDrainAsync(request, world.CreateBorrowedDrainDeadline()).ConfigureAwait(false);
        if (drain != BorrowDrainStatus.Drained)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"Terrain borrow drain failed ({drain}).");
        }
        return _ports.Candidates.SealCandidates(request).Candidates ??
            throw new InvalidOperationException($"Unable to seal {kind} candidates.");
    }

    private BarrierOutcome Finish(CandidateSet candidates, PreparedBarrierPublication? prepared = null)
    {
        var pending = world.State.Read().PendingFaultGeneration;
        prepared ??= _ports.Preparation.TryPrepare(new BarrierPublicationDraft(candidates,
            _ports.Committed.Read().Root, pending, [])).Publication
            ?? throw new InvalidOperationException("Could not prepare the empty terrain barrier.");
        var result = _ports.Finalization.TryFinalize(prepared);
        if (result.Stamp is not { } stamp)
            throw new InvalidOperationException($"Terrain barrier failed ({result.Status}).");
        if (stamp.Kind == BarrierKind.CommitResults &&
            _ports.Observer.TryPublish(new ObserverPublicationDraft(stamp, pending, new ExplorationObserver(stamp.World))) != ObserverPublication.Published)
            throw new InvalidOperationException("Could not publish the terrain observer.");
        return result;
    }

    private VolumeCamera Camera(VolumeViewIdentity view, PlanetVector foot, PlanetVector heading)
    {
        var up = Vector3.Normalize(new((float)foot.X, (float)foot.Y, (float)foot.Z));
        var tangent = new Vector3((float)heading.X, (float)heading.Y, (float)heading.Z);
        tangent = Vector3.Normalize(tangent - up * Vector3.Dot(up, tangent));
        var direction = MathF.Cos(_yaw) * tangent + MathF.Sin(_yaw) * Vector3.Cross(tangent, up);
        direction = MathF.Cos(_pitch) * direction + MathF.Sin(_pitch) * up;
        return new(view, new(foot.X + up.X * 1.65, foot.Y + up.Y * 1.65, foot.Z + up.Z * 1.65),
            direction, up, 1.0f, 0.08f, 150, Vector4.One, Sky);
    }

    private void WriteCheckpoint(string label, PlanetWalkState state) =>
        Console.WriteLine(FormattableString.Invariant(
            $"WP11 {label} seed={Descriptor.Seed} tick={_tick} foot={state.Position.X:F5},{state.Position.Y:F5},{state.Position.Z:F5} revision={_published.TerrainRevision} origin={_published.View.Frame.Revision}"));

    private static int Axis(InputSnapshot input, InputControl positive, InputControl negative) =>
        (input.HeldControls.Contains(positive) ? 1 : 0) - (input.HeldControls.Contains(negative) ? 1 : 0);

    private static void Drain(VolumeStreamingCoordinator terrain)
    {
        while (terrain.TryReadEvent(out _)) { }
    }

    private async Task WaitAsync(D3D12Renderer renderer, Func<bool> ready, CancellationToken token,
        bool pauseWhileMinimized = false)
    {
        var remaining = TimeSpan.FromSeconds(30);
        while (!ready())
        {
            ReadHealthyRenderer(renderer);
            var started = Stopwatch.GetTimestamp();
            var paused = pauseWhileMinimized && window.Read().Phase == HostWindowPhase.Minimized;
            await Task.Delay(8, token).ConfigureAwait(false);
            if (!paused)
                remaining -= Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero && !ready())
                throw new TimeoutException("Timed out waiting for terrain presentation or GPU preparation.");
        }
    }

    private static RendererSnapshot ReadHealthyRenderer(D3D12Renderer renderer) => RequireHealthyRenderer(renderer.Read());

    private static RendererSnapshot RequireHealthyRenderer(RendererSnapshot state)
    {
        if (state.FirstFault is not null || state.Lifecycle != RendererLifecycle.Running)
            throw new InvalidOperationException($"Terrain renderer failed ({state}).");
        return state;
    }

    private static void RequireFrame(RenderSubmissionResult result)
    {
        if (result is not (RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced))
            throw new InvalidOperationException($"Terrain frame rejected ({result}).");
    }

    private static void RequireMesh(MeshReplacementSubmissionResult result)
    {
        if (result != MeshReplacementSubmissionResult.Accepted)
            throw new InvalidOperationException($"Terrain mesh rejected ({result}).");
    }

    private sealed record ExplorationObserver(WorldId World) : IWorldObserverState;
}
