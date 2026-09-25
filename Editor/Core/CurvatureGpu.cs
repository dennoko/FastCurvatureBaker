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

        private readonly ComputeShader _shader;
        private readonly int _kClear;
        private readonly int _kRasterInterior;
        private readonly int _kRasterConservative;
        private readonly int _kEvaluate;
        private readonly int _kDownsample;
        private readonly int _kBlur;
        private readonly int _kDilate;

        // Per-mesh resources
        private ComputeBuffer _positions;
        private ComputeBuffer _normals;
        private ComputeBuffer _uvs;
        private ComputeBuffer _patches;
        private ComputeBuffer _patchComponents;
        private ComputeBuffer _samples;
        private ComputeBuffer _buckets;
        private ComputeBuffer _edges;
        private ComputeBuffer _edgeBuckets;
        private SpatialHashGrid<SurfaceSample> _grid;
        private SpatialHashGrid<EdgeSegment> _edgeGrid;

        public CurvatureGpu(ComputeShader shader)
        {
            _shader = shader != null ? shader : throw new ArgumentNullException(nameof(shader));
            _kClear = FindKernel("ClearGBuffer");
            _kRasterInterior = FindKernel("RasterInterior");
            _kRasterConservative = FindKernel("RasterConservative");
            _kEvaluate = FindKernel("EvaluateCurvature");
            _kDownsample = FindKernel("Downsample");
            _kBlur = FindKernel("Blur");
            _kDilate = FindKernel("Dilate");
        }

        /// <param name="edgeGrid">Hard-edge segments, or null when the mesh has none.</param>
        public void LoadMesh(SurfaceMesh mesh, SpatialHashGrid<SurfaceSample> grid, SpatialHashGrid<EdgeSegment> edgeGrid)
        {
            ReleaseMesh();
            _grid = grid;
            _edgeGrid = edgeGrid;
            _positions = CreateBuffer(mesh.Positions, 12);
            _normals = CreateBuffer(mesh.Normals, 12);
            _uvs = CreateBuffer(mesh.UVs, 8);
            _patches = CreateBuffer(mesh.PatchIds, 4);
            _patchComponents = CreateBuffer(mesh.PatchComponents, 4);
            _samples = CreateBuffer(grid.SortedItems, SurfaceSample.Stride);
            _buckets = CreateBucketBuffer(grid.Buckets);

            // The kernel always references the edge buffers; bind placeholders when there are no edges.
            _edges = edgeGrid != null
                ? CreateBuffer(edgeGrid.SortedItems, EdgeSegment.Stride)
                : new ComputeBuffer(1, EdgeSegment.Stride);
            _edgeBuckets = edgeGrid != null
                ? CreateBucketBuffer(edgeGrid.Buckets)
                : new ComputeBuffer(1, 8);
        }

        /// <summary>
        /// Bakes one sub-mesh. Returns <c>Resolution²</c> texels, row-major from v = 0,
        /// each holding (signed curvature with strengths applied, coverage).
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
            int triangleCount = triangles.Length / 3;

            ComputeBuffer triangleBuffer = null, gPosition = null, gNormal = null;
            ComputeBuffer result = null, pingA = null, pingB = null;
            try
            {
                triangleBuffer = CreateBuffer(triangles, 4);
                gPosition = new ComputeBuffer(texels, 16);
                gNormal = new ComputeBuffer(texels, 4);
                result = new ComputeBuffer(texels, 8);

                // ---- G-buffer ----
                progress(0f, "Rasterizing UV");
                _shader.SetInt("_Width", res);

                _shader.SetBuffer(_kClear, "_GPosition", gPosition);
                _shader.SetBuffer(_kClear, "_GNormal", gNormal);
                Dispatch1D(_kClear, texels);

                foreach (int kernel in new[] { _kRasterInterior, _kRasterConservative })
                {
                    _shader.SetBuffer(kernel, "_Positions", _positions);
                    _shader.SetBuffer(kernel, "_Normals", _normals);
                    _shader.SetBuffer(kernel, "_UVs", _uvs);
                    _shader.SetBuffer(kernel, "_Patches", _patches);
                    _shader.SetBuffer(kernel, "_Triangles", triangleBuffer);
                    _shader.SetBuffer(kernel, "_GPosition", gPosition);
                    _shader.SetBuffer(kernel, "_GNormal", gNormal);
                    Dispatch1D(kernel, triangleCount);
                }
                WaitForGpu(gNormal, 4, 0);
                await Task.Yield();
                token.ThrowIfCancellationRequested();

                // ---- Curvature ----
                _shader.SetVector("_GridOrigin", _grid.Origin);
                _shader.SetFloat("_InvCellSize", 1f / _grid.CellSize);
                _shader.SetInt("_HashMask", (int)_grid.HashMask);
                _shader.SetFloat("_Radius", settings.Radius);
                _shader.SetFloat("_Strength", settings.Strength);
                _shader.SetFloat("_NormalRejection", settings.NormalRejection);
                _shader.SetFloat("_GeometryWeight", settings.Source == CurvatureSource.Geometry ? 1f : 0f);
                _shader.SetInt("_SameComponentOnly", settings.SameComponentOnly ? 1 : 0);

                _shader.SetInt("_EdgeCount", _edgeGrid != null ? _edgeGrid.SortedItems.Length : 0);
                if (_edgeGrid != null)
                {
                    _shader.SetVector("_EdgeGridOrigin", _edgeGrid.Origin);
                    _shader.SetFloat("_EdgeInvCellSize", 1f / _edgeGrid.CellSize);
                    _shader.SetInt("_EdgeHashMask", (int)_edgeGrid.HashMask);
                }
                _shader.SetFloat("_EdgeWidth", settings.EdgeWidth);
                _shader.SetFloat("_EdgeStrength", settings.EdgeStrength);

                _shader.SetBuffer(_kEvaluate, "_GPosition", gPosition);
                _shader.SetBuffer(_kEvaluate, "_GNormal", gNormal);
                _shader.SetBuffer(_kEvaluate, "_PatchComponents", _patchComponents);
                _shader.SetBuffer(_kEvaluate, "_Samples", _samples);
                _shader.SetBuffer(_kEvaluate, "_Buckets", _buckets);
                _shader.SetBuffer(_kEvaluate, "_Edges", _edges);
                _shader.SetBuffer(_kEvaluate, "_EdgeBuckets", _edgeBuckets);
                _shader.SetBuffer(_kEvaluate, "_Result", result);

                await DispatchAdaptiveAsync(_kEvaluate, texels, result,
                    p => progress(0.05f + 0.85f * p, "Evaluating curvature"), token);

                gPosition.Release(); gPosition = null;
                gNormal.Release(); gNormal = null;
                triangleBuffer.Release(); triangleBuffer = null;

                // ---- Post-process ----
                progress(0.92f, "Post-processing");
                ComputeBuffer current = result;
                ComputeBuffer spare;
                if (res != outputRes)
                {
                    pingA = new ComputeBuffer(outputRes * outputRes, 8);
                    _shader.SetInt("_SrcWidth", res);
                    _shader.SetInt("_DstWidth", outputRes);
                    _shader.SetBuffer(_kDownsample, "_Src", result);
                    _shader.SetBuffer(_kDownsample, "_Dst", pingA);
                    Dispatch1D(_kDownsample, outputRes * outputRes);
                    result.Release(); result = null;
                    current = pingA;
                    pingB = new ComputeBuffer(outputRes * outputRes, 8);
                    spare = pingB;
                }
                else
                {
                    pingA = new ComputeBuffer(texels, 8);
                    spare = pingA;
                }

                _shader.SetInt("_SrcWidth", outputRes);
                for (int i = 0; i < settings.BlurPasses; i++)
                    PingPong(_kBlur, ref current, ref spare, outputRes);
                for (int i = 0; i < settings.DilationPixels; i++)
                    PingPong(_kDilate, ref current, ref spare, outputRes);

                progress(0.97f, "Reading back");
                var data = new Vector2[outputRes * outputRes];
                current.GetData(data);
                return data;
            }
            finally
            {
                triangleBuffer?.Release();
                gPosition?.Release();
                gNormal?.Release();
                result?.Release();
                pingA?.Release();
                pingB?.Release();
            }
        }

        public void Dispose() => ReleaseMesh();

        private void ReleaseMesh()
        {
            _positions?.Release(); _positions = null;
            _normals?.Release(); _normals = null;
            _uvs?.Release(); _uvs = null;
            _patches?.Release(); _patches = null;
            _patchComponents?.Release(); _patchComponents = null;
            _samples?.Release(); _samples = null;
            _buckets?.Release(); _buckets = null;
            _edges?.Release(); _edges = null;
            _edgeBuckets?.Release(); _edgeBuckets = null;
            _grid = null;
            _edgeGrid = null;
        }

        private void PingPong(int kernel, ref ComputeBuffer current, ref ComputeBuffer spare, int width)
        {
            _shader.SetBuffer(kernel, "_Src", current);
            _shader.SetBuffer(kernel, "_Dst", spare);
            Dispatch1D(kernel, width * width);
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
        /// </summary>
        private async Task DispatchAdaptiveAsync(
            int kernel, int count, ComputeBuffer output, Action<float> progress, CancellationToken token)
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
                WaitForGpu(output, 8, offset + n - 1);
                double ms = Math.Max(chunkTimer.Elapsed.TotalMilliseconds, 0.5);

                offset += n;
                double scale = Math.Min(2.0, TargetDispatchMs / ms);
                chunk = (int)Math.Max(1024, Math.Min(MaxElementsPerDispatch, n * scale));

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

        /// <summary>Blocks until the GPU has finished all work queued before this call.</summary>
        private static void WaitForGpu(ComputeBuffer buffer, int stride, int elementIndex)
        {
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                AsyncGPUReadback.Request(buffer, stride, elementIndex * stride).WaitForCompletion();
            }
            else
            {
                var probe = new byte[stride];
                buffer.GetData(probe, 0, elementIndex * stride, stride);
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

        /// <summary>Uploads an interleaved (start, count) table as one uint2 element per bucket.</summary>
        private static ComputeBuffer CreateBucketBuffer(uint[] buckets)
        {
            var buffer = new ComputeBuffer(buckets.Length / 2, 8);
            buffer.SetData(buckets);
            return buffer;
        }

        private static int CeilDiv(int a, int b) => (a + b - 1) / b;
    }
}
