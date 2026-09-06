using System;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    // A checkpoint interface; expanded in-place with the spatial gameplay HUD.
    public sealed class CampHud : MonoBehaviour
    {
        public CampSession Session;
        private string _address = "127.0.0.1", _amount = "10";
        private Vector2 _scroll;
        private GUIStyle _title, _label;
        private void OnGUI()
        {
            if (Session == null || Application.isBatchMode) return;
            _title ??= new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold };
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            GUILayout.BeginArea(new Rect(24, 24, 460, Screen.height - 48), GUI.skin.box);
            GUILayout.Label("GOLDCAMP · 淘金营地", _title);
            GUILayout.Label("永久营地 / 内部开发版", _label);
            GUILayout.Space(12);
            if (Session.View?.Self == null)
            {
                GUILayout.Label("矿工昵称");
                Session.PlayerName = GUILayout.TextField(Session.PlayerName, 24);
                if (GUILayout.Button("继续上次营地", GUILayout.Height(38))) Session.StartHost(false);
                if (GUILayout.Button("新建独立营地", GUILayout.Height(32))) Session.StartHost(true);
                GUILayout.Space(12);
                _address = GUILayout.TextField(_address, 120);
                if (GUILayout.Button("加入房主地址", GUILayout.Height(32))) Session.Join(_address);
                GUILayout.Label("此入口使用直接网络连接。Steam 邀请入口正在开发。", _label);
            }
            else
            {
                var view = Session.View;
                GUILayout.Label(view.Name + "\n金币 " + view.Self.Balance + " · 工具 " + view.Self.ToolLevel, _label);
                GUILayout.Label("最近保存 " + view.SavedUtc, _label);
                _scroll = GUILayout.BeginScrollView(_scroll);
                foreach (var item in view.Items)
                {
                    if (item.Place == ItemPlace.Ground)
                    {
                        if (GUILayout.Button("拿起 " + item.Kind)) Command("pickup", item);
                    }
                    else if (item.OwnerId == view.Self.Id)
                    {
                        GUILayout.Label(item.Kind + " / " + item.Place);
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button("放下")) Command("drop", item);
                        if (GUILayout.Button("出售 · " + item.SaleQuote)) Command("sell", item);
                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.Label("转账金额");
                _amount = GUILayout.TextField(_amount, 10);
                foreach (var p in view.Players.Where(p => p.Id != view.Self.Id))
                    if (GUILayout.Button("转给 " + p.Name) && long.TryParse(_amount, out long amount))
                        Session.SendCommand(new CampCommand { RequestId = Guid.NewGuid().ToString("N"), Action = "transfer", OtherPlayerId = p.Id, Amount = amount });
                GUILayout.EndScrollView();
                if (GUILayout.Button(Session.Authority != null ? "保存并回到主菜单" : "离开营地", GUILayout.Height(32))) Session.EndSession(false);
            }
            GUILayout.Label(Session.Feedback, _label);
            if (GUILayout.Button("保存并退出", GUILayout.Height(32))) Session.EndSession(true);
            GUILayout.EndArea();
        }
        private void Command(string action, VisibleItem item) => Session.SendCommand(new CampCommand
        { RequestId = Guid.NewGuid().ToString("N"), Action = action, TargetId = item.Id, ExpectedVersion = item.Version, Direction = Vector3.forward });
    }
}
