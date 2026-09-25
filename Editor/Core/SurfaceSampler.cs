using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>GPU layout must match <c>SurfaceSample</c> in FastCurvatureBake.compute (32 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SurfaceSample
    {
        public Vector3 Position;
        public Vector3 Normal;
        public uint Patch;     // smooth-patch id (see SurfaceMesh.PatchIds)
        public float Weight; // represented surface area

        public const int Stride = 32;
    }

    /// <summary>
    /// Distributes area-weighted point samples over the whole mesh surface.
    /// </summary>
    internal static class SurfaceSampler
    {
        public const int MaxSamples = 1 << 22;

        // R2 low-discrepancy sequence constants (1/g, 1/g^2 with g = plastic number).
        private const double R2A = 0.7548776662466927;
        private const double R2B = 0.5698402909980532;

        /// <param name="spacing">Target distance between neighbouring samples. May be enlarged to respect <see cref="MaxSamples"/>.</param>
        /// <param name="faceNormals">Give samples the triangle's geometric normal instead of the interpolated shading normal.</param>
        public static SurfaceSample[] Generate(SurfaceMesh mesh, ref float spacing, bool faceNormals)
        {
            // Measured once, reused by every budget attempt and the placement pass (same iteration order).
            var areas = new List<float>();
            var longestEdges = new List<float>();
            foreach (int[] tris in mesh.SubMeshTriangles)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    areas.Add(mesh.TriangleArea(i0, i1, i2));
                    longestEdges.Add(Mathf.Sqrt(Mathf.Max(
                        (mesh.Positions[i1] - mesh.Positions[i0]).sqrMagnitude,
                        Mathf.Max((mesh.Positions[i2] - mesh.Positions[i1]).sqrMagnitude,
                                  (mesh.Positions[i0] - mesh.Positions[i2]).sqrMagnitude))));
                }
            }
            int triangleCount = areas.Count;

            // Every triangle keeps at least one sample, so with more triangles than MaxSamples
            // the budget is the triangle count and the sample count exceeds MaxSamples.
            int budget = Mathf.Max(MaxSamples, triangleCount);
            long count = CountSamples(areas, longestEdges, spacing);
            for (int attempt = 0; attempt < 8 && count > budget; attempt++)
            {
                spacing *= Mathf.Sqrt((float)count / budget) * 1.02f;
                count = CountSamples(areas, longestEdges, spacing);
            }
            if (count > budget)
            {
                // Pathological input: fall back to one sample per triangle.
                spacing = float.MaxValue;
                count = triangleCount;
            }

            var samples = new SurfaceSample[count];
            int written = 0;
            int triangle = 0;
            foreach (int[] tris in mesh.SubMeshTriangles)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3, triangle++)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    float area = areas[triangle];
                    int k = SamplesForTriangle(area, longestEdges[triangle], spacing);
                    float weight = area / k;
                    uint patch = mesh.PatchIds[i0];
                    Vector3 faceNormal = faceNormals ? mesh.FaceNormal(i0, i1, i2) : Vector3.zero;

                    // Per-triangle offset decorrelates the sequence between neighbouring triangles.
                    uint h = Hash((uint)written);
                    double ou = (h & 0xFFFF) / 65536.0;
                    double ov = (h >> 16) / 65536.0;

                    for (int s = 0; s < k; s++)
                    {
                        float u, v;
                        if (k == 1)
                        {
                            u = v = 1f / 3f;
                        }
                        else
                        {
                            u = (float)Frac(ou + (s + 1) * R2A);
                            v = (float)Frac(ov + (s + 1) * R2B);
                            if (u + v > 1f)
                            {
                                u = 1f - u;
                                v = 1f - v;
                            }
                        }
                        float w0 = 1f - u - v;

                        samples[written++] = new SurfaceSample
                        {
                            Position = w0 * mesh.Positions[i0] + u * mesh.Positions[i1] + v * mesh.Positions[i2],
                            Normal = faceNormals
                                ? faceNormal
                                : SurfaceMesh.SafeNormalize(w0 * mesh.Normals[i0] + u * mesh.Normals[i1] + v * mesh.Normals[i2]),
                            Patch = patch,
                            Weight = weight,
                        };
                    }
                }
            }

            if (spacing == float.MaxValue)
                Debug.LogWarning($"[FastCurvatureBaker] '{mesh.Name}': too many triangles for the sample budget; quality is reduced.");

            return samples;
        }

        private static long CountSamples(List<float> areas, List<float> longestEdges, float spacing)
        {
            long count = 0;
            for (int i = 0; i < areas.Count; i++)
                count += SamplesForTriangle(areas[i], longestEdges[i], spacing);
            return count;
        }

        /// <summary>
        /// One sample per spacing² of area, but at least one per spacing along the longest edge:
        /// a sliver narrower than the spacing would otherwise get a single sample at its centroid
        /// and leave its far ends unsupported. Only triangles thinner than about 2 spacings are affected.
        /// </summary>
        private static int SamplesForTriangle(float area, float longestEdge, float spacing)
        {
            double s = spacing;
            double byArea = System.Math.Round(area / (s * s));
            double byLength = System.Math.Floor(longestEdge / s);
            double k = System.Math.Max(byArea, byLength);
            return k < 1 ? 1 : k > MaxSamples ? MaxSamples : (int)k;
        }

        private static double Frac(double x) => x - System.Math.Floor(x);

        private static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352dU;
            x ^= x >> 15;
            x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }
    }
}
