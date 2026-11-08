using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed class FlightMission : IDisposable
{
    private readonly FlightLandingSite _site;
    private readonly PlanetSaveSlot _slot;
    private readonly FlightTerrain _terrain;
    private readonly IRuntimeOwnership _ownership;
    private readonly bool _scripted;
    private readonly bool _reload;
    private readonly PrototypeCaption _caption = new();
    private readonly IncrementalHash _trace = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private PlanetFlightState _state;
    private FlightPhase _phase;
    private ulong _missionTick;
    private int _holds;
    private int _consecutiveHolds;
    private bool _startRequested;
    private bool _departureSaved;
    private bool _landingSaved;
    private bool _alpha;
    private double _orbitAngle;
    private double _lastAngle;
    private double _minimumCoastRadius = double.PositiveInfinity;
    private double _maximumCoastRadius;
    private double _entryEnergy;
    private double _entryMomentum;

    private FlightMission(FlightLandingSite site, PlanetSaveSlot slot, IRuntimeOwnership ownership, bool scripted, bool reload)
    {
        (_site, _slot, _ownership, _scripted, _reload) = (site, slot, ownership, scripted, reload);
        _terrain = new(site);
        _state = site.InitialState;
        _trace.AppendData("WYU-WP09-THRUST-1"u8);
    }

    internal static async Task<FlightMission> OpenAsync(string directory, IRuntimeOwnership ownership, bool scripted, bool reload,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var slot = FlightLandingSite.OpenSlot(directory);
        var result = await FlightLandingSite.RunIoAsync(ownership, slot, slot.Load).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.Status is not (SaveSlotStatus.Empty or SaveSlotStatus.Loaded) ||
            scripted && (reload ? result.Checkpoint?.Identity.Generation != 2 : result.Status != SaveSlotStatus.Empty))
            throw new InvalidOperationException($"Unexpected planetary slot state ({result.Status}).", result.Error);

        return new(new FlightLandingSite(result.Checkpoint), slot, ownership, scripted, reload);
    }

    internal void EnterAlpha()
    {
        if (_site.Generation > 2)
            throw new InvalidDataException("Unexpected WP10 flight checkpoint generation.");
        _alpha = true;
        _departureSaved = _site.Generation >= 1;
        _landingSaved = _site.Generation == 2;
        if (_landingSaved)
        {
            _phase = FlightPhase.Landed;
            Console.WriteLine($"WP10 restored landing generation={_site.Generation} {FlightLandingSite.Describe(_state)} edits={_site.EditDigest()}");
        }
    }

    internal void Tick(TickContext tick, bool drawable)
    {
        _startRequested |= tick.Input.Events.Any(static e => e is
            { Kind: InputEventKind.Pressed, Device: InputDevice.Keyboard, Control: InputControl.Space });
        if (!drawable)
            return;

        _terrain.Prepare(_state.Position);
        if (_reload || !_departureSaved || _phase == FlightPhase.Landed)
            return;
        if (!_terrain.Ready || !_terrain.TryRead(_state.Position, out var surface))
        {
            Hold();
            return;
        }

        using (surface)
        {
            if (_phase == FlightPhase.Ready)
            {
                if (!_scripted && !_startRequested)
                    return;
                Enter(FlightPhase.Ascent);
            }

            var command = FlightGuidance.Thrust(_phase, _state);
            var step = PlanetFlight.Step(surface!, _state, new(command, _state.Orientation));
            if (step.Status == PlanetFlightStatus.CoverageUnavailable)
            {
                Hold();
                return;
            }
            if (step.Status is PlanetFlightStatus.LandingBlocked or PlanetFlightStatus.SweepLimitExceeded)
                throw new InvalidOperationException($"WP09 {_phase} blocked at mission tick {_missionTick} ({step.Status}).");
            _consecutiveHolds = 0;
            _state = step.State;
            _missionTick++;
            AppendThrust(command);
            AdvancePhase(step.Status);
        }
    }

    internal async Task<bool> PresentAsync(D3D12Renderer renderer, RendererSnapshot snapshot, ulong tick, CancellationToken token)
    {
        if (snapshot.Surface != RenderSurfaceState.Drawable)
            return false;
        var finished = _reload || _phase == FlightPhase.Landed;
        _terrain.Present(renderer, _state.Position, tick, _missionTick, _scripted, finished);
        if (!_scripted)
            UpdateCaption(snapshot);

        if (!_terrain.Ready)
            return false;
        if (_reload)
        {
            if (!_terrain.FinalPresented(snapshot))
                return false;
            Console.WriteLine($"WP09 restored generation={_site.Generation} {FlightLandingSite.Describe(_state)} edits={_site.EditDigest()}");
            return true;
        }

        if (!_departureSaved)
        {
            await _site.SaveAsync(_slot, _ownership, _state, token).ConfigureAwait(false);
            _departureSaved = true;
        }
        if (!finished || !_terrain.FinalPresented(snapshot))
            return false;
        if (!_landingSaved)
        {
            await _site.SaveAsync(_slot, _ownership, _state, token).ConfigureAwait(false);
            _landingSaved = true;
            Console.WriteLine($"WP09 saved generation={_site.Generation} {FlightLandingSite.Describe(_state)} edits={_site.EditDigest()}");
            Console.WriteLine($"WP09 trace v1 ticks={_missionTick} sha256={Convert.ToHexString(_trace.GetHashAndReset())}");
            Console.WriteLine($"WP09 readiness holds={_holds} peakPatches={_terrain.PeakPatches} finalFrame={snapshot.LastPresentedSequence}");
        }
        return _scripted;
    }

    private void UpdateCaption(RendererSnapshot snapshot)
    {
        if (_alpha && _landingSaved)
        {
            _caption.Update(_terrain.FinalPresented(snapshot) ? "WYU | Mission complete | Landing saved | Esc to exit"
                : "WYU | Restoring the landing view");
            return;
        }

        var hint = _phase == FlightPhase.Ready
            ? _alpha ? "Space to launch · Follow the amber pad on approach · Esc to exit"
                : "Space to begin guided flight · Esc to exit"
            : _alpha && _phase == FlightPhase.Landed ? "Saving landing checkpoint" : "Guided flight · Esc to exit";
        _caption.Update($"WYU {(_alpha ? "WP10" : "WP09")} | {_phase} | " +
            $"Altitude {Length(_state.Position) - FlightLandingSite.Radius:F1} m | {hint}");
    }

    public void Dispose()
    {
        _trace.Dispose();
        _terrain.Stop();
    }

    private void Hold()
    {
        _holds++;
        if (++_consecutiveHolds > 600)
            throw new TimeoutException("The flight could not acquire confirmed terrain coverage.");
    }

    private void AdvancePhase(PlanetFlightStatus status)
    {
        var radius = Length(_state.Position);
        var up = Unit(_state.Position);
        var radialSpeed = Dot(_state.Velocity, up);
        switch (_phase)
        {
            case FlightPhase.Ascent when Math.Abs(radius - FlightGuidance.OrbitRadius) < 0.05 && Math.Abs(radialSpeed) < 0.05:
                Enter(FlightPhase.Insertion);
                break;
            case FlightPhase.Insertion when Math.Abs(radius - FlightGuidance.OrbitRadius) < 0.05 && Math.Abs(radialSpeed) < 0.05 &&
                Math.Abs(Dot(_state.Velocity, FlightGuidance.Prograde(up)) - FlightGuidance.OrbitSpeed) < 0.015:
                _lastAngle = Math.Atan2(up.Z, up.X);
                _entryEnergy = Energy(radius);
                _entryMomentum = Momentum();
                Enter(FlightPhase.Orbit);
                break;
            case FlightPhase.Orbit:
                var angle = Math.Atan2(up.Z, up.X);
                var increment = Math.IEEERemainder(angle - _lastAngle, Math.Tau);
                if (increment <= 0 || radius is < 95.5 or > 96.5 || Math.Abs(_state.Position.Y) > 0.1)
                    throw new InvalidOperationException("WP09 coast left the qualified prograde orbit.");
                _orbitAngle += increment;
                _lastAngle = angle;
                _minimumCoastRadius = Math.Min(_minimumCoastRadius, radius);
                _maximumCoastRadius = Math.Max(_maximumCoastRadius, radius);
                if (_orbitAngle >= Math.Tau)
                {
                    Console.WriteLine(FormattableString.Invariant(
                        $"WP09 orbit angle={_orbitAngle:F8} radius={_minimumCoastRadius:F6}..{_maximumCoastRadius:F6} energy={_entryEnergy:F8}->{Energy(radius):F8} momentum={_entryMomentum:F8}->{Momentum():F8}"));
                    Enter(FlightPhase.Return);
                }
                break;
            case FlightPhase.Return when Length(Subtract(_state.Position, Scale(FlightLandingSite.Direction, 78))) < 0.15 &&
                Length(_state.Velocity) < 0.2:
                Enter(FlightPhase.Descent);
                break;
            case FlightPhase.Descent when status == PlanetFlightStatus.Touchdown:
                var distance = FlightLandingSite.Radius * Math.Acos(Math.Clamp(Dot(up, FlightLandingSite.Direction), -1, 1));
                if (distance > 0.25 || _terrain.Stamp.View != PlanetTerrainView.Near)
                    throw new InvalidOperationException("WP09 touched down outside the qualified landing site.");
                Enter(FlightPhase.Landed);
                break;
        }
    }

    private double Energy(double radius) => Dot(_state.Velocity, _state.Velocity) / 2 - PlanetFlight.GravitationalParameter / radius;
    private double Momentum() => _state.Position.X * _state.Velocity.Z - _state.Position.Z * _state.Velocity.X;

    private void Enter(FlightPhase phase)
    {
        _phase = phase;
        if (_scripted || _alpha)
            Console.WriteLine($"WP09 checkpoint phase={phase} mission={_missionTick} origin={_terrain.Stamp.Frame.Revision} " +
                $"generation={_terrain.Stamp.Generation} {FlightLandingSite.Describe(_state)}");
    }

    private void AppendThrust(PlanetVector thrust)
    {
        Span<byte> bytes = stackalloc byte[24];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, thrust.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[8..], thrust.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[16..], thrust.Z);
        _trace.AppendData(bytes);
    }
}
