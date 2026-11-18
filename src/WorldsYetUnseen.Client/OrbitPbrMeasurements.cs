using System.Diagnostics;
using Groundwork.Rendering.D3D12;

namespace WorldsYetUnseen.Client;

// Scripted Orbit KPI evidence. QPC ends on observed renderer receipt, not display scanout.
internal static class OrbitPbrMeasurements
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("WYU_ORBIT_PBR_MEASURE") == "1";

    internal static void WriteFrame(string phase, long step, ulong sequence, ulong generation,
        long submitted, long observed, D3D12Renderer renderer)
    {
        var size = renderer.Read();
        if (size.AppliedWidth != 1920 || size.AppliedHeight != 1080)
            throw new InvalidOperationException("Orbit PBR measurements require native 1920x1080 rendering.");
        var texture = renderer.ReadTextureDiagnostics();
        Console.WriteLine(FormattableString.Invariant(
            $"ORBIT KPI FRAME {Environment.ProcessId} {phase} {step} {sequence} {generation} {submitted} {observed} {texture.ResidentAndPendingBytes} {texture.StagingBytes} {texture.NativeHighWaterBytes} {texture.UploadedBytes}"));
    }

    internal static void WriteCrossing(int step, ulong generation, long started)
    {
        if (started == 0)
            return;
        Console.WriteLine(FormattableString.Invariant(
            $"ORBIT KPI CROSSING {Environment.ProcessId} {step} {generation} {started} {Stopwatch.GetTimestamp()}"));
    }
}
