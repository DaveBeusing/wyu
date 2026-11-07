using System.Numerics;
using Groundwork.Core;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed class PlanetRoute(bool scripted)
{
    private const double Radius = 64;
    private const uint Seed = 0x57505538;
    private const int GeneratorVersion = 1;
    private const double StageDistance = 3.5;
    private const double WaypointTolerance = 0.25;
    private const int PatchesPerTick = 16;
    private static readonly PlanetVector Start = Equator(35);
    private static readonly PlanetVector Turnaround = Equator(145);
    private static readonly Vector4 TerrainTint = new(0.42f, 0.55f, 0.44f, 1);
    private static readonly Vector4 Sky = new(0.12f, 0.20f, 0.32f, 1);
    private readonly PlanetTerrainCoordinator _terrain = new();
    private readonly PrototypeCaption _caption = new();
    private PlanetWalkState _walker = new(Scale(Start, Radius + Height(Start) - 0.01), new(-Start.Z, 0, Start.X), default);
    private PlanetTerrainPlan? _plan;
    private PlanetTerrainPlan? _presentedPlan;
    private PlanetVector _focus;
    private PlanetLocalFrame _frame;
    private PlanetTerrainLease? _candidate;
    private ulong _sequence;
    private ulong _candidateSequence;
    private ulong _lastCameraTick;
    private ulong _finalCameraSequence;
    private ulong _initialRevision;
    private ulong _turnRevision;
    private int _faceCrossings;
    private int _holds;
    private int _walkTicks;
    private int _peakPatches;
    private double _maximumSeparation;
    private double _maximumPenetration;
    private bool _returning;
    private bool _refined;
    private bool _coarsened;
    private PlanetFace? _lastFace;
    private string _loading = "Loading terrain";

    internal bool Completed { get; private set; }
    internal bool FinalCameraPresented(RendererSnapshot snapshot) =>
        _finalCameraSequence != 0 && snapshot.LastPresentedSequence >= _finalCameraSequence;

    internal void Tick(TickContext tick, bool drawable)
    {
        if (!drawable || Completed)
            return;

        var position = _walker.Position;
        if (_candidate is null && (_plan is null || Length(Subtract(position, _focus)) >= StageDistance))
        {
            var nextFrame = _frame.Revision == 0 ? PlanetLocalFrame.Create(position) : _frame;
            _ = nextFrame.TryRebase(position, out var rebased);
            if (PlanetTerrainSelection.TrySelect(position, Radius, _plan, out var plan) &&
                _terrain.TryStage(plan!, rebased))
            {
                if (scripted && _plan is null)
                    Console.WriteLine($"WP08 recipe seed=0x{Seed:X8} version={GeneratorVersion} radius={Radius} m.");
                _plan = plan;
                _focus = position;
                _frame = rebased;
                _peakPatches = Math.Max(_peakPatches, _terrain.RetainedPatchCount);
            }
        }

        // Work is owner-local and sliced across fixed ticks. Stop has no detached
        // mesh worker or reservation to join.
        for (var i = 0; i < PatchesPerTick && _terrain.TryReserveMesh(out var work); i++)
        {
            var mesh = PlanetPatchMesh.Build(work.Tile.Patch, Radius,
                static sample => Height(PlanetCoordinates.Direction(sample)), work.Tile.Transitions);
            if (!_terrain.CompleteMesh(work, mesh))
                throw new InvalidOperationException("The current planet mesh reservation could not complete.");
        }

        if (!_terrain.TryReadActive(CameraPosition(), out var active))
        {
            _holds++;
            return;
        }

        using (active)
        {
            var (state, forward, right) = ReadInput(tick.Input);
            var movement = PlanetSurfaceWalker.Step(active, state, forward, right);
            if (movement.Status == PlanetWalkStatus.CoverageUnavailable)
            {
                _loading = "Waiting for terrain coverage";
                _holds++;
                return;
            }

            if (scripted && (!movement.Grounded || movement.Status == PlanetWalkStatus.SteepTerrain))
                throw new InvalidOperationException($"WP08 route lost walkable contact at tick {tick.Tick}.");

            _walker = movement.State;
            _loading = "";
            if (scripted)
                VerifyContact(active);
            if (forward != 0 || right != 0)
                _walkTicks++;
            RecordWaypoints(tick.Tick, active);
        }
    }

    internal void Present(D3D12Renderer renderer, ulong tick)
    {
        if (_candidate is not null && renderer.WasPlanetTerrainPresented(_candidate, _candidateSequence))
        {
            if (!_terrain.AcknowledgePresented(_candidate))
                throw new InvalidOperationException("The presented planet terrain lease is no longer current.");
            if (_presentedPlan is not null)
            {
                _refined |= Refines(_plan!, _presentedPlan);
                _coarsened |= Refines(_presentedPlan, _plan!);
            }
            _presentedPlan = _plan;
            if (_initialRevision == 0)
                _initialRevision = _candidate.Frame.Revision;
            _candidate.Dispose();
            _candidate = null;
        }

        var up = Unit(_walker.Position);
        var forward = ToSingle(Subtract(_walker.Heading, Scale(up, 0.28)));
        var upSingle = ToSingle(up);
        // The diagnostic route advances at 60 Hz but samples camera-only
        // presentations at 10 Hz to keep the bounded D3D12 info queue intact.
        var cameraInterval = scripted ? 6UL : 2UL;
        if (_candidate is null && _terrain.TryPreparePresentation(CameraPosition(), out var candidate))
        {
            _candidate = candidate;
            _candidateSequence = ++_sequence;
            var result = renderer.TrySubmitPlanetTerrain(candidate, _candidateSequence, forward, upSingle,
                MathF.PI / 3, 0.08f, 180, TerrainTint, Sky);
            if (result != MeshReplacementSubmissionResult.Accepted)
            {
                candidate.Dispose();
                _candidate = null;
                if (result != MeshReplacementSubmissionResult.Closed)
                    throw new InvalidOperationException($"Planet mesh submission failed ({result}).");
            }
        }
        else if (_candidate is null && _finalCameraSequence == 0 && (Completed || tick >= _lastCameraTick + cameraInterval) &&
            _terrain.TryReadActive(CameraPosition(), out var active))
        {
            using (active)
            {
                _lastCameraTick = tick;
                var result = renderer.TrySubmitPlanetCamera(active, ++_sequence, forward, upSingle,
                    MathF.PI / 3, 0.08f, 180, TerrainTint, Sky);
                if (result is not (RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced or RenderSubmissionResult.Closed))
                    throw new InvalidOperationException($"Planet camera submission failed ({result}).");
                if (Completed && result != RenderSubmissionResult.Closed)
                    _finalCameraSequence = _sequence;
            }
        }

        if (!scripted)
            UpdateCaption(up);
    }

    internal void Stop()
    {
        _terrain.Stop();
        _candidate?.Dispose();
        _candidate = null;
        if (!_terrain.IsSettled)
            throw new InvalidOperationException("Planet terrain work did not settle.");
    }

    private (PlanetWalkState State, double Forward, double Right) ReadInput(InputSnapshot input)
    {
        if (scripted)
            return (_walker, 1, 0);
        if (input.TransitionLoss)
            return (_walker, 0, 0);
        var held = input.HeldControls;
        var yaw = Axis(InputControl.Right, InputControl.Left) * Math.PI / 90;
        var state = _walker;
        if (yaw != 0)
        {
            var heading = state.Heading;
            var right = Cross(heading, Unit(state.Position));
            state = state with { Heading = Add(Scale(heading, Math.Cos(yaw)), Scale(right, Math.Sin(yaw))) };
        }
        return (state, Axis(InputControl.W, InputControl.S), Axis(InputControl.D, InputControl.A));

        int Axis(InputControl positive, InputControl negative) =>
            (held.Contains(positive) ? 1 : 0) - (held.Contains(negative) ? 1 : 0);
    }

    private void RecordWaypoints(ulong tick, PlanetTerrainLease active)
    {
        var up = Unit(_walker.Position);
        var (face, u, v) = PlanetCoordinates.Project(up);
        var waypoint = _returning ? Start : Turnaround;
        var reached = ArcDistance(up, waypoint) <= (scripted ? 0.06 : WaypointTolerance);
        if (_lastFace != face || reached)
        {
            if (_lastFace is not null && _lastFace != face)
                _faceCrossings++;
            _lastFace = face;
            if (scripted)
            {
                const int width = 1 << 5;
                var tileU = Math.Clamp((int)((u + 1) * width / 2), 0, width - 1);
                var tileV = Math.Clamp((int)((v + 1) * width / 2), 0, width - 1);
                Console.WriteLine(FormattableString.Invariant(
                    $"WP08 {(_returning ? "return" : "outbound")} tick={tick} motion={_walkTicks} face={face} tile={tileU},{tileV} lod=5 origin={active.Frame.Revision} generation={active.Generation} pose={_walker.Position.X:F4},{_walker.Position.Y:F4},{_walker.Position.Z:F4} contact=grounded holds={_holds}"));
            }
        }

        if (!reached)
            return;
        if (!_returning)
        {
            _returning = true;
            _turnRevision = active.Frame.Revision;
            if (scripted)
            {
                if (_faceCrossings != 2 || _turnRevision <= _initialRevision)
                    throw new InvalidOperationException("WP08 outbound leg missed a face crossing or origin change.");
                _walker = _walker with { Heading = Scale(_walker.Heading, -1) };
            }
        }
        else
        {
            if (scripted && (_faceCrossings != 4 || active.Frame.Revision <= _turnRevision || !_refined || !_coarsened))
                throw new InvalidOperationException("WP08 return leg missed a face crossing, origin change or LOD transition.");
            if (scripted)
                Console.WriteLine(FormattableString.Invariant(
                    $"WP08 coverage crossings={_faceCrossings} refined={_refined} coarsened={_coarsened} peakPatches={_peakPatches} maxSeparation={_maximumSeparation:F5} m maxPenetration={_maximumPenetration:F5} m."));
            Completed = true;
        }
    }

    private void UpdateCaption(PlanetVector up)
    {
        var target = _returning ? Start : Turnaround;
        var distance = ArcDistance(up, target);
        var right = Cross(_walker.Heading, up);
        var bearing = Math.Atan2(Dot(target, right), Dot(target, _walker.Heading)) * 180 / Math.PI;
        var label = _returning ? "◆ Start" : "▲ Turnaround";
        var turn = Math.Abs(bearing) < 5 ? "ahead" : $"{Math.Abs(bearing):F0}° {(bearing < 0 ? "left" : "right")}";
        _caption.Update(Completed ? "WYU Planet | Route complete | Esc to exit" :
            $"WYU Planet | {label}: {distance:F1} m {turn} | WASD walk · ←/→ turn · Esc exit | {_loading}");
    }

    private void VerifyContact(PlanetTerrainLease active)
    {
        var position = _walker.Position;
        if (!active.TryGetSupport(position, out var support))
            throw new InvalidOperationException("WP08 route has no visible terrain support at the foot.");
        var separation = Length(position) - Length(support.Position);
        if (separation is < -0.01 or > 0.02)
            throw new InvalidOperationException($"WP08 contact separation {separation:F5} m exceeds tolerance.");
        _maximumSeparation = Math.Max(_maximumSeparation, separation);
        _maximumPenetration = Math.Max(_maximumPenetration, -separation);
    }

    private static bool Refines(PlanetTerrainPlan next, PlanetTerrainPlan previous)
    {
        var patches = previous.Tiles.ToArray().Select(tile => tile.Patch).ToHashSet();
        foreach (var tile in next.Tiles)
        for (var shift = 1; shift <= tile.Patch.Level; shift++)
        {
            var patch = tile.Patch;
            if (patches.Contains(new PlanetPatchKey(patch.Face, patch.Level - shift, patch.TileU >> shift, patch.TileV >> shift)))
                return true;
        }
        return false;
    }

    private PlanetVector CameraPosition() => Add(_walker.Position, Scale(Unit(_walker.Position), 1.62));
    private static double Height(PlanetVector d) => Math.Round(1200 * d.X * d.Z + 600 * d.Y) / 1000;
    private static PlanetVector Equator(double degrees) => new(Math.Cos(degrees * Math.PI / 180), 0, Math.Sin(degrees * Math.PI / 180));
    private static double ArcDistance(PlanetVector a, PlanetVector b) => Radius * Math.Acos(Math.Clamp(Dot(a, b), -1, 1));
    private static double Dot(PlanetVector a, PlanetVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Length(PlanetVector v) => Math.Sqrt(Dot(v, v));
    private static PlanetVector Unit(PlanetVector v) => Scale(v, 1 / Length(v));
    private static PlanetVector Add(PlanetVector a, PlanetVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static PlanetVector Subtract(PlanetVector a, PlanetVector b) => Add(a, Scale(b, -1));
    private static PlanetVector Scale(PlanetVector v, double scale) => new(v.X * scale, v.Y * scale, v.Z * scale);
    private static PlanetVector Cross(PlanetVector a, PlanetVector b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static Vector3 ToSingle(PlanetVector v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
