using System.Collections;
using GoldCamp.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace GoldCamp.Tests
{
    public sealed class OreInteractionTests
    {
        private readonly Vector3 origin = new Vector3(4000f, 200f, 4000f);
        private Scene testScene;
        private Scene previousScene;
        private OreCarrier carrier;
        private CarryableOre ore;
        private Vector3 oreSpawn;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            Time.timeScale = 1f;
            previousScene = SceneManager.GetActiveScene();
            testScene = SceneManager.CreateScene("OreInteractionTests");
            SceneManager.SetActiveScene(testScene);
            CreateBox("TestGround", origin + Vector3.down * 0.5f, new Vector3(20f, 1f, 20f));
            carrier = CreateCarrier("TestPlayer");
            oreSpawn = carrier.View.transform.position + Vector3.forward * 2f;
            ore = CreateOre("TestOre", oreSpawn);
            Physics.SyncTransforms();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (carrier != null && carrier.Input != null)
                carrier.Input.SetPaused(false);
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (previousScene.IsValid() && previousScene.isLoaded)
                SceneManager.SetActiveScene(previousScene);
            if (testScene.IsValid() && testScene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(testScene);
        }

        [UnityTest]
        public IEnumerator PickupMaintainsOneOwnerAndStableHeldPhysics()
        {
            Assert.That(carrier.CanPickup(ore), Is.True);
            Assert.That(carrier.TryPickup(ore), Is.True);
            AssertHeld();
            Assert.That(carrier.TryPickup(ore), Is.False, "Repeated pickup must not create a second claim.");

            var otherCarrier = CreateCarrier("OtherPlayer");
            Assert.That(otherCarrier.TryPickup(ore), Is.False);
            Assert.That(otherCarrier.HeldOre, Is.Null);
            var anotherOre = CreateOre("AnotherOre", carrier.View.transform.position + Vector3.right * 2f);
            Assert.That(carrier.TryPickup(anotherOre), Is.False);
            Assert.That(anotherOre.HeldBy, Is.Null);

            carrier.transform.position += Vector3.right * 0.25f;
            carrier.View.transform.rotation = Quaternion.Euler(10f, 15f, 0f);
            yield return new WaitForFixedUpdate();
            yield return null;
            AssertHeld();
            Assert.That(Vector3.Distance(ore.transform.position, carrier.HoldPoint.position), Is.LessThan(0.05f));
        }

        [UnityTest]
        public IEnumerator DropClearsBothOwnersAndRestoresGravityAndCollision()
        {
            Assert.That(carrier.TryPickup(ore), Is.True);
            Assert.That(carrier.Drop(), Is.True);
            AssertReleased();
            Assert.That(carrier.Drop(), Is.False);
            float releasedHeight = ore.transform.position.y;
            for (int step = 0; step < 8; step++)
                yield return new WaitForFixedUpdate();
            Assert.That(ore.transform.position.y, Is.LessThan(releasedHeight - 0.01f),
                "A dropped ore must actually fall after fixed physics updates.");
        }

        [UnityTest]
        public IEnumerator ThrowReleasesOnceAndMovesForward()
        {
            Assert.That(carrier.TryPickup(ore), Is.True);
            Assert.That(carrier.Throw(), Is.True);
            AssertReleased();
            Vector3 firstVelocity = ore.Body.linearVelocity;
            Assert.That(Vector3.Dot(firstVelocity, carrier.View.transform.forward), Is.GreaterThan(0f));
            Assert.That(carrier.Throw(), Is.False);
            Assert.That(ore.Body.linearVelocity, Is.EqualTo(firstVelocity));
            Vector3 releasedPosition = ore.transform.position;
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            Assert.That(Vector3.Dot(ore.transform.position - releasedPosition, carrier.View.transform.forward), Is.GreaterThan(0.01f));
        }

        [UnityTest]
        public IEnumerator InvalidTargetsCannotBePickedUpThroughDistanceOrWalls()
        {
            Assert.That(carrier.TryPickup(null), Is.False);
            ore.transform.position = carrier.View.transform.position + Vector3.forward * (carrier.Reach + 1f);
            Physics.SyncTransforms();
            Assert.That(carrier.CanPickup(ore), Is.False, "Out of reach.");
            Assert.That(carrier.TryPickup(ore), Is.False);

            ore.transform.position = carrier.View.transform.position + Vector3.forward * 2f;
            var wall = CreateBox("OccludingWall", carrier.View.transform.position + Vector3.forward,
                new Vector3(2f, 2f, 0.25f));
            Physics.SyncTransforms();
            Assert.That(carrier.CanPickup(ore), Is.False, "A nearer wall must block the interaction ray.");
            Assert.That(carrier.TryPickup(ore), Is.False);
            Object.Destroy(wall);
            yield return null;

            ore.gameObject.SetActive(false);
            Physics.SyncTransforms();
            Assert.That(carrier.TryPickup(ore), Is.False, "Inactive target.");
            ore.gameObject.SetActive(true);
            ore.transform.position = carrier.View.transform.position + Vector3.right * 2f;
            Physics.SyncTransforms();
            Assert.That(carrier.TryPickup(ore), Is.False, "Nearby but not in the camera ray.");
            Assert.That(carrier.HeldOre, Is.Null);
            Assert.That(ore.HeldBy, Is.Null);

            // The ore's surface is in ray range while its center is still out of pickup range.
            // This is the boundary visible from the graybox's initial player position.
            ore.Body.useGravity = false;
            ore.Body.linearVelocity = Vector3.zero;
            ore.transform.position = carrier.View.transform.position
                + carrier.View.transform.forward * (carrier.Reach + 0.15f);
            Physics.SyncTransforms();
            var aim = new Ray(carrier.View.transform.position, carrier.View.transform.forward);
            Assert.That(ore.GetComponent<Collider>().Raycast(aim, out RaycastHit surfaceHit, carrier.Reach), Is.True);
            Assert.That(surfaceHit.distance, Is.LessThan(carrier.Reach));
            Assert.That(Vector3.Distance(carrier.View.transform.position, ore.transform.position),
                Is.GreaterThan(carrier.Reach));
            yield return null;
            Assert.That(carrier.FocusedOre, Is.Null, "The HUD must not offer an out-of-range pickup.");
            Assert.That(carrier.CanPickup(ore), Is.False);
            Assert.That(carrier.TryPickup(ore), Is.False);

            ore.transform.position = carrier.View.transform.position
                + carrier.View.transform.forward * (carrier.Reach - 0.15f);
            Physics.SyncTransforms();
            yield return null;
            Assert.That(carrier.FocusedOre, Is.SameAs(ore), "Entering pickup range must enable the prompt.");
            Assert.That(carrier.CanPickup(ore), Is.True);
            Assert.That(carrier.TryPickup(ore), Is.True);
            AssertHeld();
            ore.ResetToSpawn();
        }

        [UnityTest]
        public IEnumerator BlockedDropAndThrowPreserveTheHeldOre()
        {
            Assert.That(carrier.TryPickup(ore), Is.True);
            var obstruction = CreateBox("NoReleaseSpace", carrier.View.transform.position + Vector3.forward,
                new Vector3(8f, 8f, 8f));
            Physics.SyncTransforms();
            Assert.That(carrier.Drop(), Is.False);
            AssertHeld();
            Assert.That(carrier.Throw(), Is.False);
            AssertHeld();
            Object.Destroy(obstruction);
            yield return null;
            Physics.SyncTransforms();
            Assert.That(carrier.Drop(), Is.True, "Removing the obstruction must make release possible again.");
            AssertReleased();
        }

        [UnityTest]
        public IEnumerator CloseWallReleaseNeverEmbedsOreInWallOrPlayer()
        {
            Assert.That(carrier.TryPickup(ore), Is.True);
            var wall = CreateBox("CloseWall", carrier.View.transform.position + Vector3.forward * 1.2f,
                new Vector3(3f, 3f, 0.4f));
            Physics.SyncTransforms();
            if (carrier.Drop())
            {
                AssertReleased();
                AssertNoPenetration(ore.GetComponent<Collider>(), wall.GetComponent<Collider>());
                AssertNoPenetration(ore.GetComponent<Collider>(), carrier.GetComponent<CharacterController>());
            }
            else
            {
                AssertHeld();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator PauseBlocksPickupDropAndThrowUntilResume()
        {
            carrier.Input.SetPaused(true);
            Assert.That(carrier.CanPickup(ore), Is.False);
            Assert.That(carrier.TryPickup(ore), Is.False);
            carrier.Input.SetPaused(false);
            Assert.That(carrier.TryPickup(ore), Is.True);
            carrier.Input.SetPaused(true);
            Assert.That(carrier.Drop(), Is.False);
            Assert.That(carrier.Throw(), Is.False);
            AssertHeld();
            carrier.Input.SetPaused(false);
            Assert.That(carrier.Drop(), Is.True);
            AssertReleased();
            yield return null;
        }

        [UnityTest]
        public IEnumerator PlayerResetClearsHeldOwnershipAndReturnsOreToSpawn()
        {
            var player = carrier.gameObject.AddComponent<FirstPersonController>();
            player.enabled = false;
            player.View = carrier.View;
            player.HoldPoint = carrier.HoldPoint;
            player.SpawnPosition = origin;
            player.ResetOre = ore;
            Assert.That(carrier.TryPickup(ore), Is.True);
            carrier.transform.SetPositionAndRotation(origin + Vector3.right * 4f, Quaternion.Euler(0f, 90f, 0f));
            player.ResetPlayer();
            AssertReleased();
            Assert.That(Vector3.Distance(carrier.transform.position, origin), Is.LessThan(0.001f));
            Assert.That(Vector3.Distance(ore.transform.position, oreSpawn), Is.LessThan(0.001f));
            Assert.That(ore.Body.linearVelocity.sqrMagnitude, Is.LessThan(0.001f));
            Assert.That(ore.Body.angularVelocity.sqrMagnitude, Is.LessThan(0.001f));
            Physics.SyncTransforms();
            Assert.That(carrier.TryPickup(ore), Is.True, "Reset must leave the ore available for another pickup.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator PlayerResetAlsoRecoversAFreeThrownOre()
        {
            var player = carrier.gameObject.AddComponent<FirstPersonController>();
            player.enabled = false;
            player.View = carrier.View;
            player.HoldPoint = carrier.HoldPoint;
            player.SpawnPosition = origin;
            player.ResetOre = ore;
            Assert.That(carrier.TryPickup(ore), Is.True);
            Assert.That(carrier.Throw(), Is.True);
            ore.transform.position += Vector3.right * 4f;
            player.ResetPlayer();
            AssertReleased();
            Assert.That(Vector3.Distance(ore.transform.position, oreSpawn), Is.LessThan(0.001f));
            Assert.That(ore.Body.linearVelocity.sqrMagnitude, Is.LessThan(0.001f));
            Assert.That(ore.Body.angularVelocity.sqrMagnitude, Is.LessThan(0.001f));
            Physics.SyncTransforms();
            Assert.That(carrier.CanPickup(ore), Is.True);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DisablingHolderRestoresOreAndAllowsPickupAfterReenable()
        {
            Assert.That(carrier.TryPickup(ore), Is.True);
            carrier.gameObject.SetActive(false);
            AssertReleased();
            Assert.That(ore.gameObject.activeInHierarchy, Is.True, "The held ore must remain active when its holder is disabled.");
            Assert.That(ore.transform.parent, Is.Null, "Holding must not move the ore into the holder's hierarchy.");
            Assert.That(Vector3.Distance(ore.transform.position, oreSpawn), Is.LessThan(0.001f));
            yield return null;
            carrier.gameObject.SetActive(true);
            carrier.View.transform.LookAt(ore.transform.position);
            Physics.SyncTransforms();
            Assert.That(carrier.TryPickup(ore), Is.True, "A re-enabled holder must be able to pick up the restored ore.");
            AssertHeld();
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator OutOfBoundsFreeOreReturnsToSpawnWithNormalPhysics()
        {
            ore.transform.position = new Vector3(oreSpawn.x + 5f, ore.RescueHeight - 2f, oreSpawn.z);
            ore.Body.linearVelocity = new Vector3(3f, -20f, 4f);
            ore.Body.angularVelocity = Vector3.one * 5f;
            Physics.SyncTransforms();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            AssertReleased();
            Assert.That(Vector3.Distance(ore.transform.position, oreSpawn), Is.LessThan(0.1f));
            Assert.That(Mathf.Abs(ore.Body.linearVelocity.x), Is.LessThan(0.01f));
            Assert.That(Mathf.Abs(ore.Body.linearVelocity.z), Is.LessThan(0.01f));
            Assert.That(ore.Body.angularVelocity.sqrMagnitude, Is.LessThan(0.001f));
        }

        private OreCarrier CreateCarrier(string name)
        {
            var player = new GameObject(name);
            player.transform.position = origin;
            var controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            var viewObject = new GameObject(name + "Camera");
            viewObject.transform.SetParent(player.transform, false);
            viewObject.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var view = viewObject.AddComponent<Camera>();
            var hold = new GameObject(name + "HoldPoint").transform;
            hold.SetParent(view.transform, false);
            hold.localPosition = Vector3.forward;
            var input = player.AddComponent<M0Input>();
            var result = player.AddComponent<OreCarrier>();
            result.View = view;
            result.HoldPoint = hold;
            result.Input = input;
            result.Reach = 3f;
            input.SetPaused(false);
            return result;
        }

        private static CarryableOre CreateOre(string name, Vector3 position)
        {
            var instance = CreateBox(name, position, Vector3.one * 0.6f);
            var body = instance.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.isKinematic = false;
            return instance.AddComponent<CarryableOre>();
        }

        private static GameObject CreateBox(string name, Vector3 position, Vector3 scale)
        {
            var instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
            instance.name = name;
            instance.transform.position = position;
            instance.transform.localScale = scale;
            return instance;
        }

        private void AssertHeld()
        {
            Assert.That(carrier.HeldOre, Is.SameAs(ore));
            Assert.That(ore.HeldBy, Is.SameAs(carrier));
            Assert.That(ore.Body.isKinematic, Is.True);
            Assert.That(ore.GetComponent<Collider>().enabled, Is.False);
        }

        private void AssertReleased()
        {
            Assert.That(carrier.HeldOre, Is.Null);
            Assert.That(ore.HeldBy, Is.Null);
            Assert.That(ore.Body.isKinematic, Is.False);
            Assert.That(ore.Body.useGravity, Is.True);
            Assert.That(ore.GetComponent<Collider>().enabled, Is.True);
        }

        private static void AssertNoPenetration(Collider first, Collider second)
        {
            bool overlapping = Physics.ComputePenetration(first, first.transform.position, first.transform.rotation,
                second, second.transform.position, second.transform.rotation, out _, out float distance);
            Assert.That(overlapping && distance > 0.005f, Is.False, first.name + " penetrates " + second.name);
        }
    }
}
