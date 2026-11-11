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
        CancellationToken cancellationToken, string? settingsDirectory = null,
        string? saveRoot = null, string? saveSmoke = null)
    {
        var clock = TimeProvider.System;
        var timeout = TimeSpan.FromSeconds(10);
        var timing = new RuntimeTimingLimits(timeout, timeout, timeout);
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, RuntimeQuota));
        using var reservation = budget.TryReserveRuntime(RuntimeQuota)
            ?? throw new InvalidOperationException("Could not reserve the exploration runtime.");
        var runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
        var context = new EngineModuleContext(clock, timing, runtime.Reporter, runtime.StopControl);
        var scripted = smokeSeed.HasValue || saveSmoke is not null;
        var window = new WindowsHostModule(new WindowsHostOptions(
            scripted ? DesktopSmoke.WindowTitle : "Worlds Yet Unseen — New World", 960, 540, 256),
            runtime.UiWake, runtime.RuntimeOwnership);
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stage.Token;
        Task<bool> watchClose = Task.FromResult(false);
        D3D12Renderer? renderer = null;
        VolumeStreamingCoordinator? terrain = null;
        AlphaOverlay? overlay = null;
        PrototypeMouseLook? mouse = null;
        VolumeWorldSlot? slot = null;
        var reason = HostStopReason.StartupFault;
        Exception? failure = null;
        try
        {
            diagnostics?.Enter("WorldSelection");
            await window.InitializeAsync(context, token).ConfigureAwait(false);
            await window.StartAsync(token).ConfigureAwait(false);
            watchClose = WatchWindowCloseAsync(window, stage);
            var settings = settingsDirectory is null ? AlphaSettings.Defaults : new AlphaSettingsStore(settingsDirectory).Load().Value;
            if (!scripted)
            {
                overlay = new AlphaOverlay(activateOwner: true);
                AlphaWindowSize.TryApply(settings.Size);
            }
            if (!scripted)
                slot = await ChooseVolumeSlotAsync(window, overlay!, saveRoot ?? DefaultVolumeRoot(), token).ConfigureAwait(false);
            else if (saveSmoke is not null)
                slot = await OpenVolumeSmokeSlotAsync(saveRoot!, saveSmoke, token).ConfigureAwait(false);
            if (!scripted && slot is null)
            {
                reason = HostStopReason.ExternalCancellation;
                return;
            }

            diagnostics?.Enter("PlanetTerrain");
            if (!runtime.TryAllocateWorldId(out var id))
                throw new InvalidOperationException("Could not allocate an exploration world.");
            var world = runtime.OpenWorld(id, WorldQuota,
                new BarrierSupervisorPolicy(WorldQuota.Diagnostics.RequiredEvidenceRecords), new ExplorationRoot(id),
                new ExplorationJobScope(), slot?.Checkpoint?.Tick ?? 0);
            var planet = slot?.Planet ?? new ReferencePlanet(new Guid("d89e4d83-1228-4780-b2c4-1af37f48468e"), smokeSeed!.Value);
            if (!scripted)
                mouse = new PrototypeMouseLook(window, settings);
            var exploration = new VolumeExploration(world, planet, window, overlay, mouse, slot);
            terrain = exploration.CreateTerrain();
            renderer = D3D12Renderer.CreateWithVolumeTerrain(window, runtime.RuntimeOwnership, terrain, 1,
                new D3D12DiagnosticsOptions(true, true), useWarp, timeout, timeout);
            await renderer.InitializeAsync(context, token).ConfigureAwait(false);
            await renderer.StartAsync(token).ConfigureAwait(false);
            reason = HostStopReason.RuntimeFault;
            if (saveSmoke is null)
                await exploration.ExploreAsync(terrain, renderer, scripted, token).ConfigureAwait(false);
            else
                await exploration.RunSaveSmokeAsync(terrain, renderer, saveSmoke, token).ConfigureAwait(false);
            if (scripted)
                DesktopSmoke.RequestNativeClose(renderer.Read(), "WP11 exploration");
            reason = HostStopReason.ExternalCancellation;
        }
        catch (OperationCanceledException) when (!scripted && token.IsCancellationRequested)
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
            slot?.Dispose();
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

        if (scripted)
        {
            DesktopSmoke.VerifyShutdown(renderer!, requireDebugLayer: true, requireNvidia: !useWarp, useWarp: useWarp);
            if (terrain!.Statistics is not { Readers: 0, Generations: 0 })
                throw new InvalidOperationException("Exploration retained terrain after shutdown.");
            Console.WriteLine("PASS: WYU WP11 exploration, rebase and native shutdown.");
        }
    }

    private static string DefaultVolumeRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorldsYetUnseen", "WP11", "v2");

    private static async Task<VolumeWorldSlot?> ChooseVolumeSlotAsync(WindowsHostModule window, AlphaOverlay overlay,
        string root, CancellationToken token)
    {
        var slots = VolumeWorldSlot.List(root);
        var selected = 0;
        string? notice = null;
        while (true)
        {
            var label = selected == 0 ? "New World" : $"Continue {selected}/{slots.Length}\n{slots[selected - 1]:N}";
            overlay.Menu = $"WYU\n\nPlanet worlds\n\n> {label}\n\nUp / Down to choose\nEnter to open\nEsc to exit\n\n{notice}";
            await Task.Delay(16, token).ConfigureAwait(false);
            var input = window.Capture();
            if (PrototypeMenu.Pressed(input, InputControl.Escape))
                return null;
            if (PrototypeMenu.Pressed(input, InputControl.Up))
                selected = (selected + slots.Length) % (slots.Length + 1);
            if (PrototypeMenu.Pressed(input, InputControl.Down))
                selected = (selected + 1) % (slots.Length + 1);
            if (!PrototypeMenu.Pressed(input, InputControl.Enter))
                continue;

            if (selected == 0)
            {
                var seed = await VolumeExploration.ChooseSeedAsync(window, overlay, token).ConfigureAwait(false);
                if (seed is { } value)
                    return await Task.Run(() => VolumeWorldSlot.Create(root, value)).ConfigureAwait(false);
                continue;
            }
            var opened = await Task.Run(() =>
            {
                var loaded = VolumeWorldSlot.Open(root, slots[selected - 1], out var status);
                return (loaded, status);
            }).ConfigureAwait(false);
            if (opened.loaded is { } slot)
                return slot;
            notice = $"Cannot continue: {opened.status}.\nChoose New World for a separate slot.";
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
