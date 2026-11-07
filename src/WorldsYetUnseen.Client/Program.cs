namespace WorldsYetUnseen.Client;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var saveSmoke = args is ["--save-smoke-init" or "--save-smoke-interrupt" or "--save-smoke-resume" or
            "--prototype-save-init" or "--prototype-save-before-publish" or "--prototype-save-after-publish" or
            "--prototype-save-finish" or "--prototype-save-verify", _, _];
        HostMode? mode = args switch
        {
            [] or ["--prototype"] or ["--prototype", _] => HostMode.Prototype,
            ["--prototype-smoke"] => HostMode.PrototypeSmoke,
            ["--d3d12"] => HostMode.D3D12,
            ["--d3d12-smoke"] => HostMode.D3D12Smoke,
            ["--terrain"] => HostMode.Terrain,
            ["--terrain-smoke"] => HostMode.TerrainSmoke,
            ["--walkable"] => HostMode.Walkable,
            ["--walkable-smoke"] => HostMode.WalkableSmoke,
            ["--save-world"] or ["--save-world", _] => HostMode.SaveWorld,
            ["--window-smoke"] => HostMode.WindowSmoke,
            ["--headless-smoke"] => HostMode.HeadlessSmoke,
            ["--fault-smoke"] => HostMode.FaultSmoke,
            _ => null,
        };
        if (mode is null && !saveSmoke)
        {
            Console.Error.WriteLine("Usage: WorldsYetUnseen.Client [--prototype [slot-directory] | --prototype-smoke | --d3d12 | --d3d12-smoke | --terrain | --terrain-smoke | --walkable | --walkable-smoke | --save-world [slot-directory] | --window-smoke | --headless-smoke | --fault-smoke]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        if (saveSmoke)
            cancellation.CancelAfter(TimeSpan.FromSeconds(45));
        else if (!GameHost.IsInteractive(mode!.Value))
            cancellation.CancelAfter(TimeSpan.FromSeconds(mode is HostMode.WalkableSmoke or HostMode.PrototypeSmoke ? 120 : 15));

        Console.CancelKeyPress += Cancel;
        try
        {
            if (saveSmoke)
                await GameHost.RunSaveSmokeAsync(args[0], args[1], args[2], cancellation.Token).ConfigureAwait(false);
            else
                await GameHost.RunAsync(mode!.Value, cancellation.Token, args.Length == 2 ? args[1] : null).ConfigureAwait(false);
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
