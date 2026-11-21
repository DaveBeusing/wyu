using System.Globalization;
using Groundwork.Rendering;

namespace WorldsYetUnseen.Client;

internal sealed class ConsoleFrameProfile(TimeProvider clock)
{
    private const string Usage = "Usage: profile [1-30|cancel]";
    private const double SlowPollMilliseconds = 33;
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
    private string? _result;

    internal string State() => _seconds == 0 ? "Profile idle." : "Profile running.";

    internal string Handle(string value)
    {
        if (value.Length == 0)
            return _seconds != 0 ? State() : _result ?? "Use profile 10 to start.";
        if (value == "cancel")
            return Interrupt("Profile cancelled.") ?? "No active profile.";
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is not (>= 1 and <= 30))
            return Usage;
        if (_seconds != 0)
            return State();

        _seconds = seconds;
        return FormattableString.Invariant($"Profiling for {seconds} s.\nEsc to move.");
    }

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
            _started = _lastPoll = now;
            _observedPresents = _slowPolls = 0;
            _maxPollGap = 0;
            return null;
        }
        if (!ReferenceEquals(_renderer, renderer) ||
            snapshot.AppliedWidth != _baseline.AppliedWidth || snapshot.AppliedHeight != _baseline.AppliedHeight ||
            snapshot.LastPresentedSequence < _previous.LastPresentedSequence ||
            snapshot.ReplacedFrames < _previous.ReplacedFrames || snapshot.SkippedFrames < _previous.SkippedFrames)
            return Interrupt("Profile surface changed.");

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
        _result = FormattableString.Invariant(
            $"Profile {elapsed:0.0} s\nObserved FPS: {_observedPresents / elapsed:0.0}\nMax poll gap: {_maxPollGap:0.0} ms\nPolls >33 ms: {_slowPolls}\nReplaced: {replaced}\nSkipped: {skipped}");
        return Interrupt(_result);
    }

    internal string? Interrupt(string message)
    {
        if (_seconds == 0)
            return null;

        _seconds = 0;
        _renderer = null;
        return message;
    }
}
