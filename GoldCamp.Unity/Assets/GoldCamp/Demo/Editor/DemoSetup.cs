using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GoldCamp.Demo.Editor
{
    public static class DemoSetup
    {
        public const string ScenePath = "Assets/GoldCamp/Demo/Scenes/Demo_Camp.unity";
        private const string ConfigPath = "Assets/GoldCamp/Demo/Settings/DemoConfig.asset";

        [MenuItem("GoldCamp/Demo/Generate Camp")]
        public static void Generate()
        {
            if (Application.unityVersion != "6000.3.23f1") throw new InvalidOperationException("GoldCamp requires Unity 6000.3.23f1.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            AssetDatabase.Refresh();
            var config = AssetDatabase.LoadAssetAtPath<DemoConfig>(ConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<DemoConfig>();
                AssetDatabase.CreateAsset(config, ConfigPath);
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("GoldCamp Demo");
            var session = root.AddComponent<CampSession>();
            session.Config = config;
            root.AddComponent<CampHud>().Session = session;
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(12, 13, -17);
            camera.transform.LookAt(Vector3.zero);
            camera.backgroundColor = new Color(.44f, .66f, .75f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.gameObject.AddComponent<AudioListener>();
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 2;
            sun.transform.rotation = Quaternion.Euler(48, -35, 0);
            RenderSettings.ambientLight = new Color(.6f, .63f, .57f);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Camp foundation";
            ground.transform.position = new Vector3(0, -.5f, 0);
            ground.transform.localScale = new Vector3(40, 1, 40);
            // Persistent material asset, generated idempotently alongside the scene.
            const string matPath = "Assets/GoldCamp/Demo/Settings/CampGround.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, matPath);
            }
            material.color = new Color(.37f, .42f, .21f);
            ground.GetComponent<Renderer>().sharedMaterial = material;
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Demo scene could not be saved.");
            var others = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            others.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = others.ToArray();
            PlayerSettings.bundleVersion = "0.3.0-prototype";
            PlayerSettings.runInBackground = true;
            AssetDatabase.SaveAssets();
            Debug.Log("GOLDCAMP_DEMO_GENERATE_OK " + ScenePath);
        }

        [MenuItem("GoldCamp/Demo/Build Windows")]
        public static void BuildWindows()
        {
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Builds/DemoPrototype/Windows/GoldCamp.exe"));
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = output,
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Demo Windows build failed: " + report.summary.result + "; errors=" + report.summary.totalErrors);
            Debug.Log("GOLDCAMP_DEMO_BUILD_OK " + output + "; bytes=" + report.summary.totalSize);
        }
    }
}
