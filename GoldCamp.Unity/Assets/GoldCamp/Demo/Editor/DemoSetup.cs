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
            EditorUtility.SetDirty(config);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("GoldCamp Demo");
            var session = root.AddComponent<CampSession>();
            session.Config = config;
            var hud = root.AddComponent<CampHud>(); hud.Session = session;
            hud.InterfaceFont = AssetDatabase.LoadAssetAtPath<Font>("Assets/GoldCamp/Demo/Fonts/NotoSansCJKsc-Regular.otf");
            root.AddComponent<CampSimulation>().Session = session;
            root.AddComponent<CampVehicleMotion>().Session = session;
            var world = root.AddComponent<CampWorld>(); world.Session = session;
            world.NameFont = hud.InterfaceFont;
            var controls = root.AddComponent<CampControls>(); controls.Session = session; controls.Hud = hud; hud.Controls = controls;
            var voice = root.AddComponent<CampVoice>(); voice.Session = session;
            var voicePanel = root.AddComponent<CampVoicePanel>(); voicePanel.Session = session; voicePanel.Voice = voice;
            root.AddComponent<CampSound>().Session = session;
            var display = root.AddComponent<CampDisplaySettings>(); display.Session = session; display.Controls = controls;
            root.AddComponent<CampPerformanceSample>().Session = session;
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(12, 13, -17);
            camera.transform.LookAt(Vector3.zero);
            camera.backgroundColor = new Color(.44f, .66f, .75f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.gameObject.AddComponent<AudioListener>();
            camera.nearClipPlane = .04f; camera.farClipPlane = 200; controls.Camera = camera;
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 2;
            sun.color = new Color(1f,.91f,.78f); sun.shadows = LightShadows.Soft; sun.shadowStrength = .65f;
            sun.transform.rotation = Quaternion.Euler(48, -35, 0);
            RenderSettings.ambientLight = new Color(.6f, .63f, .57f);
            world.Palette = new[] { Material("CampGround", new Color(.36f,.41f,.23f)), Material("Rock", new Color(.43f,.41f,.34f)),
                Material("Iron",new Color(.35f,.4f,.43f)), Material("Gold",new Color(1,.7f,.1f)), Material("Wood",new Color(.38f,.22f,.11f)),
                Material("Miner",new Color(.35f,.65f,.27f)), Material("Clothes",new Color(.15f,.19f,.19f)), Material("Hardhat",new Color(1,.75f,.12f)) };
            var hands=root.AddComponent<CampFirstPerson>();hands.Controls=controls;hands.Session=session;hands.Palette=world.Palette;
            var terrain=root.AddComponent<CampTerrain>();terrain.Session=session;terrain.Ground=world.Palette[0];
            terrain.Rock=world.Palette[1];terrain.Timber=world.Palette[4];terrain.Configure(config.Rules);
            Station("收购台", "station", "sale", CampAuthority.SalePoint, world.Palette[4]);
            Station("维修台", "station", "repair", CampMap.RepairPoint, world.Palette[2]);
            Station("原石加工", "station", "cutter", CampMap.CutterPoint, world.Palette[4]);
            Station("原石对赌", "station", "wager", CampMap.WagerPoint, world.Palette[4]);
            Station("公共修车", "station", "road", CampMap.VehicleMaintenancePoint, world.Palette[4]);
            for (int i=0;i<4;i++) Station("个人储物柜 " + (i+1), "cabinet", i.ToString(), CampMap.CabinetPoint(i) + Vector3.right * (i<2?-1f:1f), world.Palette[4]);
            CampArtBuilder.Build(hud.InterfaceFont);
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Demo scene could not be saved.");
            var others = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            others.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = others.ToArray();
            PlayerSettings.bundleVersion = "0.3.0-prototype";
            PlayerSettings.runInBackground = true;
            PlayerSettings.enableFrameTimingStats = true;
            AssetDatabase.SaveAssets();
            Debug.Log("GOLDCAMP_DEMO_GENERATE_OK " + ScenePath);
        }

        private static Material Material(string name, Color color)
        {
            string path = "Assets/GoldCamp/Demo/Settings/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material, path); }
            material.color = color; EditorUtility.SetDirty(material); return material;
        }
        private static GameObject Box(string name, Vector3 position, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            go.transform.position = position; go.transform.localScale = size; go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }
        private static void Station(string name, string kind, string id, Vector3 position, Material material)
        {
            var go = Box(name, position + Vector3.up * .55f, new Vector3(1.6f,1.1f,1), material);
            if (kind == "cabinet") go.transform.localScale = new Vector3(.8f,1.1f,.6f);
            var target = go.AddComponent<CampTarget>(); target.Kind = kind; target.Id = id;
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
            string licenses = Path.Combine(Path.GetDirectoryName(output), "Licenses");
            Directory.CreateDirectory(licenses);
            File.Copy(Path.Combine(Application.dataPath,"GoldCamp/Demo/Fonts/LICENSE.txt"),Path.Combine(licenses,"Noto-OFL.txt"),true);
            File.Copy(Path.Combine(Application.dataPath,"GoldCamp/Demo/Fonts/SOURCE.md"),Path.Combine(licenses,"Noto-source.md"),true);
            CopyPackageLicense("com.firstgeargames.fishnet", "LICENSE.txt", licenses, "FishNet-LICENSE.txt");
            CopyPackageLicense("com.firstgeargames.fishnet", "THIRD PARTY NOTICE.md", licenses, "FishNet-THIRD-PARTY-NOTICE.md");
            CopyPackageLicense("com.adrenak.concentus-unity", "LICENSE", licenses, "Concentus-LICENSE.txt");
            File.WriteAllText(Path.Combine(licenses, "Dependency-sources.txt"),
                "FishNet release 4.7.3, source commit 73f30cf2425dc808a4f463f0a233d386010810b3\n" +
                "https://github.com/FirstGearGames/FishNet/tree/73f30cf2425dc808a4f463f0a233d386010810b3\n" +
                "Concentus Unity 1.0.1, source commit f1564332c43ddf36f9129c5fca51d73c7b47d1fb\n" +
                "https://github.com/adrenak/concentus-unity/tree/f1564332c43ddf36f9129c5fca51d73c7b47d1fb\n");
            Debug.Log("GOLDCAMP_DEMO_BUILD_OK " + output + "; bytes=" + report.summary.totalSize);
        }
        private static void CopyPackageLicense(string packageName, string file, string output, string name)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + packageName);
            if (package == null) throw new InvalidOperationException("Missing resolved dependency " + packageName);
            File.Copy(Path.Combine(package.resolvedPath, file), Path.Combine(output, name), true);
        }
    }
}
