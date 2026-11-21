using System.Buffers.Binary;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed record InventoryImportResult(VolumeWorldSlot? Slot, VolumeSlotStatus Status, string? Reason = null);

internal sealed class VolumeWorldSlot(string root, Guid id, Guid lineage, Guid terrainWorld, ReferencePlanet? planet,
    CavePlanet? cave, bool orbitDemo = false) : IDisposable
{
    private const int LegacyManifestBytes = 32;
    private const int ManifestBytes = 49;
    private VolumeCheckpointWriter? _writer;
    private bool _retry;

    internal ReferencePlanet? Planet { get; } = planet;
    internal CavePlanet? Cave { get; } = cave;
    internal VolumeCheckpointSlot Storage { get; private set; } = new(root, id, lineage, (cave?.Descriptor ?? planet!.Descriptor), InventoryCheckpoint.Validate);
    internal VolumeCheckpoint? Checkpoint { get; private set; }
    internal bool OrbitDemo { get; } = orbitDemo;
    internal bool ReadOnly { get; private set; }
    internal bool HasPendingWrite => _retry;
    internal bool NeedsInventoryImport => Checkpoint is { } cut && !InventoryCheckpoint.IsCurrent(cut.Game);
    internal string? InventoryImportError => NeedsInventoryImport && Checkpoint is { } cut &&
        InventoryCheckpoint.Read(cut, out _, out _, out _) != SaveGameValidation.Valid
        ? "The original inventory cannot be represented within the new save limits. The original slot is preserved."
        : null;

    internal static WorldProfile DefaultProfile => WorldProfile.Connected;

    internal enum WorldProfile { Reference, Cave, Connected, OrbitDemo }

    internal static string ProfileName(WorldProfile profile) => profile switch
    {
        WorldProfile.OrbitDemo => OrbitDemoPreset.DisplayName,
        WorldProfile.Reference => ReferencePlanet.DisplayName,
        WorldProfile.Cave => CavePlanet.Name,
        WorldProfile.Connected => CavePlanet.ConnectedName,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static VolumeWorldSlot Create(string root, ulong seed, WorldProfile profile) =>
        Create(root, seed, cave: profile == WorldProfile.Cave, connected: profile == WorldProfile.Connected,
            orbitDemo: profile == WorldProfile.OrbitDemo);

    internal static ulong? ReadManifestSeed(string root, Guid id)
    {
        try
        {
            using var manifest = new FileStream(ManifestPath(root, id), FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> prefix = stackalloc byte[16];
            if (manifest.Length < prefix.Length)
                return null;
            manifest.ReadExactly(prefix);
            return prefix[..6].SequenceEqual("WYUVOL"u8) ? BinaryPrimitives.ReadUInt64LittleEndian(prefix[8..]) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static VolumeWorldSlot Create(string root, ulong seed, bool cave = false, bool connected = false,
        bool orbitDemo = false)
    {
        if (orbitDemo && (cave || connected))
            throw new ArgumentException("The Orbit demo uses the reference planet.", nameof(orbitDemo));
        var profile = orbitDemo ? WorldProfile.OrbitDemo : connected ? WorldProfile.Connected : cave ? WorldProfile.Cave : WorldProfile.Reference;
        var slot = Reserve(root, seed, profile);
        slot.PublishManifest(seed, profile);
        return slot;
    }

    internal static VolumeWorldSlot? Open(string root, Guid id, out VolumeSlotStatus status)
    {
        status = VolumeSlotStatus.Incompatible;
        if (id == Guid.Empty)
            return null;
        try
        {
            using var manifest = new FileStream(ManifestPath(root, id), FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = manifest.Length;
            if (length is not (LegacyManifestBytes or ManifestBytes))
                return null;
            Span<byte> bytes = stackalloc byte[(int)length];
            manifest.ReadExactly(bytes);
            var slotLineage = new Guid(bytes.Slice(16, 16));
            if (slotLineage == Guid.Empty || !TryReadProfile(bytes, id, out var profile, out var worldIdentity))
                return null;
            var seed = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
            if (profile == WorldProfile.Cave && seed != CavePlanet.Seed)
                return null;
            var slot = Describe(root, id, slotLineage, worldIdentity, seed, profile);
            var loaded = slot.Storage.Load();
            status = loaded.Status;
            if (loaded.Status is not (VolumeSlotStatus.Loaded or VolumeSlotStatus.ReadOnlyRecovery) || loaded.Checkpoint is null)
                return null;
            slot.Checkpoint = loaded.Checkpoint;
            slot.ReadOnly = loaded.Status == VolumeSlotStatus.ReadOnlyRecovery;
            return slot;
        }
        catch (IOException error)
        {
            status = (error.HResult & 0xffff) is 32 or 33 ? VolumeSlotStatus.Busy :
                error is FileNotFoundException or DirectoryNotFoundException ? VolumeSlotStatus.Incompatible : VolumeSlotStatus.StorageError;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            status = VolumeSlotStatus.StorageError;
            return null;
        }
    }

    internal static Guid[] List(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Select(Path.GetFileName)
            .Where(static name => Guid.TryParseExact(name, "N", out _)).Select(static name => Guid.ParseExact(name!, "N"))
            .Where(id => id != Guid.Empty && File.Exists(ManifestPath(root, id))).Order().ToArray()
        : [];

    internal VolumeSlotResult Save(VolumeCheckpointCut cut, ulong environmentTick, CancellationToken token)
    {
        var transferred = false;
        try
        {
            if (ReadOnly)
                return new(VolumeSlotStatus.ReadOnlyRecovery);
            if (NeedsInventoryImport)
                return new(VolumeSlotStatus.Incompatible);
            if (token.IsCancellationRequested)
                return new(VolumeSlotStatus.Canceled);
            if (_writer is null)
            {
                _writer = Storage.OpenWriter(out var opened);
                if (_writer is null)
                    return opened;
                if (opened.Checkpoint?.Generation != Checkpoint?.Generation)
                {
                    _writer.Dispose();
                    _writer = null;
                    return new(VolumeSlotStatus.Stale);
                }
            }

            var admitted = _writer.TryAdmit(cut, 1, (root, buffer) => root.State is CaveInventory state
                ? InventoryCheckpoint.Write(state.Items, state.Landing, root.Player, root.Terrain.TerrainRevision,
                    environmentTick, buffer.Span)
                : -1);
            if (admitted != VolumeCaptureAdmission.Accepted)
                return new(admitted switch
                {
                    VolumeCaptureAdmission.Busy => VolumeSlotStatus.Busy,
                    VolumeCaptureAdmission.Stale => VolumeSlotStatus.Stale,
                    _ => VolumeSlotStatus.InvalidCheckpoint
                });
            transferred = true;
            _retry = true;
            return Execute(token);
        }
        finally
        {
            if (!transferred)
                cut.Dispose();
        }
    }

    internal VolumeSlotResult Execute(CancellationToken token)
    {
        var result = _writer?.ExecutePending(token) ?? new VolumeSlotResult(VolumeSlotStatus.Empty);
        _retry = result.Status is VolumeSlotStatus.Busy or VolumeSlotStatus.Indeterminate or VolumeSlotStatus.StorageError;
        if (result.Status is VolumeSlotStatus.Committed or VolumeSlotStatus.AlreadyCommitted)
            Checkpoint = result.Checkpoint;
        return result;
    }

    internal InventoryImportResult ImportInventory(VolumeCheckpointCut cut, CancellationToken token)
    {
        var transferred = false;
        try
        {
            if (!NeedsInventoryImport || Checkpoint is not { } source)
                return new(null, VolumeSlotStatus.Incompatible, "The selected slot has no supported legacy checkpoint to import.");
            if (token.IsCancellationRequested)
                return new(null, VolumeSlotStatus.Canceled, "The import was canceled.");
            if (InventoryCheckpoint.Read(source, out var inventory, out var landing, out var environmentTick) != SaveGameValidation.Valid ||
                inventory is null)
                return new(null, VolumeSlotStatus.Incompatible, "The original inventory cannot be represented within the new save limits.");

            var expected = new byte[VolumeCheckpoint.MaximumGameBytes];
            var length = InventoryCheckpoint.Write(inventory, landing, source.Player, source.TerrainRevision, environmentTick, expected);
            if (length < 0)
                return new(null, VolumeSlotStatus.InvalidCheckpoint, "The imported game state exceeds its save budget.");
            var profile = OrbitDemo ? WorldProfile.OrbitDemo : Cave is null ? WorldProfile.Reference :
                Cave.Descriptor.GeneratorVersion == PlanetGeologyField.ConnectedGeneratorVersion ? WorldProfile.Connected : WorldProfile.Cave;

            // Keep terrain identity, but reserve a different save ID and lineage. Publish its menu entry only after readback.
            using var target = Reserve(root, source.Planet.Seed, profile, terrainWorld);
            transferred = true;
            var saved = target.Save(cut, environmentTick, token);
            if (target.HasPendingWrite)
                saved = target.Execute(token);
            var loaded = target.Storage.Load();
            if (loaded.Status != VolumeSlotStatus.Loaded || loaded.Checkpoint is not { } restored ||
                restored.Generation != 1 || restored.Planet != source.Planet || restored.Player != source.Player ||
                restored.TerrainRevision != source.TerrainRevision || !restored.Overlay.SequenceEqual(source.Overlay) ||
                !restored.Game.SequenceEqual(expected.AsSpan(0, length)))
            {
                var committed = saved.Status is VolumeSlotStatus.Committed or VolumeSlotStatus.AlreadyCommitted;
                return new(null, committed ? VolumeSlotStatus.InvalidCheckpoint : saved.Status,
                    committed ? "The new slot did not reproduce the original checkpoint." : $"The import could not save ({saved.Status}).");
            }

            target.PublishManifest(source.Planet.Seed, profile);
            var verified = Open(root, restored.Operation.SlotId, out var verifiedStatus);
            return verified is not null && verifiedStatus == VolumeSlotStatus.Loaded
                ? new(verified, VolumeSlotStatus.Committed)
                : new(null, verifiedStatus, $"The imported slot could not be opened ({verifiedStatus}).");
        }
        catch (IOException)
        {
            return new(null, VolumeSlotStatus.StorageError, "The new slot could not be written or read back.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, VolumeSlotStatus.StorageError, "The save folder does not permit the import.");
        }
        finally
        {
            if (!transferred)
                cut.Dispose();
        }
    }

    public void Dispose() => _writer?.Dispose();

    internal static bool SupportsMaterial(ushort material) =>
        material is (ushort)ReferenceTerrainMaterial.Basalt or (ushort)ReferenceTerrainMaterial.OchreStone;

    private static VolumeWorldSlot Reserve(string root, ulong seed, WorldProfile profile, Guid? worldIdentity = null)
    {
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid();
        var lineage = Guid.NewGuid();
        var slot = Describe(root, id, lineage, worldIdentity ?? id, seed, profile);
        slot.Storage = VolumeCheckpointSlot.Reserve(root, id, lineage, slot.Cave?.Descriptor ?? slot.Planet!.Descriptor,
            InventoryCheckpoint.Validate);
        return slot;
    }

    private static VolumeWorldSlot Describe(string root, Guid id, Guid lineage, Guid worldIdentity, ulong seed, WorldProfile profile)
    {
        var cave = profile is WorldProfile.Cave or WorldProfile.Connected
            ? new CavePlanet(worldIdentity, seed, profile == WorldProfile.Connected) : null;
        return new(root, id, lineage, worldIdentity, cave is null ? new ReferencePlanet(worldIdentity, seed) : null,
            cave, profile == WorldProfile.OrbitDemo);
    }

    private static bool TryReadProfile(ReadOnlySpan<byte> bytes, Guid id, out WorldProfile profile, out Guid worldIdentity)
    {
        var magic = bytes[..8];
        if (bytes.Length == ManifestBytes && magic.SequenceEqual("WYUVOL05"u8))
        {
            profile = (WorldProfile)bytes[48];
            worldIdentity = new(bytes.Slice(32, 16));
            return worldIdentity != Guid.Empty && Enum.IsDefined(profile);
        }

        worldIdentity = id;
        profile = magic.SequenceEqual("WYUVOL01"u8) ? WorldProfile.Reference :
            magic.SequenceEqual("WYUVOL02"u8) ? WorldProfile.Cave :
            magic.SequenceEqual("WYUVOL03"u8) ? WorldProfile.Connected :
            magic.SequenceEqual("WYUVOL04"u8) ? WorldProfile.OrbitDemo : (WorldProfile)(-1);
        return bytes.Length == LegacyManifestBytes && Enum.IsDefined(profile);
    }

    private void PublishManifest(ulong seed, WorldProfile profile)
    {
        Span<byte> bytes = stackalloc byte[ManifestBytes];
        "WYUVOL05"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], seed);
        lineage.TryWriteBytes(bytes[16..]);
        terrainWorld.TryWriteBytes(bytes[32..]);
        bytes[48] = (byte)profile;
        var path = ManifestPath(root, id);
        var candidate = path + ".pending";
        using (var manifest = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            manifest.Write(bytes);
            manifest.Flush(flushToDisk: true);
        }
        File.Move(candidate, path);
    }

    private static string ManifestPath(string root, Guid id) => Path.Combine(root, $"{id:N}.wyu");
}
