using System;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    // The same generator runs in the Editor and when a saved world's geometry
    // differs from the current tuning asset. No save or mine data is rewritten.
    public sealed class CampTerrain : MonoBehaviour
    {
        public CampSession Session;
        public Material Ground, Rock, Timber;
        [SerializeField] private Transform _terrainRoot;
        [SerializeField] private GameObject _gate;
        [SerializeField] private string _geometry;

        public void Configure(CampRules rules)
        {
            string key = string.Join("|", rules.CellSize, rules.PitWidth, rules.PitLength, rules.PitDepth,
                rules.AdvancedWidth, rules.AdvancedLength, rules.AdvancedDepth);
            if (_terrainRoot != null && _geometry == key) return;
            float size = rules.CellSize;
            Vector3 first = CampMap.PitOrigin, second = CampMap.AdvancedOrigin;
            float width = rules.PitWidth * size, length = rules.PitLength * size;
            float advancedWidth = rules.AdvancedWidth * size, advancedLength = rules.AdvancedLength * size;
            if (!float.IsFinite(size) || size <= 0 || width <= 0 || length <= 0 || advancedWidth <= 0 || advancedLength <= 0 ||
                first.x + width >= 12 || first.z + length >= 40 || second.x + advancedWidth >= 38 || second.z + advancedLength >= 64 ||
                rules.PitDepth < 1 || rules.AdvancedDepth < 1)
                throw new InvalidOperationException("矿坑配置超出当前受控地图边界；保留存档，请恢复支持的配置。");
            if (_terrainRoot != null)
            {
                _terrainRoot.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(_terrainRoot.gameObject); else DestroyImmediate(_terrainRoot.gameObject);
            }
            _terrainRoot = new GameObject("Terrain generated").transform; _terrainRoot.SetParent(transform, false);
            _geometry = key;
            float thickness = Mathf.Max(rules.PitDepth, rules.AdvancedDepth) * size + 1;
            float[] xs = new[] { -40f, first.x, first.x + width, second.x, second.x + advancedWidth, 40f }.Distinct().OrderBy(v=>v).ToArray();
            float[] zs = new[] { -25f, first.z, first.z + length, second.z, second.z + advancedLength, 65f }.Distinct().OrderBy(v=>v).ToArray();
            for (int x=0;x<xs.Length-1;x++) for(int z=0;z<zs.Length-1;z++)
            {
                float cx=(xs[x]+xs[x+1])*.5f, cz=(zs[z]+zs[z+1])*.5f;
                if (Inside(cx,cz,first,width,length) || Inside(cx,cz,second,advancedWidth,advancedLength)) continue;
                Box("Solid bank",new Vector3(cx,-thickness*.5f,cz),new Vector3(xs[x+1]-xs[x],thickness,zs[z+1]-zs[z]),Ground);
            }
            Box("Starter bedrock",first+new Vector3(width*.5f,-rules.PitDepth*size-.5f,length*.5f),new Vector3(width,1,length),Rock);
            Box("Advanced bedrock",second+new Vector3(advancedWidth*.5f,-rules.AdvancedDepth*size-.5f,advancedLength*.5f),new Vector3(advancedWidth,1,advancedLength),Rock);
            Box("Boundary west",new Vector3(-40,1,20),new Vector3(.6f,3,90),Rock);
            Box("Boundary east",new Vector3(40,1,20),new Vector3(.6f,3,90),Rock);
            Box("Boundary south",new Vector3(0,1,-25),new Vector3(80,3,.6f),Rock);
            Box("Boundary north",new Vector3(0,1,65),new Vector3(80,3,.6f),Rock);
            // The western ridge prevents bypassing the locked eastern road. After
            // repair, the same wide opening supports both walking and driving.
            Box("Advanced western ridge",new Vector3(12,1.35f,52.5f),new Vector3(.7f,3.2f,25),Rock);
            Box("Road left embankment",new Vector3(16,1.35f,40),new Vector3(8,3.2f,.7f),Rock);
            Box("Road right embankment",new Vector3(32,1.35f,40),new Vector3(16,3.2f,.7f),Rock);
            _gate = Box("Repair locked road gate",new Vector3(22,1.45f,40),new Vector3(4,3,.5f),Timber);
            Physics.SyncTransforms();
        }
        private static bool Inside(float x,float z,Vector3 origin,float width,float length) =>
            x>origin.x && x<origin.x+width && z>origin.z && z<origin.z+length;
        private GameObject Box(string title,Vector3 position,Vector3 scale,Material material)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name=title; go.transform.SetParent(_terrainRoot,false);
            go.transform.position=position;go.transform.localScale=scale;go.GetComponent<Renderer>().sharedMaterial=material;return go;
        }
        private void LateUpdate()
        {
            var rules=Session?.Authority?.Rules ?? Session?.View?.Rules;
            if(rules!=null)Configure(rules);
            if(_gate!=null)_gate.SetActive(!(Session?.Authority?.State.RoadUnlocked ?? Session?.View?.RoadUnlocked ?? false));
        }
    }
}
