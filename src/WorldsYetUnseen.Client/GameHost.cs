using Groundwork.Core;
using Groundwork.Diagnostics;
using Groundwork.Ecs;
using Groundwork.Platform.Windows;
using Groundwork.Simulation;
using System.Runtime.ExceptionServices;

namespace WorldsYetUnseen.Client;

internal enum HostMode { Interactive, WindowSmoke, HeadlessSmoke, FaultSmoke }

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
                window = new WindowsHostModule(new WindowsHostOptions("Worlds Yet Unseen", 960, 540, 256),
                    runtime.UiWake, runtime.RuntimeOwnership);
                await window.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
                await window.StartAsync(cancellationToken).ConfigureAwait(false);
                if (mode == HostMode.WindowSmoke && window.Read() is not { Phase: HostWindowPhase.Open, ClientWidth: > 0, ClientHeight: > 0 })
                    throw new InvalidOperationException("The Windows host did not open a usable window.");
            }

            stopReason = HostStopReason.RuntimeFault;
            IInputSource input = window is not null ? window : new SmokeInput();
            success = await DriveAsync(mode, context, runtime, world, input, smokeClock, cancellationToken).ConfigureAwait(false);
            stopReason = mode == HostMode.FaultSmoke ? HostStopReason.RuntimeFault : HostStopReason.ExternalCancellation;
        }
        catch (OperationCanceledException) when (mode == HostMode.Interactive &&
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
            await StopAsync(runtime, clock, window, world, storage, stopReason).ConfigureAwait(false);
        }
        catch (Exception cleanup) when (failure is not null)
        {
            throw new AggregateException(failure, cleanup);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        if (success is not null)
            Console.WriteLine(success);
    }

    private static async Task<string?> DriveAsync(HostMode mode, EngineModuleContext context, RuntimeSafetyAuthority runtime,
        EcsWorld world, IInputSource input, SmokeClock? smokeClock, CancellationToken cancellationToken)
    {
        var injectedFault = mode == HostMode.FaultSmoke ? new InvalidOperationException("Controlled tick failure.") : null;
        var pipeline = new WorldTickPipeline(world, injectedFault);
        var simulation = new FixedStepSimulation(context, input, pipeline, world.Safety.State, runtime.SimulationWake);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // The coordinator lets an owned barrier settle before handling console cancellation.
                var iteration = await simulation.AdvanceAsync(CancellationToken.None).ConfigureAwait(false);
                if (iteration.State == SimulationState.Stopped || pipeline.CloseRequested)
                    break;
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

        if (runtime.Diagnostics.ReadEmergency().Primary is { } fault)
            throw new InvalidOperationException($"Groundwork stopped after {fault.Subsystem} fault {fault.Classification} ({fault.Code}).");
        var runtimeConditions = runtime.State.Read().Conditions;
        var worldConditions = world.Safety.State.Read().Conditions;
        if ((worldConditions & SafetyCondition.RequiredEvidenceLost) != 0 ||
            worldConditions.HasFlag(SafetyCondition.TerminalStop) && !runtimeConditions.HasFlag(SafetyCondition.TerminalStop))
            throw new InvalidOperationException($"Groundwork stopped the world unexpectedly ({worldConditions}).");
        if (mode == HostMode.Interactive)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        if (pipeline.PublishedTick < 3 || pipeline.GameplaySteps != pipeline.PublishedTick / 3 ||
            simulation.LastConfirmedBarrier is not { Kind: BarrierKind.CommitResults } barrier ||
            barrier.Tick != pipeline.PublishedTick || barrier.Epoch != 2 * barrier.Tick ||
            mode == HostMode.HeadlessSmoke && (pipeline.PublishedTick != 3 || !pipeline.CloseRequested) ||
            mode == HostMode.FaultSmoke)
            throw new InvalidOperationException("The WYU smoke did not complete the expected simulation and input sequence.");

        var observer = world.Safety.Publication.ObserverSnapshots.TryAcquire();
        using var lease = observer.Lease;
        if (observer.Status != ObserverReadStatus.Acquired || lease?.Current.Stamp != barrier)
            throw new InvalidOperationException("The final observer frame does not match the last committed tick.");

        return mode == HostMode.WindowSmoke ? "PASS: WYU Windows startup and shutdown." : "PASS: WYU headless startup and shutdown.";
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

    private static async Task StopAsync(RuntimeSafetyAuthority runtime, TimeProvider clock, WindowsHostModule? window,
        EcsWorld? world, EcsStorageBudget storage, HostStopReason reason)
    {
        var deadline = runtime.StopControl.RequestStop(reason);
        Exception? windowFailure = null;
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
            windowFailure = error;
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
        catch (Exception settlementFailure) when (windowFailure is not null)
        {
            throw new AggregateException(windowFailure, settlementFailure);
        }

        if (windowFailure is not null)
            ExceptionDispatchInfo.Throw(windowFailure);
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
