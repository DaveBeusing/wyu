namespace WorldsYetUnseen.Client;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        HostMode? mode = args switch
        {
            [] => HostMode.Interactive,
            ["--window-smoke"] => HostMode.WindowSmoke,
            ["--headless-smoke"] => HostMode.HeadlessSmoke,
            ["--fault-smoke"] => HostMode.FaultSmoke,
            _ => null,
        };
        if (mode is null)
        {
            Console.Error.WriteLine("Usage: WorldsYetUnseen.Client [--window-smoke | --headless-smoke | --fault-smoke]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        if (mode != HostMode.Interactive)
            cancellation.CancelAfter(TimeSpan.FromSeconds(15));

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
