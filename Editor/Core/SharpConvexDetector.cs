using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// Detects convex sharp corners — split-normal hard edges, smooth-shaded sharp creases, and
    /// narrow 1-to-N segment bevels whose cumulative convex bend reaches <c>minAngleDeg</c> within
    /// <c>maxBevelWidth</c> — while rejecting smooth curved surfaces, concave creases, and shallow
    /// triangulation/holding edges adjacent to real corners.
    /// </summary>
    internal static class SharpConvexDetector
    {
        private const int MaxHops = 32;
        private const float CoplanarToleranceRad = 2f * Mathf.Deg2Rad;
        private const float ConcaveStopRad = 5f * Mathf.Deg2Rad;
        private const float ParallelMinDot = 0.75f;        // ~41 deg max misalignment between bevel edges
        private const float CollinearMinDot = 0.70f;       // ~45 deg max turn along a continuous edge loop
        private const float TailMinAngleRad = 8f * Mathf.Deg2Rad;
        private const float MaxPiggybackRatio = 4.0f;
        private const float WidthSoftFadeRatio = 1.5f;     // soft falloff from 1.0x to 1.5x MaxBevelWidth

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
            public int PosFrom;
            public int PosTo;
            public Vector3 Dir;
            public float SignedAngle;
        }

        public static List<HardEdge> Detect(
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
                return new List<HardEdge>();

            // Flatten all triangle submeshes so bevel marches cross material seams seamlessly.
            var triV0 = new int[totalTris];
            var triV1 = new int[totalTris];
            var triV2 = new int[totalTris];
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
                    triNormal[gTri] = mesh.FaceNormal(i0, i1, i2);
                }
            }
            token.ThrowIfCancellationRequested();

            // Build position-welded manifold edge table and per-triangle dual adjacency.
            int[] posId = mesh.PositionIds;
            int maxPosId = 0;
            for (int i = 0; i < posId.Length; i++)
            {
                if (posId[i] > maxPosId) maxPosId = posId[i];
            }

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
            var adjDir = new Vector3[totalTris * 3];
            for (int i = 0; i < adjTri.Length; i++)
                adjTri[i] = -1;

            var convexEdges = new List<ManifoldEdge>(edgeMap.Count / 2);
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
                Vector3 bPos = positions[h0.To];
                Vector3 edgeVec = bPos - aPos;
                float edgeLen = edgeVec.magnitude;
                if (edgeLen <= 1e-12f) continue;
                Vector3 dir = edgeVec / edgeLen;

                float bend = Vector3.Dot(nA, positions[h1.Opposite] - aPos) +
                             Vector3.Dot(nB, positions[h0.Opposite] - aPos);
                float signedAngle = bend < 0f ? angle : (bend > 0f ? -angle : 0f);

                int idx0 = h0.Tri * 3 + h0.LocalEdge;
                int idx1 = h1.Tri * 3 + h1.LocalEdge;
                adjTri[idx0] = h1.Tri;
                adjEdge[idx0] = h1.LocalEdge;
                adjAngle[idx0] = signedAngle;
                adjDir[idx0] = dir;

                adjTri[idx1] = h0.Tri;
                adjEdge[idx1] = h0.LocalEdge;
                adjAngle[idx1] = signedAngle;
                adjDir[idx1] = dir;

                if (signedAngle > CoplanarToleranceRad)
                {
                    convexEdges.Add(new ManifoldEdge
                    {
                        TriA = h0.Tri,
                        EdgeA = h0.LocalEdge,
                        TriB = h1.Tri,
                        EdgeB = h1.LocalEdge,
                        From = h0.From,
                        To = h0.To,
                        PosFrom = posId[h0.From],
                        PosTo = posId[h0.To],
                        Dir = dir,
                        SignedAngle = signedAngle,
                    });
                }
            }
            token.ThrowIfCancellationRequested();

            int edgeCount = convexEdges.Count;
            if (edgeCount == 0)
                return new List<HardEdge>();

            float minAngleRad = minAngleDeg * Mathf.Deg2Rad;
            float fullAngleDeg = minAngleDeg < 75f
                ? minAngleDeg + 15f
                : Mathf.Min(minAngleDeg + 10f, 175f);
            float fullAngleRad = fullAngleDeg * Mathf.Deg2Rad;
            float marchLimit = maxBevelWidth * WidthSoftFadeRatio;
            float minLocalCurv = maxBevelWidth > 1e-6f ? 0.5f * (minAngleRad / maxBevelWidth) : 0f;

            var rawStrength = new float[edgeCount];
            var flankNormA = new Vector3[edgeCount];
            var flankNormB = new Vector3[edgeCount];

            // Preallocated scratch buffers for zero-GC cross-section marches.
            var distA = new float[MaxHops + 1];
            var angA = new float[MaxHops + 1];
            var normA = new Vector3[MaxHops + 1];

            var distB = new float[MaxHops + 1];
            var angB = new float[MaxHops + 1];
            var normB = new Vector3[MaxHops + 1];

            var visited = new int[MaxHops + 2];

            for (int i = 0; i < edgeCount; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();

                ManifoldEdge me = convexEdges[i];
                Vector3 baseNA = triNormal[me.TriA];
                Vector3 baseNB = triNormal[me.TriB];
                flankNormA[i] = baseNA;
                flankNormB[i] = baseNB;

                float baseStrength = Smoothstep(minAngleRad, fullAngleRad, me.SignedAngle);
                if (maxBevelWidth <= 1e-6f || baseStrength >= 0.999f)
                {
                    rawStrength[i] = baseStrength;
                    continue;
                }

                // Sample 3 points along the edge (25%, 50%, 75%) and take the median:
                // - rescues midpoint vertex-grazing / skewed triangulation
                // - rejects single-tip proximity false positives
                // - smoothly transitions along variable-width tapered bevels
                Vector3 pA = positions[me.From];
                Vector3 pB = positions[me.To];

                float s25 = EvalAtPoint(
                    pA * 0.75f + pB * 0.25f, me, baseStrength,
                    minAngleRad, fullAngleRad, maxBevelWidth, marchLimit, minLocalCurv,
                    positions, triV0, triV1, triV2, triNormal, adjTri, adjEdge, adjAngle, adjDir,
                    distA, angA, normA, distB, angB, normB, visited,
                    out Vector3 nA25, out Vector3 nB25);

                float s50 = EvalAtPoint(
                    (pA + pB) * 0.5f, me, baseStrength,
                    minAngleRad, fullAngleRad, maxBevelWidth, marchLimit, minLocalCurv,
                    positions, triV0, triV1, triV2, triNormal, adjTri, adjEdge, adjAngle, adjDir,
                    distA, angA, normA, distB, angB, normB, visited,
                    out Vector3 nA50, out Vector3 nB50);

                float s75 = EvalAtPoint(
                    pA * 0.25f + pB * 0.75f, me, baseStrength,
                    minAngleRad, fullAngleRad, maxBevelWidth, marchLimit, minLocalCurv,
                    positions, triV0, triV1, triV2, triNormal, adjTri, adjEdge, adjAngle, adjDir,
                    distA, angA, normA, distB, angB, normB, visited,
                    out Vector3 nA75, out Vector3 nB75);

                // Select median strength and its corresponding flank normals.
                if ((s50 >= s25 && s50 <= s75) || (s50 >= s75 && s50 <= s25))
                {
                    rawStrength[i] = s50;
                    flankNormA[i] = nA50;
                    flankNormB[i] = nB50;
                }
                else if ((s25 >= s50 && s25 <= s75) || (s25 >= s75 && s25 <= s50))
                {
                    rawStrength[i] = s25;
                    flankNormA[i] = nA25;
                    flankNormB[i] = nB25;
                }
                else
                {
                    rawStrength[i] = s75;
                    flankNormA[i] = nA75;
                    flankNormB[i] = nB75;
                }
            }
            token.ThrowIfCancellationRequested();

            // Build compact vertex -> convex edge linked list for O(E) collinear endpoint blending.
            var head = new int[maxPosId + 1];
            for (int i = 0; i < head.Length; i++) head[i] = -1;
            var linkEdge = new int[edgeCount * 2];
            var linkNext = new int[edgeCount * 2];
            int linkCursor = 0;
            for (int i = 0; i < edgeCount; i++)
            {
                int p0 = convexEdges[i].PosFrom;
                linkEdge[linkCursor] = i;
                linkNext[linkCursor] = head[p0];
                head[p0] = linkCursor++;

                int p1 = convexEdges[i].PosTo;
                linkEdge[linkCursor] = i;
                linkNext[linkCursor] = head[p1];
                head[p1] = linkCursor++;
            }

            // Blend endpoint intensities across collinear continuing edges so tapered bevels fade
            // smoothly to 0 without abrupt semicircular end-caps (blobs), while sharp corners stay 1.0.
            var detectedEdges = new List<HardEdge>();
            for (int i = 0; i < edgeCount; i++)
            {
                ManifoldEdge me = convexEdges[i];
                float sSelf = rawStrength[i];
                float valA = ComputeEndpointStrength(i, me.PosFrom, -me.Dir, sSelf, me.SignedAngle, convexEdges, rawStrength, head, linkEdge, linkNext);
                float valB = ComputeEndpointStrength(i, me.PosTo, me.Dir, sSelf, me.SignedAngle, convexEdges, rawStrength, head, linkEdge, linkNext);

                if (valA < 0.01f && valB < 0.01f)
                    continue;

                detectedEdges.Add(new HardEdge
                {
                    A = positions[me.From],
                    B = positions[me.To],
                    FaceNormalA = flankNormA[i],
                    FaceNormalB = flankNormB[i],
                    ValueA = valA,
                    ValueB = valB,
                    PatchA = mesh.PatchIds[triV0[me.TriA]],
                    PatchB = mesh.PatchIds[triV0[me.TriB]],
                });
            }

            return detectedEdges;
        }

        private static float ComputeEndpointStrength(
            int selfIdx, int vertexPosId, Vector3 outwardDir, float selfStrength, float selfAngle,
            List<ManifoldEdge> edges, float[] rawStrength,
            int[] head, int[] linkEdge, int[] linkNext)
        {
            int bestOther = -1;
            float bestDot = CollinearMinDot;
            for (int node = head[vertexPosId]; node >= 0; node = linkNext[node])
            {
                int other = linkEdge[node];
                if (other == selfIdx) continue;
                ManifoldEdge oe = edges[other];
                // Outgoing direction of `other` leaving `vertexPosId`.
                Vector3 otherOut = oe.PosFrom == vertexPosId ? oe.Dir : -oe.Dir;
                float d = Vector3.Dot(otherOut, outwardDir);
                if (d > bestDot)
                {
                    bestDot = d;
                    bestOther = other;
                }
            }

            if (bestOther < 0)
                return selfStrength;

            float otherStrength = rawStrength[bestOther];
            // Do not bleed a fade-out tail onto a tiny flat-wall continuation crease.
            if (selfStrength <= 0f && otherStrength > 0f)
            {
                float otherAngle = edges[bestOther].SignedAngle;
                if (selfAngle < TailMinAngleRad || selfAngle < 0.35f * otherAngle)
                    return 0f;
            }
            if (otherStrength <= 0f && selfStrength > 0f)
            {
                float otherAngle = edges[bestOther].SignedAngle;
                if (otherAngle < TailMinAngleRad || otherAngle < 0.35f * selfAngle)
                    return selfStrength;
            }

            return 0.5f * (selfStrength + otherStrength);
        }

        private static float EvalAtPoint(
            Vector3 p, ManifoldEdge me, float baseStrength,
            float minAngleRad, float fullAngleRad, float maxBevelWidth, float marchLimit, float minLocalCurv,
            Vector3[] positions, int[] triV0, int[] triV1, int[] triV2, Vector3[] triNormal,
            int[] adjTri, int[] adjEdge, float[] adjAngle, Vector3[] adjDir,
            float[] distA, float[] angA, Vector3[] normA,
            float[] distB, float[] angB, Vector3[] normB,
            int[] visited,
            out Vector3 bestNormA, out Vector3 bestNormB)
        {
            bestNormA = triNormal[me.TriA];
            bestNormB = triNormal[me.TriB];

            int bendsA = MarchSide(
                me.TriA, me.EdgeA, p, me.Dir, me.SignedAngle, minAngleRad, marchLimit,
                positions, triV0, triV1, triV2, triNormal, adjTri, adjEdge, adjAngle, adjDir,
                distA, angA, normA, visited, out float firstStepA);

            int bendsB = MarchSide(
                me.TriB, me.EdgeB, p, me.Dir, me.SignedAngle, minAngleRad, marchLimit,
                positions, triV0, triV1, triV2, triNormal, adjTri, adjEdge, adjAngle, adjDir,
                distB, angB, normB, visited, out float firstStepB);

            // Local curvature guard: reject weak creases/holding edges whose own bend-per-width
            // is far below the target bevel curvature (prevents piggybacking on nearby real corners).
            float localWidth;
            if (firstStepA > 0f && firstStepB > 0f)
                localWidth = 0.5f * (firstStepA + firstStepB);
            else if (firstStepA > 0f)
                localWidth = 0.5f * firstStepA;
            else if (firstStepB > 0f)
                localWidth = 0.5f * firstStepB;
            else
                return baseStrength;

            if (me.SignedAngle / Mathf.Max(localWidth, 1e-6f) < minLocalCurv)
                return baseStrength;

            float bestStrength = baseStrength;
            float limit = marchLimit + 1e-6f;
            for (int a = 0; a < bendsA; a++)
            {
                float dA = distA[a];
                float aA = angA[a];
                for (int b = 0; b < bendsB; b++)
                {
                    float span = dA + distB[b];
                    if (span > limit)
                        break; // distB is non-decreasing

                    float total = me.SignedAngle + aA + angB[b];
                    if (total <= minAngleRad)
                        continue;

                    float angFactor = Smoothstep(minAngleRad, fullAngleRad, total);
                    float widthFactor = span <= maxBevelWidth
                        ? 1f
                        : 1f - Smoothstep(maxBevelWidth, marchLimit, span);
                    float cand = angFactor * widthFactor;
                    if (cand > bestStrength)
                    {
                        bestStrength = cand;
                        bestNormA = normA[a];
                        bestNormB = normB[b];
                    }
                }
            }

            return bestStrength;
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
        /// crossing coplanar edges (e.g. quad diagonals) freely and recording parallel convex turns.
        /// Stops at concave creases, non-parallel convex folds, much sharper standalone corners, or open borders.
        /// </summary>
        private static int MarchSide(
            int startTri, int entryEdge, Vector3 startPos, Vector3 edgeU,
            float selfAngle, float minAngleRad, float maxDist,
            Vector3[] positions, int[] triV0, int[] triV1, int[] triV2, Vector3[] triNormal,
            int[] adjTri, int[] adjEdge, float[] adjAngle, Vector3[] adjDir,
            float[] outDist, float[] outCumAngle, Vector3[] outNorm,
            int[] visited, out float firstBendDist)
        {
            outDist[0] = 0f;
            outCumAngle[0] = 0f;
            outNorm[0] = triNormal[startTri];
            int bendCount = 1;
            firstBendDist = 0f;

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

                if (sAngle > CoplanarToleranceRad)
                {
                    // Crossed convex edge must belong to the same roughly parallel bevel strip.
                    if (Mathf.Abs(Vector3.Dot(edgeU, adjDir[flatIdx])) < ParallelMinDot)
                        break;
                    // Do not let a weak crease piggyback on a much sharper standalone corner.
                    if (sAngle >= minAngleRad && sAngle > MaxPiggybackRatio * selfAngle)
                        break;

                    if (firstBendDist <= 0f)
                        firstBendDist = dist;

                    cumAngle += sAngle;
                    outDist[bendCount] = dist;
                    outCumAngle[bendCount] = cumAngle;
                    outNorm[bendCount] = triNormal[nextT];
                    bendCount++;
                }

                visited[visitedCount++] = nextT;
                t = nextT;
                inEdge = adjEdge[flatIdx];
                p = exitPos;
            }

            return bendCount;
        }

        private static float Smoothstep(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / Mathf.Max(edge1 - edge0, 1e-6f));
            return t * t * (3f - 2f * t);
        }

        private static void GetEdgeVertices(int v0, int v1, int v2, int edge, out int from, out int to, out int opposite)
        {
            if (edge == 0) { from = v0; to = v1; opposite = v2; }
            else if (edge == 1) { from = v1; to = v2; opposite = v0; }
            else { from = v2; to = v0; opposite = v1; }
        }
    }
}
