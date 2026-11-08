using System.Diagnostics;
using Groundwork.Core;
using Groundwork.Rendering;

namespace WorldsYetUnseen.Client;

internal sealed class AlphaPerformance(AlphaOverlay overlay)
{
    private long _lastSample;
    private ulong _lastSequence;
    private int _presents;
    private bool _visible;

    internal void Handle(InputSnapshot input)
    {
        if (!PrototypeMenu.Pressed(input, InputControl.F1))
            return;
        _visible = !_visible;
        _lastSample = 0;
        _presents = 0;
        if (!_visible)
            overlay.Performance = null;
    }

    internal void Update(RendererSnapshot? renderer)
    {
        if (!_visible)
            return;

        var now = Stopwatch.GetTimestamp();
        var sequence = renderer?.LastPresentedSequence ?? 0;
        var elapsed = _lastSample == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lastSample, now);
        if (_lastSample != 0 && sequence > _lastSequence)
            _presents++;
        _lastSequence = sequence;
        if (_lastSample != 0 && elapsed < TimeSpan.FromSeconds(1))
            return;

        var fps = renderer is null || elapsed == TimeSpan.Zero ? "—" :
            (_presents / elapsed.TotalSeconds).ToString("F1", System.Globalization.CultureInfo.CurrentCulture);
        var heap = GC.GetGCMemoryInfo().HeapSizeBytes / (1024d * 1024);
        overlay.Performance = $"Engine performance · F1 hide | FPS (observed): {fps} | " +
            $"GC collections (0/1/2): {GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)} | " +
            $"GC heap at last collection: {heap:F1} MiB | " +
            $"Frames replaced/skipped: {renderer?.ReplacedFrames ?? 0} / {renderer?.SkippedFrames ?? 0}";
        _lastSample = now;
        _presents = 0;
    }
}
