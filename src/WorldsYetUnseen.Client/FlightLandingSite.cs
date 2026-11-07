using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed class FlightLandingSite(PlanetCheckpoint? restored)
{
    internal const double Radius = PlanetFlight.ReferenceRadius;
    internal static readonly PlanetSaveRecipe Recipe = new(1, 1, 0x57505538);
    internal static readonly PlanetVector Direction = new(Math.Cos(35 * Math.PI / 180), 0, Math.Sin(35 * Math.PI / 180));
    private static readonly PlanetTerrainPlan SupportPlan = SelectPad();
    private readonly PlanetHeightEdit[] _edits = restored is null ? CreateEdits() : restored.Edits.ToArray();
    private readonly Guid _lineage = restored?.Identity.WorldLineageId ?? Guid.NewGuid();
    internal ulong Generation { get; private set; } = restored?.Identity.Generation ?? 0;
    internal PlanetFlightState InitialState => restored is { } checkpoint
        ? new(checkpoint.Pose.Position, default, checkpoint.Pose.Orientation, Grounded: true)
        : FindDeparture();

    internal static PlanetSaveSlot OpenSlot(string directory) =>
        new(directory, static (id, version) => id == Recipe.GeneratorId && version == Recipe.GeneratorVersion, ValidateCheckpoint);

    internal async Task SaveAsync(PlanetSaveSlot slot, IRuntimeOwnership ownership, PlanetFlightState state, CancellationToken token)
    {
        if (!state.Grounded || state.Velocity != default)
            throw new InvalidOperationException("A flight checkpoint requires an admitted grounded state.");
        var checkpoint = new PlanetCheckpoint(new(checked(Generation + 1), _lineage, Guid.NewGuid()), Recipe,
            new(state.Position, state.Orientation), _edits);
        var expected = Generation;
        var result = await RunIoAsync(ownership, checkpoint, () => slot.Save(expected, checkpoint, token)).ConfigureAwait(false);
        if (result.Status != SaveSlotStatus.Committed)
            throw new InvalidOperationException($"Could not commit flight checkpoint ({result.Status}).", result.Error);
        Generation = checkpoint.Identity.Generation;
    }

    internal static async Task<T> RunIoAsync<T>(IRuntimeOwnership ownership, object tracked, Func<T> operation)
    {
        using var lease = ownership.TryTrack(tracked) ??
            throw new InvalidOperationException("Core could not track planetary checkpoint I/O.");
        return await Task.Run(operation).ConfigureAwait(false);
    }

    internal double Height(PlanetSampleKey key) => Height(key, _edits);

    internal static string Describe(PlanetFlightState state) => FormattableString.Invariant(
        $"pose={state.Position.X:R},{state.Position.Y:R},{state.Position.Z:R} orientation={state.Orientation.X:R},{state.Orientation.Y:R},{state.Orientation.Z:R},{state.Orientation.W:R}");

    internal string EditDigest()
    {
        // A fixed identity lets the trace compare the full canonical overlay across processes.
        var fixture = new PlanetCheckpoint(new(1, new Guid(1, 0, 0, new byte[8]), new Guid(2, 0, 0, new byte[8])),
            Recipe, new(new(Radius, 0, 0), PlanetFlightOrientation.Identity), _edits);
        return Convert.ToHexString(PlanetCheckpointCodec.Encode(fixture).AsSpan(^32));
    }

    private PlanetFlightState FindDeparture()
    {
        var surface = BuildSupport(_edits);
        if (!surface.TryGetSupport(Direction, out var contact))
            throw new InvalidOperationException("Departure pad is missing.");
        var state = new PlanetFlightState(Add(contact.Position, Scale(Direction, 0.01)),
            Scale(Direction, -0.25), PlanetFlightOrientation.Identity);
        for (var i = 0; i < 8; i++)
        {
            var result = PlanetFlight.Step(surface, state, new(default, state.Orientation));
            state = result.State;
            if (result.Status == PlanetFlightStatus.Touchdown)
                return state;
        }

        throw new InvalidOperationException("Departure footprint could not settle on the pad.");
    }

    private static SaveGameValidation ValidateCheckpoint(PlanetCheckpoint checkpoint)
    {
        if (checkpoint.Recipe != Recipe)
            return SaveGameValidation.Unsupported;
        var pose = checkpoint.Pose;
        var result = PlanetFlight.Step(BuildSupport(checkpoint.Edits.ToArray()),
            new(pose.Position, default, pose.Orientation, Grounded: true), new(default, pose.Orientation));
        return result.Status == PlanetFlightStatus.Grounded ? SaveGameValidation.Valid : SaveGameValidation.Malformed;
    }

    private static PlanetTerrainPlan SelectPad() =>
        PlanetTerrainSelection.TrySelect(Direction, Radius, null, out var plan) && plan is not null
            ? plan : throw new InvalidOperationException("Landing site exceeds the terrain budget.");

    private static PlanetSurface BuildSupport(PlanetHeightEdit[] edits) =>
        new(SupportPlan.Tiles.ToArray().Where(tile => tile.SupportsCollision)
            .Select(tile => PlanetPatchMesh.Build(tile.Patch, Radius, sample => Height(sample, edits))).ToArray());

    private static PlanetHeightEdit[] CreateEdits()
    {
        var edits = new Dictionary<PlanetSampleKey, short>();
        foreach (var tile in SupportPlan.Tiles)
        {
            if (!tile.SupportsCollision)
                continue;
            for (var v = 0; v <= PlanetCoordinates.IntervalsPerPatch; v++)
            for (var u = 0; u <= PlanetCoordinates.IntervalsPerPatch; u++)
            {
                var key = PlanetCoordinates.Sample(tile.Patch, u, v);
                var distance = Radius * Math.Acos(Math.Clamp(Dot(Direction, PlanetCoordinates.Direction(key)), -1, 1));
                var delta = (short)Math.Round(120 * Math.Clamp(2 - distance, 0, 1));
                if (delta != 0)
                    edits[key] = delta;
            }
        }

        return edits.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Y).ThenBy(pair => pair.Key.Z)
            .Select(pair => new PlanetHeightEdit(pair.Key, pair.Value)).ToArray();
    }

    private static double Height(PlanetSampleKey key, PlanetHeightEdit[] edits)
    {
        var d = PlanetCoordinates.Direction(key);
        var baseline = Math.Round(1200 * d.X * d.Z + 600 * d.Y);
        var low = 0;
        var high = edits.Length - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var sample = edits[mid].Sample;
            var comparison = (sample.X, sample.Y, sample.Z).CompareTo((key.X, key.Y, key.Z));
            if (comparison == 0)
                return (baseline + edits[mid].DeltaMillimeters) / 1000d;
            if (comparison < 0)
                low = mid + 1;
            else
                high = mid - 1;
        }

        return baseline / 1000d;
    }
}

internal static class FlightVector
{
    internal static PlanetVector Add(PlanetVector a, PlanetVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    internal static PlanetVector Scale(PlanetVector v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    internal static PlanetVector Subtract(PlanetVector a, PlanetVector b) => Add(a, Scale(b, -1));
    internal static double Dot(PlanetVector a, PlanetVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    internal static double Length(PlanetVector v) => Math.Sqrt(Dot(v, v));
    internal static PlanetVector Unit(PlanetVector v) => Scale(v, 1 / Length(v));
    internal static PlanetVector Limit(PlanetVector v, double max) => Length(v) > max ? Scale(Unit(v), max) : v;
}
