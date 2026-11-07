using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed class PrototypeSession(WalkableScene scene)
{
    private PrototypeProgress? _pending;
    private string? _notice;
    private PrototypeTarget _lastTarget;

    internal PrototypeProgress Progress { get; private set; }
    internal bool Paused { get; set; }
    internal string Caption { get; private set; } = "WYU | Find stone and build the rescue beacon";

    internal bool Ready
    {
        get
        {
            using var world = scene.TryReadPresented();
            return _pending is null && world is not null;
        }
    }

    internal void Advance(float yaw, float pitch, bool interact, bool drawable, bool showObjective)
    {
        PrototypeTarget target = default;
        using (var world = scene.TryReadPresented())
        {
            if (world is not null && _pending is { } candidate)
            {
                Progress = candidate;
                _pending = null;
                _notice = null;
            }

            if (world is not null && drawable && !Paused)
                target = PrototypeWorld.Target(world, scene.Pose, yaw, pitch);
        }

        if (target != _lastTarget)
        {
            _lastTarget = target;
            _notice = null;
        }
        if (interact && drawable && !Paused && _pending is null)
            Interact(target);
        if (showObjective)
            Caption = Describe(target, yaw);
    }

    private void Interact(PrototypeTarget target)
    {
        ActionResult result;
        PrototypeProgress candidate;
        if (target.Deposit is { } deposit)
            result = Progress.Mine(deposit, out candidate);
        else if (target.IsPad && Progress.NextModule is { } module)
        {
            if (PrototypeWorld.IsSocketOccupied(module, scene.Pose))
            {
                _notice = "Step clear of the socket";
                return;
            }
            result = Progress.Build(module, out candidate);
        }
        else if (target.IsPad)
        {
            if (Progress.Activate(out candidate) == ActionResult.Applied)
                Progress = candidate;
            _notice = null;
            return;
        }
        else
        {
            _notice = "No target in reach and in sight";
            return;
        }

        if (result != ActionResult.Applied)
        {
            _notice = result == ActionResult.InsufficientStone ? "Mine more stone" : "Action unavailable";
            return;
        }

        if (scene.TryStageTerrain(PrototypeWorld.EffectiveTerrain(candidate)))
        {
            _pending = candidate;
            _notice = null;
        }
        else
            _notice = "Wait for terrain";
    }

    private string Describe(PrototypeTarget target, float yaw)
    {
        if (Paused)
            return "WYU | Paused | Esc resume | Close window to quit";
        if (Progress.BeaconOnline)
            return "WYU | Rescue beacon online | Objective complete | Esc pause";

        var collected = BitOperations.PopCount((uint)Progress.MinedDeposits);
        var action = _pending is not null ? "Updating terrain"
            : scene.TerrainChangePending ? "Waiting for terrain"
            : target.Deposit is not null ? "E: mine stone"
            : target.IsPad && Progress.NextModule is { } module
                ? Progress.Stone == 0 ? "Mine more stone" : $"E: build {ModuleName(module)} (1 stone)"
            : target.IsPad ? "E: send rescue signal"
            : Guidance(yaw);
        var suffix = _notice is not null && _pending is null
            ? $" | {_notice}" : " | WASD move / arrows aim / E interact / Esc pause";
        return $"Rescue beacon | Stone {Progress.Stone} | Deposits {collected}/3 | {action}{suffix}";
    }

    private string Guidance(float yaw)
    {
        var eye = PrototypeWorld.Eye(scene.Pose);
        var waypoint = PrototypeWorld.PadAnchor;
        var objective = Progress.NextModule is null ? "Activate beacon" : "Return to beacon pad";
        var nearest = float.PositiveInfinity;
        foreach (var deposit in PrototypeWorld.Deposits)
        {
            if ((Progress.MinedDeposits & PrototypeWorld.DepositBit(deposit)) != 0)
                continue;
            var center = PrototypeWorld.DepositCenter(deposit);
            var distance = Vector3.DistanceSquared(eye, center);
            if (distance >= nearest)
                continue;
            nearest = distance;
            waypoint = center;
            objective = $"Find {deposit.ToString().ToLowerInvariant()} deposit";
        }

        var offset = waypoint - eye;
        var bearing = MathF.IEEERemainder(MathF.Atan2(offset.Z, offset.X) - yaw, MathF.Tau);
        var direction = MathF.Abs(bearing) < MathF.PI / 8 ? "ahead" :
            MathF.Abs(bearing) > 7 * MathF.PI / 8 ? "behind" : bearing > 0 ? "right" : "left";
        var range = MathF.Round(MathF.Sqrt(offset.X * offset.X + offset.Z * offset.Z));
        return $"{objective}: {direction}, {range:0} vox";
    }

    private static string ModuleName(BeaconModule module) =>
        module == BeaconModule.SignalHead ? "signal head" : module.ToString().ToLowerInvariant();
}

// The platform owns and pumps the window. Bound caption updates so UI feedback cannot block shutdown.
internal sealed partial class PrototypeCaption
{
    private const uint SetText = 0x000C;
    private const uint AbortIfHung = 0x0002;
    private string? _last;

    internal void Update(string title)
    {
        if (title == _last)
            return;
        using var process = Process.GetCurrentProcess();
        var window = process.MainWindowHandle;
        if (window != 0 && SendMessageTimeout(window, SetText, 0, title, AbortIfHung, 10, out _) != 0)
            _last = title;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint window, uint message, nuint wParam, string text,
        uint flags, uint timeout, out nuint result);
}
