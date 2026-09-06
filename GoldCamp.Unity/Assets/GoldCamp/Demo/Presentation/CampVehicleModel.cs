using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Presentation only. The caller owns vehicle placement, collisions, seats and cargo.
    // Local forward is +Z. Wheels touch y=0; all geometry fits 2.2 x 4.4 x 2.6 metres.
    public sealed class CampVehicleModel : MonoBehaviour
    {
        private const float WheelRadius = .40f;
        private readonly List<Mesh> _ownedMeshes = new List<Mesh>();
        private readonly List<Material> _ownedMaterials = new List<Material>();
        private readonly Transform[] _wheelRoll = new Transform[4];
        private readonly Transform[] _frontSteer = new Transform[2];
        private readonly Renderer[] _headlights = new Renderer[2];
        private Material[] _palette;
        private Material _fallback;
        private MaterialPropertyBlock _properties;
        private Mesh _box, _cylinder, _ball, _tire, _arch, _ring;
        private Transform _hood, _steeringWheel, _damage, _bumper;
        private int _baseColor, _color;
        private bool _built, _repaired;
        private float _speed, _steering, _roll;

        private static readonly Color Paint = new Color(.23f, .44f, .41f);
        private static readonly Color Edge = new Color(.32f, .55f, .49f);
        private static readonly Color DarkPaint = new Color(.15f, .28f, .27f);
        private static readonly Color Rubber = new Color(.095f, .10f, .10f);
        private static readonly Color Steel = new Color(.28f, .32f, .31f);
        private static readonly Color Rim = new Color(.66f, .63f, .49f);
        private static readonly Color Wood = new Color(.43f, .26f, .12f);
        private static readonly Color WoodEdge = new Color(.57f, .36f, .18f);
        private static readonly Color Rust = new Color(.46f, .25f, .13f);
        private static readonly Color Seat = new Color(.25f, .20f, .14f);
        private static readonly Color WarmLamp = new Color(1f, .91f, .52f);

        private void Awake()
        {
            if (_properties != null) return;
            _properties = new MaterialPropertyBlock();
            _baseColor = Shader.PropertyToID("_BaseColor");
            _color = Shader.PropertyToID("_Color");
        }

        public static CampVehicleModel Create(Transform parent, Material[] palette)
        {
            var root = new GameObject("Camp transport truck model");
            var model = root.AddComponent<CampVehicleModel>();
            // Initialize once while active so OnDestroy also runs if an inactive parent
            // is destroyed before its first enable; owned meshes still get released.
            root.transform.SetParent(parent, false);
            try { model.Build(palette); }
            catch { ReleaseOwned(root); throw; }
            return model;
        }

        // speed is signed metres/second; steering is normalized [-1,1].
        // Only internal visual transforms change. Safe before the first Update.
        public void Pose(bool repaired, float speed, float steering)
        {
            _speed = Finite(speed) ? Mathf.Clamp(speed, -30f, 30f) : 0f;
            _steering = Finite(steering) ? Mathf.Clamp(steering, -1f, 1f) : 0f;
            if (!_built) { _repaired = repaired; return; }
            if (_repaired != repaired)
            {
                _repaired = repaired;
                ApplyCondition();
            }
            ApplyWheelPose();
        }

        private void Update()
        {
            if (!_built) return;
            if (_repaired)
                _roll = Mathf.Repeat(_roll + _speed * Mathf.Min(Time.unscaledDeltaTime, .1f) /
                    WheelRadius * Mathf.Rad2Deg, 360f);
            ApplyWheelPose();
            if (!_repaired)
                _damage.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(Time.unscaledTime * 1.7f) * 3f);
        }

        private void Build(Material[] palette)
        {
            if (_built) return;
            Awake(); // Awake is deferred if Create is called under an inactive parent.
            _palette = palette;
            _box = BoxMesh();
            _cylinder = Lathe("Truck cylinder", new[] { -.5f, -.5f, .5f, .5f }, new[] { 0f, .5f, .5f, 0f });
            _tire = Lathe("Bevelled truck tire", new[] { -.5f, -.5f, -.34f, .34f, .5f, .5f }, new[] { 0f, .40f, .5f, .5f, .40f, 0f });
            _arch = Lathe("Wheel arch", new[] { -.5f, -.5f, .5f, .5f, -.5f }, new[] { .465f, .5f, .5f, .465f, .465f }, 18, 180f);
            _ring = Torus();
            _ball = BallMesh();
            BuildChassis();
            BuildCabin();
            BuildFront();
            BuildCargo();
            BuildDamage();
            BuildWear();
            _built = true;
            ApplyCondition();
            ApplyWheelPose();
        }

        private void BuildChassis()
        {
            Box(transform, "Dark ladder chassis", new Vector3(0f, .49f, -.04f), new Vector3(1.36f, .19f, 3.83f), 6, Steel);
            // Top at .65 matches the four seat foot reference heights supplied by the caller.
            Box(transform, "Open cabin floor", new Vector3(0f, .59f, .15f), new Vector3(1.76f, .12f, 2.1f), 6, DarkPaint);
            for (int side = 0; side < 2; side++)
            {
                float x = side == 0 ? -.78f : .78f;
                BuildWheel(side, new Vector3(x, WheelRadius, 1.32f), true, side == 0 ? -1f : 1f);
                BuildWheel(side + 2, new Vector3(x, WheelRadius, -1.40f), false, side == 0 ? -1f : 1f);
                Box(transform, "Cab sill", new Vector3(Mathf.Sign(x) * .90f, .70f, .04f), new Vector3(.14f, .18f, 1.97f), 5, Paint);
                Box(transform, "Metal boarding step", new Vector3(Mathf.Sign(x) * .96f, .40f, .05f), new Vector3(.22f, .10f, .65f), 2, Steel);
                for (int rib = 0; rib < 4; rib++)
                    Box(transform, "Step grip", new Vector3(Mathf.Sign(x) * .96f, .455f, -.18f + rib * .15f), new Vector3(.20f, .012f, .035f), 2, Rim);
            }
            Cylinder(transform, "Front axle", new Vector3(0f, .40f, 1.32f), new Vector3(.13f, 1.53f, .13f), 2, Steel, Quaternion.Euler(0f, 0f, 90f));
            Cylinder(transform, "Rear axle", new Vector3(0f, .40f, -1.40f), new Vector3(.16f, 1.53f, .16f), 2, Steel, Quaternion.Euler(0f, 0f, 90f));
            Box(transform, "Rear bumper", new Vector3(0f, .49f, -2.12f), new Vector3(1.94f, .18f, .16f), 2, Steel);
            Box(transform, "Rear number plate", new Vector3(0f, .64f, -2.135f), new Vector3(.45f, .17f, .04f), 7, Rim);
            for (int i = 0; i < 3; i++)
                Box(transform, "Stamped plate marks", new Vector3(-.12f + i * .12f, .64f, -2.159f), new Vector3(.045f, .085f, .007f), 6, Rubber);
        }

        private void BuildWheel(int index, Vector3 position, bool front, float side)
        {
            Transform steer = Joint(transform, front ? "Front steering knuckle" : "Rear wheel pivot", position);
            if (front) _frontSteer[index] = steer;
            Transform roll = Joint(steer, "Rolling tire and hub", Vector3.zero);
            _wheelRoll[index] = roll;
            Part(roll, "Chunky rubber tire", _tire, Vector3.zero, new Vector3(.80f, .28f, .80f), 6, Rubber, Quaternion.Euler(0f, 0f, 90f));
            Cylinder(roll, "Inset wheel rim", new Vector3(side * .149f, 0f, 0f), new Vector3(.45f, .04f, .45f), 2, Rim, Quaternion.Euler(0f, 0f, 90f));
            Cylinder(roll, "Greased axle cap", new Vector3(side * .175f, 0f, 0f), new Vector3(.18f, .035f, .18f), 2, Steel, Quaternion.Euler(0f, 0f, 90f));
            for (int lug = 0; lug < 5; lug++)
            {
                float angle = lug * Mathf.PI * .4f;
                Ball(roll, "Wheel nut", new Vector3(side * .178f, Mathf.Cos(angle) * .15f, Mathf.Sin(angle) * .15f), Vector3.one * .043f, 2, Rust);
            }
            // Tread remains inside the .4 m rolling radius; no decorative wheel colliders exist.
            for (int tread = 0; tread < 12; tread++)
            {
                float angle = tread * 30f;
                float radians = angle * Mathf.Deg2Rad;
                Box(roll, "Tire tread", new Vector3(0f, Mathf.Cos(radians) * .387f, Mathf.Sin(radians) * .387f),
                    new Vector3(.235f, .018f, .083f), 6, new Color(.15f, .16f, .15f), Quaternion.Euler(angle, 0f, 0f));
            }
            Part(transform, "Painted curved fender", _arch, position, new Vector3(.97f, .50f, .97f), 5, Paint, Quaternion.Euler(0f, 0f, 90f));
        }

        private void BuildCabin()
        {
            BuildSeat(new Vector3(-.55f, .65f, .65f), "Driver");
            BuildSeat(new Vector3(.55f, .65f, .65f), "Front passenger");
            BuildSeat(new Vector3(-.55f, .65f, -.35f), "Rear left passenger");
            BuildSeat(new Vector3(.55f, .65f, -.35f), "Rear right passenger");
            Box(transform, "Dashboard", new Vector3(0f, 1.20f, 1.12f), new Vector3(1.70f, .20f, .19f), 5, DarkPaint);
            Box(transform, "Dashboard worn top", new Vector3(0f, 1.32f, 1.10f), new Vector3(1.76f, .07f, .25f), 5, Edge);
            Cylinder(transform, "Speedometer rim", new Vector3(-.45f, 1.21f, 1.009f), new Vector3(.17f, .025f, .17f), 2, Rim, Quaternion.Euler(90f, 0f, 0f));
            Cylinder(transform, "Speedometer dark face", new Vector3(-.45f, 1.21f, .992f), new Vector3(.135f, .012f, .135f), 6, Rubber, Quaternion.Euler(90f, 0f, 0f));
            Box(transform, "Gauge needle", new Vector3(-.465f, 1.226f, .982f), new Vector3(.010f, .06f, .008f), 7, WarmLamp, Quaternion.Euler(0f, 0f, -32f));
            Beam(transform, "Steering column", new Vector3(-.55f, .86f, 1.09f), new Vector3(-.55f, 1.36f, .87f), .065f, 2, Steel);
            Transform steeringTilt = Joint(transform, "Tilted steering mount", new Vector3(-.55f, 1.36f, .87f));
            steeringTilt.localRotation = Quaternion.Euler(54f, 0f, 0f);
            _steeringWheel = Joint(steeringTilt, "Animated steering wheel", Vector3.zero);
            Part(_steeringWheel, "Steering rubber rim", _ring, Vector3.zero, Vector3.one * .44f, 6, Rubber);
            Box(_steeringWheel, "Steering horizontal spoke", Vector3.zero, new Vector3(.34f, .035f, .036f), 2, Rim);
            Box(_steeringWheel, "Steering lower spoke", new Vector3(0f, -.09f, 0f), new Vector3(.036f, .18f, .036f), 2, Rim);
            Ball(_steeringWheel, "Steering hub", Vector3.zero, new Vector3(.09f, .09f, .06f), 2, Steel);
            Beam(transform, "Gear stick", new Vector3(.02f, .65f, .80f), new Vector3(.02f, 1.02f, .70f), .034f, 2, Steel);
            Ball(transform, "Gear knob", new Vector3(.02f, 1.02f, .70f), Vector3.one * .085f, 6, Seat);

            // An open four-seat cab: a slim frame, no opaque roof/glass over the occupants.
            for (int side = -1; side <= 1; side += 2)
            {
                Beam(transform, "Windshield side frame", new Vector3(side * .85f, 1.28f, 1.20f), new Vector3(side * .85f, 2.27f, 1.02f), .073f, 5, Edge);
                Beam(transform, "Rear roll hoop post", new Vector3(side * .85f, .70f, -.92f), new Vector3(side * .85f, 2.28f, -.92f), .075f, 5, Paint);
                Beam(transform, "Open roof edge", new Vector3(side * .85f, 2.28f, -.92f), new Vector3(side * .85f, 2.27f, 1.02f), .065f, 5, DarkPaint);
                Beam(transform, "Mirror arm", new Vector3(side * .85f, 1.72f, 1.14f), new Vector3(side * 1.035f, 1.73f, 1.14f), .035f, 2, Steel);
                Box(transform, "Mirror housing", new Vector3(side * 1.035f, 1.74f, 1.12f), new Vector3(.13f, .22f, .08f), 6, Rubber);
                Box(transform, "Mirror metal face", new Vector3(side * 1.035f, 1.74f, 1.074f), new Vector3(.104f, .18f, .012f), 2, new Color(.57f, .66f, .66f));
                Box(transform, "Low open cab side", new Vector3(side * .865f, .87f, .15f), new Vector3(.08f, .25f, 1.76f), 5, Paint);
            }
            Beam(transform, "Windshield upper frame", new Vector3(-.85f, 2.27f, 1.02f), new Vector3(.85f, 2.27f, 1.02f), .075f, 5, Edge);
            Beam(transform, "Rear hoop crossbar", new Vector3(-.85f, 2.28f, -.92f), new Vector3(.85f, 2.28f, -.92f), .08f, 5, Paint);
            Beam(transform, "Windshield central divider", new Vector3(0f, 1.30f, 1.20f), new Vector3(0f, 2.27f, 1.02f), .035f, 2, Steel);
            Beam(transform, "Driver wiper", new Vector3(-.18f, 1.35f, 1.225f), new Vector3(-.59f, 1.53f, 1.20f), .018f, 6, Rubber);
        }

        private void BuildSeat(Vector3 feet, string label)
        {
            // These are model parts, not gameplay seat anchors or occupancy state.
            Box(transform, label + " seat pedestal", feet + new Vector3(0f, .08f, -.22f), new Vector3(.46f, .16f, .44f), 2, Steel);
            Box(transform, label + " cushion", feet + new Vector3(0f, .23f, -.17f), new Vector3(.68f, .18f, .55f), 6, Seat);
            Box(transform, label + " cushion seam", feet + new Vector3(0f, .325f, -.17f), new Vector3(.58f, .015f, .44f), 4, new Color(.36f, .29f, .20f));
            Box(transform, label + " short backrest", feet + new Vector3(0f, .52f, -.47f), new Vector3(.69f, .51f, .12f), 6, Seat, Quaternion.Euler(-8f, 0f, 0f));
        }

        private void BuildFront()
        {
            Box(transform, "Front engine bay", new Vector3(0f, .85f, 1.59f), new Vector3(1.62f, .46f, .83f), 5, Paint);
            Box(transform, "Radiator inset", new Vector3(0f, .82f, 2.017f), new Vector3(.98f, .38f, .035f), 6, Rubber);
            for (int bar = 0; bar < 7; bar++)
                Box(transform, "Rounded grille bars", new Vector3(-.39f + bar * .13f, .81f, 2.045f), new Vector3(.065f, .34f, .035f), 2, Rim);
            _hood = Joint(transform, "Hinged engine bonnet", new Vector3(0f, 1.11f, 1.17f));
            Part(_hood, "Sloped rounded bonnet", HoodMesh(), new Vector3(0f, 0f, .44f), new Vector3(1.72f, .18f, .88f), 5, Edge);
            Box(_hood, "Bonnet centre strip", new Vector3(0f, .085f, .43f), new Vector3(.07f, .024f, .76f), 2, Rim);
            for (int side = -1; side <= 1; side += 2)
            {
                int light = side < 0 ? 0 : 1;
                Cylinder(transform, "Headlamp dark bucket", new Vector3(side * .65f, 1.00f, 1.99f), new Vector3(.36f, .18f, .36f), 6, Rubber, Quaternion.Euler(90f, 0f, 0f));
                Cylinder(transform, "Headlamp metal bezel", new Vector3(side * .65f, 1.00f, 2.085f), new Vector3(.31f, .035f, .31f), 2, Rim, Quaternion.Euler(90f, 0f, 0f));
                _headlights[light] = Cylinder(transform, "Headlamp lens", new Vector3(side * .65f, 1.00f, 2.108f), new Vector3(.254f, .014f, .254f), 7, WarmLamp, Quaternion.Euler(90f, 0f, 0f));
                Box(transform, "Amber corner lamp", new Vector3(side * .89f, .88f, 1.94f), new Vector3(.15f, .12f, .10f), 7, new Color(.94f, .51f, .10f));
                for (int vent = 0; vent < 3; vent++)
                    Box(transform, "Engine cooling slot", new Vector3(side * .817f, .94f, 1.38f + vent * .16f), new Vector3(.012f, .17f, .045f), 6, DarkPaint);
            }
            _bumper = Joint(transform, "Bent front bumper mount", new Vector3(0f, .49f, 2.10f));
            Box(_bumper, "Front bumper", Vector3.zero, new Vector3(1.98f, .20f, .19f), 2, Steel);
            Box(_bumper, "Front bumper wear", new Vector3(0f, .018f, .092f), new Vector3(.57f, .09f, .009f), 2, Rust);
            // Exhaust is beside the cargo, below the top of the cabin frame.
            Beam(transform, "Exhaust upright", new Vector3(.82f, .62f, -.91f), new Vector3(.82f, 2.04f, -.91f), .10f, 2, Steel);
            Beam(transform, "Bent exhaust outlet", new Vector3(.82f, 2.04f, -.91f), new Vector3(.82f, 2.13f, -1.05f), .11f, 2, Steel);
        }

        private void BuildCargo()
        {
            // Clear platform top is y=1.05, centered at z=-1.55; no fake cargo fills it.
            Box(transform, "Cargo platform frame", new Vector3(0f, .96f, -1.55f), new Vector3(1.78f, .12f, 1.08f), 2, Steel);
            for (int plank = 0; plank < 7; plank++)
                Box(transform, "Cargo floor plank", new Vector3(-.72f + plank * .24f, 1.025f, -1.55f), new Vector3(.225f, .05f, 1.03f), 4, plank % 2 == 0 ? Wood : WoodEdge);
            for (int side = -1; side <= 1; side += 2)
            {
                for (int end = 0; end < 2; end++)
                {
                    float z = end == 0 ? -1.025f : -2.075f;
                    Box(transform, "Cargo corner stake", new Vector3(side * .86f, 1.28f, z), new Vector3(.095f, .58f, .095f), 4, WoodEdge);
                    Box(transform, "Cargo metal strap", new Vector3(side * .912f, 1.12f, z), new Vector3(.022f, .30f, .115f), 2, Steel);
                    Ball(transform, "Rail carriage bolt", new Vector3(side * .93f, 1.21f, z), Vector3.one * .055f, 2, Rim);
                }
                for (int rail = 0; rail < 2; rail++)
                    Box(transform, "Side wooden cargo rail", new Vector3(side * .868f, 1.18f + rail * .25f, -1.55f), new Vector3(.08f, .17f, 1.10f), 4, rail == 0 ? Wood : WoodEdge);
                Box(transform, "Rear red lamp", new Vector3(side * .68f, .78f, -2.114f), new Vector3(.21f, .15f, .06f), 7, new Color(.68f, .12f, .08f));
            }
            for (int rail = 0; rail < 2; rail++)
            {
                Box(transform, "Tailgate wooden rail", new Vector3(0f, 1.18f + rail * .25f, -2.075f), new Vector3(1.67f, .17f, .08f), 4, WoodEdge);
                Box(transform, "Cargo front stop rail", new Vector3(0f, 1.18f + rail * .25f, -1.025f), new Vector3(1.67f, .17f, .08f), 4, Wood);
            }
        }

        private void BuildDamage()
        {
            _damage = Joint(transform, "Unrepaired warning tag", new Vector3(0f, 1.44f, 1.12f));
            Beam(_damage, "Tag hanging wire", new Vector3(0f, .15f, 0f), Vector3.zero, .018f, 2, Steel);
            // A red metal diamond and exclamation mark work without a font or localization.
            Box(_damage, "Repair warning diamond", new Vector3(0f, -.12f, 0f), new Vector3(.23f, .23f, .025f), 7, new Color(.78f, .16f, .08f), Quaternion.Euler(0f, 0f, 45f));
            Box(_damage, "Warning exclamation stroke", new Vector3(0f, -.10f, .018f), new Vector3(.034f, .13f, .015f), 7, WarmLamp);
            Ball(_damage, "Warning exclamation dot", new Vector3(0f, -.19f, .018f), new Vector3(.037f, .037f, .016f), 7, WarmLamp);
            Beam(_damage, "Loose engine cable", new Vector3(.19f, -.15f, -.02f), new Vector3(.25f, -.40f, .22f), .034f, 6, Rubber);
        }

        private void BuildWear()
        {
            for (int side = -1; side <= 1; side += 2)
            {
                Box(transform, "Cab paint repair patch", new Vector3(side * .910f, .84f, -.42f), new Vector3(.017f, .12f, .23f), 2, Rust, Quaternion.Euler(side * 8f, 0f, 0f));
                Box(transform, "Sill scraped paint", new Vector3(side * .978f, .70f, .51f), new Vector3(.012f, .065f, .28f), 2, Rim);
                Box(transform, "Bonnet rust edge", new Vector3(side * .816f, .79f, 1.68f), new Vector3(.015f, .06f, .23f), 4, Rust);
                for (int bolt = 0; bolt < 3; bolt++)
                    Ball(transform, "Cab rivet", new Vector3(side * .913f, .93f, -.53f + bolt * .54f), Vector3.one * .045f, 2, Rim);
            }
        }

        private void ApplyCondition()
        {
            _hood.localRotation = Quaternion.Euler(_repaired ? 0f : -22f, 0f, 0f);
            _bumper.localRotation = Quaternion.Euler(0f, 0f, _repaired ? 0f : -4f);
            _damage.gameObject.SetActive(!_repaired);
            for (int i = 0; i < _headlights.Length; i++)
                Tint(_headlights[i], _repaired ? WarmLamp : new Color(.37f, .33f, .20f));
        }

        private void ApplyWheelPose()
        {
            foreach (Transform wheel in _wheelRoll) wheel.localRotation = Quaternion.Euler(_roll, 0f, 0f);
            foreach (Transform steer in _frontSteer) steer.localRotation = Quaternion.Euler(0f, _steering * 28f, 0f);
            _steeringWheel.localRotation = Quaternion.Euler(0f, 0f, -_steering * 100f);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static Transform Joint(Transform parent, string name, Vector3 position)
        {
            Transform part = new GameObject(name).transform;
            part.SetParent(parent, false);
            part.localPosition = position;
            return part;
        }

        private Renderer Box(Transform parent, string name, Vector3 position, Vector3 scale, int palette, Color tint, Quaternion rotation = default) =>
            Part(parent, name, _box, position, scale, palette, tint, rotation);
        private Renderer Cylinder(Transform parent, string name, Vector3 position, Vector3 scale, int palette, Color tint, Quaternion rotation = default) =>
            Part(parent, name, _cylinder, position, scale, palette, tint, rotation);
        private Renderer Ball(Transform parent, string name, Vector3 position, Vector3 scale, int palette, Color tint) =>
            Part(parent, name, _ball, position, scale, palette, tint);

        private void Beam(Transform parent, string name, Vector3 start, Vector3 end, float width, int palette, Color tint)
        {
            Vector3 direction = end - start;
            Cylinder(parent, name, (start + end) * .5f, new Vector3(width, direction.magnitude, width), palette, tint,
                Quaternion.FromToRotation(Vector3.up, direction.normalized));
        }

        private Renderer Part(Transform parent, string name, Mesh mesh, Vector3 position, Vector3 scale, int palette, Color tint, Quaternion rotation = default)
        {
            Transform part = Joint(parent, name, position);
            part.localScale = scale;
            part.localRotation = rotation == default ? Quaternion.identity : rotation;
            part.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Surface(palette);
            Tint(renderer, tint);
            return renderer;
        }

        private Material Surface(int index)
        {
            if (_palette != null && index >= 0 && index < _palette.Length && _palette[index] != null) return _palette[index];
            if (_fallback != null) return _fallback;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("Truck model needs a lit palette material or shader.");
            _fallback = new Material(shader) { name = "Runtime truck fallback" };
            _ownedMaterials.Add(_fallback);
            return _fallback;
        }

        private void Tint(Renderer renderer, Color color)
        {
            _properties.Clear();
            _properties.SetColor(_baseColor, color);
            _properties.SetColor(_color, color);
            renderer.SetPropertyBlock(_properties);
        }

        private Mesh BoxMesh()
        {
            return SolidMesh("Truck unit box", new[] {
                new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                new Vector3(-.5f,-.5f,.5f), new Vector3(.5f,-.5f,.5f), new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f) });
        }

        private Mesh HoodMesh()
        {
            return SolidMesh("Sloped truck bonnet", new[] {
                new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                new Vector3(-.47f,-.5f,.5f), new Vector3(.47f,-.5f,.5f), new Vector3(.47f,.10f,.5f), new Vector3(-.47f,.10f,.5f) });
        }

        private Mesh SolidMesh(string name, Vector3[] corners)
        {
            int[] indices = { 0,2,1,0,3,2,4,5,6,4,6,7,0,4,7,0,7,3,1,2,6,1,6,5,3,7,6,3,6,2,0,1,5,0,5,4 };
            var vertices = new Vector3[indices.Length];
            var triangles = new int[indices.Length];
            for (int i = 0; i < indices.Length; i++) { vertices[i] = corners[indices[i]]; triangles[i] = i; }
            return MeshFrom(name, vertices, triangles);
        }

        private Mesh Lathe(string name, float[] height, float[] radius, int segments = 20, float angleDegrees = 360f)
        {
            var vertices = new Vector3[height.Length * (segments + 1)];
            var triangles = new int[(height.Length - 1) * segments * 6];
            for (int row = 0; row < height.Length; row++)
                for (int col = 0; col <= segments; col++)
                {
                    float angle = col / (float)segments * angleDegrees * Mathf.Deg2Rad;
                    vertices[row * (segments + 1) + col] = new Vector3(Mathf.Sin(angle) * radius[row], height[row], Mathf.Cos(angle) * radius[row]);
                }
            int cursor = 0;
            for (int row = 0; row < height.Length - 1; row++)
                for (int col = 0; col < segments; col++)
                {
                    int a = row * (segments + 1) + col, b = a + 1, c = a + segments + 1, d = c + 1;
                    triangles[cursor++] = a; triangles[cursor++] = b; triangles[cursor++] = c;
                    triangles[cursor++] = b; triangles[cursor++] = d; triangles[cursor++] = c;
                }
            return MeshFrom(name, vertices, triangles);
        }

        private Mesh BallMesh()
        {
            const int rings = 8;
            var height = new float[rings + 1];
            var radius = new float[rings + 1];
            for (int i = 0; i <= rings; i++)
            {
                float angle = i * Mathf.PI / rings;
                height[i] = -.5f * Mathf.Cos(angle);
                radius[i] = .5f * Mathf.Sin(angle);
            }
            return Lathe("Truck rounded fastener", height, radius, 12);
        }

        private Mesh Torus()
        {
            const int around = 24, sides = 6;
            var vertices = new Vector3[(around + 1) * (sides + 1)];
            var triangles = new int[around * sides * 6];
            for (int a = 0; a <= around; a++)
                for (int b = 0; b <= sides; b++)
                {
                    float theta = a * Mathf.PI * 2f / around, phi = b * Mathf.PI * 2f / sides;
                    float r = .43f + Mathf.Cos(phi) * .07f;
                    vertices[a * (sides + 1) + b] = new Vector3(Mathf.Cos(theta) * r, Mathf.Sin(theta) * r, Mathf.Sin(phi) * .07f);
                }
            int cursor = 0;
            for (int a = 0; a < around; a++)
                for (int b = 0; b < sides; b++)
                {
                    int p = a * (sides + 1) + b, q = p + sides + 1;
                    triangles[cursor++] = p; triangles[cursor++] = q; triangles[cursor++] = p + 1;
                    triangles[cursor++] = p + 1; triangles[cursor++] = q; triangles[cursor++] = q + 1;
                }
            return MeshFrom("Steering wheel ring", vertices, triangles);
        }

        private Mesh MeshFrom(string name, Vector3[] vertices, int[] triangles)
        {
            var mesh = new Mesh { name = name, vertices = vertices, triangles = triangles };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _ownedMeshes.Add(mesh);
            return mesh;
        }

        private void OnDestroy()
        {
            // Never dispose shared palette materials or any caller-owned objects.
            foreach (Mesh mesh in _ownedMeshes) ReleaseOwned(mesh);
            foreach (Material material in _ownedMaterials) ReleaseOwned(material);
            _ownedMeshes.Clear();
            _ownedMaterials.Clear();
        }

        private static void ReleaseOwned(UnityEngine.Object owned)
        {
            if (owned == null) return;
            if (Application.isPlaying) Destroy(owned);
            else DestroyImmediate(owned);
        }
    }
}
