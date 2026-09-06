using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace GoldCamp.Demo.Editor
{
    // Actual imported models and the gameplay Pose method. Does not open a save,
    // simulate a human playtest, or persist the temporary review scene.
    public static class CampArtPreview
    {
        public static void Render()
        {
            CampSampleImporter.Prepare();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var set = AssetDatabase.LoadAssetAtPath<CampArtSet>(CampSampleImporter.SetPath);
            if (set == null) throw new InvalidOperationException("Import Sample01 before preview.");
            var parent = new GameObject("Actual model preview");
            var model = CampMinerModel.Create(parent.transform, null);
            model.transform.Find("Name billboard").gameObject.SetActive(false);
            var miner = new PublicMiner { Id = "art-preview", Name = "Sample01", Online = true, VehicleSeat = -1, ColorIndex = 0 };
            var view = new CampView { OnlineSeconds = 1 };
            model.Pose(miner, view, false, ItemKind.Stone, false);
            var body = CampArtSet.Node(model.transform, "Body");
            var shoulder = CampArtSet.Node(model.transform, "LeftShoulder");
            var pivot = body.InverseTransformPoint(shoulder.position);
            Debug.Log("ART_PIVOT " + pivot.ToString("F5") + " root=" + model.transform.GetChild(0).localEulerAngles + " mouth=" + CampArtSet.Node(model.transform,"Mouth").localPosition);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.transform.position = new Vector3(0,-.065f,0);floor.transform.localScale = new Vector3(200,.08f,200);
            var floorMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));floorMaterial.color = new Color(.32f,.36f,.36f); floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;
            var sun = new GameObject("Preview key").AddComponent<Light>();sun.type = LightType.Directional;sun.intensity=2.2f;sun.shadows=LightShadows.Soft;sun.color=new Color(1,.92f,.8f);sun.transform.rotation=Quaternion.Euler(35,-25,0);
            var fill = new GameObject("Preview fill").AddComponent<Light>();fill.type=LightType.Directional;fill.intensity=.8f;fill.color=new Color(.7f,.84f,1);fill.transform.rotation=Quaternion.Euler(20,150,0);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=new Color(.65f,.67f,.66f);RenderSettings.fog=false;
            var camera = new GameObject("Actual model review camera").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.22f,.27f,.29f);camera.nearClipPlane=.02f;camera.farClipPlane=220;camera.fieldOfView=36;
            string output = Path.GetFullPath(Path.Combine(Application.dataPath,"../../Builds/ArtReview/"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(output);
            var cargo = CampArtSet.Spawn("HeavyOre",parent.transform);
            cargo.localPosition=CampMinerModel.CarryLocalPosition(ItemKind.HeavyOre);cargo.gameObject.SetActive(false);
            var ladder = CampArtSet.Spawn("Ladder",parent.transform);
            ladder.localPosition=new Vector3(0,1.075f,.15f);ladder.gameObject.SetActive(false);
            var vehicleParent=new GameObject("Actual gameplay vehicle for seat review");
            var vehicle=CampVehicleModel.Create(vehicleParent.transform,null);vehicle.Pose(true,0,0);vehicleParent.SetActive(false);
            foreach (var pose in new[]{"front","side","back","three-quarter","walk","cheeks","smoke","drink","detect","swing","carry","ladder","stunned","driver","passenger","variant-red","variant-blue","variant-green"})
            {
                cargo.gameObject.SetActive(pose=="carry");ladder.gameObject.SetActive(pose=="ladder");
                miner.ColorIndex=pose=="variant-red"?1:pose=="variant-blue"?2:pose=="variant-green"?3:0;
                miner.Action="";miner.ActionUntil=0;miner.CheeksFull=pose=="cheeks";miner.ClimbingLadderId=pose=="ladder"?"preview-ladder":"";
                miner.StunnedUntil=pose=="stunned"?10:0;miner.VehicleSeat=pose=="driver"?0:pose=="passenger"?1:-1;
                vehicleParent.SetActive(miner.VehicleSeat>=0);
                model.transform.position=miner.VehicleSeat>=0?CampMap.SeatOffset(miner.VehicleSeat):Vector3.zero;
                if(pose=="smoke"||pose=="drink"||pose=="swing"||pose=="detect"){miner.Action=pose=="swing"?"mine":pose;miner.ActionUntil=10;}
                // Each still starts settled; a prior walking sample cannot leak bob
                // and stride into unrelated smoke/drink/standing review frames.
                for(int settle=0;settle<240;settle++)model.Pose(miner,view,pose=="carry",ItemKind.HeavyOre,false,0);
                if(pose=="walk") {
                    for(int frame=0;frame<240;frame++) { model.transform.position=Vector3.forward*frame*.005f;model.Pose(miner,view,false,ItemKind.Stone,false,frame*.016f); }
                    model.transform.position=Vector3.zero;
                }
                else model.Pose(miner,view,pose=="carry",ItemKind.HeavyOre,false,0);
                float angle=pose=="side"?90:pose=="back"?180:pose=="front"?0:pose=="carry"||pose=="ladder"?65:25;
                camera.transform.position=Quaternion.Euler(0,angle,0)*new Vector3(0,0,pose=="carry"||pose=="ladder"?4.7f:3.65f)+new Vector3(0,1.02f,0);
                camera.transform.LookAt(new Vector3(0,pose=="ladder"?1.06f:.94f,pose=="carry"?.3f:0));
                if(miner.VehicleSeat>=0){camera.transform.position=new Vector3(-3.7f,2.65f,4.2f);camera.transform.LookAt(new Vector3(-.3f,1.1f,.1f));}
                Capture(camera,Path.Combine(output,pose+".png"));
            }
            parent.SetActive(false);
            vehicleParent.SetActive(false);
            foreach (string key in new[]{"Pickaxe","Detector","GloveRight","GoldOre","RawClosed","RawWindow","RawOpenGold","RawOpenIron","WoodCrate","Ladder","CabinCorner","MarketStall","PineSample","RockSample"})
            {
                var display=new GameObject("Imported asset "+key);
                var asset=CampArtSet.Spawn(key,display.transform);
                var renderers=asset.GetComponentsInChildren<Renderer>();var bounds=renderers[0].bounds;
                foreach(var renderer in renderers)bounds.Encapsulate(renderer.bounds);
                float lift=Mathf.Max(0,-bounds.min.y);asset.position+=Vector3.up*lift;bounds.center+=Vector3.up*lift;
                float radius=Mathf.Max(bounds.size.x,bounds.size.y,bounds.size.z);
                Vector3 direction=(key=="CabinCorner"||key=="MarketStall")?new Vector3(-.65f,.33f,-1):new Vector3(.5f,.28f,1);
                camera.transform.position=bounds.center+direction.normalized*Mathf.Max(.5f,radius)*2.4f;
                camera.transform.LookAt(bounds.center);Capture(camera,Path.Combine(output,"prop-"+key+".png"));
                UnityEngine.Object.DestroyImmediate(display);
            }
            File.WriteAllText(Path.Combine(output,"preview.txt"),"Actual Unity imported FBX + CampMinerModel.Pose; isolated unsaved editor scene. Not a human/network playtest.\n"+Application.unityVersion);
            File.WriteAllText(Path.GetFullPath(Path.Combine(Application.dataPath,"../../.local/art-preview-current.txt")),output);
            if (Vector3.Distance(pivot, new Vector3(-.355f,1.185f,-.006f)) > .01f)
                throw new InvalidOperationException("Imported FBX pivot axes/scale mismatch: " + pivot + "; renders=" + output);
            Debug.Log("GOLDCAMP_ART_PREVIEW_OK "+output);
        }

        private static void Capture(Camera camera,string path)
        {
            var rt=new RenderTexture(1000,1100,24,RenderTextureFormat.ARGB32);rt.antiAliasing=4;rt.Create();
            var previous=RenderTexture.active;
            try
            {
                camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
                var image=new Texture2D(rt.width,rt.height,TextureFormat.RGB24,false);
                try { image.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG()); }
                finally { UnityEngine.Object.DestroyImmediate(image); }
            }
            finally { camera.targetTexture=null;RenderTexture.active=previous;rt.Release();UnityEngine.Object.DestroyImmediate(rt); }
        }
    }
}
