using System;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// Hashed uniform grid over surface samples, built on the CPU and queried on the GPU.
    /// Samples are sorted by bucket; <see cref="Buckets"/> holds (start, count) pairs.
    /// The hash must stay identical to <c>HashCell</c> in FastCurvatureBake.compute.
    /// </summary>
    internal sealed class SpatialHashGrid
    {
        public SurfaceSample[] SortedSamples;
        public uint[] Buckets;      // interleaved (start, count), length = 2 * BucketCount
        public int BucketCount;     // power of two
        public Vector3 Origin;
        public float CellSize;

        public uint HashMask => (uint)BucketCount - 1;

        public static SpatialHashGrid Build(SurfaceSample[] samples, Bounds bounds, float cellSize)
        {
            int bucketCount = NextPowerOfTwo(Math.Max(samples.Length, 1024));
            // Two cells of margin keep every queried cell coordinate non-negative.
            Vector3 origin = bounds.min - Vector3.one * (cellSize * 2f);
            float invCell = 1f / cellSize;
            uint mask = (uint)bucketCount - 1;

            var bucketOf = new uint[samples.Length];
            var counts = new uint[bucketCount];
            for (int i = 0; i < samples.Length; i++)
            {
                Vector3 p = (samples[i].Position - origin) * invCell;
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

            var sorted = new SurfaceSample[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                sorted[cursor[bucketOf[i]]++] = samples[i];

            return new SpatialHashGrid
            {
                SortedSamples = sorted,
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
