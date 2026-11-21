using Groundwork.Core;
using Groundwork.Persistence;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    internal async Task<InventoryImportResult> ImportInventoryAsync(VolumeStreamingCoordinator terrain, D3D12Renderer renderer,
        CancellationToken token)
    {
        if (slot is not { NeedsInventoryImport: true } source)
            return new(null, VolumeSlotStatus.Incompatible, "The selected slot is already current.");

        await OpenTerrainAsync(terrain, renderer, token).ConfigureAwait(false);
        var begin = await BeginAsync(token).ConfigureAwait(false);
        using var reservation = terrain.TryReserveCheckpoint(begin.Tick);
        Finish(await SealAsync(BarrierKind.CommitResults, token).ConfigureAwait(false));
        if (reservation?.TryTake() is not { } cut)
            return new(null, VolumeSlotStatus.Busy, "Terrain is not ready for a verified import. Try importing again.");

        var imported = await Task.Run(() => source.ImportInventory(cut, token)).ConfigureAwait(false);
        if (imported.Slot is { } verified)
        {
            slot = verified;
            Notice("Imported and verified in a new save slot.");
        }
        return imported;
    }
}
