using System;
using System.Collections.Generic;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// An edge where the shading normals are split (hard edge), in world space.
    /// </summary>
    internal struct HardEdge
    {
        public Vector3 A;
        public Vector3 B;
        public Vector3 FaceNormalA;  // normals of the two adjacent faces
        public Vector3 FaceNormalB;
        public float Value;          // +1 = 90-degree convex, -1 = 90-degree concave (saturated beyond)
        public uint Component;
    }

    /// <summary>
    /// World-space snapshot of a renderer's mesh, prepared for curvature baking.
    /// </summary>
    internal sealed class SurfaceMesh
    {
        public string Name;
        public Vector3[] Positions;     // world space
        public Vector3[] Normals;       // world space, normalized
        public Vector2[] UVs;
        public uint[] ComponentIds;     // connected-part id per vertex (welded by position: UV seams and hard edges joined)
        public int ComponentCount;
        public uint[] PatchIds;         // smooth-patch id per vertex (welded by position + normal: bounded by hard edges)
        public uint[] PatchComponents;  // component id of each patch
        public List<HardEdge> HardEdges;
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

            ComputeTopology(localPositions, source.normals, mesh);
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
        /// Derives the mesh topology used by the bake:
        /// <list type="bullet">
        /// <item>Components: vertices welded by exact local position, so UV seams and hard edges are joined.</item>
        /// <item>Patches: vertices welded by position and normal, so only hard edges separate them.</item>
        /// <item>Hard edges: manifold edges whose two faces do not share normals at an endpoint.</item>
        /// </list>
        /// </summary>
        private static void ComputeTopology(Vector3[] localPositions, Vector3[] localNormals, SurfaceMesh mesh)
        {
            int vertexCount = localPositions.Length;
            bool hasNormals = localNormals != null && localNormals.Length == vertexCount;

            // Weld by position, then split each position into clusters of matching normals.
            var positionMap = new Dictionary<Vector3, int>(vertexCount);
            var positionId = new int[vertexCount];
            var smoothId = new int[vertexCount];
            var clusterHead = new List<int>();    // per position: first smooth id at that position
            var nextInPosition = new List<int>(); // per smooth id: next smooth id at the same position
            var smoothRep = new List<int>();      // per smooth id: representative vertex
            for (int i = 0; i < vertexCount; i++)
            {
                if (!positionMap.TryGetValue(localPositions[i], out int pid))
                {
                    pid = positionMap.Count;
                    positionMap.Add(localPositions[i], pid);
                    clusterHead.Add(-1);
                }
                positionId[i] = pid;

                int found = -1;
                for (int sid = clusterHead[pid]; sid >= 0; sid = nextInPosition[sid])
                {
                    if (!hasNormals || SameNormal(localNormals[i], localNormals[smoothRep[sid]]))
                    {
                        found = sid;
                        break;
                    }
                }
                if (found < 0)
                {
                    found = smoothRep.Count;
                    smoothRep.Add(i);
                    nextInPosition.Add(clusterHead[pid]);
                    clusterHead[pid] = found;
                }
                smoothId[i] = found;
            }

            var componentParent = CreateSets(positionMap.Count);
            var patchParent = CreateSets(smoothRep.Count);
            foreach (int[] tris in mesh.SubMeshTriangles)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    Union(componentParent, positionId[a], positionId[b]);
                    Union(componentParent, positionId[a], positionId[c]);
                    Union(patchParent, smoothId[a], smoothId[b]);
                    Union(patchParent, smoothId[a], smoothId[c]);
                }
            }

            mesh.ComponentIds = LabelVertices(componentParent, positionId, out mesh.ComponentCount);
            mesh.PatchIds = LabelVertices(patchParent, smoothId, out int patchCount);
            mesh.PatchComponents = new uint[patchCount];
            for (int i = 0; i < vertexCount; i++)
                mesh.PatchComponents[mesh.PatchIds[i]] = mesh.ComponentIds[i];

            mesh.HardEdges = FindHardEdges(mesh, positionId, smoothId);
        }

        private struct EdgeSide
        {
            public int From, To, Opposite; // vertex indices in the triangle's winding order
        }

        private struct EdgeRecord
        {
            public int Count;
            public EdgeSide First, Second;
        }

        private static List<HardEdge> FindHardEdges(SurfaceMesh mesh, int[] positionId, int[] smoothId)
        {
            var edges = new Dictionary<long, EdgeRecord>();
            foreach (int[] tris in mesh.SubMeshTriangles)
            {
                if (tris == null) continue;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        var side = new EdgeSide
                        {
                            From = tris[t + e],
                            To = tris[t + (e + 1) % 3],
                            Opposite = tris[t + (e + 2) % 3],
                        };
                        int pa = positionId[side.From], pb = positionId[side.To];
                        if (pa == pb) continue;

                        long key = pa < pb ? ((long)pa << 32) | (uint)pb : ((long)pb << 32) | (uint)pa;
                        edges.TryGetValue(key, out EdgeRecord record);
                        if (record.Count == 0) record.First = side;
                        else if (record.Count == 1) record.Second = side;
                        record.Count++;
                        edges[key] = record;
                    }
                }
            }

            var result = new List<HardEdge>();
            foreach (EdgeRecord record in edges.Values)
            {
                if (record.Count != 2) continue; // open border or non-manifold

                EdgeSide s0 = record.First, s1 = record.Second;
                // Pair the endpoints of both sides by position.
                int s1AtFrom = positionId[s1.From] == positionId[s0.From] ? s1.From : s1.To;
                int s1AtTo = s1AtFrom == s1.From ? s1.To : s1.From;
                if (smoothId[s0.From] == smoothId[s1AtFrom] && smoothId[s0.To] == smoothId[s1AtTo])
                    continue; // normals shared at both endpoints: smooth edge

                Vector3 nA = FaceNormal(mesh, s0);
                Vector3 nB = FaceNormal(mesh, s1);
                float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(nA, nB), -1f, 1f));
                float strength = Mathf.Min(angle / (0.5f * Mathf.PI), 1f);
                if (strength < 0.01f) continue;

                // Convex when each face's far vertex lies below the other face's plane.
                Vector3 a = mesh.Positions[s0.From];
                float bend = Vector3.Dot(nA, mesh.Positions[s1.Opposite] - a) +
                             Vector3.Dot(nB, mesh.Positions[s0.Opposite] - a);
                if (bend == 0f) continue;

                result.Add(new HardEdge
                {
                    A = a,
                    B = mesh.Positions[s0.To],
                    FaceNormalA = nA,
                    FaceNormalB = nB,
                    Value = bend < 0f ? strength : -strength,
                    Component = mesh.ComponentIds[s0.From],
                });
            }
            return result;
        }

        /// <summary>
        /// Geometric normal of the triangle, oriented to agree with its shading normals
        /// (robust against mirrored transforms and flipped winding).
        /// </summary>
        private static Vector3 FaceNormal(SurfaceMesh mesh, EdgeSide side) =>
            mesh.FaceNormal(side.From, side.To, side.Opposite);

        /// <summary>
        /// Geometric normal of the triangle, oriented to agree with its shading normals
        /// (robust against mirrored transforms and flipped winding). Matches the shader's FaceNormal.
        /// </summary>
        public Vector3 FaceNormal(int i0, int i1, int i2)
        {
            Vector3 p0 = Positions[i0];
            Vector3 n = SafeNormalize(Vector3.Cross(Positions[i1] - p0, Positions[i2] - p0));
            Vector3 shading = Normals[i0] + Normals[i1] + Normals[i2];
            return Vector3.Dot(n, shading) < 0f ? -n : n;
        }

        private static bool SameNormal(Vector3 a, Vector3 b)
        {
            return Vector3.Dot(SafeNormalize(a), SafeNormalize(b)) > 0.9999f;
        }

        internal static int[] CreateSets(int count)
        {
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            return parent;
        }

        /// <summary>Maps each vertex's set to a compact id in [0, count).</summary>
        private static uint[] LabelVertices(int[] parent, int[] vertexSet, out int count)
        {
            var rootToLabel = new Dictionary<int, uint>();
            var labels = new uint[vertexSet.Length];
            for (int i = 0; i < vertexSet.Length; i++)
            {
                int root = Find(parent, vertexSet[i]);
                if (!rootToLabel.TryGetValue(root, out uint label))
                {
                    label = (uint)rootToLabel.Count;
                    rootToLabel.Add(root, label);
                }
                labels[i] = label;
            }
            count = rootToLabel.Count;
            return labels;
        }

        internal static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        internal static void Union(int[] parent, int a, int b)
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
