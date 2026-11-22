using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private VolumeStreamingCoordinator? _streamEvidenceOwner;
    private VolumeStreamingEvent? _streamPresentationEvidence;
    private VolumeStreamingEvent? _streamDenialEvidence;

    private string InspectAim(VolumeStreamingCoordinator terrain, PlanetWalkState state)
    {
        if (TraceTerrainAim(terrain, state, out var status, out _) is not { } hit)
            return status switch
            {
                VolumeQueryStatus.Clear => "No terrain hit (10 m).",
                VolumeQueryStatus.CoverageUnavailable => "Terrain view unavailable.",
                VolumeQueryStatus.BudgetExceeded => "Terrain query budget held.",
                VolumeQueryStatus.InitialOverlap => "Aim starts inside terrain.",
                _ => "Terrain aim unavailable.",
            };

        var material = (cave is null, hit.Material) switch
        {
            (true, (ushort)ReferenceTerrainMaterial.Basalt) => "Basalt",
            (true, (ushort)ReferenceTerrainMaterial.OchreStone) => "Ochre stone",
            (false, CavePlanet.ShallowRock) => "Shallow rock",
            (false, CavePlanet.MiddleRock) => "Middle rock",
            (false, CavePlanet.DeepRock) => "Deep rock",
            (false, CavePlanet.Ore) => "Ember ore",
            _ => null,
        };
        var stride = _selection is { } selection && !hit.IsTransition
            ? StrideAt(selection, Descriptor.SampleAt(hit.Position).Vertex.Locate().Brick)
            : 0;
        var lod = hit.IsTransition ? "LOD transition" : stride > 0 ? $"Brick LOD {stride}" : "Brick LOD n/a";
        var materialLine = material is null ? $"Material {hit.Material}" : $"Material {hit.Material} · {material}";
        return FormattableString.Invariant(
            $"Terrain hit {hit.Distance:0.##} m\n{materialLine}\nX {hit.Position.X:0.###}\nY {hit.Position.Y:0.###}\nZ {hit.Position.Z:0.###}\n{lod}");
    }

    private string ReadStreamStatus(VolumeStreamingCoordinator terrain)
    {
        const double MiB = 1024d * 1024d;
        var statistics = terrain.Statistics;
        var used = statistics.Used;
        var budget = _streamingBudget;
        var selection = _selection is { } current
            ? FormattableString.Invariant($"Regions {current.MeshCount}\nLOD 1/2 {_fineBricks}/{current.Requests.Length - _fineBricks}")
            : "Regions n/a\nLOD 1/2 n/a";
        var activity = statistics.Stopped ? "Terrain stream stopped" : statistics.MovementHeld
            ? "Terrain movement held" : "Terrain movement open";
        return FormattableString.Invariant(
            $"{activity}\n{selection}\nCPU {used.CpuBytes / MiB:0.0}/{budget.CpuBytes / MiB:0.0} MiB\nGPU {used.NativeBytes / MiB:0.0}/{budget.NativeBytes / MiB:0.0} MiB\nQueue {used.QueueSlots}/{budget.QueueSlots} slots\nDensity {used.DensityJobs}/{budget.DensityJobs}\nMesh {used.MeshJobs}/{budget.MeshJobs}");
    }

    private void RecordStreamEvidence(VolumeStreamingCoordinator terrain, VolumeStreamingEvent entry)
    {
        if (!ReferenceEquals(_streamEvidenceOwner, terrain))
        {
            _streamEvidenceOwner = terrain;
            _streamPresentationEvidence = null;
            _streamDenialEvidence = null;
        }

        if (entry.Kind is VolumeEventKind.Accepted or VolumeEventKind.Opened)
            _streamPresentationEvidence = entry;
        else if (entry.Kind is VolumeEventKind.Backpressure or VolumeEventKind.Rejected or
            VolumeEventKind.MovementRejected or VolumeEventKind.EditRejected)
            _streamDenialEvidence = entry;
    }

    private string ReadStreamWhy(VolumeStreamingCoordinator terrain)
    {
        var statistics = terrain.Statistics;
        if (statistics.Stopped)
            return terrain.TerminalEvidence?.Stamp is { } stop
                ? FormattableString.Invariant($"Terrain stream stopped.\nStopped at tick {stop.Tick}")
                : "Terrain stream stopped.";

        var view = terrain.View;
        var committed = (_ports.Committed.Read().Root as VolumeWorldRoot)?.Terrain;
        var rows = new List<string>
        {
            statistics.MovementHeld ? "Terrain gate held." : "Terrain gate open.",
            FormattableString.Invariant($"View {view.View} / epoch {view.RendererEpoch}"),
        };
        if (committed is null)
            rows.Add("No terrain root published.");
        else if (committed.Value.View != view)
            rows.Add("This view not committed.");
        else
        {
            rows.Add("Current view committed.");
            if (statistics.MovementHeld)
                rows.Add("Gate reason not exposed.");
        }

        if (ReferenceEquals(_streamEvidenceOwner, terrain))
        {
            if (_streamPresentationEvidence is { } presentation && presentation.View == view &&
                (presentation.Generation == 0 || presentation.Generation == committed?.Generation))
            {
                rows.Add(presentation.Kind == VolumeEventKind.Opened ? "Seen: gate opened."
                    : presentation.Generation == 0 ? "Seen: mask acknowledged." : "Seen: frame acknowledged.");
                if (presentation.Frame != 0)
                    rows.Add(FormattableString.Invariant($"Frame {presentation.Frame}"));
            }
            if (_streamDenialEvidence is { } denial && denial.View == view &&
                (committed is null || denial.TerrainRevision == committed.Value.TerrainRevision))
            {
                rows.Add(denial.Kind switch
                {
                    VolumeEventKind.Backpressure => "Seen: backpressure.",
                    VolumeEventKind.MovementRejected => "Seen: movement rejected.",
                    VolumeEventKind.EditRejected => "Seen: edit rejected.",
                    _ => "Seen: work rejected.",
                });
                if (denial.Stamp is { } stamp)
                    rows.Add(FormattableString.Invariant($"Denial at tick {stamp.Tick}"));
            }
        }
        return string.Join('\n', rows);
    }
}
