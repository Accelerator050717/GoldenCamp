using System;
using UnityEngine;

namespace GoldCamp.Demo
{
    [CreateAssetMenu(menuName = "GoldCamp/Art sample set")]
    public sealed class CampArtSet : ScriptableObject
    {
        [Serializable] public sealed class Entry { public string Key; public GameObject Prefab; }
        public Entry[] Models = Array.Empty<Entry>();
        public Material[] MinerVariants = Array.Empty<Material>();
        private static CampArtSet _loaded;
        public static string RawModelKey(int stage, string publicCore) => stage == 0 ? "RawClosed" : stage == 1 ? "RawWindow" :
            publicCore == "gold" || publicCore == "rich_gold" ? "RawOpenGold" : publicCore == "iron" ? "RawOpenIron" : "RawOpenStone";
        public static CampArtSet Current => _loaded != null ? _loaded : (_loaded = Resources.Load<CampArtSet>("GoldCampArtSample"));
        public GameObject Find(string key)
        {
            foreach (var entry in Models) if (entry.Key == key) return entry.Prefab;
            return null;
        }
        public static Transform Spawn(string key, Transform parent)
        {
            var asset = Current;
            var prefab = asset != null ? asset.Find(key) : null;
            if (prefab == null) return null; // M0 and older authoring contexts keep their original visuals.
            var go = Instantiate(prefab, parent, false);
            go.name = key;
            return go.transform;
        }
        public static Transform Node(Transform root, string name)
        {
            foreach (var node in root.GetComponentsInChildren<Transform>(true))
                if (node.name == name) return node;
            throw new InvalidOperationException("Art model lacks required pivot: " + name);
        }
    }
}
