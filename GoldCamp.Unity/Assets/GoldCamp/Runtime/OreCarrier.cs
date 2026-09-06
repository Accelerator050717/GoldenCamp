using UnityEngine;

namespace GoldCamp.Runtime
{
    [RequireComponent(typeof(M0Input))]
    [DisallowMultipleComponent]
    public sealed class OreCarrier : MonoBehaviour
    {
        public Camera View;
        public Transform HoldPoint;
        public M0Input Input;
        public float Reach = 3f;
        public float ThrowSpeed = 7f;
        public float UpwardThrowSpeed = 0.7f;
        public CarryableOre HeldOre { get; private set; }
        public CarryableOre FocusedOre { get; private set; }
        public string Feedback { get; private set; }

        private float _feedbackUntil;

        private void Awake()
        {
            ResolveInput();
        }

        private void ResolveInput()
        {
            if (Input == null)
                Input = GetComponent<M0Input>();
        }

        private void Update()
        {
            if (Time.unscaledTime > _feedbackUntil)
                Feedback = string.Empty;
            if (Input == null || Input.IsPaused)
            {
                FocusedOre = null;
                return;
            }
            CarryableOre lookedAtOre = HeldOre == null ? FindLookedAtOre() : null;
            FocusedOre = MeetsPickupConditions(lookedAtOre) ? lookedAtOre : null;
            if (Input.InteractPressed)
            {
                if (HeldOre != null)
                    Drop();
                else if (FocusedOre != null)
                    TryPickup(FocusedOre);
            }
            if (Input.ThrowPressed && HeldOre != null)
                Throw();
        }

        public bool CanPickup(CarryableOre ore)
        {
            return MeetsPickupConditions(ore) && FindLookedAtOre() == ore;
        }

        private bool MeetsPickupConditions(CarryableOre ore)
        {
            ResolveInput();
            if (!isActiveAndEnabled || ore == null || !ore.isActiveAndEnabled || HeldOre != null
                || ore.HeldBy != null || View == null || HoldPoint == null || Reach <= 0f
                || !HoldPoint.gameObject.activeInHierarchy
                || (Input != null && Input.IsPaused) || !ore.HasUsableCollider())
                return false;
            return Vector3.Distance(View.transform.position, ore.transform.position) <= Reach;
        }

        public bool TryPickup(CarryableOre ore)
        {
            if (!CanPickup(ore) || !ore.BeginHold(this, HoldPoint))
                return false;
            HeldOre = ore;
            FocusedOre = null;
            ShowFeedback("Ore picked up");
            return true;
        }

        private CarryableOre FindLookedAtOre()
        {
            if (View == null || Reach <= 0f)
                return null;
            RaycastHit[] hits = Physics.RaycastAll(View.transform.position, View.transform.forward, Reach,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            CarryableOre result = null;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.transform.IsChildOf(transform) || hit.distance >= nearest)
                    continue;
                nearest = hit.distance;
                result = hit.collider.GetComponentInParent<CarryableOre>();
            }
            return result;
        }

        public bool Drop()
        {
            return Release(false);
        }

        public bool Throw()
        {
            return Release(true);
        }

        private bool Release(bool throwOre)
        {
            ResolveInput();
            if (HeldOre == null || View == null || HoldPoint == null || (Input != null && Input.IsPaused))
                return false;
            if (!TryGetReleasePosition(HeldOre, out Vector3 position))
            {
                ShowFeedback("Not enough room. Turn toward open space.");
                return false;
            }
            CarryableOre ore = HeldOre;
            Vector3 velocity = throwOre
                ? View.transform.forward * ThrowSpeed + Vector3.up * UpwardThrowSpeed
                : Vector3.zero;
            ore.EndHold(position, velocity, GetComponentsInChildren<Collider>(true));
            HeldOre = null;
            ShowFeedback(throwOre ? "Ore thrown" : "Ore dropped");
            return true;
        }

        private bool TryGetReleasePosition(CarryableOre ore, out Vector3 position)
        {
            const float margin = 0.04f;
            Physics.SyncTransforms();
            float radius = ore.CollisionRadius + margin;
            Vector3 origin = View.transform.position;
            Vector3 offset = HoldPoint.position - origin;
            float distance = offset.magnitude;
            position = HoldPoint.position;
            if (distance < 0.01f)
                return false;
            Vector3 direction = offset / distance;
            RaycastHit[] hits = Physics.SphereCastAll(origin, radius, direction, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.transform.IsChildOf(transform) || hit.collider.transform.IsChildOf(ore.transform))
                    continue;
                distance = Mathf.Min(distance, Mathf.Max(0f, hit.distance - margin));
            }
            position = origin + direction * distance;
            if (!IsClearOfPlayer(position, radius, ore))
                return false;
            foreach (Collider obstacle in Physics.OverlapSphere(position, radius,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                if (obstacle.transform.IsChildOf(ore.transform))
                    continue;
                // Includes the player's capsule: a release may never begin inside the player.
                return false;
            }
            return true;
        }

        private bool IsClearOfPlayer(Vector3 position, float radius, CarryableOre ore)
        {
            // Check the authored capsule directly: a clear broadphase result alone must
            // never permit releasing an ore into the player's geometry.
            foreach (Collider player in GetComponentsInChildren<Collider>(true))
            {
                if (player == null || !player.enabled || !player.gameObject.activeInHierarchy
                    || player.isTrigger || player.transform.IsChildOf(ore.transform))
                    continue;
                if (player is CharacterController capsule)
                {
                    Vector3 scale = capsule.transform.lossyScale;
                    float capsuleRadius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                    float halfSegment = Mathf.Max(0f, capsule.height * Mathf.Abs(scale.y) * 0.5f - capsuleRadius);
                    Vector3 center = capsule.transform.TransformPoint(capsule.center);
                    Vector3 axis = capsule.transform.up;
                    float along = Mathf.Clamp(Vector3.Dot(position - center, axis), -halfSegment, halfSegment);
                    Vector3 closest = center + axis * along;
                    float clearance = radius + capsuleRadius + capsule.skinWidth * Mathf.Max(
                        Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    if ((position - closest).sqrMagnitude <= clearance * clearance)
                        return false;
                }
                else if (player.bounds.SqrDistance(position) <= radius * radius)
                {
                    return false;
                }
            }
            return true;
        }

        private void ShowFeedback(string message)
        {
            Feedback = message;
            _feedbackUntil = Time.unscaledTime + 2f;
        }

        internal void ForgetOre(CarryableOre ore)
        {
            if (HeldOre == ore)
                HeldOre = null;
        }

        public void ResetHeldOre()
        {
            if (HeldOre != null)
                HeldOre.ResetToSpawn();
        }

        private void OnDisable()
        {
            ResetHeldOre();
            FocusedOre = null;
        }
    }
}
