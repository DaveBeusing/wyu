using System.Buffers.Binary;
using System.Security.Cryptography;
using Groundwork.Core;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Gameplay.Tests;

internal static class ReferencePlanetTests
{
    private static readonly Guid WorldId = new("aa0567e4-de48-416b-a55d-e21caf4fef6a");
    private static readonly (ulong Seed, string Macro, string Local)[] Fixtures =
    [
        (42, "05c751d67262bb349f0dd938105507024f8bf40ad26968b0c78a2dccfb606723",
            "3b5063da45eb00feb7cb9580bfdfd1e4eea0d8d641cd073b5a2901d11a4d78fb"),
        (20261111, "a3a2a1f2a1b9c9f8105febdc795db8b01492e3dd72697d9a839559419ffa0245",
            "3e4ed664cd8f135861bb4e87341072ab56ed072a3ed00ced9f904af9d3d372ed"),
        (ulong.MaxValue, "6818485a4248651846964ed887df73db06b9eb649ad658b601665046b3bc1817",
            "ed20593d8b7f46125b28698cf4ef40a21b5bc744136272fc1446cea73bfb8319"),
    ];
    private static readonly PlanetVector East = new(1, 0, 0);

    public static void SeedAudit()
    {
        var macro = new HashSet<string>(StringComparer.Ordinal);
        var local = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (seed, expectedMacro, expectedLocal) in Fixtures)
        {
            var planet = new ReferencePlanet(WorldId, seed);
            Check(planet.Descriptor.ParameterSha256 ==
                "b31da48cef059e05f932952229ab9fcc5e932af0fedea1089f4043a0a5b57f5d", "Recipe hash changed");
            var candidate = planet.FindSpawnCandidates().First();
            var center = planet.Descriptor.SampleAt(candidate).Vertex;
            var coarse = SampleHash(planet.Field, new(0, ReferencePlanet.RadiusMeters, 0), 256, false);
            var fine = SampleHash(planet.Field, center, 1, false);
            Check(coarse == expectedMacro && fine == expectedLocal, $"Golden samples changed for seed {seed}");
            Check(coarse == SampleHash(new ReferencePlanet(WorldId, seed).Field,
                new(0, ReferencePlanet.RadiusMeters, 0), 256, true), "Parallel macro hash changed");
            Check(fine == SampleHash(new ReferencePlanet(WorldId, seed).Field, center, 1, true),
                "Parallel local hash changed");
            Check(macro.Add(coarse) && local.Add(fine), "Two seeds generated identical terrain");
            Console.WriteLine(FormattableString.Invariant(
                $"Seed {seed}: recipe {planet.Descriptor.ParameterSha256}, macro {coarse}, local {fine}, candidate {candidate.X},{candidate.Y:R},{candidate.Z}"));
        }
    }

    public static void ChartContinuity()
    {
        foreach (var (seed, _, _) in Fixtures)
        {
            var planet = new ReferencePlanet(WorldId, seed);
            foreach (var face in Enum.GetValues<PlanetFace>())
            foreach (var edge in Enum.GetValues<PlanetEdge>())
            {
                var patch = new PlanetPatchKey(face, 0, 0, 0);
                var (other, otherEdge, reversed) = PlanetCoordinates.Neighbor(patch, edge);
                for (var i = 0; i <= PlanetCoordinates.IntervalsPerPatch; i++)
                {
                    var a = planet.Descriptor.SampleAt(patch, edge, i);
                    var b = planet.Descriptor.SampleAt(other, otherEdge,
                        reversed ? PlanetCoordinates.IntervalsPerPatch - i : i);
                    Check(a == b && planet.Field.SampleDensity(a) == planet.Field.SampleDensity(b) &&
                        planet.Field.SampleMaterial(a.Vertex) == planet.Field.SampleMaterial(b.Vertex),
                        "Shared face sample changed");
                }
            }
        }
    }

    public static void PublishedSpawnAndLanding()
    {
        foreach (var (seed, _, _) in Fixtures)
            VerifySpawnAsync(seed).GetAwaiter().GetResult();
    }

    private static string SampleHash(PlanetDensityField field, LatticeCoordinate center, int stride, bool parallel)
    {
        const int edge = 9;
        const int sampleCount = edge * edge * edge;
        var bytes = new byte[sampleCount * 10];
        if (parallel)
            Parallel.For(0, sampleCount, WriteSample);
        else
            for (var i = 0; i < sampleCount; i++)
                WriteSample(i);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));

        void WriteSample(int index)
        {
            var x = index % edge - edge / 2;
            var y = index / edge % edge - edge / 2;
            var z = index / (edge * edge) - edge / 2;
            var vertex = new LatticeCoordinate(center.X + x * stride, center.Y + y * stride, center.Z + z * stride);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(index * 10, 8), field.SampleDensity(vertex));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 10 + 8, 2), field.SampleMaterial(vertex));
        }
    }

    private static async Task VerifySpawnAsync(ulong seed)
    {
        var planet = new ReferencePlanet(WorldId, seed);
        var candidate = planet.FindSpawnCandidates().First();
        var quota = new ResourceQuota(1, 16,
            new JobQuota(8, 4, 16, 16, 4, 8, 16, 65536, 65536, 65536),
            new DiagnosticQuota(1, 16384, 12, 32768, 4, 4, 64, 1024));
        var budget = new ProcessResourceBudget(new ProcessResourceLimits(1, quota));
        using var reservation = budget.TryReserveRuntime(quota) ?? throw new InvalidOperationException("Runtime quota");
        var timeout = TimeSpan.FromSeconds(5);
        var runtime = RuntimeSafetyAuthority.Create(TimeProvider.System, new(timeout, timeout, timeout), reservation);
        Check(runtime.TryAllocateWorldId(out var id), "World allocation");
        var worldQuota = quota with
        {
            OutstandingOperations = 8,
            Diagnostics = quota.Diagnostics with { EmergencyRecords = 9, EmergencyBytes = 28672 },
        };
        var world = runtime.OpenWorld(id, worldQuota, new Supervisor(), new Root(id), new NoJobs());
        var selection = VolumeStreamingSelection.Select(planet.Descriptor, candidate, East);
        const long meshBytes = 10_000_000;
        var charge = new VolumeResources(VolumeStreamingCoordinator.RequiredCpuBytes(selection, meshBytes),
            100, 20, 100, 1, 1, 1, 64, 1);
        var baseline = charge.Retained + charge.Retained;
        baseline = baseline with { LeaseSlots = baseline.LeaseSlots + 16 };
        var limits = new VolumeStreamingLimits(baseline + charge + charge + charge + charge,
            baseline, charge, charge, charge, charge, 16, 16, 1024);
        var terrain = new VolumeStreamingCoordinator(world, planet.Descriptor, limits);
        try
        {
            Check(terrain.TryRead() is null, "Collision was exposed before publication");
            Check(terrain.ConfirmMask(terrain.View), "Initial mask");
            Check(terrain.TryStage(VolumeWorkClass.Movement, selection, 0, 1, meshBytes, charge, planet.Field,
                out var work) == VolumeAdmission.Accepted, "Generation admission");
            using var staged = work ?? throw new InvalidOperationException("Missing staged work");
            Check(staged.Execute(), "Reference region meshing");
            Check(!planet.TryConfirmSpawn(terrain, candidate, out _), "Unpublished geometry accepted for spawn");
            var begin = await SealAsync(world, BarrierKind.BeginTick);
            var empty = world.Publication.Preparation.TryPrepare(new BarrierPublicationDraft(begin,
                world.Publication.Committed.Read().Root, world.State.Read().PendingFaultGeneration, [])).Publication
                ?? throw new InvalidOperationException("BeginTick preparation");
            Check(world.Publication.Finalization.TryFinalize(empty).Status == BarrierStatus.Unchanged, "BeginTick");
            var commit = await SealAsync(world, BarrierKind.CommitResults);
            var prepared = terrain.TryPreparePublication(staged.Generation, commit, [])
                ?? throw new InvalidOperationException("Missing collision publication");
            Check(world.Publication.Finalization.TryFinalize(prepared).Status == BarrierStatus.Committed, "Collision commit");
            using var lease = terrain.TryRead() ?? throw new InvalidOperationException("Missing collision coverage");
            var spawn = default(ReferenceSpawn);
            var confirmed = false;
            foreach (var site in planet.FindSpawnCandidates())
            {
                if (!planet.TryConfirmSpawn(terrain, site, out spawn))
                    continue;
                confirmed = true;
                break;
            }
            Check(confirmed, $"Unsafe spawn for seed {seed}");
            Check(spawn.Terrain == lease.Info, "Spawn proof lost the committed terrain identity");
            Check(!new ReferencePlanet(Guid.NewGuid(), seed).TryConfirmSpawn(terrain, candidate, out _), "Foreign world accepted");
            Check(!planet.TryConfirmSpawn(terrain, candidate with { X = candidate.X + 2048 }, out _), "Invalid candidate accepted");
            var foot = spawn.Foot;
            var body = new VolumeCapsule(foot with { Y = foot.Y + 0.35 }, foot with { Y = foot.Y + 1.4 }, 0.35);
            Check(lease.Sweep(body, new(2, 0, 0)).Status == VolumeQueryStatus.Clear, "Eastbound clearance");
            Check(lease.Sweep(body with { A = body.A with { X = body.A.X + 2 },
                B = body.B with { X = body.B.X + 2 } }, new(-2, 0, 0)).Status == VolumeQueryStatus.Clear, "Return clearance");
            Console.WriteLine(FormattableString.Invariant($"Seed {seed}: collision-confirmed foot {foot.X},{foot.Y:R},{foot.Z}"));
        }
        finally
        {
            terrain.Stop();
            runtime.StopControl.RequestStop(HostStopReason.ExternalCancellation);
            runtime.SettleStop();
        }
    }

    private static async Task<CandidateSet> SealAsync(WorldSafetyPorts world, BarrierKind kind)
    {
        var publication = world.Publication;
        var request = new BarrierRequest(publication.Committed.Read().Stamp, 1, kind);
        Check(await publication.Borrow.CloseAndDrainAsync(request, world.CreateBorrowedDrainDeadline()) ==
            BorrowDrainStatus.Drained, "Borrow drain");
        return publication.Candidates.SealCandidates(request).Candidates ??
            throw new InvalidOperationException("No sealed candidates");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Root(WorldId World) : IWorldStateRoot;

    private sealed class Supervisor : IBarrierSupervisorPolicy
    {
        public ValueTask<SupervisorAction> DecideAsync(BarrierReviewRequest request, IFaultEvidenceReader evidence,
            CancellationToken cancellationToken) => ValueTask.FromResult(SupervisorAction.ContinueAtSafeBarrier);
    }

    private sealed class NoJobs : IJobScopePolicy
    {
        public JobScopeAdmission ValidateAdmission(JobSubmission submission, in CommittedWorldView world) =>
            new(JobScopeStatus.InvalidAccess, null);
        public bool CanDispatch(IAdmittedJobScope scope, in CommittedWorldView world) => false;
        public JobScopeOverlap EvaluateOverlap(IAdmittedJobScope first, IAdmittedJobScope second,
            in CommittedWorldView world) =>
            JobScopeOverlap.Independent;
    }
}
