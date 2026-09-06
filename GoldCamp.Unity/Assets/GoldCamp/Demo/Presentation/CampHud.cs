using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed class CampHud : MonoBehaviour
    {
        public CampSession Session;
        public CampControls Controls;
        public string Panel = "menu";
        private string _address = "127.0.0.1", _amount = "10";
        private Vector2 _scroll;
        private Font _font;
        private GUIStyle _title, _label;
        private void OnGUI()
        {
            if (Session == null || Application.isBatchMode) return;
            if (_font == null) _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 16);
            GUI.skin.font = _font;
            GUI.skin.label.fontSize = 16; GUI.skin.button.fontSize = 16; GUI.skin.textField.fontSize = 16;
            _title ??= new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            var view = Session.View;
            if (view?.Self != null && string.IsNullOrEmpty(Panel))
            {
                GUI.Box(new Rect(18, 18, 350, 105), "");
                GUI.Label(new Rect(30, 25, 330, 90), view.Name + "\n金币 " + view.Self.Balance + " · 耐久 " + Mathf.CeilToInt(view.Self.Durability) + "\n梯子 " + view.Self.LadderStock + " · " + view.Players.Count(p => p.Online) + " / 4 人", _label);
                GUI.Label(new Rect(Screen.width / 2f - 5, Screen.height / 2f - 12, 20, 24), "+");
                GUI.Box(new Rect(18, Screen.height - 106, Screen.width - 36, 90), "");
                GUI.Label(new Rect(30, Screen.height - 100, Screen.width - 60, 82), Controls.Hint + "\n" + Session.Feedback + "\n1 矿镐 / 2 探测 · E 互动 · Q 抛出 · F 收袋 · L 放梯 · Tab 物品 · Esc 菜单", _label);
                return;
            }
            GUILayout.BeginArea(new Rect(Mathf.Max(24, (Screen.width - 570) / 2f), 24, 570, Screen.height - 48), GUI.skin.box);
            GUILayout.Label("GOLDCAMP · 淘金营地", _title);
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (view?.Self == null)
            {
                GUILayout.Label("永久营地 · 退出后继续", _label);
                Session.PlayerName = GUILayout.TextField(Session.PlayerName, 24);
                if (Session.Authority != null)
                { if (Button("保存并结束当前会话")) Session.EndSession(false); }
                else
                {
                    if (Button("继续上次营地")) Session.StartHost(false);
                    if (Button("新建独立营地")) Session.StartHost(true);
                    GUILayout.Space(12); GUILayout.Label("房主地址（同网络可填房主 IPv4）");
                    _address = GUILayout.TextField(_address, 120);
                    if (Button("加入营地")) Session.Join(_address);
                }
                GUILayout.Label("Steam 邀请尚待账号条件接入；当前为开发检查点。", _label);
            }
            else
            {
                GUILayout.Label("金币 " + view.Self.Balance + " · 耐久 " + Mathf.CeilToInt(view.Self.Durability) + " · 梯子 " + view.Self.LadderStock, _label);
                if (Panel == "sale" || Panel == "bag" || Panel == "cabinet") Inventory(view);
                if (Panel == "repair")
                {
                    if (Button("金币维修 · " + view.Rules.RepairPrice)) Controls.Send("repair");
                    if (Button("铁料维修 · " + view.Rules.RepairIronCost)) Controls.Send("repair_material", "iron");
                    if (Button("石料维修 · " + view.Rules.RepairStoneCost)) Controls.Send("repair_material", "stone");
                }
                if (Panel == "bag")
                {
                    if (Button("制作梯子 · " + view.Rules.LadderStoneCost + " 石料")) Controls.Send("craft_ladder");
                    if (Button("在瞄准空格放置梯子")) Controls.PlaceLadder();
                    var assisted = view.Items.Find(i => i.AssistantId == view.Self.Id);
                    if (assisted != null && Button("退出协助搬运")) Controls.Send("leave_assist", assisted.Id, assisted.Version);
                    foreach (var ladder in view.Ladders.Where(l => Vector3.Distance(CampMap.LadderCenter(l, view.Rules), view.Players.Find(p => p.Id == view.Self.Id).Position) < 3))
                        if (Button("回收附近梯段 · 深度 " + (ladder.Depth + 1) * view.Rules.CellSize + "m")) Controls.Send("remove_ladder", ladder.Id, ladder.Version);
                    Transfers(view);
                }
                if (Panel == "menu")
                {
                    GUILayout.Label("菜单只停用你的输入，营地继续运行。", _label);
                    GUILayout.Label("鼠标灵敏度 " + Controls.Sensitivity.ToString("F1"));
                    Controls.Sensitivity = GUILayout.HorizontalSlider(Controls.Sensitivity, .3f, 6f);
                    GUILayout.Label("视野 " + Mathf.RoundToInt(Controls.FieldOfView));
                    Controls.FieldOfView = GUILayout.HorizontalSlider(Controls.FieldOfView, 60, 100);
                    if (Button(view.Self.EscapeAt > 0 ? "取消返回营地" : "返回营地 · 4 秒引导，战利品留原地")) Controls.Send(view.Self.EscapeAt > 0 ? "cancel_escape" : "escape");
                    GUILayout.Label("最近保存 " + view.SavedUtc, _label);
                    if (Button(Session.Authority != null ? "保存并结束会话" : "离开营地（随身货物留包）")) Session.EndSession(false);
                }
                if (Button("返回游戏")) { Controls.SaveSettings(); Panel = ""; }
            }
            GUILayout.EndScrollView(); GUILayout.Label(Session.Feedback, _label);
            if (Button("保存并退出游戏")) Session.EndSession(true);
            GUILayout.EndArea();
        }
        private void Inventory(CampView view)
        {
            foreach (var item in view.Items.Where(i => i.OwnerId == view.Self.Id))
            {
                GUILayout.Label(Name(item.Kind) + " × " + item.Quantity + " · " + Place(item.Place));
                GUILayout.BeginHorizontal();
                if (Panel == "sale" && (item.Place == ItemPlace.Hand || item.Place == ItemPlace.Pocket) && GUILayout.Button("出售 · " + item.SaleQuote)) Controls.Send("sell", item.Id, item.Version);
                if (item.Place == ItemPlace.Hand && GUILayout.Button("收入口袋")) Controls.Send("pocket", item.Id, item.Version);
                if (item.Place == ItemPlace.Pocket && GUILayout.Button("取到手中")) Controls.Send("unpocket", item.Id, item.Version);
                if (Panel == "cabinet")
                {
                    if (item.Place == ItemPlace.Cabinet) { if (GUILayout.Button("取出")) Controls.Send("cabinet_take", item.Id, item.Version); }
                    else if (GUILayout.Button("放入个人柜")) Controls.Send("cabinet_store", item.Id, item.Version);
                }
                GUILayout.EndHorizontal();
            }
        }
        private void Transfers(CampView view)
        {
            GUILayout.Label("转账金额（确认即支付）"); _amount = GUILayout.TextField(_amount, 10);
            foreach (var player in view.Players.Where(p => p.Id != view.Self.Id))
                if (Button("确认转给 " + player.Name) && long.TryParse(_amount, out long amount)) Controls.Send("transfer", amount: amount, other: player.Id);
        }
        private static bool Button(string text) => GUILayout.Button(text, GUILayout.Height(34));
        private static string Name(ItemKind kind) => kind == ItemKind.Stone ? "石料" : kind == ItemKind.Iron ? "铁料" : kind == ItemKind.Gold ? "金粒" : kind == ItemKind.HeavyOre ? "沉重大矿" : "未知原石";
        private static string Place(ItemPlace place) => place == ItemPlace.Hand ? "手持" : place == ItemPlace.Pocket ? "口袋" : place == ItemPlace.Cabinet ? "个人柜" : "嘴藏";
        private void OnDestroy() { if (_font != null) Destroy(_font); }
    }
}
