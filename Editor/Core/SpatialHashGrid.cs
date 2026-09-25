using System;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// Hashed uniform grid over point-like items, built on the CPU and queried on the GPU.
    /// Items are sorted by bucket; <see cref="Buckets"/> holds (start, count) pairs.
    /// The hash must stay identical to <c>HashCell</c> in FastCurvatureBake.compute.
    /// </summary>
    internal sealed class SpatialHashGrid<T> where T : struct
    {
        public T[] SortedItems;
        public uint[] Buckets;      // interleaved (start, count), length = 2 * BucketCount
        public int BucketCount;     // power of two
        public Vector3 Origin;
        public float CellSize;

        public uint HashMask => (uint)BucketCount - 1;

        /// <param name="position">Point used to place each item in a cell.</param>
        public static SpatialHashGrid<T> Build(T[] items, Func<T, Vector3> position, Bounds bounds, float cellSize)
        {
            int bucketCount = NextPowerOfTwo(Math.Max(items.Length, 1024));
            // Two cells of margin keep every queried cell coordinate non-negative.
            Vector3 origin = bounds.min - Vector3.one * (cellSize * 2f);
            float invCell = 1f / cellSize;
            uint mask = (uint)bucketCount - 1;

            var bucketOf = new uint[items.Length];
            var counts = new uint[bucketCount];
            for (int i = 0; i < items.Length; i++)
            {
                Vector3 p = (position(items[i]) - origin) * invCell;
                uint b = HashCell(FloorToInt(p.x), FloorToInt(p.y), FloorToInt(p.z)) & mask;
                bucketOf[i] = b;
                counts[b]++;
            }

            var buckets = new uint[bucketCount * 2];
            var cursor = new uint[bucketCount];
            uint running = 0;
            for (int b = 0; b < bucketCount; b++)
            {
                buckets[b * 2] = running;
                buckets[b * 2 + 1] = counts[b];
                cursor[b] = running;
                running += counts[b];
            }

            var sorted = new T[items.Length];
            for (int i = 0; i < items.Length; i++)
                sorted[cursor[bucketOf[i]]++] = items[i];

            return new SpatialHashGrid<T>
            {
                SortedItems = sorted,
                Buckets = buckets,
                BucketCount = bucketCount,
                Origin = origin,
                CellSize = cellSize,
            };
        }

        private static int FloorToInt(float v) => (int)Math.Floor(v);

        private static uint HashCell(int x, int y, int z)
        {
            unchecked
            {
                return ((uint)x * 73856093u) ^ ((uint)y * 19349663u) ^ ((uint)z * 83492791u);
            }
        }

        private static int NextPowerOfTwo(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }
    }
}
