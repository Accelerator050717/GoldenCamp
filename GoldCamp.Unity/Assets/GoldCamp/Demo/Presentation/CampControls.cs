using System;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed class CampControls : MonoBehaviour
    {
        public CampSession Session;
        public CampHud Hud;
        public Camera Camera;
        public float Sensitivity = 2f, FieldOfView = 75f;
        public bool CameraSway;
        public string Hint { get; private set; } = "";
        public CampTarget Target { get; private set; }
        public int CurrentTool => _tool;
        private string _world;
        private int _sequence, _tool = 1;
        private float _yaw, _pitch, _nextSend, _nextMine, _vertical;
        private bool _jump;
        private CharacterController _prediction;
        private bool _wasSeated;
        private float _vehicleYaw;
        private Vector3 _lastCameraPosition;
        private void Start()
        {
            if (Session.IsTestSession) return;
            Sensitivity = PlayerPrefs.GetFloat("Demo.Mouse", 2); FieldOfView = PlayerPrefs.GetFloat("Demo.FOV", 75);
            CameraSway = PlayerPrefs.GetInt("Demo.CameraSway", 0) != 0;
        }
        private void Update()
        {
            if (Application.isBatchMode) return;
            var view = Session.View;
            if (view?.Self == null)
            {
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true; _world = null;
                if (_prediction != null) { Destroy(_prediction.gameObject); _prediction = null; }
                return;
            }
            var self = view.Players.Find(p => p.Id == view.Self.Id);
            if (self == null) return;
            if (_world != view.WorldId) { _world = view.WorldId; _yaw = self.Yaw; _pitch = 0; Hud.Panel = ""; _wasSeated = false; }
            bool seated = self.VehicleSeat >= 0 && view.Vehicle != null;
            if (seated)
            {
                if (!_wasSeated) _yaw = view.Vehicle.Yaw;
                else _yaw += Mathf.DeltaAngle(_vehicleYaw, view.Vehicle.Yaw);
                _vehicleYaw = view.Vehicle.Yaw;
            }
            _wasSeated = seated;
            if (Input.GetKeyDown(KeyCode.Escape)) Hud.Panel = string.IsNullOrEmpty(Hud.Panel) ? "menu" : "";
            if (Input.GetKeyDown(KeyCode.Tab)) Hud.Panel = Hud.Panel == "bag" ? "" : "bag";
            bool automated = Session.IsTestSession && CampSession.HasArgument("--gc-auto-input");
            bool control = !automated && string.IsNullOrEmpty(Hud.Panel) && Application.isFocused;
            if (automated) { _yaw = self.Yaw; _pitch = self.Pitch; }
            Cursor.lockState = control ? CursorLockMode.Locked : CursorLockMode.None; Cursor.visible = !control;
            if (control)
            {
                _yaw += Input.GetAxisRaw("Mouse X") * Sensitivity;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxisRaw("Mouse Y") * Sensitivity, -85, 85);
                if (Input.GetKeyDown(KeyCode.Alpha1)) _tool = 1;
                if (Input.GetKeyDown(KeyCode.Alpha2)) _tool = 2;
            }
            float x = control ? Input.GetAxisRaw("Horizontal") : 0, z = control ? Input.GetAxisRaw("Vertical") : 0;
            _jump |= control && Input.GetKeyDown(KeyCode.Space);
            if (!automated && Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + .05f;
                Session.SendInput(new CampInputFrame { Sequence = ++_sequence, X = x, Z = z, Yaw = _yaw, Pitch = _pitch, Jump = _jump });
                _jump = false;
            }
            Vector3 position = self.Position;
            if (seated)
            {
                if (_prediction != null) _prediction.enabled = false;
                position = Session.GetComponent<CampVehicleMotion>().SeatPosition(self.VehicleSeat, view.Vehicle);
            }
            else if (Session.Authority != null) position = Session.GetComponent<CampSimulation>().Position(self.Id, position);
            else position = Predict(self, view, x, z, control);
            float sway = CameraSway && !seated && Vector3.Distance(position, _lastCameraPosition) > .005f
                ? Mathf.Sin(Time.unscaledTime * 10) * .018f : 0;
            _lastCameraPosition = position;
            Camera.transform.SetPositionAndRotation(position + Vector3.up * ((seated ? 1.32f : 1.62f) + sway), Quaternion.Euler(_pitch, _yaw, 0));
            Camera.fieldOfView = FieldOfView;
            Target = null;
            if (Physics.Raycast(Camera.transform.position, Camera.transform.forward, out var hit, view.Rules.Reach + .5f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)) Target = hit.collider.GetComponentInParent<CampTarget>();
            Hint = DescribeTarget();
            if (seated) Hint = self.VehicleSeat == 0 ? "驾驶 · W/S 油门/倒车 · A/D 转向 · E 停车下车" : "乘坐中 · E 安全下车";
            if (!control) return;
            if (seated)
            { if (Input.GetKeyDown(KeyCode.E)) Send("vehicle_exit", view.Vehicle.Id, view.Vehicle.Version); return; }
            var hand = view.Items.Find(i => i.OwnerId == self.Id && i.Place == ItemPlace.Hand);
            if (Input.GetKeyDown(KeyCode.Q) && hand != null) Send("throw", hand.Id, hand.Version);
            if (Input.GetKeyDown(KeyCode.F) && hand != null) Send("pocket", hand.Id, hand.Version);
            if (Input.GetKeyDown(KeyCode.B))
            {
                var mouth = view.Items.Find(i => i.OwnerId == self.Id && i.Place == ItemPlace.Mouth);
                if (mouth != null) Send("spit", mouth.Id, mouth.Version);
                else if (hand != null) Send("mouth", hand.Id, hand.Version);
            }
            if (Input.GetKeyDown(KeyCode.X)) Send("cancel_action");
            if (Input.GetKeyDown(KeyCode.L)) PlaceLadder();
            if (Input.GetKeyDown(KeyCode.E))
            {
                if (!string.IsNullOrEmpty(self.ClimbingLadderId)) Send("unclip");
                else if (Target != null && (Target.Kind == "station" || Target.Kind == "cabinet")) Hud.Panel = Target.Kind == "cabinet" ? "cabinet" : Target.Id;
                else if (Target?.Kind == "ladder")
                { var ladder = view.Ladders.Find(l => l.Id == Target.Id); if (ladder != null) Send("climb", ladder.Id, ladder.Version); }
                else if (Target?.Kind == "item")
                { var item = view.Items.Find(i => i.Id == Target.Id); if (item != null) Send(item.Place == ItemPlace.Hand && item.OwnerId != self.Id ? "assist" : "pickup", item.Id, item.Version); }
                else if (Target?.Kind == "player") Send("rob", Target.Id);
                else if (Target?.Kind == "bag") { Hud.LootContainer = Target.Id; Hud.Panel = "bagloot"; }
                else if (Target?.Kind == "vehicle") Hud.Panel = "vehicle";
                else if (hand != null) Send("drop", hand.Id, hand.Version);
            }
            if (Input.GetMouseButton(0) && Time.unscaledTime >= _nextMine)
            {
                _nextMine = Time.unscaledTime + view.Rules.MineInterval + .03f;
                if (_tool == 2) Send("detect", Target?.Id ?? "");
                else if (Target?.Kind == "cell")
                { var cell = view.Cells.Find(c => c.Id == Target.Id); if (cell != null) Send("mine", cell.Id, cell.Version); }
                else if (Target?.Kind == "player") Send("attack", Target.Id);
            }
        }
        private Vector3 Predict(PublicMiner self, CampView view, float x, float z, bool control)
        {
            if (_prediction != null && !_prediction.enabled)
            { _prediction.transform.position = self.Position; _prediction.enabled = true; _vertical = 0; }
            if (_prediction == null)
            {
                var go = new GameObject("Local view prediction"); go.transform.SetParent(Session.transform,false); go.layer = 2; go.transform.position = self.Position;
                _prediction = go.AddComponent<CharacterController>();
                _prediction.height = 1.8f; _prediction.radius = .3f; _prediction.center = Vector3.up * .9f;
                _prediction.stepOffset = .3f; _prediction.skinWidth = .025f;
                Session.GetComponent<CampWorld>()?.IgnoreMinerCollisions(_prediction);
            }
            float error = Vector3.Distance(_prediction.transform.position, self.Position);
            if (error > 2f || !string.IsNullOrEmpty(self.ClimbingLadderId))
            {
                _prediction.enabled = false;
                _prediction.transform.position = Vector3.Lerp(_prediction.transform.position, self.Position, error > 2 ? 1 : .25f);
                _prediction.enabled = true; _vertical = 0;
            }
            else
            {
                float speed = view.Rules.WalkSpeed;
                var held = view.Items.Find(i => i.OwnerId == self.Id && i.Place == ItemPlace.Hand);
                if (held?.Kind == ItemKind.HeavyOre) speed *= string.IsNullOrEmpty(held.AssistantId) ? view.Rules.SoloCarryMultiplier : view.Rules.TeamCarryMultiplier;
                bool canMove = self.StunnedUntil <= view.OnlineSeconds && view.Self.EscapeAt <= 0;
                Vector3 motion = Quaternion.Euler(0, _yaw, 0) * Vector3.ClampMagnitude(new Vector3(x, 0, z), 1) * (canMove ? speed : 0);
                _vertical = _prediction.isGrounded ? (control && Input.GetKeyDown(KeyCode.Space) ? view.Rules.JumpSpeed : -2) : _vertical - view.Rules.Gravity * Time.unscaledDeltaTime;
                motion.y = _vertical; _prediction.Move(motion * Time.unscaledDeltaTime);
                if (error > .35f) _prediction.Move((self.Position - _prediction.transform.position) * Mathf.Min(Time.unscaledDeltaTime * 3, .2f));
            }
            return _prediction.transform.position;
        }
        public void Send(string action, string target = "", int version = 0, long amount = 0, string other = "") =>
            Session.SendCommand(new CampCommand { RequestId = Guid.NewGuid().ToString("N"), Action = action, TargetId = target,
                ExpectedVersion = version, Direction = Camera.transform.forward, Amount = amount, OtherPlayerId = other });
        public void PlaceLadder()
        {
            var view = Session.View;
            var point = Camera.transform.position + Camera.transform.forward * 2f;
            var cell = view.Cells.Where(c => c.Dug && !view.Ladders.Any(l => l.CellId == c.Id))
                .OrderBy(c => Vector3.Distance(point, CampMap.CellCenter(c.PitId, c.X, c.Depth, c.Z, view.Rules))).FirstOrDefault();
            if (cell != null) Send("place_ladder", cell.Id, cell.Version);
        }
        private string DescribeTarget()
        {
            if (Target == null) return _tool == 1 ? "矿镐 · 对准矿坑岩层按住左键" : "探测器 · 左键扫描";
            if (Target.Kind == "cell")
            { var cell=Session.View.Cells.Find(c=>c.Id==Target.Id); return "岩层 · 需要矿镐 " + (cell?.RequiredTool??1) + " 级 · " + (_tool == 1 ? "左键挥镐" : "左键探测"); }
            if (Target.Kind == "vehicle") return "E 运输车 · 四座 / 公共货架";
            if (Target.Kind == "item") return "E 拿起 / 协助搬运";
            if (Target.Kind == "bag") return "E 查看掉落包 · 逐件取出";
            if (Target.Kind == "ladder") return "E 攀爬 · W / S 上下 · E 离梯";
            if (Target.Kind == "player") return _tool == 2 ? "左键扫人 · 只给粗略矿物反应" : "左键短晕 · E 搜一个口袋格 · X 取消";
            return "E 打开 " + (Target.Id == "sale" ? "收购台" : Target.Id == "repair" ? "维修台" : Target.Id == "cutter" ? "原石加工" : Target.Id == "wager" ? "原石对赌桌" : Target.Id == "road" ? "公共修车" : "个人储物柜");
        }
        public void SaveSettings()
        {
            Session.GetComponent<CampVoice>()?.SavePreferences(); Session.GetComponent<CampSound>()?.SavePreferences();
            if (Session.IsTestSession) return;
            PlayerPrefs.SetFloat("Demo.Mouse", Sensitivity); PlayerPrefs.SetFloat("Demo.FOV", FieldOfView);
            PlayerPrefs.SetInt("Demo.CameraSway", CameraSway ? 1 : 0); PlayerPrefs.Save();
        }
        private void OnDestroy() { if (_prediction != null) Destroy(_prediction.gameObject); Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
    }
}
