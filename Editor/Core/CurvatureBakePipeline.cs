using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    public readonly struct BakeProgress
    {
        public readonly float Progress; // 0..1 over the whole run
        public readonly string Message;

        public BakeProgress(float progress, string message)
        {
            Progress = progress;
            Message = message;
        }
    }

    public sealed class BakeReport
    {
        public readonly List<string> SavedAssetPaths = new List<string>();
        public readonly List<string> Errors = new List<string>();
        public bool Cancelled;
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// Entry point of the curvature bake. UI-independent: pass targets and settings, observe progress,
    /// and cancel through the token. One texture is written per triangle sub-mesh of every renderer.
    /// </summary>
    public sealed class CurvatureBakePipeline
    {
        private const string ShaderName = "FastCurvatureBake";
        private const string LogPrefix = "[FastCurvatureBaker]";

        // Share of each renderer's progress spent on CPU preparation.
        private const float PrepareShare = 0.1f;

        /// <summary>
        /// Renderers baked for the given targets: the object's own MeshRenderer/SkinnedMeshRenderer,
        /// or every active one among its children when the object itself has none.
        /// </summary>
        public static List<Renderer> CollectRenderers(IEnumerable<GameObject> targets)
        {
            var result = new List<Renderer>();
            foreach (var go in targets)
            {
                if (go == null) continue;

                var own = go.GetComponent<Renderer>();
                if (IsSupported(own))
                {
                    AddUnique(result, own);
                    continue;
                }
                foreach (var child in go.GetComponentsInChildren<Renderer>(false))
                {
                    if (IsSupported(child))
                        AddUnique(result, child);
                }
            }
            return result;
        }

        public async Task<BakeReport> RunAsync(
            IReadOnlyList<GameObject> targets,
            CurvatureBakeSettings settings,
            IProgress<BakeProgress> progress,
            CancellationToken token)
        {
            settings = settings.Clone();
            settings.Validate();

            var report = new BakeReport();
            var timer = Stopwatch.StartNew();

            if (!SystemInfo.supportsComputeShaders)
            {
                report.Errors.Add("Compute shaders are not supported on this graphics API.");
                return report;
            }

            ComputeShader shader = LoadShader();
            if (shader == null)
            {
                report.Errors.Add($"Compute shader '{ShaderName}' was not found.");
                return report;
            }

            List<Renderer> renderers = CollectRenderers(targets);
            if (renderers.Count == 0)
            {
                report.Errors.Add("No MeshRenderer or SkinnedMeshRenderer found in the targets.");
                return report;
            }

            var usedPaths = new HashSet<string>();
            using (var gpu = new CurvatureGpu(shader))
            {
                for (int r = 0; r < renderers.Count; r++)
                {
                    Renderer renderer = renderers[r];
                    string rendererName = renderer.name;
                    float start = (float)r / renderers.Count;
                    float span = 1f / renderers.Count;
                    string prefix = $"[{r + 1}/{renderers.Count}] {rendererName}";
                    void Report(float local, string message) =>
                        progress?.Report(new BakeProgress(start + span * Mathf.Clamp01(local), $"{prefix}: {message}"));

                    try
                    {
                        await BakeRendererAsync(renderer, settings, gpu, usedPaths, report, Report, token);
                    }
                    catch (OperationCanceledException)
                    {
                        report.Cancelled = true;
                        break;
                    }
                    catch (Exception e)
                    {
                        report.Errors.Add($"{rendererName}: {e.Message}");
                        Debug.LogError($"{LogPrefix} Failed to bake '{rendererName}': {e}");
                    }
                }
            }

            report.Elapsed = timer.Elapsed;
            Debug.Log($"{LogPrefix} Finished in {report.Elapsed.TotalSeconds:F2}s. " +
                      $"Saved {report.SavedAssetPaths.Count}, errors {report.Errors.Count}" +
                      (report.Cancelled ? ", cancelled." : "."));
            return report;
        }

        private static async Task BakeRendererAsync(
            Renderer renderer,
            CurvatureBakeSettings settings,
            CurvatureGpu gpu,
            HashSet<string> usedPaths,
            BakeReport report,
            Action<float, string> progress,
            CancellationToken token)
        {
            progress(0f, "Preparing mesh");
            await Task.Yield();
            token.ThrowIfCancellationRequested();

            SurfaceMesh mesh = SurfaceMesh.FromRenderer(renderer, settings.UVChannel);

            var subMeshes = new List<int>();
            for (int sub = 0; sub < mesh.SubMeshTriangles.Length; sub++)
            {
                int[] tris = mesh.SubMeshTriangles[sub];
                if (tris != null && tris.Length >= 3)
                    subMeshes.Add(sub);
            }
            if (subMeshes.Count == 0)
                throw new InvalidOperationException("The mesh has no triangle sub-meshes.");

            progress(PrepareShare * 0.5f, "Sampling surface");
            await Task.Yield();
            token.ThrowIfCancellationRequested();

            // The smooth term is skipped entirely (no samples) when its strength is zero.
            SurfaceSample[] samples = Array.Empty<SurfaceSample>();
            if (settings.Strength > 0f)
            {
                float requestedSpacing = settings.Radius / settings.SamplesPerRadius;
                float spacing = requestedSpacing;
                samples = SurfaceSampler.Generate(mesh, ref spacing, settings.Source == CurvatureSource.Geometry);
                if (spacing > requestedSpacing * 1.001f)
                {
                    Debug.LogWarning($"{LogPrefix} '{renderer.name}': sample budget reached, spacing widened " +
                                     $"from {requestedSpacing:G3} to {spacing:G3}. Increase Radius or lower Quality for cleaner results.");
                }
            }

            progress(PrepareShare * 0.8f, "Building search grids");
            await Task.Yield();
            token.ThrowIfCancellationRequested();

            // 1% margin: CPU and GPU may floor a cell coordinate differently right on a boundary.
            var grid = SpatialHashGrid<SurfaceSample>.Build(
                samples, s => s.Position, mesh.Bounds, settings.Radius * 1.01f);

            SpatialHashGrid<EdgeSegment> edgeGrid = null;
            if (mesh.HardEdges.Count > 0 && settings.EdgeStrength > 0f)
            {
                EdgeSegment[] segments = HardEdgeSegments.Build(mesh.HardEdges, settings.EdgeWidth, out float edgeCell);
                edgeGrid = SpatialHashGrid<EdgeSegment>.Build(segments, e => e.Midpoint, mesh.Bounds, edgeCell);
            }
            token.ThrowIfCancellationRequested();
            Debug.Log($"{LogPrefix} '{renderer.name}': {samples.Length} samples, {mesh.HardEdges.Count} hard edges, " +
                      $"{mesh.ComponentCount} parts.");
            gpu.LoadMesh(mesh, grid, edgeGrid);

            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < subMeshes.Count; i++)
            {
                int sub = subMeshes[i];
                Material material = sub < materials.Length ? materials[sub] : null;
                string materialName = material != null ? material.name : $"sub{sub}";
                string label = subMeshes.Count > 1 ? $"{materialName}: " : "";

                float subStart = PrepareShare + (1f - PrepareShare) * i / subMeshes.Count;
                float subSpan = (1f - PrepareShare) / subMeshes.Count;

                Vector2[] data = await gpu.BakeSubMeshAsync(
                    mesh.SubMeshTriangles[sub], settings,
                    (p, message) => progress(subStart + subSpan * p, label + message),
                    token);

                progress(subStart + subSpan * 0.98f, label + "Saving");
                string baseName = subMeshes.Count > 1 ? $"{renderer.name}_{materialName}" : renderer.name;
                string assetPath = ReservePath(
                    CurvatureTextureExporter.ResolveOutputFolder(material),
                    CurvatureTextureExporter.SanitizeFileName(baseName),
                    settings.FileSuffix,
                    usedPaths);

                string saved = CurvatureTextureExporter.Save(data, settings.Resolution, settings, assetPath);
                report.SavedAssetPaths.Add(saved);
                Debug.Log($"{LogPrefix} Saved {saved}");
            }
        }

        /// <summary>
        /// Picks <c>{folder}/{name}{suffix}.png</c>, adding a number when an earlier texture of this run
        /// already took that path (e.g. two renderers with the same name).
        /// </summary>
        private static string ReservePath(string folder, string name, string suffix, HashSet<string> usedPaths)
        {
            string path = $"{folder}/{name}{suffix}.png";
            for (int n = 1; usedPaths.Contains(path); n++)
                path = $"{folder}/{name}{suffix} {n}.png";
            usedPaths.Add(path);
            return path;
        }

        private static ComputeShader LoadShader()
        {
            foreach (string guid in AssetDatabase.FindAssets($"{ShaderName} t:ComputeShader"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == ShaderName)
                    return AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            }
            return null;
        }

        private static bool IsSupported(Renderer renderer)
        {
            return (renderer is SkinnedMeshRenderer || renderer is MeshRenderer) &&
                   SurfaceMesh.GetSharedMesh(renderer) != null;
        }

        private static void AddUnique(List<Renderer> list, Renderer renderer)
        {
            if (!list.Contains(renderer))
                list.Add(renderer);
        }
    }
}
