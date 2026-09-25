using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>
    /// Minimal window for driving <see cref="CurvatureBakePipeline"/>. Intended to be replaced by the final UI.
    /// </summary>
    public sealed class FastCurvatureBakerWindow : EditorWindow
    {
        private const string Title = "Fast Curvature Baker";

        private static readonly GUIContent[] ModeLabels =
        {
            new GUIContent("Default"),
            new GUIContent("Convex (edge wear mask)"),
            new GUIContent("Concave (dirt mask)"),
        };

        [SerializeField] private List<GameObject> _targets = new List<GameObject>();
        [SerializeField] private CurvatureBakeSettings _settings = new CurvatureBakeSettings();
        [SerializeField] private bool _showAdvanced;

        private SerializedObject _serializedObject;
        private Vector2 _scroll;
        private CancellationTokenSource _cancellation;

        private bool IsBaking => _cancellation != null;

        [MenuItem("dennokoworks/Fast Curvature Baker")]
        public static void Open() => GetWindow<FastCurvatureBakerWindow>(Title);

        private void OnEnable()
        {
            _serializedObject = new SerializedObject(this);
        }

        private void OnDisable()
        {
            _cancellation?.Cancel();
        }

        private void OnGUI()
        {
            _serializedObject.Update();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            using (new EditorGUI.DisabledScope(IsBaking))
            {
                EditorGUILayout.LabelField("Targets", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(_serializedObject.FindProperty(nameof(_targets)), true);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Add Selected"))
                        AddSelection();
                    if (GUILayout.Button("Clear"))
                    {
                        Undo.RecordObject(this, "Clear Targets");
                        _targets.Clear();
                    }
                }
                _serializedObject.ApplyModifiedProperties();

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
                DrawSettings();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Output: '<base texture folder>/" + CurvatureTextureExporter.OutputFolderName + "/' " +
                "or '" + CurvatureTextureExporter.FallbackFolder + "/' when the material has no base texture.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(IsBaking || _targets.All(t => t == null)))
            {
                if (GUILayout.Button(IsBaking ? "Baking..." : "Bake", GUILayout.Height(32)))
                    Bake();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSettings()
        {
            EditorGUI.BeginChangeCheck();
            var s = _settings.Clone();

            s.Mode = (CurvatureBakeMode)EditorGUILayout.Popup(
                new GUIContent("Bake Mode", "Default: signed curvature (0.5 = flat).\n" +
                                            "Convex: white on convex edges, for edge wear / scratch masks.\n" +
                                            "Concave: white in creases, for dirt / grime masks."),
                (int)s.Mode, ModeLabels);
            s.Resolution = EditorGUILayout.IntPopup("Resolution", s.Resolution,
                CurvatureBakeSettings.SupportedResolutions.Select(r => r.ToString()).ToArray(),
                CurvatureBakeSettings.SupportedResolutions);
            s.UVChannel = EditorGUILayout.IntPopup("UV Channel", s.UVChannel,
                Enumerable.Range(0, 8).Select(i => $"UV{i}").ToArray(),
                Enumerable.Range(0, 8).ToArray());

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Hard Edges", EditorStyles.miniBoldLabel);
            s.EdgeWidth = EditorGUILayout.FloatField(
                new GUIContent("Edge Width (m)", "Distance from a hard edge (split normals) that is treated as convex/concave. " +
                                                 "Make it small for thin wear lines on metal corners."),
                s.EdgeWidth);
            s.EdgeStrength = EditorGUILayout.Slider(
                new GUIContent("Edge Strength", "A 90-degree edge reaches full intensity at 1. 0 disables hard edges."),
                s.EdgeStrength, 0f, 4f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Smooth Surfaces", EditorStyles.miniBoldLabel);
            s.Radius = EditorGUILayout.FloatField(
                new GUIContent("Radius (m)", "Radius used to measure the curvature of smooth (non hard-edge) surfaces."),
                s.Radius);
            s.Strength = EditorGUILayout.Slider("Strength", s.Strength, 0f, 4f);
            s.Source = (CurvatureSource)EditorGUILayout.EnumPopup(
                new GUIContent("Source", "Shading Normals: smooth, follows shading. Geometry: every polygon crease."),
                s.Source);

            EditorGUILayout.Space();
            s.Quality = (BakeQuality)EditorGUILayout.EnumPopup("Quality", s.Quality);
            s.Supersample = EditorGUILayout.Toggle(
                new GUIContent("Supersample 2x", "Ignored when the resolution is " + CurvatureBakeSettings.MaxInternalResolution + "."),
                s.Supersample);

            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Advanced", true);
            if (_showAdvanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    s.SameComponentOnly = EditorGUILayout.Toggle(
                        new GUIContent("Same Part Only", "Ignore other disconnected parts of the mesh (e.g. clothes over skin)."),
                        s.SameComponentOnly);
                    s.NormalRejection = EditorGUILayout.Slider(
                        new GUIContent("Normal Rejection", "Ignore nearby surfaces facing away more than this (dot product). Weight fades back in over the next 0.25."),
                        s.NormalRejection, -1f, CurvatureBakeSettings.MaxNormalRejection);
                    s.BlurPasses = EditorGUILayout.IntSlider("Blur Passes", s.BlurPasses, 0, 16);
                    s.DilationPixels = EditorGUILayout.IntSlider("Dilation (px)", s.DilationPixels, 0, 64);
                    s.OverwriteExisting = EditorGUILayout.Toggle("Overwrite Existing", s.OverwriteExisting);
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(this, "Change Curvature Bake Settings");
                s.Validate();
                _settings = s;
            }
        }

        private void AddSelection()
        {
            Undo.RecordObject(this, "Add Targets");
            foreach (var go in Selection.gameObjects)
            {
                if (!EditorUtility.IsPersistent(go) && !_targets.Contains(go))
                    _targets.Add(go);
            }
            _targets.RemoveAll(t => t == null);
        }

        private async void Bake()
        {
            var targets = _targets.Where(t => t != null).Distinct().ToList();
            var renderers = CurvatureBakePipeline.CollectRenderers(targets);
            if (renderers.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "No MeshRenderer or SkinnedMeshRenderer found in the targets.", "OK");
                return;
            }
            if (!EnsureReadable(renderers))
                return;

            _cancellation = new CancellationTokenSource();
            var progress = new ImmediateProgress(p =>
            {
                if (EditorUtility.DisplayCancelableProgressBar(Title, p.Message, p.Progress))
                    _cancellation?.Cancel();
            });

            BakeReport report = null;
            try
            {
                report = await new CurvatureBakePipeline().RunAsync(targets, _settings, progress, _cancellation.Token);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog(Title, "Bake failed: " + e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _cancellation.Dispose();
                _cancellation = null;
                Repaint();
            }

            if (report != null)
                ShowReport(report);
        }

        private static bool EnsureReadable(List<Renderer> renderers)
        {
            List<Mesh> unreadable = MeshReadabilityUtility.FindUnreadableMeshes(renderers);
            if (unreadable.Count == 0)
                return true;

            string names = string.Join("\n", unreadable.Select(m => "- " + m.name));
            if (!EditorUtility.DisplayDialog(Title,
                    "The following meshes need Read/Write enabled to be baked:\n" + names + "\n\nEnable it now?",
                    "Enable", "Cancel"))
                return false;

            List<Mesh> failed = MeshReadabilityUtility.EnableReadWrite(unreadable);
            if (failed.Count == 0)
                return true;

            EditorUtility.DisplayDialog(Title,
                "Could not enable Read/Write for:\n" + string.Join("\n", failed.Select(m => "- " + m.name)), "OK");
            return false;
        }

        private static void ShowReport(BakeReport report)
        {
            if (report.SavedAssetPaths.Count > 0)
            {
                var first = AssetDatabase.LoadAssetAtPath<Texture2D>(report.SavedAssetPaths[0]);
                if (first != null)
                    EditorGUIUtility.PingObject(first);
            }

            if (report.Errors.Count == 0 && !report.Cancelled)
                return;

            string message = $"Saved {report.SavedAssetPaths.Count} texture(s) in {report.Elapsed.TotalSeconds:F1}s.";
            if (report.Cancelled)
                message += "\nCancelled.";
            if (report.Errors.Count > 0)
                message += "\n\nErrors:\n" + string.Join("\n", report.Errors);
            EditorUtility.DisplayDialog(Title, message, "OK");
        }

        /// <summary>
        /// Invokes the callback synchronously. <see cref="Progress{T}"/> posts to the synchronization
        /// context instead, which could re-open the progress bar after it has been cleared.
        /// </summary>
        private sealed class ImmediateProgress : IProgress<BakeProgress>
        {
            private readonly Action<BakeProgress> _callback;
            public ImmediateProgress(Action<BakeProgress> callback) => _callback = callback;
            public void Report(BakeProgress value) => _callback(value);
        }
    }
}
