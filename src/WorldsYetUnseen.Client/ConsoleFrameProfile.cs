using System.Globalization;
using Groundwork.Rendering;

namespace WorldsYetUnseen.Client;

internal sealed class ConsoleFrameProfile(TimeProvider clock, Func<string> describeStart) : IDisposable
{
    internal const string Usage = "profile [n|cancel]";
    internal const string ComparisonUsage = "profile keep <a|b>\nprofile compare";
    private const double SlowPollMilliseconds = 33;
    private const double WarmupSeconds = 1;
    private const double FinalPollGraceSeconds = 0.25;
    private int _seconds;
    private object? _renderer;
    private RendererSnapshot _baseline;
    private RendererSnapshot _previous;
    private long _started;
    private long _lastPoll;
    private int _observedPresents;
    private int _slowPolls;
    private double _maxPollGap;
    private string? _startContext;
    private Measurement? _last;
    private Measurement? _a;
    private Measurement? _b;
    private Comparison? _comparison;

    private sealed class Comparison(Func<ulong> applyB, Action restore)
    {
        internal Func<ulong> ApplyB { get; } = applyB;
        internal Action Restore { get; } = restore;
        internal Measurement? First { get; set; }
        internal long AppliedAt { get; set; }
        internal ulong MinimumSequence { get; set; }
        internal long? WarmupStarted { get; set; }
        internal bool Warming { get; set; } = true;
    }

    private readonly record struct Measurement(double Seconds, int Presents, double MaxGap, int SlowPolls,
        ulong Replaced, ulong Skipped, string StartContext)
    {
        internal double ObservedFps => Presents / Seconds;

        internal string Describe() => FormattableString.Invariant(
            $"Profile {Seconds:0.0} s\nObserved FPS: {ObservedFps:0.0}\nMax poll gap: {MaxGap:0.0} ms\nPolls >33 ms: {SlowPolls}\nReplaced: {Replaced}\nSkipped: {Skipped}");
    }

    internal bool IsComparing => _comparison is not null;

    internal string State() => _comparison is { } comparison
        ? $"A/B {(comparison.Warming ? "warmup" : "sampling")} {(comparison.First.HasValue ? "B" : "A")}."
        : _seconds == 0 ? "Profile idle." : "Profile running.";

    internal string Handle(string value)
    {
        if (value.Length == 0)
            return _seconds != 0 ? State() : _last?.Describe() ?? "Use profile 10 to start.";
        if (value == "cancel")
            return Interrupt("Profile cancelled.") ?? "No active profile.";
        if (value == "keep a")
            return Keep(ref _a, "A");
        if (value == "keep b")
            return Keep(ref _b, "B");
        if (value == "compare")
            return Compare();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is not (>= 1 and <= 30))
            return $"Usage: {Usage}\n{ComparisonUsage}";
        if (_seconds != 0)
            return State();

        _seconds = seconds;
        return FormattableString.Invariant($"Profiling for {seconds} s.\nEsc to move.");
    }

    internal string StartComparison(int seconds, Func<ulong> applyA, Func<ulong> applyB, Action restore)
    {
        if (_seconds != 0)
            return State();
        if (seconds is not (>= 1 and <= 30))
            return "A/B duration: 1–30 s.";

        var comparison = new Comparison(applyB, restore);
        _comparison = comparison;
        _seconds = seconds;
        ApplyComparisonSettings(comparison, applyA);
        return FormattableString.Invariant($"A/B sampling {seconds} s each.\nKeep console open.\nEsc cancels.");
    }

    private void ApplyComparisonSettings(Comparison comparison, Func<ulong> apply)
    {
        comparison.Warming = true;
        comparison.AppliedAt = clock.GetTimestamp();
        comparison.WarmupStarted = null;
        try
        {
            comparison.MinimumSequence = apply();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private string Keep(ref Measurement? slot, string name)
    {
        if (IsComparing)
            return State();
        if (_last is not { } result)
            return "Complete a profile first.";

        slot = result;
        return FormattableString.Invariant($"Kept {result.Seconds:0.0} s in {name}.");
    }

    private string Compare()
    {
        if (_a is not { } a || _b is not { } b)
            return _a is null && _b is null ? "Keep profiles in A and B."
                : _a is null ? "Slot A is empty." : "Slot B is empty.";

        return DescribeComparison(a, b);
    }

    internal string? ReadComparisonReport(string build) =>
        _a is { } a && _b is { } b
            ? $"WYU A/B performance report\nBuild {build}\n\n{DescribeComparison(a, b)}\n\nA at start\n{a.StartContext}\n\nB at start\n{b.StartContext}"
            : null;

    private static string DescribeComparison(Measurement a, Measurement b) =>
        FormattableString.Invariant(
            $"A {a.Seconds:0.0}s | B {b.Seconds:0.0}s\nObs FPS {a.ObservedFps:0.0} | {b.ObservedFps:0.0}\nMax ms {a.MaxGap:0.0} | {b.MaxGap:0.0}\nSlow polls/s {a.SlowPolls / a.Seconds:0.0} | {b.SlowPolls / b.Seconds:0.0}\nReplaced/s {a.Replaced / a.Seconds:0.0} | {b.Replaced / b.Seconds:0.0}\nSkipped/s {a.Skipped / a.Seconds:0.0} | {b.Skipped / b.Seconds:0.0}");

    internal string? Observe(object renderer, RendererSnapshot snapshot, bool eligible)
    {
        if (_seconds == 0)
            return null;
        if (!eligible)
            return Interrupt("Profile lost active view.");

        var now = clock.GetTimestamp();
        if (_renderer is null)
        {
            _renderer = renderer;
            _baseline = _previous = snapshot;
            if (!IsComparing)
            {
                BeginMeasurement(now, snapshot);
                return null;
            }
        }
        else if (!ReferenceEquals(_renderer, renderer) ||
            snapshot.AppliedWidth != _baseline.AppliedWidth || snapshot.AppliedHeight != _baseline.AppliedHeight ||
            snapshot.LastPresentedSequence < _previous.LastPresentedSequence ||
            snapshot.ReplacedFrames < _previous.ReplacedFrames || snapshot.SkippedFrames < _previous.SkippedFrames)
            return Interrupt("Profile surface changed.");

        if (_comparison is { Warming: true } warmup)
        {
            _previous = snapshot;
            if (warmup.WarmupStarted is not { } warmupStarted)
            {
                if (clock.GetElapsedTime(warmup.AppliedAt, now).TotalSeconds > WarmupSeconds + FinalPollGraceSeconds)
                    return Interrupt("A/B frame not acknowledged.");
                if (snapshot.LastPresentedSequence >= warmup.MinimumSequence)
                {
                    warmup.WarmupStarted = now;
                    _baseline = snapshot;
                }
                return null;
            }

            var warmupElapsed = clock.GetElapsedTime(warmupStarted, now).TotalSeconds;
            if (warmupElapsed > WarmupSeconds + FinalPollGraceSeconds)
                return Interrupt("A/B warmup deadline missed.");

            if (warmupElapsed < WarmupSeconds)
                return null;
            if (snapshot.LastPresentedSequence == _baseline.LastPresentedSequence)
                return Interrupt("A/B warmup stopped.\nNo observed presents.");

            warmup.Warming = false;
            BeginMeasurement(now, snapshot);
            return null;
        }

        var elapsed = clock.GetElapsedTime(_started, now).TotalSeconds;
        if (elapsed > _seconds + FinalPollGraceSeconds)
            return Interrupt("Profile deadline missed.");

        // Sequence numbers are frame IDs. A poll can establish only one observed advance.
        if (snapshot.LastPresentedSequence > _previous.LastPresentedSequence)
            _observedPresents++;

        var gap = clock.GetElapsedTime(_lastPoll, now).TotalMilliseconds;
        if (gap > SlowPollMilliseconds)
            _slowPolls++;
        _maxPollGap = Math.Max(_maxPollGap, gap);
        _lastPoll = now;
        _previous = snapshot;

        if (elapsed < _seconds)
            return null;

        var replaced = snapshot.ReplacedFrames - _baseline.ReplacedFrames;
        var skipped = snapshot.SkippedFrames - _baseline.SkippedFrames;
        var measurement = new Measurement(elapsed, _observedPresents, _maxPollGap, _slowPolls, replaced, skipped, _startContext!);
        if (_comparison is not { } comparison)
        {
            _last = measurement;
            return Interrupt(measurement.Describe());
        }
        if (_observedPresents == 0)
            return Interrupt("A/B sample stopped.\nNo observed presents.");

        if (comparison.First is { } first)
        {
            var result = DescribeComparison(first, measurement);
            Interrupt(result);
            (_a, _b, _last) = (first, measurement, measurement);
            return result;
        }

        comparison.First = measurement;
        ApplyComparisonSettings(comparison, comparison.ApplyB);
        return null;
    }

    private void BeginMeasurement(long now, RendererSnapshot snapshot)
    {
        _startContext = describeStart();
        _baseline = _previous = snapshot;
        _started = _lastPoll = now;
        _observedPresents = _slowPolls = 0;
        _maxPollGap = 0;
    }

    internal string? Interrupt(string message)
    {
        if (_seconds == 0)
            return null;

        var comparison = _comparison;
        _comparison = null;
        _seconds = 0;
        _renderer = null;
        _startContext = null;
        comparison?.Restore();
        return message;
    }

    public void Dispose() => Interrupt("Profile cancelled.");
}
