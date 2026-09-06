using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace GoldCamp.Demo
{
    [Serializable] public sealed class CampPacket { public string Type; public string Payload; }
    [Serializable] public sealed class CampIdentity { public string Id; public string Secret; public string Name; }
    [Serializable] public sealed class CampInputFrame
    {
        public int Sequence;
        public float X, Z, Yaw, Pitch;
        public bool Jump;
    }

    // Gameplay protocol and lifetime sit above FishNet. The connection, never a
    // client-supplied actor field, selects which persistent account may act.
    public sealed class CampSession : MonoBehaviour
    {
        public DemoConfig Config;
        public CampView View { get; private set; }
        public CampAuthority Authority { get; private set; }
        public CampTransport Transport { get; private set; }
        public string Feedback { get; private set; } = "继续已有营地，或独立新建一个营地。";
        public string SaveDirectory { get; private set; }
        public bool IsTestSession { get; private set; }
        public string TestRoot { get; private set; }
        public string PlayerName = "矿工";
        public event Action<CampResult> CommandResult;
        public event Action<CampView> ViewChanged;
        public event Action<string, CampInputFrame> InputReceived;
        public ICollection<string> OnlinePlayerIds => _actors.Values;
        private CampIdentity _identity;
        private CampSaveStore _store;
        private readonly Dictionary<int, string> _actors = new Dictionary<int, string>();
        private readonly HashSet<string> _pendingDepartures = new HashSet<string>();
        private float _nextDepartureRetry;
        private bool _ending;
        private float _nextSave, _nextView;
        private string _worldsRoot;
        private ushort _port = 7770;

        private void Awake()
        {
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
            Time.timeScale = 1f;
            string testRoot = Argument("--gc-test-root");
            if (!string.IsNullOrEmpty(testRoot))
            {
                testRoot = Path.GetFullPath(testRoot);
                if (!Debug.isDebugBuild || !File.Exists(Path.Combine(testRoot, ".goldcamp-test-root")))
                    throw new InvalidOperationException("测试目录必须由测试器独立创建并带标记。");
                if (testRoot.StartsWith(Path.GetFullPath(Application.persistentDataPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("测试不能指向真人存档目录。");
                IsTestSession = true; TestRoot = testRoot;
            }
            string profileRoot = IsTestSession ? Path.Combine(TestRoot, "Profile") : Path.Combine(Application.persistentDataPath, "Identity");
            _worldsRoot = IsTestSession ? Path.Combine(TestRoot, "Camps") : Path.Combine(Application.persistentDataPath, "Camps");
            Directory.CreateDirectory(profileRoot);
            string profilePath = Path.Combine(profileRoot, "player.json");
            if (File.Exists(profilePath))
            {
                _identity = JsonUtility.FromJson<CampIdentity>(File.ReadAllText(profilePath));
                if (_identity == null || !Guid.TryParseExact(_identity.Id, "N", out _) || string.IsNullOrEmpty(_identity.Secret))
                    throw new InvalidOperationException("玩家身份文件损坏；保留文件，请从备份恢复。");
            }
            else
            {
                byte[] secret = new byte[32];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(secret);
                _identity = new CampIdentity { Id = Guid.NewGuid().ToString("N"), Secret = Convert.ToBase64String(secret), Name = PlayerName };
                using (var stream = new FileStream(profilePath, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream)) { writer.Write(JsonUtility.ToJson(_identity)); writer.Flush(); stream.Flush(true); }
            }
            if (ushort.TryParse(Argument("--gc-port"), out var port)) _port = port;
            Transport = gameObject.AddComponent<CampTransport>();
            Transport.ServerMessage += ReceiveServer;
            Transport.ClientMessage += ReceiveClient;
            Transport.ClientReady += ClientReady;
            Transport.ClientStopped += ClientStopped;
            Transport.PeerDisconnected += Disconnected;
        }

        private void Start()
        {
            if (HasArgument("--gc-host")) StartHost(HasArgument("--gc-new"));
            else if (!string.IsNullOrEmpty(Argument("--gc-connect"))) Join(Argument("--gc-connect"));
            if (IsTestSession) gameObject.AddComponent<CampTestDriver>().Initialize(this);
        }

        public void StartHost(bool createNew)
        {
            if (Authority != null || Transport.IsClientConnected || _store != null) return;
            try
            {
                CampState state;
                if (createNew)
                {
                    int seed = Environment.TickCount;
                    if (IsTestSession && int.TryParse(Argument("--gc-seed"), out int testSeed)) seed = testSeed;
                    state = CampAuthority.NewWorld("淘金营地 " + DateTime.Now.ToString("MM-dd HH:mm"), seed);
                    SaveDirectory = Path.Combine(_worldsRoot, state.WorldId);
                    _store = new CampSaveStore(SaveDirectory);
                    if (_store.Exists) throw new IOException("新营地目录已经存在，拒绝覆盖。");
                    if (IsTestSession && HasArgument("--gc-test-fixture"))
                        state.Items.Add(new CampItem { Id = "a-ore", Kind = ItemKind.Gold, Position = CampAuthority.Spawn + Vector3.forward * 1.2f, HiddenValue = 12345, CoreKind = "host-secret-test" });
                    if (IsTestSession && HasArgument("--gc-test-social"))
                    {
                        state.Items.Add(new CampItem { Id="c-gold-a",Kind=ItemKind.Gold,Quantity=10,Position=new Vector3(-2,.6f,-3) });
                        state.Items.Add(new CampItem { Id="c-gold-b",Kind=ItemKind.Gold,Quantity=10,Position=new Vector3(2,.6f,-3) });
                        state.Items.Add(new CampItem { Id="c-raw-a",Kind=ItemKind.RawStone,HiddenValue=120,CoreKind="gold",Position=new Vector3(-2,.6f,-5) });
                        state.Items.Add(new CampItem { Id="c-raw-b",Kind=ItemKind.RawStone,HiddenValue=240,CoreKind="gold",Position=new Vector3(2,.6f,-5) });
                        state.Items.Add(new CampItem { Id="c-mouth-gold",Kind=ItemKind.Gold,Position=new Vector3(-3,.6f,-3) });
                        state.Items.Add(new CampItem { Id="c-rob-stone",Kind=ItemKind.Stone,Position=new Vector3(-3,.6f,-5) });
                        state.Items.Add(new CampItem { Id="c-rob-stone-2",Kind=ItemKind.Stone,Position=new Vector3(-3,.6f,-6) });
                    }
                }
                else
                {
                    SaveDirectory = LatestCampDirectory();
                    if (SaveDirectory == null) { Feedback = "还没有营地，请使用「新建营地」。"; return; }
                    _store = new CampSaveStore(SaveDirectory);
                    state = _store.Load();
                }
                Authority = new CampAuthority(state, Config != null ? Config.Rules : new CampRules(), s => _store.Save(s), CampWorld.HasSight,
                    (position, radius) => !Physics.CheckSphere(position, radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore),
                    id => _actors.Values.Contains(id), id => id == _identity.Id);
                // Test fixtures for checkpoint A intentionally retain their exact minimal world.
                if (!(IsTestSession && HasArgument("--gc-test-fixture"))) Authority.InitializeMining();
                Authority.RecoverSocial();
                Authority.Save();
                Feedback = string.IsNullOrEmpty(_store.RecoveryNotice) ? "营地已保存。" : _store.RecoveryNotice;
                _ending = false;
                _nextSave = Time.unscaledTime + Authority.Rules.AutoSaveSeconds;
                if (!Transport.StartHost(_port, IsTestSession)) throw new IOException("网络监听未能启动；存档已保留。");
                Debug.Log("GOLDCAMP_HOST_READY world=" + state.WorldId + " port=" + _port);
            }
            catch (Exception error)
            {
                Feedback = "无法打开营地：" + error.Message;
                Debug.LogError(Feedback);
                _store?.Dispose(); _store = null; Authority = null;
            }
        }

        public void Join(string address)
        {
            if (Authority != null || Transport.IsClientConnected) return;
            if (string.IsNullOrWhiteSpace(address)) { Feedback = "请输入房主地址。"; return; }
            _ending = false;
            Feedback = Transport.StartClient(address.Trim(), _port) ? "正在连接房主…" : "连接未能启动，请先离开当前连接再重试。";
        }
        public string LatestCampDirectory()
        {
            if (!Directory.Exists(_worldsRoot)) return null;
            return Directory.GetDirectories(_worldsRoot).Where(d => Guid.TryParseExact(Path.GetFileName(d), "N", out _))
                .Where(d => File.Exists(Path.Combine(d, "world.camp.json")) || File.Exists(Path.Combine(d, "world.camp.json.bak1")) || File.Exists(Path.Combine(d, "world.camp.json.bak2")))
                .OrderByDescending(d => File.Exists(Path.Combine(d, "world.camp.json")) ? File.GetLastWriteTimeUtc(Path.Combine(d, "world.camp.json")) : Directory.GetLastWriteTimeUtc(d)).FirstOrDefault();
        }
        private void ClientReady()
        {
            _identity.Name = string.IsNullOrWhiteSpace(PlayerName) ? "矿工" : PlayerName;
            Send("hello", JsonUtility.ToJson(_identity));
        }
        private void ClientStopped()
        {
            if (_ending) return;
            View = null;
            Feedback = "与房主的连接已断开。资产以房主最近成功保存为准；离开后可重新加入。";
            // Stop releases the old transport so the same menu can connect again.
            Transport.Stop();
        }
        public void SendCommand(CampCommand command) => Send("command", JsonUtility.ToJson(command));
        public void SendInput(CampInputFrame input) => Send("input", JsonUtility.ToJson(input));
        private void Send(string type, string payload) => Transport.SendToHost(JsonUtility.ToJson(new CampPacket { Type = type, Payload = payload }));
        private void Reply(int peer, string type, string payload) => Transport.SendToClient(peer, JsonUtility.ToJson(new CampPacket { Type = type, Payload = payload }));

        private void ReceiveServer(int peer, string text)
        {
            if (Authority == null || _ending) return;
            try
            {
                var packet = JsonUtility.FromJson<CampPacket>(text);
                if (packet == null) return;
                if (packet.Type == "hello")
                {
                    if (_actors.ContainsKey(peer)) return;
                    var identity = JsonUtility.FromJson<CampIdentity>(packet.Payload);
                    if (identity != null && _pendingDepartures.Contains(identity.Id))
                    { Reply(peer, "error", "上次离线处理正在重试保存，请稍后再连接。"); return; }
                    if (_actors.Count >= 4 || identity == null || _actors.ContainsValue(identity.Id))
                    { Reply(peer, "error", "房间已满或该玩家已在线。"); return; }
                    Authority.Join(identity.Id, identity.Secret, identity.Name);
                    _actors.Add(peer, identity.Id);
                    BroadcastViews();
                    Debug.Log("GOLDCAMP_PLAYER_JOIN peer=" + peer + " player=" + identity.Id);
                }
                else if (packet.Type == "command" && _actors.TryGetValue(peer, out var actor))
                {
                    GetComponent<CampWorld>()?.CapturePhysics();
                    var command = JsonUtility.FromJson<CampCommand>(packet.Payload);
                    long before = Authority.State.Revision;
                    CampResult result = Authority.Execute(actor, command);
                    Reply(peer, "result", JsonUtility.ToJson(result));
                    if (result.Success && command.Action == "sell" && Authority.State.Revision > before)
                    {
                        var seller = Authority.State.Players.Find(p=>p.Id==actor);
                        foreach(var nearby in _actors)
                        {
                            var recipient=Authority.State.Players.Find(p=>p.Id==nearby.Value);
                            if(recipient!=null&&Vector3.Distance(recipient.Position,seller.Position)<=12)
                                Reply(nearby.Key,"notice",seller.Name+" 售出矿物，收入 "+result.Amount+" 金币。");
                        }
                    }
                    BroadcastViews();
                }
                else if (packet.Type == "input" && _actors.TryGetValue(peer, out var movingActor))
                {
                    var input = JsonUtility.FromJson<CampInputFrame>(packet.Payload);
                    if (input != null && float.IsFinite(input.X) && float.IsFinite(input.Z) && float.IsFinite(input.Yaw) && float.IsFinite(input.Pitch))
                        InputReceived?.Invoke(movingActor, input);
                }
            }
            catch (Exception error)
            {
                Reply(peer, "error", "请求失败：" + error.GetType().Name);
                Debug.LogWarning("GOLDCAMP_REQUEST_REJECTED peer=" + peer + " reason=" + error.GetType().Name);
            }
        }

        private void ReceiveClient(string text)
        {
            var packet = JsonUtility.FromJson<CampPacket>(text);
            if (packet == null) return;
            if (packet.Type == "view")
            {
                View = JsonUtility.FromJson<CampView>(packet.Payload);
                if (View.Wager != null && string.IsNullOrEmpty(View.Wager.Id)) View.Wager = null;
                ViewChanged?.Invoke(View);
            }
            else if (packet.Type == "result")
            {
                var result = JsonUtility.FromJson<CampResult>(packet.Payload);
                Feedback = result.Message;
                CommandResult?.Invoke(result);
            }
            else if (packet.Type == "error" || packet.Type == "ended" || packet.Type == "notice") Feedback = packet.Payload;
        }
        private void BroadcastViews()
        {
            if (Authority == null || _ending) return;
            foreach (var pair in _actors) Reply(pair.Key, "view", JsonUtility.ToJson(CampProjection.ForPlayer(Authority, pair.Value, _actors.Values)));
        }
        private void Disconnected(int peer)
        {
            if (!_actors.TryGetValue(peer, out var actor)) return;
            _actors.Remove(peer);
            if (!_ending && Authority != null)
            {
                _pendingDepartures.Add(actor);
                try { FlushDepartures(); BroadcastViews(); }
                catch (Exception error) { Feedback = "离线保存失败：" + error.Message; Debug.LogError(Feedback); }
            }
        }
        private void Update()
        {
            if (Authority == null || _ending) return;
            Authority.State.OnlineSeconds += Time.unscaledDeltaTime;
            try { Authority.TickWorld(_actors.Values); }
            catch (Exception error) { Feedback = "营地事件保存失败，将重试：" + error.GetType().Name; }
            if (_pendingDepartures.Count > 0 && Time.unscaledTime >= _nextDepartureRetry)
            {
                _nextDepartureRetry = Time.unscaledTime + 3f;
                try { FlushDepartures(); BroadcastViews(); }
                catch (Exception) { Feedback = "离线物品保存仍未成功，相关玩家暂时不能重连。"; }
            }
            if (Time.unscaledTime >= _nextSave)
            {
                _nextSave = Time.unscaledTime + Authority.Rules.AutoSaveSeconds;
                try { FlushDepartures(); GetComponent<CampWorld>()?.CapturePhysics(); Authority.Save(); }
                catch (Exception error) { Feedback = "自动保存失败：" + error.Message; Debug.LogError(Feedback); }
            }
            if (Time.unscaledTime >= _nextView) { _nextView = Time.unscaledTime + .2f; BroadcastViews(); }
        }
        public void EndSession(bool quitApplication)
        {
            if (_ending) return;
            if (Authority != null)
            {
                try { FlushDepartures(); GetComponent<CampWorld>()?.CapturePhysics(); Authority.Save(); }
                catch (Exception error) { Feedback = "保存失败，未结束营地：" + error.Message; return; }
                foreach (int peer in _actors.Keys) Reply(peer, "ended", "房主结束了会话，营地进度已保存。");
            }
            _ending = true;
            StartCoroutine(StopAfterNotice(quitApplication));
        }
        private void FlushDepartures()
        {
            foreach (string actor in _pendingDepartures.ToArray())
            {
                Authority.Disconnect(actor);
                _pendingDepartures.Remove(actor);
            }
        }
        private IEnumerator StopAfterNotice(bool quit)
        {
            yield return new WaitForSecondsRealtime(.35f);
            Transport.Stop(); _actors.Clear(); _pendingDepartures.Clear(); View = null; Authority = null;
            _store?.Dispose(); _store = null;
            if (quit) Application.Quit(); else _ending = false;
        }
        private void OnApplicationQuit()
        {
            if (!_ending && Authority != null)
            {
                _ending = true;
                try { FlushDepartures(); GetComponent<CampWorld>()?.CapturePhysics(); Authority.Save(); } catch (Exception error) { Debug.LogError("营地退出保存失败：" + error.GetType().Name); }
            }
        }
        private void OnDestroy()
        {
            _ending = true;
            if (Transport != null)
            {
                Transport.ServerMessage -= ReceiveServer; Transport.ClientMessage -= ReceiveClient;
                Transport.ClientReady -= ClientReady; Transport.PeerDisconnected -= Disconnected;
                Transport.ClientStopped -= ClientStopped;
                Transport.Stop();
            }
            _store?.Dispose();
        }
        public static bool HasArgument(string key) => Environment.GetCommandLineArgs().Contains(key);
        public static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
