using Groundwork.Core;
using Groundwork.Diagnostics;
using Groundwork.Ecs;
using Groundwork.Persistence;
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
        string? saveRoot = null, string? saveSmoke = null, bool flightSmoke = false, string? caveSmoke = null, bool freeTestWorld = false,
        bool constructionContinue = false, bool constructionMotion = false, bool constructionMeasure = false)
    {
        var clock = TimeProvider.System;
        var timeout = TimeSpan.FromSeconds(10);
        var timing = new RuntimeTimingLimits(timeout, timeout, timeout);
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, RuntimeQuota));
        using var reservation = budget.TryReserveRuntime(RuntimeQuota)
            ?? throw new InvalidOperationException("Could not reserve the exploration runtime.");
        var runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
        var context = new EngineModuleContext(clock, timing, runtime.Reporter, runtime.StopControl);
        var scripted = smokeSeed.HasValue || saveSmoke is not null || caveSmoke is not null;
        var readbackSmoke = flightSmoke && saveSmoke is "readback" or "marker-readback";
        var orbitSmoke = scripted && !readbackSmoke && caveSmoke is null &&
            Environment.GetEnvironmentVariable("WYU_ORBIT_DEMO_SMOKE") == "1";
        var measureSunlight = scripted && Environment.GetEnvironmentVariable("WYU_WP15_PROBE") == "measure";
        var window = new WindowsHostModule(new WindowsHostOptions(
            scripted ? DesktopSmoke.WindowTitle : "Worlds Yet Unseen — New World", 960, 540, 256),
            runtime.UiWake, runtime.RuntimeOwnership);
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stage.Token;
        Task<bool> watchClose = Task.FromResult(false);
        D3D12Renderer? renderer = null;
        var measure = measureSunlight || scripted && BiomeMeasurements.Enabled;
        var measureR09 = scripted && flightSmoke && saveSmoke == "flight" &&
            Environment.GetEnvironmentVariable("WYU_R09_LATENCY") == "1";
        using var renderHost = new ProceduralRenderHost(window, useWarp, timing, RuntimeQuota, measure, measureR09)
        {
            ConstructionVisuals = scripted ? null : new ConstructionVisuals(),
            WarpReadback = readbackSmoke
                ? new Wp11WarpReadback(saveRoot!, markerOnly: saveSmoke == "marker-readback") : null,
        };
        VolumeStreamingCoordinator? terrain = null;
        VolumeExploration? exploration = null;
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
            if ((measureSunlight || orbitSmoke && OrbitPbrMeasurements.Enabled) && !AlphaWindowSize.TryApply((1920, 1080)))
                throw new InvalidOperationException("The renderer probe requires a 1920x1080 client area.");
            watchClose = WatchWindowCloseAsync(window, stage);
            var settings = settingsDirectory is null ? AlphaSettings.Defaults : new AlphaSettingsStore(settingsDirectory).Load().Value;
            if (!scripted)
            {
                overlay = new AlphaOverlay(activateOwner: true);
                AlphaWindowSize.TryApply(settings.Size);
            }
            if (constructionContinue)
            {
                if (saveRoot is null || renderHost.ConstructionVisuals is null)
                    throw new InvalidOperationException("Construction proof needs a fixture and an active construction renderer.");
                var selected = VolumeWorldSlot.List(saveRoot).Single();
                slot = VolumeWorldSlot.Open(saveRoot, selected, out var loaded);
                if (slot is null || loaded != VolumeSlotStatus.Loaded || slot.NeedsInventoryImport)
                    throw new InvalidDataException($"Construction proof could not continue the fixture ({loaded}).");
            }
            else if (!scripted)
                slot = await ChooseVolumeSlotAsync(window, overlay!, saveRoot ?? (freeTestWorld ? Path.Combine(FreeTestWorldRoot(), "v2") : DefaultVolumeRoot()),
                    token, freeTestWorld).ConfigureAwait(false);
            else if (caveSmoke is not null)
                slot = await OpenCaveSmokeSlotAsync(saveRoot!, caveSmoke, token).ConfigureAwait(false);
            else if (saveSmoke is not null)
                slot = await OpenVolumeSmokeSlotAsync(saveRoot!, readbackSmoke || saveSmoke is "flight" or "abort" ? "init" : saveSmoke,
                    token, flightSmoke ? smokeSeed ?? 42 : 42, orbitSmoke)
                    .ConfigureAwait(false);
            if (!scripted && slot is null)
            {
                reason = HostStopReason.ExternalCancellation;
                return;
            }

            if (overlay is not null)
                overlay.Reticle = slot?.Cave is not null;
            diagnostics?.Enter("PlanetTerrain");
            if (!runtime.TryAllocateWorldId(out var id))
                throw new InvalidOperationException("Could not allocate an exploration world.");
            InventoryState? inventory = scripted ? InventoryState.Empty : InventoryState.Starter;
            ConstructionState? construction = ConstructionState.Empty;
            ProceduralLanding? landed = null;
            if (slot?.Checkpoint is { } checkpoint &&
                InventoryCheckpoint.Read(checkpoint, out inventory, out construction, out landed, out _) != SaveGameValidation.Valid)
                throw new InvalidDataException("The game checkpoint could not be restored.");
            var initial = new CaveInventory(id, inventory!, landed) { Construction = construction! };
            var world = runtime.OpenWorld(id, WorldQuota,
                new BarrierSupervisorPolicy(WorldQuota.Diagnostics.RequiredEvidenceRecords), initial,
                measureR09 ? new R09JobScope() : new ExplorationJobScope(), slot?.Checkpoint?.Tick ?? 0);
            var planet = slot?.Planet ?? (slot?.Cave is null
                ? new ReferencePlanet(new Guid("d89e4d83-1228-4780-b2c4-1af37f48468e"), smokeSeed!.Value) : null);
            if (!scripted)
                mouse = new PrototypeMouseLook(window, settings);
            var retrySmoke = scripted ? Environment.GetEnvironmentVariable("WYU_HOST_RETRY_SMOKE") : null;
            var orbitDemo = slot?.OrbitDemo ?? orbitSmoke;
            string? orbitMaterialFallbackNotice = null;
            if (orbitDemo && OrbitDemoPreset.PbrEnabled)
            {
                try
                {
                    renderHost.OrbitMaterials = await Task.Run(OrbitTerrainMaterials.Load, token).ConfigureAwait(false);
                }
                catch (InvalidDataException error)
                {
                    if (scripted && OrbitPbrMeasurements.Enabled)
                        throw new InvalidDataException($"Orbit PBR measurement stopped. {error.Message}", error);

                    orbitMaterialFallbackNotice = $"{error.Message}\nUsing base colors for Orbit terrain.";
                    diagnostics?.Write("orbit-texture-fallback", error.ToString());
                    if (scripted)
                        Console.Error.WriteLine(orbitMaterialFallbackNotice);
                }
            }
            exploration = new VolumeExploration(world, planet, window, overlay, mouse, slot, diagnostics, freeTestWorld, orbitDemo)
            {
                ConstructionRenderer = renderHost.ConstructionVisuals,
                VerifyTerrainRetries = retrySmoke == "1",
                R09RenderHost = measureR09 ? renderHost : null,
                OrbitMaterialHost = renderHost.BuildsOrbitalMaterials ? renderHost : null,
                OrbitMaterialFallbackNotice = orbitMaterialFallbackNotice,
                ExhaustTerrainRetry = retrySmoke switch
                {
                    "Movement" => VolumeWorkClass.Movement,
                    "FirstEdit" => VolumeWorkClass.FirstEdit,
                    "Recovery" => VolumeWorkClass.Recovery,
                    _ => null,
                },
            };
            var propsSmoke = scripted && (orbitDemo || Environment.GetEnvironmentVariable("WYU_WP16_SMOKE") == "1");
            if (planet is not null && (propsSmoke || !scripted && Environment.GetEnvironmentVariable("WYU_WP16") != "off"))
                exploration.Props = renderHost.Props = new ReferenceProps(planet, orbitDemo && OrbitDemoPreset.BoulderEnabled);
            if (planet is not null && (!scripted || BiomeMeasurements.UseBiomes || propsSmoke))
                renderHost.SetBiomes(new ReferenceBiomes(planet));
            if (measureR09)
                terrain = exploration.CreateTerrain();
            await using var earlyTerrain = terrain is null ? null
                : await exploration.StartR09LookaheadAsync(terrain, saveSmoke, token).ConfigureAwait(false);
            if (planet is not null)
            {
                var planStarted = BiomeMeasurements.Start();
                ProceduralGlobe.Prepare();
                BiomeMeasurements.Write("globe-plan", planStarted);
            }
            terrain ??= exploration.CreateTerrain();
            diagnostics?.Enter("GraphicsStartup");
            diagnostics?.CheckGraphics();
            await renderHost.NearAsync(terrain, token).ConfigureAwait(false);
            renderer = renderHost.Active;
            diagnostics?.Write("adapter", renderer.ReadDiagnostics().Adapter?.Description);
            if (!scripted && slot?.NeedsInventoryImport == true)
            {
                var imported = await exploration.ImportInventoryAsync(terrain, renderer, token).ConfigureAwait(false);
                if (imported.Slot is not { } importedSlot)
                {
                    overlay!.Menu = $"Import stopped\n{imported.Reason ?? imported.Status.ToString()}\nThe original slot was kept.\nEsc to return";
                    while (!PrototypeMenu.Pressed(window.Capture(), InputControl.Escape))
                        await Task.Delay(16, token).ConfigureAwait(false);
                    reason = HostStopReason.ExternalCancellation;
                    return;
                }
                slot.Dispose();
                slot = importedSlot;
            }
            diagnostics?.Enter("PlanetTerrain");
            reason = HostStopReason.RuntimeFault;
            if (caveSmoke is not null)
                await exploration.RunCaveSmokeAsync(terrain, renderHost, caveSmoke, token).ConfigureAwait(false);
            else if (flightSmoke)
                await exploration.RunFlightSmokeAsync(terrain, renderHost, saveSmoke!, token).ConfigureAwait(false);
            else if (saveSmoke is null)
                await exploration.ExploreAsync(terrain, renderHost, scripted, token, constructionContinue, constructionMotion, constructionMeasure)
                    .ConfigureAwait(false);
            else
                await exploration.RunSaveSmokeAsync(terrain, renderer, saveSmoke, token).ConfigureAwait(false);
            if (scripted)
            {
                renderHost.Dispose();
                DesktopSmoke.RequestNativeClose(renderHost.Active.Read(), caveSmoke is null ? "WP11 exploration" : "WP12 cave");
            }
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
            if (renderHost.Props is { } props)
                await props.StopAsync().ConfigureAwait(false);
            terrain?.Stop();
            renderer = renderHost.Current;
            if (renderer?.Read() is { } graphics &&
                (graphics.FirstFault is not null || graphics.FirstCleanupFault is not null))
                diagnostics?.MarkGraphicsFailure();
            try
            {
                try
                {
                    try
                    {
                        if (measureR09 && exploration is not null)
                            await exploration.StopR09JobsAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await renderHost.StopAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    await StopAsync(runtime, clock, null, window, null, new EcsStorageBudget(StorageBytes),
                        null, null, reason).ConfigureAwait(false);
                }
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
            DesktopSmoke.VerifyShutdown(renderer!, requireDebugLayer: !measure, requireNvidia: !useWarp, useWarp: useWarp);
            if (terrain!.Statistics is not { Readers: 0, Generations: 0 } settled || settled.Used != default)
                throw new InvalidOperationException("Exploration retained terrain after shutdown.");
            if (measureR09)
                exploration!.WriteR09SettledResources(renderer!, terrain.View.RendererEpoch, settled);
            if (Environment.GetEnvironmentVariable("WYU_HOST_RETRY_SMOKE") is "1" or "Movement" or "FirstEdit" or "Recovery")
                Console.WriteLine("WP11 HOST RETRY SETTLED terrain=0 readers=0 generations=0");
            Console.WriteLine(caveSmoke is null ? "PASS: WYU WP11 exploration, rebase and native shutdown."
                : "PASS: WYU WP12 cave route and native shutdown.");
        }
    }

    internal static async Task VerifyConnectedProfilesAsync(WindowsHostModule window, VolumeWorldSlot slot,
        Groundwork.Rendering.RenderMeshPair globe, CancellationToken token)
    {
        // The profile sites are away from the route. Publish them without changing the saved player's coverage.
        var clock = TimeProvider.System;
        var timeout = TimeSpan.FromSeconds(10);
        var timing = new RuntimeTimingLimits(timeout, timeout, timeout);
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, RuntimeQuota));
        using var reservation = budget.TryReserveRuntime(RuntimeQuota)
            ?? throw new InvalidOperationException("Could not reserve the connected relief profile runtime.");
        var runtime = RuntimeSafetyAuthority.Create(clock, timing, reservation);
        VolumeStreamingCoordinator? terrain = null;
        try
        {
            if (!runtime.TryAllocateWorldId(out var id))
                throw new InvalidOperationException("Could not allocate the connected relief profile world.");
            var world = runtime.OpenWorld(id, WorldQuota,
                new BarrierSupervisorPolicy(WorldQuota.Diagnostics.RequiredEvidenceRecords), new CaveInventory(id, 0, 1),
                new ExplorationJobScope(), slot.Checkpoint?.Tick ?? 0);
            var exploration = new VolumeExploration(world, null, window, null, null, slot, null);
            terrain = exploration.CreateTerrain(restore: false);
            await exploration.VerifyConnectedProfilesAsync(terrain, globe, token).ConfigureAwait(false);
        }
        finally
        {
            terrain?.Stop();
            await StopAsync(runtime, clock, null, null, null, new EcsStorageBudget(StorageBytes),
                null, null, HostStopReason.ExternalCancellation).ConfigureAwait(false);
        }
    }

    private static string DefaultVolumeRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorldsYetUnseen", "WP11", "v2");

    private static string FreeTestWorldRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorldsYetUnseen", "TestWorlds");

    private static async Task<VolumeWorldSlot?> ChooseVolumeSlotAsync(WindowsHostModule window, AlphaOverlay overlay,
        string root, CancellationToken token, bool freeTestWorld = false)
    {
        var slots = VolumeWorldSlot.List(root);
        var defaultProfile = VolumeWorldSlot.DefaultProfile;
        var selected = 0;
        var caveOption = slots.Length + 1;
        var connectedOption = caveOption + 1;
        var orbitOption = freeTestWorld ? connectedOption + 1 : -1;
        var referenceOption = connectedOption + (freeTestWorld ? 2 : 1);
        var optionCount = referenceOption + 1;
        ulong? replacementSeed = null;
        string? notice = null;
        while (true)
        {
            var label = selected == 0 ? (freeTestWorld ? $"New Test World\n{VolumeWorldSlot.ProfileName(defaultProfile)}" : "New World") :
                selected == orbitOption ? $"New {OrbitDemoPreset.DisplayName}\nSeed {OrbitDemoPreset.Seed} · sunset lighting" :
                selected == referenceOption ? "New Far Horizon World\nBiomes, weather, sky, shadows and vegetation" : selected == connectedOption
                ? $"New V3 World\n{CavePlanet.ConnectedName} · seed {CavePlanet.Seed}" : selected == caveOption
                ? $"New Cave World\n{CavePlanet.Name} · seed {CavePlanet.Seed}" : $"Continue {selected}/{slots.Length}\n{slots[selected - 1]:N}";
            var guidance = selected == 0 ? (freeTestWorld ? "Up for Far Horizon features\n" : "Up for Far Horizon\n") : "";
            var menuTitle = freeTestWorld ? "Free Test World" : "Planet worlds";
            overlay.Menu = $"WYU\n\n{menuTitle}\n\n> {label}\n\n{guidance}Up / Down to choose\nEnter to open\nEsc to exit\n\n{notice}";
            await Task.Delay(16, token).ConfigureAwait(false);
            var input = window.Capture();
            if (PrototypeMenu.Pressed(input, InputControl.Escape))
                return null;
            if (PrototypeMenu.Pressed(input, InputControl.Up))
                selected = (selected + optionCount - 1) % optionCount;
            if (PrototypeMenu.Pressed(input, InputControl.Down))
                selected = (selected + 1) % optionCount;
            if (!PrototypeMenu.Pressed(input, InputControl.Enter))
                continue;

            if (selected == orbitOption)
                return await Task.Run(() => VolumeWorldSlot.Create(root, OrbitDemoPreset.Seed, VolumeWorldSlot.WorldProfile.OrbitDemo))
                    .ConfigureAwait(false);
            if (selected == connectedOption)
                return await Task.Run(() => VolumeWorldSlot.Create(root, CavePlanet.Seed, VolumeWorldSlot.WorldProfile.Connected)).ConfigureAwait(false);
            if (selected == caveOption)
                return await Task.Run(() => VolumeWorldSlot.Create(root, CavePlanet.Seed, VolumeWorldSlot.WorldProfile.Cave)).ConfigureAwait(false);

            if (selected == 0 || selected == referenceOption)
            {
                var profile = selected == 0 ? defaultProfile : VolumeWorldSlot.WorldProfile.Reference;
                ulong? seed = profile == VolumeWorldSlot.WorldProfile.Cave ? CavePlanet.Seed :
                    await VolumeExploration.ChooseSeedAsync(window, overlay, token,
                    VolumeWorldSlot.ProfileName(profile),
                    freeTestWorld ? (replacementSeed ?? CavePlanet.Seed) : null).ConfigureAwait(false);
                if (seed is { } value)
                    return await Task.Run(() => VolumeWorldSlot.Create(root, value, profile)).ConfigureAwait(false);
                continue;
            }
            var opened = await Task.Run(() =>
            {
                var loaded = VolumeWorldSlot.Open(root, slots[selected - 1], out var status);
                return (loaded, status);
            }).ConfigureAwait(false);
            if (opened.loaded is { } slot)
            {
                if (!slot.NeedsInventoryImport)
                    return slot;
                if (slot.InventoryImportError is { } importError)
                {
                    notice = $"{importError}\nThe original slot was kept.";
                    slot.Dispose();
                    continue;
                }
                overlay.Menu = "Import saved world\n\nImport the validated state into a new slot. The original slot will be kept.\n\nEnter to import\nEsc to go back";
                var accepted = false;
                try
                {
                    while (true)
                    {
                        await Task.Delay(16, token).ConfigureAwait(false);
                        var confirmation = window.Capture();
                        if (PrototypeMenu.Pressed(confirmation, InputControl.Enter))
                        {
                            accepted = true;
                            return slot;
                        }
                        if (PrototypeMenu.Pressed(confirmation, InputControl.Escape))
                            break;
                    }
                }
                finally
                {
                    if (!accepted)
                        slot.Dispose();
                }
                continue;
            }
            notice = opened.status switch
            {
                VolumeSlotStatus.Incompatible => "This slot has an incompatible world format. Its files were kept.",
                VolumeSlotStatus.Corrupt => "This world has damaged checkpoint data. Its files were kept.",
                VolumeSlotStatus.Empty => "This world has no committed checkpoint yet.",
                VolumeSlotStatus.Busy or VolumeSlotStatus.Indeterminate => "This world is busy or still recovering. Try again.",
                _ => $"Cannot continue: {opened.status}."
            };
            replacementSeed = freeTestWorld && opened.status == VolumeSlotStatus.Incompatible
                ? VolumeWorldSlot.ReadManifestSeed(root, slots[selected - 1]) : null;
            notice += replacementSeed is { } originalSeed
                ? $"\nChoose New Test World to start a separate slot with seed {originalSeed}."
                : "\nChoose a new world for a separate slot.";
        }
    }

    private sealed class ExplorationJobScope : IJobScopePolicy
    {
        public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
            new(JobScopeStatus.InvalidAccess, null);
        public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => false;
        public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second,
            in CommittedWorldView world) => JobScopeOverlap.Independent;
    }
}
