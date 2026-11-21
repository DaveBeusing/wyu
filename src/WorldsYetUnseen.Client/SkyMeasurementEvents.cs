using System.Diagnostics;
using Groundwork.Rendering.D3D12;

namespace WorldsYetUnseen.Client;

internal sealed partial class SkyMeasurements
{
    // One bounded allocation per renderer, before its measurement windows.
    // No JSON, console I/O, boxing or growing collections on the GPU owner.
    private const int EventCapacity = 8192;
    private static readonly Lock OwnersGate = new();
    private static readonly SkyMeasurements?[] EvidenceOwners = new SkyMeasurements[8];
    private static int _evidenceOwnerCount;
    private static bool _exitHandlerRegistered;
    private readonly Lock _eventGate = new();
    private readonly MeasurementEvent[] _events = new MeasurementEvent[EventCapacity];
    private int _eventCount;

    private static void RegisterEvidenceOwner(SkyMeasurements observer)
    {
        lock (OwnersGate)
        {
            if (!_exitHandlerRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAtProcessExit();
                _exitHandlerRegistered = true;
            }
            if (_evidenceOwnerCount == EvidenceOwners.Length)
                throw new InvalidOperationException("Sky evidence owner capacity exhausted; measurement is incomplete.");
            EvidenceOwners[_evidenceOwnerCount++] = observer;
        }
    }

    private static void FlushAtProcessExit()
    {
        // Best effort for startup/shutdown failures before a phase finally or
        // handoff endpoint. Forced OS termination can still prevent export.
        lock (OwnersGate)
            for (var i = 0; i < _evidenceOwnerCount; i++)
                try { EvidenceOwners[i]!.FlushEvents(); }
                catch (Exception error)
                {
                    try { Console.Error.WriteLine("Sky evidence flush failed: " + error); }
                    catch { /* Never replace the original process failure. */ }
                }
    }

    private static void FlushBeforeHandoff()
    {
        // Initial metadata is exported outside the next measured interval,
        // including flights that collect only handoffs, without held phases.
        lock (OwnersGate)
            for (var i = 0; i < _evidenceOwnerCount; i++)
                EvidenceOwners[i]!.FlushEvents();
    }

    private enum EventKind : byte { Gpu, Record, Prepare, Pipeline, GpuTicks, Calibration, Owner, UnboundPresent, LookupGpu, LookupRecord }
    private readonly record struct MeasurementEvent(EventKind Kind, string Phase, ulong Sequence, ulong Fence,
        long Qpc, double A = 0, double B = 0, long Allocated = 0, bool Flag = false,
        ulong T0 = 0, ulong T1 = 0, ulong T2 = 0, ulong T3 = 0, ulong Frequency = 0);

    private void Buffer(MeasurementEvent value)
    {
        lock (_eventGate)
        {
            if (_eventCount == _events.Length)
                throw new InvalidOperationException("Sky diagnostic event capacity exhausted; measurement is incomplete. No overwrite is permitted.");
            _events[_eventCount++] = value;
        }
    }

    private void PipelineReady(long started, long ended, bool reused) =>
        Buffer(new(EventKind.Pipeline, "startup", 0, 0, ended, Allocated: started, Flag: reused));

    private void ClockCalibration(ulong gpu, ulong cpu, ulong frequency) =>
        Buffer(new(EventKind.Calibration, "startup", 0, 0, Stopwatch.GetTimestamp(),
            T0: gpu, T1: cpu, Frequency: frequency));

    private void TimingDetails(ulong sequence, ulong fence, ulong first, ulong last, ulong skyFirst, ulong skyLast, ulong frequency)
    {
        var window = Volatile.Read(ref _window);
        if (window is not null && sequence >= window.First && sequence <= window.Last)
            Buffer(new(EventKind.GpuTicks, window.Phase, sequence, fence, Stopwatch.GetTimestamp(),
                T0: first, T1: last, T2: skyFirst, T3: skyLast, Frequency: frequency));
    }

    private void LookupGpuTiming(ulong sequence, ulong fence, double compute, double draw) =>
        BufferLookupTiming(EventKind.LookupGpu, sequence, fence, compute, draw);

    private void LookupRecordTiming(ulong sequence, ulong fence, double compute, double draw) =>
        BufferLookupTiming(EventKind.LookupRecord, sequence, fence, compute, draw);

    private void BufferLookupTiming(EventKind kind, ulong sequence, ulong fence, double compute, double draw)
    {
        var window = Volatile.Read(ref _window);
        if (window is not null && sequence >= window.First && sequence <= window.Last)
            Buffer(new(kind, window.Phase, sequence, fence, Stopwatch.GetTimestamp(), compute, draw));
    }

    internal static void Flush(D3D12Renderer renderer)
    {
        if (!Owners.TryGetValue(renderer, out var observer))
            return;
        observer.FlushEvents();
    }

    private void FlushEvents()
    {
        lock (_eventGate)
        {
            for (var i = 0; i < _eventCount; i++)
            {
                var e = _events[i];
                switch (e.Kind)
                {
                    case EventKind.Owner:
                        Write(new { kind = "owner", owner = _owner, mode = Mode, frequency = Stopwatch.Frequency,
                            smoke = Smoke, lookupPrototype = _lookupPrototype,
                            engine = typeof(D3D12Renderer).Assembly.GetName().Version?.ToString(), engineHash = EngineHash.Value,
                            wp15 = ReferenceSunlight.Enabled, composition = Composition,
                            orbitPbr = OrbitCombined && OrbitDemoPreset.PbrEnabled,
                            boulder = OrbitCombined && OrbitDemoPreset.BoulderEnabled });
                        break;
                    case EventKind.UnboundPresent:
                        Write(new { kind = "unbound-present", owner = _owner, fence = e.Fence, timestamp = e.Qpc });
                        break;
                    case EventKind.Gpu:
                        Write(new { kind = "gpu", owner = _owner, sequence = e.Sequence, fence = e.Fence, phase = e.Phase,
                            observedQpc = e.Qpc, frameMicroseconds = e.A, skyMicroseconds = e.B, hasSky = e.Flag });
                        break;
                    case EventKind.Record:
                        Write(new { kind = "record", owner = _owner, sequence = e.Sequence, fence = e.Fence, phase = e.Phase,
                            observedQpc = e.Qpc, recordMicroseconds = e.A, allocatedBytes = e.Allocated, draws = (int)e.B });
                        break;
                    case EventKind.Prepare:
                        Write(new { kind = "prepare", owner = _owner, sequence = e.Sequence, phase = e.Phase,
                            observedQpc = e.Qpc, preparationMicroseconds = e.A, allocatedBytes = e.Allocated });
                        break;
                    case EventKind.Pipeline:
                        Write(new { kind = "sky-pipeline", owner = _owner, start = e.Allocated, end = e.Qpc,
                            frequency = Stopwatch.Frequency, reused = e.Flag });
                        break;
                    case EventKind.GpuTicks:
                        Write(new { kind = "gpu-ticks", owner = _owner, sequence = e.Sequence, fence = e.Fence, phase = e.Phase,
                            observedQpc = e.Qpc, frameStart = e.T0, frameEnd = e.T1, skyStart = e.T2, skyEnd = e.T3,
                            frequency = e.Frequency });
                        break;
                    case EventKind.LookupGpu:
                    case EventKind.LookupRecord:
                        Write(new { kind = e.Kind == EventKind.LookupGpu ? "lookup-gpu-split" : "lookup-record-split",
                            owner = _owner, sequence = e.Sequence, fence = e.Fence, phase = e.Phase,
                            observedQpc = e.Qpc, computeMicroseconds = e.A, drawBarrierMicroseconds = e.B });
                        break;
                    case EventKind.Calibration:
                        Write(new { kind = "gpu-clock-calibration", owner = _owner, gpu = e.T0, cpu = e.T1,
                            gpuFrequency = e.Frequency, cpuFrequency = Stopwatch.Frequency, observedQpc = e.Qpc });
                        break;
                }
            }
            Array.Clear(_events, 0, _eventCount);
            _eventCount = 0;
        }
    }
}
