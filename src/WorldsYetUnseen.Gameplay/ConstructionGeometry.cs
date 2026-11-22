using System.Diagnostics.CodeAnalysis;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Gameplay;

public enum ConstructionBlockType : byte
{
    Cube = 1,
    [SuppressMessage("Naming", "CA1720:Identifier contains type name",
        Justification = "Long identifies the elongated construction block, independently of its numeric ID type.")]
    Long = 2
}

public readonly record struct ConstructionAnchor(uint Id, long XMillimeters, long YMillimeters, long ZMillimeters,
    uint FrameVersion)
{
    public const uint CurrentFrameVersion = 1;
    // Keep millimetre coordinates exactly convertible to double before division.
    private const long CoordinateLimit = 1L << 52;

    public PlanetVector Position => new(XMillimeters / 1000d, YMillimeters / 1000d, ZMillimeters / 1000d);

    public bool IsValidFrame() => FrameVersion == CurrentFrameVersion &&
        XMillimeters is >= -CoordinateLimit and <= CoordinateLimit &&
        YMillimeters is >= -CoordinateLimit and <= CoordinateLimit &&
        ZMillimeters is >= -CoordinateLimit and <= CoordinateLimit &&
        (XMillimeters != 0 || YMillimeters != 0 || ZMillimeters != 0);

    public static bool TryCreate(PlanetVector position, out ConstructionAnchor anchor)
    {
        var x = Math.Round(position.X * 1000);
        var y = Math.Round(position.Y * 1000);
        var z = Math.Round(position.Z * 1000);
        if (!(x is >= -CoordinateLimit and <= CoordinateLimit &&
              y is >= -CoordinateLimit and <= CoordinateLimit &&
              z is >= -CoordinateLimit and <= CoordinateLimit))
        {
            anchor = default;
            return false;
        }

        anchor = new(0, (long)x, (long)y, (long)z, CurrentFrameVersion);
        return anchor.IsValidFrame();
    }

    public ConstructionFrame Frame
    {
        get
        {
            if (!IsValidFrame())
                throw new ArgumentException("Invalid construction anchor frame.");

            var origin = Position;
            var up = ConstructionMath.Normalize(origin);
            var x = Math.Abs(up.X);
            var y = Math.Abs(up.Y);
            var z = Math.Abs(up.Z);
            // Choose the least aligned planet axis with a fixed XYZ tie order.
            var seed = x <= y && x <= z ? new PlanetVector(1, 0, 0) :
                y <= z ? new PlanetVector(0, 1, 0) : new PlanetVector(0, 0, 1);
            var east = ConstructionMath.Normalize(ConstructionMath.Subtract(seed,
                ConstructionMath.Scale(up, ConstructionMath.Dot(seed, up))));
            return new(origin, east, up, ConstructionMath.Cross(east, up));
        }
    }
}

public readonly record struct ConstructionFrame(PlanetVector Origin, PlanetVector XAxis, PlanetVector YAxis, PlanetVector ZAxis)
{
    public PlanetVector ToWorld(PlanetVector local) => new(
        Origin.X + XAxis.X * local.X + YAxis.X * local.Y + ZAxis.X * local.Z,
        Origin.Y + XAxis.Y * local.X + YAxis.Y * local.Y + ZAxis.Y * local.Z,
        Origin.Z + XAxis.Z * local.X + YAxis.Z * local.Y + ZAxis.Z * local.Z);

    public PlanetVector ToLocal(PlanetVector world)
    {
        var delta = ConstructionMath.Subtract(world, Origin);
        return new(ConstructionMath.Dot(delta, XAxis), ConstructionMath.Dot(delta, YAxis), ConstructionMath.Dot(delta, ZAxis));
    }
}

public static class ConstructionOrientation
{
    public const int Count = 24;
    // This table defines the format's codes. The signed axis order is part of version 1.
    private static readonly PlanetVector[] Directions =
        [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(-1, 0, 0), new(0, -1, 0), new(0, 0, -1)];
    private static readonly Axes[] Orientations = BuildOrientations();
    private static readonly byte[] AroundY = RotationTable(static p => new(p.Z, p.Y, -p.X));
    private static readonly byte[] AroundX = RotationTable(static p => new(p.X, -p.Z, p.Y));

    public static byte RotateAroundY(byte orientation) => AroundY[orientation];

    public static byte RotateAroundX(byte orientation) => AroundX[orientation];

    public static PlanetVector Transform(byte orientation, PlanetVector vector)
    {
        var (x, y, z) = Orientations[orientation];
        return new(x.X * vector.X + y.X * vector.Y + z.X * vector.Z,
            x.Y * vector.X + y.Y * vector.Y + z.Y * vector.Z,
            x.Z * vector.X + y.Z * vector.Y + z.Z * vector.Z);
    }

    private static Axes[] BuildOrientations()
    {
        var result = new List<Axes>(Count);
        foreach (var x in Directions)
            foreach (var y in Directions)
                if (ConstructionMath.Dot(x, y) == 0)
                    result.Add(new(x, y, ConstructionMath.Cross(x, y)));
        return [.. result];
    }

    private static byte[] RotationTable(Func<PlanetVector, PlanetVector> rotate) =>
        [.. Orientations.Select(axes => checked((byte)Array.IndexOf(Orientations,
            new Axes(rotate(axes.X), rotate(axes.Y), rotate(axes.Z)))))];

    private readonly record struct Axes(PlanetVector X, PlanetVector Y, PlanetVector Z);
}

public readonly record struct ConstructionGridBounds(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ)
{
    public const double CellMeters = 0.25;

    public bool Intersects(ConstructionGridBounds other) =>
        MinX < other.MaxX && other.MinX < MaxX && MinY < other.MaxY && other.MinY < MaxY &&
        MinZ < other.MaxZ && other.MinZ < MaxZ;

    public ConstructionBox Box(ConstructionFrame frame)
    {
        const double halfCell = CellMeters / 2;
        return new(frame.ToWorld(new((MinX + MaxX) * halfCell, (MinY + MaxY) * halfCell, (MinZ + MaxZ) * halfCell)),
            frame.XAxis, frame.YAxis, frame.ZAxis, new((MaxX - MinX) * halfCell, (MaxY - MinY) * halfCell, (MaxZ - MinZ) * halfCell));
    }
}

public readonly record struct ConstructionBox(PlanetVector Center, PlanetVector XAxis, PlanetVector YAxis,
    PlanetVector ZAxis, PlanetVector HalfExtent)
{
    public bool Intersects(ConstructionBox other)
    {
        var delta = ConstructionMath.Subtract(other.Center, Center);
        // The diagonal bounds the box regardless of alignment. Most blocks miss here.
        var reach = ConstructionMath.Length(HalfExtent) + ConstructionMath.Length(other.HalfExtent);
        if (ConstructionMath.Dot(delta, delta) >= reach * reach)
            return false;

        Span<PlanetVector> left = [XAxis, YAxis, ZAxis];
        Span<PlanetVector> right = [other.XAxis, other.YAxis, other.ZAxis];
        for (var i = 0; i < 3; i++)
        {
            if (Separates(left[i], delta, this, other) || Separates(right[i], delta, this, other))
                return false;
            for (var j = 0; j < 3; j++)
                if (Separates(ConstructionMath.Cross(left[i], right[j]), delta, this, other))
                    return false;
        }
        return true;
    }

    private static bool Separates(PlanetVector axis, PlanetVector delta, ConstructionBox left, ConstructionBox right)
    {
        var axisLength = ConstructionMath.Length(axis);
        if (axisLength < 1e-12)
            return false;
        // Projection error is accounted for at shared faces, where touching is allowed.
        return Math.Abs(ConstructionMath.Dot(delta, axis)) >= left.Project(axis) + right.Project(axis) - 1e-9 * axisLength;
    }

    private double Project(PlanetVector axis) =>
        HalfExtent.X * Math.Abs(ConstructionMath.Dot(axis, XAxis)) +
        HalfExtent.Y * Math.Abs(ConstructionMath.Dot(axis, YAxis)) +
        HalfExtent.Z * Math.Abs(ConstructionMath.Dot(axis, ZAxis));
}

internal static class ConstructionMath
{
    internal static PlanetVector Subtract(PlanetVector a, PlanetVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    internal static PlanetVector Scale(PlanetVector value, double amount) => new(value.X * amount, value.Y * amount, value.Z * amount);
    internal static double Dot(PlanetVector a, PlanetVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static PlanetVector Cross(PlanetVector a, PlanetVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    internal static double Length(PlanetVector value) => Math.Sqrt(Dot(value, value));
    internal static PlanetVector Normalize(PlanetVector value) => Scale(value, 1 / Length(value));
}
