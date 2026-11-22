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
    private const int RepeatSampleSeconds = 5;
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
    private string? _repeatSummary;
    private Comparison? _comparison;

    private sealed class Comparison(int pairs, Func<ulong> applyA, Func<ulong> applyB, Action restore)
    {
        internal Action Restore { get; } = restore;
        internal Measurement[] A { get; } = new Measurement[pairs];
        internal Measurement[] B { get; } = new Measurement[pairs];
        internal int CompletedSamples { get; private set; }
        internal int Pair => CompletedSamples / 2;
        internal int Pairs => A.Length;
        // Even pairs run A then B, odd pairs B then A.
        internal bool IsSampleA => CompletedSamples % 2 == Pair % 2;
        internal Func<ulong> Apply => IsSampleA ? applyA : applyB;
        internal void Record(Measurement measurement)
        {
            (IsSampleA ? A : B)[Pair] = measurement;
            CompletedSamples++;
        }
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

    internal string State()
    {
        if (_comparison is not { } comparison)
            return _seconds == 0 ? "Profile idle." : "Profile running.";

        var phase = comparison.Warming ? "warmup" : "sampling";
        var sample = comparison.IsSampleA ? 'A' : 'B';
        return comparison.Pairs == 1 ? $"A/B {phase} {sample}."
            : FormattableString.Invariant($"A/B {phase} {sample} {comparison.Pair + 1}/{comparison.Pairs}.");
    }

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

        BeginComparison(seconds, 1, applyA, applyB, restore);
        return FormattableString.Invariant($"A/B sampling {seconds} s each.\nKeep console open.\nEsc cancels.");
    }

    internal string StartRepeatedComparison(int count, Func<ulong> applyA, Func<ulong> applyB, Action restore)
    {
        if (_seconds != 0)
            return State();
        if (count is not (>= 2 and <= 6))
            return "A/B repeats: 2–6.";

        BeginComparison(RepeatSampleSeconds, count, applyA, applyB, restore);
        return FormattableString.Invariant($"A/B repeat {count} pairs.\n{RepeatSampleSeconds} s per sample.\nKeep console open.\nEsc cancels.");
    }

    private void BeginComparison(int seconds, int pairs, Func<ulong> applyA, Func<ulong> applyB, Action restore)
    {
        var comparison = new Comparison(pairs, applyA, applyB, restore);
        _comparison = comparison;
        _seconds = seconds;
        ApplyComparisonSettings(comparison);
    }

    private void ApplyComparisonSettings(Comparison comparison)
    {
        comparison.Warming = true;
        comparison.AppliedAt = clock.GetTimestamp();
        comparison.WarmupStarted = null;
        try
        {
            comparison.MinimumSequence = comparison.Apply();
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
        _repeatSummary = null;
        return FormattableString.Invariant($"Kept {result.Seconds:0.0} s in {name}.");
    }

    private string Compare()
    {
        if (_a is not { } a || _b is not { } b)
            return _a is null && _b is null ? "Keep profiles in A and B."
                : _a is null ? "Slot A is empty." : "Slot B is empty.";

        return _repeatSummary ?? DescribeComparison(a, b);
    }

    internal string? ReadComparisonReport(string build)
    {
        if (_a is not { } a || _b is not { } b)
            return null;

        var comparison = DescribeComparison(a, b);
        if (_repeatSummary is { } summary)
            comparison = $"{summary}\n\nLast pair\n{comparison}";
        return $"WYU A/B performance report\nBuild {build}\n\n{comparison}\n\nA at start\n{a.StartContext}\n\nB at start\n{b.StartContext}";
    }

    private static string DescribeRepeat(Comparison comparison) =>
        FormattableString.Invariant(
            $"A/B repeat {comparison.Pairs} pairs\nObserved FPS\n{DescribeFps("A", comparison.A)}\n{DescribeFps("B", comparison.B)}");

    private static string DescribeFps(string label, Measurement[] samples)
    {
        var values = samples.Select(sample => sample.ObservedFps).Order().ToArray();
        var median = (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
        return FormattableString.Invariant(
            $"{label} median {median:0.0}\n{label} min–max {values[0]:0.0}–{values[^1]:0.0}");
    }

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

        comparison.Record(measurement);
        if (comparison.Pair < comparison.Pairs)
        {
            ApplyComparisonSettings(comparison);
            return null;
        }

        var a = comparison.A[^1];
        var b = comparison.B[^1];
        var summary = comparison.Pairs == 1 ? null : DescribeRepeat(comparison);
        var result = summary ?? DescribeComparison(a, b);
        Interrupt(result);
        (_a, _b, _last, _repeatSummary) = (a, b, measurement, summary);
        return result;
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
