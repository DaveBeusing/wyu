using System.Buffers;
using System.Buffers.Binary;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal static class InventoryOrePreview
{
    private const int RowBytes = 32;
    // Groundwork's canonical overlay retains these flags across fill/remove cycles.
    private const long PreviouslyDepletedOrPlaced = (1L << 16) | (1L << 17);

    internal static bool TryCount(VolumeStreamingLease lease, PlanetGeologyField baseline, LatticeCoordinate center,
        out int deposits)
    {
        deposits = 0;
        var size = lease.EditOverlayBytes;
        if (size < 8 || lease.EditLineage != baseline.Descriptor)
            return false;
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var bytes = buffer.AsSpan(0, size);
            if (!lease.TryWriteEditOverlay(bytes, out var written) || written != size)
                return false;
            var densityCount = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            var materialCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
            if (densityCount < 0 || materialCount < 0 || 8L + RowBytes * ((long)densityCount + materialCount) != size)
                return false;
            var split = 8 + RowBytes * densityCount;
            var density = bytes[8..split];
            var material = bytes[split..];

            for (var x = -2; x <= 1; x++)
            for (var y = -2; y <= 1; y++)
            for (var z = -2; z <= 1; z++)
            {
                var cell = new LatticeCoordinate(center.X + x, center.Y + y, center.Z + z);
                if (baseline.SampleMaterial(cell) != CavePlanet.Ore ||
                    TryRead(material, cell, out var previous) && (previous & PreviouslyDepletedOrPlaced) != 0)
                    continue;
                if (DepletesOre(density, baseline, center, cell))
                    deposits++;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Preview only. The authoritative edit recomputes depletion and accepts the warning
    // only for the exact brush, inventory revision and count staged by Groundwork.
    private static bool DepletesOre(ReadOnlySpan<byte> density, PlanetGeologyField baseline, LatticeCoordinate center,
        LatticeCoordinate cell)
    {
        var hadSolid = false;
        for (var x = 0; x <= 1; x++)
        for (var y = 0; y <= 1; y++)
        for (var z = 0; z <= 1; z++)
        {
            var corner = new LatticeCoordinate(cell.X + x, cell.Y + y, cell.Z + z);
            var value = TryRead(density, corner, out var edited) ? edited : baseline.SampleDensity(corner);
            if (value >= 0)
                continue;
            if (Math.Abs(corner.X - center.X) > 1 || Math.Abs(corner.Y - center.Y) > 1 || Math.Abs(corner.Z - center.Z) > 1)
                return false;
            hadSolid = true;
        }
        return hadSolid;
    }

    private static bool TryRead(ReadOnlySpan<byte> rows, LatticeCoordinate key, out long value)
    {
        var low = 0;
        var high = rows.Length / RowBytes - 1;
        while (low <= high)
        {
            var index = low + (high - low) / 2;
            var row = rows.Slice(index * RowBytes, RowBytes);
            var compare = Read(row).CompareTo(key.X);
            if (compare == 0) compare = Read(row[8..]).CompareTo(key.Y);
            if (compare == 0) compare = Read(row[16..]).CompareTo(key.Z);
            if (compare == 0)
            {
                value = Read(row[24..]);
                return true;
            }
            if (compare < 0) low = index + 1;
            else high = index - 1;
        }
        value = 0;
        return false;

        static long Read(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }
}
