using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Owns generated presentation and host ore bodies, never the persistent world schema.
    public sealed class CampWorld : MonoBehaviour
    {
        public CampSession Session;
        // Ground, rock, iron, gold, wood, green cloth, dark cloth, yellow helmet.
        public Material[] Palette;
        public Font NameFont;

        private class Visual
        {
            public GameObject Root;
        }

        private sealed class CellVisual : Visual
        {
            public Renderer Renderer;
            public float Health = float.NaN;
            public int Version = int.MinValue;
        }

        private sealed class ItemVisual : Visual
        {
            public Rigidbody Body;
            public Collider Collider;
            public Collider HeldTrigger;
            public ItemKind Kind;
            public ItemPlace Place;
            public int Version = int.MinValue;
            public int RevealStage = -1;
        }

        private sealed class MinerVisual : Visual
        {
            public CampMinerModel Model;
        }
        private sealed class BagVisual : Visual { public Rigidbody Body; public Collider Collider; }
        private readonly CampView _poseView = new CampView();

        private readonly Dictionary<string, CellVisual> _cells = new Dictionary<string, CellVisual>();
        private readonly Dictionary<string, ItemVisual> _items = new Dictionary<string, ItemVisual>();
        private readonly Dictionary<string, Visual> _ladders = new Dictionary<string, Visual>();
        private readonly Dictionary<string, MinerVisual> _miners = new Dictionary<string, MinerVisual>();
        private readonly Dictionary<string, BagVisual> _bags = new Dictionary<string, BagVisual>();
        private readonly Dictionary<string, CampPlayer> _hostPlayers = new Dictionary<string, CampPlayer>();
        private readonly Dictionary<string, PublicMiner> _clientPlayers = new Dictionary<string, PublicMiner>();
        private readonly HashSet<string> _online = new HashSet<string>();
        private readonly HashSet<string> _seen = new HashSet<string>();
        private readonly List<string> _remove = new List<string>();
        private readonly Dictionary<int, Material> _fallbackMaterials = new Dictionary<int, Material>();
        private MaterialPropertyBlock _cellTint;
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int StandardColor = Shader.PropertyToID("_Color");
        private Transform _generatedRoot;
        private string _worldId;
        private bool _hostMode;

        private void Awake() => _cellTint = new MaterialPropertyBlock();

        private void LateUpdate()
        {
            CampAuthority authority = Session != null ? Session.Authority : null;
            CampView view = Session != null ? Session.View : null;
            if (authority == null && view == null)
            {
                ClearVisuals();
                _worldId = null;
                return;
            }

            bool host = authority != null;
            string worldId = host ? authority.State.WorldId : view.WorldId;
            if (_worldId != worldId || _hostMode != host)
            {
                ClearVisuals();
                _worldId = worldId;
                _hostMode = host;
            }
            EnsureRoot();
            CampRules rules = host ? authority.Rules : view.Rules;
            if (rules == null)
                return;

            _hostPlayers.Clear();
            _clientPlayers.Clear();
            _online.Clear();
            string selfId = view?.Self?.Id;
            if (host)
            {
                foreach (CampPlayer player in authority.State.Players)
                    _hostPlayers[player.Id] = player;
                foreach (string id in Session.OnlinePlayerIds)
                    _online.Add(id);
                CapturePhysics();
                SyncHost(authority.State, rules, selfId);
            }
            else
            {
                foreach (PublicMiner player in view.Players)
                    _clientPlayers[player.Id] = player;
                SyncClient(view, rules, selfId);
            }
        }

        // Call immediately before a host command or save. A new authoritative version wins
        // over a body which has not yet consumed that drop/throw/ownership change.
        public void CapturePhysics()
        {
            if (Session == null || Session.Authority == null || !_hostMode || _worldId != Session.Authority.State.WorldId)
                return;
            foreach (CampItem item in Session.Authority.State.Items)
            {
                if (item.Place == ItemPlace.Ground && IsBag(item.ContainerId) && _bags.TryGetValue(item.ContainerId,out var bag))
                { item.Position=bag.Body.position;item.Velocity=bag.Body.linearVelocity;continue; }
                if (item.Place != ItemPlace.Ground || !_items.TryGetValue(item.Id, out ItemVisual visual) ||
                    visual.Place != ItemPlace.Ground || visual.Version != item.Version ||
                    visual.Body == null || visual.Body.isKinematic)
                    continue;
                Vector3 position = visual.Body.position;
                Vector3 velocity = visual.Body.linearVelocity;
                if (!CampCodec.IsFinite(position) || !CampCodec.IsFinite(velocity))
                    continue;
                item.Position = position;
                item.Velocity = velocity;
            }
        }

        private void SyncHost(CampState state, CampRules rules, string selfId)
        {
            _seen.Clear();
            foreach (CampCell cell in state.Cells)
                SyncCell(cell.Id, cell.PitId, cell.X, cell.Depth, cell.Z, cell.Dug, cell.Health, cell.Version, rules);
            RemoveMissing(_cells);

            _seen.Clear();
            foreach (CampItem item in state.Items)
            {
                SyncItem(item.Id, item.Kind, item.Place, item.OwnerId, item.Position, item.Velocity, item.Version, true);
                if(item.Kind==ItemKind.RawStone) SyncRaw(item.Id,item.RevealStage,item.RawWindowBand,item.RevealStage>=2?item.CoreKind:"");
            }
            RemoveMissing(_items);
            SyncBags(state.Items.Where(i=>i.Place==ItemPlace.Ground&&IsBag(i.ContainerId)).Select(i=>(i.ContainerId,i.Position)),true);

            SyncLadders(state.Ladders, rules);
            _poseView.OnlineSeconds = state.OnlineSeconds;
            _poseView.Vehicle = state.Vehicle;
            _seen.Clear();
            foreach (CampPlayer player in state.Players)
            {
                if (selfId == null || player.Id == selfId || !_online.Contains(player.Id))
                    continue;
                var held = state.Items.Find(i => i.OwnerId == player.Id && i.Place == ItemPlace.Hand);
                SyncMiner(new PublicMiner { Id=player.Id,Name=player.Name,ColorIndex=player.ColorIndex,Position=player.Position,
                    Yaw=player.Yaw,Pitch=player.Pitch,Action=player.Action,ActionUntil=player.ActionUntil,StunnedUntil=player.StunnedUntil,
                    VehicleSeat=state.Vehicle?.Seats.IndexOf(player.Id)??-1,ToolLevel=player.ToolLevel,
                    ClimbingLadderId=player.ClimbingLadderId,CheeksFull=state.Items.Exists(i=>i.OwnerId==player.Id&&i.Place==ItemPlace.Mouth) },
                    _poseView,held!=null,held?.Kind??ItemKind.Stone,state.Items.Exists(i=>i.AssistantId==player.Id));
            }
            RemoveMissing(_miners);
        }

        private void SyncClient(CampView view, CampRules rules, string selfId)
        {
            _seen.Clear();
            foreach (VisibleCell cell in view.Cells)
                SyncCell(cell.Id, cell.PitId, cell.X, cell.Depth, cell.Z, cell.Dug, cell.Health, cell.Version, rules);
            RemoveMissing(_cells);

            _seen.Clear();
            foreach (VisibleItem item in view.Items)
            {
                SyncItem(item.Id, item.Kind, item.Place, item.OwnerId, item.Position, Vector3.zero, item.Version, false);
                if(item.Kind==ItemKind.RawStone) SyncRaw(item.Id,item.RevealStage,item.WindowBand,item.PublicCore);
            }
            RemoveMissing(_items);
            SyncBags(view.Items.Where(i=>i.Place==ItemPlace.Ground&&IsBag(i.ContainerId)).Select(i=>(i.ContainerId,i.Position)),false);

            SyncLadders(view.Ladders, rules);
            _seen.Clear();
            foreach (PublicMiner player in view.Players)
            {
                if (selfId == null || player.Id == selfId || !player.Online)
                    continue;
                var held = view.Items.Find(i => i.OwnerId == player.Id && i.Place == ItemPlace.Hand);
                SyncMiner(player, view, held != null, held?.Kind ?? ItemKind.Stone, view.Items.Exists(i=>i.AssistantId==player.Id));
            }
            RemoveMissing(_miners);
        }

        private void SyncCell(string id, string pitId, int x, int depth, int z, bool dug, float health, int version, CampRules rules)
        {
            if (dug)
                return;
            _seen.Add(id);
            if (!_cells.TryGetValue(id, out CellVisual visual))
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Mine cell " + id;
                cube.transform.SetParent(_generatedRoot, false);
                cube.transform.position = CampMap.CellCenter(pitId, x, depth, z, rules);
                cube.transform.localScale = Vector3.one * rules.CellSize;
                SetTarget(cube, "cell", id);
                visual = new CellVisual { Root = cube, Renderer = cube.GetComponent<Renderer>() };
                visual.Renderer.sharedMaterial = MaterialAt(1);
                _cells.Add(id, visual);
            }
            if (visual.Version == version && visual.Health == health)
                return;
            float maximum = Mathf.Max(1f, rules.BaseCellHealth + depth * rules.DepthHealthIncrease);
            if (pitId == CampMap.AdvancedPitId) maximum *= rules.AdvancedHealthMultiplier;
            float brightness = Mathf.Lerp(.42f, 1f, Mathf.Clamp01(health / maximum));
            Color color = MaterialColor(visual.Renderer.sharedMaterial, new Color(.37f, .39f, .4f));
            color = new Color(color.r * brightness, color.g * brightness, color.b * brightness, color.a);
            _cellTint.Clear();
            _cellTint.SetColor(BaseColor, color);
            _cellTint.SetColor(StandardColor, color);
            visual.Renderer.SetPropertyBlock(_cellTint);
            visual.Health = health;
            visual.Version = version;
        }

        private void SyncItem(string id, ItemKind kind, ItemPlace place, string ownerId,
            Vector3 position, Vector3 velocity, int version, bool host)
        {
            // Containers and escrow have their own future presentation; never leave a duplicate ore in space.
            if (place != ItemPlace.Ground && place != ItemPlace.Hand && place != ItemPlace.Vehicle)
                return;
            string container = host ? Session.Authority.State.Items.Find(i=>i.Id==id)?.ContainerId : Session.View.Items.Find(i=>i.Id==id)?.ContainerId;
            if (place == ItemPlace.Ground && IsBag(container)) return;
            _seen.Add(id);
            if (_items.TryGetValue(id, out ItemVisual existing) && existing.Kind != kind)
            {
                DestroyVisual(existing);
                _items.Remove(id);
            }
            bool created = !_items.TryGetValue(id, out ItemVisual visual);
            if (created)
            {
                visual = CreateOre(id, kind);
                _items.Add(id, visual);
            }

            Quaternion rotation = Quaternion.identity;
            if (place == ItemPlace.Hand)
                GetHoldPose(ownerId, kind, position, host, out position, out rotation);
            if (place == ItemPlace.Vehicle)
            {
                var car = host ? Session.Authority.State.Vehicle : Session.View.Vehicle;
                if (car != null) rotation = Quaternion.Euler(0, car.Yaw, 0);
            }
            visual.HeldTrigger.enabled = place == ItemPlace.Hand;
            // Ignore only the local holder's interaction trigger, never the ground body's layer.
            visual.HeldTrigger.gameObject.layer = ownerId == Session.View?.Self?.Id ? 2 : 0;

            if (host)
            {
                if (created || visual.Version != version || visual.Place != place)
                {
                    if (!visual.Body.isKinematic)
                    {
                        visual.Body.linearVelocity = Vector3.zero;
                        visual.Body.angularVelocity = Vector3.zero;
                    }
                    bool ground = place == ItemPlace.Ground;
                    visual.Body.isKinematic = !ground;
                    visual.Body.useGravity = ground;
                    visual.Collider.enabled = ground;
                    visual.Body.position = position;
                    visual.Body.rotation = rotation;
                    if (ground)
                    {
                        visual.Body.linearVelocity = velocity;
                        visual.Body.WakeUp();
                    }
                }
                if (place == ItemPlace.Hand || place == ItemPlace.Vehicle)
                {
                    visual.Body.position = position;
                    visual.Body.rotation = rotation;
                }
            }
            else
            {
                visual.Collider.enabled = place == ItemPlace.Ground;
                float blend = created ? 1f : FollowBlend();
                visual.Body.position = Vector3.Lerp(visual.Body.position, position, blend);
                visual.Body.rotation = Quaternion.Slerp(visual.Body.rotation, rotation, blend);
            }
            visual.Place = place;
            visual.Version = version;
        }

        private void GetHoldPose(string ownerId, ItemKind kind, Vector3 fallback, bool host, out Vector3 position, out Quaternion rotation)
        {
            position = fallback;
            rotation = Quaternion.identity;
            float yaw;
            Vector3 feet;
            bool climbing;
            if (host && _hostPlayers.TryGetValue(ownerId, out CampPlayer player))
            {
                feet = player.Position;
                yaw = player.Yaw;
                climbing = !string.IsNullOrEmpty(player.ClimbingLadderId);
            }
            else if (!host && _clientPlayers.TryGetValue(ownerId, out PublicMiner miner))
            {
                feet = miner.Position;
                yaw = miner.Yaw;
                climbing = !string.IsNullOrEmpty(miner.ClimbingLadderId);
            }
            else
                return;
            rotation = Quaternion.Euler(0f, climbing ? 0 : yaw, 0f);
            // Match the articulated carry grips. This is presentation only; the
            // authority still owns drop/throw position, asset ID and ownership.
            position = feet + rotation * CampMinerModel.CarryLocalPosition(kind, climbing);
        }

        private ItemVisual CreateOre(string id, ItemKind kind)
        {
            GameObject root = NewRoot("Ore " + id, "item", id);
            bool heavy = kind == ItemKind.HeavyOre;
            Vector3 size = heavy ? new Vector3(1.1f, .85f, .95f) : new Vector3(.55f, .45f, .5f);
            BoxCollider collider = root.AddComponent<BoxCollider>();
            collider.size = size;
            GameObject interaction = new GameObject("Held ore interaction");
            interaction.transform.SetParent(root.transform, false);
            BoxCollider heldTrigger = interaction.AddComponent<BoxCollider>();
            heldTrigger.size = size;
            heldTrigger.isTrigger = true;
            heldTrigger.enabled = false;
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.mass = heavy ? 16f : 1.5f;
            body.linearDamping = .15f;
            body.angularDamping = .65f;
            body.maxDepenetrationVelocity = 1f;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.interpolation = RigidbodyInterpolation.None;
            IgnoreExistingMiners(collider);

            string modelKey = kind == ItemKind.RawStone ? "RawClosed" : heavy ? "HeavyOre" :
                kind == ItemKind.Gold ? "GoldOre" : kind == ItemKind.Iron ? "IronOre" : "StoneOre";
            var sample = CampArtSet.Spawn(modelKey, root.transform);
            if (sample != null)
            {
                sample.name = "Sample ore visual";
                return new ItemVisual { Root = root, Body = body, Collider = collider, HeldTrigger = heldTrigger, Kind = kind };
            }

            uint shape = 2166136261;
            foreach (char letter in id)
                shape = unchecked((shape ^ letter) * 16777619);
            float turn = shape % 37;
            int baseMaterial = kind == ItemKind.RawStone ? 4 : 1;
            Piece(root.transform, "Stone shell", PrimitiveType.Sphere, Vector3.zero, size * .92f, baseMaterial,
                Quaternion.Euler(8f, turn, -8f));
            Piece(root.transform, "Broken face", PrimitiveType.Cube, new Vector3(-size.x * .18f, .04f, -.04f),
                size * .68f, baseMaterial, Quaternion.Euler(-13f, 24f + turn, 14f));
            if (kind == ItemKind.Iron || kind == ItemKind.Gold || heavy)
            {
                int mineral = kind == ItemKind.Gold ? 3 : 2;
                for (int i = 0; i < 3; i++)
                    Piece(root.transform, "Exposed mineral", PrimitiveType.Cube,
                        new Vector3((i - 1) * size.x * .21f, size.y * .36f, -.06f + i * .05f),
                        size * .3f, mineral, Quaternion.Euler(14f * i, 20f + turn, 15f));
            }
            else if (kind == ItemKind.RawStone)
            {
                // Shape depends on the public ID only, never value, core contents, or reveal information.
                for (int i = 0; i < 3; i++)
                    Piece(root.transform, "Rough unopened crust", PrimitiveType.Cube,
                        new Vector3((i - 1) * .12f, .16f, i % 2 == 0 ? .08f : -.1f),
                        new Vector3(.2f, .14f, .21f), 1, Quaternion.Euler(18f, turn + i * 31f, 12f));
            }
            return new ItemVisual { Root = root, Body = body, Collider = collider, HeldTrigger = heldTrigger, Kind = kind };
        }

        private static bool IsBag(string container) => !string.IsNullOrEmpty(container) &&
            (container.StartsWith("offline-",StringComparison.Ordinal)||container.StartsWith("escape-",StringComparison.Ordinal)||container.StartsWith("fall-",StringComparison.Ordinal)||container.StartsWith("recovery-",StringComparison.Ordinal));

        private void SyncBags(IEnumerable<(string ContainerId,Vector3 Position)> items,bool host)
        {
            _seen.Clear();
            foreach(var group in items.GroupBy(i=>i.ContainerId))
            {
                _seen.Add(group.Key);Vector3 position=group.First().Position;
                bool created=!_bags.TryGetValue(group.Key,out var bag);
                if(created)
                {
                    var root=NewRoot("Dropped cargo bag "+group.Key,"bag",group.Key);
                    Piece(root.transform,"Canvas sack",PrimitiveType.Sphere,Vector3.zero,new Vector3(.62f,.65f,.55f),4);
                    Piece(root.transform,"Tied neck",PrimitiveType.Cylinder,Vector3.up*.33f,new Vector3(.2f,.09f,.2f),6);
                    var collider=root.AddComponent<BoxCollider>();collider.size=new Vector3(.62f,.65f,.55f);
                    var body=root.AddComponent<Rigidbody>();body.mass=2;body.isKinematic=!host;body.useGravity=host;
                    body.linearDamping=.8f;body.angularDamping=2;body.maxDepenetrationVelocity=1;
                    body.constraints=RigidbodyConstraints.FreezeRotation;body.position=position;IgnoreExistingMiners(collider);
                    bag=new BagVisual{Root=root,Body=body,Collider=collider};_bags.Add(group.Key,bag);
                }
                if(!host)bag.Body.position=Vector3.Lerp(bag.Body.position,position,created?1:FollowBlend());
            }
            RemoveMissing(_bags);
        }

        public void IgnoreMinerCollisions(CharacterController miner)
        {
            foreach(var item in _items.Values)if(item.Collider!=null)Physics.IgnoreCollision(miner,item.Collider,true);
            foreach(var bag in _bags.Values)if(bag.Collider!=null)Physics.IgnoreCollision(miner,bag.Collider,true);
        }
        private void IgnoreExistingMiners(Collider cargo)
        { foreach(var miner in Session.GetComponentsInChildren<CharacterController>())Physics.IgnoreCollision(cargo,miner,true); }

        private void SyncRaw(string id,int stage,int windowBand,string core)
        {
            if(!_items.TryGetValue(id,out var item)||item.RevealStage==stage)return;
            item.RevealStage=stage;
            var sample = item.Root.transform.Find("Sample ore visual");
            if (sample != null)
            {
                // Only the public reveal stage/core can select a visible cut surface.
                // The window is deliberately a neutral stone inspection patch.
                string key = CampArtSet.RawModelKey(stage, core);
                var replacement = CampArtSet.Spawn(key, item.Root.transform);
                if (replacement != null) {
                    sample.gameObject.SetActive(false); Destroy(sample.gameObject);
                    replacement.name = "Sample ore visual";
                }
                return;
            }
            var old=item.Root.transform.Find("Raw revealed surface");
            if(old!=null){old.gameObject.SetActive(false);Destroy(old.gameObject);}
            if(stage==0)return;
            int material=stage==1?(windowBand==3?3:windowBand==2?2:1):(core=="gold"||core=="rich_gold")?3:core=="iron"?2:1;
            if(stage>=2)
                foreach(var r in item.Root.GetComponentsInChildren<Renderer>())
                    if(r.name=="Stone shell"||r.name=="Broken face"||r.name=="Rough unopened crust")r.enabled=false;
            Piece(item.Root.transform,"Raw revealed surface",PrimitiveType.Sphere,
                stage==1?new Vector3(0,.14f,-.19f):Vector3.zero,
                stage==1?new Vector3(.25f,.22f,.09f):new Vector3(.4f,.38f,.34f),material);
        }

        private void SyncLadders(List<CampLadder> ladders, CampRules rules)
        {
            _seen.Clear();
            foreach (CampLadder ladder in ladders)
            {
                _seen.Add(ladder.Id);
                if (!_ladders.TryGetValue(ladder.Id, out Visual visual))
                {
                    GameObject root = NewRoot("Ladder " + ladder.Id, "ladder", ladder.Id);
                    float length = rules.CellSize + .15f;
                    var sample = CampArtSet.Spawn("Ladder", root.transform);
                    if (sample != null) sample.localScale = new Vector3(1, length / 2.15f, 1);
                    else {
                    foreach (float side in new[] { -.34f, .34f })
                        Piece(root.transform, "Rail", PrimitiveType.Cylinder, new Vector3(side, 0f, 0f),
                            new Vector3(.09f, length * .5f, .09f), 4);
                    for (float y = -rules.CellSize * .5f + .2f; y <= rules.CellSize * .5f; y += .34f)
                        Piece(root.transform, "Rung", PrimitiveType.Cube, new Vector3(0f, y, 0f),
                            new Vector3(.76f, .075f, .1f), 4);
                    Piece(root.transform, "Climb direction", PrimitiveType.Cube, new Vector3(0f, .5f, -.12f),
                        new Vector3(.055f, .4f, .045f), 7);
                    Piece(root.transform, "Arrow left", PrimitiveType.Cube, new Vector3(-.07f, .65f, -.12f),
                        new Vector3(.045f, .21f, .045f), 7, Quaternion.Euler(0f, 0f, -40f));
                    Piece(root.transform, "Arrow right", PrimitiveType.Cube, new Vector3(.07f, .65f, -.12f),
                        new Vector3(.045f, .21f, .045f), 7, Quaternion.Euler(0f, 0f, 40f));
                    }
                    BoxCollider trigger = root.AddComponent<BoxCollider>();
                    trigger.isTrigger = true;
                    trigger.size = new Vector3(.95f, length, .35f);
                    visual = new Visual { Root = root };
                    _ladders.Add(ladder.Id, visual);
                }
                visual.Root.transform.position = CampMap.LadderCenter(ladder, rules);
            }
            RemoveMissing(_ladders);
        }

        private void SyncMiner(PublicMiner miner, CampView view, bool hasHand, ItemKind handKind, bool assisting)
        {
            _seen.Add(miner.Id);
            bool created = !_miners.TryGetValue(miner.Id, out MinerVisual visual);
            if (created)
            {
                var go = NewRoot("Miner " + miner.Id, "player", miner.Id);
                var trigger = go.AddComponent<BoxCollider>(); trigger.isTrigger = true;
                trigger.center = Vector3.up * .9f; trigger.size = new Vector3(.75f,1.8f,.6f);
                var model = CampMinerModel.Create(go.transform, Palette); model.SetNameFont(NameFont);
                visual = new MinerVisual { Root = go, Model = model }; _miners.Add(miner.Id, visual);
            }
            Transform root = visual.Root.transform;
            bool seated = miner.VehicleSeat >= 0 && view.Vehicle != null;
            var vehicleMotion = Session.GetComponent<CampVehicleMotion>();
            root.position = seated ? vehicleMotion.SeatPosition(miner.VehicleSeat, view.Vehicle) :
                Vector3.Lerp(root.position, miner.Position, created ? 1f : FollowBlend());
            bool climbing = !seated && !string.IsNullOrEmpty(miner.ClimbingLadderId);
            root.rotation = Quaternion.Slerp(root.rotation, Quaternion.Euler(0,seated?vehicleMotion.VisualYaw(view.Vehicle):climbing?0:miner.Yaw,0), created ? 1f : FollowBlend());
            visual.Model.Pose(miner, view, hasHand, handKind, assisting);
        }
        private GameObject NewRoot(string name, string kind, string id)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(_generatedRoot, false);
            SetTarget(root, kind, id);
            return root;
        }

        private static void SetTarget(GameObject root, string kind, string id)
        {
            CampTarget target = root.AddComponent<CampTarget>();
            target.Kind = kind;
            target.Id = id;
        }

        private void Piece(Transform parent, string name, PrimitiveType type, Vector3 position,
            Vector3 scale, int material, Quaternion? rotation = null)
        {
            GameObject piece = GameObject.CreatePrimitive(type);
            piece.name = name;
            piece.transform.SetParent(parent, false);
            piece.transform.localPosition = position;
            piece.transform.localScale = scale;
            piece.transform.localRotation = rotation ?? Quaternion.identity;
            Collider collider = piece.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Destroy(collider);
            }
            piece.GetComponent<Renderer>().sharedMaterial = MaterialAt(material);
        }

        private Material MaterialAt(int index)
        {
            if (Palette != null && index < Palette.Length && Palette[index] != null)
                return Palette[index];
            if (_fallbackMaterials.TryGetValue(index, out Material material))
                return material;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null)
                return null;
            Color[] colors = { new Color(.29f, .35f, .22f), new Color(.37f, .39f, .4f), new Color(.3f, .44f, .53f),
                new Color(.92f, .67f, .12f), new Color(.47f, .29f, .16f), new Color(.23f, .5f, .27f),
                new Color(.12f, .16f, .16f), new Color(1f, .77f, .12f) };
            material = new Material(shader) { name = "Camp fallback " + index, color = colors[Mathf.Clamp(index, 0, colors.Length - 1)] };
            _fallbackMaterials.Add(index, material);
            return material;
        }

        private static Color MaterialColor(Material material, Color fallback)
        {
            if (material == null)
                return fallback;
            if (material.HasProperty(BaseColor))
                return material.GetColor(BaseColor);
            return material.HasProperty(StandardColor) ? material.GetColor(StandardColor) : fallback;
        }

        private void EnsureRoot()
        {
            if (_generatedRoot != null)
                return;
            _generatedRoot = new GameObject("Camp generated world").transform;
            _generatedRoot.SetParent(transform, false);
        }

        private void RemoveMissing<T>(Dictionary<string, T> visuals) where T : Visual
        {
            _remove.Clear();
            foreach (var pair in visuals)
                if (!_seen.Contains(pair.Key))
                    _remove.Add(pair.Key);
            foreach (string id in _remove)
            {
                DestroyVisual(visuals[id]);
                visuals.Remove(id);
            }
        }

        private void DestroyVisual(Visual visual)
        {
            if (visual.Root == null)
                return;
            // Disable now so a dug cell or removed item cannot block this frame's later queries.
            visual.Root.SetActive(false);
            Destroy(visual.Root);
        }

        private void ClearVisuals()
        {
            _cells.Clear();
            _items.Clear();
            _ladders.Clear();
            _miners.Clear();
            _bags.Clear();
            _hostPlayers.Clear();
            _clientPlayers.Clear();
            if (_generatedRoot != null)
            {
                _generatedRoot.gameObject.SetActive(false);
                Destroy(_generatedRoot.gameObject);
                _generatedRoot = null;
            }
        }

        private static float FollowBlend() => 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);

        public static bool HasSight(Vector3 start, Vector3 end)
        {
            if (!CampCodec.IsFinite(start) || !CampCodec.IsFinite(end))
                return false;
            Vector3 delta = end - start;
            float distance = delta.magnitude;
            CampTarget allowedTarget = null;
            if (distance > .0001f)
            {
                RaycastHit[] hits = Physics.RaycastAll(start, delta / distance, distance, ~0, QueryTriggerInteraction.Ignore);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (RaycastHit hit in hits)
                {
                    if (IgnoredForSight(hit.collider))
                        continue;
                    CampTarget target = hit.collider.GetComponentInParent<CampTarget>();
                    if (allowedTarget == null && IsTargetCenter(target, end))
                        allowedTarget = target;
                    if (target == null || target != allowedTarget)
                        return false;
                }
            }
            // Raycasts omit a collider containing the origin. Endpoints inside some other
            // obstacle must also fail, even if the intended ore surface was hit first.
            return EndpointClear(start, allowedTarget) && EndpointClear(end, allowedTarget);
        }

        private static bool IsTargetCenter(CampTarget target, Vector3 end) => target != null &&
            (target.Kind == "item" || target.Kind == "cell" || target.Kind == "bag" || target.Kind == "vehicle") && (target.transform.position - end).sqrMagnitude <= .000001f;

        private static bool IgnoredForSight(Collider collider) => collider == null || collider.isTrigger || collider is CharacterController;

        private static bool EndpointClear(Vector3 point, CampTarget allowedTarget)
        {
            foreach (Collider collider in Physics.OverlapSphere(point, .002f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (IgnoredForSight(collider))
                    continue;
                // OverlapSphere also returns nearby surfaces: only a containing/touching solid blocks the point.
                if ((collider.ClosestPoint(point) - point).sqrMagnitude > .0000001f)
                    continue;
                if (allowedTarget == null || collider.GetComponentInParent<CampTarget>() != allowedTarget)
                    return false;
            }
            return true;
        }

        private void OnDisable()
        {
            CapturePhysics();
            ClearVisuals();
        }

        private void OnDestroy()
        {
            ClearVisuals();
            foreach (Material material in _fallbackMaterials.Values)
                if (material != null)
                    Destroy(material);
            _fallbackMaterials.Clear();
        }
    }
}
