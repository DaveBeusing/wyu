using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Simulation;

namespace WorldsYetUnseen.Client;

internal sealed class SceneSubmission(IRenderer renderer, bool terrain)
{
    private static readonly Vector4 ClearColor = new(0.04f, 0.08f, 0.12f, 1);
    private static readonly Vector4 CubeTint = new(0.28f, 0.76f, 0.94f, 1);
    private static readonly Matrix4x4 View = Matrix4x4.CreateLookAt(new Vector3(2, 1.5f, 3), Vector3.Zero, Vector3.UnitY);
    private ulong _submittedTick;

    internal void Submit(TimeProvider clock, in SimulationIteration iteration, ulong publishedTick)
    {
        // Keep simulation debt ahead of rendering and compose only from published tick values.
        if (iteration.State != SimulationState.Running || publishedTick <= _submittedTick ||
            publishedTick != iteration.CompletedTick ||
            iteration.NextWakeTimestamp is long next && next <= clock.GetTimestamp())
            return;

        var angle = (publishedTick % 360) * (MathF.Tau / 360);
        var scene = terrain
            ? TerrainScene.Camera
            : new RenderScene(Matrix4x4.CreateRotationY(angle), CubeTint, View, MathF.PI / 3, 0.1f, 20);
        var submission = renderer.TrySubmit(new RenderFrame(publishedTick, ClearColor, scene));
        if (submission is RenderSubmissionResult.Accepted or RenderSubmissionResult.Replaced)
            _submittedTick = publishedTick;
        else if (submission != RenderSubmissionResult.Closed)
            throw new InvalidOperationException($"The D3D12 renderer rejected the published scene frame ({submission}).");
    }
}
