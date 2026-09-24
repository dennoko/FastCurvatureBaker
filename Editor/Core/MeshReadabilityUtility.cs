using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DennokoWorks.Tool.FastCurvatureBaker
{
    /// <summary>Detects meshes without Read/Write access and enables it on their model importers.</summary>
    public static class MeshReadabilityUtility
    {
        public static List<Mesh> FindUnreadableMeshes(IEnumerable<Renderer> renderers)
        {
            var result = new List<Mesh>();
            foreach (var renderer in renderers)
            {
                Mesh mesh = SurfaceMesh.GetSharedMesh(renderer);
                if (mesh != null && !mesh.isReadable && !result.Contains(mesh))
                    result.Add(mesh);
            }
            return result;
        }

        /// <summary>
        /// Turns on Read/Write for every mesh that comes from a model file.
        /// Returns the meshes that could not be changed (e.g. meshes not imported from a model).
        /// </summary>
        public static List<Mesh> EnableReadWrite(IEnumerable<Mesh> meshes)
        {
            var failed = new List<Mesh>();
            var importers = new HashSet<ModelImporter>();
            foreach (var mesh in meshes)
            {
                string path = AssetDatabase.GetAssetPath(mesh);
                if (AssetImporter.GetAtPath(path) is ModelImporter importer)
                    importers.Add(importer);
                else
                    failed.Add(mesh);
            }

            foreach (var importer in importers)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
            return failed;
        }
    }
}
