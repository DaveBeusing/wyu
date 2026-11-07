using System.Numerics;

namespace WorldsYetUnseen.Gameplay;

public enum Deposit
{
    West,
    Seam,
    East
}

[Flags]
public enum DepositMask
{
    None = 0,
    West = 1,
    Seam = 2,
    East = 4
}

public enum BeaconModule
{
    Foundation,
    Mast,
    SignalHead
}

public enum ActionResult
{
    Applied,
    InvalidTarget,
    AlreadyMined,
    OutOfOrder,
    InsufficientStone,
    BeaconIncomplete,
    AlreadyOnline
}

/// <summary>
/// An immutable snapshot of WP07 progress. The default value is a new game.
/// Action methods return a candidate for a target already qualified by the caller.
/// </summary>
public readonly record struct PrototypeProgress
{
    private PrototypeProgress(DepositMask minedDeposits, int installedModules, bool beaconOnline)
    {
        MinedDeposits = minedDeposits;
        InstalledModules = installedModules;
        BeaconOnline = beaconOnline;
    }

    public DepositMask MinedDeposits { get; }
    public int InstalledModules { get; }
    public bool BeaconOnline { get; }
    public int Stone => BitOperations.PopCount((uint)MinedDeposits) - InstalledModules;

    public BeaconModule? NextModule => InstalledModules switch
    {
        0 => BeaconModule.Foundation,
        1 => BeaconModule.Mast,
        2 => BeaconModule.SignalHead,
        _ => null
    };

    /// <summary>Removes a deposit and credits its stone in the same candidate.</summary>
    public ActionResult Mine(Deposit deposit, out PrototypeProgress next)
    {
        next = this;
        var bit = deposit switch
        {
            Deposit.West => DepositMask.West,
            Deposit.Seam => DepositMask.Seam,
            Deposit.East => DepositMask.East,
            _ => DepositMask.None
        };
        if (bit == DepositMask.None)
            return ActionResult.InvalidTarget;
        if ((MinedDeposits & bit) != 0)
            return ActionResult.AlreadyMined;

        next = new PrototypeProgress(MinedDeposits | bit, InstalledModules, BeaconOnline);
        return ActionResult.Applied;
    }

    /// <summary>Installs the next module and spends one stone in the same candidate.</summary>
    public ActionResult Build(BeaconModule module, out PrototypeProgress next)
    {
        next = this;
        if (module is not (BeaconModule.Foundation or BeaconModule.Mast or BeaconModule.SignalHead))
            return ActionResult.InvalidTarget;
        if (module != NextModule)
            return ActionResult.OutOfOrder;
        if (Stone == 0)
            return ActionResult.InsufficientStone;

        next = new PrototypeProgress(MinedDeposits, InstalledModules + 1, BeaconOnline);
        return ActionResult.Applied;
    }

    /// <summary>Marks the objective complete after all modules have been installed.</summary>
    public ActionResult Activate(out PrototypeProgress next)
    {
        next = this;
        if (NextModule is not null)
            return ActionResult.BeaconIncomplete;
        if (BeaconOnline)
            return ActionResult.AlreadyOnline;

        next = new PrototypeProgress(MinedDeposits, InstalledModules, beaconOnline: true);
        return ActionResult.Applied;
    }
}
