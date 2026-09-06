using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Only the host simulates player capsules. Clients submit axes, never positions.
    public sealed class CampSimulation : MonoBehaviour
    {
        public CampSession Session;
        private sealed class Motor
        {
            public CharacterController Body;
            public CampInputFrame Input = new CampInputFrame();
            public float LastInput, Vertical, FallStart;
            public int LastSequence = -1;
            public bool WasGrounded;
        }
        private readonly Dictionary<string, Motor> _motors = new Dictionary<string, Motor>();
        private void Start()
        { Session.InputReceived += ReceiveInput; Session.PlayerDisconnected += Disconnected; }
        private void Disconnected(string actor)
        {
            if (!_motors.TryGetValue(actor, out var motor)) return;
            motor.Body.gameObject.SetActive(false); Destroy(motor.Body.gameObject); _motors.Remove(actor);
        }
        private void ReceiveInput(string actor, CampInputFrame input)
        {
            if (!_motors.TryGetValue(actor, out var motor) || input.Sequence <= motor.LastSequence) return;
            var player = Session.Authority.State.Players.Find(p => p.Id == actor);
            if (player != null && player.EscapeAt > 0 && (Mathf.Abs(input.X) > .1f || Mathf.Abs(input.Z) > .1f))
                Session.Authority.Execute(actor, new CampCommand { RequestId = "move-cancel-" + System.Guid.NewGuid().ToString("N"), Action = "cancel_escape" });
            if (player != null && player.Action == "rob" && (Mathf.Abs(input.X) > .1f || Mathf.Abs(input.Z) > .1f))
                Session.Authority.Execute(actor, new CampCommand { RequestId = "move-cancel-" + System.Guid.NewGuid().ToString("N"), Action = "cancel_action" });
            motor.LastSequence = input.Sequence;
            motor.Input = input; motor.LastInput = Time.unscaledTime;
        }
        private void FixedUpdate()
        {
            var authority = Session.Authority;
            if (authority == null) { Clear(); return; }
            foreach (string id in _motors.Keys.Where(id => !Session.OnlinePlayerIds.Contains(id)).ToArray())
            { Destroy(_motors[id].Body.gameObject); _motors.Remove(id); }
            foreach (string id in Session.OnlinePlayerIds)
            {
                var player = authority.State.Players.Find(p => p.Id == id);
                if (player == null) continue;
                if (!_motors.TryGetValue(id, out var motor))
                {
                    var go = new GameObject("Authority motor " + id);
                    go.transform.SetParent(transform, false); go.transform.position = player.Position;
                    go.layer = 2;
                    var body = go.AddComponent<CharacterController>();
                    body.height = 1.8f; body.radius = .3f; body.center = Vector3.up * .9f;
                    body.stepOffset = .3f; body.skinWidth = .025f;
                    Session.GetComponent<CampWorld>()?.IgnoreMinerCollisions(body);
                    motor = new Motor { Body = body, FallStart = player.Position.y, Input = new CampInputFrame { Yaw = player.Yaw, Pitch = player.Pitch } };
                    _motors.Add(id, motor);
                }
                int seat = authority.State.Vehicle?.Seats.IndexOf(id) ?? -1;
                if (seat >= 0)
                {
                    motor.Body.enabled = false;
                    motor.Body.transform.position = CampMap.SeatPoint(authority.State.Vehicle, seat);
                    player.Position = motor.Body.transform.position;
                    player.Yaw = motor.Input.Yaw; player.Pitch = Mathf.Clamp(motor.Input.Pitch, -85, 85);
                    motor.Vertical = 0; motor.FallStart = player.Position.y; motor.WasGrounded = true;
                    continue;
                }
                if (!motor.Body.enabled)
                { motor.Body.transform.position = player.Position; motor.Body.enabled = true; motor.Vertical = 0; motor.FallStart = player.Position.y; }
                // Domain escape/load may move a player; synchronize before consuming axes.
                if (Vector3.Distance(motor.Body.transform.position, player.Position) > 1f)
                {
                    motor.Body.enabled = false; motor.Body.transform.position = player.Position;
                    motor.Body.enabled = true; motor.Vertical = 0; motor.FallStart = player.Position.y;
                }
                CampRules rules = authority.Rules;
                var input = motor.Input;
                bool active = Time.unscaledTime - motor.LastInput < .3f && player.StunnedUntil <= authority.State.OnlineSeconds && player.EscapeAt <= 0;
                Vector2 axes = active ? Vector2.ClampMagnitude(new Vector2(input.X, input.Z), 1) : Vector2.zero;
                player.Yaw = input.Yaw;
                player.Pitch = Mathf.Clamp(input.Pitch, -85, 85);
                var held = authority.State.Items.Find(i => i.Place == ItemPlace.Hand && i.OwnerId == id);
                float speed = rules.WalkSpeed;
                if (held?.Kind == ItemKind.HeavyOre) speed *= string.IsNullOrEmpty(held.AssistantId) ? rules.SoloCarryMultiplier : rules.TeamCarryMultiplier;
                var ladder = authority.State.Ladders.Find(l => l.Id == player.ClimbingLadderId);
                if (ladder != null)
                {
                    var chain = authority.State.Ladders.Where(l => l.X == ladder.X && l.Z == ladder.Z && l.PitId == ladder.PitId).ToArray();
                    float low = -(chain.Max(l => l.Depth) + 1) * rules.CellSize + .08f;
                    var target = CampMap.LadderCenter(ladder, rules) - Vector3.forward * .15f;
                    target.y = Mathf.Clamp(motor.Body.transform.position.y + axes.y * rules.ClimbSpeed * Time.fixedDeltaTime * (held?.Kind == ItemKind.HeavyOre ? .5f : 1), low, .1f);
                    motor.Body.Move(target - motor.Body.transform.position);
                    motor.Vertical = 0; motor.FallStart = target.y;
                }
                else
                {
                    bool grounded = motor.Body.isGrounded;
                    if (grounded)
                    {
                        if (!motor.WasGrounded && motor.FallStart - motor.Body.transform.position.y > 3.2f)
                        {
                            authority.LandHard(id);
                            player = authority.State.Players.Find(p => p.Id == id);
                        }
                        motor.FallStart = motor.Body.transform.position.y;
                        motor.Vertical = active && input.Jump ? rules.JumpSpeed : -2f;
                    }
                    else
                    {
                        motor.FallStart = Mathf.Max(motor.FallStart, motor.Body.transform.position.y);
                        motor.Vertical -= rules.Gravity * Time.fixedDeltaTime;
                    }
                    motor.WasGrounded = grounded;
                    Vector3 motion = Quaternion.Euler(0, input.Yaw, 0) * new Vector3(axes.x, 0, axes.y) * speed;
                    motion.y = motor.Vertical;
                    motor.Body.Move(motion * Time.fixedDeltaTime);
                }
                input.Jump = false;
                player.Position = motor.Body.transform.position;
                var carried = authority.State.Items.Find(i => i.Place == ItemPlace.Hand && i.OwnerId == id);
                if (carried != null)
                {
                    carried.Position = player.Position + Vector3.up + Quaternion.Euler(0, player.Yaw, 0) * Vector3.forward * 1.15f;
                    carried.Velocity = Vector3.zero;
                }
            }
        }
        public Vector3 Position(string id, Vector3 fallback) => _motors.TryGetValue(id, out var motor) ? motor.Body.transform.position : fallback;
        private void Clear()
        {
            foreach (var motor in _motors.Values) if (motor.Body != null) Destroy(motor.Body.gameObject);
            _motors.Clear();
        }
        private void OnDestroy()
        { if (Session != null) { Session.InputReceived -= ReceiveInput; Session.PlayerDisconnected -= Disconnected; } Clear(); }
    }
}
