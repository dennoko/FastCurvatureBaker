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
        public uint Component;
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
        public static SurfaceSample[] Generate(SurfaceMesh mesh, ref float spacing)
        {
            int triangleCount = 0;
            foreach (int[] tris in mesh.SubMeshTriangles)
            {
                if (tris != null) triangleCount += tris.Length / 3;
            }

            // Every triangle keeps at least one sample, so the budget is spent on the remainder.
            int budget = Mathf.Max(MaxSamples, triangleCount);
            long count = CountSamples(mesh, spacing);
            for (int attempt = 0; attempt < 8 && count > budget; attempt++)
            {
                spacing *= Mathf.Sqrt((float)count / budget) * 1.02f;
                count = CountSamples(mesh, spacing);
            }
            if (count > budget)
            {
                // Pathological input: fall back to one sample per triangle.
                spacing = float.MaxValue;
                count = triangleCount;
            }

            var samples = new SurfaceSample[count];
            float invCellArea = spacing < float.MaxValue ? 1f / (spacing * spacing) : 0f;
            int written = 0;
            foreach (int[] tris in mesh.SubMeshTriangles)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    float area = mesh.TriangleArea(i0, i1, i2);
                    int k = SamplesForTriangle(area, invCellArea);
                    float weight = area / k;
                    uint component = mesh.ComponentIds[i0];

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
                            Normal = SurfaceMesh.SafeNormalize(
                                w0 * mesh.Normals[i0] + u * mesh.Normals[i1] + v * mesh.Normals[i2]),
                            Component = component,
                            Weight = weight,
                        };
                    }
                }
            }

            if (spacing == float.MaxValue)
                Debug.LogWarning($"[FastCurvatureBaker] '{mesh.Name}': too many triangles for the sample budget; quality is reduced.");

            return samples;
        }

        private static long CountSamples(SurfaceMesh mesh, float spacing)
        {
            float invCellArea = 1f / (spacing * spacing);
            long count = 0;
            foreach (int[] tris in mesh.SubMeshTriangles)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                    count += SamplesForTriangle(mesh.TriangleArea(tris[t], tris[t + 1], tris[t + 2]), invCellArea);
            }
            return count;
        }

        private static int SamplesForTriangle(float area, float invCellArea)
        {
            double k = System.Math.Round(area * (double)invCellArea);
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
