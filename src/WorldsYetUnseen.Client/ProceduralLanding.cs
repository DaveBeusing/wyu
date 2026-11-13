using System.Buffers.Binary;
using Groundwork.Persistence;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal readonly record struct ProceduralLanding(PlanetVector Foot, ulong Revision, int FlightTick)
{
    private const int LandingBytes = 36;
    private const int HeaderBytes = 17;

    internal static int Write(ProceduralLanding? landed, PlanetWalkState? player, ulong revision, ulong environmentTick,
        Span<byte> destination)
    {
        "WYUVOL03"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], environmentTick);
        destination[16] = 0;
        if (landed is not { } value || player is not { } pose || value.Revision != revision ||
            Length(Subtract(value.Foot, pose.Position)) > 16)
            return HeaderBytes;
        destination[16] = 1;
        var body = destination[HeaderBytes..];
        BinaryPrimitives.WriteDoubleLittleEndian(body, value.Foot.X);
        BinaryPrimitives.WriteDoubleLittleEndian(body[8..], value.Foot.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(body[16..], value.Foot.Z);
        BinaryPrimitives.WriteUInt64LittleEndian(body[24..], value.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(body[32..], value.FlightTick);
        return HeaderBytes + LandingBytes;
    }

    internal static SaveGameValidation Read(VolumeCheckpoint checkpoint, out ProceduralLanding? landed) =>
        Read(checkpoint, out landed, out _);

    internal static SaveGameValidation Read(VolumeCheckpoint checkpoint, out ProceduralLanding? landed,
        out ulong environmentTick) => Read(checkpoint, checkpoint.Game, out landed, out environmentTick);

    internal static SaveGameValidation Read(VolumeCheckpoint checkpoint, ReadOnlySpan<byte> bytes,
        out ProceduralLanding? landed, out ulong environmentTick)
    {
        landed = null;
        environmentTick = ReferenceWeather.InitialTick;
        if (checkpoint.Player is null)
            return SaveGameValidation.Malformed;
        if (bytes.SequenceEqual("WYUVOL01"u8))
            return SaveGameValidation.Valid;
        if (bytes.Length < 8)
            return SaveGameValidation.Unsupported;
        var magic = bytes[..8];
        if (magic.SequenceEqual("WYUVOL03"u8))
        {
            if (bytes.Length < HeaderBytes || bytes[16] > 1 ||
                bytes.Length != HeaderBytes + bytes[16] * LandingBytes)
                return SaveGameValidation.Malformed;
            environmentTick = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
            if (bytes[16] == 0)
                return SaveGameValidation.Valid;
            bytes = bytes[HeaderBytes..];
        }
        else if (magic.SequenceEqual("WYUVOL02"u8))
        {
            if (bytes.Length != 8 + LandingBytes)
                return SaveGameValidation.Malformed;
            bytes = bytes[8..];
        }
        else
            return SaveGameValidation.Unsupported;
        var foot = new PlanetVector(BinaryPrimitives.ReadDoubleLittleEndian(bytes),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[8..]), BinaryPrimitives.ReadDoubleLittleEndian(bytes[16..]));
        var revision = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
        var tick = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        var radius = Length(foot);
        if (!double.IsFinite(radius) || Math.Abs(radius - checkpoint.Planet.RadiusMeters) > 64 ||
            revision != checkpoint.TerrainRevision || tick is <= 0 or > 30_000 ||
            Length(Subtract(foot, checkpoint.Player.Value.Position)) > 16)
            return SaveGameValidation.Malformed;
        landed = new(foot, revision, tick);
        return SaveGameValidation.Valid;
    }
}
