using System.Collections.Generic;
using UnityEngine;

namespace GoldCamp.Runtime
{
    [RequireComponent(typeof(Rigidbody), typeof(Collider))]
    [DisallowMultipleComponent]
    public sealed class CarryableOre : MonoBehaviour
    {
        public float RescueHeight = -12f;
        public OreCarrier HeldBy { get; private set; }
        public Rigidbody Body { get { EnsureInitialized(); return _body; } }
        public float CollisionRadius { get { EnsureInitialized(); return _collisionRadius; } }

        private Rigidbody _body;
        private Collider[] _colliders;
        private bool[] _colliderEnabled;
        private bool _normalGravity;
        private bool _normalKinematic;
        private RigidbodyInterpolation _normalInterpolation;
        private CollisionDetectionMode _normalCollisionDetection;
        private float _collisionRadius;
        private Transform _holdPoint;
        private Vector3 _spawnPosition;
        private Quaternion _spawnRotation;
        private bool _initialized;
        private readonly List<Collider> _ignoredPlayerColliders = new List<Collider>();

        private void Awake()
        {
            EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            if (_initialized)
                return;
            _body = GetComponent<Rigidbody>();
            _colliders = GetComponentsInChildren<Collider>(true);
            _colliderEnabled = new bool[_colliders.Length];
            _normalGravity = _body.useGravity;
            _normalKinematic = _body.isKinematic;
            _normalInterpolation = _body.interpolation;
            _normalCollisionDetection = _body.collisionDetectionMode;
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
            _collisionRadius = 0.1f;
            for (int i = 0; i < _colliders.Length; i++)
            {
                _colliderEnabled[i] = _colliders[i].enabled;
                if (_colliders[i].enabled && !_colliders[i].isTrigger)
                {
                    Bounds bounds = _colliders[i].bounds;
                    float radius = Vector3.Distance(transform.position, bounds.center) + bounds.extents.magnitude;
                    _collisionRadius = Mathf.Max(_collisionRadius, radius);
                }
            }
            _initialized = true;
        }

        public bool HasUsableCollider()
        {
            EnsureInitialized();
            foreach (Collider item in _colliders)
                if (item != null && item.enabled && !item.isTrigger && item.gameObject.activeInHierarchy)
                    return true;
            return false;
        }

        internal bool BeginHold(OreCarrier carrier, Transform point)
        {
            EnsureInitialized();
            if (HeldBy != null || carrier == null || point == null || !isActiveAndEnabled
                || !point.gameObject.activeInHierarchy)
                return false;
            RestorePlayerCollisions();
            _normalGravity = _body.useGravity;
            _normalKinematic = _body.isKinematic;
            _normalInterpolation = _body.interpolation;
            _normalCollisionDetection = _body.collisionDetectionMode;
            if (!_body.isKinematic)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
            _body.useGravity = false;
            _body.interpolation = RigidbodyInterpolation.None;
            _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _body.isKinematic = true;
            for (int i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] == null)
                    continue;
                _colliderEnabled[i] = _colliders[i].enabled;
                _colliders[i].enabled = false;
            }
            HeldBy = carrier;
            _holdPoint = point;
            FollowHoldPoint();
            return true;
        }

        private void LateUpdate()
        {
            if (_holdPoint == null)
            {
                if (HeldBy != null)
                    ResetToSpawn();
                return;
            }
            if (HeldBy == null || !HeldBy.isActiveAndEnabled || !_holdPoint.gameObject.activeInHierarchy)
            {
                ResetToSpawn();
                return;
            }
            FollowHoldPoint();
        }

        private void FollowHoldPoint()
        {
            // Keep the ore outside the player's hierarchy. Disabling or destroying a holder
            // must never reparent children while Unity is deactivating that hierarchy.
            transform.SetPositionAndRotation(_holdPoint.position, _holdPoint.rotation);
        }

        internal void EndHold(Vector3 position, Vector3 velocity, Collider[] playerColliders)
        {
            EnsureInitialized();
            _holdPoint = null;
            transform.position = position;
            HeldBy = null;
            RestorePhysicalState();
            Physics.SyncTransforms();
            foreach (Collider player in playerColliders)
            {
                // Defensive exclusion if an authored player hierarchy contains another ore.
                if (player == null || player.transform.IsChildOf(transform))
                    continue;
                foreach (Collider ore in _colliders)
                    if (ore != null && ore.enabled)
                        Physics.IgnoreCollision(ore, player, true);
                _ignoredPlayerColliders.Add(player);
            }
            if (!_body.isKinematic)
            {
                _body.linearVelocity = velocity;
                _body.angularVelocity = Vector3.zero;
                _body.WakeUp();
            }
        }

        private void RestorePhysicalState()
        {
            for (int i = 0; i < _colliders.Length; i++)
                if (_colliders[i] != null)
                    _colliders[i].enabled = _colliderEnabled[i];
            if (_body != null)
            {
                _body.isKinematic = _normalKinematic;
                _body.useGravity = _normalGravity;
                _body.interpolation = _normalInterpolation;
                _body.collisionDetectionMode = _normalCollisionDetection;
            }
        }

        private void FixedUpdate()
        {
            if (HeldBy == null && transform.position.y < RescueHeight)
                ResetToSpawn();
            for (int i = _ignoredPlayerColliders.Count - 1; i >= 0; i--)
            {
                Collider player = _ignoredPlayerColliders[i];
                bool overlaps = false;
                if (player != null && player.enabled)
                {
                    foreach (Collider ore in _colliders)
                        if (ore != null && ore.enabled && ore.bounds.Intersects(player.bounds))
                        {
                            overlaps = true;
                            break;
                        }
                }
                if (overlaps)
                    continue;
                foreach (Collider ore in _colliders)
                    if (ore != null && player != null)
                        Physics.IgnoreCollision(ore, player, false);
                _ignoredPlayerColliders.RemoveAt(i);
            }
        }

        public void ResetToSpawn()
        {
            EnsureInitialized();
            OreCarrier owner = HeldBy;
            HeldBy = null;
            _holdPoint = null;
            if (owner != null)
                owner.ForgetOre(this);
            transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
            RestorePhysicalState();
            RestorePlayerCollisions();
            if (!_body.isKinematic)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
        }

        private void RestorePlayerCollisions()
        {
            foreach (Collider player in _ignoredPlayerColliders)
                foreach (Collider ore in _colliders)
                    if (ore != null && player != null)
                        Physics.IgnoreCollision(ore, player, false);
            _ignoredPlayerColliders.Clear();
        }

        private void OnDisable()
        {
            if (!_initialized)
                return;
            if (HeldBy != null)
                ResetToSpawn();
            _holdPoint = null;
            RestorePlayerCollisions();
        }
    }
}
