using System;
using System.IO;
using GoldCamp.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GoldCamp.Editor
{
    public static class M0Setup
    {
        public const string ScenePath = "Assets/GoldCamp/Scenes/M0_Graybox.unity";
        const string Root = "Assets/GoldCamp";

        [MenuItem("GoldCamp/M0/Generate Graybox")]
        public static void CreateScene()
        {
            if (Application.unityVersion != "6000.3.23f1")
                throw new InvalidOperationException("GoldCamp requires Unity 6000.3.23f1.");
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            foreach (string folder in new[] { "Scenes", "Materials", "Prefabs", "Settings" })
                Directory.CreateDirectory($"{Root}/{folder}");
            AssetDatabase.Refresh();
            EditorSettings.serializationMode = SerializationMode.ForceText;
            UnityEditor.VersionControlSettings.mode = "Visible Meta Files";
            ConfigureRendering();
            PlayerSettings.companyName = "GoldCamp";
            PlayerSettings.productName = "GoldCamp";
            PlayerSettings.bundleVersion = "0.1.0-m0";
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.runInBackground = false;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            // Use Unity's supported serialized editor settings API for the legacy input backend.
            var playerSettings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var inputHandler = playerSettings.FindProperty("activeInputHandler");
            if (inputHandler != null) { inputHandler.intValue = 0; playerSettings.ApplyModifiedPropertiesWithoutUndo(); }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.56f, .64f, .73f);
            RenderSettings.fog = false;
            var ground = Material("Ground", new Color(.23f, .30f, .32f));
            var wall = Material("Wall", new Color(.42f, .49f, .52f));
            var wood = Material("Obstacles", new Color(.54f, .38f, .23f));
            var gold = Material("Gold", new Color(1f, .71f, .08f));
            Cube("Ground", new Vector3(0, -.5f, 2), new Vector3(24, 1, 24), ground);
            Cube("Wall North", new Vector3(0, 1.5f, 14), new Vector3(24, 3, .5f), wall);
            Cube("Wall West", new Vector3(-12, 1.5f, 2), new Vector3(.5f, 3, 24), wall);
            Cube("Wall East", new Vector3(12, 1.5f, 2), new Vector3(.5f, 3, 24), wall);
            Cube("Wall South", new Vector3(0, 1.5f, -10), new Vector3(24, 3, .5f), wall);
            Cube("Low Step", new Vector3(-3, .15f, 1), new Vector3(2, .3f, 2), wood);
            Cube("Jump Block", new Vector3(-3, .45f, 4), new Vector3(2, .9f, 2), wood);
            Cube("Tall Obstacle", new Vector3(4, 1, 3), new Vector3(2, 2, 3), wood);
            Cube("Ore Plinth", new Vector3(0, .3f, 0), new Vector3(1.5f, .6f, 1.5f), wood);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(48, -35, 0);

            var player = new GameObject("Player");
            player.transform.position = new Vector3(0, .1f, -3);
            var capsule = player.AddComponent<CharacterController>();
            capsule.height = 1.8f;
            capsule.radius = .35f;
            capsule.center = new Vector3(0, .9f, 0);
            capsule.stepOffset = .3f;
            capsule.slopeLimit = 50;
            capsule.skinWidth = .035f;
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.SetParent(player.transform, false);
            camera.transform.localPosition = new Vector3(0, 1.62f, 0);
            camera.nearClipPlane = .05f;
            camera.farClipPlane = 150;
            camera.fieldOfView = 75;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.23f, .36f, .49f);
            camera.gameObject.AddComponent<AudioListener>();
            camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
            var hold = new GameObject("Hold Point").transform;
            hold.SetParent(camera.transform, false);
            hold.localPosition = new Vector3(0, -.15f, 1.35f);
            var input = player.AddComponent<M0Input>();
            var controller = player.AddComponent<FirstPersonController>();
            controller.View = camera;
            controller.HoldPoint = hold;
            controller.SpawnPosition = player.transform.position;
            var carrier = player.AddComponent<OreCarrier>();
            carrier.View = camera;
            carrier.HoldPoint = hold;
            carrier.Reach = 3;
            var hud = player.AddComponent<M0Hud>();
            hud.Controller = controller;
            hud.Carrier = carrier;
            hud.Input = input;
            var ore = Cube("Ore", new Vector3(0, 1, 0), Vector3.one * .55f, gold);
            var body = ore.AddComponent<Rigidbody>();
            body.mass = 2;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            controller.ResetOre = ore.AddComponent<CarryableOre>();
            PrefabUtility.SaveAsPrefabAsset(ore, $"{Root}/Prefabs/Ore.prefab", out bool oreSaved);
            // The reusable player prefab has no external scene references; bind ResetOre in each scene.
            var sceneOre = controller.ResetOre;
            controller.ResetOre = null;
            PrefabUtility.SaveAsPrefabAsset(player, $"{Root}/Prefabs/Player.prefab", out bool playerSaved);
            controller.ResetOre = sceneOre;
            if (!oreSaved || !playerSaved) throw new IOException("Could not save M0 prefabs.");
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Could not save graybox.");
            AssetDatabase.SaveAssets();
            Debug.Log("GOLDCAMP_M0_GENERATED: " + ScenePath);
        }

        static void ConfigureRendering()
        {
            string rendererPath = $"{Root}/Settings/M0_Renderer.asset";
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }
            string pipelinePath = $"{Root}/Settings/M0_URP.asset";
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }
            pipeline.msaaSampleCount = 2;
            pipeline.shadowDistance = 40;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            EditorUtility.SetDirty(pipeline);
        }

        static Material Material(string name, Color color)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) throw new InvalidOperationException("URP Lit shader is not available.");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", .22f);
            EditorUtility.SetDirty(material);
            return material;
        }

        static GameObject Cube(string name, Vector3 position, Vector3 scale, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = position;
            cube.transform.localScale = scale;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            return cube;
        }
    }
}
