using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed class ConstructionCollision
{
    private const double Tolerance = 0.0000001;
    private const int SweepIterations = 32;
    private readonly Entry[] _entries;

    internal ConstructionCollision(ConstructionState state)
    {
        Query = Sweep;
        var boxes = state.BuildBoxes();
        _entries = new Entry[boxes.Length];
        for (var i = 0; i < boxes.Length; i++)
            _entries[i] = new(state.Blocks[i].Id, boxes[i], Bounds(boxes[i]));
    }

    internal Func<VolumeCapsule, VolumeCapsule, VolumeSweepResult> Query { get; }

    internal static VolumeCapsule Body(PlanetWalkState player)
    {
        var up = Unit(player.Position);
        var radius = PlanetSurfaceWalker.BodyRadius;
        return new(Add(player.Position, Scale(up, radius)),
            Add(player.Position, Scale(up, PlanetSurfaceWalker.BodyHeight - radius)), radius);
    }

    internal static bool Intersects(ConstructionBox box, VolumeCapsule player) =>
        Distance(box, player.A, player.B, out _) < player.Radius - Tolerance;

    internal bool Intersects(VolumeCapsule body) =>
        Sweep(body, body).Status == VolumeQueryStatus.InitialOverlap;

    internal bool Intersects(ConstructionBox box)
    {
        var bounds = Bounds(box);
        foreach (var entry in _entries)
            if (Intersects(entry.Bounds, bounds) && box.Intersects(entry.Box))
                return true;
        return false;
    }

    internal VolumeSweepResult Sweep(VolumeCapsule start, VolumeCapsule end)
    {
        var travel = Union(Union(Point(start.A), Point(start.B)), Union(Point(end.A), Point(end.B)));
        var extent = new PlanetVector(start.Radius, start.Radius, start.Radius);
        travel = (Subtract(travel.Low, extent), Add(travel.High, extent));
        var first = Result(VolumeQueryStatus.Clear, 1);
        foreach (var entry in _entries)
        {
            if (!Intersects(travel, entry.Bounds))
                continue;
            var result = SweepBox(entry.Box, start, end);
            if (result.Status is not (VolumeQueryStatus.Clear or VolumeQueryStatus.Contact))
                return result;
            if (result.Status == VolumeQueryStatus.Contact &&
                (first.Status == VolumeQueryStatus.Clear || result.Fraction < first.Fraction))
                first = result;
        }
        return first;
    }

    internal Hit? Raycast(PlanetVector origin, PlanetVector direction, double range)
    {
        Hit? nearest = null;
        foreach (var entry in _entries)
        {
            var box = entry.Box;
            var a = Local(box, origin);
            var d = new PlanetVector(Dot(direction, box.XAxis), Dot(direction, box.YAxis), Dot(direction, box.ZAxis));
            var t = 0d;
            var end = range;
            var normal = default(PlanetVector);
            if (!Slab(a.X, d.X, box.HalfExtent.X, box.XAxis, ref t, ref end, ref normal) ||
                !Slab(a.Y, d.Y, box.HalfExtent.Y, box.YAxis, ref t, ref end, ref normal) ||
                !Slab(a.Z, d.Z, box.HalfExtent.Z, box.ZAxis, ref t, ref end, ref normal) ||
                t <= 0 || nearest is { } prior && prior.Distance <= t)
                continue;
            nearest = new(entry.Id, t, Add(origin, Scale(direction, t)), normal);
        }
        return nearest;
    }

    internal static VolumeQueryStatus ProbeTerrain(VolumeStreamingLease terrain, ConstructionBox box)
    {
        var sphere = new VolumeCapsule(box.Center, box.Center, Length(box.HalfExtent));
        var probe = terrain.Sweep(sphere, default);
        if (probe.Status is not (VolumeQueryStatus.Contact or VolumeQueryStatus.InitialOverlap))
            return probe.Status;

        // A small sphere inside the box also catches a block wholly enclosed by solid terrain.
        var centre = terrain.Sweep(sphere with { Radius = 0.001 }, default);
        if (centre.Status != VolumeQueryStatus.Clear)
            return centre.Status;

        var bounds = Bounds(box);
        var depth = 0;
        foreach (var mesh in terrain.Meshes)
        {
            var step = mesh.Descriptor.CellSizeMeters;
            var origin = new PlanetVector(mesh.Origin.X * step, mesh.Origin.Y * step, mesh.Origin.Z * step);
            var margin = new PlanetVector(2 * step, 2 * step, 2 * step);
            var size = new PlanetVector(64 * step, 64 * step, 64 * step);
            if (!Intersects(bounds, (Subtract(origin, margin), Add(Add(origin, size), margin))))
                continue;
            foreach (var triangle in mesh.Triangles)
            {
                if (++depth > VolumeQueryLimits.Default.TriangleTests)
                    return VolumeQueryStatus.BudgetExceeded;
                var a = Local(box, Vertex(triangle.A));
                var b = Local(box, Vertex(triangle.B));
                var c = Local(box, Vertex(triangle.C));
                if (TriangleOverlaps(a, b, c, box.HalfExtent))
                    return VolumeQueryStatus.Contact;
            }

            PlanetVector Vertex(VolumeVertex v) => Add(origin,
                new(v.Position.X * step, v.Position.Y * step, v.Position.Z * step));
        }
        return VolumeQueryStatus.Clear;
    }

    internal static ConstructionBox TerrainEditBox(LatticeCoordinate center, double metres)
    {
        // The 3x3x3 brush's vertices can affect the cells incident to its outer vertices.
        var half = 2 * metres;
        return new(new(center.X * metres, center.Y * metres, center.Z * metres),
            new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(half, half, half));
    }

    private static VolumeSweepResult SweepBox(ConstructionBox box, VolumeCapsule start, VolumeCapsule end)
    {
        if (OutsideFace(box.XAxis, box.HalfExtent.X) || OutsideFace(box.YAxis, box.HalfExtent.Y) ||
            OutsideFace(box.ZAxis, box.HalfExtent.Z))
            return Result(VolumeQueryStatus.Clear, 1);
        var da = Subtract(end.A, start.A);
        var db = Subtract(end.B, start.B);
        var speed = Math.Max(Length(da), Length(db));
        var time = 0d;
        for (var iteration = 0; iteration < SweepIterations; iteration++)
        {
            var distance = Distance(box, Add(start.A, Scale(da, time)), Add(start.B, Scale(db, time)), out var normal);
            var separation = distance - start.Radius;
            if (time == 0 && separation < -Tolerance)
                return Result(VolumeQueryStatus.InitialOverlap, 0, normal);
            if (speed <= Tolerance || Length(normal) == 0)
                return Result(VolumeQueryStatus.Clear, 1);

            // A plane separating the box from both end capsules separates their whole linear sweep.
            var boxSupport = box.HalfExtent.X * Math.Abs(Dot(box.XAxis, normal)) +
                box.HalfExtent.Y * Math.Abs(Dot(box.YAxis, normal)) + box.HalfExtent.Z * Math.Abs(Dot(box.ZAxis, normal));
            var endSeparation = Math.Min(Dot(Subtract(end.A, box.Center), normal), Dot(Subtract(end.B, box.Center), normal)) - boxSupport;
            if (endSeparation >= start.Radius - Tolerance && separation >= -Tolerance)
                return Result(VolumeQueryStatus.Clear, 1);
            if (separation <= Tolerance)
                return Result(VolumeQueryStatus.Contact, time, normal);
            time += (separation - Tolerance / 2) / speed;
            if (time > 1)
                return Result(VolumeQueryStatus.Clear, 1);
        }
        return Result(VolumeQueryStatus.BudgetExceeded, 0);

        bool OutsideFace(PlanetVector axis, double extent)
        {
            var a = Dot(Subtract(start.A, box.Center), axis);
            var b = Dot(Subtract(start.B, box.Center), axis);
            var c = Dot(Subtract(end.A, box.Center), axis);
            var d = Dot(Subtract(end.B, box.Center), axis);
            var margin = extent + start.Radius - Tolerance;
            return Math.Min(Math.Min(a, b), Math.Min(c, d)) >= margin ||
                Math.Max(Math.Max(a, b), Math.Max(c, d)) <= -margin;
        }
    }

    // Squared segment-to-AABB distance is quadratic between the six box-plane crossings.
    private static double Distance(ConstructionBox box, PlanetVector start, PlanetVector end, out PlanetVector normal)
    {
        var a = Local(box, start);
        var d = Subtract(Local(box, end), a);
        var half = box.HalfExtent;
        Span<double> breaks = stackalloc double[8];
        breaks[0] = 0;
        breaks[1] = 1;
        var n = 2;
        Crossings(a.X, d.X, half.X, breaks, ref n);
        Crossings(a.Y, d.Y, half.Y, breaks, ref n);
        Crossings(a.Z, d.Z, half.Z, breaks, ref n);
        breaks[..n].Sort();
        var closest = default(PlanetVector);
        var best = double.PositiveInfinity;
        for (var i = 1; i < n; i++)
        {
            var lo = breaks[i - 1];
            var hi = breaks[i];
            var mid = (lo + hi) / 2;
            double numerator = 0, denominator = 0;
            Quadratic(a.X, d.X, half.X, mid, ref numerator, ref denominator);
            Quadratic(a.Y, d.Y, half.Y, mid, ref numerator, ref denominator);
            Quadratic(a.Z, d.Z, half.Z, mid, ref numerator, ref denominator);
            var t = denominator > 0 ? Math.Clamp(-numerator / denominator, lo, hi) : mid;
            var p = Add(a, Scale(d, t));
            var q = new PlanetVector(Math.Clamp(p.X, -half.X, half.X), Math.Clamp(p.Y, -half.Y, half.Y),
                Math.Clamp(p.Z, -half.Z, half.Z));
            var diff = Subtract(p, q);
            var dsq = Dot(diff, diff);
            if (dsq >= best)
                continue;
            best = dsq;
            closest = diff;
        }
        var distance = Math.Sqrt(best);
        normal = distance <= Tolerance ? default : Scale(Add(Scale(box.XAxis, closest.X),
            Add(Scale(box.YAxis, closest.Y), Scale(box.ZAxis, closest.Z))), 1 / distance);
        return distance;
    }

    private static void Crossings(double a, double d, double half, Span<double> cuts, ref int n)
    {
        if (d == 0)
            return;
        var t = (-half - a) / d;
        if (t is > 0 and < 1) cuts[n++] = t;
        t = (half - a) / d;
        if (t is > 0 and < 1) cuts[n++] = t;
    }

    private static void Quadratic(double a, double d, double half, double mid, ref double linear, ref double quadratic)
    {
        var p = a + d * mid;
        if (Math.Abs(p) <= half)
            return;
        linear += d * (a - Math.CopySign(half, p));
        quadratic += d * d;
    }

    private static bool TriangleOverlaps(PlanetVector a, PlanetVector b, PlanetVector c, PlanetVector half)
    {
        half = Subtract(half, new(Tolerance, Tolerance, Tolerance));
        var ab = Subtract(b, a);
        var bc = Subtract(c, b);
        var ca = Subtract(a, c);
        return Overlaps(new(1, 0, 0)) && Overlaps(new(0, 1, 0)) && Overlaps(new(0, 0, 1)) &&
            Overlaps(Cross(ab, bc)) && Edge(ab) && Edge(bc) && Edge(ca);

        bool Edge(PlanetVector edge) => Overlaps(new(0, -edge.Z, edge.Y)) &&
            Overlaps(new(edge.Z, 0, -edge.X)) && Overlaps(new(-edge.Y, edge.X, 0));

        bool Overlaps(PlanetVector axis)
        {
            if (Dot(axis, axis) < Tolerance * Tolerance)
                return true;
            var p = Dot(axis, a);
            var q = Dot(axis, b);
            var r = Dot(axis, c);
            var radius = Math.Abs(axis.X) * half.X + Math.Abs(axis.Y) * half.Y + Math.Abs(axis.Z) * half.Z;
            return Math.Min(p, Math.Min(q, r)) < radius && Math.Max(p, Math.Max(q, r)) > -radius;
        }
    }

    private static PlanetVector Cross(PlanetVector a, PlanetVector b) => new(a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static bool Slab(double origin, double direction, double half, PlanetVector axis,
        ref double start, ref double end, ref PlanetVector normal)
    {
        if (Math.Abs(direction) <= Tolerance)
            return Math.Abs(origin) <= half;
        var a = (-half - origin) / direction;
        var b = (half - origin) / direction;
        var entry = Math.Min(a, b);
        if (entry > start)
        {
            start = entry;
            normal = Scale(axis, a <= b ? -1 : 1);
        }
        end = Math.Min(end, Math.Max(a, b));
        return start <= end;
    }

    private static PlanetVector Local(ConstructionBox box, PlanetVector point)
    {
        var delta = Subtract(point, box.Center);
        return new(Dot(delta, box.XAxis), Dot(delta, box.YAxis), Dot(delta, box.ZAxis));
    }

    private static (PlanetVector Low, PlanetVector High) Bounds(ConstructionBox box)
    {
        static PlanetVector Abs(PlanetVector p) => new(Math.Abs(p.X), Math.Abs(p.Y), Math.Abs(p.Z));
        var r = Add(Scale(Abs(box.XAxis), box.HalfExtent.X), Add(Scale(Abs(box.YAxis), box.HalfExtent.Y),
            Scale(Abs(box.ZAxis), box.HalfExtent.Z)));
        return (Subtract(box.Center, r), Add(box.Center, r));
    }

    private static (PlanetVector Low, PlanetVector High) Point(PlanetVector p) => (p, p);

    private static (PlanetVector Low, PlanetVector High) Union((PlanetVector Low, PlanetVector High) a,
        (PlanetVector Low, PlanetVector High) b) =>
        (new(Math.Min(a.Low.X, b.Low.X), Math.Min(a.Low.Y, b.Low.Y), Math.Min(a.Low.Z, b.Low.Z)),
            new(Math.Max(a.High.X, b.High.X), Math.Max(a.High.Y, b.High.Y), Math.Max(a.High.Z, b.High.Z)));

    private static bool Intersects((PlanetVector Low, PlanetVector High) a, (PlanetVector Low, PlanetVector High) b) =>
        a.Low.X <= b.High.X && a.High.X >= b.Low.X && a.Low.Y <= b.High.Y && a.High.Y >= b.Low.Y &&
        a.Low.Z <= b.High.Z && a.High.Z >= b.Low.Z;

    private static VolumeSweepResult Result(VolumeQueryStatus status, double fraction, PlanetVector normal = default) =>
        new(status, fraction, normal, default, 0);

    internal readonly record struct Hit(uint BlockId, double Distance, PlanetVector Point, PlanetVector Normal);
    private readonly record struct Entry(uint Id, ConstructionBox Box, (PlanetVector Low, PlanetVector High) Bounds);
}
