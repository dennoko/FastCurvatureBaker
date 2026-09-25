using System;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>Which surface signal the curvature is estimated from.</summary>
    public enum CurvatureSource
    {
        /// <summary>Uses the mesh's shading normals. Smooth result that follows the shading of low-poly meshes.</summary>
        ShadingNormals = 0,
        /// <summary>Uses vertex positions only. Picks up every polygon crease (high-poly / hard-surface meshes).</summary>
        Geometry = 1,
    }

    public enum CurvatureOutputMode
    {
        /// <summary>0.5 = flat, brighter = convex, darker = concave.</summary>
        Combined = 0,
        /// <summary>Convex areas only (edge highlight mask).</summary>
        Convex = 1,
        /// <summary>Concave areas only (cavity mask).</summary>
        Concave = 2,
    }

    public enum BakeQuality
    {
        Draft = 0,
        Standard = 1,
        High = 2,
        Ultra = 3,
    }

    [Serializable]
    public class CurvatureBakeSettings
    {
        public static readonly int[] SupportedResolutions = { 256, 512, 1024, 2048, 4096 };

        public const int MaxInternalResolution = 4096;

        [Tooltip("Output texture resolution.")]
        public int Resolution = 2048;

        [Tooltip("UV channel used for baking (0-7).")]
        public int UVChannel = 0;

        [Tooltip("World-space radius used to measure curvature. Controls the width of edge highlights.")]
        public float Radius = 0.01f;

        [Tooltip("Output intensity. A sphere whose radius equals Radius reaches full intensity at 1.")]
        public float Strength = 1f;

        public CurvatureSource Source = CurvatureSource.ShadingNormals;

        public CurvatureOutputMode OutputMode = CurvatureOutputMode.Combined;

        public BakeQuality Quality = BakeQuality.Standard;

        [Tooltip("Evaluate at 2x resolution and average down (skipped when the output is already 4096).")]
        public bool Supersample = true;

        [Tooltip("Only use surface samples from the same connected mesh part as the texel.")]
        public bool SameComponentOnly = true;

        [Tooltip("Ignore neighbouring surfaces whose normal dot product with the texel normal is at or below this value. Their weight fades back in over the next 0.25.")]
        public float NormalRejection = -0.5f;

        [Tooltip("Number of 3x3 blur passes applied after evaluation.")]
        public int BlurPasses = 0;

        [Tooltip("Pixels to extend the result outward from UV island borders.")]
        public int DilationPixels = 16;

        [Tooltip("Overwrite existing files with the same name. When off, a numbered file is created.")]
        public bool OverwriteExisting = true;

        public CurvatureBakeSettings Clone() => (CurvatureBakeSettings)MemberwiseClone();

        /// <summary>Clamps every value into its supported range.</summary>
        public void Validate()
        {
            Resolution = SnapResolution(Resolution);
            UVChannel = Mathf.Clamp(UVChannel, 0, 7);
            Radius = Mathf.Max(Radius, 1e-5f);
            Strength = Mathf.Max(Strength, 0f);
            NormalRejection = Mathf.Clamp(NormalRejection, -1f, 1f);
            BlurPasses = Mathf.Clamp(BlurPasses, 0, 16);
            DilationPixels = Mathf.Clamp(DilationPixels, 0, 64);
        }

        /// <summary>Supersampling factor actually used for the current resolution.</summary>
        public int SupersampleFactor =>
            Supersample && Resolution * 2 <= MaxInternalResolution ? 2 : 1;

        public int SamplesPerRadius
        {
            get
            {
                switch (Quality)
                {
                    case BakeQuality.Draft: return 4;
                    case BakeQuality.High: return 8;
                    case BakeQuality.Ultra: return 12;
                    default: return 6;
                }
            }
        }

        /// <summary>Background value written outside UV islands.</summary>
        public float BackgroundValue => OutputMode == CurvatureOutputMode.Combined ? 0.5f : 0f;

        /// <summary>Maps a dimensionless curvature value (H * radius) to the [0, 1] output range.</summary>
        public float MapValue(float curvature)
        {
            float v = curvature * Strength;
            switch (OutputMode)
            {
                case CurvatureOutputMode.Convex: return Mathf.Clamp01(v);
                case CurvatureOutputMode.Concave: return Mathf.Clamp01(-v);
                default: return Mathf.Clamp01(0.5f + 0.5f * v);
            }
        }

        private static int SnapResolution(int value)
        {
            int best = SupportedResolutions[0];
            foreach (int r in SupportedResolutions)
            {
                if (Mathf.Abs(r - value) < Mathf.Abs(best - value))
                    best = r;
            }
            return best;
        }
    }
}
