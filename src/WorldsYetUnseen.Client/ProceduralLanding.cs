using System.Buffers.Binary;
using Groundwork.Persistence;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal readonly record struct ProceduralLanding(PlanetVector Foot, ulong Revision, int FlightTick)
{
    private const int Bytes = 44;

    internal static int Write(ProceduralLanding? landed, PlanetWalkState? player, ulong revision, Span<byte> destination)
    {
        if (landed is not { } value || player is not { } pose || value.Revision != revision ||
            Length(Subtract(value.Foot, pose.Position)) > 16)
        {
            "WYUVOL01"u8.CopyTo(destination);
            return 8;
        }
        "WYUVOL02"u8.CopyTo(destination);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[8..], value.Foot.X);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[16..], value.Foot.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(destination[24..], value.Foot.Z);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], value.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(destination[40..], value.FlightTick);
        return Bytes;
    }

    internal static SaveGameValidation Read(VolumeCheckpoint checkpoint, out ProceduralLanding? landed)
    {
        landed = null;
        if (checkpoint.Player is null)
            return SaveGameValidation.Malformed;
        var bytes = checkpoint.Game;
        if (bytes.SequenceEqual("WYUVOL01"u8))
            return SaveGameValidation.Valid;
        if (bytes.Length < 8 || !bytes[..8].SequenceEqual("WYUVOL02"u8))
            return SaveGameValidation.Unsupported;
        if (bytes.Length != Bytes)
            return SaveGameValidation.Malformed;
        var foot = new PlanetVector(BinaryPrimitives.ReadDoubleLittleEndian(bytes[8..]),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[16..]), BinaryPrimitives.ReadDoubleLittleEndian(bytes[24..]));
        var revision = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]);
        var tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]);
        var radius = Length(foot);
        if (!double.IsFinite(radius) || Math.Abs(radius - checkpoint.Planet.RadiusMeters) > 64 ||
            revision != checkpoint.TerrainRevision || tick is <= 0 or > 30_000 ||
            Length(Subtract(foot, checkpoint.Player.Value.Position)) > 16)
            return SaveGameValidation.Malformed;
        landed = new(foot, revision, tick);
        return SaveGameValidation.Valid;
    }
}
