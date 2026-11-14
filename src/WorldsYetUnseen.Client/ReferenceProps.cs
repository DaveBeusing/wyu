using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

/// <summary>Versioned Far Horizon placement in a planet-centred lattice.</summary>
internal sealed class ReferenceProps(ReferencePlanet planet)
{
    private const uint Recipe = 1;
    private const int CellSize = 8;
    private const int Radius = 12;
    private const int CellCount = 128;
    private const int Candidates = 8;
    private static readonly D3D12PropAsset[] Assets = CreateAssets();
    private readonly ReferenceBiomes _biomes = new(planet);
    private readonly Dictionary<PropCellKey, LatticeCoordinate[]> _supports = [];
    private D3D12PropCells? _stream;
    private VolumePublicationInfo _previous;
    private VolumeStreamingSelection? _selection;
    private PlacementWork? _pending;
    private PlacementRun? _running;
    internal Task? PlacementGateForSmoke { get; set; }
    internal bool PlacementActive => _running is not null;
    internal int Failed { get; private set; }

    internal int CoverageCount { get; private set; }
    internal int Staged { get; private set; }
    internal int Retired { get; private set; }
    internal D3D12PropStatistics Read() => _stream?.ReadStatistics() ?? default;

    internal void Attach(D3D12Renderer renderer)
    {
        var stream = new D3D12PropCells(new(CellCount, CellCount, CellCount * Candidates, CellCount * Candidates, 8),
            Candidates, planet.Descriptor.Seed, Recipe, Assets);
        renderer.AttachPropCells(stream);
        _stream = stream;
        _previous = default;
        _selection = null;
        Supersede();
        _supports.Clear();
    }

    internal void Refresh(VolumeStreamingCoordinator terrain, VolumePublicationInfo publication,
        VolumeStreamingSelection selection, PlanetVector position, VolumeBrushIntent? edit, CancellationToken token)
    {
        if (_stream is not { } stream)
            return;

        Supersede();
        var cells = Coverage(position);
        var overlap = _previous.TerrainRevision == publication.TerrainRevision && _previous.View.RendererEpoch == publication.View.RendererEpoch;
        var coverage = cells.ToArray();
        // A coverage change can change collision detail. Resample support rather than keeping a placement
        // from the old collision mesh. The renderer has already masked that generation.
        var sameCut = SameCollisionCut(_selection, selection);
        var canReuse = edit.HasValue && _previous.View.RendererEpoch == publication.View.RendererEpoch &&
            publication.TerrainRevision == _previous.TerrainRevision + 1 && sameCut;
        HashSet<PropCellKey>? invalidated = null;
        if (!sameCut || overlap)
            invalidated = new(coverage);
        else if (canReuse)
        {
            var change = edit!.Value;
            invalidated = [];
            foreach (var key in coverage)
                if (_supports.TryGetValue(key, out var supports) && supports.Any(point => Affects(point, change)))
                    invalidated.Add(key);
        }
        stream.Publish(new(terrain.WorldIdentity, publication.View.RendererEpoch, publication.TerrainRevision, publication.Generation),
            coverage, invalidated);
        var next = new Dictionary<PropCellKey, LatticeCoordinate[]>();
        var wanted = new List<PropCellKey>(coverage.Length);
        foreach (var key in coverage)
        {
            if (canReuse && invalidated is not null && !invalidated.Contains(key) && _supports.TryGetValue(key, out var prior))
                next.Add(key, prior);
            else
                wanted.Add(key);
        }
        CoverageCount = coverage.Length;
        Retired += _supports.Keys.Count(key => invalidated?.Contains(key) == true || !coverage.Contains(key) ||
            !overlap && !next.ContainsKey(key));
        _supports.Clear();
        foreach (var (key, supports) in next)
            _supports.Add(key, supports);
        _previous = publication;
        _selection = selection;
        _pending = new(terrain, stream, publication, wanted, PlacementGateForSmoke, token);
        Pump();
    }

    internal void Pump()
    {
        if (_running is { } run)
        {
            if (!run.Result.IsCompleted)
                return;
            Finish(run);
        }

        if (_pending is not { } work)
            return;
        _pending = null;
        var requests = new List<PropCellRequest>(work.Cells.Count);
        foreach (var key in work.Cells)
            if (work.Stream.TryRequest(key, Candidates, out var request) == PropCellResult.Accepted)
                requests.Add(request);
        if (requests.Count == 0)
            return;

        var cancel = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
        var result = Task.Run(async () =>
        {
            if (work.Gate is { } gate)
                await gate.WaitAsync(cancel.Token).ConfigureAwait(false);
            return Sample(work.Terrain, work.Publication, requests, cancel.Token);
        }, cancel.Token);
        _running = new(work, requests, cancel, result);
    }

    private void Finish(PlacementRun run)
    {
        _running = null;
        try
        {
            var sample = run.Result.GetAwaiter().GetResult();
            if (run.Cancel.IsCancellationRequested || run.Work.Publication != _previous ||
                run.Work.Stream != _stream)
                return;
            foreach (var cell in sample)
            {
                if (run.Work.Stream.TryStage(cell.Request, new(cell.Origin.X, cell.Origin.Y, cell.Origin.Z), cell.Placements)
                    != PropCellResult.Accepted || cell.Placements.Length == 0)
                    continue;
                _supports[cell.Request.Cell] = cell.Supports;
                Staged++;
            }
        }
        catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested) { }
        catch (Exception)
        {
            Failed++;
        }
        finally
        {
            foreach (var request in run.Requests)
                run.Work.Stream.CompleteWorker(request);
            run.Cancel.Dispose();
        }
    }

    private void Supersede()
    {
        _pending = null;
        _running?.Cancel.Cancel();
    }

    internal async Task StopAsync()
    {
        Supersede();
        if (_running is { } run)
        {
            await ((Task)run.Result).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Finish(run);
        }
    }

    internal static bool SameCollisionCut(VolumeStreamingSelection? previous, VolumeStreamingSelection next) =>
        previous is not null && previous.Descriptor == next.Descriptor && previous.Minimum == next.Minimum && previous.Width == next.Width &&
        previous.RequiresFineCoverage == next.RequiresFineCoverage && previous.Requests.SequenceEqual(next.Requests);

    private sealed record PlacementWork(VolumeStreamingCoordinator Terrain, D3D12PropCells Stream,
        VolumePublicationInfo Publication, List<PropCellKey> Cells, Task? Gate, CancellationToken Token);
    private sealed record PlacementRun(PlacementWork Work, List<PropCellRequest> Requests,
        CancellationTokenSource Cancel, Task<List<Cell>> Result);
    private static List<PropCellKey> Coverage(PlanetVector position)
    {
        // Face coordinates are half-open. A chart owns a candidate only when its signed dominant axis matches.
        var face = Face(position);
        var (u, v) = (face / 2) switch
        {
            0 => (position.Y, position.Z),
            1 => (position.X, position.Z),
            _ => (position.X, position.Y),
        };
        var x = (long)Math.Floor(u / CellSize);
        var y = (long)Math.Floor(v / CellSize);
        // At most 128 cells in the active terrain window, sorted nearest-first.
        return (from du in Enumerable.Range(-Radius, 2 * Radius + 1)
                from dv in Enumerable.Range(-Radius, 2 * Radius + 1)
                orderby du * du + dv * dv, du, dv
                select new PropCellKey(x + du, y + dv, face)).Take(CellCount).ToList();
    }

    private List<Cell> Sample(VolumeStreamingCoordinator terrain, VolumePublicationInfo publication,
        List<PropCellRequest> requests, CancellationToken token)
    {
        using var lease = terrain.TryRead();
        if (lease is null || lease.Info != publication)
            return [];
        var result = new List<Cell>();
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            var key = request.Cell;
            var placements = new List<PropPlacement>(Candidates);
            var supports = new List<LatticeCoordinate>(Candidates);
            var origin = default(PlanetVector);
            for (uint ordinal = 0; ordinal < Candidates; ordinal++)
            {
                var hash = Mix(planet.Descriptor.Seed ^ Mix((ulong)key.X) ^ Mix((ulong)key.Y + 31) ^
                    Mix((ulong)key.Z + 67) ^ Mix(ordinal + 101));
                var u = key.X * CellSize + ((ordinal % 4) + 0.15 + (hash & 255) / 366d) * (CellSize / 4d);
                var v = key.Y * CellSize + ((ordinal / 4) + 0.15 + ((hash >> 8) & 255) / 366d) * (CellSize / 2d);
                var radius = planet.Descriptor.RadiusMeters;
                var normal = radius * radius - u * u - v * v;
                if (normal <= 0)
                    continue;
                var axis = Math.Sqrt(normal) * ((key.Z & 1) == 0 ? 1 : -1);
                var surface = (key.Z / 2) switch
                {
                    0 => new PlanetVector(axis, u, v),
                    1 => new PlanetVector(u, axis, v),
                    _ => new PlanetVector(u, v, axis),
                };
                if (Face(surface) != key.Z)
                    continue;
                var up = Unit(surface);
                var land = planet.Field.SampleLandform(planet.Descriptor.SampleAt(surface).Vertex);
                var elevation = land.Elevation / (double)PlanetDensityField.UnitsPerCell;
                supports.Add(planet.Descriptor.SampleAt(Scale(up, radius + elevation)).Vertex);
                var ray = lease.Raycast(Scale(up, radius + elevation + 7), Scale(up, radius + elevation - 7), hits);
                if (ray.Status != VolumeQueryStatus.Contact || Dot(up, hits[0].Normal) < 0.75)
                    continue;
                var foot = hits[0].Position;
                // Keep the initial landing/launch footprint clear.
                if (Math.Abs(foot.X - 16) < 6 && Math.Abs(foot.Z - 16) < 6 && foot.Y > 0)
                    continue;
                var biome = _biomes.Evaluate(foot).Dominant;
                var kind = ordinal < 5 ? 0 : ordinal == 5 ? 1 : biome switch
                {
                    ReferenceBiome.Meadow => 2,
                    ReferenceBiome.Steppe => ordinal == 6 ? 2 : 3,
                    _ => 3,
                };
                if (kind == 0 && biome == ReferenceBiome.Highland && (hash & 1) == 0)
                    continue;
                if (placements.Count == 0)
                    origin = foot;
                var normalVector = Vector3.Normalize(new((float)up.X, (float)up.Y, (float)up.Z));
                var tilt = normalVector.Y < -0.9999f ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI) :
                    Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitY, normalVector), 1 + normalVector.Y));
                var spin = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)((hash >> 16 & 255) * (Math.Tau / 256)));
                var delta = Subtract(foot, origin);
                var tint = kind switch
                {
                    0 => 0xff397b57u,
                    1 => 0xff8d8e99u,
                    2 => 0xff286933u,
                    _ => 0xff71788du,
                };
                placements.Add(new(ordinal, kind, new((float)delta.X, (float)delta.Y, (float)delta.Z),
                    tilt * spin, 0.7f + (hash >> 24 & 255) / 512f, tint));
            }
            result.Add(new(request, origin, placements.ToArray(), supports.ToArray()));
        }
        return result;
    }

    private static bool Affects(LatticeCoordinate point, VolumeBrushIntent edit)
    {
        // The placement ray extends seven cells each way. Allow four more cells
        // for rounding, meshing support and the 2:1 transition.
        const int halo = 11;
        return point.X >= edit.Minimum.X - halo && point.X <= edit.Maximum.X + halo &&
            point.Y >= edit.Minimum.Y - halo && point.Y <= edit.Maximum.Y + halo &&
            point.Z >= edit.Minimum.Z - halo && point.Z <= edit.Maximum.Z + halo;
    }

    private static int Face(PlanetVector point)
    {
        var x = Math.Abs(point.X);
        var y = Math.Abs(point.Y);
        var z = Math.Abs(point.Z);
        return x >= y && x >= z ? (point.X < 0 ? 1 : 0) :
            y >= z ? (point.Y < 0 ? 3 : 2) : (point.Z < 0 ? 5 : 4);
    }

    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    private static D3D12PropAsset[] CreateAssets() =>
    [
        new(new(1, 1, 0.65f, 32, 10, 14), Tuft(0.5f, 0.16f, 3), Tuft(0.5f, 0.2f, 2)),
        new(new(2, 2, 1.35f, 128, 18, 23), Cone(0.95f, 0, 0.8f, 7), Cone(0.95f, 0, 0.8f, 4)),
        new(new(3, 3, 4.5f, 128, 22, 28), Tree(7), Tree(4)),
        new(new(4, 4, 0.9f, 128, 17, 22), Cone(0.72f, 0, 0.46f, 6), Cone(0.72f, 0, 0.46f, 3)),
    ];

    private static RenderMesh Tuft(float height, float width, int blades)
    {
        var vertices = new Vector3[blades * 3];
        var indices = new uint[blades * 6];
        for (var i = 0; i < blades; i++)
        {
            var offset = 3 * i;
            var angle = i * MathF.PI / blades;
            var side = new Vector3(MathF.Cos(angle) * width, 0, MathF.Sin(angle) * width);
            vertices[offset] = -side;
            vertices[offset + 1] = side;
            vertices[offset + 2] = side / 2 + Vector3.UnitY * height;
            var first = (uint)offset;
            uint[] face = [first, first + 1, first + 2, first + 2, first + 1, first];
            face.CopyTo(indices, 6 * i);
        }
        return RenderMesh.Create(vertices, indices);
    }

    private static RenderMesh Tree(int sides)
    {
        var trunk = Cone(0.2f, 0, 2.25f, sides);
        var crown = Cone(1.25f, 1.2f, 4.2f, sides);
        var vertices = new Vector3[trunk.Positions.Length + crown.Positions.Length];
        trunk.Positions.CopyTo(vertices);
        crown.Positions.CopyTo(vertices.AsSpan(trunk.Positions.Length));
        var colors = new uint[vertices.Length];
        Array.Fill(colors, 0xff48789bu, 0, trunk.Positions.Length);
        Array.Fill(colors, 0xffffffffu, trunk.Positions.Length, crown.Positions.Length);
        var indices = new uint[trunk.Indices.Length + crown.Indices.Length];
        trunk.Indices.CopyTo(indices);
        for (var i = 0; i < crown.Indices.Length; i++)
            indices[trunk.Indices.Length + i] = crown.Indices[i] + (uint)trunk.Positions.Length;
        return RenderMesh.Create(vertices, indices, colors);
    }

    private static RenderMesh Cone(float radius, float bottom, float top, int sides)
    {
        var vertices = new Vector3[sides + 1];
        var indices = new uint[sides * 3];
        vertices[^1] = new(radius * 0.18f, top, 0);
        for (var i = 0; i < sides; i++)
        {
            var angle = i * MathF.Tau / sides;
            vertices[i] = new(MathF.Cos(angle) * radius, bottom, MathF.Sin(angle) * radius);
            indices[3 * i] = (uint)i;
            indices[3 * i + 1] = (uint)sides;
            indices[3 * i + 2] = (uint)((i + 1) % sides);
        }
        return RenderMesh.Create(vertices, indices);
    }

    private sealed record Cell(PropCellRequest Request, PlanetVector Origin, PropPlacement[] Placements, LatticeCoordinate[] Supports);
}
