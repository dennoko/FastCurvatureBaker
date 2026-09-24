using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>Resolves output locations and writes baked curvature maps as PNG assets.</summary>
    internal static class CurvatureTextureExporter
    {
        public const string OutputFolderName = "BakedCurvature";
        public const string FallbackFolder = "Assets/" + OutputFolderName;

        private static readonly string[] BaseTextureProperties = { "_MainTex", "_BaseMap", "_BaseColorMap" };

        /// <summary>
        /// <c>{base texture folder}/BakedCurvature</c> when the material has a base texture under Assets/,
        /// otherwise <see cref="FallbackFolder"/>.
        /// </summary>
        public static string ResolveOutputFolder(Material material)
        {
            Texture baseTexture = FindBaseTexture(material);
            if (baseTexture != null)
            {
                string texturePath = AssetDatabase.GetAssetPath(baseTexture);
                if (!string.IsNullOrEmpty(texturePath) && texturePath.StartsWith("Assets/"))
                {
                    string directory = Path.GetDirectoryName(texturePath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(directory))
                        return $"{directory}/{OutputFolderName}";
                }
            }
            return FallbackFolder;
        }

        /// <summary>
        /// Writes <paramref name="data"/> (curvature, coverage) as an 8-bit grayscale PNG and configures its importer.
        /// Returns the asset path.
        /// </summary>
        public static string Save(Vector2[] data, int resolution, CurvatureBakeSettings settings, string assetPath)
        {
            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            if (!settings.OverwriteExisting)
                assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);

            var pixels = new byte[resolution * resolution * 3];
            float background = settings.BackgroundValue;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i].y > 0.5f ? settings.MapValue(data[i].x) : background;
                byte b = (byte)Mathf.RoundToInt(v * 255f);
                pixels[i * 3] = b;
                pixels[i * 3 + 1] = b;
                pixels[i * 3 + 2] = b;
            }

            var texture = new Texture2D(resolution, resolution, TextureFormat.RGB24, false, true);
            try
            {
                texture.SetPixelData(pixels, 0);
                texture.Apply(false);
                File.WriteAllBytes(ToFullPath(assetPath), texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                importer.sRGBTexture = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.maxTextureSize = resolution;
                importer.SaveAndReimport();
            }
            return assetPath;
        }

        public static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Unnamed" : name.Trim();
        }

        private static Texture FindBaseTexture(Material material)
        {
            if (material == null || material.shader == null)
                return null;

            // Property flagged with [MainTexture] takes priority, then common base-color names.
            Shader shader = material.shader;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture &&
                    (shader.GetPropertyFlags(i) & ShaderPropertyFlags.MainTexture) != 0)
                {
                    Texture texture = material.GetTexture(shader.GetPropertyNameId(i));
                    if (texture != null) return texture;
                }
            }
            foreach (string property in BaseTextureProperties)
            {
                if (material.HasTexture(property))
                {
                    Texture texture = material.GetTexture(property);
                    if (texture != null) return texture;
                }
            }
            return null;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        private static string ToFullPath(string assetPath)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
