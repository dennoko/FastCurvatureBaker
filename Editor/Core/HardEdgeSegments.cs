using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>GPU layout must match <c>EdgeSegment</c> in FastCurvatureBake.compute (64 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EdgeSegment
    {
        public Vector3 A;
        public Vector3 B;
        public Vector3 FaceNormalA;
        public Vector3 FaceNormalB;
        public float ValueA;
        public float ValueB;
        public uint PatchA;
        public uint PatchB;

        public const int Stride = 64;

        public Vector3 Midpoint => (A + B) * 0.5f;
    }

    /// <summary>
    /// Splits hard/sharp edges into short segments so they can be bucketed by midpoint in a
    /// <see cref="SpatialHashGrid{T}"/> whose cells are about the size of the edge width.
    /// </summary>
    internal static class HardEdgeSegments
    {
        public const int MaxSegments = 1 << 20;

        /// <param name="width">Distance from the edge over which it is visible.</param>
        /// <param name="maxReach">Maximum distance from a surface point to the midpoint of any segment within <paramref name="width"/>.</param>
        /// <param name="cellSize">Grid cell size that guarantees every segment within
        /// <paramref name="width"/> of a point lies in the point's 3x3x3 cell neighbourhood.</param>
        public static EdgeSegment[] Build(
            List<HardEdge> edges, float width, out float maxReach, out float cellSize,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            float totalLength = 0f;
            for (int i = 0; i < edges.Count; i++)
                totalLength += Vector3.Distance(edges[i].A, edges[i].B);

            // Segments no longer than the width, unless that would exceed the budget
            // (every edge takes at least one segment, so only the remainder is subdivided).
            int budget = Mathf.Max(MaxSegments - edges.Count, 1);
            float segmentLength = Mathf.Max(width, totalLength / budget);

            var segments = new List<EdgeSegment>(edges.Count);
            for (int e = 0; e < edges.Count; e++)
            {
                if ((e & 4095) == 0) token.ThrowIfCancellationRequested();
                HardEdge edge = edges[e];
                int pieces = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(edge.A, edge.B) / segmentLength));
                for (int i = 0; i < pieces; i++)
                {
                    float t0 = (float)i / pieces;
                    float t1 = (float)(i + 1) / pieces;
                    segments.Add(new EdgeSegment
                    {
                        A = Vector3.Lerp(edge.A, edge.B, t0),
                        B = Vector3.Lerp(edge.A, edge.B, t1),
                        FaceNormalA = edge.FaceNormalA,
                        FaceNormalB = edge.FaceNormalB,
                        ValueA = Mathf.Lerp(edge.ValueA, edge.ValueB, t0),
                        ValueB = Mathf.Lerp(edge.ValueA, edge.ValueB, t1),
                        PatchA = edge.PatchA,
                        PatchB = edge.PatchB,
                    });
                }
            }

            // A segment within `width` of p has its midpoint within width + segmentLength / 2.
            // 1% margin: CPU and GPU may floor a cell coordinate differently right on a boundary.
            maxReach = width + segmentLength * 0.5f;
            cellSize = maxReach * 1.01f;
            return segments.ToArray();
        }
    }
}
