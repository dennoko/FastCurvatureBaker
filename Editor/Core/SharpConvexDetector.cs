using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    internal sealed class SharpConvexResult
    {
        public List<HardEdge> Edges;
        /// <summary>
        /// Per-submesh, per-triangle bevel core strength in [0, 1]. Non-zero for triangles that lie
        /// strictly inside a detected narrow convex bevel so the bevel face never develops a dark
        /// centerline dip when EdgeWidth is narrower than the bevel. Null entries mean no bevel faces.
        /// </summary>
        public float[][] SubMeshBevelCore;
    }

    /// <summary>
    /// Detects convex sharp corners — both split-normal hard edges, smooth-shaded sharp creases,
    /// and narrow 1-to-N segment bevels whose cumulative convex bend reaches <c>minAngleDeg</c>
    /// within <c>maxBevelWidth</c> — while rejecting smooth curved surfaces and concave creases.
    /// </summary>
    internal static class SharpConvexDetector
    {
        private const int MaxHops = 32;
        private const float CoplanarToleranceRad = 2f * Mathf.Deg2Rad;
        private const float ConcaveStopRad = 5f * Mathf.Deg2Rad;
        private const float SingleEdgeFastAcceptRad = 85f * Mathf.Deg2Rad;

        private struct EdgeHalf
        {
            public int Tri;
            public int LocalEdge;
            public int From;
            public int To;
            public int Opposite;
        }

        private struct EdgeEntry
        {
            public int Count;
            public EdgeHalf First;
            public EdgeHalf Second;
        }

        private struct ManifoldEdge
        {
            public int TriA;
            public int EdgeA;
            public int TriB;
            public int EdgeB;
            public int From;
            public int To;
            public float SignedAngle;
        }

        public static SharpConvexResult Detect(
            SurfaceMesh mesh,
            float minAngleDeg,
            float maxBevelWidth,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            int subCount = mesh.SubMeshTriangles.Length;
            int totalTris = 0;
            for (int sub = 0; sub < subCount; sub++)
            {
                int[] tris = mesh.SubMeshTriangles[sub];
                if (tris != null)
                    totalTris += tris.Length / 3;
            }

            if (totalTris == 0)
            {
                return new SharpConvexResult
                {
                    Edges = new List<HardEdge>(),
                    SubMeshBevelCore = new float[subCount][],
                };
            }

            // Flatten all triangle submeshes so bevel marches cross material seams seamlessly.
            var triV0 = new int[totalTris];
            var triV1 = new int[totalTris];
            var triV2 = new int[totalTris];
            var triSub = new int[totalTris];
            var triLocal = new int[totalTris];
            var triNormal = new Vector3[totalTris];

            int gTri = 0;
            for (int sub = 0; sub < subCount; sub++)
            {
                int[] tris = mesh.SubMeshTriangles[sub];
                if (tris == null) continue;
                int localCount = tris.Length / 3;
                for (int lt = 0; lt < localCount; lt++, gTri++)
                {
                    int i0 = tris[lt * 3];
                    int i1 = tris[lt * 3 + 1];
                    int i2 = tris[lt * 3 + 2];
                    triV0[gTri] = i0;
                    triV1[gTri] = i1;
                    triV2[gTri] = i2;
                    triSub[gTri] = sub;
                    triLocal[gTri] = lt;
                    triNormal[gTri] = mesh.FaceNormal(i0, i1, i2);
                }
            }
            token.ThrowIfCancellationRequested();

            // Build position-welded manifold edge table and per-triangle dual adjacency.
            int[] posId = mesh.PositionIds;
            var edgeMap = new Dictionary<long, EdgeEntry>(totalTris * 3 / 2);
            for (int t = 0; t < totalTris; t++)
            {
                if ((t & 4095) == 0) token.ThrowIfCancellationRequested();
                int v0 = triV0[t], v1 = triV1[t], v2 = triV2[t];
                AddHalfEdge(edgeMap, posId, t, 0, v0, v1, v2);
                AddHalfEdge(edgeMap, posId, t, 1, v1, v2, v0);
                AddHalfEdge(edgeMap, posId, t, 2, v2, v0, v1);
            }

            var adjTri = new int[totalTris * 3];
            var adjEdge = new int[totalTris * 3];
            var adjAngle = new float[totalTris * 3];
            for (int i = 0; i < adjTri.Length; i++)
                adjTri[i] = -1;

            var manifoldEdges = new List<ManifoldEdge>(edgeMap.Count);
            Vector3[] positions = mesh.Positions;
            foreach (EdgeEntry entry in edgeMap.Values)
            {
                if (entry.Count != 2) continue;

                EdgeHalf h0 = entry.First;
                EdgeHalf h1 = entry.Second;
                Vector3 nA = triNormal[h0.Tri];
                Vector3 nB = triNormal[h1.Tri];
                float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(nA, nB), -1f, 1f));

                Vector3 aPos = positions[h0.From];
                float bend = Vector3.Dot(nA, positions[h1.Opposite] - aPos) +
                             Vector3.Dot(nB, positions[h0.Opposite] - aPos);
                float signedAngle = bend < 0f ? angle : (bend > 0f ? -angle : 0f);

                int idx0 = h0.Tri * 3 + h0.LocalEdge;
                int idx1 = h1.Tri * 3 + h1.LocalEdge;
                adjTri[idx0] = h1.Tri;
                adjEdge[idx0] = h1.LocalEdge;
                adjAngle[idx0] = signedAngle;

                adjTri[idx1] = h0.Tri;
                adjEdge[idx1] = h0.LocalEdge;
                adjAngle[idx1] = signedAngle;

                if (signedAngle > CoplanarToleranceRad)
                {
                    manifoldEdges.Add(new ManifoldEdge
                    {
                        TriA = h0.Tri,
                        EdgeA = h0.LocalEdge,
                        TriB = h1.Tri,
                        EdgeB = h1.LocalEdge,
                        From = h0.From,
                        To = h0.To,
                        SignedAngle = signedAngle,
                    });
                }
            }
            token.ThrowIfCancellationRequested();

            float minAngleRad = minAngleDeg * Mathf.Deg2Rad;
            float fullAngleDeg = minAngleDeg < 75f
                ? minAngleDeg + 15f
                : Mathf.Min(minAngleDeg + 10f, 175f);
            float fullAngleRad = fullAngleDeg * Mathf.Deg2Rad;
            float invRamp = 1f / Mathf.Max(fullAngleRad - minAngleRad, 1e-4f);

            var detectedEdges = new List<HardEdge>();
            float[] globalBevelCore = maxBevelWidth > 1e-6f ? new float[totalTris] : null;
            bool anyBevelCore = false;

            // Preallocated scratch buffers for zero-GC cross-section marches.
            var distA = new float[MaxHops + 1];
            var angA = new float[MaxHops + 1];
            var crossTriA = new int[MaxHops];
            var crossDistA = new float[MaxHops];

            var distB = new float[MaxHops + 1];
            var angB = new float[MaxHops + 1];
            var crossTriB = new int[MaxHops];
            var crossDistB = new float[MaxHops];

            var visited = new int[MaxHops + 2];

            for (int i = 0; i < manifoldEdges.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();

                ManifoldEdge me = manifoldEdges[i];
                float bestAngle = me.SignedAngle;
                float bestDistA = 0f;
                float bestDistB = 0f;
                int countCrossA = 0;
                int countCrossB = 0;
                bool marched = false;

                if (maxBevelWidth > 1e-6f && me.SignedAngle < SingleEdgeFastAcceptRad)
                {
                    Vector3 pA = positions[me.From];
                    Vector3 pB = positions[me.To];
                    Vector3 edgeVec = pB - pA;
                    float edgeLen = edgeVec.magnitude;
                    if (edgeLen > 1e-12f)
                    {
                        marched = true;
                        Vector3 edgeU = edgeVec / edgeLen;
                        Vector3 mid = (pA + pB) * 0.5f;

                        int bendsA = MarchSide(
                            me.TriA, me.EdgeA, mid, edgeU, maxBevelWidth,
                            positions, triV0, triV1, triV2, triNormal, adjTri, adjEdge, adjAngle,
                            distA, angA, crossTriA, crossDistA, visited, out countCrossA);

                        int bendsB = MarchSide(
                            me.TriB, me.EdgeB, mid, edgeU, maxBevelWidth,
                            positions, triV0, triV1, triV2, triNormal, adjTri, adjEdge, adjAngle,
                            distB, angB, crossTriB, crossDistB, visited, out countCrossB);

                        // Sliding window: maximize cumulative convex turning angle over any span <= maxBevelWidth.
                        float limit = maxBevelWidth + 1e-6f;
                        for (int a = 0; a < bendsA; a++)
                        {
                            float dA = distA[a];
                            float aA = angA[a];
                            for (int b = 0; b < bendsB; b++)
                            {
                                if (dA + distB[b] > limit)
                                    break; // distB is non-decreasing
                                float total = me.SignedAngle + aA + angB[b];
                                if (total > bestAngle)
                                {
                                    bestAngle = total;
                                    bestDistA = dA;
                                    bestDistB = distB[b];
                                }
                            }
                        }
                    }
                }

                if (bestAngle <= minAngleRad)
                    continue;

                float tRamp = Mathf.Clamp01((bestAngle - minAngleRad) * invRamp);
                float strength = tRamp * tRamp * (3f - 2f * tRamp);
                if (strength < 0.01f)
                    continue;

                detectedEdges.Add(new HardEdge
                {
                    A = positions[me.From],
                    B = positions[me.To],
                    FaceNormalA = triNormal[me.TriA],
                    FaceNormalB = triNormal[me.TriB],
                    Value = strength,
                    PatchA = mesh.PatchIds[triV0[me.TriA]],
                    PatchB = mesh.PatchIds[triV0[me.TriB]],
                });

                // Mark triangles strictly between the outermost detected bevel edges so bevel strips
                // stay solid without a dark dip when EdgeWidth is narrower than the bevel.
                if (marched && globalBevelCore != null)
                {
                    if (bestDistA > 0f)
                    {
                        float limA = bestDistA + 1e-6f;
                        for (int c = 0; c < countCrossA; c++)
                        {
                            if (crossDistA[c] <= limA && strength > globalBevelCore[crossTriA[c]])
                            {
                                globalBevelCore[crossTriA[c]] = strength;
                                anyBevelCore = true;
                            }
                        }
                    }
                    if (bestDistB > 0f)
                    {
                        float limB = bestDistB + 1e-6f;
                        for (int c = 0; c < countCrossB; c++)
                        {
                            if (crossDistB[c] <= limB && strength > globalBevelCore[crossTriB[c]])
                            {
                                globalBevelCore[crossTriB[c]] = strength;
                                anyBevelCore = true;
                            }
                        }
                    }
                }
            }

            // Scatter global bevel core strengths back to per-submesh arrays.
            var subBevelCore = new float[subCount][];
            if (anyBevelCore)
            {
                for (int t = 0; t < totalTris; t++)
                {
                    float v = globalBevelCore[t];
                    if (v <= 0f) continue;
                    int sub = triSub[t];
                    if (subBevelCore[sub] == null)
                        subBevelCore[sub] = new float[mesh.SubMeshTriangles[sub].Length / 3];
                    subBevelCore[sub][triLocal[t]] = v;
                }
            }

            return new SharpConvexResult
            {
                Edges = detectedEdges,
                SubMeshBevelCore = subBevelCore,
            };
        }

        private static void AddHalfEdge(
            Dictionary<long, EdgeEntry> edges, int[] posId,
            int tri, int localEdge, int from, int to, int opposite)
        {
            int pa = posId[from];
            int pb = posId[to];
            if (pa == pb) return;

            long key = pa < pb ? ((long)pa << 32) | (uint)pb : ((long)pb << 32) | (uint)pa;
            edges.TryGetValue(key, out EdgeEntry entry);
            var half = new EdgeHalf
            {
                Tri = tri,
                LocalEdge = localEdge,
                From = from,
                To = to,
                Opposite = opposite,
            };
            if (entry.Count == 0) entry.First = half;
            else if (entry.Count == 1) entry.Second = half;
            entry.Count++;
            edges[key] = entry;
        }

        /// <summary>
        /// Walks across the surface perpendicular to <paramref name="edgeU"/> up to <paramref name="maxDist"/>,
        /// crossing coplanar edges (e.g. quad diagonals) freely and recording every convex turn.
        /// Stops at concave creases, open borders, or when the distance exceeds <paramref name="maxDist"/>.
        /// </summary>
        private static int MarchSide(
            int startTri, int entryEdge, Vector3 startPos, Vector3 edgeU, float maxDist,
            Vector3[] positions, int[] triV0, int[] triV1, int[] triV2, Vector3[] triNormal,
            int[] adjTri, int[] adjEdge, float[] adjAngle,
            float[] outDist, float[] outCumAngle,
            int[] outCrossTri, float[] outCrossDist,
            int[] visited, out int crossCount)
        {
            outDist[0] = 0f;
            outCumAngle[0] = 0f;
            int bendCount = 1;
            crossCount = 0;

            int t = startTri;
            int inEdge = entryEdge;
            Vector3 p = startPos;
            float dist = 0f;
            float cumAngle = 0f;

            int visitedCount = 1;
            visited[0] = t;

            for (int hop = 0; hop < MaxHops; hop++)
            {
                Vector3 nT = triNormal[t];
                int v0 = triV0[t], v1 = triV1[t], v2 = triV2[t];

                GetEdgeVertices(v0, v1, v2, inEdge, out int ea, out int eb, out int opp);
                Vector3 posEa = positions[ea];
                Vector3 inward = Vector3.Cross(nT, positions[eb] - posEa);
                if (Vector3.Dot(inward, positions[opp] - posEa) < 0f)
                    inward = -inward;
                float inwardLen = inward.magnitude;
                if (inwardLen < 1e-12f)
                    break;

                Vector3 d = Vector3.Cross(nT, edgeU);
                float dLen = d.magnitude;
                if (dLen < 1e-4f)
                    break;
                d /= dLen;

                float dotInward = Vector3.Dot(d, inward);
                if (dotInward < 0f)
                {
                    d = -d;
                    dotInward = -dotInward;
                }
                if (dotInward < 1e-4f * inwardLen)
                    break;

                Vector3 eY = Vector3.Cross(nT, d);
                float bestX = -1f;
                int exitEdge = -1;
                Vector3 exitPos = Vector3.zero;

                for (int e = 0; e < 3; e++)
                {
                    if (e == inEdge) continue;
                    GetEdgeVertices(v0, v1, v2, e, out int ca, out int cb, out _);
                    Vector3 posA = positions[ca];
                    Vector3 posB = positions[cb];
                    float ya = Vector3.Dot(posA - p, eY);
                    float yb = Vector3.Dot(posB - p, eY);
                    float denom = ya - yb;
                    if (Mathf.Abs(denom) < 1e-12f)
                        continue;

                    float s = ya / denom;
                    if (s < -1e-4f || s > 1f + 1e-4f)
                        continue;
                    s = Mathf.Clamp01(s);

                    Vector3 cand = posA + s * (posB - posA);
                    float x = Vector3.Dot(cand - p, d);
                    if (x >= -1e-7f && x > bestX)
                    {
                        bestX = x > 0f ? x : 0f;
                        exitEdge = e;
                        exitPos = cand;
                    }
                }

                if (exitEdge < 0)
                    break;
                if (dist + bestX > maxDist)
                    break;

                int flatIdx = t * 3 + exitEdge;
                int nextT = adjTri[flatIdx];
                if (nextT < 0)
                    break;

                bool seen = false;
                for (int v = 0; v < visitedCount; v++)
                {
                    if (visited[v] == nextT) { seen = true; break; }
                }
                if (seen)
                    break;

                float sAngle = adjAngle[flatIdx];
                if (sAngle < -ConcaveStopRad)
                    break;

                dist += bestX;
                outCrossTri[crossCount] = t;
                outCrossDist[crossCount] = dist;
                crossCount++;

                if (sAngle > CoplanarToleranceRad)
                {
                    cumAngle += sAngle;
                    outDist[bendCount] = dist;
                    outCumAngle[bendCount] = cumAngle;
                    bendCount++;
                }

                visited[visitedCount++] = nextT;
                t = nextT;
                inEdge = adjEdge[flatIdx];
                p = exitPos;
            }

            return bendCount;
        }

        private static void GetEdgeVertices(int v0, int v1, int v2, int edge, out int from, out int to, out int opposite)
        {
            if (edge == 0) { from = v0; to = v1; opposite = v2; }
            else if (edge == 1) { from = v1; to = v2; opposite = v0; }
            else { from = v2; to = v0; opposite = v1; }
        }
    }
}
