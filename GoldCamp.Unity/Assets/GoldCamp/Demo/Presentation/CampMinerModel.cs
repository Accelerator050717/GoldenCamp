using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoldCamp.Demo
{
    // Visuals only. CampWorld owns placement, interaction targets and all physical colliders.
    public sealed class CampMinerModel : MonoBehaviour
    {
        public Transform CarryAnchor { get; private set; }

        private sealed class Arm
        {
            public Transform Shoulder, Elbow, Hand;
            public Vector3 Origin;
            public float Side;
        }

        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<Material> _materials = new List<Material>();
        private readonly List<Renderer> _patches = new List<Renderer>();
        private Material[] _palette;
        private Material _fallback, _smokeMaterial;
        private Mesh _ball, _cylinder, _box;
        private MaterialPropertyBlock _properties;
        private int _baseColor, _color;
        private Transform _body, _leftLeg, _rightLeg, _leftKnee, _rightKnee;
        private Transform _leftCheek, _rightCheek, _mouth, _pickaxe, _detector, _bottle, _pipe;
        private Transform _leftLid, _rightLid, _labelRoot, _stars;
        private readonly Transform[] _puffs = new Transform[6];
        private readonly Renderer[] _puffRenderers = new Renderer[6];
        private Arm _leftArm, _rightArm;
        private TextMesh _name;
        private Font _nameFont;
        private string _minerId = "", _displayName = "";
        private int _colorIndex = -1;
        private bool _ready, _havePosition, _customFont;
        private Vector3 _lastPosition;
        private float _motion, _stepPhase, _phaseOffset;

        private static readonly Color Skin = new Color(.65f, .76f, .37f);
        private static readonly Color Belly = new Color(.82f, .85f, .48f);
        private static readonly Color Cloth = new Color(.20f, .27f, .29f);
        private static readonly Color Seam = new Color(.33f, .39f, .39f);
        private static readonly Color Leather = new Color(.48f, .28f, .12f);
        private static readonly Color Boot = new Color(.22f, .17f, .12f);
        private static readonly Color Gold = new Color(1f, .66f, .10f);
        private static readonly Color Metal = new Color(.35f, .38f, .38f);
        // With hips at -90 degrees and knees at +90, this keeps the boot soles at
        // the caller's seat foot origin while putting the hips behind the knees.
        private static readonly Vector3 SeatedBodyOffset = new Vector3(0f, -.206f, -.21f);

        private void Awake()
        {
            _properties = new MaterialPropertyBlock();
            _baseColor = Shader.PropertyToID("_BaseColor");
            _color = Shader.PropertyToID("_Color");
        }

        public static CampMinerModel Create(Transform parent, Material[] palette)
        {
            var root = new GameObject("Bean miner model");
            root.transform.SetParent(parent, false);
            var model = root.AddComponent<CampMinerModel>();
            model.Build(palette);
            return model;
        }

        public void SetNameFont(Font font)
        {
            if (font == null) return;
            _nameFont = font;
            _customFont = true;
            if (_name == null) return;
            _name.font = font;
            _name.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            RefreshName();
        }

        private void Build(Material[] palette)
        {
            if (_ready) return;
            _palette = palette;
            // AddComponent on an inactive parent defers Awake; initialize through the same path.
            if (_properties == null) Awake();
            _ball = Sphere();
            _cylinder = Lathe("Unit cylinder", new[] { -.5f, -.5f, .5f, .5f },
                new[] { 0f, .5f, .5f, 0f }, new[] { 0f, .5f, .5f, 0f });
            _box = BoxMesh();
            _body = Joint(transform, "Animated body", Vector3.zero);
            BuildBody();
            BuildFace();
            BuildHelmet();
            _leftArm = BuildArm(-1f);
            _rightArm = BuildArm(1f);
            _leftLeg = BuildLeg(-1f, out _leftKnee);
            _rightLeg = BuildLeg(1f, out _rightKnee);
            BuildTools();
            BuildSocialProps();
            BuildName();
            CarryAnchor = Joint(_body, "Carry visual anchor", new Vector3(0f, 1f, .66f));
            _ready = true;
        }

        private void BuildBody()
        {
            Mesh bean = Lathe("Continuous neckless bean", new[] { .52f, .59f, .76f, .93f, 1.15f, 1.36f, 1.51f, 1.60f, 1.63f },
                new[] { 0f, .27f, .37f, .39f, .34f, .28f, .235f, .14f, 0f },
                new[] { 0f, .20f, .25f, .27f, .25f, .22f, .185f, .12f, 0f }, 28);
            Part(_body, "Green bean head and torso", bean, Vector3.zero, Vector3.one, 5, Skin);
            Ball(_body, "Pale belly", new Vector3(0f, .98f, .245f), new Vector3(.34f, .37f, .08f), 5, Belly);

            Mesh jacket = Lathe("Open short jacket", new[] { .78f, .81f, .97f, 1.16f, 1.23f },
                new[] { .37f, .39f, .405f, .355f, .29f }, new[] { .269f, .287f, .287f, .27f, .236f }, 26, 25f, 335f);
            Part(_body, "Curved jacket sides and back", jacket, Vector3.zero, Vector3.one, 6, Cloth);
            Mesh hem = Lathe("Jacket hem", new[] { .785f, .817f }, new[] { .379f, .397f }, new[] { .28f, .294f }, 26, 25f, 335f);
            Part(_body, "Stitched jacket hem", hem, Vector3.zero, Vector3.one, 6, Seam);
            foreach (float side in new[] { -1f, 1f })
            {
                Ball(_body, "Folded jacket collar", new Vector3(side * .235f, 1.18f, .225f),
                    new Vector3(.145f, .205f, .052f), 6, Seam, Quaternion.Euler(0f, side * 16f, side * -22f));
                Box(_body, "Breast pocket", new Vector3(side * .255f, .995f, .247f), new Vector3(.13f, .14f, .035f), 6, Seam);
                Box(_body, "Pocket flap", new Vector3(side * .255f, 1.05f, .27f), new Vector3(.145f, .038f, .025f), 6, Cloth);
                Ball(_body, "Pocket rivet", new Vector3(side * .255f, 1.05f, .287f), Vector3.one * .024f, 2, new Color(.67f, .59f, .40f));
                for (int i = 0; i < 3; i++)
                    Ball(_body, "Jacket button", new Vector3(side * .163f, .865f + i * .11f, .268f), new Vector3(.031f, .031f, .018f), 2, Metal);
            }
            Renderer patch = Box(_body, "Four-color miner patch", new Vector3(-.258f, 1.005f, .273f), new Vector3(.089f, .075f, .012f), 5, Gold);
            _patches.Add(patch);
            Box(_body, "Patch stitch", new Vector3(-.258f, 1.005f, .282f), new Vector3(.014f, .047f, .007f), 7, new Color(.96f, .92f, .70f));

            Ball(_body, "Trouser seat", new Vector3(0f, .575f, -.005f), new Vector3(.66f, .255f, .45f), 4, Leather * .76f);
            Mesh belt = Lathe("Waist belt", new[] { .645f, .706f }, new[] { .34f, .347f }, new[] { .246f, .251f });
            Part(_body, "Leather belt", belt, Vector3.zero, Vector3.one, 4, Leather * .68f);
            Box(_body, "Belt buckle", new Vector3(0f, .678f, .26f), new Vector3(.135f, .077f, .025f), 2, new Color(.69f, .65f, .50f));
            Box(_body, "Buckle inset", new Vector3(0f, .678f, .278f), new Vector3(.079f, .035f, .012f), 4, Boot);
            foreach (float side in new[] { -1f, 1f })
                Box(_body, "Belt loop", new Vector3(side * .23f, .677f, .205f), new Vector3(.035f, .081f, .039f), 4, Leather);
        }

        private void BuildFace()
        {
            foreach (float side in new[] { -1f, 1f })
            {
                Vector3 eye = new Vector3(side * .108f, 1.411f, .208f);
                Ball(_body, "Round green eye rim", eye, new Vector3(.113f, .124f, .054f), 5, new Color(.81f, .90f, .66f));
                Ball(_body, "Round dark pupil", eye + new Vector3(0f, .003f, .029f), new Vector3(.067f, .077f, .026f), 6, new Color(.035f, .065f, .055f));
                Ball(_body, "Eye highlight", eye + new Vector3(-.015f, .023f, .043f), Vector3.one * .024f, 7, Color.white);
                Transform lid = Ball(_body, "Sleepy or blinking eyelid", eye + new Vector3(0f, .027f, .045f),
                    new Vector3(.118f, .004f, .022f), 5, Skin).transform;
                if (side < 0) _leftLid = lid; else _rightLid = lid;
            }
            _mouth = Ball(_body, "Small horizontal mouth", new Vector3(0f, 1.292f, .246f), new Vector3(.10f, .012f, .013f), 6, new Color(.12f, .15f, .07f)).transform;
            _leftCheek = Ball(_body, "Left full cheek", new Vector3(-.139f, 1.303f, .225f), new Vector3(.12f, .12f, .055f), 5, Skin).transform;
            _rightCheek = Ball(_body, "Right full cheek", new Vector3(.139f, 1.303f, .225f), new Vector3(.12f, .12f, .055f), 5, Skin).transform;
        }

        private void BuildHelmet()
        {
            Mesh dome = Lathe("Hardhat dome", new[] { 0f, .055f, .13f, .205f, .24f },
                new[] { .305f, .30f, .26f, .17f, 0f }, new[] { .264f, .262f, .23f, .15f, 0f }, 28);
            Part(_body, "Yellow hardhat shell", dome, new Vector3(0f, 1.54f, -.01f), Vector3.one, 7, Gold);
            Ball(_body, "Wide helmet brim", new Vector3(0f, 1.545f, .025f), new Vector3(.737f, .066f, .658f), 7, Gold);
            Mesh band = Lathe("Hardhat dark strap", new[] { 1.564f, 1.614f }, new[] { .311f, .3f }, new[] { .275f, .265f }, 28);
            Part(_body, "Dark hardhat band", band, new Vector3(0f, 0f, -.009f), Vector3.one, 6, Boot);
            Ball(_body, "Raised helmet ridge", new Vector3(0f, 1.706f, -.02f), new Vector3(.07f, .168f, .365f), 7, new Color(1f, .73f, .17f));
            Quaternion face = Quaternion.Euler(90f, 0f, 0f);
            Cylinder(_body, "Lamp black housing", new Vector3(0f, 1.67f, .277f), new Vector3(.203f, .084f, .203f), 6, Boot, face);
            Cylinder(_body, "Lamp metal rim", new Vector3(0f, 1.67f, .324f), new Vector3(.171f, .027f, .171f), 2, new Color(.59f, .53f, .35f), face);
            Cylinder(_body, "Warm headlamp lens", new Vector3(0f, 1.67f, .342f), new Vector3(.137f, .018f, .137f), 7, new Color(1f, .94f, .59f), face);
            Ball(_body, "Headlamp bright center", new Vector3(0f, 1.67f, .355f), new Vector3(.072f, .072f, .016f), 7, new Color(1f, 1f, .88f));
            foreach (float side in new[] { -1f, 1f })
            {
                Box(_body, "Band buckle", new Vector3(side * .17f, 1.585f, .228f), new Vector3(.046f, .071f, .029f), 7, Gold);
                Renderer badge = Ball(_body, "Helmet team patch", new Vector3(side * .284f, 1.642f, .037f), new Vector3(.023f, .071f, .092f), 5, Gold);
                _patches.Add(badge);
            }
        }

        private Arm BuildArm(float side)
        {
            var arm = new Arm { Side = side, Origin = new Vector3(side * .355f, 1.185f, -.006f) };
            arm.Shoulder = Joint(_body, side < 0 ? "Left shoulder" : "Right shoulder", arm.Origin);
            Ball(arm.Shoulder, "Short jacket sleeve", new Vector3(0f, -.10f, 0f), new Vector3(.24f, .282f, .248f), 6, Cloth);
            Ball(arm.Shoulder, "Rolled sleeve cuff", new Vector3(0f, -.205f, 0f), new Vector3(.244f, .073f, .254f), 4, new Color(.53f, .49f, .39f));
            arm.Elbow = Joint(arm.Shoulder, "Elbow", Vector3.down * .25f);
            Ball(arm.Elbow, "Green forearm", new Vector3(0f, -.074f, 0f), new Vector3(.15f, .19f, .16f), 5, Skin);
            arm.Hand = Joint(arm.Elbow, "Gloved hand", Vector3.down * .25f);
            Ball(arm.Hand, "Leather glove cuff", new Vector3(0f, .068f, 0f), new Vector3(.192f, .08f, .19f), 4, Leather * .85f);
            Ball(arm.Hand, "Rounded mitten palm", new Vector3(0f, -.018f, .012f), new Vector3(.18f, .195f, .157f), 4, Leather);
            Ball(arm.Hand, "Glove thumb", new Vector3(-side * .078f, -.012f, .04f), new Vector3(.073f, .118f, .092f), 4, Leather,
                Quaternion.Euler(0f, 0f, side * 25f));
            Box(arm.Hand, "Glove stitched patch", new Vector3(0f, .006f, -.072f), new Vector3(.104f, .087f, .017f), 4, Leather * 1.13f);
            return arm;
        }

        private Transform BuildLeg(float side, out Transform knee)
        {
            Transform leg = Joint(_body, side < 0 ? "Left hip" : "Right hip", new Vector3(side * .171f, .52f, 0f));
            Ball(leg, "Short work trousers", new Vector3(0f, -.092f, 0f), new Vector3(.275f, .29f, .289f), 4, Leather * .85f);
            Box(leg, "Cargo pocket", new Vector3(side * .127f, -.07f, .026f), new Vector3(.063f, .153f, .16f), 4, Leather);
            knee = Joint(leg, "Knee", Vector3.down * .21f);
            Ball(knee, "Trouser cuff", new Vector3(0f, -.034f, 0f), new Vector3(.239f, .111f, .248f), 4, Leather * .66f);
            Transform boot = Joint(knee, "Heavy boot", new Vector3(0f, -.19f, .058f));
            Ball(boot, "Rounded boot toe", Vector3.zero, new Vector3(.278f, .214f, .365f), 6, Boot);
            Ball(boot, "Boot ankle", new Vector3(0f, .06f, -.044f), new Vector3(.238f, .18f, .251f), 6, Boot * 1.22f);
            Box(boot, "Thick boot sole", new Vector3(0f, -.098f, .004f), new Vector3(.285f, .052f, .365f), 4, Leather * .76f);
            for (int i = 0; i < 3; i++)
                Ball(boot, "Gold boot lace", new Vector3(0f, .067f - i * .024f, .065f + i * .03f), new Vector3(.137f, .015f, .017f), 7, Gold * .8f);
            return leg;
        }

        private void BuildTools()
        {
            _pickaxe = Joint(_rightArm.Hand, "Pickaxe", new Vector3(0f, -.025f, .035f));
            Cylinder(_pickaxe, "Wooden pick handle", new Vector3(0f, .16f, 0f), new Vector3(.04f, .65f, .04f), 4, Leather);
            Cylinder(_pickaxe, "Wrapped pick grip", new Vector3(0f, -.053f, 0f), new Vector3(.052f, .18f, .052f), 6, Boot);
            Box(_pickaxe, "Pick head socket", new Vector3(0f, .454f, 0f), new Vector3(.091f, .13f, .09f), 2, Metal);
            Part(_pickaxe, "Curved iron pick head", PickHead(), new Vector3(0f, .457f, 0f), Vector3.one, 2, Metal);
            Ball(_pickaxe, "Pick head bolt", new Vector3(0f, .453f, .054f), Vector3.one * .042f, 2, new Color(.7f, .69f, .6f));

            _detector = Joint(_rightArm.Hand, "Ore detector", new Vector3(0f, .04f, .07f));
            Box(_detector, "Detector rubber grip", new Vector3(0f, -.025f, 0f), new Vector3(.062f, .19f, .078f), 6, Boot);
            Box(_detector, "Detector yellow housing", new Vector3(0f, .126f, .015f), new Vector3(.225f, .197f, .091f), 7, Gold);
            Box(_detector, "Detector black screen", new Vector3(0f, .14f, .064f), new Vector3(.172f, .119f, .015f), 6, new Color(.045f, .10f, .07f));
            for (int i = 0; i < 5; i++)
                Box(_detector, "Detector green readout", new Vector3(-.058f + i * .029f, .121f, .076f),
                    new Vector3(.015f, .03f + (i % 3) * .02f, .006f), 5, new Color(.42f, 1f, .26f));
            Ball(_detector, "Detector button", new Vector3(0f, .036f, .068f), Vector3.one * .031f, 6, Boot);
        }

        private void BuildSocialProps()
        {
            _bottle = Joint(_rightArm.Hand, "Drink bottle", new Vector3(0f, .014f, .055f));
            Cylinder(_bottle, "Blue bottle body", new Vector3(0f, .045f, 0f), new Vector3(.105f, .226f, .105f), 2, new Color(.18f, .38f, .55f));
            Ball(_bottle, "Bottle shoulder", new Vector3(0f, .16f, 0f), new Vector3(.101f, .076f, .101f), 2, new Color(.18f, .38f, .55f));
            Cylinder(_bottle, "Bottle neck", new Vector3(0f, .208f, 0f), new Vector3(.043f, .059f, .043f), 2, Metal);
            Box(_bottle, "Bottle pale label", new Vector3(0f, .043f, .052f), new Vector3(.047f, .08f, .008f), 7, new Color(.91f, .9f, .73f));
            _pipe = Joint(_body, "Smoking pipe", new Vector3(.029f, 1.293f, .257f));
            Cylinder(_pipe, "Pipe stem", new Vector3(.022f, -.014f, .09f), new Vector3(.018f, .205f, .018f), 4, Boot, Quaternion.Euler(90f, 0f, -8f));
            Cylinder(_pipe, "Pipe bowl", new Vector3(.025f, .008f, .203f), new Vector3(.078f, .099f, .078f), 4, Leather);
            Cylinder(_pipe, "Pipe dark bowl opening", new Vector3(.025f, .061f, .203f), new Vector3(.058f, .009f, .058f), 6, Boot);
            Ball(_pipe, "Pipe ember", new Vector3(.024f, .067f, .203f), new Vector3(.026f, .009f, .026f), 7, new Color(.93f, .25f, .045f));
            _smokeMaterial = MakeSmokeMaterial();
            for (int i = 0; i < _puffs.Length; i++)
            {
                Renderer puff = Part(_pipe, "Drifting smoke " + i, _ball, new Vector3(.025f, .1f, .203f), Vector3.one * .03f, 0, Color.white);
                puff.sharedMaterial = _smokeMaterial;
                puff.shadowCastingMode = ShadowCastingMode.Off;
                puff.receiveShadows = false;
                _puffs[i] = puff.transform;
                _puffRenderers[i] = puff;
            }
            _stars = Joint(_body, "Stun stars", new Vector3(0f, 1.79f, 0f));
            for (int i = 0; i < 3; i++)
            {
                float angle = i * Mathf.PI * 2f / 3f;
                Transform star = Joint(_stars, "Dizzy sparkle", new Vector3(Mathf.Cos(angle) * .29f, .06f, Mathf.Sin(angle) * .25f));
                Box(star, "Spark horizontal", Vector3.zero, new Vector3(.088f, .022f, .024f), 7, Gold);
                Box(star, "Spark vertical", Vector3.zero, new Vector3(.022f, .088f, .024f), 7, Gold);
            }
            _bottle.gameObject.SetActive(false);
            _pipe.gameObject.SetActive(false);
            _stars.gameObject.SetActive(false);
        }

        private void BuildName()
        {
            _labelRoot = Joint(transform, "Name billboard", new Vector3(0f, 1.99f, 0f));
            _name = _labelRoot.gameObject.AddComponent<TextMesh>();
            _nameFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _name.font = _nameFont;
            _name.fontSize = 40;
            _name.characterSize = .055f;
            _name.anchor = TextAnchor.MiddleCenter;
            _name.alignment = TextAlignment.Center;
            _name.color = new Color(1f, .96f, .77f);
            var renderer = _name.GetComponent<MeshRenderer>();
            if (_nameFont != null) renderer.sharedMaterial = _nameFont.material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _name.text = "MINER";
        }

        public void Pose(PublicMiner miner, CampView view, bool hasHand, ItemKind handKind, bool assisting)
        {
            if (!_ready || miner == null || view == null) return;
            if (_minerId != miner.Id)
            {
                _minerId = miner.Id;
                uint hash = 17;
                if (_minerId != null) foreach (char letter in _minerId) hash = unchecked(hash * 31 + letter);
                _phaseOffset = hash % 1000 * .01f;
                _havePosition = false;
            }
            if (_displayName != miner.Name || _colorIndex != miner.ColorIndex)
            {
                _displayName = miner.Name;
                _colorIndex = miner.ColorIndex;
                Color patch = TeamColor(_colorIndex);
                foreach (Renderer renderer in _patches) Tint(renderer, patch);
                RefreshName();
            }
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, .001f, .1f);
            bool seated = miner.VehicleSeat >= 0;
            Vector3 position = transform.position;
            float speed = _havePosition ? Vector3.Distance(position, _lastPosition) / dt : 0f;
            _lastPosition = position;
            _havePosition = true;
            _motion = seated ? 0f : Mathf.MoveTowards(_motion, Mathf.Clamp01(speed / 3f), dt * 6f);
            if (!seated) _stepPhase += dt * Mathf.Lerp(2f, 9f, _motion);
            float time = Time.unscaledTime + _phaseOffset;
            float step = Mathf.Sin(_stepPhase), alternate = Mathf.Sin(_stepPhase + Mathf.PI);
            bool actionActive = miner.ActionUntil > view.OnlineSeconds;
            bool climbing = !seated && !string.IsNullOrEmpty(miner.ClimbingLadderId);
            bool stunned = miner.StunnedUntil > view.OnlineSeconds;
            bool swingingPickaxe = !seated && actionActive && (miner.Action == "mine" || miner.Action == "attack")
                && !hasHand && !assisting && !climbing && !stunned;
            bool robbing = !seated && actionActive && miner.Action == "rob" && !hasHand && !assisting && !climbing && !stunned;
            bool detecting = !seated && actionActive && miner.Action == "detect" && !hasHand && !climbing;
            bool smoking = !seated && actionActive && miner.Action == "smoke";
            bool drinking = !seated && actionActive && miner.Action == "drink";
            float bob = Mathf.Abs(step) * .016f * _motion;
            _body.localPosition = seated ? SeatedBodyOffset : new Vector3(0f, bob, 0f);
            _labelRoot.localPosition = new Vector3(0f, 1.99f, 0f) + (seated ? SeatedBodyOffset : Vector3.zero);
            _body.localRotation = seated ? Quaternion.identity : Quaternion.Euler(stunned ? 13f : robbing ? 18f + Mathf.Sin(time * 5f) * 2f : 0f,
                swingingPickaxe ? Mathf.Sin(time * 10f) * 7f : 0f,
                stunned ? Mathf.Sin(time * 5f) * 9f : -step * 1.7f * _motion);

            Vector3 left = new Vector3(-.51f, .735f, .035f + step * .15f * _motion);
            Vector3 right = new Vector3(.51f, .735f, .035f + alternate * .15f * _motion);
            float legAngle = step * 26f * _motion;
            _leftLeg.localRotation = Quaternion.Euler(legAngle, 0f, -2f);
            _rightLeg.localRotation = Quaternion.Euler(-legAngle, 0f, 2f);
            _leftKnee.localRotation = Quaternion.Euler(Mathf.Max(0f, -step) * 20f * _motion, 0f, 0f);
            _rightKnee.localRotation = Quaternion.Euler(Mathf.Max(0f, step) * 20f * _motion, 0f, 0f);
            if (seated)
            {
                PoseVehicleSeat(miner.VehicleSeat, out left, out right);
            }
            else if (climbing)
            {
                float climb = Mathf.Sin(time * 5f) * .10f * Mathf.Clamp01(_motion * 2f);
                left = new Vector3(-.26f, 1.48f + climb, .24f);
                right = new Vector3(.26f, 1.48f - climb, .24f);
                _leftLeg.localRotation = Quaternion.Euler(-43f + climb * 130f, 0f, -4f);
                _rightLeg.localRotation = Quaternion.Euler(-43f - climb * 130f, 0f, 4f);
                _leftKnee.localRotation = Quaternion.Euler(62f - climb * 160f, 0f, 0f);
                _rightKnee.localRotation = Quaternion.Euler(62f + climb * 160f, 0f, 0f);
            }
            else if (hasHand || assisting)
            {
                bool heavy = handKind == ItemKind.HeavyOre || assisting;
                left = new Vector3(heavy ? -.31f : -.21f, heavy ? 1.02f : .96f, .40f);
                right = new Vector3(-left.x, left.y, left.z);
            }
            else if (robbing)
            {
                float reach = Mathf.Sin(time * 8f) * .065f;
                left = new Vector3(-.22f, .91f + reach * .4f, .37f + reach);
                right = new Vector3(.22f, .91f - reach * .4f, .37f - reach);
            }
            else if (swingingPickaxe)
            {
                float stroke = .5f + .5f * Mathf.Sin(time * 11f);
                right = Vector3.Lerp(new Vector3(.33f, 1.59f, .11f), new Vector3(.12f, .98f, .46f), stroke);
                left = Vector3.Lerp(new Vector3(-.10f, 1.3f, .31f), new Vector3(-.13f, .96f, .4f), stroke);
            }
            else if (detecting)
            {
                right = new Vector3(.18f + Mathf.Sin(time * 3f) * .10f, 1.02f, .41f);
                left = new Vector3(-.30f, .81f, .12f);
            }
            if (drinking) right = new Vector3(.155f, 1.13f, .34f);
            if (smoking) left = new Vector3(-.19f, 1.08f, .27f);
            if (stunned && !seated) { left = new Vector3(-.53f, .88f, -.035f); right = new Vector3(.53f, .88f, -.035f); }
            AimArm(_leftArm, left);
            AimArm(_rightArm, right);
            _pickaxe.gameObject.SetActive(!seated && !hasHand && !assisting && !climbing && !detecting && !drinking && !smoking && !robbing && !stunned);
            _pickaxe.localRotation = Quaternion.Euler(swingingPickaxe ? -38f : 17f, 0f, -12f);
            _detector.gameObject.SetActive(detecting && !stunned);
            _bottle.gameObject.SetActive(drinking && !stunned);
            if (drinking)
            {
                Vector3 bottleOrigin = _body.InverseTransformPoint(_rightArm.Hand.position) + _bottle.localPosition;
                _bottle.localRotation = Quaternion.FromToRotation(Vector3.up, new Vector3(0f, 1.293f, .27f) - bottleOrigin);
            }
            _pipe.gameObject.SetActive(smoking && !stunned);
            _stars.gameObject.SetActive(stunned);
            _stars.localRotation = Quaternion.Euler(0f, time * 100f, 0f);
            CarryAnchor.localPosition = new Vector3(0f, 1f, handKind == ItemKind.HeavyOre ? .93f : .66f);
            PoseFace(miner.CheeksFull, smoking, drinking, stunned, time);
            if (smoking && !stunned) PoseSmoke(time);
        }

        private void PoseVehicleSeat(int seat, out Vector3 left, out Vector3 right)
        {
            // Horizontal thighs, vertical shins and level boots. No root motion,
            // walking, ladder cycling or suspension bob is inferred from car speed.
            _leftLeg.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            _rightLeg.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            _leftKnee.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _rightKnee.localRotation = Quaternion.Euler(90f, 0f, 0f);
            if (seat == 0)
            {
                // These grips align with the truck wheel at seat-local (0,.71,.22).
                left = new Vector3(-.185f, .952f, .398f);
                right = new Vector3(.185f, .952f, .398f);
            }
            else
            {
                // Relaxed elbows and mittens resting over the forward thighs.
                left = new Vector3(-.255f, .745f, .19f);
                right = new Vector3(.255f, .745f, .19f);
            }
        }

        private static void AimArm(Arm arm, Vector3 target)
        {
            const float length = .25f;
            Vector3 direction = target - arm.Origin;
            float distance = Mathf.Clamp(direction.magnitude, .055f, length * 2f - .003f);
            direction = direction.sqrMagnitude > .00001f ? direction.normalized : Vector3.down;
            Vector3 bend = new Vector3(arm.Side, -.45f, -.55f);
            bend = Vector3.ProjectOnPlane(bend, direction).normalized;
            if (bend.sqrMagnitude < .001f) bend = Vector3.forward;
            Vector3 elbow = arm.Origin + direction * (distance * .5f) + bend * Mathf.Sqrt(length * length - distance * distance * .25f);
            Vector3 hand = arm.Origin + direction * distance;
            Quaternion upper = Quaternion.FromToRotation(Vector3.down, elbow - arm.Origin);
            Quaternion lower = Quaternion.FromToRotation(Vector3.down, hand - elbow);
            arm.Shoulder.localRotation = upper;
            arm.Elbow.localRotation = Quaternion.Inverse(upper) * lower;
            // Keep props consistently upright in body space; shoulders/elbows still articulate.
            arm.Hand.localRotation = Quaternion.Inverse(lower);
        }

        private void PoseFace(bool cheeks, bool smoking, bool drinking, bool stunned, float time)
        {
            Vector3 cheekSize = cheeks ? new Vector3(.206f, .19f, .173f) : new Vector3(.12f, .12f, .055f);
            _leftCheek.localScale = cheekSize;
            _rightCheek.localScale = cheekSize;
            _mouth.localScale = drinking ? new Vector3(.044f, .034f, .02f) : cheeks ? new Vector3(.056f, .019f, .018f) : new Vector3(.1f, .012f, .013f);
            float blink = Mathf.Repeat(time, 4.4f) > 4.24f ? 1f : 0f;
            float lid = stunned ? .8f : drinking ? 1f : smoking ? .65f : blink;
            Vector3 lidScale = new Vector3(.118f, Mathf.Lerp(.004f, .102f, lid), .023f);
            _leftLid.localScale = lidScale;
            _rightLid.localScale = lidScale;
        }

        private void PoseSmoke(float time)
        {
            for (int i = 0; i < _puffs.Length; i++)
            {
                float age = Mathf.Repeat(time * .47f + i / (float)_puffs.Length, 1f);
                _puffs[i].localPosition = new Vector3(.025f + Mathf.Sin(age * 7f + i) * age * .042f,
                    .089f + age * .36f, .203f + age * .038f);
                _puffs[i].localScale = Vector3.one * Mathf.Lerp(.021f, .11f, age);
                Tint(_puffRenderers[i], new Color(.87f, .88f, .83f, (1f - age) * .36f));
            }
        }

        private void LateUpdate()
        {
            if (_labelRoot != null && Camera.main != null) _labelRoot.rotation = Camera.main.transform.rotation;
        }

        private void RefreshName()
        {
            if (_name == null) return;
            string fallback = "MINER " + (char)('A' + PositiveColor(_colorIndex));
            _name.text = _customFont && !string.IsNullOrWhiteSpace(_displayName) ? _displayName : fallback;
            _name.color = Color.Lerp(TeamColor(_colorIndex), Color.white, .55f);
        }

        private static int PositiveColor(int index) => (index % 4 + 4) % 4;
        private static Color TeamColor(int index)
        {
            switch (PositiveColor(index))
            {
                case 1: return new Color(.89f, .31f, .20f);
                case 2: return new Color(.22f, .51f, .9f);
                case 3: return new Color(.40f, .80f, .48f);
                default: return Gold;
            }
        }

        private static Transform Joint(Transform parent, string name, Vector3 position)
        {
            Transform joint = new GameObject(name).transform;
            joint.SetParent(parent, false);
            joint.localPosition = position;
            return joint;
        }

        private Renderer Ball(Transform parent, string name, Vector3 position, Vector3 scale, int palette, Color tint, Quaternion rotation = default) =>
            Part(parent, name, _ball, position, scale, palette, tint, rotation);
        private Renderer Box(Transform parent, string name, Vector3 position, Vector3 scale, int palette, Color tint, Quaternion rotation = default) =>
            Part(parent, name, _box, position, scale, palette, tint, rotation);
        private Renderer Cylinder(Transform parent, string name, Vector3 position, Vector3 scale, int palette, Color tint, Quaternion rotation = default) =>
            Part(parent, name, _cylinder, position, scale, palette, tint, rotation);

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
            if (_fallback == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader == null) throw new InvalidOperationException("Miner model needs a lit shader or palette material.");
                _fallback = new Material(shader) { name = "Runtime miner fallback" };
                _materials.Add(_fallback);
            }
            return _fallback;
        }

        private Material MakeSmokeMaterial()
        {
            var material = new Material(Surface(0)) { name = "Runtime miner smoke", renderQueue = 3000 };
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
            if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHATEST_ON");
            _materials.Add(material);
            return material;
        }

        private void Tint(Renderer renderer, Color tint)
        {
            _properties.Clear();
            _properties.SetColor(_baseColor, tint);
            _properties.SetColor(_color, tint);
            renderer.SetPropertyBlock(_properties);
        }

        private Mesh Sphere()
        {
            const int rings = 14;
            var height = new float[rings + 1];
            var radius = new float[rings + 1];
            for (int i = 0; i <= rings; i++)
            {
                float angle = Mathf.PI * i / rings;
                height[i] = -.5f * Mathf.Cos(angle);
                radius[i] = .5f * Mathf.Sin(angle);
            }
            return Lathe("Unit rounded part", height, radius, radius, 20);
        }

        private Mesh Lathe(string name, float[] height, float[] width, float[] depth, int segments = 24, float start = 0f, float end = 360f)
        {
            var vertices = new Vector3[height.Length * (segments + 1)];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[(height.Length - 1) * segments * 6];
            for (int y = 0; y < height.Length; y++)
                for (int x = 0; x <= segments; x++)
                {
                    float angle = Mathf.Lerp(start, end, x / (float)segments) * Mathf.Deg2Rad;
                    int index = y * (segments + 1) + x;
                    vertices[index] = new Vector3(Mathf.Sin(angle) * width[y], height[y], Mathf.Cos(angle) * depth[y]);
                    uv[index] = new Vector2(x / (float)segments, y / (float)(height.Length - 1));
                }
            int cursor = 0;
            for (int y = 0; y < height.Length - 1; y++)
                for (int x = 0; x < segments; x++)
                {
                    int a = y * (segments + 1) + x, b = a + 1, c = a + segments + 1, d = c + 1;
                    triangles[cursor++] = a; triangles[cursor++] = b; triangles[cursor++] = c;
                    triangles[cursor++] = b; triangles[cursor++] = d; triangles[cursor++] = c;
                }
            return MeshFrom(name, vertices, triangles, uv);
        }

        private Mesh BoxMesh()
        {
            var v = new[] { new Vector3(-.5f,-.5f,-.5f),new Vector3(.5f,-.5f,-.5f),new Vector3(.5f,.5f,-.5f),new Vector3(-.5f,.5f,-.5f),
                new Vector3(-.5f,-.5f,.5f),new Vector3(.5f,-.5f,.5f),new Vector3(.5f,.5f,.5f),new Vector3(-.5f,.5f,.5f) };
            return MeshFrom("Unit workwear detail", v, new[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,4,7,0,7,3,1,2,6,1,6,5,3,7,6,3,6,2,0,1,5,0,5,4 });
        }

        private Mesh PickHead()
        {
            Vector2[] outline = { new Vector2(-.30f,-.07f),new Vector2(-.20f,.055f),new Vector2(-.065f,.103f),
                new Vector2(.085f,.09f),new Vector2(.22f,.018f),new Vector2(.29f,-.09f),new Vector2(.12f,-.016f),new Vector2(-.10f,.018f) };
            var vertices = new Vector3[outline.Length * 2];
            for (int i = 0; i < outline.Length; i++)
            { vertices[i] = new Vector3(outline[i].x, outline[i].y, -.034f); vertices[i + outline.Length] = new Vector3(outline[i].x, outline[i].y, .034f); }
            var triangles = new List<int>();
            for (int i = 1; i < outline.Length - 1; i++)
            { triangles.Add(0); triangles.Add(i); triangles.Add(i + 1); triangles.Add(outline.Length); triangles.Add(i + 1 + outline.Length); triangles.Add(i + outline.Length); }
            for (int i = 0; i < outline.Length; i++)
            {
                int next = (i + 1) % outline.Length;
                triangles.Add(i); triangles.Add(i + outline.Length); triangles.Add(next);
                triangles.Add(next); triangles.Add(i + outline.Length); triangles.Add(next + outline.Length);
            }
            return MeshFrom("Curved tapered pick", vertices, triangles.ToArray());
        }

        private Mesh MeshFrom(string name, Vector3[] vertices, int[] triangles, Vector2[] uv = null)
        {
            var mesh = new Mesh { name = name, vertices = vertices, triangles = triangles };
            if (uv != null) mesh.uv = uv;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _meshes.Add(mesh);
            return mesh;
        }

        private void OnDestroy()
        {
            // Palette and font materials belong to the caller/project and are never destroyed here.
            foreach (Mesh mesh in _meshes) ReleaseOwned(mesh);
            foreach (Material material in _materials) ReleaseOwned(material);
            _meshes.Clear();
            _materials.Clear();
        }

        private static void ReleaseOwned(UnityEngine.Object owned)
        {
            if (owned == null) return;
            if (Application.isPlaying) Destroy(owned);
            else DestroyImmediate(owned);
        }
    }
}
