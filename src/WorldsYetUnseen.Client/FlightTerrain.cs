using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed class FlightTerrain(FlightLandingSite site)
{
    private static readonly Vector4 Tint = new(0.42f, 0.55f, 0.44f, 1);
    private static readonly Vector4 Sky = new(0.09f, 0.15f, 0.25f, 1);
    private readonly PlanetTerrainCoordinator _terrain = new();
    private PlanetTerrainPlan? _activePlan;
    private PlanetTerrainPlan? _stagedPlan;
    private PlanetLocalFrame _frame;
    private PlanetTerrainLease? _candidate;
    private ulong _sequence;
    private ulong _lastCameraTick;
    private ulong _finalSequence;
    internal PlanetTerrainStamp Stamp { get; private set; }
    internal int PeakPatches { get; private set; }
    internal bool Ready => _activePlan is not null && _stagedPlan is null;

    internal void Prepare(PlanetVector position)
    {
        if (_stagedPlan is null)
        {
            var nextFrame = _frame.Revision == 0 ? PlanetLocalFrame.Create(position) : _frame;
            _ = nextFrame.TryRebase(position, out nextFrame);
            var radius = Length(position);
            var view = _activePlan?.View == PlanetTerrainView.Far
                ? radius > 80 ? PlanetTerrainView.Far : PlanetTerrainView.Near
                : radius >= 84 ? PlanetTerrainView.Far : PlanetTerrainView.Near;
            if (_activePlan is null || _activePlan.View != view || nextFrame != _frame)
            {
                if (!PlanetTerrainSelection.TrySelectFlight(position, FlightLandingSite.Direction, 1, _activePlan, out var plan) ||
                    plan is null || !_terrain.TryStage(plan, nextFrame))
                    throw new InvalidOperationException("The flight terrain generation could not be staged.");
                _stagedPlan = plan;
                PeakPatches = Math.Max(PeakPatches, _terrain.RetainedPatchCount);
            }
        }

        for (var i = 0; i < 16 && _terrain.TryReserveMesh(out var work); i++)
        {
            var mesh = PlanetPatchMesh.Build(work.Tile.Patch, FlightLandingSite.Radius, site.Height, work.Tile.Transitions);
            if (!_terrain.CompleteMesh(work, mesh))
                throw new InvalidOperationException("The flight terrain generation did not accept its mesh.");
        }
    }

    internal bool TryRead(PlanetVector position, out PlanetTerrainLease? lease) =>
        _terrain.TryReadActive(Camera(position), out lease);

    internal bool FinalPresented(RendererSnapshot snapshot) => _finalSequence != 0 && snapshot.LastPresentedSequence >= _finalSequence;

    internal void Present(D3D12Renderer renderer, PlanetVector position, ulong tick, ulong missionTick, bool scripted, bool final)
    {
        if (_candidate is not null && renderer.TryReadPresentedPlanetTerrain(_candidate, _sequence, out var stamp))
        {
            if (stamp != _candidate.Stamp || !_terrain.AcknowledgePresented(_candidate))
                throw new InvalidOperationException("Flight terrain presentation did not match its generation.");
            if (scripted && (_activePlan is null || stamp.View != Stamp.View))
                Console.WriteLine($"WP09 checkpoint view={stamp.View} mission={missionTick} origin={stamp.Frame.Revision} generation={stamp.Generation} edit={stamp.EditRevision}");
            _frame = stamp.Frame;
            Stamp = stamp;
            _activePlan = _stagedPlan;
            _stagedPlan = null;
            _candidate.Dispose();
            _candidate = null;
        }

        var radial = Unit(position);
        var forward = new Vector3((float)-radial.X, (float)-radial.Y, (float)-radial.Z);
        if (_candidate is null && _terrain.TryPreparePresentation(Camera(position), out var candidate))
        {
            _candidate = candidate;
            var result = renderer.TrySubmitPlanetTerrain(candidate, ++_sequence, forward, Vector3.UnitY,
                MathF.PI / 3, 0.08f, 220, Tint, Sky);
            if (result != MeshReplacementSubmissionResult.Accepted)
            {
                candidate.Dispose();
                _candidate = null;
                if (result != MeshReplacementSubmissionResult.Closed)
                    throw new InvalidOperationException($"Flight terrain submission failed ({result}).");
            }
        }
        else if (Ready && _finalSequence == 0 && (final || tick >= _lastCameraTick + (scripted ? 12UL : 2UL)) &&
            TryRead(position, out var active))
        {
            using (active)
            {
                _lastCameraTick = tick;
                var result = renderer.TrySubmitPlanetCamera(active!, ++_sequence, forward, Vector3.UnitY,
                    MathF.PI / 3, 0.08f, 220, Tint, Sky);
                if (result is not (RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced or RenderSubmissionResult.Closed))
                    throw new InvalidOperationException($"Flight camera submission failed ({result}).");
                if (final && result != RenderSubmissionResult.Closed)
                    _finalSequence = _sequence;
            }
        }
    }

    internal void Stop()
    {
        _terrain.Stop();
        _candidate?.Dispose();
        _candidate = null;
        if (!_terrain.IsSettled)
            throw new InvalidOperationException("Flight terrain did not settle.");
    }

    private static PlanetVector Camera(PlanetVector position) => Add(position, Scale(Unit(position), 3));
}
