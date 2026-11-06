namespace WorldsYetUnseen.Client;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        HostMode? mode = args switch
        {
            [] => HostMode.Interactive,
            ["--d3d12"] => HostMode.D3D12,
            ["--d3d12-smoke"] => HostMode.D3D12Smoke,
            ["--terrain"] => HostMode.Terrain,
            ["--terrain-smoke"] => HostMode.TerrainSmoke,
            ["--walkable"] => HostMode.Walkable,
            ["--walkable-smoke"] => HostMode.WalkableSmoke,
            ["--window-smoke"] => HostMode.WindowSmoke,
            ["--headless-smoke"] => HostMode.HeadlessSmoke,
            ["--fault-smoke"] => HostMode.FaultSmoke,
            _ => null,
        };
        if (mode is null)
        {
            Console.Error.WriteLine("Usage: WorldsYetUnseen.Client [--d3d12 | --d3d12-smoke | --terrain | --terrain-smoke | --walkable | --walkable-smoke | --window-smoke | --headless-smoke | --fault-smoke]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        if (mode is not (HostMode.Interactive or HostMode.D3D12 or HostMode.Terrain or HostMode.Walkable))
            cancellation.CancelAfter(TimeSpan.FromSeconds(mode == HostMode.WalkableSmoke ? 120 : 15));

        Console.CancelKeyPress += Cancel;
        try
        {
            await GameHost.RunAsync(mode.Value, cancellation.Token).ConfigureAwait(false);
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
