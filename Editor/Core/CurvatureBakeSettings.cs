using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>Which surface signal the curvature is estimated from.</summary>
    public enum CurvatureSource
    {
        /// <summary>Uses the mesh's shading normals. Smooth result that follows the shading of low-poly meshes.</summary>
        ShadingNormals = 0,
        /// <summary>
        /// Uses positions and triangle (face) normals; the shading normals only decide which edges are hard.
        /// Picks up every polygon crease (high-poly / hard-surface meshes).
        /// </summary>
        Geometry = 1,
    }

    /// <summary>What the baked texture represents.</summary>
    public enum CurvatureBakeMode
    {
        /// <summary>Signed curvature: 0.5 = flat, brighter = convex, darker = concave.</summary>
        Default = 0,
        /// <summary>Convex emphasis: white on convex edges, black elsewhere (edge wear / scratch mask).</summary>
        Convex = 1,
        /// <summary>Concave emphasis: white in concave creases, black elsewhere (dirt / grime mask).</summary>
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

        /// <summary>Normal-dot range over which rejected samples fade back in (RejectionBand in the shader).</summary>
        public const float NormalRejectionBand = 0.25f;

        /// <summary>
        /// Highest <see cref="NormalRejection"/> at which samples facing exactly like the texel still get
        /// full weight; above it the fade would end beyond a dot product of 1 and remove the smooth term.
        /// </summary>
        public const float MaxNormalRejection = 1f - NormalRejectionBand;

        [Tooltip("Output texture resolution.")]
        public int Resolution = 2048;

        [Tooltip("UV channel used for baking (0-7).")]
        public int UVChannel = 0;

        [FormerlySerializedAs("OutputMode")]
        public CurvatureBakeMode Mode = CurvatureBakeMode.Default;

        [Tooltip("World-space radius used to measure curvature of smooth surfaces. Hard edges use Edge Width instead.")]
        public float Radius = 0.01f;

        [Tooltip("World-space distance from a hard edge (split normals) over which the edge is treated as convex/concave.")]
        public float EdgeWidth = 0.003f;

        [Tooltip("Intensity of hard edges. A 90-degree edge reaches full intensity at 1.")]
        public float EdgeStrength = 1f;

        [Tooltip("Output intensity. A sphere whose radius equals Radius reaches full intensity at 1.")]
        public float Strength = 1f;

        public CurvatureSource Source = CurvatureSource.ShadingNormals;

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
        public bool OverwriteExisting = false;

        public CurvatureBakeSettings Clone() => (CurvatureBakeSettings)MemberwiseClone();

        /// <summary>Clamps every value into its supported range.</summary>
        public void Validate()
        {
            Resolution = SnapResolution(Resolution);
            UVChannel = Mathf.Clamp(UVChannel, 0, 7);
            Radius = Mathf.Max(Radius, 1e-5f);
            Strength = Mathf.Max(Strength, 0f);
            EdgeWidth = Mathf.Max(EdgeWidth, 1e-5f);
            EdgeStrength = Mathf.Max(EdgeStrength, 0f);
            NormalRejection = Mathf.Clamp(NormalRejection, -1f, MaxNormalRejection);
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
        public float BackgroundValue => Mode == CurvatureBakeMode.Default ? 0.5f : 0f;

        /// <summary>File name suffix, so the three modes can be baked side by side.</summary>
        public string FileSuffix
        {
            get
            {
                switch (Mode)
                {
                    case CurvatureBakeMode.Convex: return "_Convex";
                    case CurvatureBakeMode.Concave: return "_Concave";
                    default: return "_Curvature";
                }
            }
        }

        /// <summary>
        /// Maps a signed curvature value (+1 = sphere of radius Radius or a 90-degree convex edge,
        /// strengths already applied) to the [0, 1] output range.
        /// </summary>
        public float MapValue(float curvature)
        {
            switch (Mode)
            {
                case CurvatureBakeMode.Convex: return Mathf.Clamp01(curvature);
                case CurvatureBakeMode.Concave: return Mathf.Clamp01(-curvature);
                default: return Mathf.Clamp01(0.5f + 0.5f * curvature);
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
