using System.Buffers.Binary;
using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Rendering;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed class PrototypeSaveSmoke(IRuntimeOwnership ownership)
{
    private const int Unit = VoxelCollision.UnitsPerVoxel;
    private static readonly VoxelPose MiningPose = new(-12 * Unit, 4 * Unit, 15 * Unit / 2);
    private static readonly VoxelPose BeaconPose = new(-31 * Unit / 2, 4 * Unit, 19 * Unit / 2);
    private static readonly VoxelPose CompletePose = new(-29 * Unit / 2, 4 * Unit, 19 * Unit / 2);

    internal PrototypeSave? Save { get; private set; }
    internal WalkableScene? Scene { get; private set; }

    internal async Task RunAsync(string phase, string directory, string token, CancellationToken ct)
    {
        var fixture = Path.Combine(directory, "wyu-prototype-save.fixture");
        if (!Guid.TryParseExact(token, "N", out _) ||
            (await File.ReadAllTextAsync(fixture, ct).ConfigureAwait(false)).Trim() != token)
            throw new InvalidOperationException("The WP07 save smoke requires its own test fixture.");

        var first = new PrototypeCheckpoint(MiningPose, Mine(default, Deposit.West));
        var partial = new PrototypeCheckpoint(BeaconPose,
            Build(Mine(first.Progress, Deposit.Seam), BeaconModule.Foundation));
        var offline = new PrototypeCheckpoint(BeaconPose,
            Build(Build(Mine(partial.Progress, Deposit.East), BeaconModule.Mast), BeaconModule.SignalHead));
        Require(offline.Progress.Activate(out var activated) == ActionResult.Applied, "activate the finished beacon");
        var complete = new PrototypeCheckpoint(CompletePose, activated);

        var (probe, generation) = phase switch
        {
            "--prototype-save-before-publish" => ("save.pending", 2UL),
            "--prototype-save-after-publish" => ("save.b", 2UL),
            "--prototype-save-finish" => ("save.pending", 3UL),
            _ => (null, 0UL)
        };
        var armed = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = await PrototypeSave.OpenAsync(directory, ownership, ct, probe is null ? null : PauseWriter)
            .ConfigureAwait(false);
        Save = save;
        switch (phase)
        {
            case "--prototype-save-mission-fixture":
                Require(save.InitialLoad.Status == SaveSlotStatus.Empty, "start from an empty mission fixture");
                var construction = new PrototypeCheckpoint(BeaconPose, Build(Mine(partial.Progress, Deposit.East), BeaconModule.Mast));
                await CommitAsync(save, construction, 1, ct).ConfigureAwait(false);
                await VerifyRegionAsync(construction, ct).ConfigureAwait(false);
                Console.WriteLine("PASS: WP10 saved a partially built beacon.");
                break;
            case "--prototype-save-init":
                Require(save.InitialLoad.Status == SaveSlotStatus.Empty, "start from an empty slot");
                await CommitAsync(save, first, 1, ct).ConfigureAwait(false);
                Console.WriteLine("PASS: WP07 committed the mining checkpoint.");
                break;
            case "--prototype-save-before-publish":
            case "--prototype-save-after-publish":
                RequireCheckpoint(save, 1, first);
                if (phase == "--prototype-save-after-publish")
                    await VerifyRegionAsync(first, ct).ConfigureAwait(false);
                armed = true;
                Require(save.RequestSave(partial), "queue the interrupted checkpoint");
                var unexpected = await save.WaitForSaveAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException($"WP07 interrupted save unexpectedly returned {unexpected.Status}.");
            case "--prototype-save-finish":
                RequireCheckpoint(save, 2, partial);
                await VerifyRegionAsync(partial, ct).ConfigureAwait(false);
                armed = true;
                Require(save.RequestSave(offline), "queue the pre-activation checkpoint");
                try
                {
                    await reached.Task.WaitAsync(ct).ConfigureAwait(false);
                    Require(offline.Progress.Activate(out var live) == ActionResult.Applied,
                        "activate while the previous checkpoint is staged");
                    Require(live == complete.Progress && !save.RequestSave(complete), "retain the captured checkpoint and reject a second active save");
                }
                finally
                {
                    release.TrySetResult();
                }
                Require((await save.WaitForSaveAsync(ct).ConfigureAwait(false)).Status == SaveSlotStatus.Committed,
                    "commit the unchanged pre-activation snapshot");
                RequireCheckpoint(save, 3, offline);
                var reopened = await PrototypeSave.OpenAsync(directory, ownership, ct).ConfigureAwait(false);
                RequireCheckpoint(reopened, 3, offline);
                reopened.Stop();
                await reopened.SettleAsync(ct).ConfigureAwait(false);
                await CommitAsync(save, complete, 4, ct).ConfigureAwait(false);
                Console.WriteLine("PASS: WP07 resumed partial construction and committed the rescue signal without mutating the staged checkpoint.");
                break;
            case "--prototype-save-verify":
                RequireCheckpoint(save, 4, complete);
                await VerifyRegionAsync(complete, ct).ConfigureAwait(false);
                await VerifyRejectedNewestAsync(directory, first, ct).ConfigureAwait(false);
                Console.WriteLine("PASS: WP07 restored the completed beacon and protected invalid or unsupported checkpoints.");
                break;
            default:
                throw new InvalidOperationException("Unexpected WP07 save smoke phase.");
        }

        void PauseWriter()
        {
            if (!armed || probe is null)
                return;
            var path = Path.Combine(directory, probe);
            if (!File.Exists(path))
                return;
            var frame = File.ReadAllBytes(path);
            if (frame.Length < 24 || BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(16)) != generation)
                return;

            // The production slot resolves the generator when checking the flushed candidate
            // and again when checking the atomically installed file.
            if (phase == "--prototype-save-finish")
                reached.TrySetResult();
            else
                File.WriteAllText(Path.Combine(directory, "wyu-prototype-save.ready"), $"{token} {Environment.ProcessId} {phase}");
            release.Task.WaitAsync(ct).GetAwaiter().GetResult();
        }
    }

    private async Task VerifyRegionAsync(PrototypeCheckpoint checkpoint, CancellationToken ct)
    {
        var scene = new WalkableScene(ownership, checkpoint.Pose, PrototypeWorld.EffectiveTerrain(checkpoint.Progress),
            snapshot: PrototypeWorld.Snapshot, bounds: PrototypeWorld.Recipe.Bounds);
        Scene = scene;
        var meshes = await scene.StartAsync(ct).ConfigureAwait(false);
        var seamPresent = (checkpoint.Progress.MinedDeposits & DepositMask.Seam) == 0;
        Require(scene.Pose == checkpoint.Pose && scene.ReadCell(-10, 4, 7) == VoxelOccupancy.Air &&
            scene.ReadCell(0, 4, 7) == (seamPresent ? VoxelOccupancy.Solid : VoxelOccupancy.Air) &&
            CountSeamTriangles(meshes) == (seamPresent ? 2 : 0), "rebuild mined terrain, seam mesh and collision");
        foreach (var module in Enum.GetValues<BeaconModule>())
        {
            var (x, y, z) = PrototypeWorld.ModuleCell(module);
            Require(scene.ReadCell(x, y, z) == ((int)module < checkpoint.Progress.InstalledModules
                ? VoxelOccupancy.Solid : VoxelOccupancy.Air), $"reconstruct the {module} socket");
        }
    }

    private async Task VerifyRejectedNewestAsync(string directory, PrototypeCheckpoint first, CancellationToken ct)
    {
        var rejectedDirectory = Path.Combine(directory, "rejected");
        var raw = new SaveSlot(rejectedDirectory, PrototypeWorld.ResolveGenerator);
        var lineage = Guid.NewGuid();
        var valid = first.Create(new SaveFrameIdentity(1, lineage, Guid.NewGuid()));
        Require(raw.Save(0, valid, ct).Status == SaveSlotStatus.Committed, "seed the older compatible checkpoint");

        var malformedBytes = valid.GamePayload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(malformedBytes.AsSpan(12), uint.MaxValue);
        var malformed = new SaveCheckpoint(new SaveFrameIdentity(2, lineage, Guid.NewGuid()), valid.Recipe, valid.Terrain,
            valid.GameSchemaVersion, malformedBytes);
        Require(raw.Save(1, malformed, ct).Status == SaveSlotStatus.Committed, "seed the malformed newest checkpoint");
        var recovered = await PrototypeSave.OpenAsync(rejectedDirectory, ownership, ct).ConfigureAwait(false);
        RequireCheckpoint(recovered, 1, first);
        Require(recovered.InitialLoad.Recovery is not null, "report the recovered generation");
        recovered.Stop();
        await recovered.SettleAsync(ct).ConfigureAwait(false);

        var unsupported = new SaveCheckpoint(new SaveFrameIdentity(3, lineage, Guid.NewGuid()), valid.Recipe, valid.Terrain,
            valid.GameSchemaVersion + 1, valid.GamePayload);
        Require(raw.Save(2, unsupported, ct).Status == SaveSlotStatus.Committed, "seed an unsupported newest checkpoint");
        var newestPath = Path.Combine(rejectedDirectory, "save.a");
        var newestBytes = File.ReadAllBytes(newestPath);
        var blocked = await PrototypeSave.OpenAsync(rejectedDirectory, ownership, ct).ConfigureAwait(false);
        Require(blocked.InitialLoad.Status == SaveSlotStatus.Unsupported && blocked.Checkpoint is null &&
            !blocked.RequestSave(first) && newestBytes.AsSpan().SequenceEqual(File.ReadAllBytes(newestPath)),
            "block resume and new writes while preserving the unsupported newest checkpoint");
        blocked.Stop();
        await blocked.SettleAsync(ct).ConfigureAwait(false);
    }

    private static int CountSeamTriangles(RenderMeshRegion meshes)
    {
        foreach (var instance in meshes.Instances)
        {
            if (instance.Translation != System.Numerics.Vector3.Zero)
                continue;
            var positions = instance.Mesh.Positions;
            var indices = instance.Mesh.Indices;
            var count = 0;
            for (var i = 0; i < indices.Length; i += 3)
            {
                var a = positions[(int)indices[i]];
                var b = positions[(int)indices[i + 1]];
                var c = positions[(int)indices[i + 2]];
                var center = (a + b + c) / 3;
                if (a.X == 0 && b.X == 0 && c.X == 0 && center.Y is > 4 and < 5 && center.Z is > 7 and < 8)
                    count++;
            }
            return count;
        }

        throw new InvalidOperationException("WP07 restored region omitted the seam mesh.");
    }

    private static async Task CommitAsync(PrototypeSave save, PrototypeCheckpoint checkpoint, ulong generation, CancellationToken ct)
    {
        Require(save.RequestSave(checkpoint), "queue the checkpoint");
        Require((await save.WaitForSaveAsync(ct).ConfigureAwait(false)).Status == SaveSlotStatus.Committed, "commit the checkpoint");
        RequireCheckpoint(save, generation, checkpoint);
    }

    private static void RequireCheckpoint(PrototypeSave save, ulong generation, PrototypeCheckpoint checkpoint) =>
        Require(save.Generation == generation && save.Checkpoint == checkpoint, $"resume complete generation {generation}");

    private static PrototypeProgress Mine(PrototypeProgress progress, Deposit deposit)
    {
        Require(progress.Mine(deposit, out var next) == ActionResult.Applied, $"mine {deposit}");
        return next;
    }

    private static PrototypeProgress Build(PrototypeProgress progress, BeaconModule module)
    {
        Require(progress.Build(module, out var next) == ActionResult.Applied, $"build {module}");
        return next;
    }

    private static void Require(bool condition, string check)
    {
        if (!condition)
            throw new InvalidOperationException($"WP07 save smoke did not {check}.");
    }
}
