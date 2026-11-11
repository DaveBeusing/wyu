using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Gameplay.Tests;

internal static class Program
{
    private const DepositMask AllDeposits = DepositMask.West | DepositMask.Seam | DepositMask.East;

    private static int Main()
    {
        (string Name, Action Run)[] tests =
        [
            (nameof(FixedRouteRepeatsEveryCheckpoint), FixedRouteRepeatsEveryCheckpoint),
            (nameof(AllMiningOrdersConserveStone), AllMiningOrdersConserveStone),
            (nameof(RejectedActionsPreserveProgress), RejectedActionsPreserveProgress),
            (nameof(CapturedProgressSurvivesLaterActions), CapturedProgressSurvivesLaterActions),
            (nameof(ReferencePlanetTests.SeedAudit), ReferencePlanetTests.SeedAudit),
            (nameof(ReferencePlanetTests.ChartContinuity), ReferencePlanetTests.ChartContinuity),
            (nameof(ReferencePlanetTests.PublishedSpawnAndLanding), ReferencePlanetTests.PublishedSpawnAndLanding),
        ];
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL: {name}: {error.Message}");
            }
        }

        Console.WriteLine($"WYU gameplay: {tests.Length - failed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static void FixedRouteRepeatsEveryCheckpoint()
    {
        var first = RunRoute();
        var second = RunRoute();
        for (var i = 0; i < first.Length; i++)
            Equal(first[i], second[i], $"replayed checkpoint {i}");
    }

    private static PrototypeProgress[] RunRoute()
    {
        PrototypeProgress start = default;
        Checkpoint(start, DepositMask.None, 0, 0, BeaconModule.Foundation, false);
        var west = Mine(start, Deposit.West);
        Checkpoint(west, DepositMask.West, 1, 0, BeaconModule.Foundation, false);
        var seam = Mine(west, Deposit.Seam);
        Checkpoint(seam, DepositMask.West | DepositMask.Seam, 2, 0, BeaconModule.Foundation, false);
        var east = Mine(seam, Deposit.East);
        Checkpoint(east, AllDeposits, 3, 0, BeaconModule.Foundation, false);
        var foundation = Build(east, BeaconModule.Foundation);
        Checkpoint(foundation, AllDeposits, 2, 1, BeaconModule.Mast, false);
        var mast = Build(foundation, BeaconModule.Mast);
        Checkpoint(mast, AllDeposits, 1, 2, BeaconModule.SignalHead, false);
        var head = Build(mast, BeaconModule.SignalHead);
        Checkpoint(head, AllDeposits, 0, 3, null, false);
        Equal(ActionResult.Applied, head.Activate(out var online), "activate complete beacon");
        Checkpoint(online, AllDeposits, 0, 3, null, true);
        return [start, west, seam, east, foundation, mast, head, online];
    }

    private static void AllMiningOrdersConserveStone()
    {
        Deposit[][] orders =
        [
            [Deposit.West, Deposit.Seam, Deposit.East],
            [Deposit.West, Deposit.East, Deposit.Seam],
            [Deposit.Seam, Deposit.West, Deposit.East],
            [Deposit.Seam, Deposit.East, Deposit.West],
            [Deposit.East, Deposit.West, Deposit.Seam],
            [Deposit.East, Deposit.Seam, Deposit.West],
        ];
        BeaconModule[] modules = [BeaconModule.Foundation, BeaconModule.Mast, BeaconModule.SignalHead];
        foreach (var order in orders)
        {
            PrototypeProgress state = default;
            for (var i = 0; i < order.Length; i++)
            {
                var mined = Mine(state, order[i]);
                Equal(1, mined.Stone, $"award from {order[i]}");
                state = Build(mined, modules[i]);
                Equal(0, state.Stone, $"price of {modules[i]}");
            }

            Checkpoint(state, AllDeposits, 0, 3, null, false);
            Equal(ActionResult.Applied, state.Activate(out var online), "activate after interleaved mining and building");
            Checkpoint(online, AllDeposits, 0, 3, null, true);
        }
    }

    private static void RejectedActionsPreserveProgress()
    {
        PrototypeProgress start = default;
        Rejected(start, ActionResult.InvalidTarget, start.Mine((Deposit)127, out var invalidDeposit), invalidDeposit);
        Rejected(start, ActionResult.InvalidTarget, start.Build((BeaconModule)127, out var invalidModule), invalidModule);
        Rejected(start, ActionResult.InsufficientStone, start.Build(BeaconModule.Foundation, out var unaffordable), unaffordable);
        Rejected(start, ActionResult.BeaconIncomplete, start.Activate(out var earlySignal), earlySignal);

        var west = Mine(start, Deposit.West);
        Rejected(west, ActionResult.AlreadyMined, west.Mine(Deposit.West, out var duplicate), duplicate);
        Rejected(west, ActionResult.OutOfOrder, west.Build(BeaconModule.Mast, out var outOfOrder), outOfOrder);
        var foundation = Build(west, BeaconModule.Foundation);
        Rejected(foundation, ActionResult.BeaconIncomplete, foundation.Activate(out var partialSignal), partialSignal);
        Rejected(foundation, ActionResult.InsufficientStone, foundation.Build(BeaconModule.Mast, out var missingStone), missingStone);

        var online = RunRoute()[^1];
        Rejected(online, ActionResult.AlreadyOnline, online.Activate(out var repeated), repeated);
    }

    private static void CapturedProgressSurvivesLaterActions()
    {
        var captured = Mine(default, Deposit.Seam);
        var live = Build(Mine(captured, Deposit.East), BeaconModule.Foundation);
        Checkpoint(captured, DepositMask.Seam, 1, 0, BeaconModule.Foundation, false);
        Checkpoint(live, DepositMask.Seam | DepositMask.East, 1, 1, BeaconModule.Mast, false);
    }

    private static PrototypeProgress Mine(PrototypeProgress state, Deposit deposit)
    {
        Equal(ActionResult.Applied, state.Mine(deposit, out var next), $"mine {deposit}");
        return next;
    }

    private static PrototypeProgress Build(PrototypeProgress state, BeaconModule module)
    {
        Equal(ActionResult.Applied, state.Build(module, out var next), $"build {module}");
        return next;
    }

    private static void Rejected(PrototypeProgress before, ActionResult expected, ActionResult actual, PrototypeProgress after)
    {
        Equal(expected, actual, "rejected action result");
        Equal(before, after, $"progress after {expected}");
    }

    private static void Checkpoint(PrototypeProgress state, DepositMask deposits, int stone, int installed,
        BeaconModule? next, bool online)
    {
        Equal(deposits, state.MinedDeposits, "mined deposits");
        Equal(stone, state.Stone, "stone");
        Equal(installed, state.InstalledModules, "installed modules");
        Equal(next, state.NextModule, "next module");
        Equal(online, state.BeaconOnline, "beacon online");
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, found {actual}.");
    }
}
