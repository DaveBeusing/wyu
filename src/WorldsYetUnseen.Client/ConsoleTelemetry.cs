using System.Globalization;
using System.Text;
using Groundwork.Rendering;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed class ConsoleTelemetry(TimeProvider clock, Func<string>? traceContext = null)
{
    private const int DefaultSeconds = 5;
    private const int ConsoleEventLimit = 4;
    private const int RetainedEventLimit = 128;
    private const double SlowObservationMilliseconds = 33;

    private enum CaptureKind { None, Watch, Trace }

    private readonly record struct TraceEvent(double Seconds, VolumeEventKind? TerrainKind = null,
        double ObservationGap = 0, ulong Replaced = 0, ulong Skipped = 0);

    private readonly TraceEvent[] _events = new TraceEvent[RetainedEventLimit];
    private CaptureKind _capture;
    private int _seconds;
    private object? _renderer;
    private RendererSnapshot _baseline;
    private RendererSnapshot _previous;
    private long _started;
    private long _lastPoll;
    private long _lastAdvance;
    private int _polls;
    private int _observedAdvances;
    private double _maxObservationGap;
    private double _maxObservationGapAt;
    private double _maxPollGap;
    private VolumeResources _samplePeak;
    private int _eventCount;
    private string? _traceStartContext;

    internal string? LastTraceReport { get; private set; }
    internal bool IsActive => _capture != CaptureKind.None;
    internal bool IsTracing => _capture == CaptureKind.Trace;

    internal string HandleWatch(string seconds) => Handle(CaptureKind.Watch, seconds);
    internal string HandleTrace(string seconds) => Handle(CaptureKind.Trace, seconds);

    private string Handle(CaptureKind kind, string argument)
    {
        if (argument == "cancel")
            return _capture == kind ? End($"{Label(kind)} cancelled.") : $"No active {Command(kind)}.";

        var seconds = DefaultSeconds;
        if (argument.Length != 0 &&
            (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
             seconds is not (>= 1 and <= 30)))
            return $"{Command(kind)} [1–30]\n{Command(kind)} cancel";
        if (IsActive)
            return $"{Label(_capture)} already running.";

        _capture = kind;
        _seconds = seconds;
        _samplePeak = default;
        _polls = _observedAdvances = _eventCount = 0;
        _maxObservationGap = _maxObservationGapAt = _maxPollGap = 0;
        return FormattableString.Invariant($"{Label(kind)} for {seconds} s.\nClose console to move.");
    }

    internal string? Observe(object renderer, VolumeStreamingStatistics terrain, RendererSnapshot snapshot)
    {
        if (!IsActive)
            return null;
        if (terrain.Stopped)
            return End($"{Label(_capture)} stopped.\nTerrain stream stopped.");

        var now = clock.GetTimestamp();
        if (_renderer is null)
        {
            if (IsTracing)
                _traceStartContext = traceContext?.Invoke();
            _renderer = renderer;
            _baseline = _previous = snapshot;
            _started = _lastPoll = _lastAdvance = now;
            _samplePeak = terrain.Used;
            _polls = 1;
            return null;
        }

        if (!ReferenceEquals(_renderer, renderer) ||
            snapshot.AppliedWidth != _baseline.AppliedWidth || snapshot.AppliedHeight != _baseline.AppliedHeight)
            return End($"{Label(_capture)} stopped.\nRender view changed.");

        var elapsed = clock.GetElapsedTime(_started, now).TotalSeconds;
        _polls++;
        var complete = elapsed >= _seconds;
        if (IsTracing)
        {
            if (snapshot.LastPresentedSequence < _previous.LastPresentedSequence ||
                snapshot.ReplacedFrames < _previous.ReplacedFrames || snapshot.SkippedFrames < _previous.SkippedFrames)
                return End("Profile trace stopped.\nRender counters changed.");

            ObserveTrace(now, elapsed, snapshot, complete);
        }
        else
            _samplePeak = VolumeResources.Peak(_samplePeak, terrain.Used);

        if (!complete)
            return null;

        return End(IsTracing ? CompleteTrace(elapsed, snapshot) : DescribeWatch(elapsed));
    }

    private void ObserveTrace(long now, double elapsed, RendererSnapshot snapshot, bool complete)
    {
        // IDs may skip; every poll can observe at most one new acknowledged sequence.
        var advanced = snapshot.LastPresentedSequence > _previous.LastPresentedSequence;
        var observationGap = clock.GetElapsedTime(_lastAdvance, now).TotalMilliseconds;
        if (observationGap > _maxObservationGap)
        {
            _maxObservationGap = observationGap;
            _maxObservationGapAt = elapsed;
        }
        if (advanced)
        {
            _observedAdvances++;
            _lastAdvance = now;
        }

        var pollGap = clock.GetElapsedTime(_lastPoll, now).TotalMilliseconds;
        _maxPollGap = Math.Max(_maxPollGap, pollGap);
        var replaced = snapshot.ReplacedFrames - _previous.ReplacedFrames;
        var skipped = snapshot.SkippedFrames - _previous.SkippedFrames;
        var slowGap = (advanced || complete) && observationGap > SlowObservationMilliseconds;
        if (slowGap || replaced != 0 || skipped != 0)
            AddEvent(new(elapsed, ObservationGap: slowGap ? observationGap : 0, Replaced: replaced, Skipped: skipped));
        _lastPoll = now;
        _previous = snapshot;
    }

    internal void RecordTerrainEvent(VolumeEventKind kind)
    {
        // A normal walking commit occurs every tick. Keep the trace useful for
        // coverage, admission, rejection and edit events.
        if (!IsTracing || _renderer is null || kind == VolumeEventKind.MovementCommit)
            return;

        var elapsed = clock.GetElapsedTime(_started, clock.GetTimestamp()).TotalSeconds;
        AddEvent(new(elapsed, TerrainKind: kind));
    }

    private void AddEvent(TraceEvent entry)
    {
        _events[_eventCount % RetainedEventLimit] = entry;
        _eventCount++;
    }

    private string DescribeWatch(double elapsed)
    {
        const double MiB = 1024d * 1024d;
        return FormattableString.Invariant(
            $"Stream watch {elapsed:0.0} s\n{_polls} samples · peak charges\nCPU {_samplePeak.CpuBytes / MiB:0.0} MiB\nGPU {_samplePeak.NativeBytes / MiB:0.0} MiB\nQueue {_samplePeak.QueueSlots}\nDensity {_samplePeak.DensityJobs}\nMesh {_samplePeak.MeshJobs}");
    }

    private string CompleteTrace(double elapsed, RendererSnapshot snapshot)
    {
        var summary = FormattableString.Invariant(
            $"Trace {elapsed:0.0} s · {_polls} polls\nObserved advances {_observedAdvances}\nPoll gap max {_maxPollGap:0.0} ms\nObs max {_maxObservationGap:0.#}ms @{_maxObservationGapAt:0.0#}s\nReplaced {snapshot.ReplacedFrames - _baseline.ReplacedFrames} / skipped {snapshot.SkippedFrames - _baseline.SkippedFrames}");
        var transcript = DescribeTrace(summary, ConsoleEventLimit, "shown");
        var report = new StringBuilder("WYU profile trace\n");
        if (!string.IsNullOrEmpty(_traceStartContext))
            report.Append("\nAt start\n").Append(_traceStartContext).Append('\n');
        report.Append('\n').Append(DescribeTrace(summary, RetainedEventLimit, "retained"));
        LastTraceReport = report.ToString();
        return transcript;
    }

    private string DescribeTrace(string summary, int eventLimit, string eventLabel)
    {
        var shown = Math.Min(_eventCount, eventLimit);
        var text = new StringBuilder(summary);
        text.Append('\n').Append(FormattableString.Invariant($"Events {eventLabel} {shown}/{_eventCount}"));
        for (var i = _eventCount - shown; i < _eventCount; i++)
        {
            var entry = _events[i % RetainedEventLimit];
            text.Append('\n').Append(DescribeEvent(entry));
        }
        return text.ToString();
    }

    private static string DescribeEvent(TraceEvent entry)
    {
        if (entry.TerrainKind is { } kind)
            return FormattableString.Invariant($"+{entry.Seconds:0.00}s {kind}");

        var gap = entry.ObservationGap > 0 ? FormattableString.Invariant($" {entry.ObservationGap:0}ms gap") : "";
        var drops = entry.Replaced != 0 || entry.Skipped != 0
            ? FormattableString.Invariant($" R{entry.Replaced} S{entry.Skipped}") : "";
        return FormattableString.Invariant($"+{entry.Seconds:0.00}s{gap}{drops}");
    }

    internal string? Interrupt() => IsActive ? End($"{Label(_capture)} interrupted.") : null;

    private string End(string result)
    {
        _capture = CaptureKind.None;
        _renderer = null;
        _traceStartContext = null;
        return result;
    }

    private static string Label(CaptureKind kind) => kind == CaptureKind.Trace ? "Profile trace" : "Stream watch";

    private static string Command(CaptureKind kind) => kind == CaptureKind.Trace ? "profile trace" : "stream watch";
}
