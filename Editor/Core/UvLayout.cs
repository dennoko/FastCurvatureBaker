using System;
using System.Collections.Generic;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// UV-space preparation for the G-buffer rasterization.
    /// </summary>
    internal static class UvLayout
    {
        /// <summary>Conservative rasterization reach in pixels. Must match <c>ConservativeRadius</c> in the shader.</summary>
        public const float ConservativeRadius = 0.75f;

        public const int MinTileSize = 16;

        /// <summary>Upper bound of work items; tiles grow when a layout would exceed it.</summary>
        public const int MaxTiles = 1 << 23;

        /// <summary>
        /// Splits every triangle's pixel footprint (expanded by <see cref="ConservativeRadius"/>) into
        /// square tiles aligned to a global grid and returns the tiles that can touch the triangle, as
        /// interleaved (triangle, tileX | tileY &lt;&lt; 16) pairs. One GPU thread handles one tile, so the
        /// work per thread is bounded by <paramref name="tileSize"/>² pixels regardless of triangle size.
        /// </summary>
        public static uint[] BuildRasterTiles(int[] triangles, Vector2[] uvs, int resolution, out int tileSize)
        {
            int triangleCount = triangles.Length / 3;
            tileSize = MinTileSize;
            while (tileSize < resolution && CountTiles(triangles, uvs, resolution, tileSize) > MaxTiles)
                tileSize *= 2;

            var items = new List<uint>(triangleCount * 2);
            for (int t = 0; t < triangleCount; t++)
            {
                if (!PixelFootprint(triangles, uvs, resolution, t, out Vector2 a, out Vector2 b, out Vector2 c,
                        out Vector2Int lo, out Vector2Int hi))
                    continue;

                for (int ty = lo.y / tileSize; ty <= hi.y / tileSize; ty++)
                {
                    for (int tx = lo.x / tileSize; tx <= hi.x / tileSize; tx++)
                    {
                        // Pixel centres of this tile that are also inside the footprint.
                        float x0 = Mathf.Max(tx * tileSize, lo.x), x1 = Mathf.Min(tx * tileSize + tileSize - 1, hi.x);
                        float y0 = Mathf.Max(ty * tileSize, lo.y), y1 = Mathf.Min(ty * tileSize + tileSize - 1, hi.y);
                        if (!RectNearTriangle(a, b, c, x0, y0, x1, y1))
                            continue;

                        items.Add((uint)t);
                        items.Add((uint)tx | ((uint)ty << 16));
                    }
                }
            }
            return items.ToArray();
        }

        /// <summary>Upper bound of the tile count (footprint bounding boxes only).</summary>
        private static long CountTiles(int[] triangles, Vector2[] uvs, int resolution, int tileSize)
        {
            long count = 0;
            for (int t = 0; t < triangles.Length / 3; t++)
            {
                if (PixelFootprint(triangles, uvs, resolution, t, out _, out _, out _, out Vector2Int lo, out Vector2Int hi))
                    count += (long)(hi.x / tileSize - lo.x / tileSize + 1) * (hi.y / tileSize - lo.y / tileSize + 1);
            }
            return count;
        }

        /// <summary>
        /// Triangle corners in pixel space (texel centres on integers, as in the shader's <c>LoadTriangle</c>)
        /// and the clamped pixel range within <see cref="ConservativeRadius"/> of its bounding box.
        /// False for degenerate or fully off-texture triangles.
        /// </summary>
        private static bool PixelFootprint(
            int[] triangles, Vector2[] uvs, int resolution, int t,
            out Vector2 a, out Vector2 b, out Vector2 c, out Vector2Int lo, out Vector2Int hi)
        {
            a = uvs[triangles[t * 3]] * resolution - new Vector2(0.5f, 0.5f);
            b = uvs[triangles[t * 3 + 1]] * resolution - new Vector2(0.5f, 0.5f);
            c = uvs[triangles[t * 3 + 2]] * resolution - new Vector2(0.5f, 0.5f);
            lo = hi = default;
            if (Mathf.Abs(Cross(b - a, c - a)) <= 1e-10f)
                return false;

            Vector2 min = Vector2.Min(a, Vector2.Min(b, c)) - Vector2.one * ConservativeRadius;
            Vector2 max = Vector2.Max(a, Vector2.Max(b, c)) + Vector2.one * ConservativeRadius;
            if (!(max.x >= 0f && max.y >= 0f && min.x <= resolution - 1 && min.y <= resolution - 1))
                return false; // also rejects NaN UVs

            lo = new Vector2Int(Math.Max(Mathf.CeilToInt(min.x), 0), Math.Max(Mathf.CeilToInt(min.y), 0));
            hi = new Vector2Int(Math.Min(Mathf.FloorToInt(max.x), resolution - 1), Math.Min(Mathf.FloorToInt(max.y), resolution - 1));
            return lo.x <= hi.x && lo.y <= hi.y;
        }

        /// <summary>
        /// False when the rectangle lies entirely outside one of the triangle's edges by more than
        /// <see cref="ConservativeRadius"/> (then no pixel in it can be within reach of the triangle).
        /// </summary>
        private static bool RectNearTriangle(Vector2 a, Vector2 b, Vector2 c, float x0, float y0, float x1, float y1)
        {
            float orientation = Mathf.Sign(Cross(b - a, c - a));
            return !OutsideEdge(a, b, orientation, x0, y0, x1, y1) &&
                   !OutsideEdge(b, c, orientation, x0, y0, x1, y1) &&
                   !OutsideEdge(c, a, orientation, x0, y0, x1, y1);
        }

        private static bool OutsideEdge(Vector2 p0, Vector2 p1, float orientation, float x0, float y0, float x1, float y1)
        {
            Vector2 e = p1 - p0;
            float length = e.magnitude;
            if (length <= 0f) return false;

            // Signed distance to the edge line, positive towards the triangle interior; linear, so the
            // maximum over the rectangle is at a corner. Small slack for float differences with the GPU.
            float limit = -(ConservativeRadius + 0.01f) * length;
            return orientation * Cross(e, new Vector2(x0, y0) - p0) < limit &&
                   orientation * Cross(e, new Vector2(x1, y0) - p0) < limit &&
                   orientation * Cross(e, new Vector2(x0, y1) - p0) < limit &&
                   orientation * Cross(e, new Vector2(x1, y1) - p0) < limit;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
