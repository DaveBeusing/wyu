using System.Diagnostics;
using System.Numerics;
using Groundwork.Core;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private async Task VerifySunlightAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState state, CancellationToken token)
    {
        if (_weather is null)
            throw new InvalidOperationException("The WP15 probe requires Package E.");
        state = await CommitInitialSmokePoseAsync(terrain, state, token).ConfigureAwait(false);
        var origin = Camera(terrain.View, state.Position, state.Heading);
        var up = Unit(state.Position);
        var forward = Unit(state.Heading);
        var midday = SunTick(0);
        var low = SunTick(Math.Acos(0.08 / Math.Sqrt(up.Y * up.Y + up.Z * up.Z)));
        var night = SunTick(Math.PI);
        var measure = Environment.GetEnvironmentVariable("WYU_WP15_PROBE") == "measure";
        var size = renderer.Read();
        if (measure && (size.AppliedWidth != 1920 || size.AppliedHeight != 1080))
            throw new InvalidOperationException($"Expected 1920x1080, got {size.AppliedWidth}x{size.AppliedHeight}.");

        foreach (var (phase, tick) in new[] { ("midday", midday), ("low", low), ("turn", low) })
        {
            var generation = _published.Generation;
            var count = measure ? 440 : 24;
            var timings = new long[count];
            var firstSequence = _sequence + 1;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < count; i++)
            {
                var camera = phase switch
                {
                    "turn" => origin with { Forward = Turn(measure ? 0.70f * MathF.Sin(i * MathF.Tau / 24) : i < count / 2 ? -0.70f : 0.70f) },
                    "low" when !measure => origin with { Position = Add(origin.Position, Scale(forward, 4 * i / (count - 1d))) },
                    _ => origin,
                };
                // Alternate exact and half-tick presentation samples while the
                // accepted clock advances. Both A/B variants replay this path.
                var sampleTick = tick + (measure ? (ulong)i : 0);
                var fraction = measure && (i & 1) != 0 ? 0.5 : 0;
                var frame = ApplyNearEnvironment(camera, sampleTick, fraction);
                VerifyLight(frame, expectedDirect: true);
                var begin = await BeginAsync(token).ConfigureAwait(false);
                terrain.BeginTick(begin);
                Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
                var submitted = Stopwatch.GetTimestamp();
                await Present(frame, generation).ConfigureAwait(false);
                timings[i] = Stopwatch.GetTimestamp() - submitted;
                Drain(terrain);
            }

            using var visible = terrain.TryRead();
            if (visible?.Info != _published || visible.Info.Generation != generation)
                throw new InvalidOperationException("Light and camera updates changed the terrain publication.");
            Console.WriteLine(FormattableString.Invariant(
                $"WP15 WINDOW {Environment.ProcessId} {phase} {start} {Stopwatch.GetTimestamp()} {size.AppliedWidth} {size.AppliedHeight}"));
            if (measure)
            {
                Console.WriteLine($"WP15 SEQUENCES {phase} {firstSequence + 40} {count - 40}");
                Console.WriteLine($"WP15 RETIRE {phase} {string.Join(',', timings.Skip(40))} {Stopwatch.Frequency}");
            }
        }
        if (measure)
            return;

        var nightFrame = ApplyNearEnvironment(origin, night, 0);
        VerifyLight(nightFrame, expectedDirect: false);
        await Present(nightFrame, _published.Generation).ConfigureAwait(false);
        if (!terrain.View.Frame.TryRebase(Add(state.Position, Scale(forward, 24)), out var shifted))
            throw new InvalidOperationException("WP15 rebase fixture stayed within the origin cell.");
        if (!await LoadAsync(terrain, renderer, state.Position, state.Heading, shifted, token).ConfigureAwait(false) ||
            terrain.View.Frame != shifted)
            throw new InvalidOperationException("WP15 terrain did not publish at the shifted origin.");
        var afterRebase = ApplyNearEnvironment(origin with { View = terrain.View }, low, 0);
        VerifyLight(afterRebase, expectedDirect: true);
        await Present(afterRebase, _published.Generation).ConfigureAwait(false);

        var revision = _published.TerrainRevision;
        var editPoint = Descriptor.SampleAt(Add(state.Position, Scale(forward, 6))).Vertex;
        var originalTick = _environmentTick;
        try
        {
            _environmentTick = low;
            if (!await EditAsync(terrain, renderer, state, editPoint, VolumeBrushOperation.Add, 1, token).ConfigureAwait(false)
                || _published.TerrainRevision != revision + 1)
                throw new InvalidOperationException("WP15 terrain edit did not reach first presentation.");
        }
        finally
        {
            _environmentTick = originalTick;
        }
        Console.WriteLine(FormattableString.Invariant(
            $"WP15 ROUTE seed={Descriptor.Seed} midday={midday} low={low} night={night} origin={shifted.Revision} edit={_published.TerrainRevision} generation={_published.Generation} sequence={renderer.Read().LastPresentedSequence}"));

        ulong SunTick(double offset)
        {
            var angle = (Math.Atan2(up.Y, up.Z) + offset + Math.Tau) % Math.Tau;
            return (ulong)Math.Round(angle * 36_000 / Math.Tau) % 36_000;
        }

        Vector3 Turn(float angle) => Vector3.Transform(origin.Forward,
            Quaternion.CreateFromAxisAngle(ProceduralGlobe.Float(up), angle));

        async Task Present(VolumeCamera camera, ulong generation)
        {
            var sequence = ++_sequence;
            RequireFrame(renderer.TrySubmitVolumeCamera(generation, sequence, camera));
            try
            {
                await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence == sequence, token).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"WP15 sequence {sequence}, generation {generation}, renderer {renderer.Read()}, window {window.Read().Phase}.", error);
            }
        }

        static void VerifyLight(VolumeCamera camera, bool expectedDirect)
        {
            if (!ReferenceSunlight.Enabled)
            {
                if (camera.Sunlight is not null || camera.TerrainShadows)
                    throw new InvalidOperationException("WP15 off submitted lighting.");
                return;
            }
            if (camera.Sunlight is not { } light || camera.TerrainShadows != expectedDirect ||
                (light.Direct.X > 0) != expectedDirect || camera.Tint != Vector4.One ||
                (camera.Sky is { } sky && sky.SunDirection != light.Direction))
                throw new InvalidOperationException("WP15 light, sky and cascade inputs disagree.");
        }
    }
}
