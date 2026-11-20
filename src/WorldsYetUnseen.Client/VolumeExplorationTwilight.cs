using System.Numerics;
using System.Text.Json;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private async Task VerifyTwilightAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState state, CancellationToken token)
    {
        if (_weather is null || !ReferenceSky.Enabled)
            throw new InvalidOperationException("The twilight route requires Package E and G.");
        state = await CommitInitialSmokePoseAsync(terrain, state, token).ConfigureAwait(false);
        var origin = Camera(terrain.View, state.Position, state.Heading);
        var radial = Vector3.Normalize(ProceduralGlobe.Float(origin.Position));
        var baseTick = orbitDemo ? ReferenceWeather.InitialTick : 0;
        var cosineSun = orbitDemo ? OrbitDemoPreset.SunDirection(baseTick, 0) : Vector3.UnitZ;
        var sineSun = orbitDemo ? OrbitDemoPreset.SunDirection(baseTick + 9_000, 0) : Vector3.UnitY;
        var solarCosine = Vector3.Dot(radial, cosineSun);
        var solarSine = Vector3.Dot(radial, sineSun);
        var latitude = Math.Sqrt(solarCosine * solarCosine + solarSine * solarSine);
        if (latitude < (orbitDemo ? 0.05 : 0.6))
            throw new InvalidOperationException("The twilight route cannot cross this preset's solar horizon.");
        var noonAngle = Math.Atan2(solarSine, solarCosine);
        var minimum = Math.Max(-0.45, -0.95 * latitude);
        var maximum = Math.Min(0.50, 0.95 * latitude);
        var originalTick = _environmentTick;
        var publication = _published;
        var output = Environment.GetEnvironmentVariable("WYU_DAWN_CAPTURE_DIR");
        if (output is not null && !Directory.Exists(output))
            throw new InvalidOperationException("Create the twilight capture directory before running the route.");
        using var captureWindow = output is null ? null : new SkyPixels.CaptureWindow();
        foreach (var dawn in new[] { true, false })
        {
            var phase = dawn ? "sunrise" : "sunset";
            var horizon = SampleTick(0, dawn);
            var horizonSun = orbitDemo ? OrbitDemoPreset.SunDirection(horizon.Tick, horizon.Fraction)
                : ReferenceWeather.SunDirection(horizon.Tick, horizon.Fraction);
            var horizontal = Vector3.Normalize(horizonSun - radial * Vector3.Dot(horizonSun, radial));
            var fixedView = origin with { Forward = Vector3.Normalize(horizontal + 0.10f * radial), Up = radial };
            for (var frame = 0; frame <= 144; frame++)
            {
                var elevation = dawn ? minimum + (maximum - minimum) * frame / 144
                    : maximum - (maximum - minimum) * frame / 144;
                var (tick, fraction) = SampleTick(elevation, dawn);
                var camera = ApplyNearEnvironment(fixedView, tick, fraction);
                if (camera.Sky is not { } sky || camera.Sunlight is { } light && light.Direction != sky.SunDirection)
                    throw new InvalidOperationException("Twilight sun/sky inputs disagree.");
                if (Math.Abs(Vector3.Dot(radial, sky.SunDirection) - elevation) > 0.00001)
                    throw new InvalidOperationException("Twilight sampling disagrees with the preset's actual solar elevation.");
                // Retire bounded renderer receipts through the normal empty engine
                // barriers. This does not advance the accepted environment clock.
                var begin = await BeginAsync(token).ConfigureAwait(false);
                terrain.BeginTick(begin);
                Finish(await SealAsync(Groundwork.Core.BarrierKind.CommitResults, token).ConfigureAwait(false));
                var sequence = ++_sequence;
                OrbitMaterialHost?.RetryMaterials(renderer, sequence);
                Props?.Pump();
                RequireFrame(renderer.TrySubmitVolumeCamera(publication.Generation, sequence, camera));
                try
                {
                    await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence == sequence, token).ConfigureAwait(false);
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"Twilight {phase}/{frame}, sequence {sequence}, renderer {renderer.Read()}, diagnostics {renderer.ReadDiagnostics()}.", error);
                }
                Drain(terrain);
                if (frame % 24 == 0)
                {
                    var metadata = JsonSerializer.Serialize(new
                    {
                        phase, frame, Descriptor.Seed, tick, fraction, solarElevation = elevation,
                        eye = new[] { origin.Position.X, origin.Position.Y, origin.Position.Z },
                        forward = new[] { fixedView.Forward.X, fixedView.Forward.Y, fixedView.Forward.Z },
                        fov = fixedView.FieldOfView, profile = ReferenceSky.Profile,
                        daylight = ReferenceDaylight.ProfileId, weather = ReferenceWeather.RecipeId,
                        haze = camera.Fog?.MaxOpacity, wp15 = orbitDemo ? ReferenceSunlight.OrbitEnabled : ReferenceSunlight.Enabled,
                        composition = orbitDemo ? "orbit-combined" : "far-horizon",
                    });
                    Console.WriteLine($"TWILIGHT FRAME {metadata}");
                    if (output is not null)
                    {
                        await Task.Delay(50, token).ConfigureAwait(false);
                        var size = renderer.Read();
                        captureWindow!.RequireUnobstructed(size.AppliedWidth, size.AppliedHeight);
                        SkyPixels.Capture(size.AppliedWidth, size.AppliedHeight, clientSurface: true)
                            .SaveBmp(Path.Combine(output, $"{phase}-{frame:D3}.bmp"));
                        await File.WriteAllTextAsync(Path.Combine(output, $"{phase}-{frame:D3}.json"), metadata, token)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        if (_environmentTick != originalTick || _published != publication)
            throw new InvalidOperationException("The presentation-only twilight route changed the environment or terrain.");
        Console.WriteLine("PASS: WYU animated sunrise/sunset with unchanged clock and terrain publication.");

        (ulong Tick, double Fraction) SampleTick(double elevation, bool dawn)
        {
            var delta = Math.Acos(elevation / latitude);
            var angle = (noonAngle + (dawn ? -delta : delta) + Math.Tau) % Math.Tau;
            var time = (baseTick + angle * (36_000 / Math.Tau)) % 36_000;
            var tick = (ulong)Math.Floor(time);
            return (tick, time - tick);
        }
    }
}
