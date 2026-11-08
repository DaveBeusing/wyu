namespace WorldsYetUnseen.Client;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var saveSmoke = args is ["--save-smoke-init" or "--save-smoke-interrupt" or "--save-smoke-resume" or
            "--prototype-save-init" or "--prototype-save-before-publish" or "--prototype-save-after-publish" or
            "--prototype-save-finish" or "--prototype-save-verify" or "--prototype-save-mission-fixture", _, _];
        HostMode? mode = args switch
        {
            [] or ["--prototype"] or ["--prototype", _] or ["--prototype", _, _] => HostMode.Prototype,
            ["--prototype-smoke"] => HostMode.PrototypeSmoke,
            ["--d3d12"] => HostMode.D3D12,
            ["--d3d12-smoke"] => HostMode.D3D12Smoke,
            ["--terrain"] => HostMode.Terrain,
            ["--terrain-smoke"] => HostMode.TerrainSmoke,
            ["--walkable"] => HostMode.Walkable,
            ["--walkable-smoke"] => HostMode.WalkableSmoke,
            ["--planet"] => HostMode.Planet,
            ["--planet-smoke"] => HostMode.PlanetSmoke,
            ["--flight"] or ["--flight", _] => HostMode.Flight,
            ["--flight-smoke", _] => HostMode.FlightSmoke,
            ["--flight-reload-smoke", _] => HostMode.FlightReloadSmoke,
            ["--save-world"] or ["--save-world", _] => HostMode.SaveWorld,
            ["--window-smoke"] => HostMode.WindowSmoke,
            ["--headless-smoke"] => HostMode.HeadlessSmoke,
            ["--fault-smoke"] => HostMode.FaultSmoke,
            _ => null,
        };
        if (mode is null && !saveSmoke)
        {
            Console.Error.WriteLine("Usage: WorldsYetUnseen.Client [--prototype [slot-directory [settings-directory]] | --prototype-smoke | --d3d12 | --d3d12-smoke | --terrain | --terrain-smoke | --walkable | --walkable-smoke | --planet | --planet-smoke | --flight [slot-directory] | --flight-smoke slot-directory | --flight-reload-smoke slot-directory | --save-world [slot-directory] | --window-smoke | --headless-smoke | --fault-smoke]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        if (saveSmoke)
            cancellation.CancelAfter(TimeSpan.FromSeconds(45));
        else if (!GameHost.IsInteractive(mode!.Value))
            cancellation.CancelAfter(TimeSpan.FromSeconds(mode is HostMode.FlightSmoke ? 150 :
                mode is HostMode.WalkableSmoke or HostMode.PrototypeSmoke or HostMode.PlanetSmoke ? 120 : 15));

        Console.CancelKeyPress += Cancel;
        try
        {
            if (saveSmoke)
                await GameHost.RunSaveSmokeAsync(args[0], args[1], args[2], cancellation.Token).ConfigureAwait(false);
            else
                await GameHost.RunAsync(mode!.Value, cancellation.Token, args.Length >= 2 ? args[1] : null,
                    args.Length == 3 ? args[2] : null).ConfigureAwait(false);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }

        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
    }
}
