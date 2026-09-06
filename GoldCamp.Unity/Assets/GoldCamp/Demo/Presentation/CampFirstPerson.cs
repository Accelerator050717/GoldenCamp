using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed class CampFirstPerson : MonoBehaviour
    {
        public CampControls Controls;
        public CampSession Session;
        public Material[] Palette;
        private Transform _root, _right, _left, _pick, _detector, _bottle, _pipe;
        private void Start()
        {
            _root = new GameObject("First person hands").transform; _root.SetParent(Controls.Camera.transform,false);
            _right = new GameObject("Right grip").transform; _right.SetParent(_root,false);
            _left = new GameObject("Left grip").transform; _left.SetParent(_root,false);
            Piece(_right,PrimitiveType.Sphere,Vector3.zero,new Vector3(.16f,.18f,.22f),6);
            Piece(_left,PrimitiveType.Sphere,Vector3.zero,new Vector3(.16f,.18f,.22f),6);
            _pick = new GameObject("View pickaxe").transform; _pick.SetParent(_right,false);
            Piece(_pick,PrimitiveType.Cylinder,new Vector3(0,.1f,0),new Vector3(.045f,.29f,.045f),4);
            Piece(_pick,PrimitiveType.Cube,new Vector3(0,.38f,0),new Vector3(.43f,.08f,.06f),2);
            _detector = new GameObject("View detector").transform; _detector.SetParent(_right,false);
            Piece(_detector,PrimitiveType.Cylinder,new Vector3(0,0,.19f),new Vector3(.035f,.24f,.035f),2,Quaternion.Euler(90,0,0));
            Piece(_detector,PrimitiveType.Cylinder,new Vector3(0,-.03f,.43f),new Vector3(.28f,.015f,.28f),6);
            Piece(_detector,PrimitiveType.Cube,new Vector3(0,.08f,.13f),new Vector3(.11f,.1f,.15f),5);
            _bottle = new GameObject("View bottle").transform; _bottle.SetParent(_right,false);
            Piece(_bottle,PrimitiveType.Cylinder,new Vector3(0,.04f,0),new Vector3(.09f,.13f,.09f),5);
            Piece(_bottle,PrimitiveType.Cylinder,new Vector3(0,.2f,0),new Vector3(.045f,.055f,.045f),5);
            _pipe = new GameObject("View pipe").transform; _pipe.SetParent(_right,false);
            Piece(_pipe,PrimitiveType.Cylinder,new Vector3(0,.025f,.05f),new Vector3(.06f,.05f,.06f),4);
            Piece(_pipe,PrimitiveType.Cylinder,new Vector3(0,.03f,-.035f),new Vector3(.018f,.09f,.018f),4,Quaternion.Euler(90,0,0));
        }
        private void LateUpdate()
        {
            var view=Session.View;var self=view?.Self==null?null:view.Players.Find(p=>p.Id==view.Self.Id);
            if(_root==null)return; _root.gameObject.SetActive(self!=null&&!Application.isBatchMode);
            if(self==null)return;
            var hand=view.Items.Find(i=>i.OwnerId==self.Id&&i.Place==ItemPlace.Hand);
            bool active=self.ActionUntil>view.OnlineSeconds,smoke=active&&self.Action=="smoke",drink=active&&self.Action=="drink";
            bool swing=active&&(self.Action=="mine"||self.Action=="attack");
            _right.localPosition=hand!=null?new Vector3(.3f,-.47f,.75f):new Vector3(.4f,-.36f,.7f);
            _left.localPosition=new Vector3(-.3f,-.47f,.75f);_left.gameObject.SetActive(hand!=null);
            _right.localRotation=Quaternion.Euler(swing?-25+Mathf.Sin(Time.unscaledTime*13)*48:12,0,-12);
            if(smoke||drink){_right.localPosition=new Vector3(.14f,-.24f,.37f);_right.localRotation=Quaternion.Euler(drink?-55:10,0,-15);}
            _pick.gameObject.SetActive(hand==null&&!smoke&&!drink&&Controls.CurrentTool==1);
            _detector.gameObject.SetActive(hand==null&&!smoke&&!drink&&Controls.CurrentTool==2);
            _bottle.gameObject.SetActive(drink);_pipe.gameObject.SetActive(smoke);
        }
        private void Piece(Transform parent,PrimitiveType type,Vector3 position,Vector3 size,int material,Quaternion? rotation=null)
        {
            var go=GameObject.CreatePrimitive(type);go.layer=2;go.transform.SetParent(parent,false);go.transform.localPosition=position;
            go.transform.localScale=size;go.transform.localRotation=rotation??Quaternion.identity;
            var collider=go.GetComponent<Collider>();collider.enabled=false;Destroy(collider);go.GetComponent<Renderer>().sharedMaterial=Palette[material];
            go.GetComponent<Renderer>().shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        private void OnDestroy(){if(_root!=null)Destroy(_root.gameObject);}
    }
}
