using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GoldCamp.Demo.Editor
{
    // Repeatable in-project geometry. References guide silhouette and color, never pasted into the scene.
    internal static class CampArtBuilder
    {
        private const string AssetRoot = "Assets/GoldCamp/Demo/Art";
        private static Material _wood, _trim, _roofGreen, _roofBlue, _roofRed, _roofGold, _rock, _pine, _leaf, _dirt, _water, _lamp, _metal, _canvas;
        private static Font _font;
        private static Mesh _cone, _roof, _stone;
        public static void Build(Font font)
        {
            Directory.CreateDirectory(AssetRoot); AssetDatabase.Refresh(); _font = font;
            _wood = Mat("Timber",new Color(.40f,.23f,.115f)); _trim = Mat("Cut timber",new Color(.62f,.40f,.20f));
            _roofGreen = Mat("Green roof",new Color(.24f,.37f,.25f)); _roofBlue = Mat("Blue roof",new Color(.23f,.36f,.49f));
            _roofRed = Mat("Red roof",new Color(.56f,.22f,.17f)); _roofGold = Mat("Ochre roof",new Color(.73f,.48f,.15f));
            _rock = Mat("Granite",new Color(.39f,.40f,.36f)); _pine = Mat("Pine dark",new Color(.16f,.29f,.15f));
            _leaf = Mat("Pine light",new Color(.32f,.42f,.20f)); _dirt = Mat("Packed earth",new Color(.66f,.50f,.30f));
            _water = Mat("River blue",new Color(.20f,.48f,.55f)); _metal = Mat("Iron hardware",new Color(.23f,.27f,.27f));
            _canvas = Mat("Canvas",new Color(.85f,.75f,.51f)); _lamp = Mat("Amber light",new Color(1f,.65f,.20f));
            _lamp.EnableKeyword("_EMISSION"); _lamp.SetColor("_EmissionColor",new Color(1f,.45f,.06f)*2f);
            _cone = MeshAsset("PineCone",Cone()); _roof = MeshAsset("Gable",Roof()); _stone = MeshAsset("FacetedStone",Stone());
            var root = new GameObject("Camp art / generated").transform;
            var roofs = new[] { _roofGreen, _roofRed, _roofBlue, _roofGold };
            for (int i=0;i<4;i++) Cabin(root,CampMap.CabinetPoint(i),i,roofs[i]);
            Stall(root,CampAuthority.SalePoint,"收购台",true);
            Stall(root,CampMap.RepairPoint,"维修 · 升级",false);
            Cutter(root,CampMap.CutterPoint);
            Table(root,CampMap.WagerPoint);
            Stall(root,CampMap.VehicleMaintenancePoint,"公共修车 · 分次贡献",false);
            Campfire(root,new Vector3(0,0,-10));
            // Broad, flat paths are visual surfaces; existing terrain remains the collision authority.
            Part(root,"Camp clearing",PrimitiveType.Cylinder,new Vector3(0,.012f,-4),new Vector3(17,.008f,25),_dirt);
            Path(root,new Vector3(-3,0,3),new Vector3(-5,0,19),3f);
            Path(root,new Vector3(6,0,6),new Vector3(21,0,34),4f);
            Path(root,new Vector3(21,0,34),new Vector3(23,0,47),4f);
            Path(root,CampMap.VehicleHome,new Vector3(22,0,40),4f);
            Sign(root,new Vector3(18,0,43),"进阶矿坑\n基础镐走左列 · 硬层需2级",Quaternion.identity);
            Sign(root,new Vector3(-6.7f,0,18),"初级矿坑\n有限矿体 · L 放梯",Quaternion.identity);
            Sign(root,new Vector3(9,0,12),"旧车 / 进阶道路\n修复后开放",Quaternion.Euler(0,-25,0));
            var entrance = new GameObject("Mine timber entrance").transform; entrance.SetParent(root,false); entrance.position=new Vector3(0,0,19.4f);
            Solid(entrance,"Left support",new Vector3(-6.65f,1.6f,0),new Vector3(.45f,3.2f,.5f),_wood);
            Solid(entrance,"Right support",new Vector3(6.65f,1.6f,0),new Vector3(.45f,3.2f,.5f),_wood);
            Part(entrance,"Header",PrimitiveType.Cube,new Vector3(0,3.1f,0),new Vector3(13.6f,.5f,.6f),_trim);
            Lantern(entrance,new Vector3(-6.25f,2.3f,-.4f)); Lantern(entrance,new Vector3(6.25f,2.3f,-.4f));
            var random = new System.Random(8701);
            for(int i=0;i<42;i++)
            {
                float side=i%2==0?-1:1; float x=side*(18+(float)random.NextDouble()*17);
                float z=-22+(float)random.NextDouble()*80;
                // Leave the short vehicle road and river collection point open.
                if(x>0 && z>14 && z<52) x=34;
                if(x>15 && x<29 && z>-12 && z<53) x=34;
                Pine(root,new Vector3(x,0,z),3.5f+(float)random.NextDouble()*4);
                if(i%3==0) Rock(root,new Vector3(x+2,0,z+1),new Vector3(2.3f,1.8f,2.1f));
            }
            for(int i=0;i<18;i++)
            {
                float side=i%2==0?-1:1; float z=-23+i*5.2f;
                Rock(root,new Vector3(side*42,-.8f,z),new Vector3(12,9+(i%4)*3,13));
            }
            for(int i=0;i<7;i++) Rock(root,new Vector3(-33+i*11,-1,69),new Vector3(14,13+(i%3)*5,15));
            // Scenic river sits outside the walking boundary; collection gravel stays accessible.
            Part(root,"River",PrimitiveType.Cube,new Vector3(38,-.03f,25),new Vector3(4,.04f,80),_water);
            for(int i=0;i<6;i++) Rock(root,new Vector3(33+i%2*2,0,-5+i*10),new Vector3(2.2f,1.1f,3.5f));
            Part(root,"River gravel",PrimitiveType.Cylinder,CampMap.RiverPoint+Vector3.up*.018f,new Vector3(5,.012f,5),_rock);
            Sign(root,new Vector3(12.7f,0,2.5f),"河滩碎矿\n在线缓慢恢复",Quaternion.identity);
            for(int i=0;i<8;i++)
            {
                float x=-14+i*4; Fence(root,new Vector3(x,0,-20.5f),4);
            }
            RenderSettings.fog = true; RenderSettings.fogMode=FogMode.Linear;
            RenderSettings.fogColor=new Color(.62f,.72f,.72f); RenderSettings.fogStartDistance=50; RenderSettings.fogEndDistance=115;
        }
        private static void Cabin(Transform parent,Vector3 cabinet,int index,Material roof)
        {
            var root=new GameObject("Cabin "+(index+1)).transform; root.SetParent(parent,false);
            root.position=cabinet; root.rotation=Quaternion.Euler(0,index<2?-90:90,0);
            // Door faces the central clearing. The personal cabinet stays at its established interaction point.
            Solid(root,"Back wall",new Vector3(0,1.45f,4),new Vector3(4.4f,2.9f,.22f),_wood);
            Solid(root,"Side wall L",new Vector3(-2.1f,1.45f,2),new Vector3(.22f,2.9f,4),_wood);
            Solid(root,"Side wall R",new Vector3(2.1f,1.45f,2),new Vector3(.22f,2.9f,4),_wood);
            Solid(root,"Front wall L",new Vector3(-1.45f,1.45f,0),new Vector3(1.25f,2.9f,.22f),_wood);
            Solid(root,"Front wall R",new Vector3(1.45f,1.45f,0),new Vector3(1.25f,2.9f,.22f),_wood);
            Part(root,"Porch boards",PrimitiveType.Cube,new Vector3(0,.025f,.9f),new Vector3(4.4f,.05f,6.2f),_trim);
            for(int i=0;i<7;i++)
            {
                float y=.24f+i*.4f;
                Part(root,"Wall seam",PrimitiveType.Cube,new Vector3(0,y,4.13f),new Vector3(4.45f,.055f,.025f),_trim);
                Part(root,"Wall seam",PrimitiveType.Cube,new Vector3(-2.23f,y,2),new Vector3(.025f,.055f,4),_trim);
                Part(root,"Wall seam",PrimitiveType.Cube,new Vector3(2.23f,y,2),new Vector3(.025f,.055f,4),_trim);
            }
            foreach(float x in new[]{-2.15f,2.15f}) foreach(float z in new[]{-.1f,4.1f})
                Part(root,"Beam",PrimitiveType.Cube,new Vector3(x,1.5f,z),new Vector3(.22f,3.1f,.22f),_trim);
            MeshPart(root,"Pitched roof",_roof,new Vector3(0,2.85f,2),new Vector3(4.9f,1.5f,5.4f),roof);
            for(int i=0;i<9;i++) Part(root,"Roof batten",PrimitiveType.Cube,new Vector3(-2.35f+i*.59f,4.37f-Mathf.Abs(-2.35f+i*.59f)*.612f,2),new Vector3(.035f,.035f,5.5f),roof);
            Part(root,"Chimney",PrimitiveType.Cube,new Vector3(1.3f,3.75f,3),new Vector3(.48f,1.8f,.48f),_rock);
            Lantern(root,new Vector3(-.85f,2.3f,-.4f)); Lantern(root,new Vector3(1.5f,1.7f,-.4f));
            Text(root,"私人柜 · "+(index+1),new Vector3(0,2.65f,-.24f),.08f);
            Barrel(root,new Vector3(-2.9f,.5f,-.8f)); Crate(root,new Vector3(2.8f,.4f,-1),.8f);
        }
        private static void Stall(Transform parent,Vector3 position,string title,bool market)
        {
            var root=new GameObject(title+" details").transform; root.SetParent(parent,false);root.position=position;
            Part(root,"Counter top",PrimitiveType.Cube,new Vector3(0,1.15f,0),new Vector3(2.8f,.18f,1.4f),_trim);
            for(int i=0;i<7;i++) Part(root,"Counter slat",PrimitiveType.Cube,new Vector3(-1.2f+i*.4f,.6f,-.57f),new Vector3(.33f,1f,.12f),_wood);
            if(market)
            {
                foreach(float x in new[]{-1.45f,1.45f}) Part(root,"Canopy post",PrimitiveType.Cylinder,new Vector3(x,1.6f,.45f),new Vector3(.16f,1.6f,.16f),_wood);
                MeshPart(root,"Canvas canopy",_roof,new Vector3(0,2.65f,0),new Vector3(3.8f,.6f,2.8f),_canvas);
                Crate(root,new Vector3(-2.1f,.45f,0),.9f); Barrel(root,new Vector3(2.1f,.55f,0));
            }
            else
            {
                Part(root,"Anvil base",PrimitiveType.Cylinder,new Vector3(-.55f,1.33f,0),new Vector3(.55f,.15f,.5f),_metal);
                Part(root,"Anvil",PrimitiveType.Cube,new Vector3(-.55f,1.52f,0),new Vector3(.9f,.18f,.32f),_metal);
                Part(root,"Tool handle",PrimitiveType.Cube,new Vector3(.7f,1.28f,0),new Vector3(.08f,.08f,.9f),_wood);
                Part(root,"Hammer",PrimitiveType.Cube,new Vector3(.7f,1.30f,.35f),new Vector3(.4f,.16f,.16f),_metal);
            }
            Text(root,title,new Vector3(0,1.75f,-.65f),.095f);
        }
        private static void Cutter(Transform parent,Vector3 position)
        {
            Stall(parent,position,"原石加工",false);
            var root=new GameObject("Cutting wheel").transform;root.SetParent(parent,false);root.position=position;
            var wheel=Part(root,"Stone wheel",PrimitiveType.Cylinder,new Vector3(.5f,1.65f,.2f),new Vector3(1.05f,.13f,1.05f),_rock);
            wheel.transform.localRotation=Quaternion.Euler(0,0,90);
            Part(root,"Wheel axle",PrimitiveType.Cube,new Vector3(.5f,1.55f,.2f),new Vector3(1.2f,.15f,.15f),_metal);
        }
        private static void Table(Transform parent,Vector3 position)
        {
            var root=new GameObject("Two-stone wager table").transform;root.SetParent(parent,false);root.position=position;
            Part(root,"Tabletop",PrimitiveType.Cube,new Vector3(0,1.13f,0),new Vector3(2.8f,.2f,1.6f),_trim);
            Part(root,"Felt",PrimitiveType.Cube,new Vector3(0,1.24f,0),new Vector3(2.45f,.018f,1.3f),_roofGreen);
            foreach(float x in new[]{-.7f,.7f}) Part(root,"Stake tray",PrimitiveType.Cylinder,new Vector3(x,1.28f,0),new Vector3(.64f,.025f,.64f),_metal);
            Text(root,"原石对赌 · 自愿托管",new Vector3(0,1.75f,-.8f),.085f);
            foreach(float x in new[]{-1.4f,1.4f}) Lantern(root,new Vector3(x,1.7f,.55f));
        }
        private static void Campfire(Transform parent,Vector3 position)
        {
            var root=new GameObject("Campfire circle").transform;root.SetParent(parent,false);root.position=position;
            for(int i=0;i<9;i++)
            {float a=i*Mathf.PI*2/9;MeshPart(root,"Hearth stone",_stone,new Vector3(Mathf.Cos(a),.12f,Mathf.Sin(a)),new Vector3(.5f,.35f,.45f),_rock);}
            for(int i=0;i<3;i++) {var log=Part(root,"Firewood",PrimitiveType.Cylinder,new Vector3(0,.24f,0),new Vector3(.22f,.75f,.22f),_wood);log.transform.localRotation=Quaternion.Euler(90,i*60,0);}
            MeshPart(root,"Fire flame",_cone,new Vector3(0,.28f,0),new Vector3(.58f,1.15f,.58f),_lamp);
            var light=new GameObject("Warm fire light").AddComponent<Light>();light.transform.SetParent(root,false);light.transform.localPosition=Vector3.up*1.2f;
            light.type=LightType.Point;light.color=new Color(1,.52f,.2f);light.range=6;light.intensity=1.8f;light.shadows=LightShadows.None;
            foreach(float x in new[]{-2.8f,2.8f})
            {Solid(root,"Bench",new Vector3(x,.45f,0),new Vector3(.65f,.25f,2.7f),_wood);}
        }
        private static void Pine(Transform parent,Vector3 p,float height)
        {
            var root=new GameObject("Pine").transform;root.SetParent(parent,false);root.position=p;
            Solid(root,"Trunk",new Vector3(0,height*.22f,0),new Vector3(.32f,height*.44f,.32f),_wood);
            for(int i=0;i<3;i++) MeshPart(root,"Foliage",_cone,new Vector3(0,height*(.18f+i*.19f),0),new Vector3(height*(.56f-i*.11f),height*.5f,height*(.56f-i*.11f)),i%2==0?_pine:_leaf);
        }
        private static void Rock(Transform parent,Vector3 p,Vector3 scale) => MeshPart(parent,"Rock outcrop",_stone,p+Vector3.up*scale.y*.4f,scale,_rock);
        private static void Path(Transform parent,Vector3 a,Vector3 b,float width)
        {var p=Part(parent,"Dirt path",PrimitiveType.Cube,(a+b)*.5f+Vector3.up*.018f,new Vector3(width,.025f,Vector3.Distance(a,b)),_dirt);p.transform.rotation=Quaternion.LookRotation(b-a);}
        private static void Fence(Transform parent,Vector3 p,float length)
        {foreach(float x in new[]{-length/2,length/2}) Solid(parent,"Fence post",p+new Vector3(x,.6f,0),new Vector3(.2f,1.2f,.2f),_wood);foreach(float y in new[]{.4f,.85f})Part(parent,"Fence rail",PrimitiveType.Cube,p+new Vector3(0,y,0),new Vector3(length,.13f,.13f),_trim);}
        private static void Barrel(Transform parent,Vector3 p)
        {Part(parent,"Barrel",PrimitiveType.Cylinder,p,new Vector3(.65f,.55f,.65f),_wood);foreach(float y in new[]{-.32f,.32f})Part(parent,"Barrel hoop",PrimitiveType.Cylinder,p+Vector3.up*y,new Vector3(.69f,.045f,.69f),_metal);}
        private static void Crate(Transform parent,Vector3 p,float size)
        {Part(parent,"Crate",PrimitiveType.Cube,p,Vector3.one*size,_wood);foreach(float x in new[]{-.4f,.4f})Part(parent,"Crate strap",PrimitiveType.Cube,p+new Vector3(x*size,0,-.505f*size),new Vector3(.075f,size,.03f),_trim);}
        private static void Lantern(Transform parent,Vector3 p)
        {Part(parent,"Lantern iron",PrimitiveType.Cube,p,new Vector3(.23f,.38f,.23f),_metal);Part(parent,"Lantern glow",PrimitiveType.Cube,p+Vector3.back*.035f,new Vector3(.16f,.25f,.25f),_lamp);}
        private static void Sign(Transform parent,Vector3 p,string title,Quaternion rotation)
        {var root=new GameObject("Wayfinding").transform;root.SetParent(parent,false);root.position=p;root.rotation=rotation;Part(root,"Signpost",PrimitiveType.Cube,Vector3.up*.9f,new Vector3(.14f,1.8f,.14f),_wood);Part(root,"Signboard",PrimitiveType.Cube,new Vector3(0,1.6f,0),new Vector3(2.4f,.9f,.12f),_wood);Text(root,title,new Vector3(0,1.6f,-.08f),.072f);}
        private static void Text(Transform parent,string text,Vector3 p,float size)
        {var t=new GameObject("Label").AddComponent<TextMesh>();t.transform.SetParent(parent,false);t.transform.localPosition=p;t.text=text;t.characterSize=size*.55f;t.fontSize=48;t.anchor=TextAnchor.MiddleCenter;t.alignment=TextAlignment.Center;t.color=new Color(.97f,.88f,.65f);if(_font!=null){t.font=_font;t.GetComponent<MeshRenderer>().sharedMaterial=_font.material;}}
        private static GameObject Solid(Transform parent,string name,Vector3 p,Vector3 scale,Material mat)
        {var go=Part(parent,name,PrimitiveType.Cube,p,scale,mat);go.AddComponent<BoxCollider>();return go;}
        private static GameObject Part(Transform parent,string name,PrimitiveType type,Vector3 p,Vector3 scale,Material mat)
        {var go=GameObject.CreatePrimitive(type);go.name=name;go.transform.SetParent(parent,false);go.transform.localPosition=p;go.transform.localScale=scale;UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());go.GetComponent<Renderer>().sharedMaterial=mat;go.isStatic=true;return go;}
        private static GameObject MeshPart(Transform parent,string name,Mesh mesh,Vector3 p,Vector3 scale,Material mat)
        {var go=new GameObject(name);go.transform.SetParent(parent,false);go.transform.localPosition=p;go.transform.localScale=scale;go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=mat;go.isStatic=true;return go;}
        private static Material Mat(string name,Color color)
        {string path=AssetRoot+"/"+name+".mat";var m=AssetDatabase.LoadAssetAtPath<Material>(path);if(m==null){m=new Material(Shader.Find("Universal Render Pipeline/Lit"));AssetDatabase.CreateAsset(m,path);}m.color=color;EditorUtility.SetDirty(m);return m;}
        private static Mesh MeshAsset(string name,Mesh mesh)
        {string path=AssetRoot+"/"+name+".asset";var prior=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(prior!=null){EditorUtility.CopySerialized(mesh,prior);UnityEngine.Object.DestroyImmediate(mesh);EditorUtility.SetDirty(prior);return prior;}mesh.name=name;AssetDatabase.CreateAsset(mesh,path);return mesh;}
        private static Mesh Flat(Vector3[] points,int[] indices)
        {var vertices=new Vector3[indices.Length];var triangles=new int[indices.Length];for(int i=0;i<indices.Length;i++){vertices[i]=points[indices[i]];triangles[i]=i;}var mesh=new Mesh{vertices=vertices,triangles=triangles};mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;}
        private static Mesh Roof() => Flat(new[]{new Vector3(-.5f,0,-.5f),new Vector3(.5f,0,-.5f),new Vector3(0,1,-.5f),new Vector3(-.5f,0,.5f),new Vector3(.5f,0,.5f),new Vector3(0,1,.5f)},new[]{0,2,1,3,4,5,0,3,5,0,5,2,2,5,4,2,4,1,0,1,4,0,4,3});
        private static Mesh Cone()
        {var points=new List<Vector3>{Vector3.up,Vector3.zero};var indices=new List<int>();for(int i=0;i<7;i++){float a=i*Mathf.PI*2/7;points.Add(new Vector3(Mathf.Cos(a)*.5f,0,Mathf.Sin(a)*.5f));}for(int i=0;i<7;i++){int a=2+i,b=2+(i+1)%7;indices.AddRange(new[]{0,b,a,1,a,b});}return Flat(points.ToArray(),indices.ToArray());}
        private static Mesh Stone() => Flat(new[]{new Vector3(0,.7f,0),new Vector3(-.56f,.15f,-.37f),new Vector3(.4f,.28f,-.5f),new Vector3(.56f,.08f,.4f),new Vector3(-.38f,.22f,.55f),new Vector3(0,-.45f,0)},new[]{0,2,1,0,3,2,0,4,3,0,1,4,5,1,2,5,2,3,5,3,4,5,4,1});
    }
}
