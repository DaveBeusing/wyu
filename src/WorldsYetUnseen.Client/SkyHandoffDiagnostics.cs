using System.Diagnostics;

namespace WorldsYetUnseen.Client;

// Prepare outside the acceptance interval. Stage recording stores values only;
// serialization and console output happen after the native Present endpoint.
internal static class SkyHandoffDiagnostics
{
    private const int Capacity = 64;
    private static readonly Lock Gate = new();
    private static readonly Stage[] Stages = new Stage[Capacity];
    internal static bool Enabled { get; } = Environment.GetEnvironmentVariable("WYU_SKY_DIAGNOSTICS") == "1";
    private static bool _active;
    private static int _count;
    private static string _direction = "";
    private static long _start;
    private static bool _exitRegistered;

    internal static void Prepare()
    {
        if (Enabled)
            lock (Gate)
            {
                if (!_exitRegistered)
                {
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    _exitRegistered = true;
                }
            }
    }

    internal static void Begin(string direction, long startQpc)
    {
        if (!Enabled)
            return;
        lock (Gate)
        {
            if (_active)
                throw new InvalidOperationException("The previous diagnostic handoff has no native Present endpoint.");
            _direction = direction;
            _start = startQpc;
            _count = 0;
            _active = true;
        }
    }

    internal static void Mark(string stage, ulong sequence = 0)
    {
        if (!Enabled)
            return;
        lock (Gate)
        {
            if (!_active)
                return;
            if (_count == Capacity)
                throw new InvalidOperationException("Sky handoff diagnostic stage capacity was exhausted.");
            Stages[_count++] = new(stage, Stopwatch.GetTimestamp(), sequence);
        }
    }

    internal static void Complete(int owner, ulong frameSequence, ulong presentFence, long presentQpc)
    {
        if (!Enabled)
            return;
        lock (Gate)
        {
            if (!_active)
                throw new InvalidOperationException("A diagnostic native Present has no active handoff.");
            _active = false;
            Dump("COMPLETE", owner, frameSequence, presentFence, presentQpc);
        }
    }

    internal static void FlushIncomplete()
    {
        if (!Enabled)
            return;
        try
        {
            lock (Gate)
            {
                if (!_active)
                    return;
                _active = false;
                Dump("INCOMPLETE", null, null, null, null);
            }
        }
        catch (Exception error)
        {
            // Preserve the primary shutdown/failure outcome if its diagnostic
            // sink is also unavailable. Process-exit delivery is best effort.
            try { Console.Error.WriteLine($"Incomplete sky handoff diagnostic delivery failed: {error.Message}"); }
            catch (Exception) { }
        }
    }

    private static void OnProcessExit(object? sender, EventArgs arguments) => FlushIncomplete();

    private static void Dump(string status, int? owner, ulong? frameSequence, ulong? presentFence, long? end)
    {
        for (var i = 0; i < _count; i++)
        {
            var stage = Stages[i];
            SkyMeasurements.Write(new { kind = "handoff-stage", status, finalDiagnosticOnly = true,
                direction = _direction, owner, start = _start, end, presentFence, frameSequence,
                stage = stage.Name, timestamp = stage.Timestamp, sequence = stage.Sequence,
                outsideEndpoint = end.HasValue && stage.Timestamp > end.Value, frequency = Stopwatch.Frequency });
        }
    }

    private readonly record struct Stage(string Name, long Timestamp, ulong Sequence);
}
