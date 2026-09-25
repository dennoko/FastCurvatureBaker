using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>GPU layout must match <c>EdgeSegment</c> in FastCurvatureBake.compute (56 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EdgeSegment
    {
        public Vector3 A;
        public Vector3 B;
        public Vector3 FaceNormalA;
        public Vector3 FaceNormalB;
        public float Value;
        public uint Component;

        public const int Stride = 56;

        public Vector3 Midpoint => (A + B) * 0.5f;
    }

    /// <summary>
    /// Splits hard edges into short segments so they can be bucketed by midpoint in a
    /// <see cref="SpatialHashGrid{T}"/> whose cells are about the size of the edge width.
    /// </summary>
    internal static class HardEdgeSegments
    {
        public const int MaxSegments = 1 << 20;

        /// <param name="width">Distance from the edge over which it is visible.</param>
        /// <param name="cellSize">Grid cell size that guarantees every segment within
        /// <paramref name="width"/> of a point lies in the point's 3x3x3 cell neighbourhood.</param>
        public static EdgeSegment[] Build(List<HardEdge> edges, float width, out float cellSize)
        {
            float totalLength = 0f;
            foreach (var edge in edges)
                totalLength += Vector3.Distance(edge.A, edge.B);

            // Segments no longer than the width, unless that would exceed the budget
            // (every edge takes at least one segment, so only the remainder is subdivided).
            int budget = Mathf.Max(MaxSegments - edges.Count, 1);
            float segmentLength = Mathf.Max(width, totalLength / budget);

            var segments = new List<EdgeSegment>(edges.Count);
            foreach (var edge in edges)
            {
                int pieces = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(edge.A, edge.B) / segmentLength));
                for (int i = 0; i < pieces; i++)
                {
                    segments.Add(new EdgeSegment
                    {
                        A = Vector3.Lerp(edge.A, edge.B, (float)i / pieces),
                        B = Vector3.Lerp(edge.A, edge.B, (float)(i + 1) / pieces),
                        FaceNormalA = edge.FaceNormalA,
                        FaceNormalB = edge.FaceNormalB,
                        Value = edge.Value,
                        Component = edge.Component,
                    });
                }
            }

            // A segment within `width` of p has its midpoint within width + segmentLength / 2.
            // 1% margin: CPU and GPU may floor a cell coordinate differently right on a boundary.
            cellSize = (width + segmentLength * 0.5f) * 1.01f;
            return segments.ToArray();
        }
    }
}
