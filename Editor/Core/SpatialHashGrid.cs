using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// Open-addressing hash-table entry for one occupied 3D grid cell (16 bytes).
    /// Layout must match <c>CellEntry</c> in FastCurvatureBake.compute.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CellEntry
    {
        public uint KeyLo; // cx (21 bits) | (cy & 0x7FF) << 21
        public uint KeyHi; // (cy >> 11) (10 bits) | (cz << 10) (21 bits)
        public uint Start; // first item index in SortedItems
        public uint Count; // number of items in this cell (0 = empty slot)

        public const int Stride = 16;
    }

    /// <summary>
    /// Exact open-addressing spatial hash grid over 3D items, built on the CPU and queried on the GPU.
    /// Only occupied cells are stored (load factor &lt;= 0.5), which keeps the table small enough for
    /// GPU L2 cache and eliminates both bucket collisions and the 351-check deduplication loop.
    /// Key packing and hashing must stay identical to <c>LookupCell</c> in FastCurvatureBake.compute.
    /// </summary>
    internal sealed class SpatialHashGrid<T> where T : struct
    {
        private const int MaxCoord = (1 << 21) - 1;
        private const float MaxSafeSpanInCells = 1000000f;

        public T[] SortedItems;
        public CellEntry[] CellTable; // power-of-two open-addressing table
        public int TableSize;         // power of two
        public Vector3 Origin;
        public float CellSize;

        public uint HashMask => (uint)TableSize - 1u;

        public static SpatialHashGrid<T> Empty(Bounds bounds, float cellSize)
        {
            float safeCell = Mathf.Max(cellSize, 1e-5f);
            return new SpatialHashGrid<T>
            {
                SortedItems = Array.Empty<T>(),
                CellTable = new CellEntry[1],
                TableSize = 1,
                Origin = bounds.min - Vector3.one * (safeCell * 2f),
                CellSize = safeCell,
            };
        }

        public static SpatialHashGrid<T> Build(
            T[] items, Vector3[] itemPositions, Bounds bounds, float cellSize,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (items == null || items.Length == 0)
                return Empty(bounds, cellSize);

            // Guarantee every cell coordinate stays well within the 21-bit packed range.
            float maxSpan = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            float safeCell = Mathf.Max(cellSize, Mathf.Max(1e-5f, maxSpan / MaxSafeSpanInCells));

            // Two cells of margin keep every queried cell coordinate (including -1 neighbours) non-negative.
            Vector3 origin = bounds.min - Vector3.one * (safeCell * 2f);
            float invCell = 1f / safeCell;

            int n = items.Length;
            var itemCellIndex = new int[n];
            var keyToDense = new Dictionary<ulong, int>(Math.Min(n, 65536));
            var cellKeys = new List<ulong>(Math.Min(n, 65536));
            var cellHashes = new List<uint>(Math.Min(n, 65536));
            var cellCounts = new List<uint>(Math.Min(n, 65536));

            for (int i = 0; i < n; i++)
            {
                if ((i & 8191) == 0) token.ThrowIfCancellationRequested();

                Vector3 rel = (itemPositions[i] - origin) * invCell;
                uint cx = ClampCoord(FloorToInt(rel.x));
                uint cy = ClampCoord(FloorToInt(rel.y));
                uint cz = ClampCoord(FloorToInt(rel.z));

                PackKey(cx, cy, cz, out uint keyLo, out uint keyHi);
                ulong fullKey = ((ulong)keyHi << 32) | keyLo;

                if (!keyToDense.TryGetValue(fullKey, out int dense))
                {
                    dense = cellCounts.Count;
                    keyToDense.Add(fullKey, dense);
                    cellKeys.Add(fullKey);
                    cellHashes.Add(HashCell(cx, cy, cz));
                    cellCounts.Add(1u);
                }
                else
                {
                    cellCounts[dense]++;
                }
                itemCellIndex[i] = dense;
            }
            token.ThrowIfCancellationRequested();

            int occupiedCells = cellCounts.Count;
            var cursor = new uint[occupiedCells];
            var starts = new uint[occupiedCells];
            uint running = 0;
            for (int c = 0; c < occupiedCells; c++)
            {
                starts[c] = running;
                cursor[c] = running;
                running += cellCounts[c];
            }

            var sorted = new T[n];
            for (int i = 0; i < n; i++)
            {
                if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                sorted[cursor[itemCellIndex[i]]++] = items[i];
            }
            token.ThrowIfCancellationRequested();

            int tableSize = NextPowerOfTwo(Math.Max(occupiedCells * 2, 1024));
            uint mask = (uint)tableSize - 1u;
            var table = new CellEntry[tableSize];
            for (int c = 0; c < occupiedCells; c++)
            {
                ulong fullKey = cellKeys[c];
                uint slot = cellHashes[c] & mask;
                while (table[slot].Count != 0)
                    slot = (slot + 1u) & mask;

                table[slot] = new CellEntry
                {
                    KeyLo = (uint)fullKey,
                    KeyHi = (uint)(fullKey >> 32),
                    Start = starts[c],
                    Count = cellCounts[c],
                };
            }

            return new SpatialHashGrid<T>
            {
                SortedItems = sorted,
                CellTable = table,
                TableSize = tableSize,
                Origin = origin,
                CellSize = safeCell,
            };
        }

        private static uint ClampCoord(int v) => v <= 0 ? 0u : (v >= MaxCoord ? (uint)MaxCoord : (uint)v);

        private static int FloorToInt(float v) => (int)Math.Floor(v);

        private static void PackKey(uint cx, uint cy, uint cz, out uint keyLo, out uint keyHi)
        {
            keyLo = (cx & 0x1FFFFFu) | ((cy & 0x7FFu) << 21);
            keyHi = ((cy >> 11) & 0x3FFu) | ((cz & 0x1FFFFFu) << 10);
        }

        private static uint HashCell(uint cx, uint cy, uint cz)
        {
            unchecked
            {
                return (cx * 73856093u) ^ (cy * 19349663u) ^ (cz * 83492791u);
            }
        }

        private static int NextPowerOfTwo(int v)
        {
            int p = 1;
            while (p < v && p < (1 << 30)) p <<= 1;
            return p;
        }
    }

    internal static class SpatialHashGridBuilders
    {
        public static SpatialHashGrid<SurfaceSample> BuildSamples(
            SurfaceSample[] samples, Bounds bounds, float cellSize, CancellationToken token)
        {
            if (samples == null || samples.Length == 0)
                return SpatialHashGrid<SurfaceSample>.Empty(bounds, cellSize);

            var positions = new Vector3[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                positions[i] = samples[i].Position;
            return SpatialHashGrid<SurfaceSample>.Build(samples, positions, bounds, cellSize, token);
        }

        public static SpatialHashGrid<EdgeSegment> BuildEdges(
            EdgeSegment[] segments, Bounds bounds, float cellSize, CancellationToken token)
        {
            if (segments == null || segments.Length == 0)
                return SpatialHashGrid<EdgeSegment>.Empty(bounds, cellSize);

            var positions = new Vector3[segments.Length];
            for (int i = 0; i < segments.Length; i++)
                positions[i] = segments[i].Midpoint;
            return SpatialHashGrid<EdgeSegment>.Build(segments, positions, bounds, cellSize, token);
        }
    }
}
