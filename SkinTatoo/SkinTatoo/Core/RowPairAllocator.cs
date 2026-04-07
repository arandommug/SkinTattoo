using System;
using System.Linq;

namespace SkinTatoo.Core;

/// <summary>
/// Per-TargetGroup allocator for ColorTable row pairs (0-15).
/// Scans vanilla normal.a to mark occupied slots, then hands out
/// unused slots on demand when a layer enables its first Affects* field.
/// </summary>
public class RowPairAllocator
{
    // occupied[i] tracks "vanilla uses row pair i"
    private readonly bool[] vanillaOccupied = new bool[16];
    // assigned[i] tracks "we've handed row pair i to a layer"
    private readonly bool[] assigned = new bool[16];

    private bool scanned;

    /// <summary>Whether vanilla scan has been performed (skip re-scan on reset).</summary>
    public bool Scanned => scanned;

    /// <summary>
    /// Scan vanilla index map's R channel histogram and mark high-frequency row pairs
    /// as occupied. Per Penumbra MaterialExporter:136, character.shpk reads the row pair
    /// from g_SamplerIndex.r as `round(R / 17)`. Threshold: any row pair covering ≥0.5%
    /// of pixels is "vanilla occupied". Idempotent.
    /// </summary>
    public void ScanVanillaOccupation(byte[] vanillaIndexRgba, int width, int height)
    {
        Array.Clear(vanillaOccupied, 0, 16);
        scanned = true;

        if (vanillaIndexRgba == null || vanillaIndexRgba.Length < 4) return;
        if (width <= 0 || height <= 0) return;

        var histogram = new int[16];
        int totalPixels = width * height;
        // Iterate R channel (offset 0 in RGBA byte order)
        for (int i = 0; i < vanillaIndexRgba.Length; i += 4)
        {
            int rowPair = (int)Math.Round(vanillaIndexRgba[i] / 17.0);
            if (rowPair >= 0 && rowPair < 16)
                histogram[rowPair]++;
        }

        // Threshold: row pair must cover ≥0.5% of pixels to count as "used"
        int threshold = Math.Max(1, totalPixels / 200);
        for (int i = 0; i < 16; i++)
        {
            if (histogram[i] > threshold)
                vanillaOccupied[i] = true;
        }
    }

    /// <summary>Allocate an unused row pair, or null if exhausted.</summary>
    public int? TryAllocate()
    {
        for (int i = 0; i < 16; i++)
        {
            if (!vanillaOccupied[i] && !assigned[i])
            {
                assigned[i] = true;
                return i;
            }
        }
        return null;
    }

    /// <summary>Release a previously-allocated row pair.</summary>
    public void Release(int rowPair)
    {
        if (rowPair >= 0 && rowPair < 16)
            assigned[rowPair] = false;
        // Vanilla-occupied entries are never released — we never allocated them.
    }

    /// <summary>Number of row pairs still available to allocate.</summary>
    public int AvailableSlots
    {
        get
        {
            int count = 0;
            for (int i = 0; i < 16; i++)
                if (!vanillaOccupied[i] && !assigned[i]) count++;
            return count;
        }
    }

    /// <summary>Total slots occupied by vanilla (informational).</summary>
    public int VanillaOccupiedCount => vanillaOccupied.Count(b => b);

    /// <summary>Reset all assignments but keep vanilla scan.</summary>
    public void ReleaseAll()
    {
        Array.Clear(assigned, 0, 16);
    }

    /// <summary>Fully reset including vanilla scan (e.g. on TargetGroup swap).</summary>
    public void Reset()
    {
        Array.Clear(vanillaOccupied, 0, 16);
        Array.Clear(assigned, 0, 16);
        scanned = false;
    }
}
