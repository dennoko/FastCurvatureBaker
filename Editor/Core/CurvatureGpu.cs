using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// Runs FastCurvatureBake.compute: UV rasterization, curvature evaluation and post-processing.
    /// Load a mesh with <see cref="LoadMesh"/>, then bake each sub-mesh with <see cref="BakeSubMeshAsync"/>.
    /// </summary>
    internal sealed class CurvatureGpu : IDisposable
    {
        private const int GroupSize = 64;
        private const int MaxElementsPerDispatch = GroupSize * 65535;
        private const double TargetDispatchMs = 150.0;
        private const long YieldIntervalMs = 100;
        private const int MaxChunk = 1 << 20;

        private readonly ComputeShader _shader;
        private readonly int _kClear;
        private readonly int _kRasterInterior;
        private readonly int _kRasterDistance;
        private readonly int _kRasterOwner;
        private readonly int _kResolve;
        private readonly int _kEvaluate;
        private readonly int _kDownsample;
        private readonly int _kBlur;
        private readonly int _kJfaInit;
        private readonly int _kJfaStep;
        private readonly int _kJfaApply;

        // Per-mesh resources
        private ComputeBuffer _positions;
        private ComputeBuffer _normals;
        private ComputeBuffer _uvsBuffer;
        private Vector2[] _uvs;
        private ComputeBuffer _patches;
        private ComputeBuffer _patchComponents;
        private ComputeBuffer _samples;
        private ComputeBuffer _sampleCells;
        private ComputeBuffer _edges;
        private ComputeBuffer _edgeCells;
        private SpatialHashGrid<SurfaceSample> _grid;
        private SpatialHashGrid<EdgeSegment> _edgeGrid;
        private float _edgeMaxReach;

        public CurvatureGpu(ComputeShader shader)
        {
            _shader = shader != null ? shader : throw new ArgumentNullException(nameof(shader));
            _kClear = FindKernel("ClearGBuffer");
            _kRasterInterior = FindKernel("RasterInterior");
            _kRasterDistance = FindKernel("RasterConservativeDistance");
            _kRasterOwner = FindKernel("RasterConservativeOwner");
            _kResolve = FindKernel("ResolveGBuffer");
            _kEvaluate = FindKernel("EvaluateCurvature");
            _kDownsample = FindKernel("Downsample");
            _kBlur = FindKernel("Blur");
            _kJfaInit = FindKernel("JfaInit");
            _kJfaStep = FindKernel("JfaStep");
            _kJfaApply = FindKernel("JfaApply");
        }

        /// <param name="edgeGrid">Hard/sharp edge segments, or null when the mesh has none.</param>
        /// <param name="edgeMaxReach">Maximum distance from a point to an edge segment midpoint within EdgeWidth.</param>
        public void LoadMesh(
            SurfaceMesh mesh,
            SpatialHashGrid<SurfaceSample> grid,
            SpatialHashGrid<EdgeSegment> edgeGrid,
            float edgeMaxReach)
        {
            ReleaseMesh();
            _grid = grid;
            _edgeGrid = edgeGrid;
            _edgeMaxReach = edgeMaxReach;

            _positions = CreateBuffer(mesh.Positions, 12);
            _normals = CreateBuffer(mesh.Normals, 12);
            _uvs = mesh.UVs;
            _uvsBuffer = CreateBuffer(mesh.UVs, 8);
            _patches = CreateBuffer(mesh.PatchIds, 4);
            _patchComponents = CreateBuffer(mesh.PatchComponents, 4);

            // Placeholder when the smooth term is off: buffers cannot be empty.
            _samples = grid.SortedItems.Length > 0
                ? CreateBuffer(grid.SortedItems, SurfaceSample.Stride)
                : new ComputeBuffer(1, SurfaceSample.Stride);
            _sampleCells = CreateBuffer(grid.CellTable, CellEntry.Stride);

            // The kernel always references the edge buffers; bind placeholders when there are no edges.
            _edges = edgeGrid != null && edgeGrid.SortedItems.Length > 0
                ? CreateBuffer(edgeGrid.SortedItems, EdgeSegment.Stride)
                : new ComputeBuffer(1, EdgeSegment.Stride);
            _edgeCells = edgeGrid != null && edgeGrid.CellTable.Length > 0
                ? CreateBuffer(edgeGrid.CellTable, CellEntry.Stride)
                : CreateBuffer(new CellEntry[1], CellEntry.Stride);
        }

        /// <summary>
        /// Bakes one sub-mesh. Returns <c>Resolution²</c> texels, row-major from v = 0,
        /// each holding (signed curvature with strengths applied, UV island id + 1 or 0 when empty).
        /// </summary>
        public async Task<Vector2[]> BakeSubMeshAsync(
            int[] triangles,
            CurvatureBakeSettings settings,
            Action<float, string> progress,
            CancellationToken token)
        {
            if (_grid == null)
                throw new InvalidOperationException("LoadMesh must be called before baking.");

            int outputRes = settings.Resolution;
            int res = outputRes * settings.SupersampleFactor;
            int texels = res * res;

            ComputeBuffer triangleBuffer = null, islandBuffer = null, tileBuffer = null;
            ComputeBuffer gOwner = null, gPosition = null, gNormal = null;
            ComputeBuffer result = null, pingA = null, pingB = null;
            ComputeBuffer jfaA = null, jfaB = null;
            try
            {
                progress(0f, "Preparing UV layout");
                Vector2[] uvs = _uvs;
                (uint[] tiles, int tileSize, uint[] islands) = await Task.Run(() =>
                {
                    uint[] t = UvLayout.BuildRasterTiles(triangles, uvs, res, out int ts, token);
                    uint[] isl = UvLayout.LabelIslands(triangles, uvs, token);
                    return (t, ts, isl);
                }, token);
                int tileCount = tiles.Length / 2;
                token.ThrowIfCancellationRequested();

                progress(0.01f, "Rasterizing UV");
                triangleBuffer = CreateBuffer(triangles, 4);
                islandBuffer = CreateBuffer(islands, 4);

                gOwner = new ComputeBuffer(texels, 4);
                gPosition = new ComputeBuffer(texels, 16);
                gNormal = new ComputeBuffer(texels, 4); // also the conservative passes' distance scratch (_GDist)
                result = new ComputeBuffer(texels, 8);

                // ---- G-buffer ----
                _shader.SetInt("_Width", res);
                _shader.SetInt("_TileSize", tileSize);
                _shader.SetFloat("_GeometryWeight", settings.Source == CurvatureSource.Geometry ? 1f : 0f);

                _shader.SetBuffer(_kClear, "_GOwner", gOwner);
                _shader.SetBuffer(_kClear, "_GDist", gNormal);
                Dispatch1D(_kClear, texels);

                if (tileCount > 0)
                {
                    tileBuffer = CreatePairBuffer(tiles);
                    var passes = new[] { _kRasterInterior, _kRasterDistance, _kRasterOwner };
                    for (int i = 0; i < passes.Length; i++)
                    {
                        int kernel = passes[i];
                        _shader.SetBuffer(kernel, "_UVs", _uvsBuffer);
                        _shader.SetBuffer(kernel, "_Triangles", triangleBuffer);
                        _shader.SetBuffer(kernel, "_RasterTiles", tileBuffer);
                        _shader.SetBuffer(kernel, "_GOwner", gOwner);
                        _shader.SetBuffer(kernel, "_GDist", gNormal);
                        int pass = i;
                        await DispatchAdaptiveAsync(kernel, tileCount, gOwner, 4,
                            p => progress(0.01f + 0.04f * (pass + p) / passes.Length, "Rasterizing UV"), token);
                    }
                    tileBuffer.Release(); tileBuffer = null;
                }

                _shader.SetBuffer(_kResolve, "_Positions", _positions);
                _shader.SetBuffer(_kResolve, "_Normals", _normals);
                _shader.SetBuffer(_kResolve, "_UVs", _uvsBuffer);
                _shader.SetBuffer(_kResolve, "_Patches", _patches);
                _shader.SetBuffer(_kResolve, "_Triangles", triangleBuffer);
                _shader.SetBuffer(_kResolve, "_GOwner", gOwner);
                _shader.SetBuffer(_kResolve, "_GPosition", gPosition);
                _shader.SetBuffer(_kResolve, "_GNormal", gNormal);
                Dispatch1D(_kResolve, texels);
                WaitForGpu(gNormal, 4);
                await Task.Yield();
                token.ThrowIfCancellationRequested();

                // ---- Curvature ----
                float sampleNorm = settings.Radius / _grid.CellSize + 1e-3f;
                _shader.SetVector("_GridOrigin", _grid.Origin);
                _shader.SetFloat("_InvCellSize", 1f / _grid.CellSize);
                _shader.SetInt("_HashMask", (int)_grid.HashMask);
                _shader.SetFloat("_SampleSearchNorm", sampleNorm * sampleNorm);
                _shader.SetFloat("_Radius", settings.Radius);
                _shader.SetFloat("_Strength", settings.UsesSmoothTerm ? settings.Strength : 0f);
                _shader.SetFloat("_NormalRejection", settings.NormalRejection);
                _shader.SetInt("_SameComponentOnly", settings.SameComponentOnly ? 1 : 0);

                int edgeCount = _edgeGrid != null ? _edgeGrid.SortedItems.Length : 0;
                _shader.SetInt("_EdgeCount", edgeCount);
                if (edgeCount > 0)
                {
                    float edgeNorm = _edgeMaxReach / _edgeGrid.CellSize + 1e-3f;
                    _shader.SetVector("_EdgeGridOrigin", _edgeGrid.Origin);
                    _shader.SetFloat("_EdgeInvCellSize", 1f / _edgeGrid.CellSize);
                    _shader.SetInt("_EdgeHashMask", (int)_edgeGrid.HashMask);
                    _shader.SetFloat("_EdgeSearchNorm", edgeNorm * edgeNorm);
                }
                else
                {
                    _shader.SetFloat("_EdgeSearchNorm", 0f);
                }
                _shader.SetFloat("_EdgeWidth", settings.EdgeWidth);
                _shader.SetFloat("_EdgeStrength", settings.EdgeStrength);
                _shader.SetInt("_SharpConvexMode", settings.Mode == CurvatureBakeMode.SharpConvex ? 1 : 0);

                _shader.SetBuffer(_kEvaluate, "_GPosition", gPosition);
                _shader.SetBuffer(_kEvaluate, "_GNormal", gNormal);
                _shader.SetBuffer(_kEvaluate, "_GOwner", gOwner);
                _shader.SetBuffer(_kEvaluate, "_TriangleIslands", islandBuffer);
                _shader.SetBuffer(_kEvaluate, "_PatchComponents", _patchComponents);
                _shader.SetBuffer(_kEvaluate, "_Samples", _samples);
                _shader.SetBuffer(_kEvaluate, "_SampleCells", _sampleCells);
                _shader.SetBuffer(_kEvaluate, "_Edges", _edges);
                _shader.SetBuffer(_kEvaluate, "_EdgeCells", _edgeCells);
                _shader.SetBuffer(_kEvaluate, "_Result", result);

                await DispatchAdaptiveAsync(_kEvaluate, texels, result, 8,
                    p => progress(0.05f + 0.85f * p, "Evaluating curvature"), token);

                gOwner.Release(); gOwner = null;
                gPosition.Release(); gPosition = null;
                gNormal.Release(); gNormal = null;
                triangleBuffer.Release(); triangleBuffer = null;
                islandBuffer.Release(); islandBuffer = null;

                // ---- Post-process ----
                progress(0.92f, "Post-processing");
                int outputTexels = outputRes * outputRes;
                ComputeBuffer current = result;
                ComputeBuffer spare;
                if (res != outputRes)
                {
                    pingA = new ComputeBuffer(outputTexels, 8);
                    _shader.SetInt("_SrcWidth", res);
                    _shader.SetInt("_DstWidth", outputRes);
                    _shader.SetBuffer(_kDownsample, "_Src", result);
                    _shader.SetBuffer(_kDownsample, "_Dst", pingA);
                    Dispatch1D(_kDownsample, outputTexels);
                    result.Release(); result = null;
                    current = pingA;
                    pingB = new ComputeBuffer(outputTexels, 8);
                    spare = pingB;
                }
                else
                {
                    pingA = new ComputeBuffer(outputTexels, 8);
                    spare = pingA;
                }

                _shader.SetInt("_SrcWidth", outputRes);
                for (int i = 0; i < settings.BlurPasses; i++)
                    PingPong(_kBlur, ref current, ref spare, outputTexels);

                if (settings.DilationPixels > 0)
                {
                    jfaA = new ComputeBuffer(outputTexels, 4);
                    jfaB = new ComputeBuffer(outputTexels, 4);

                    _shader.SetBuffer(_kJfaInit, "_Src", current);
                    _shader.SetBuffer(_kJfaInit, "_JfaDst", jfaA);
                    Dispatch1D(_kJfaInit, outputTexels);

                    ComputeBuffer jfaCur = jfaA;
                    ComputeBuffer jfaSpare = jfaB;
                    int maxStep = 1;
                    while (maxStep * 2 <= settings.DilationPixels)
                        maxStep *= 2;

                    for (int step = maxStep; step >= 1; step >>= 1)
                    {
                        _shader.SetInt("_JfaStep", step);
                        _shader.SetBuffer(_kJfaStep, "_JfaSrc", jfaCur);
                        _shader.SetBuffer(_kJfaStep, "_JfaDst", jfaSpare);
                        Dispatch1D(_kJfaStep, outputTexels);
                        (jfaCur, jfaSpare) = (jfaSpare, jfaCur);
                    }
                    // JFA+1 refinement pass eliminates rare 1-px Voronoi corner discrepancies.
                    if (settings.DilationPixels > 1)
                    {
                        _shader.SetInt("_JfaStep", 1);
                        _shader.SetBuffer(_kJfaStep, "_JfaSrc", jfaCur);
                        _shader.SetBuffer(_kJfaStep, "_JfaDst", jfaSpare);
                        Dispatch1D(_kJfaStep, outputTexels);
                        (jfaCur, jfaSpare) = (jfaSpare, jfaCur);
                    }

                    _shader.SetInt("_DilationDist2", settings.DilationPixels * settings.DilationPixels);
                    _shader.SetBuffer(_kJfaApply, "_Src", current);
                    _shader.SetBuffer(_kJfaApply, "_JfaSrc", jfaCur);
                    _shader.SetBuffer(_kJfaApply, "_Dst", spare);
                    Dispatch1D(_kJfaApply, outputTexels);
                    (current, spare) = (spare, current);

                    jfaA.Release(); jfaA = null;
                    jfaB.Release(); jfaB = null;
                }

                progress(0.97f, "Reading back");
                var data = new Vector2[outputTexels];
                current.GetData(data);
                return data;
            }
            finally
            {
                triangleBuffer?.Release();
                islandBuffer?.Release();
                tileBuffer?.Release();
                gOwner?.Release();
                gPosition?.Release();
                gNormal?.Release();
                result?.Release();
                pingA?.Release();
                pingB?.Release();
                jfaA?.Release();
                jfaB?.Release();
            }
        }

        public void Dispose() => ReleaseMesh();

        private void ReleaseMesh()
        {
            _positions?.Release(); _positions = null;
            _normals?.Release(); _normals = null;
            _uvsBuffer?.Release(); _uvsBuffer = null;
            _uvs = null;
            _patches?.Release(); _patches = null;
            _patchComponents?.Release(); _patchComponents = null;
            _samples?.Release(); _samples = null;
            _sampleCells?.Release(); _sampleCells = null;
            _edges?.Release(); _edges = null;
            _edgeCells?.Release(); _edgeCells = null;
            _grid = null;
            _edgeGrid = null;
        }

        private void PingPong(int kernel, ref ComputeBuffer current, ref ComputeBuffer spare, int count)
        {
            _shader.SetBuffer(kernel, "_Src", current);
            _shader.SetBuffer(kernel, "_Dst", spare);
            Dispatch1D(kernel, count);
            (current, spare) = (spare, current);
        }

        private void Dispatch1D(int kernel, int count)
        {
            for (int offset = 0; offset < count; offset += MaxElementsPerDispatch)
            {
                int n = Math.Min(MaxElementsPerDispatch, count - offset);
                _shader.SetInt("_Offset", offset);
                _shader.SetInt("_End", offset + n);
                _shader.Dispatch(kernel, CeilDiv(n, GroupSize), 1, 1);
            }
        }

        /// <summary>
        /// Splits a heavy dispatch into chunks sized to take about <see cref="TargetDispatchMs"/> each,
        /// waiting for every chunk so no single GPU submission can trip the driver timeout (TDR).
        /// Chunk sizes are rounded to multiples of <see cref="GroupSize"/> so 8x8 UV blocks stay aligned.
        /// </summary>
        private async Task DispatchAdaptiveAsync(
            int kernel, int count, ComputeBuffer probe, int probeStride, Action<float> progress, CancellationToken token)
        {
            int chunk = 16384;
            var chunkTimer = new Stopwatch();
            var yieldTimer = Stopwatch.StartNew();

            for (int offset = 0; offset < count;)
            {
                int n = Math.Min(chunk, count - offset);
                _shader.SetInt("_Offset", offset);
                _shader.SetInt("_End", offset + n);

                chunkTimer.Restart();
                _shader.Dispatch(kernel, CeilDiv(n, GroupSize), 1, 1);
                WaitForGpu(probe, probeStride);
                double ms = Math.Max(chunkTimer.Elapsed.TotalMilliseconds, 0.5);

                offset += n;
                double scale = Math.Min(2.0, TargetDispatchMs / ms);
                int rawChunk = (int)Math.Max(1024, Math.Min(MaxChunk, n * scale));
                chunk = (rawChunk + GroupSize - 1) & ~(GroupSize - 1);

                if (yieldTimer.ElapsedMilliseconds >= YieldIntervalMs)
                {
                    progress((float)offset / count);
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                    yieldTimer.Restart();
                }
            }
            progress(1f);
        }

        /// <summary>Blocks until the GPU has finished all work queued before this call (reads back one element).</summary>
        private static void WaitForGpu(ComputeBuffer buffer, int stride)
        {
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                AsyncGPUReadback.Request(buffer, stride, 0).WaitForCompletion();
            }
            else
            {
                var probe = new byte[stride];
                buffer.GetData(probe, 0, 0, stride);
            }
        }

        private int FindKernel(string name)
        {
            if (!_shader.HasKernel(name))
                throw new InvalidOperationException($"Kernel '{name}' not found in {_shader.name}.");
            return _shader.FindKernel(name);
        }

        private static ComputeBuffer CreateBuffer<T>(T[] data, int stride) where T : struct
        {
            var buffer = new ComputeBuffer(data.Length, stride);
            buffer.SetData(data);
            return buffer;
        }

        /// <summary>Uploads an interleaved uint array as one uint2 element per pair.</summary>
        private static ComputeBuffer CreatePairBuffer(uint[] pairs)
        {
            var buffer = new ComputeBuffer(pairs.Length / 2, 8);
            buffer.SetData(pairs);
            return buffer;
        }

        private static int CeilDiv(int a, int b) => (a + b - 1) / b;
    }
}
