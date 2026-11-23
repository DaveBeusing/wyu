using Groundwork.Core;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private sealed class ConsoleRelocationAttempt(VolumeWorldRoot source, VolumeRelocationRequest request,
        VolumeStreamingSelection selection, ConstructionCollision? obstacles)
    {
        internal VolumeWorldRoot Source { get; } = source;
        internal VolumeRelocationRequest Request { get; } = request;
        internal VolumeStreamingSelection Selection { get; } = selection;
        internal ConstructionCollision? Obstacles { get; } = obstacles;
        internal VolumeRelocationStatus Status { get; set; }
        internal PlanetWalkState? CommittedPose { get; set; }
    }

    private async Task<(PlanetWalkState? Pose, string Result)> RelocateConsoleAsync(VolumeStreamingCoordinator terrain,
        D3D12Renderer renderer, PlanetWalkState source, PlanetVector destination, CancellationToken token)
    {
        if (!freeTestWorld)
            return (null, "Teleport: Free Test World only.");
        var distance = Length(Subtract(destination, source.Position));
        var radius = Length(destination);
        if (!double.IsFinite(distance) || !double.IsFinite(radius) ||
            radius <= PlanetSurfaceWalker.BodyHeight + VolumeRelocationRequest.MaximumSettleMeters ||
            radius > Descriptor.RadiusMeters + 1024)
            return (null, "Coordinates outside the world.");
        if (distance > VolumeRelocationRequest.MaximumDistanceMeters)
            return (null, FormattableString.Invariant($"Teleport limit: {VolumeRelocationRequest.MaximumDistanceMeters:0} m."));
        if (distance == 0)
            return (null, "Already at that position.");

        if (_ports.Committed.Read().Root is not VolumeWorldRoot root || root.Player != source ||
            root.Terrain != _published || root.Terrain.View != terrain.View ||
            terrain.Statistics is { Stopped: true } or { MovementHeld: true })
            return (null, "Teleport held. Wait for terrain.");

        var up = Scale(destination, 1 / radius);
        var horizontal = Subtract(source.Heading, Scale(up, Dot(source.Heading, up)));
        var horizontalLength = Length(horizontal);
        if (!double.IsFinite(horizontalLength) || horizontalLength < 1e-9)
            return (null, "Teleport heading unavailable.");
        var heading = Scale(horizontal, 1 / horizontalLength);

        VolumeStreamingSelection selection;
        try
        {
            selection = SelectTerrain(destination, heading);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (null, "Teleport coordinates outside terrain.");
        }

        var obstacles = root.State is CaveInventory inventory && !inventory.Construction.Blocks.IsEmpty
            ? Collision(inventory.Construction) : null;
        var attempt = new ConsoleRelocationAttempt(root, new(root.Terrain, source, destination), selection, obstacles);
        var sourceView = terrain.View;
        token.ThrowIfCancellationRequested();

        TerrainPublicationOutcome outcome;
        try
        {
            outcome = await LoadAsync(terrain, renderer, destination, heading, sourceView.Frame, token, relocation: attempt)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) when (attempt.CommittedPose is null)
        {
            outcome = TerrainPublicationOutcome.Busy;
        }

        if (attempt.CommittedPose is { } pose)
        {
            if (outcome != TerrainPublicationOutcome.Presented)
                throw new InvalidOperationException("Committed relocation was not presented.");
            return (pose, "Teleported.");
        }

        // The view and mask may be newer, but the old root still owns the walking pose.
        if (!ReferenceEquals(_ports.Committed.Read().Root, root))
            throw new InvalidOperationException("Source terrain changed before relocation recovery.");
        if (terrain.View != sourceView)
        {
            var restored = await LoadAsync(terrain, renderer, source.Position, source.Heading, terrain.View.Frame,
                token, recovery: true).ConfigureAwait(false);
            if (restored != TerrainPublicationOutcome.Presented ||
                _ports.Committed.Read().Root is not VolumeWorldRoot restoredRoot || restoredRoot.Player != source ||
                restoredRoot.Terrain != _published || restoredRoot.Terrain.TerrainRevision != root.Terrain.TerrainRevision)
                throw new InvalidOperationException("Could not restore the source view after relocation.");
        }

        return (null, attempt.Status switch
        {
            VolumeRelocationStatus.Obstructed => "Teleport blocked.\nTerrain or construction.",
            VolumeRelocationStatus.Unsupported => "No walkable support within 1 m.",
            VolumeRelocationStatus.InvalidDestination => "Invalid teleport destination.",
            VolumeRelocationStatus.StaleSource => "Teleport source changed. Try again.",
            _ => outcome == TerrainPublicationOutcome.Busy ? "Teleport terrain busy. Try again." : "Teleport terrain unavailable.",
        });
    }

    private PreparedBarrierPublication? PrepareConsoleRelocation(ConsoleRelocationAttempt attempt, D3D12Renderer renderer,
        ulong generation, CandidateSet commit, out PlanetWalkState? pose)
    {
        if (!ReferenceEquals(_ports.Committed.Read().Root, attempt.Source))
        {
            attempt.Status = VolumeRelocationStatus.StaleSource;
            pose = null;
            return null;
        }

        var prepared = renderer.TryPrepareVolumeRelocation(generation, commit, [], attempt.Request,
            out var status, out pose, attempt.Obstacles?.Query);
        attempt.Status = status;
        return prepared;
    }
}
