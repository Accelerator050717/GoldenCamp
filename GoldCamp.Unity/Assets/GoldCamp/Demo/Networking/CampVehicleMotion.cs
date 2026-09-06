using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    // A bounded, flat-road vehicle. The host sweeps its hull and checks ground;
    // clients only supply axes and interpolate the authoritative pose.
    [DefaultExecutionOrder(-30)]
    public sealed class CampVehicleMotion : MonoBehaviour
    {
        public CampSession Session;
        private sealed class InputSample { public CampInputFrame Frame; public float Received; }
        private readonly Dictionary<string, InputSample> _inputs = new Dictionary<string, InputSample>();
        private GameObject _root;
        private Rigidbody _body;
        private BoxCollider _hull;
        private CampVehicleModel _model;
        private string _worldId;
        private static readonly Vector3 HullHalf = new Vector3(1.06f, .65f, 2.12f);

        private void Start()
        { Session.InputReceived += ReceiveInput; Session.PlayerDisconnected += Disconnected; }
        private void Disconnected(string actor) => _inputs.Remove(actor);
        private void ReceiveInput(string actor, CampInputFrame frame)
        {
            if (_inputs.TryGetValue(actor, out var old) && frame.Sequence <= old.Frame.Sequence) return;
            _inputs[actor] = new InputSample { Frame = frame, Received = Time.unscaledTime };
        }
        private void EnsureWorld(string worldId, CampVehicle vehicle)
        {
            if (_worldId != worldId) { Clear(); _worldId = worldId; }
            if (_root != null || vehicle == null) return;
            _root = new GameObject("Public camp truck"); _root.transform.SetParent(transform, false);
            _root.transform.SetPositionAndRotation(vehicle.Position, Quaternion.Euler(0, vehicle.Yaw, 0));
            _body = _root.AddComponent<Rigidbody>(); _body.isKinematic = true; _body.useGravity = false;
            var hull = new GameObject("Truck interaction hull"); hull.transform.SetParent(_root.transform, false);
            hull.transform.localPosition = Vector3.up * 1.05f;
            _hull = hull.AddComponent<BoxCollider>(); _hull.size = HullHalf * 2;
            var target = hull.AddComponent<CampTarget>(); target.Kind = "vehicle"; target.Id = vehicle.Id;
            _model = CampVehicleModel.Create(_root.transform, Session.GetComponent<CampWorld>().Palette);
            IgnoreCargo();
        }
        private void FixedUpdate()
        {
            var authority = Session.Authority;
            if (authority?.State.Vehicle == null) return;
            var state = authority.State; var car = state.Vehicle;
            EnsureWorld(state.WorldId, car);
            string driver = car.Seats[0];
            bool active = state.RoadUnlocked && !string.IsNullOrEmpty(driver) && Session.OnlinePlayerIds.Contains(driver)
                && _inputs.TryGetValue(driver, out var input) && Time.unscaledTime - input.Received < .3f;
            var driverState = state.Players.Find(p => p.Id == driver);
            active &= driverState != null && driverState.StunnedUntil <= state.OnlineSeconds;
            CampInputFrame frame = active ? _inputs[driver].Frame : null;
            float throttle = frame == null ? 0 : Mathf.Clamp(frame.Z, -1, 1);
            car.Steering = frame == null ? 0 : Mathf.Clamp(frame.X, -1, 1);
            if (!active) car.Speed = 0;
            else
            {
                float target = throttle * (throttle >= 0 ? authority.Rules.VehicleSpeed : authority.Rules.VehicleReverseSpeed);
                car.Speed = Mathf.MoveTowards(car.Speed, target, authority.Rules.VehicleAcceleration * (Mathf.Abs(throttle) < .01f ? 2 : 1) * Time.fixedDeltaTime);
            }
            float turn = car.Steering * authority.Rules.VehicleTurnSpeed * Mathf.Clamp(car.Speed / 3, -1, 1) * Time.fixedDeltaTime;
            float yaw = Mathf.Repeat(car.Yaw + turn, 360);
            Quaternion rotation = Quaternion.Euler(0, yaw, 0);
            Vector3 motion = rotation * Vector3.forward * car.Speed * Time.fixedDeltaTime;
            Vector3 next = car.Position + motion;
            if (motion.sqrMagnitude > .0000001f || Mathf.Abs(turn) > .0001f)
            {
                if (SafePose(state, next, rotation, motion)) { car.Position = next; car.Yaw = yaw; }
                else car.Speed = 0;
            }
            _body.position = car.Position; _body.rotation = Quaternion.Euler(0, car.Yaw, 0);
            for (int seat = 0; seat < car.Seats.Count; seat++)
            {
                var passenger = state.Players.Find(p => p.Id == car.Seats[seat]);
                if (passenger != null) passenger.Position = CampMap.SeatPoint(car, seat);
            }
            foreach (var cargo in state.Items.Where(i => i.Place == ItemPlace.Vehicle))
            {
                for (int slot = 0; slot < car.CargoSlots; slot++)
                    if (cargo.ContainerId == CampMap.VehicleCargoContainer(slot))
                        cargo.Position = CampMap.CargoPoint(car, slot);
                cargo.Velocity = Vector3.zero;
            }
        }
        private bool SafePose(CampState state, Vector3 feet, Quaternion rotation, Vector3 motion)
        {
            Vector3 center = feet + Vector3.up * 1.05f;
            foreach (var obstacle in Physics.OverlapBox(center, HullHalf, rotation, ~0, QueryTriggerInteraction.Ignore))
                if (BlocksCar(obstacle, state)) return false;
            if (motion.sqrMagnitude > .0000001f)
                foreach (var hit in Physics.BoxCastAll(_body.position + Vector3.up * 1.05f, HullHalf,
                    motion.normalized, _body.rotation, motion.magnitude, ~0, QueryTriggerInteraction.Ignore))
                    if (BlocksCar(hit.collider, state)) return false;
            // Four wheels must remain over the supported road/banks. This prevents
            // driving through an excavated pit or requiring a vehicle rescue to mine.
            foreach (float x in new[] { -.8f, .8f }) foreach (float z in new[] { -1.6f, 1.6f })
            {
                Vector3 wheel = feet + rotation * new Vector3(x, 2f, z);
                bool supported = false;
                foreach (var hit in Physics.RaycastAll(wheel, Vector3.down, 2.35f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
                {
                    if (hit.collider == _hull || hit.collider is CharacterController) continue;
                    var target = hit.collider.GetComponentInParent<CampTarget>();
                    if (target != null && target.Kind != "cell") continue;
                    if (Mathf.Abs(hit.point.y - feet.y) <= .25f) supported = true;
                    break;
                }
                if (!supported) return false;
            }
            return true;
        }
        private bool BlocksCar(Collider collider, CampState state)
        {
            if (collider == null || collider == _hull || collider.isTrigger) return false;
            var target = collider.GetComponentInParent<CampTarget>();
            if (target != null && (target.Kind == "item" || target.Kind == "bag" || target.Kind == "ladder")) return false;
            if (collider is CharacterController)
            {
                // Seated controllers are disabled by CampSimulation. The local host
                // has no additional prediction capsule; walking miners stop the truck.
                return collider.enabled;
            }
            return true;
        }
        private void LateUpdate()
        {
            var view = Session.View;
            var car = Session.Authority?.State.Vehicle ?? view?.Vehicle;
            if (car == null || string.IsNullOrEmpty(car.Id)) { Clear(); return; }
            EnsureWorld(Session.Authority?.State.WorldId ?? view.WorldId, car);
            if (Session.Authority == null)
            {
                float blend = 1 - Mathf.Exp(-14 * Time.unscaledDeltaTime);
                _body.position = Vector3.Lerp(_body.position, car.Position, blend);
                _body.rotation = Quaternion.Slerp(_body.rotation, Quaternion.Euler(0, car.Yaw, 0), blend);
            }
            _model.Pose(Session.Authority?.State.RoadUnlocked ?? view.RoadUnlocked, car.Speed, car.Steering);
            IgnoreCargo();
        }
        private void IgnoreCargo()
        {
            if (_hull == null) return;
            foreach (var target in Session.GetComponentsInChildren<CampTarget>())
                if (target.Kind == "item" || target.Kind == "bag")
                    foreach (var collider in target.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(_hull, collider, true);
        }
        public Vector3 SeatPosition(int seat, CampVehicle fallback) => _root != null ?
            _root.transform.TransformPoint(CampMap.SeatOffset(seat)) : CampMap.SeatPoint(fallback, seat);
        public float VisualYaw(CampVehicle fallback) => _root != null ? _root.transform.eulerAngles.y : fallback.Yaw;
        private void Clear()
        {
            if (_root != null) { _root.SetActive(false); Destroy(_root); }
            _root = null; _body = null; _hull = null; _model = null; _inputs.Clear();
        }
        private void OnDestroy()
        { if (Session != null) { Session.InputReceived -= ReceiveInput; Session.PlayerDisconnected -= Disconnected; } Clear(); }
    }
}
