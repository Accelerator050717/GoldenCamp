using System.Linq;
using GoldCamp.Runtime;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GoldCamp.Tests
{
    public sealed class M0SceneTests
    {
        private const string ScenePath = "Assets/GoldCamp/Scenes/M0_Graybox.unity";

        [Test]
        public void GeneratedSceneIsAnEnabledBuildSceneExactlyOnce()
        {
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath), Is.Not.Null,
                "Run the actual Generate entry point before tests.");
            Assert.That(EditorBuildSettings.scenes.Count(scene => scene.path == ScenePath), Is.EqualTo(1));
            Assert.That(EditorBuildSettings.scenes.Single(scene => scene.path == ScenePath).enabled, Is.True);
            Assert.That(GraphicsSettings.defaultRenderPipeline, Is.Not.Null, "M0 requires a configured URP asset.");
            Assert.That(GraphicsSettings.defaultRenderPipeline.GetType().Name, Does.Contain("UniversalRenderPipeline"));
        }

        [Test]
        public void GeneratedSceneHasOnePlayerCameraOreAndCompleteReferences()
        {
            var scene = SceneManager.GetSceneByPath(ScenePath);
            bool openedByTest = !scene.IsValid() || !scene.isLoaded;
            if (openedByTest)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);

            try
            {
                var roots = scene.GetRootGameObjects();
                foreach (string expectedName in new[] { "Player", "Ground", "Sun", "Ore" })
                    Assert.That(roots.Count(root => root.name == expectedName), Is.EqualTo(1), expectedName);

                var players = roots.SelectMany(root => root.GetComponentsInChildren<FirstPersonController>(true)).ToArray();
                var cameras = roots.SelectMany(root => root.GetComponentsInChildren<Camera>(true)).ToArray();
                var ores = roots.SelectMany(root => root.GetComponentsInChildren<CarryableOre>(true)).ToArray();
                var carriers = roots.SelectMany(root => root.GetComponentsInChildren<OreCarrier>(true)).ToArray();
                Assert.That(players, Has.Length.EqualTo(1));
                Assert.That(cameras, Has.Length.EqualTo(1));
                Assert.That(ores, Has.Length.EqualTo(1));
                Assert.That(carriers, Has.Length.EqualTo(1));
                Assert.That(cameras[0].CompareTag("MainCamera"), Is.True);
                Assert.That(players[0].View, Is.SameAs(cameras[0]));
                Assert.That(players[0].HoldPoint, Is.Not.Null);
                Assert.That(players[0].ResetOre, Is.SameAs(ores[0]));
                Assert.That(carriers[0].View, Is.SameAs(cameras[0]));
                Assert.That(carriers[0].HoldPoint, Is.SameAs(players[0].HoldPoint));
                Assert.That(ores[0].GetComponent<Rigidbody>(), Is.Not.Null);
                Assert.That(ores[0].GetComponent<Collider>(), Is.Not.Null);
                Assert.That(roots.SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true)).Any(component => component == null),
                    Is.False, "Generated scene contains a missing script.");
            }
            finally
            {
                if (openedByTest)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
