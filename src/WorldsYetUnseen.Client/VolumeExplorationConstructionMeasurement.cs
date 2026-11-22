using System.Diagnostics;
using Groundwork.Core;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private async Task MeasureConstructionAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        PlanetWalkState player, CancellationToken token)
    {
        const int warmup = 20, samples = 100;
        if (ReadInventory() is not { Construction.Blocks.IsEmpty: true } || ConstructionRenderer is not { } visuals)
            throw new InvalidOperationException("Construction measurements require a dedicated empty fixture.");
        using var process = Process.GetCurrentProcess();
        var baseline = process.WorkingSet64;
        var origin = Add(Add(player.Position, Scale(Unit(player.Position), 2.05)), Scale(player.Heading, 2.8));
        if (!ConstructionAnchor.TryCreate(origin, out var draftAnchor))
            throw new InvalidOperationException("Construction measurement anchor unavailable.");
        var anchor = draftAnchor with { Id = 1 };
        var target = new ConstructionTarget(anchor, 0, 0, 0, ConstructionBlockType.Long, 0);

        foreach (var count in new[] { 16, 256, 1440 })
        {
            var previous = ReadInventory()!;
            var construction = new ConstructionState(previous.Construction.Revision + 1, (uint)count + 1, [anchor],
                [.. Enumerable.Range(0, count - 1).Select(i => new ConstructionBlock((uint)i + 2, anchor.Id,
                    (short)(3 + 3 * (i % 40)), 0, (short)(-50 + 3 * (i / 40)), i % 2 == 0 ? ConstructionBlockType.Cube : ConstructionBlockType.Long, 0))]);
            if (!construction.IsValid())
                throw new InvalidOperationException("Construction measurement footprint is invalid.");
            var items = InventoryState.Empty with
            {
                Revision = checked(previous.Items.Revision + 1),
                Carried = [new(0, InventoryItem.Ore, 20)],
                Equipped = [new(2, InventoryItem.Tool, 1)],
            };
            await PublishMeasuredStateAsync(terrain, previous, construction, items, token).ConfigureAwait(false);
            await PresentMeasuredStateAsync(terrain, renderer, visuals, player, construction, null, token).ConfigureAwait(false);

            var preview = new double[samples];
            var place = new double[samples];
            var remove = new double[samples];
            var placePresent = new double[samples];
            var removePresent = new double[samples];
            var rejected = new double[samples];
            long previewBytes = 0, placeBytes = 0, removeBytes = 0, rejectedBytes = 0, peak = baseline;
            for (var i = -warmup; i < samples; i++)
            {
                token.ThrowIfCancellationRequested();
                previous = ReadInventory()!;
                var command = new BuildCommand(_buildSession, 0, previous, _published, target, 0, false);
                var alloc = GC.GetAllocatedBytesForCurrentThread();
                var at = Stopwatch.GetTimestamp();
                _ = PrepareMeasuredPlacement(terrain, player, command);
                var previewUs = Stopwatch.GetElapsedTime(at).TotalMicroseconds;
                var previewAlloc = GC.GetAllocatedBytesForCurrentThread() - alloc;
                await PresentMeasuredStateAsync(terrain, renderer, visuals, player, previous.Construction, target.Box, token).ConfigureAwait(false);

                alloc = GC.GetTotalAllocatedBytes();
                at = Stopwatch.GetTimestamp();
                var plan = PrepareMeasuredPlacement(terrain, player, command);
                if (plan.Construction.Blocks.Length != count)
                    throw new InvalidOperationException("Unexpected construction measurement size.");
                await PublishMeasuredStateAsync(terrain, previous, plan.Construction, plan.Inventory, token).ConfigureAwait(false);
                var placedAt = Stopwatch.GetTimestamp();
                var placedUs = Stopwatch.GetElapsedTime(at, placedAt).TotalMicroseconds;
                var placeAlloc = GC.GetTotalAllocatedBytes() - alloc;
                await PresentMeasuredStateAsync(terrain, renderer, visuals, player, plan.Construction, null, token).ConfigureAwait(false);
                var placedFrameUs = Stopwatch.GetElapsedTime(placedAt).TotalMicroseconds;
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);

                var withBlock = ReadInventory()!;
                if (count == ConstructionState.MaximumBlocks && i >= 0)
                {
                    at = Stopwatch.GetTimestamp();
                    alloc = GC.GetAllocatedBytesForCurrentThread();
                    if (ConstructionOperations.Place(withBlock.Construction, withBlock.Items, target with { Y = 2 }).Status != ConstructionStatus.Limit)
                        throw new InvalidOperationException("Block 1441 escaped the limit.");
                    rejected[i] = Stopwatch.GetElapsedTime(at).TotalMicroseconds;
                    rejectedBytes += GC.GetAllocatedBytesForCurrentThread() - alloc;
                }

                alloc = GC.GetTotalAllocatedBytes();
                var removingAt = Stopwatch.GetTimestamp();
                var refund = ConstructionOperations.Remove(withBlock.Construction, withBlock.Items, plan.Construction.Blocks[^1].Id);
                if (refund.Status != ConstructionStatus.Applied || refund.Construction.Blocks.Length != count - 1 || refund.Inventory.OreCount != 20)
                    throw new InvalidOperationException("Construction measurement refund failed.");
                await PublishMeasuredStateAsync(terrain, withBlock, refund.Construction, refund.Inventory, token).ConfigureAwait(false);
                var removedAt = Stopwatch.GetTimestamp();
                var removeAlloc = GC.GetTotalAllocatedBytes() - alloc;
                await PresentMeasuredStateAsync(terrain, renderer, visuals, player, refund.Construction, null, token).ConfigureAwait(false);
                var removedFrameUs = Stopwatch.GetElapsedTime(removedAt).TotalMicroseconds;

                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                if (i < 0)
                    continue;
                preview[i] = previewUs;
                // Preview and rejected commands have their own clock and are outside this interval.
                place[i] = placedUs;
                remove[i] = Stopwatch.GetElapsedTime(removingAt, removedAt).TotalMicroseconds;
                placePresent[i] = placedFrameUs;
                removePresent[i] = removedFrameUs;
                previewBytes += previewAlloc;
                placeBytes += placeAlloc;
                removeBytes += removeAlloc;
            }
            var line = FormattableString.Invariant($"blocks={count} warmup={warmup} samples={samples} previewPlan={Percentiles(preview)} place={Percentiles(place)} remove={Percentiles(remove)} placePresent={Percentiles(placePresent)} removePresent={Percentiles(removePresent)} previewPlanB={previewBytes / samples} placeProcessB={placeBytes / samples} removeProcessB={removeBytes / samples} sampledWorkingSetDelta={peak - baseline}");
            Console.WriteLine($"WP76 native {line}");
            diagnostics?.Write("construction-measure", line);
            if (count == ConstructionState.MaximumBlocks)
                Console.WriteLine($"WP76 native blocked=1441 samples={samples} domainUs={Percentiles(rejected)} B={rejectedBytes / samples}");
        }
    }

    private static ConstructionChange PrepareMeasuredPlacement(VolumeStreamingCoordinator terrain, PlanetWalkState player, BuildCommand command)
    {
        using var lease = terrain.TryRead() ?? throw new InvalidOperationException("Construction proof lost its terrain lease.");
        var plan = PlanConstruction(lease, command.Base, player, command, out var reason);
        return plan is { Status: ConstructionStatus.Applied } accepted ? accepted :
            throw new InvalidOperationException($"Construction measurement placement rejected ({reason ?? plan?.Status.ToString()}).");
    }

    private async Task PublishMeasuredStateAsync(VolumeStreamingCoordinator terrain, CaveInventory previous,
        ConstructionState construction, InventoryState items, CancellationToken token)
    {
        var begin = await BeginAsync(token).ConfigureAwait(false);
        terrain.BeginTick(begin);
        var collision = new ConstructionCollision(construction);
        var cut = await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false);
        if (!CommitInventoryRoot(cut, previous, previous with { Construction = construction, Items = items }, token))
            throw new InvalidOperationException("Construction measurement commit was held.");
        _collision = collision;
        _collisionState = construction;
    }

    private async Task PresentMeasuredStateAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        ConstructionVisuals visuals, PlanetWalkState player, ConstructionState construction, ConstructionBox? preview,
        CancellationToken token)
    {
        var camera = Camera(terrain.View, player.Position, player.Heading);
        var deadline = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(10))
        {
            token.ThrowIfCancellationRequested();
            Props?.Pump();
            Drain(terrain);
            visuals.Update(terrain.WorldIdentity, _published, construction, preview);
            var sequence = ++_sequence;
            if (renderer.TrySubmitVolumeCamera(_published.Generation, sequence, camera) == RenderSubmissionResult.Accepted)
                await WaitAsync(renderer, () => renderer.Read().LastPresentedSequence >= sequence, token).ConfigureAwait(false);
            if (preview is { } box && visuals.HasPresentedPreview(_published, construction.Revision, box) ||
                preview is null && visuals.IsPresented(_published, construction.Revision, sequence))
                return;
            await Task.Delay(8, token).ConfigureAwait(false);
        }
        throw new TimeoutException("Construction measurement did not receive a matching Present.");
    }

    private static string Percentiles(double[] values)
    {
        Array.Sort(values);
        return FormattableString.Invariant($"{values[49]:F2}/{values[94]:F2}us");
    }
}
