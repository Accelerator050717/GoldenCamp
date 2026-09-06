using System.Collections;
using System.Linq;
using GoldCamp.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace GoldCamp.Tests
{
    public sealed class M0ScenePlayTests
    {
        private const string ScenePath = "Assets/GoldCamp/Scenes/M0_Graybox.unity";
        private Scene loadedScene;
        private Scene previousScene;
        private float previousCaptureDeltaTime;
        private bool timingCaptured;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = 1f;
            if (timingCaptured)
                Time.captureDeltaTime = previousCaptureDeltaTime;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (previousScene.IsValid() && previousScene.isLoaded)
                SceneManager.SetActiveScene(previousScene);
            if (loadedScene.IsValid() && loadedScene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(loadedScene);
        }

        [UnityTest]
        public IEnumerator ActualBuildSceneLoadsWithRequiredRuntimeReferences()
        {
            previousScene = SceneManager.GetActiveScene();
            previousCaptureDeltaTime = Time.captureDeltaTime;
            timingCaptured = true;
            // Headless no-throttling can produce sub-millisecond Update steps. Exercise
            // the authored controller at a deterministic normal game frame duration.
            Time.captureDeltaTime = 1f / 60f;
            int buildIndex = SceneUtility.GetBuildIndexByScenePath(ScenePath);
            Assert.That(buildIndex, Is.GreaterThanOrEqualTo(0), "Generated scene must be in enabled Build Settings.");
            // The test runner can retain a backup of the already-open graybox. Keep
            // this actual scene's physics independent from that duplicate geometry.
            var parameters = new LoadSceneParameters(LoadSceneMode.Additive, LocalPhysicsMode.Physics3D);
            var operation = SceneManager.LoadSceneAsync(buildIndex, parameters);
            Assert.That(operation, Is.Not.Null);
            yield return operation;
            loadedScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
            Assert.That(loadedScene.path, Is.EqualTo(ScenePath));
            Assert.That(loadedScene.isLoaded, Is.True);
            Assert.That(SceneManager.SetActiveScene(loadedScene), Is.True);
            var roots = loadedScene.GetRootGameObjects();
            foreach (string expected in new[] { "Player", "Ground", "Sun", "Ore" })
                Assert.That(roots.Count(root => root.name == expected), Is.EqualTo(1), expected);
            var players = roots.SelectMany(root => root.GetComponentsInChildren<FirstPersonController>(true)).ToArray();
            var ores = roots.SelectMany(root => root.GetComponentsInChildren<CarryableOre>(true)).ToArray();
            Assert.That(players, Has.Length.EqualTo(1));
            Assert.That(ores, Has.Length.EqualTo(1));
            var player = players[0];
            Assert.That(player.View, Is.Not.Null);
            Assert.That(player.HoldPoint, Is.Not.Null);
            Assert.That(player.ResetOre, Is.SameAs(ores[0]));
            var controller = player.GetComponent<CharacterController>();
            var input = player.GetComponent<M0Input>();
            Assert.That(controller, Is.Not.Null);
            Assert.That(input, Is.Not.Null);
            Assert.That(input.IsPaused, Is.False, "The loaded game must start unpaused.");
            var carrier = player.GetComponent<OreCarrier>();
            Assert.That(carrier, Is.Not.Null);
            Assert.That(carrier.View, Is.SameAs(player.View));
            Assert.That(carrier.HoldPoint, Is.SameAs(player.HoldPoint));
            Assert.That(ores[0].Body, Is.Not.Null);
            Assert.That(ores[0].GetComponent<Collider>().enabled, Is.True);
            Assert.That(roots.SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true)).Any(component => component == null), Is.False);
            var ground = roots.Single(root => root.name == "Ground").GetComponent<Collider>();
            Assert.That(ground, Is.Not.Null);
            Assert.That(ground.enabled && !ground.isTrigger, Is.True);
            var physicsScene = loadedScene.GetPhysicsScene();
            Assert.That(physicsScene.IsValid(), Is.True);
            float elapsed = 0f;
            int updateFrames = 0;
            Physics.SyncTransforms();
            while (!controller.isGrounded && elapsed < 2f && updateFrames < 180)
            {
                // CharacterController.Move runs in Update, not FixedUpdate. Local
                // physics scenes also require an explicit simulation step.
                yield return null;
                if (Time.deltaTime > 0f)
                    physicsScene.Simulate(Time.deltaTime);
                elapsed += Time.deltaTime;
                updateFrames++;
            }
            string diagnostic = $"y={player.transform.position.y}, feet={controller.bounds.min.y}, " +
                $"ground={ground.bounds.max.y}, paused={input.IsPaused}, UpdateFrames={updateFrames}, elapsed={elapsed}";
            Assert.That(controller.isGrounded, Is.True, "The actual scene player must settle onto the floor. " + diagnostic);
            Assert.That(Mathf.Abs(controller.bounds.min.y - ground.bounds.max.y),
                Is.LessThanOrEqualTo(controller.skinWidth + 0.02f), "Grounded player must actually be on the authored floor. " + diagnostic);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
