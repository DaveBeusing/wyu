using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal enum InventoryDropSiteStatus { Found, NoSupportedSite, CoverageUnavailable, QueryBudgetExceeded }

internal static class InventoryDropSite
{
    internal const double Radius = 0.25;

    private const double GridSpacing = 0.5;
    private const int SearchSteps = 4;
    private const double ProbeHeight = 0.5;
    private const double FootClearance = 0.025;
    private const double MinimumSupportNormal = 0.7071067811865476;
    private const double SupportHeightTolerance = 0.10;
    private const int TriangleBudget = 16_000_000;
    private static readonly (int X, int Z)[] Candidates = BuildCandidates();
    private static readonly (double X, double Z)[] Footprint =
        [(-0.16, -0.16), (-0.16, 0.16), (0.16, -0.16), (0.16, 0.16)];

    // The caller binds this retained collision view and the occupancy list to the same committed root.
    // The shape is a half-metre bundle with the persisted position at the bottom of its clearance volume.
    internal static InventoryDropSiteStatus Find(VolumeStreamingLease lease, PlanetVector origin,
        PlanetVector playerFoot, IReadOnlyList<PlanetVector> occupied, out PlanetVector site)
    {
        site = default;
        var length = Length(origin);
        if (!double.IsFinite(length) || length <= Radius)
            return InventoryDropSiteStatus.NoSupportedSite;

        var up = Scale(origin, 1 / length);
        var axis = Math.Abs(up.X) < 0.9 ? new PlanetVector(1, 0, 0) : new PlanetVector(0, 0, 1);
        var xAxis = Unit(Subtract(axis, Scale(up, Dot(axis, up))));
        var zAxis = new PlanetVector(up.Y * xAxis.Z - up.Z * xAxis.Y, up.Z * xAxis.X - up.X * xAxis.Z,
            up.X * xAxis.Y - up.Y * xAxis.X);
        var remaining = TriangleBudget;
        var coverageUnavailable = false;
        foreach (var (x, z) in Candidates)
        {
            var probe = Add(origin, Add(Scale(xAxis, x * GridSpacing), Scale(zAxis, z * GridSpacing)));
            var status = CheckSite(probe, up, xAxis, zAxis, out var foot);
            if (status == InventoryDropSiteStatus.Found)
            {
                site = foot;
                return status;
            }
            if (status == InventoryDropSiteStatus.QueryBudgetExceeded)
                return status;
            coverageUnavailable |= status == InventoryDropSiteStatus.CoverageUnavailable;
        }

        return coverageUnavailable ? InventoryDropSiteStatus.CoverageUnavailable : InventoryDropSiteStatus.NoSupportedSite;

        InventoryDropSiteStatus CheckSite(PlanetVector probe, PlanetVector normal, PlanetVector xDirection,
            PlanetVector zDirection, out PlanetVector foot)
        {
            foot = default;
            var support = ProbeSupport(lease, probe, normal, ref remaining, out var supportPoint);
            if (support != InventoryDropSiteStatus.Found)
                return support;
            foot = Add(supportPoint, Scale(normal, FootClearance));
            var center = Add(foot, Scale(normal, Radius));
            if (IsOccupied(center, playerFoot, occupied))
                return InventoryDropSiteStatus.NoSupportedSite;

            foreach (var (sampleX, sampleZ) in Footprint)
            {
                var sample = Add(supportPoint, Add(Scale(xDirection, sampleX), Scale(zDirection, sampleZ)));
                var sampleStatus = ProbeSupport(lease, sample, normal, ref remaining, out var surface);
                if (sampleStatus != InventoryDropSiteStatus.Found)
                    return sampleStatus;
                if (Math.Abs(Dot(Subtract(surface, sample), normal)) > SupportHeightTolerance)
                    return InventoryDropSiteStatus.NoSupportedSite;
            }

            if (remaining <= 0)
                return InventoryDropSiteStatus.QueryBudgetExceeded;
            var clearance = lease.Sweep(new(center, center, Radius), default, new(remaining));
            remaining -= clearance.TriangleTests;
            return clearance.Status == VolumeQueryStatus.Clear ? InventoryDropSiteStatus.Found : Reject(clearance.Status);
        }
    }

    private static InventoryDropSiteStatus ProbeSupport(VolumeStreamingLease lease, PlanetVector probe, PlanetVector up,
        ref int remaining, out PlanetVector surface)
    {
        surface = default;
        if (remaining <= 0)
            return InventoryDropSiteStatus.QueryBudgetExceeded;
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        var vertical = Scale(up, ProbeHeight);
        var result = lease.Raycast(Add(probe, vertical), Subtract(probe, vertical), hits, new(remaining));
        remaining -= result.TriangleTests;
        if (result.Status != VolumeQueryStatus.Contact)
            return Reject(result.Status);
        var hit = hits[0];
        if (hit.IsTransition || Dot(hit.Normal, up) < MinimumSupportNormal)
            return InventoryDropSiteStatus.NoSupportedSite;
        surface = hit.Position;
        return InventoryDropSiteStatus.Found;
    }

    private static bool IsOccupied(PlanetVector center, PlanetVector playerFoot, IReadOnlyList<PlanetVector> occupied)
    {
        var playerUp = Unit(playerFoot);
        const double playerRadius = PlanetSurfaceWalker.BodyRadius;
        var offset = Subtract(center, playerFoot);
        var closest = Add(playerFoot, Scale(playerUp,
            Math.Clamp(Dot(offset, playerUp), playerRadius, PlanetSurfaceWalker.BodyHeight - playerRadius)));
        if (Within(center, closest, Radius + playerRadius))
            return true;

        foreach (var foot in occupied)
        {
            if (Within(center, Add(foot, Scale(Unit(foot), Radius)), Radius * 2))
                return true;
        }
        return false;

        static bool Within(PlanetVector a, PlanetVector b, double reach)
        {
            var delta = Subtract(a, b);
            return Dot(delta, delta) < reach * reach;
        }
    }

    private static InventoryDropSiteStatus Reject(VolumeQueryStatus status) => status switch
    {
        VolumeQueryStatus.CoverageUnavailable => InventoryDropSiteStatus.CoverageUnavailable,
        VolumeQueryStatus.BudgetExceeded => InventoryDropSiteStatus.QueryBudgetExceeded,
        _ => InventoryDropSiteStatus.NoSupportedSite
    };

    private static (int X, int Z)[] BuildCandidates() =>
        (from x in Enumerable.Range(-SearchSteps, 2 * SearchSteps + 1)
         from z in Enumerable.Range(-SearchSteps, 2 * SearchSteps + 1)
         let distance = x * x + z * z
         where distance <= SearchSteps * SearchSteps
         orderby distance, x, z
         select (x, z)).ToArray();
}
