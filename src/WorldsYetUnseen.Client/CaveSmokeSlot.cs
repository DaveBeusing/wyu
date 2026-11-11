using Groundwork.Persistence;

namespace WorldsYetUnseen.Client;

internal static partial class GameHost
{
    private static async Task<VolumeWorldSlot> OpenCaveSmokeSlotAsync(string root, string phase, CancellationToken token)
    {
        var fixture = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(fixture, "wyu-cave-smoke.fixture")))
            throw new InvalidOperationException("Cave smoke requires an isolated fixture directory.");
        var volume = Path.Combine(fixture, "v2");
        if (phase == "init")
        {
            if (Directory.Exists(volume))
                throw new InvalidOperationException("Cave smoke must start with a fresh world.");
            return VolumeWorldSlot.Create(volume, CavePlanet.Seed, cave: true);
        }
        ArgumentOutOfRangeException.ThrowIfNotEqual(phase, "resume");
        var id = VolumeWorldSlot.List(volume).Single();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var slot = VolumeWorldSlot.Open(volume, id, out var status);
            if (slot?.Cave is not null)
                return slot;
            slot?.Dispose();
            if (status is not (VolumeSlotStatus.Busy or VolumeSlotStatus.Indeterminate))
                throw new InvalidOperationException($"Cave checkpoint could not continue ({status}).");
            await Task.Delay(50, token).ConfigureAwait(false);
        }
        throw new TimeoutException("Cave checkpoint remained locked after writer exit.");
    }
}
