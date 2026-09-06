using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Owns generated presentation and host ore bodies, never the persistent world schema.
    public sealed class CampWorld : MonoBehaviour
    {
        public CampSession Session;
        // Ground, rock, iron, gold, wood, green cloth, dark cloth, yellow helmet.
        public Material[] Palette;

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
        }

        private sealed class MinerVisual : Visual
        {
            public Transform LeftArm, RightArm, LeftLeg, RightLeg;
            public Vector3 PreviousPosition;
        }

        private readonly Dictionary<string, CellVisual> _cells = new Dictionary<string, CellVisual>();
        private readonly Dictionary<string, ItemVisual> _items = new Dictionary<string, ItemVisual>();
        private readonly Dictionary<string, Visual> _ladders = new Dictionary<string, Visual>();
        private readonly Dictionary<string, MinerVisual> _miners = new Dictionary<string, MinerVisual>();
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
                SyncCell(cell.Id, cell.X, cell.Depth, cell.Z, cell.Dug, cell.Health, cell.Version, rules);
            RemoveMissing(_cells);

            _seen.Clear();
            foreach (CampItem item in state.Items)
                SyncItem(item.Id, item.Kind, item.Place, item.OwnerId, item.Position, item.Velocity, item.Version, true);
            RemoveMissing(_items);

            SyncLadders(state.Ladders, rules);
            _seen.Clear();
            foreach (CampPlayer player in state.Players)
            {
                if (selfId == null || player.Id == selfId || !_online.Contains(player.Id))
                    continue;
                SyncMiner(player.Id, player.Position, player.Yaw, player.Action,
                    player.ActionUntil > state.OnlineSeconds, !string.IsNullOrEmpty(player.ClimbingLadderId));
            }
            RemoveMissing(_miners);
        }

        private void SyncClient(CampView view, CampRules rules, string selfId)
        {
            _seen.Clear();
            foreach (VisibleCell cell in view.Cells)
                SyncCell(cell.Id, cell.X, cell.Depth, cell.Z, cell.Dug, cell.Health, cell.Version, rules);
            RemoveMissing(_cells);

            _seen.Clear();
            foreach (VisibleItem item in view.Items)
                SyncItem(item.Id, item.Kind, item.Place, item.OwnerId, item.Position, Vector3.zero, item.Version, false);
            RemoveMissing(_items);

            SyncLadders(view.Ladders, rules);
            _seen.Clear();
            foreach (PublicMiner player in view.Players)
            {
                if (selfId == null || player.Id == selfId || !player.Online)
                    continue;
                SyncMiner(player.Id, player.Position, player.Yaw, player.Action,
                    player.ActionUntil > view.OnlineSeconds, !string.IsNullOrEmpty(player.ClimbingLadderId));
            }
            RemoveMissing(_miners);
        }

        private void SyncCell(string id, int x, int depth, int z, bool dug, float health, int version, CampRules rules)
        {
            if (dug)
                return;
            _seen.Add(id);
            if (!_cells.TryGetValue(id, out CellVisual visual))
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Mine cell " + id;
                cube.transform.SetParent(_generatedRoot, false);
                cube.transform.position = CampMap.CellCenter(x, depth, z, rules);
                cube.transform.localScale = Vector3.one * rules.CellSize;
                SetTarget(cube, "cell", id);
                visual = new CellVisual { Root = cube, Renderer = cube.GetComponent<Renderer>() };
                visual.Renderer.sharedMaterial = MaterialAt(1);
                _cells.Add(id, visual);
            }
            if (visual.Version == version && visual.Health == health)
                return;
            float maximum = Mathf.Max(1f, rules.BaseCellHealth + depth * rules.DepthHealthIncrease);
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
            if (place != ItemPlace.Ground && place != ItemPlace.Hand)
                return;
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
                GetHoldPose(ownerId, position, host, out position, out rotation);
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
                if (place == ItemPlace.Hand)
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

        private void GetHoldPose(string ownerId, Vector3 fallback, bool host, out Vector3 position, out Quaternion rotation)
        {
            position = fallback;
            rotation = Quaternion.identity;
            float yaw;
            Vector3 feet;
            if (host && _hostPlayers.TryGetValue(ownerId, out CampPlayer player))
            {
                feet = player.Position;
                yaw = player.Yaw;
            }
            else if (!host && _clientPlayers.TryGetValue(ownerId, out PublicMiner miner))
            {
                feet = miner.Position;
                yaw = miner.Yaw;
            }
            else
                return;
            rotation = Quaternion.Euler(0f, yaw, 0f);
            position = feet + rotation * Vector3.forward * 1.15f + Vector3.up;
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
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.interpolation = RigidbodyInterpolation.None;

            uint shape = 2166136261;
            foreach (char letter in id)
                shape = unchecked((shape ^ letter) * 16777619);
            float turn = shape % 37;
            int baseMaterial = kind == ItemKind.RawStone ? 4 : 1;
            Piece(root.transform, "Stone shell", PrimitiveType.Cube, Vector3.zero, size * .78f, baseMaterial,
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

        private void SyncMiner(string id, Vector3 position, float yaw, string action, bool actionActive, bool climbing)
        {
            _seen.Add(id);
            bool created = !_miners.TryGetValue(id, out MinerVisual visual);
            if (created)
            {
                visual = CreateMiner(id);
                _miners.Add(id, visual);
            }
            Transform root = visual.Root.transform;
            root.position = Vector3.Lerp(root.position, position, created ? 1f : FollowBlend());
            root.rotation = Quaternion.Slerp(root.rotation, Quaternion.Euler(0f, yaw, 0f), created ? 1f : FollowBlend());
            float motion = created ? 0f : Mathf.Clamp01((root.position - visual.PreviousPosition).magnitude / Mathf.Max(.001f, Time.unscaledDeltaTime));
            float swing = Mathf.Sin(Time.unscaledTime * 9f) * 28f * motion;
            visual.LeftLeg.localRotation = Quaternion.Euler(swing, 0f, 0f);
            visual.RightLeg.localRotation = Quaternion.Euler(-swing, 0f, 0f);
            visual.LeftArm.localRotation = Quaternion.Euler(climbing ? -140f + swing : -swing, 0f, -8f);
            visual.RightArm.localRotation = Quaternion.Euler(climbing ? -140f - swing : swing, 0f, 8f);
            if (actionActive && action == "mine")
                visual.RightArm.localRotation = Quaternion.Euler(-85f + Mathf.Sin(Time.unscaledTime * 12f) * 40f, 0f, 8f);
            visual.PreviousPosition = root.position;
        }

        private MinerVisual CreateMiner(string id)
        {
            GameObject root = NewRoot("Miner " + id, "player", id);
            BoxCollider trigger = root.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, .95f, 0f);
            trigger.size = new Vector3(.72f, 1.9f, .6f);
            Piece(root.transform, "Green work jacket", PrimitiveType.Sphere, new Vector3(0f, 1.12f, 0f), new Vector3(.67f, .87f, .47f), 5);
            Piece(root.transform, "Dark belt", PrimitiveType.Cube, new Vector3(0f, .82f, 0f), new Vector3(.53f, .16f, .41f), 6);
            Piece(root.transform, "Head", PrimitiveType.Sphere, new Vector3(0f, 1.61f, 0f), Vector3.one * .4f, 4);
            Piece(root.transform, "Helmet brim", PrimitiveType.Cylinder, new Vector3(0f, 1.79f, .02f), new Vector3(.52f, .035f, .49f), 7);
            Piece(root.transform, "Helmet dome", PrimitiveType.Sphere, new Vector3(0f, 1.81f, 0f), new Vector3(.42f, .24f, .41f), 7);
            Piece(root.transform, "Helmet lamp", PrimitiveType.Cylinder, new Vector3(0f, 1.8f, .24f), new Vector3(.11f, .035f, .11f), 2, Quaternion.Euler(90f, 0f, 0f));
            var miner = new MinerVisual { Root = root };
            miner.LeftArm = Limb(root.transform, "Left arm", new Vector3(-.37f, 1.4f, 0f), .45f, 5, 4);
            miner.RightArm = Limb(root.transform, "Right arm", new Vector3(.37f, 1.4f, 0f), .45f, 5, 4);
            miner.LeftLeg = Limb(root.transform, "Left leg", new Vector3(-.17f, .76f, 0f), .6f, 6, 6);
            miner.RightLeg = Limb(root.transform, "Right leg", new Vector3(.17f, .76f, 0f), .6f, 6, 6);
            return miner;
        }

        private Transform Limb(Transform parent, string name, Vector3 pivot, float length, int cloth, int tip)
        {
            Transform joint = new GameObject(name).transform;
            joint.SetParent(parent, false);
            joint.localPosition = pivot;
            Piece(joint, "Cloth", PrimitiveType.Cylinder, Vector3.down * length * .5f,
                new Vector3(.16f, length * .5f, .16f), cloth);
            Piece(joint, "Hand or boot", PrimitiveType.Cube, Vector3.down * length + Vector3.forward * .035f,
                new Vector3(.19f, .17f, .25f), tip);
            return joint;
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
            (target.Kind == "item" || target.Kind == "cell") && (target.transform.position - end).sqrMagnitude <= .000001f;

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
