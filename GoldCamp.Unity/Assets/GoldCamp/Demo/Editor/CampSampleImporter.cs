using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GoldCamp.Demo.Editor
{
    public static class CampSampleImporter
    {
        public const string Root = "Assets/GoldCamp/Demo/Art/Sample01";
        public const string SetPath = "Assets/GoldCamp/Demo/Resources/GoldCampArtSample.asset";
        public static void Prepare()
        {
            if (!Directory.Exists(Root + "/Models")) return;
            Directory.CreateDirectory(Root + "/Materials");
            Directory.CreateDirectory(Root + "/Prefabs");
            Directory.CreateDirectory(Path.GetDirectoryName(SetPath));
            AssetDatabase.Refresh();
            var materials = new Material[4];
            for (int i = 0; i < 4; i++)
            {
                string texturePath = Root + "/Textures/Palette" + i + ".png";
                var textureImporter = (TextureImporter)AssetImporter.GetAtPath(texturePath);
                if (textureImporter == null) throw new IOException("Missing generated atlas " + texturePath);
                textureImporter.filterMode = FilterMode.Point; textureImporter.wrapMode = TextureWrapMode.Clamp;
                textureImporter.mipmapEnabled = false; textureImporter.textureCompression = TextureImporterCompression.Uncompressed;
                textureImporter.SaveAndReimport();
                string path = Root + "/Materials/Palette" + i + ".mat";
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material, path); }
                material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
                material.SetColor("_BaseColor", Color.white); material.SetFloat("_Smoothness", .23f); material.SetFloat("_Metallic", 0);
                EditorUtility.SetDirty(material); materials[i] = material;
            }
            var entries = Directory.GetFiles(Root + "/Models", "*.fbx").OrderBy(p => p, StringComparer.Ordinal).Select(path =>
            {
                path = path.Replace('\\', '/');
                var importer = (ModelImporter)AssetImporter.GetAtPath(path);
                importer.globalScale = 1; importer.useFileScale = true; importer.bakeAxisConversion = true;
                importer.importAnimation = false; importer.importCameras = false; importer.importLights = false;
                importer.addCollider = false; importer.isReadable = false; importer.importNormals = ModelImporterNormals.Import;
                importer.materialImportMode = ModelImporterMaterialImportMode.None; importer.SaveAndReimport();
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null) throw new IOException("FBX import failed: " + path);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
                try
                {
                    instance.name = Path.GetFileNameWithoutExtension(path);
                    foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                        renderer.sharedMaterials = Enumerable.Repeat(materials[0], renderer.sharedMaterials.Length).ToArray();
                    foreach (string variant in new[] { "VariantScarf", "VariantGoggles", "VariantLeaf" })
                        foreach (var t in instance.GetComponentsInChildren<Transform>(true)) if (t.name == variant) t.gameObject.SetActive(false);
                    string prefabPath = Root + "/Prefabs/" + instance.name + ".prefab";
                    return new CampArtSet.Entry { Key = instance.name, Prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath) };
                }
                finally { UnityEngine.Object.DestroyImmediate(instance); }
            }).ToArray();
            var set = AssetDatabase.LoadAssetAtPath<CampArtSet>(SetPath);
            if (set == null) { set = ScriptableObject.CreateInstance<CampArtSet>(); AssetDatabase.CreateAsset(set, SetPath); }
            set.Models = entries; set.MinerVariants = materials; EditorUtility.SetDirty(set); AssetDatabase.SaveAssets();
            Debug.Log("GOLDCAMP_ART_IMPORT_OK " + entries.Length + " imported models; four shared palette variants");
        }
    }
}
