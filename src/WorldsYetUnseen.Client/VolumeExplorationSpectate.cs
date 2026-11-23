using System.Numerics;
using Groundwork.Core;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const double SpectateRangeMeters = 24;
    private const double SpectateSpeedMetersPerSecond = 8;
    private const string SpectateControls = "WASD move · E up · Q down\nMouse look · Esc return";
    private SpectateCamera? _spectate;

    private sealed class SpectateCamera(VolumeViewIdentity view, PlanetVector eye, float yaw, float pitch)
    {
        internal VolumeViewIdentity View { get; } = view;
        internal PlanetVector Origin { get; } = eye;
        internal PlanetVector Eye { get; set; } = eye;
        internal float WalkingYaw { get; } = yaw;
        internal float WalkingPitch { get; } = pitch;
    }

    private bool IsSpectating => _spectate is not null;

    private string SetSpectate(string value, VolumeStreamingCoordinator terrain, PlanetWalkState player, bool blocked)
    {
        if (!freeTestWorld)
            return "Free Test World only.";
        if (value.Length == 0)
            return IsSpectating ? $"Spectate on.\n{SpectateControls}" : "Spectate off.";
        if (value == "off")
        {
            StopSpectating();
            return "Spectate off.";
        }
        if (value != "on")
            return "Usage: spectate [on|off]";
        if (IsSpectating)
            return $"Spectate on.\n{SpectateControls}";
        if (blocked)
            return "Finish the active action first.";

        var eye = WalkingEye(player.Position);
        using var lease = terrain.TryRead();
        if (lease is null || lease.Info != _published || lease.Info.View != terrain.View ||
            _selection is not { } selection || !SpectateCovers(selection, eye))
            return "No loaded terrain at eye.\nWait, then try spectate on.";

        _spectate = new(lease.Info.View, eye, _yaw, _pitch);
        return $"Spectate on.\n{SpectateControls}";
    }

    private void StopSpectating()
    {
        if (_spectate is not { } spectate)
            return;
        _yaw = spectate.WalkingYaw;
        _pitch = spectate.WalkingPitch;
        _spectate = null;
    }

    private void RenderSpectate(VolumeStreamingCoordinator terrain, D3D12Renderer renderer, PlanetWalkState player,
        InputSnapshot input, TimeSpan frameTime, bool allowMovement)
    {
        if (_spectate is not { } spectate)
            return;
        var statistics = terrain.Statistics;
        if (statistics.Stopped || terrain.View != spectate.View || _published.View != spectate.View ||
            _selection is not { } selection)
        {
            StopSpectating();
            Notice("Spectate ended. Terrain view changed.");
            return;
        }

        var (direction, up) = WalkingView(spectate.Eye, player.Heading);
        var heldAtBoundary = false;
        if (allowMovement)
        {
            var forward = Axis(input, InputControl.W, InputControl.S);
            var strafe = Axis(input, InputControl.D, InputControl.A);
            var rise = Axis(input, InputControl.E, InputControl.Q);
            if (forward != 0 || strafe != 0 || rise != 0)
            {
                var right = Vector3.Normalize(Vector3.Cross(direction, up));
                var translation = direction * forward + right * strafe + up * rise;
                var distance = SpectateSpeedMetersPerSecond * Math.Clamp(frameTime.TotalSeconds, 0, 0.05);
                var movement = Vector3.Normalize(translation);
                var candidate = Add(spectate.Eye, new(movement.X * distance, movement.Y * distance, movement.Z * distance));
                heldAtBoundary = Length(Subtract(candidate, spectate.Origin)) > SpectateRangeMeters ||
                    !SpectateCovers(selection, candidate);
                if (!heldAtBoundary)
                {
                    spectate.Eye = candidate;
                    (direction, up) = WalkingView(candidate, player.Heading);
                }
            }
        }

        var camera = ApplyNearEnvironment(new(spectate.View, spectate.Eye, direction, up,
            _fieldOfView, 0.08f, 150, Vector4.One, Sky), _previewTime ?? _environmentTick, 0);
        Props?.Pump();
        var frame = ++_sequence;
        OrbitMaterialHost?.RetryMaterials(renderer, frame);
        RequireFrame(renderer.TrySubmitVolumeCamera(_published.Generation, frame, camera));
        Drain(terrain);

        if (overlay is null)
            return;
        var heading = heldAtBoundary ? "Spectate · travel limit" : "Spectate";
        overlay.Menu = $"{heading}\n{SpectateControls}";
        overlay.Performance = _debug ? FormattableString.Invariant(
            $"Spectate | {PlanetName}\nSeed {Descriptor.Seed} / gen {Descriptor.GeneratorVersion}\n{selection.MeshCount} regions / LOD 1/2 {_fineBricks}/{selection.Requests.Length - _fineBricks}\nCPU {statistics.Used.CpuBytes / 1048576d:F1} / GPU {statistics.Used.NativeBytes / 1048576d:F1} MiB\nFrame {frameTime.TotalMilliseconds:F1} ms\nCamera X {spectate.Eye.X:F1} Y {spectate.Eye.Y:F1} Z {spectate.Eye.Z:F1}\nPlayer X {player.Position.X:F1} Y {player.Position.Y:F1} Z {player.Position.Z:F1} / origin {_published.View.Frame.Revision}") : null;
    }

    private bool SpectateCovers(VolumeStreamingSelection selection, PlanetVector eye) =>
        StrideAt(selection, Descriptor.SampleAt(eye).Vertex.Locate().Brick) > 0;
}
