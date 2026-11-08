using Groundwork.Core;
using Groundwork.Diagnostics;
using Groundwork.Ecs;
using Groundwork.Platform.Windows;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Simulation;
using System.Runtime.ExceptionServices;

namespace WorldsYetUnseen.Client;

internal enum HostMode { Prototype, PrototypeSmoke, D3D12, D3D12Smoke, Terrain, TerrainSmoke, Walkable, WalkableSmoke, Planet, PlanetSmoke, Flight, FlightSmoke, FlightReloadSmoke, SaveWorld, WindowSmoke, HeadlessSmoke, FaultSmoke }

internal static class GameHost
{
    private const long StorageBytes = 32 * 1024 * 1024;
    private static readonly ResourceQuota RuntimeQuota = new(1, 32,
        new JobQuota(8, 2, 8, 8, 4, 8, 16, 65536, 65536, 65536),
        new DiagnosticQuota(8, 65536, 24, 65536, 16, 4, 64, 1024));
    private static readonly ResourceQuota WorldQuota = RuntimeQuota with
    {
        Diagnostics = RuntimeQuota.Diagnostics with { EmergencyRecords = 21, EmergencyBytes = 61440 },
    };

    private static bool IsDesktopSmoke(HostMode mode) =>
        mode is HostMode.D3D12Smoke or HostMode.TerrainSmoke or HostMode.WalkableSmoke or HostMode.PrototypeSmoke or HostMode.PlanetSmoke ||
        IsFlightSmoke(mode);

    private static bool IsFlightSmoke(HostMode mode) => mode is HostMode.FlightSmoke or HostMode.FlightReloadSmoke;
    private static bool IsFlight(HostMode mode) => mode == HostMode.Flight || IsFlightSmoke(mode);

    private static bool IsTerrain(HostMode mode) => mode is HostMode.Terrain or HostMode.TerrainSmoke;

    internal static bool IsInteractive(HostMode mode) =>
        mode is HostMode.Prototype or HostMode.D3D12 or HostMode.Terrain or HostMode.Walkable or HostMode.Planet or HostMode.Flight or HostMode.SaveWorld;

    internal static async Task RunSaveSmokeAsync(string phase, string directory, string token, CancellationToken cancellationToken)
    {
        var clock = TimeProvider.System;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, RuntimeQuota));
        using var reservation = budget.TryReserveRuntime(RuntimeQuota) ??
            throw new InvalidOperationException("Could not reserve the WYU runtime budget.");
        var runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
        if (phase.StartsWith("--prototype-save-", StringComparison.Ordinal))
        {
            var prototype = new PrototypeSaveSmoke(runtime.RuntimeOwnership);
            try
            {
                await prototype.RunAsync(phase, directory, token, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await StopAsync(runtime, clock, null, null, null, new EcsStorageBudget(StorageBytes),
                    prototype.Scene, null, HostStopReason.ExternalCancellation, prototype.Save).ConfigureAwait(false);
            }
            return;
        }
        var smoke = new SaveResumeSmoke(runtime.RuntimeOwnership);
        try
        {
            await smoke.RunAsync(phase, directory, token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopAsync(runtime, clock, null, null, null, new EcsStorageBudget(StorageBytes),
                smoke.Scene, smoke.Save, HostStopReason.ExternalCancellation).ConfigureAwait(false);
        }
        Console.WriteLine(phase == "--save-smoke-init"
            ? "PASS: WYU committed the initial edited terrain."
            : "PASS: WYU restored committed terrain, position and inventory after interrupted save.");
    }

    internal static async Task RunAsync(HostMode mode, AlphaDiagnostics? diagnostics, string? saveDirectory,
        string? settingsDirectory, CancellationToken cancellationToken)
    {
        if (mode == HostMode.Prototype)
            settingsDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorldsYetUnseen", "Alpha");
        var (flightDirectory, restoreFocus) = await RunStageAsync(mode, saveDirectory, settingsDirectory,
            false, false, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (flightDirectory is not null && !cancellationToken.IsCancellationRequested)
            await RunStageAsync(HostMode.Flight, flightDirectory, settingsDirectory, true, restoreFocus, diagnostics, cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<(string? FlightDirectory, bool RestoreFocus)> RunStageAsync(HostMode mode, string? saveDirectory,
        string? settingsDirectory, bool alphaFlight, bool restoreFocus, AlphaDiagnostics? alphaDiagnostics,
        CancellationToken hostCancellation)
    {
        var smokeClock = mode is HostMode.HeadlessSmoke or HostMode.FaultSmoke ? new SmokeClock() : null;
        TimeProvider clock = smokeClock ?? TimeProvider.System;
        var timing = new RuntimeTimingLimits(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, RuntimeQuota));
        using var reservation = budget.TryReserveRuntime(RuntimeQuota) ??
            throw new InvalidOperationException("Could not reserve the WYU runtime budget.");
        var runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
        var context = new EngineModuleContext(clock, timing, runtime.Reporter, runtime.StopControl);
        var storage = new EcsStorageBudget(StorageBytes);
        EcsWorld? world = null;
        WindowsHostModule? window = null;
        D3D12Renderer? renderer = null;
        TerrainScene? terrain = IsTerrain(mode) ? new TerrainScene() : null;
        WorldSave? save = null;
        PrototypeSave? prototypeSave = null;
        AlphaOverlay? overlay = null;
        AlphaPerformance? performance = null;
        var settings = AlphaSettings.Defaults;
        FlightMission? flight = null;
        var startGame = true;
        var launchFlight = false;
        var handoffFocus = false;
        WalkableScene? walkable = mode switch
        {
            HostMode.Walkable or HostMode.WalkableSmoke => new WalkableScene(runtime.RuntimeOwnership),
            HostMode.PrototypeSmoke => new WalkableScene(runtime.RuntimeOwnership,
                PrototypeWorld.Spawn, snapshot: PrototypeWorld.Snapshot, bounds: PrototypeWorld.Recipe.Bounds,
                colorPrototype: true),
            _ => null
        };
        var stopReason = HostStopReason.StartupFault;
        Exception? failure = null;
        string? success = null;
        using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation);
        var cancellationToken = stageCancellation.Token;
        Task<bool> watchClose = Task.FromResult(false);

        try
        {
            using var wakeOnCancellation = cancellationToken.UnsafeRegister(
                static state => ((RuntimeWakeSignal)state!).Signal(), runtime.SimulationWake);
            if (mode == HostMode.SaveWorld)
            {
                saveDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorldsYetUnseen", "WP06");
                save = await WorldSave.OpenAsync(saveDirectory, runtime.RuntimeOwnership, cancellationToken).ConfigureAwait(false);
                walkable = new WalkableScene(runtime.RuntimeOwnership, save.Pose, save.Terrain.Span);
                if (save.Generation != 0)
                    Console.WriteLine($"Resumed save generation {save.Generation}.");
                if (save.Recovery is { } recovery)
                    Console.WriteLine($"Save recovery: {recovery.FileName} ({recovery.Reason}).");
            }
            world = EcsWorld.Open(runtime, WorldQuota, new BarrierSupervisorPolicy(WorldQuota.Diagnostics.RequiredEvidenceRecords),
                new ComponentRegistryBuilder().Build(), new EcsLimits(16, 1, 0, 1024 * 1024, StorageBytes), storage);
            if (smokeClock is null)
            {
                alphaDiagnostics?.Enter("WindowStartup");
                var title = IsDesktopSmoke(mode) ? DesktopSmoke.WindowTitle : "Worlds Yet Unseen";
                window = new WindowsHostModule(new WindowsHostOptions(title, 960, 540, 256),
                    runtime.UiWake, runtime.RuntimeOwnership);
                await window.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
                await window.StartAsync(cancellationToken).ConfigureAwait(false);
                if (IsInteractive(mode))
                    watchClose = WatchWindowCloseAsync(window, stageCancellation);
                if (mode == HostMode.Prototype || alphaFlight)
                {
                    overlay = new AlphaOverlay(restoreFocus);
                    performance = new AlphaPerformance(overlay);
                }
                if (alphaFlight && settingsDirectory is not null)
                    AlphaWindowSize.TryApply(new AlphaSettingsStore(settingsDirectory).Load().Value.Size);
                if (mode == HostMode.Prototype)
                {
                    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "WorldsYetUnseen");
                    saveDirectory ??= Path.Combine(data, "WP07");
                    alphaDiagnostics?.Enter("SaveLoad");
                    new PrototypeCaption().Update("WYU | Checking save slot");
                    prototypeSave = await PrototypeSave.OpenAsync(saveDirectory, runtime.RuntimeOwnership, cancellationToken,
                            beforeLoad: alphaDiagnostics?.BeforeSaveLoad)
                        .ConfigureAwait(false);
                    new PrototypeCaption().Update("Worlds Yet Unseen");
                    alphaDiagnostics?.Enter("StartMenu");
                    var startMenu = new AlphaStartMenu(window, prototypeSave, new AlphaSettingsStore(settingsDirectory!),
                        overlay!, performance!);
                    startGame = await startMenu.ChooseAsync(cancellationToken).ConfigureAwait(false);
                    overlay!.Menu = null;
                    settings = startMenu.Settings;
                    launchFlight = startGame && prototypeSave.Checkpoint is { Progress.BeaconOnline: true };
                    startGame &= !launchFlight;
                    if (startGame)
                    {
                        var checkpoint = prototypeSave.Checkpoint;
                        walkable = new WalkableScene(runtime.RuntimeOwnership, checkpoint?.Pose ?? PrototypeWorld.Spawn,
                            checkpoint is { } selected ? PrototypeWorld.EffectiveTerrain(selected.Progress) : [],
                            snapshot: PrototypeWorld.Snapshot, bounds: PrototypeWorld.Recipe.Bounds, colorPrototype: true);
                    }
                }
                if ((mode == HostMode.WindowSmoke || IsDesktopSmoke(mode)) &&
                    window.Read() is not { Phase: HostWindowPhase.Open, ClientWidth: > 0, ClientHeight: > 0 })
                    throw new InvalidOperationException("The Windows host did not open a usable window.");
                if (mode is HostMode.D3D12 or HostMode.Planet or HostMode.Flight || IsDesktopSmoke(mode) || terrain is not null || walkable is not null)
                {
                    alphaDiagnostics?.Enter(walkable is null ? "GraphicsStartup" : "RescueTerrainLoad");
                    var diagnostics = IsDesktopSmoke(mode)
                        ? new D3D12DiagnosticsOptions(EnableDebugLayer: true, EnableDred: true)
                        : default;
                    if (mode is HostMode.Planet or HostMode.PlanetSmoke || IsFlight(mode))
                    {
                        renderer = D3D12Renderer.CreateWithPlanetTerrain(window, runtime.RuntimeOwnership, diagnostics);
                    }
                    else if (walkable is not null)
                    {
                        var initial = await walkable.StartAsync(cancellationToken).ConfigureAwait(false);
                        alphaDiagnostics?.Enter("GraphicsStartup");
                        renderer = D3D12Renderer.CreateWithMeshRegionReplacement(window, runtime.RuntimeOwnership, initial, diagnostics);
                    }
                    else
                    {
                        renderer = terrain is not null
                            ? D3D12Renderer.CreateWithMeshReplacement(window, runtime.RuntimeOwnership, terrain.CreateMeshes(), diagnostics)
                            : new D3D12Renderer(diagnostics, window, runtime.RuntimeOwnership);
                    }
                    await renderer.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
                    alphaDiagnostics?.CheckGraphics();
                    await renderer.StartAsync(cancellationToken).ConfigureAwait(false);
                    if (walkable is not null)
                        walkable.Attach(renderer.MeshRegionReplacement
                            ?? throw new InvalidOperationException("The renderer did not expose its region port."));
                }
            }

            if (startGame)
            {
                stopReason = HostStopReason.RuntimeFault;
                IInputSource input = window is not null ? window : new SmokeInput();
                if (IsFlight(mode))
                {
                    alphaDiagnostics?.Enter("FlightSaveLoad");
                    flight = await FlightMission.OpenAsync(saveDirectory ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorldsYetUnseen", "WP09"),
                        runtime.RuntimeOwnership, IsFlightSmoke(mode), mode == HostMode.FlightReloadSmoke, cancellationToken)
                        .ConfigureAwait(false);
                }
                alphaDiagnostics?.Enter(mode == HostMode.Prototype ? "Rescue" : mode.ToString());
                if (alphaFlight)
                    flight!.EnterAlpha(alphaDiagnostics);
                (success, launchFlight) = await DriveAsync(mode, context, runtime, world, input, renderer, terrain, walkable, save,
                    prototypeSave, smokeClock, flight, settings, overlay, performance, hostCancellation).ConfigureAwait(false);
            }
            stopReason = mode == HostMode.FaultSmoke ? HostStopReason.RuntimeFault : HostStopReason.ExternalCancellation;
        }
        catch (OperationCanceledException) when (IsInteractive(mode) &&
            (cancellationToken.IsCancellationRequested || runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop)) &&
            runtime.Diagnostics.ReadEmergency().Primary is null)
        {
            stopReason = HostStopReason.ExternalCancellation;
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            handoffFocus = launchFlight && overlay?.AllowFocusHandoff() == true;
            overlay?.Dispose();
            await stageCancellation.CancelAsync().ConfigureAwait(false);
            if (await watchClose.ConfigureAwait(false) ||
                window?.Read().Phase is HostWindowPhase.Closing or HostWindowPhase.Closed)
            {
                launchFlight = false;
                alphaDiagnostics?.Write("window-close");
            }
        }

        try
        {
            await StopAsync(runtime, clock, renderer, window, world, storage, walkable, save, stopReason, prototypeSave)
                .ConfigureAwait(false);
        }
        catch (Exception cleanup) when (failure is not null)
        {
            throw new AggregateException(failure, cleanup);
        }
        finally
        {
            if (alphaDiagnostics is not null && renderer?.Read() is { } state &&
                (state.FirstFault is not null || state.FirstCleanupFault is not null))
            {
                alphaDiagnostics.MarkGraphicsFailure();
                var graphics = renderer.ReadDiagnostics();
                alphaDiagnostics.Write("graphics-state",
                    $"fault={state.FirstFault} cleanup={state.FirstCleanupFault} adapter={graphics.Adapter} deviceRemoval={graphics.DeviceRemoval}");
            }
            flight?.Dispose();
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        if (IsDesktopSmoke(mode))
            DesktopSmoke.VerifyShutdown(renderer!, requireDebugLayer: IsTerrain(mode) || IsFlightSmoke(mode) ||
                mode is HostMode.WalkableSmoke or HostMode.PrototypeSmoke or HostMode.PlanetSmoke,
                requireNvidia: mode == HostMode.PlanetSmoke || IsFlightSmoke(mode));
        if (success is not null)
            Console.WriteLine(success);
        return (launchFlight ? Path.Combine(saveDirectory!, $"flight-{prototypeSave!.Lineage:N}") : null, handoffFocus);
    }

    private static async Task<bool> WatchWindowCloseAsync(WindowsHostModule window, CancellationTokenSource stage)
    {
        while (!stage.IsCancellationRequested)
        {
            if (window.Read().Phase is HostWindowPhase.Closing or HostWindowPhase.Closed)
            {
                await stage.CancelAsync().ConfigureAwait(false);
                return true;
            }
            await Task.Delay(20, stage.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        return false;
    }

    private static async Task<(string? Success, bool LaunchFlight)> DriveAsync(HostMode mode, EngineModuleContext context, RuntimeSafetyAuthority runtime,
        EcsWorld world, IInputSource input, D3D12Renderer? renderer, TerrainScene? terrain,
        WalkableScene? walkable, WorldSave? save, PrototypeSave? prototypeSave, SmokeClock? smokeClock,
        FlightMission? flight, AlphaSettings settings, AlphaOverlay? overlay, AlphaPerformance? performance,
        CancellationToken cancellationToken)
    {
        var injectedFault = mode == HostMode.FaultSmoke ? new InvalidOperationException("Controlled tick failure.") : null;
        var route = mode == HostMode.WalkableSmoke ? new WalkableRouteSmoke() : null;
        var prototype = mode is HostMode.Prototype or HostMode.PrototypeSmoke
            ? new PrototypeSession(walkable!, prototypeSave?.Checkpoint?.Progress ?? default) : null;
        var menu = prototypeSave is null ? null : new PrototypeMenu(prototypeSave);
        var prototypeRoute = mode == HostMode.PrototypeSmoke ? new PrototypeRouteSmoke() : null;
        var planet = mode is HostMode.Planet or HostMode.PlanetSmoke ? new PlanetRoute(mode == HostMode.PlanetSmoke) : null;
        var caption = mode == HostMode.Prototype ? new PrototypeCaption() : null;
        using var mouseLook = mode == HostMode.Prototype && input is IHostWindowStateSource windowState
            ? new PrototypeMouseLook(windowState, settings) : null;
        var yaw = 0f;
        var pitch = -0.20f;
        var launchPending = false;
        var launchFailed = false;
        var launchFlight = false;
        var flightPaused = false;
        var flightExitSelected = false;
        var flightExitRequested = false;
        ulong routeCompletionTick = 0;
        var pipeline = new WorldTickPipeline(world, injectedFault, terrain is not null,
            flight is not null ? AdvanceFlight : planet is not null ? AdvancePlanet : walkable is null ? null : AdvanceWalkable,
            escapeCloses: prototype is null && overlay is null);
        var simulation = new FixedStepSimulation(context, input, pipeline, world.Safety.State, runtime.SimulationWake);
        var scene = renderer is null || planet is not null || flight is not null ? null : new SceneSubmission(renderer, terrain is not null,
            walkable is null ? null : () => walkable.GetScene(route?.CameraYaw ?? yaw, prototype is null ? null : pitch));
        var terrainSmoke = mode == HostMode.TerrainSmoke ? new TerrainEditSmoke() : null;
        var meshPort = renderer?.MeshReplacement;
        var edits = terrain is null ? null : new TerrainEdits(terrain,
            meshPort ?? throw new InvalidOperationException("The terrain renderer has no mesh replacement port."),
            runtime.RuntimeOwnership, terrainSmoke is null ? null : terrainSmoke.BeforeCompletion);
        var voxelRemoved = false;
        var nativeCloseSent = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // The coordinator lets an owned barrier settle before handling console cancellation.
                var iteration = await simulation.AdvanceAsync(CancellationToken.None).ConfigureAwait(false);
                if (iteration.State == SimulationState.Stopped || pipeline.CloseRequested ||
                    menu is { QuitRequested: true } || flightExitRequested)
                {
                    launchFlight = false;
                    break;
                }
                if (launchFlight)
                    break;
                if (caption is not null)
                {
                    caption.Update(PrototypeStatus());
                    overlay!.Menu = prototype!.Paused ? menu!.Caption : null;
                }
                performance?.Update(renderer?.Read());
                if (!nativeCloseSent && planet is not null)
                {
                    var rendered = renderer!.Read();
                    if (rendered.Surface == RenderSurfaceState.Drawable)
                        planet.Present(renderer, pipeline.PublishedTick);
                    if (mode == HostMode.PlanetSmoke && planet.Completed && planet.FinalCameraPresented(rendered))
                    {
                        VerifySmokePublication(world, pipeline, simulation);
                        DesktopSmoke.RequestNativeClose(rendered, "planet route");
                        nativeCloseSent = true;
                    }
                }
                if (!nativeCloseSent && flight is not null)
                {
                    var rendered = renderer!.Read();
                    if (await flight.PresentAsync(renderer, rendered, pipeline.PublishedTick, cancellationToken).ConfigureAwait(false))
                    {
                        VerifySmokePublication(world, pipeline, simulation);
                        DesktopSmoke.RequestNativeClose(rendered, "flight mission");
                        nativeCloseSent = true;
                    }
                }
                if (!nativeCloseSent && scene is not null)
                {
                    if (edits is not null)
                    {
                        // Apply published input outside the simulation writer and observer leases.
                        if (pipeline.TerrainVoxelRemoved != voxelRemoved)
                        {
                            edits.Toggle();
                            voxelRemoved = pipeline.TerrainVoxelRemoved;
                        }
                        edits.Advance();
                    }
                    // Keep the long diagnostic route within the D3D12 message budget.
                    // Simulation still advances on every tick and waits for the actual presented generation.
                    if (prototypeRoute is null || pipeline.PublishedTick % 2 == 0)
                        scene.Submit(context.Clock, iteration, pipeline.PublishedTick);
                    var rendered = renderer!.Read();
                    if (route is not null)
                        route.ObservePresentation(rendered, renderer.MeshRegionReplacement!.ReadMeshReplacement());

                    if (IsDesktopSmoke(mode) && pipeline.PublishedTick >= 3 &&
                        rendered is { LastPresentedSequence: > 0, Surface: RenderSurfaceState.Drawable } &&
                        (terrainSmoke is null || terrainSmoke.Advance(meshPort!, edits!)) &&
                        (route is null && prototypeRoute is null ||
                         routeCompletionTick > 0 && rendered.LastPresentedSequence >= routeCompletionTick))
                    {
                        VerifySmokePublication(world, pipeline, simulation);
                        DesktopSmoke.RequestNativeClose(rendered, prototypeRoute is not null ? "rescue beacon" :
                            route is not null ? "walkable region" : IsTerrain(mode) ? "terrain" : "cube");
                        nativeCloseSent = true;
                    }
                }
                if (mode == HostMode.WindowSmoke && iteration.CompletedTick >= 3)
                    break;
                if (smokeClock is not null)
                    smokeClock.AdvanceBaseStep();
                else
                    WaitForNextIteration(context.Clock, runtime.SimulationWake, iteration.NextWakeTimestamp);
            }
        }
        catch (InvalidOperationException failure) when (ReferenceEquals(failure, injectedFault))
        {
            if (pipeline.PublishedTick != 1 ||
                !runtime.State.Read().Conditions.HasFlag(SafetyCondition.TerminalStop) ||
                runtime.Diagnostics.ReadEmergency().Primary is not
                    { Classification: FaultClass.UnknownCritical, Subsystem: FaultSubsystem.Simulation, Context: { Tick: 2, Phase: TickPhase.Base } })
                throw new InvalidOperationException("The controlled fault did not fence the runtime at the expected tick.", failure);
            return ("PASS: WYU controlled fault shutdown.", false);
        }
        finally
        {
            edits?.Stop();
            planet?.Stop();
            terrainSmoke?.Release();
        }

        if (runtime.Diagnostics.ReadEmergency().Primary is { } fault)
            throw new InvalidOperationException($"Groundwork stopped after {fault.Subsystem} fault {fault.Classification} ({fault.Code}).");
        var runtimeConditions = runtime.State.Read().Conditions;
        var worldConditions = world.Safety.State.Read().Conditions;
        if ((worldConditions & SafetyCondition.RequiredEvidenceLost) != 0 ||
            worldConditions.HasFlag(SafetyCondition.TerminalStop) && !runtimeConditions.HasFlag(SafetyCondition.TerminalStop))
            throw new InvalidOperationException($"Groundwork stopped the world unexpectedly ({worldConditions}).");
        if (IsInteractive(mode))
            return (null, launchFlight);

        cancellationToken.ThrowIfCancellationRequested();
        if (IsDesktopSmoke(mode))
        {
            if (!nativeCloseSent || !runtimeConditions.HasFlag(SafetyCondition.TerminalStop))
                throw new InvalidOperationException("The D3D12 smoke did not present and observe native close.");
            return (mode switch
            {
                HostMode.TerrainSmoke => "PASS: WYU D3D12 terrain presentation and native shutdown.",
                HostMode.WalkableSmoke => "PASS: WYU walkable route and native shutdown.",
                HostMode.PrototypeSmoke => "PASS: WYU WP07 rescue beacon route and native shutdown.",
                HostMode.PlanetSmoke => "PASS: WYU WP08 planet route and native shutdown.",
                HostMode.FlightSmoke => "PASS: WYU WP09 flight mission and native shutdown.",
                HostMode.FlightReloadSmoke => "PASS: WYU WP09 landing checkpoint reload and native shutdown.",
                _ => "PASS: WYU D3D12 Windows startup and shutdown.",
            }, false);
        }

        if (mode == HostMode.HeadlessSmoke && (pipeline.PublishedTick != 3 || !pipeline.CloseRequested) || mode == HostMode.FaultSmoke)
            throw new InvalidOperationException("The WYU smoke did not complete the expected input sequence.");
        VerifySmokePublication(world, pipeline, simulation);
        return (mode == HostMode.WindowSmoke ? "PASS: WYU Windows startup and shutdown." : "PASS: WYU headless startup and shutdown.", false);

        string PrototypeStatus()
        {
            if (prototype!.Paused)
                return "Worlds Yet Unseen";
            if (!prototype.Progress.BeaconOnline)
                return prototype.Caption;
            if (launchPending || prototypeSave!.IsSaving)
                return "WYU | Saving rescue checkpoint";
            return launchFailed ? "WYU | Rescue save incomplete | Enter to retry"
                : "WYU | Rescue beacon online | Enter for guided flight | Esc pause";
        }

        void AdvancePlanet(TickContext tick) =>
            planet!.Tick(tick, renderer!.Read().Surface == RenderSurfaceState.Drawable);

        void AdvanceFlight(TickContext tick)
        {
            if (overlay is not null)
            {
                performance!.Handle(tick.Input);
                if (PrototypeMenu.Pressed(tick.Input, InputControl.Escape))
                {
                    flightPaused = !flightPaused;
                    flightExitSelected = false;
                }
                else if (flightPaused)
                {
                    if (PrototypeMenu.Pressed(tick.Input, InputControl.Up) ||
                        PrototypeMenu.Pressed(tick.Input, InputControl.Down))
                        flightExitSelected = !flightExitSelected;
                    if (PrototypeMenu.Pressed(tick.Input, InputControl.Enter))
                    {
                        flightExitRequested = flightExitSelected;
                        flightPaused = false;
                    }
                }
                overlay.Menu = flightPaused
                    ? flightExitSelected ? "WYU paused | Resume | > Exit | Esc resume"
                        : "WYU paused | > Resume | Exit | Esc resume"
                    : null;
                if (flightPaused || flightExitRequested)
                    return;
            }
            flight!.Tick(tick, renderer!.Read().Surface == RenderSurfaceState.Drawable);
        }

        void AdvanceWalkable(TickContext tick)
        {
            var drawable = renderer!.Read().Surface == RenderSurfaceState.Drawable;
            if (prototype is not null)
            {
                AdvancePrototype(tick, drawable);
                return;
            }
            var (x, z) = route is null && drawable ? ReadWalkableInput(tick.Input, ref yaw) : default;
            walkable!.Advance(x, z, tick.Tick, route, drawable);
            if (save is not null)
            {
                save.Advance();
                if (drawable && !tick.Input.TransitionLoss)
                {
                    if (tick.Input.Events.Any(static e => e is
                        { Kind: InputEventKind.Pressed, Device: InputDevice.Keyboard, Control: InputControl.T }))
                        walkable.TryToggleTerrain();
                    if (tick.Input.Events.Any(static e => e is
                        { Kind: InputEventKind.Pressed, Device: InputDevice.Keyboard, Control: InputControl.F5 })
                        && !save.RequestSave(walkable.Pose, walkable.Terrain))
                        Console.WriteLine("Save request could not be queued.");
                }
            }
            if (route is { Completed: true } && routeCompletionTick == 0)
                routeCompletionTick = tick.Tick;
        }

        void AdvancePrototype(TickContext tick, bool drawable)
        {
            var input = tick.Input;
            performance?.Handle(input);
            var x = 0;
            var z = 0;
            var interact = false;
            if (prototypeRoute is not null)
            {
                (x, z, yaw, pitch, interact) = prototypeRoute.NextInput(walkable!, prototype!.Progress,
                    drawable && prototype.Ready, prototype.Caption);
            }
            else
            {
                var playInput = drawable && (mouseLook?.Focused ?? true) && !prototype!.Paused && !input.TransitionLoss &&
                    !input.Events.Any(static e => e.Kind == InputEventKind.FocusCleared) &&
                    !PrototypeMenu.Pressed(input, InputControl.Escape);
                mouseLook?.Update(playInput, ref yaw, ref pitch);
                if (playInput)
                {
                    (x, z) = ReadWalkableInput(input, ref yaw);
                    var held = input.HeldControls;
                    var tilt = (held.Contains(InputControl.Up) ? 1 : 0) - (held.Contains(InputControl.Down) ? 1 : 0);
                    pitch = Math.Clamp(pitch + tilt * (MathF.PI / 90), -PrototypeMouseLook.MaximumPitch,
                        PrototypeMouseLook.MaximumPitch);
                    interact = PrototypeMenu.Pressed(input, InputControl.E);
                }
            }
            walkable!.Advance(x, z, tick.Tick, drawable: drawable);
            prototype!.Advance(yaw, pitch, interact, drawable, showObjective: caption is not null || prototypeRoute is not null);
            if (menu is not null)
            {
                prototypeSave!.Advance();
                var checkpoint = new PrototypeCheckpoint(walkable.Pose, prototype.Progress);
                if (launchPending && !prototypeSave.IsSaving)
                {
                    launchPending = false;
                    launchFlight = prototypeSave.Checkpoint is { Progress.BeaconOnline: true };
                    launchFailed = !launchFlight;
                }
                if (!prototype.Paused && prototype.Progress.BeaconOnline && !prototypeSave.IsSaving &&
                    PrototypeMenu.Pressed(input, InputControl.Enter))
                {
                    launchFlight = prototypeSave.Checkpoint is { Progress.BeaconOnline: true };
                    if (!launchFlight)
                    {
                        launchPending = prototype.Ready && prototypeSave.RequestSave(checkpoint);
                        launchFailed = !launchPending;
                    }
                }
                menu.Handle(input, prototype, checkpoint);
            }
            if (prototypeRoute is { Completed: true } && routeCompletionTick == 0)
                routeCompletionTick = tick.Tick;
        }
    }

    private static (int X, int Z) ReadWalkableInput(InputSnapshot input, ref float yaw)
    {
        if (input.TransitionLoss)
            return default;

        var held = input.HeldControls;
        var turn = Direction(held.Contains(InputControl.Right), held.Contains(InputControl.Left));
        yaw = MathF.IEEERemainder(yaw + turn * (MathF.PI / 90), MathF.Tau);
        var forward = Direction(held.Contains(InputControl.W), held.Contains(InputControl.S));
        var strafe = Direction(held.Contains(InputControl.D), held.Contains(InputControl.A));
        var speed = forward != 0 && strafe != 0 ? 128 / MathF.Sqrt(2) : 128;
        var (sin, cos) = MathF.SinCos(yaw);
        return ((int)MathF.Round(speed * (forward * cos - strafe * sin)),
            (int)MathF.Round(speed * (forward * sin + strafe * cos)));

        static int Direction(bool positive, bool negative) => (positive ? 1 : 0) - (negative ? 1 : 0);
    }

    private static void VerifySmokePublication(EcsWorld world, WorldTickPipeline pipeline, FixedStepSimulation simulation)
    {
        if (pipeline.PublishedTick < 3 || pipeline.GameplaySteps != pipeline.PublishedTick / 3 ||
            simulation.LastConfirmedBarrier is not { Kind: BarrierKind.CommitResults } barrier ||
            barrier.Tick != pipeline.PublishedTick || barrier.Epoch != 2 * barrier.Tick)
            throw new InvalidOperationException("The WYU smoke did not complete the expected simulation sequence.");

        var observer = world.Safety.Publication.ObserverSnapshots.TryAcquire();
        using var lease = observer.Lease;
        if (observer.Status != ObserverReadStatus.Acquired || lease?.Current.Stamp != barrier)
            throw new InvalidOperationException("The final observer frame does not match the last committed tick.");
    }

    private static void WaitForNextIteration(TimeProvider clock, RuntimeWakeSignal wake, long? targetTimestamp)
    {
        if (targetTimestamp is not long target)
        {
            wake.Handle.WaitOne();
            return;
        }

        var now = clock.GetTimestamp();
        if (target > now)
        {
            var remaining = clock.GetElapsedTime(now, target);
            wake.Handle.WaitOne((int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, int.MaxValue));
        }
    }

    private static async Task StopAsync(RuntimeSafetyAuthority runtime, TimeProvider clock, D3D12Renderer? renderer, WindowsHostModule? window,
        EcsWorld? world, EcsStorageBudget storage, WalkableScene? walkable, WorldSave? save, HostStopReason reason,
        PrototypeSave? prototypeSave = null)
    {
        walkable?.Stop();
        save?.Stop();
        prototypeSave?.Stop();
        var deadline = runtime.StopControl.RequestStop(reason);
        var failures = new List<Exception>();
        try
        {
            if (renderer is not null)
            {
                await renderer.DisposeAsync().ConfigureAwait(false);
                var status = renderer.Read();
                if (status is not { Lifecycle: RendererLifecycle.Stopped, FirstFault: null,
                    FirstCleanupFault: null, IncompleteCleanup: false })
                    throw new InvalidOperationException($"The D3D12 renderer stopped with invalid settlement ({status}).");
            }
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        try
        {
            if (window is not null)
            {
                await window.DisposeAsync().ConfigureAwait(false);
                if (window.Read().Phase != HostWindowPhase.Closed)
                    throw new InvalidOperationException("The Windows host did not release its window.");
            }
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        try
        {
            if (walkable is not null || save is not null || prototypeSave is not null)
            {
                using var workerDeadline = new CancellationTokenSource(deadline.GetRemainingForSettlement(clock));
                await Task.WhenAll(walkable?.SettleAsync(workerDeadline.Token) ?? Task.CompletedTask,
                    save?.SettleAsync(workerDeadline.Token) ?? Task.CompletedTask,
                    prototypeSave?.SettleAsync(workerDeadline.Token) ?? Task.CompletedTask).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        try
        {
            var settlement = runtime.SettleStop();
            while (settlement == ShutdownSettlement.Pending && deadline.GetRemainingForSettlement(clock) > TimeSpan.Zero)
            {
                await Task.Delay(5).ConfigureAwait(false);
                settlement = runtime.SettleStop();
            }
            if (settlement == ShutdownSettlement.Pending)
                settlement = runtime.SettleStop();
            if (settlement != ShutdownSettlement.Released || (world is not null && !world.TryReleaseStorage()) || storage.ReservedBytes != 0)
                throw new InvalidOperationException($"Groundwork shutdown did not release its resources ({settlement}).");
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }

    private sealed class SmokeClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 60000;
        public override long GetTimestamp() => _timestamp;
        internal void AdvanceBaseStep() => _timestamp += 1000;
    }

    private sealed class SmokeInput : IInputSource
    {
        private static readonly InputSnapshot Empty = new(0, [], [], null, false);
        private int _captures;

        public InputSnapshot Capture() => ++_captures == 3
            ? new InputSnapshot(1, [new InputEvent(InputEventKind.Pressed, InputDevice.Keyboard, InputControl.Escape, 1, 3000, default, 0)],
                [InputControl.Escape], null, false)
            : Empty;
    }
}
