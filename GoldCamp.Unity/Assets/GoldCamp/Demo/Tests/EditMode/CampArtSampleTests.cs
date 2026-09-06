using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    // Real imported geometry: catches axis/UV errors that passing gameplay tests cannot see.
    public sealed class CampArtSampleTests
    {
        private static readonly string[] Required = {
            "BeanMiner","Pickaxe","Detector","GloveRight","GloveLeft","GoldOre","StoneOre","IronOre",
            "RawClosed","RawWindow","RawOpenGold","RawOpenIron","RawOpenStone","HeavyOre","WoodCrate",
            "Ladder","CabinCorner","MarketStall","PineSample","RockSample" };
        private GameObject _root;
        [TearDown] public void Clean() { if (_root != null) UnityEngine.Object.DestroyImmediate(_root); }
        private CampArtSet Set => Resources.Load<CampArtSet>("GoldCampArtSample");

        [TestCase("stone","RawOpenStone")] [TestCase("iron","RawOpenIron")]
        [TestCase("gold","RawOpenGold")] [TestCase("rich_gold","RawOpenGold")]
        public void RawAppearanceOnlyRevealsThePublicCoreAfterFullOpening(string core,string opened)
        {
            Assert.That(CampArtSet.RawModelKey(0,core),Is.EqualTo("RawClosed"));
            Assert.That(CampArtSet.RawModelKey(1,core),Is.EqualTo("RawWindow"));
            Assert.That(CampArtSet.RawModelKey(2,core),Is.EqualTo(opened));
        }

        [TestCase(true,90f)] [TestCase(true,180f)] [TestCase(false,90f)] [TestCase(false,180f)]
        public void ClimbingCarrySharesLadderFacingOnHostAndPeerEvenWhenLookingAway(bool host,float yaw)
        {
            _root=new GameObject("isolated world presentation");var world=_root.AddComponent<CampWorld>();
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var feet=new Vector3(4,-2,8);
            if(host) {
                var players=(Dictionary<string,CampPlayer>)typeof(CampWorld).GetField("_hostPlayers",flags).GetValue(world);
                players.Add("miner",new CampPlayer{Id="miner",Position=feet,Yaw=yaw,ClimbingLadderId="ladder"});
            } else {
                var players=(Dictionary<string,PublicMiner>)typeof(CampWorld).GetField("_clientPlayers",flags).GetValue(world);
                players.Add("miner",new PublicMiner{Id="miner",Position=feet,Yaw=yaw,ClimbingLadderId="ladder"});
            }
            object[] arguments={"miner",ItemKind.HeavyOre,Vector3.zero,host,Vector3.zero,Quaternion.identity};
            typeof(CampWorld).GetMethod("GetHoldPose",flags).Invoke(world,arguments);
            Assert.That(Vector3.Distance((Vector3)arguments[4],feet+new Vector3(0,1,.71f)),Is.LessThan(.002f));
            Assert.That(Quaternion.Angle((Quaternion)arguments[5],Quaternion.identity),Is.LessThan(.1f));
        }

        [Test] public void SampleIncludesEveryRequiredModelAndFourSharedPalettes()
        {
            Assert.That(Set, Is.Not.Null);
            Assert.That(Set.Models.Select(e=>e.Key), Is.EquivalentTo(Required));
            Assert.That(Set.MinerVariants.Length, Is.EqualTo(4));
            foreach (var material in Set.MinerVariants) Assert.That(material.GetTexture("_BaseMap"), Is.Not.Null);
        }

        [TestCaseSource(nameof(Required))]
        public void ImportedPrefabHasOnlyVisualComponentsAndOneAtlasPerRenderer(string key)
        {
            var prefab=Set.Find(key);Assert.That(prefab,Is.Not.Null,key);
            foreach (var c in prefab.GetComponentsInChildren<Component>(true))
                Assert.That(c is Transform || c is MeshFilter || c is MeshRenderer,Is.True,key+" / "+c.GetType());
            foreach(var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true)) {
                Assert.That(renderer.sharedMaterials.Length,Is.EqualTo(1),key);
                Assert.That(renderer.sharedMaterial,Is.SameAs(Set.MinerVariants[0]));
            }
            int triangles=0;
            foreach(var filter in prefab.GetComponentsInChildren<MeshFilter>(true)) {
                Assert.That(filter.sharedMesh,Is.Not.Null);
                using var data=MeshUtility.AcquireReadOnlyMeshData(filter.sharedMesh);
                using var uv=new NativeArray<Vector2>(data[0].vertexCount,Allocator.Temp);
                data[0].GetUVs(0,uv);
                // Every vertex samples a swatch centre, never a primitive's unmodified spherical UV0.
                foreach(var v in uv) {
                    Assert.That(Mathf.Abs(v.x*8-.5f-Mathf.Round(v.x*8-.5f)),Is.LessThan(.002f),key+" UV0.x");
                    Assert.That(Mathf.Abs(v.y*8-.5f-Mathf.Round(v.y*8-.5f)),Is.LessThan(.002f),key+" UV0.y");
                }
                for(int i=0;i<data[0].subMeshCount;i++)triangles+=data[0].GetSubMesh(i).indexCount/3;
            }
            Assert.That(triangles,Is.GreaterThan(10));
            Assert.That(triangles,Is.LessThan(key=="BeanMiner"?40000:20000),key+" triangle budget");
        }

        [Test] public void ImportedRigidJointsKeepUnityLocalAxesAndMetreScale()
        {
            _root=UnityEngine.Object.Instantiate(Set.Find("BeanMiner"));
            Assert.That(Quaternion.Angle(_root.transform.localRotation,Quaternion.identity),Is.LessThan(.1f));
            foreach(var side in new[]{"Left","Right"}) {
                float sign=side=="Left"?-1:1;
                CheckNode(side+"Shoulder",new Vector3(sign*.355f,1.185f,-.006f));
                CheckNode(side+"Elbow",new Vector3(0,-.25f,0));CheckNode(side+"Hand",new Vector3(0,-.25f,0));
                CheckNode(side+"Hip",new Vector3(sign*.171f,.52f,0));CheckNode(side+"Knee",new Vector3(0,-.21f,0));
            }
            CheckNode("Mouth",new Vector3(0,1.318f,.287f));
        }
        private void CheckNode(string name,Vector3 expected) {
            var t=CampArtSet.Node(_root.transform,name);
            Assert.That(Vector3.Distance(t.localPosition,expected),Is.LessThan(.002f),name);
            Assert.That(Quaternion.Angle(t.localRotation,Quaternion.identity),Is.LessThan(.1f),name);
        }

        [TestCase("GoldOre",.55f,.45f,.5f)] [TestCase("StoneOre",.55f,.45f,.5f)]
        [TestCase("IronOre",.55f,.45f,.5f)] [TestCase("RawClosed",.55f,.45f,.5f)]
        [TestCase("RawWindow",.55f,.45f,.5f)] [TestCase("RawOpenGold",.55f,.45f,.5f)]
        [TestCase("RawOpenIron",.55f,.45f,.5f)] [TestCase("RawOpenStone",.55f,.45f,.5f)]
        [TestCase("HeavyOre",1.1f,.85f,.95f)]
        public void ActualOreMeshFitsExistingPhysicsEnvelope(string key,float x,float y,float z)
        {
            _root=UnityEngine.Object.Instantiate(Set.Find(key));
            var points=Vertices(_root.transform);var size=new Vector3(x,y,z);
            var bounds=new Bounds(points[0],Vector3.zero);foreach(var v in points)bounds.Encapsulate(v);
            for(int axis=0;axis<3;axis++) {
                Assert.That(bounds.min[axis],Is.GreaterThanOrEqualTo(-size[axis]/2-.005f),key);
                Assert.That(bounds.max[axis],Is.LessThanOrEqualTo(size[axis]/2+.005f),key);
                Assert.That(bounds.size[axis],Is.GreaterThan(size[axis]*.90f),key);
            }
        }

        [Test] public void SwingCycleKeepsBothHandsOnTheImportedHandleWithoutMutatingState()
        {
            var model=CreateMiner(out var miner,out var view);miner.Action="mine";miner.ActionUntil=100;
            string before=JsonUtility.ToJson(miner)+JsonUtility.ToJson(view);
            var left=CampArtSet.Node(model.transform,"LeftHand");
            var right=CampArtSet.Node(model.transform,"RightHand");
            var pick=CampArtSet.Node(model.transform,"Pickaxe");
            for(int step=0;step<48;step++) {
                model.Pose(miner,view,false,ItemKind.Stone,false,step/48f*Mathf.PI*2/11f);
                Assert.That(pick.gameObject.activeInHierarchy,Is.True);
                Assert.That(Vector3.Distance(left.position,pick.TransformPoint(new Vector3(0,-.14f,0))),Is.LessThan(.025f),"left grip phase "+step);
                Assert.That(Vector3.Distance(right.position,pick.position),Is.LessThan(.05f),"right grip");
                // Blade centre must stay ahead of the body when below eye level.
                var tip=model.transform.InverseTransformPoint(pick.TransformPoint(new Vector3(0,.479f,0)));
                if(tip.y<1.5f)Assert.That(tip.z,Is.GreaterThan(.30f),"blade enters body phase "+step);
            }
            Assert.That(JsonUtility.ToJson(miner)+JsonUtility.ToJson(view),Is.EqualTo(before));
            Assert.That(model.GetComponentsInChildren<Collider>(true),Is.Empty);
        }

        [TestCase("smoke")] [TestCase("drink")]
        public void SocialPropMeetsTheNewMouthAnchor(string action)
        {
            var model=CreateMiner(out var miner,out var view);miner.Action=action;miner.ActionUntil=100;
            model.Pose(miner,view,false,ItemKind.Stone,false,0);
            var mouth=CampArtSet.Node(model.transform,"Mouth");
            var prop=CampArtSet.Node(model.transform,action=="smoke"?"Smoking pipe":"Drink bottle");
            Vector3 tip=prop.TransformPoint(action=="smoke"?new Vector3(.022f,-.014f,-.0125f):new Vector3(0,.2375f,0));
            Assert.That(Vector3.Distance(tip,mouth.position),Is.LessThan(.05f));
        }

        private CampMinerModel CreateMiner(out PublicMiner miner,out CampView view) {
            _root=new GameObject("isolated art regression");
            var model=CampMinerModel.Create(_root.transform,null);
            miner=new PublicMiner{Id="art-test",Name="art-test",Online=true,VehicleSeat=-1};
            view=new CampView{OnlineSeconds=1};return model;
        }
        private static List<Vector3> Vertices(Transform root) {
            var result=new List<Vector3>();
            foreach(var mf in root.GetComponentsInChildren<MeshFilter>(true)) {
                using var data=MeshUtility.AcquireReadOnlyMeshData(mf.sharedMesh);
                using var vertices=new NativeArray<Vector3>(data[0].vertexCount,Allocator.Temp);
                data[0].GetVertices(vertices);
                foreach(var v in vertices)result.Add(root.InverseTransformPoint(mf.transform.TransformPoint(v)));
            }
            return result;
        }
    }
}
