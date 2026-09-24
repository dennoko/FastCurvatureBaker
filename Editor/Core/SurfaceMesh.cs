using System;
using System.Collections.Generic;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// World-space snapshot of a renderer's mesh, prepared for curvature baking.
    /// </summary>
    internal sealed class SurfaceMesh
    {
        public string Name;
        public Vector3[] Positions;     // world space
        public Vector3[] Normals;       // world space, normalized
        public Vector2[] UVs;
        public uint[] ComponentIds;     // connected-part id per vertex (UV seams welded)
        public int ComponentCount;
        public int[][] SubMeshTriangles; // null entry = non-triangle sub-mesh
        public Bounds Bounds;

        public int VertexCount => Positions.Length;

        /// <summary>
        /// Extracts the mesh of a MeshRenderer or SkinnedMeshRenderer in its current pose.
        /// </summary>
        public static SurfaceMesh FromRenderer(Renderer renderer, int uvChannel)
        {
            if (renderer == null)
                throw new ArgumentNullException(nameof(renderer));

            Mesh source = GetSharedMesh(renderer);
            if (source == null)
                throw new InvalidOperationException($"'{renderer.name}' has no mesh.");
            if (!source.isReadable)
                throw new InvalidOperationException(
                    $"Mesh '{source.name}' is not readable. Enable Read/Write in its import settings.");

            Vector3[] localPositions = source.vertices;
            if (localPositions.Length == 0)
                throw new InvalidOperationException($"Mesh '{source.name}' has no vertices.");

            var uvList = new List<Vector2>();
            source.GetUVs(uvChannel, uvList);
            if (uvList.Count != localPositions.Length)
                throw new InvalidOperationException($"Mesh '{source.name}' has no UV{uvChannel}.");

            var mesh = new SurfaceMesh
            {
                Name = renderer.name,
                UVs = uvList.ToArray(),
                SubMeshTriangles = new int[source.subMeshCount][],
            };
            for (int sub = 0; sub < source.subMeshCount; sub++)
            {
                if (source.GetTopology(sub) == MeshTopology.Triangles)
                    mesh.SubMeshTriangles[sub] = source.GetTriangles(sub);
            }

            ExtractPose(renderer, source, out Vector3[] posedPositions, out Vector3[] posedNormals, out Matrix4x4 toWorld);

            var normalMatrix = toWorld.inverse.transpose;
            mesh.Positions = new Vector3[posedPositions.Length];
            for (int i = 0; i < posedPositions.Length; i++)
                mesh.Positions[i] = toWorld.MultiplyPoint3x4(posedPositions[i]);

            if (posedNormals != null && posedNormals.Length == posedPositions.Length)
            {
                mesh.Normals = new Vector3[posedNormals.Length];
                for (int i = 0; i < posedNormals.Length; i++)
                    mesh.Normals[i] = SafeNormalize(normalMatrix.MultiplyVector(posedNormals[i]));
            }
            else
            {
                mesh.Normals = ComputeAreaWeightedNormals(mesh.Positions, mesh.SubMeshTriangles);
            }

            ComputeComponents(localPositions, mesh);
            mesh.Bounds = ComputeBounds(mesh.Positions);
            return mesh;
        }

        public static Mesh GetSharedMesh(Renderer renderer)
        {
            switch (renderer)
            {
                case SkinnedMeshRenderer smr:
                    return smr.sharedMesh;
                case MeshRenderer mr:
                    var mf = mr.GetComponent<MeshFilter>();
                    return mf != null ? mf.sharedMesh : null;
                default:
                    return null;
            }
        }

        public float TriangleArea(int i0, int i1, int i2)
        {
            return 0.5f * Vector3.Cross(Positions[i1] - Positions[i0], Positions[i2] - Positions[i0]).magnitude;
        }

        private static void ExtractPose(
            Renderer renderer, Mesh source,
            out Vector3[] positions, out Vector3[] normals, out Matrix4x4 toWorld)
        {
            if (renderer is SkinnedMeshRenderer smr)
            {
                // BakeMesh applies bones and blend shapes. With useScale the result includes the
                // renderer's scale, so only position/rotation remain to reach world space.
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked, true);
                    positions = baked.vertices;
                    normals = baked.normals;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(baked);
                }

                if (positions.Length != source.vertexCount)
                    throw new InvalidOperationException($"Failed to bake the current pose of '{renderer.name}'.");

                var t = smr.transform;
                toWorld = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                return;
            }

            positions = source.vertices;
            normals = source.normals;
            toWorld = renderer.transform.localToWorldMatrix;
        }

        /// <summary>
        /// Welds vertices by exact local position (UV seams / hard edges split vertices) and labels
        /// every vertex with the id of the connected part it belongs to.
        /// </summary>
        private static void ComputeComponents(Vector3[] localPositions, SurfaceMesh mesh)
        {
            int vertexCount = localPositions.Length;
            var weldMap = new Dictionary<Vector3, int>(vertexCount);
            var weldId = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                if (!weldMap.TryGetValue(localPositions[i], out int id))
                {
                    id = weldMap.Count;
                    weldMap.Add(localPositions[i], id);
                }
                weldId[i] = id;
            }

            var parent = new int[weldMap.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            foreach (int[] tris in mesh.SubMeshTriangles)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = weldId[tris[t]];
                    Union(parent, a, weldId[tris[t + 1]]);
                    Union(parent, a, weldId[tris[t + 2]]);
                }
            }

            var rootToComponent = new Dictionary<int, uint>();
            mesh.ComponentIds = new uint[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                int root = Find(parent, weldId[i]);
                if (!rootToComponent.TryGetValue(root, out uint component))
                {
                    component = (uint)rootToComponent.Count;
                    rootToComponent.Add(root, component);
                }
                mesh.ComponentIds[i] = component;
            }
            mesh.ComponentCount = rootToComponent.Count;
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a != b) parent[b] = a;
        }

        private static Vector3[] ComputeAreaWeightedNormals(Vector3[] positions, int[][] subMeshTriangles)
        {
            var normals = new Vector3[positions.Length];
            foreach (int[] tris in subMeshTriangles)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    // Unnormalized cross product: length = 2 * area, so accumulation is area weighted.
                    Vector3 n = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
                    normals[i0] += n;
                    normals[i1] += n;
                    normals[i2] += n;
                }
            }
            for (int i = 0; i < normals.Length; i++)
                normals[i] = SafeNormalize(normals[i]);
            return normals;
        }

        private static Bounds ComputeBounds(Vector3[] positions)
        {
            var bounds = new Bounds(positions[0], Vector3.zero);
            for (int i = 1; i < positions.Length; i++)
                bounds.Encapsulate(positions[i]);
            return bounds;
        }

        internal static Vector3 SafeNormalize(Vector3 v)
        {
            float len = v.magnitude;
            return len > 1e-20f ? v / len : Vector3.up;
        }
    }
}
