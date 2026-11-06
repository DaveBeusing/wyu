using Groundwork.Core;
using Groundwork.Diagnostics;
using Groundwork.Ecs;
using Groundwork.Platform.Windows;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Simulation;
using System.Runtime.ExceptionServices;

namespace WorldsYetUnseen.Client;

internal enum HostMode { Interactive, D3D12, D3D12Smoke, Terrain, TerrainSmoke, Walkable, WalkableSmoke, WindowSmoke, HeadlessSmoke, FaultSmoke }

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

    private static bool IsDesktopSmoke(HostMode mode) => mode is HostMode.D3D12Smoke or HostMode.TerrainSmoke or HostMode.WalkableSmoke;

    private static bool IsTerrain(HostMode mode) => mode is HostMode.Terrain or HostMode.TerrainSmoke;

    private static bool IsInteractive(HostMode mode) => mode is HostMode.Interactive or HostMode.D3D12 or HostMode.Terrain or HostMode.Walkable;

    internal static async Task RunAsync(HostMode mode, CancellationToken cancellationToken)
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
        WalkableScene? walkable = mode is HostMode.Walkable or HostMode.WalkableSmoke
            ? new WalkableScene(runtime.RuntimeOwnership)
            : null;
        var stopReason = HostStopReason.StartupFault;
        Exception? failure = null;
        string? success = null;

        try
        {
            using var wakeOnCancellation = cancellationToken.UnsafeRegister(
                static state => ((RuntimeWakeSignal)state!).Signal(), runtime.SimulationWake);
            world = EcsWorld.Open(runtime, WorldQuota, new BarrierSupervisorPolicy(WorldQuota.Diagnostics.RequiredEvidenceRecords),
                new ComponentRegistryBuilder().Build(), new EcsLimits(16, 1, 0, 1024 * 1024, StorageBytes), storage);
            if (smokeClock is null)
            {
                var title = IsDesktopSmoke(mode) ? DesktopSmoke.WindowTitle : "Worlds Yet Unseen";
                window = new WindowsHostModule(new WindowsHostOptions(title, 960, 540, 256),
                    runtime.UiWake, runtime.RuntimeOwnership);
                await window.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
                await window.StartAsync(cancellationToken).ConfigureAwait(false);
                if ((mode == HostMode.WindowSmoke || IsDesktopSmoke(mode)) &&
                    window.Read() is not { Phase: HostWindowPhase.Open, ClientWidth: > 0, ClientHeight: > 0 })
                    throw new InvalidOperationException("The Windows host did not open a usable window.");
                if (mode == HostMode.D3D12 || IsDesktopSmoke(mode) || terrain is not null || walkable is not null)
                {
                    var diagnostics = IsDesktopSmoke(mode)
                        ? new D3D12DiagnosticsOptions(EnableDebugLayer: true, EnableDred: true)
                        : default;
                    if (walkable is not null)
                    {
                        var initial = await walkable.StartAsync(cancellationToken).ConfigureAwait(false);
                        renderer = D3D12Renderer.CreateWithMeshRegionReplacement(window, runtime.RuntimeOwnership, initial, diagnostics);
                    }
                    else
                    {
                        renderer = terrain is not null
                            ? D3D12Renderer.CreateWithMeshReplacement(window, runtime.RuntimeOwnership, terrain.CreateMeshes(), diagnostics)
                            : new D3D12Renderer(diagnostics, window, runtime.RuntimeOwnership);
                    }
                    await renderer.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
                    await renderer.StartAsync(cancellationToken).ConfigureAwait(false);
                    if (walkable is not null)
                        walkable.Attach(renderer.MeshRegionReplacement
                            ?? throw new InvalidOperationException("The renderer did not expose its region port."));
                }
            }

            stopReason = HostStopReason.RuntimeFault;
            IInputSource input = window is not null ? window : new SmokeInput();
            success = await DriveAsync(mode, context, runtime, world, input, renderer, terrain, walkable, smokeClock, cancellationToken)
                .ConfigureAwait(false);
            stopReason = mode == HostMode.FaultSmoke ? HostStopReason.RuntimeFault : HostStopReason.ExternalCancellation;
        }
        catch (OperationCanceledException) when (IsInteractive(mode) &&
            (cancellationToken.IsCancellationRequested || runtime.State.Read().Conditions == SafetyCondition.TerminalStop) &&
            runtime.Diagnostics.ReadEmergency().Primary is null)
        {
            stopReason = HostStopReason.ExternalCancellation;
        }
        catch (Exception error)
        {
            failure = error;
        }

        try
        {
            await StopAsync(runtime, clock, renderer, window, world, storage, walkable, stopReason).ConfigureAwait(false);
        }
        catch (Exception cleanup) when (failure is not null)
        {
            throw new AggregateException(failure, cleanup);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        if (IsDesktopSmoke(mode))
            DesktopSmoke.VerifyShutdown(renderer!, requireDebugLayer: IsTerrain(mode) || mode == HostMode.WalkableSmoke);
        if (success is not null)
            Console.WriteLine(success);
    }

    private static async Task<string?> DriveAsync(HostMode mode, EngineModuleContext context, RuntimeSafetyAuthority runtime,
        EcsWorld world, IInputSource input, D3D12Renderer? renderer, TerrainScene? terrain,
        WalkableScene? walkable, SmokeClock? smokeClock, CancellationToken cancellationToken)
    {
        var injectedFault = mode == HostMode.FaultSmoke ? new InvalidOperationException("Controlled tick failure.") : null;
        var route = mode == HostMode.WalkableSmoke ? new WalkableRouteSmoke() : null;
        var yaw = 0f;
        ulong routeCompletionTick = 0;
        var pipeline = new WorldTickPipeline(world, injectedFault, terrain is not null, walkable is null ? null : AdvanceWalkable);
        var simulation = new FixedStepSimulation(context, input, pipeline, world.Safety.State, runtime.SimulationWake);
        var scene = renderer is null ? null : new SceneSubmission(renderer, terrain is not null,
            walkable is null ? null : () => walkable.GetScene(route?.CameraYaw ?? yaw));
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
                if (iteration.State == SimulationState.Stopped || pipeline.CloseRequested)
                    break;
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
                    scene.Submit(context.Clock, iteration, pipeline.PublishedTick);
                    var rendered = renderer!.Read();
                    if (route is not null)
                        route.ObservePresentation(rendered, renderer.MeshRegionReplacement!.ReadMeshReplacement());

                    if (IsDesktopSmoke(mode) && pipeline.PublishedTick >= 3 &&
                        rendered is { LastPresentedSequence: > 0, Surface: RenderSurfaceState.Drawable } &&
                        (terrainSmoke is null || terrainSmoke.Advance(meshPort!, edits!)) &&
                        (route is null || routeCompletionTick > 0 && rendered.LastPresentedSequence >= routeCompletionTick))
                    {
                        VerifySmokePublication(world, pipeline, simulation);
                        DesktopSmoke.RequestNativeClose(rendered, route is not null ? "walkable region" : IsTerrain(mode) ? "terrain" : "cube");
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
            return "PASS: WYU controlled fault shutdown.";
        }
        finally
        {
            edits?.Stop();
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
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        if (IsDesktopSmoke(mode))
        {
            if (!nativeCloseSent || !runtimeConditions.HasFlag(SafetyCondition.TerminalStop))
                throw new InvalidOperationException("The D3D12 smoke did not present and observe native close.");
            return mode switch
            {
                HostMode.TerrainSmoke => "PASS: WYU D3D12 terrain presentation and native shutdown.",
                HostMode.WalkableSmoke => "PASS: WYU walkable route and native shutdown.",
                _ => "PASS: WYU D3D12 Windows startup and shutdown.",
            };
        }

        if (mode == HostMode.HeadlessSmoke && (pipeline.PublishedTick != 3 || !pipeline.CloseRequested) || mode == HostMode.FaultSmoke)
            throw new InvalidOperationException("The WYU smoke did not complete the expected input sequence.");
        VerifySmokePublication(world, pipeline, simulation);
        return mode == HostMode.WindowSmoke ? "PASS: WYU Windows startup and shutdown." : "PASS: WYU headless startup and shutdown.";

        void AdvanceWalkable(TickContext tick)
        {
            var drawable = renderer!.Read().Surface == RenderSurfaceState.Drawable;
            var (x, z) = route is null && drawable ? ReadWalkableInput(tick.Input, ref yaw) : default;
            walkable!.Advance(x, z, tick.Tick, route, drawable);
            if (route is { Completed: true } && routeCompletionTick == 0)
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
        EcsWorld? world, EcsStorageBudget storage, WalkableScene? walkable, HostStopReason reason)
    {
        walkable?.Stop();
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
            if (walkable is not null)
            {
                using var workerDeadline = new CancellationTokenSource(deadline.GetRemainingForSettlement(clock));
                await walkable.SettleAsync(workerDeadline.Token).ConfigureAwait(false);
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
