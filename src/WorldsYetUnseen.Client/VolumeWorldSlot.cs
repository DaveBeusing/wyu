using System.Buffers.Binary;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed class VolumeWorldSlot(string root, Guid id, Guid lineage, ReferencePlanet planet) : IDisposable
{
    private const int ManifestBytes = 32;
    private VolumeCheckpointWriter? _writer;
    private bool _retry;

    internal ReferencePlanet Planet { get; } = planet;
    internal VolumeCheckpointSlot Storage { get; private set; } = new(root, id, lineage, planet.Descriptor, Validate);
    internal VolumeCheckpoint? Checkpoint { get; private set; }
    internal bool ReadOnly { get; private set; }
    internal bool HasPendingWrite => _retry;

    internal static VolumeWorldSlot Create(string root, ulong seed)
    {
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid();
        var lineage = Guid.NewGuid();
        var planet = new ReferencePlanet(id, seed);
        var slot = new VolumeWorldSlot(root, id, lineage, planet)
        {
            Storage = VolumeCheckpointSlot.Reserve(root, id, lineage, planet.Descriptor, Validate)
        };
        Span<byte> bytes = stackalloc byte[ManifestBytes];
        "WYUVOL01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], seed);
        lineage.TryWriteBytes(bytes[16..]);
        using var manifest = new FileStream(ManifestPath(root, id), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        manifest.Write(bytes);
        manifest.Flush(flushToDisk: true);
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
            Span<byte> bytes = stackalloc byte[ManifestBytes];
            if (manifest.Length != bytes.Length)
                return null;
            manifest.ReadExactly(bytes);
            if (!bytes[..8].SequenceEqual("WYUVOL01"u8) || new Guid(bytes[16..]) == Guid.Empty)
                return null;
            var planet = new ReferencePlanet(id, BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]));
            var slot = new VolumeWorldSlot(root, id, new Guid(bytes[16..]), planet);
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
            .Where(static name => Guid.TryParseExact(name, "N", out _))
            .Select(static name => Guid.ParseExact(name!, "N")).Where(static id => id != Guid.Empty).Order().ToArray()
        : [];

    internal VolumeSlotResult Save(VolumeCheckpointCut cut, CancellationToken token)
    {
        var transferred = false;
        try
        {
            if (ReadOnly)
                return new(VolumeSlotStatus.ReadOnlyRecovery);
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

            var admitted = _writer.TryAdmit(cut, 1, static (_, buffer) =>
            {
                "WYUVOL01"u8.CopyTo(buffer.Span);
                return 8;
            });
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

    public void Dispose() => _writer?.Dispose();

    internal static bool SupportsMaterial(ushort material) =>
        material is (ushort)ReferenceTerrainMaterial.Basalt or (ushort)ReferenceTerrainMaterial.OchreStone;

    private static SaveGameValidation Validate(VolumeCheckpoint checkpoint) =>
        !checkpoint.Game.SequenceEqual("WYUVOL01"u8) ? SaveGameValidation.Unsupported :
        checkpoint.Player is null ? SaveGameValidation.Malformed : SaveGameValidation.Valid;

    private static string ManifestPath(string root, Guid id) => Path.Combine(root, $"{id:N}.wyu");
}
