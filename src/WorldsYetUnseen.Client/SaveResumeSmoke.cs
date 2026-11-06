using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Voxels;
using System.Buffers.Binary;

namespace WorldsYetUnseen.Client;

internal sealed class SaveResumeSmoke(IRuntimeOwnership ownership)
{
    private const int Unit = VoxelCollision.UnitsPerVoxel;
    private static readonly VoxelPose SavedPose = WalkableTerrain.Spawn with { X = -12 * Unit - Unit / 2 };
    private static readonly SavedItem[] SavedInventory = [new(1, 3)];

    internal WorldSave? Save { get; private set; }
    internal WalkableScene? Scene { get; private set; }

    internal async Task RunAsync(string phase, string directory, string token, CancellationToken ct)
    {
        if (!Guid.TryParseExact(token, "N", out _) ||
            (await File.ReadAllTextAsync(Path.Combine(directory, "wyu-save-smoke.fixture"), ct).ConfigureAwait(false)).Trim() != token)
            throw new InvalidOperationException("The save smoke requires its own test fixture.");

        var save = await WorldSave.OpenAsync(directory, ownership, ct,
            phase == "--save-smoke-interrupt" ? PauseBeforeInstall : null).ConfigureAwait(false);
        Save = save;
        if (phase == "--save-smoke-init")
        {
            if (save.Generation != 0 || !WalkableTerrain.TryToggle(SavedPose, [], out var terrain) ||
                !save.RequestSave(SavedPose, terrain, SavedInventory) ||
                (await save.WaitForSaveAsync(ct).ConfigureAwait(false)).Status != SaveSlotStatus.Committed)
                throw new InvalidOperationException("The first terrain edit was not committed.");
            return;
        }

        if (save.Generation != 1 || save.Pose != SavedPose || !save.Inventory.Span.SequenceEqual(SavedInventory) ||
            save.Terrain.Length != 1 || save.Terrain.Span[0].Coordinate != WalkableTerrain.EditChunk ||
            !save.Terrain.Span[0].Cells.SequenceEqual([new TerrainCellDelta(WalkableTerrain.EditIndex, 0)]))
            throw new InvalidOperationException("The slot did not restore the complete committed checkpoint.");

        if (phase == "--save-smoke-interrupt")
        {
            if (!save.RequestSave(WalkableTerrain.Spawn, [], [new SavedItem(1, 4)]))
                throw new InvalidOperationException("The second save could not be queued.");
            var result = await save.WaitForSaveAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"The interrupted save returned unexpectedly ({result.Status}).");
        }

        var scene = new WalkableScene(ownership, save.Pose, save.Terrain.Span);
        Scene = scene;
        await scene.StartAsync(ct).ConfigureAwait(false);
        if (scene.Pose != SavedPose || scene.ReadCell(-1, 3, 4) != VoxelOccupancy.Air ||
            scene.ReadCell(0, 3, 4) != VoxelOccupancy.Solid)
            throw new InvalidOperationException("The resumed region did not contain the restored edit and its unmodified neighbor.");

        void PauseBeforeInstall()
        {
            // SaveSlot validates the closed, flushed candidate before atomically installing it.
            var pending = Path.Combine(directory, "save.pending");
            if (!File.Exists(pending))
                return;
            var frame = File.ReadAllBytes(pending);
            if (frame.Length < 24 || BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(16)) != 2)
                return;
            File.WriteAllText(Path.Combine(directory, "wyu-save-smoke.ready"), $"{token} {Environment.ProcessId}");
            using var gate = new ManualResetEventSlim();
            gate.Wait(ct);
        }
    }
}
