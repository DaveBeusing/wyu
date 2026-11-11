using Groundwork.Core;
using Groundwork.Diagnostics;
using Groundwork.Ecs;
using Groundwork.Platform.Windows;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using System.Runtime.ExceptionServices;

namespace WorldsYetUnseen.Client;

internal static partial class GameHost
{
    internal static async Task RunExplorationAsync(ulong? smokeSeed, bool useWarp, AlphaDiagnostics? diagnostics,
        CancellationToken cancellationToken, string? settingsDirectory = null)
    {
        var clock = TimeProvider.System;
        var timeout = TimeSpan.FromSeconds(10);
        var timing = new RuntimeTimingLimits(timeout, timeout, timeout);
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, RuntimeQuota));
        using var reservation = budget.TryReserveRuntime(RuntimeQuota)
            ?? throw new InvalidOperationException("Could not reserve the exploration runtime.");
        var runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
        var context = new EngineModuleContext(clock, timing, runtime.Reporter, runtime.StopControl);
        var window = new WindowsHostModule(new WindowsHostOptions(
            smokeSeed.HasValue ? DesktopSmoke.WindowTitle : "Worlds Yet Unseen — New World", 960, 540, 256),
            runtime.UiWake, runtime.RuntimeOwnership);
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stage.Token;
        Task<bool> watchClose = Task.FromResult(false);
        D3D12Renderer? renderer = null;
        VolumeStreamingCoordinator? terrain = null;
        AlphaOverlay? overlay = null;
        PrototypeMouseLook? mouse = null;
        var reason = HostStopReason.StartupFault;
        Exception? failure = null;
        try
        {
            diagnostics?.Enter("WorldSelection");
            await window.InitializeAsync(context, token).ConfigureAwait(false);
            await window.StartAsync(token).ConfigureAwait(false);
            watchClose = WatchWindowCloseAsync(window, stage);
            var settings = settingsDirectory is null ? AlphaSettings.Defaults : new AlphaSettingsStore(settingsDirectory).Load().Value;
            if (!smokeSeed.HasValue)
            {
                overlay = new AlphaOverlay(activateOwner: true);
                AlphaWindowSize.TryApply(settings.Size);
            }
            var seed = smokeSeed ?? await VolumeExploration.ChooseSeedAsync(window, overlay!, token).ConfigureAwait(false);
            if (seed is null)
            {
                reason = HostStopReason.ExternalCancellation;
                return;
            }

            diagnostics?.Enter("PlanetTerrain");
            if (!runtime.TryAllocateWorldId(out var id))
                throw new InvalidOperationException("Could not allocate an exploration world.");
            var world = runtime.OpenWorld(id, WorldQuota,
                new BarrierSupervisorPolicy(WorldQuota.Diagnostics.RequiredEvidenceRecords), new ExplorationRoot(id),
                new ExplorationJobScope());
            // W02 worlds are ephemeral. A later persistence package supplies an instance ID from the saved manifest.
            var worldIdentity = smokeSeed.HasValue ? new Guid("d89e4d83-1228-4780-b2c4-1af37f48468e") : Guid.NewGuid();
            var planet = new ReferencePlanet(worldIdentity, seed.Value);
            if (!smokeSeed.HasValue)
                mouse = new PrototypeMouseLook(window, settings);
            var exploration = new VolumeExploration(world, planet, window, overlay, mouse);
            terrain = exploration.CreateTerrain();
            renderer = D3D12Renderer.CreateWithVolumeTerrain(window, runtime.RuntimeOwnership, terrain, 1,
                new D3D12DiagnosticsOptions(true, true), useWarp, timeout, timeout);
            await renderer.InitializeAsync(context, token).ConfigureAwait(false);
            await renderer.StartAsync(token).ConfigureAwait(false);
            reason = HostStopReason.RuntimeFault;
            await exploration.ExploreAsync(terrain, renderer, smokeSeed.HasValue, token).ConfigureAwait(false);
            if (smokeSeed.HasValue)
                DesktopSmoke.RequestNativeClose(renderer.Read(), "WP11 exploration");
            reason = HostStopReason.ExternalCancellation;
        }
        catch (OperationCanceledException) when (!smokeSeed.HasValue && token.IsCancellationRequested)
        {
            reason = HostStopReason.ExternalCancellation;
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            stage.Cancel();
            await watchClose.ConfigureAwait(false);
            mouse?.Dispose();
            overlay?.Dispose();
            terrain?.Stop();
            try
            {
                await StopAsync(runtime, clock, renderer, window, null, new EcsStorageBudget(StorageBytes),
                    null, null, reason).ConfigureAwait(false);
            }
            catch (Exception cleanup) when (failure is not null)
            {
                failure = new AggregateException(failure, cleanup);
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);

        if (smokeSeed.HasValue)
        {
            DesktopSmoke.VerifyShutdown(renderer!, requireDebugLayer: true, requireNvidia: !useWarp, useWarp: useWarp);
            if (terrain!.Statistics is not { Readers: 0, Generations: 0 })
                throw new InvalidOperationException("Exploration retained terrain after shutdown.");
            Console.WriteLine("PASS: WYU WP11 exploration, rebase and native shutdown.");
        }
    }

    private sealed record ExplorationRoot(WorldId World) : IWorldStateRoot;

    private sealed class ExplorationJobScope : IJobScopePolicy
    {
        public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
            new(JobScopeStatus.InvalidAccess, null);
        public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => false;
        public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second,
            in CommittedWorldView world) => JobScopeOverlap.Independent;
    }
}
