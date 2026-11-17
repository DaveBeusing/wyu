using System.Diagnostics;
using Groundwork.Jobs;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    internal void WriteR09SettledResources(D3D12Renderer renderer, ulong epoch, VolumeStreamingStatistics terrain)
    {
        var jobs = _r09Scheduler?.ReadMetrics() ?? default;
        if (!terrain.Stopped || jobs is not { Queued: 0, OccupiedWorkerSlots: 0 })
            throw new InvalidOperationException("R09 retained terrain or scheduler work after shutdown.");

        var settled = R09ResourceMeasurement.Capture(renderer, epoch, terrain, jobs);
        settled.RequireReleasedGpu();
        settled.Write("Shutdown", "settled");
    }

    private readonly record struct R09ResourceMeasurement(long Timestamp, ulong RendererEpoch,
        VolumeStreamingStatistics Terrain, JobSchedulerMetrics Jobs, long WorkingSet, long PrivateBytes,
        long PeakWorkingSet, D3D12ResourceSnapshot Gpu)
    {
        private static readonly D3D12ResourceOwner[] Owners = Enum.GetValues<D3D12ResourceOwner>();

        internal static R09ResourceMeasurement Capture(D3D12Renderer renderer, ulong epoch,
            VolumeStreamingStatistics terrain, JobSchedulerMetrics jobs)
        {
            var timestamp = Stopwatch.GetTimestamp();
            if (!renderer.TryReadResourceDiagnostics(out var gpu))
                throw new InvalidOperationException("R09 could not read the renderer resource snapshot.");

            using var process = Process.GetCurrentProcess();
            return new(timestamp, epoch, terrain, jobs, process.WorkingSet64, process.PrivateMemorySize64,
                process.PeakWorkingSet64, gpu);
        }

        internal void RequireReleasedGpu()
        {
            if (Gpu.Custody != D3D12CustodyStatus.None)
                throw new InvalidOperationException($"R09 retained GPU custody after shutdown ({Gpu.Custody}).");
            foreach (var owner in Owners)
            {
                if (Gpu.GetBalance(owner) is not { Live: 0, LiveBytes: 0, Retained: 0, RetainedBytes: 0 })
                    throw new InvalidOperationException($"R09 retained GPU backing for {owner} after shutdown.");
            }
        }

        internal void Write(string path, string sample)
        {
            Console.WriteLine(FormattableString.Invariant(
                $"WYU R09 RESOURCE {path} {sample} {Timestamp} {Stopwatch.Frequency} {RendererEpoch} {Jobs.OccupiedWorkerSlots} {Jobs.Queued} {Terrain.Used.CpuBytes} {Terrain.Peak.CpuBytes} {Terrain.Generations} {Terrain.Readers} {WorkingSet} {PrivateBytes} {PeakWorkingSet} {(Gpu.LedgerOverflowed ? 1 : 0)} {(Gpu.LedgerHasUnknownSizes ? 1 : 0)} {(int)Gpu.Custody}"));
            foreach (var owner in Owners)
            {
                var balance = Gpu.GetBalance(owner);
                Console.WriteLine(FormattableString.Invariant(
                    $"WYU R09 GPU {path} {sample} {owner} {balance.Live} {balance.LiveBytes} {balance.Retained} {balance.RetainedBytes} {balance.Released} {balance.ReleasedBytes}"));
            }
        }
    }
}
