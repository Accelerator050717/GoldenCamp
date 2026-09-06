using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed class CampHud : MonoBehaviour
    {
        public CampSession Session;
        public CampControls Controls;
        public Font InterfaceFont;
        public string LootContainer = "";
        public string Panel = "menu";
        private string _address = "127.0.0.1", _amount = "10";
        private string _stake = "10", _opponent = "", _stone = "";
        private string _roadCoins = "50", _roadIron = "1";
        private Vector2 _scroll;
        private Font _font;
        private GUIStyle _title, _label;
        private void OnGUI()
        {
            if (Session == null || Application.isBatchMode) return;
            if (_font == null) _font = InterfaceFont != null ? InterfaceFont : Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 16);
            GUI.skin.font = _font;
            GUI.skin.label.fontSize = 20; GUI.skin.button.fontSize = 20; GUI.skin.textField.fontSize = 20;
            _title ??= new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
            var view = Session.View;
            if (view?.Self != null && string.IsNullOrEmpty(Panel))
            {
                var self = view.Players.Find(p=>p.Id==view.Self.Id);
                var hand = view.Items.Find(i=>i.OwnerId==view.Self.Id&&i.Place==ItemPlace.Hand);
                GUI.Box(new Rect(18, 18, 380, 147), "");
                GUI.Label(new Rect(30, 25, 360, 132), view.Name + "\n金币 " + view.Self.Balance + " · 耐久 " + Mathf.CeilToInt(view.Self.Durability) + " · 梯子 " + view.Self.LadderStock
                    + "\n手中：" + (hand!=null?Name(hand.Kind):"空") + " · 嘴槽：" + (self.CheeksFull?"有矿":"空")
                    + "\n" + view.Players.Count(p=>p.Online) + " / 4 人 · " + (view.PvpEnabled?"可击晕抢货":"和平营地")
                    + (self.StunnedUntil>view.OnlineSeconds?"\n短晕："+Mathf.CeilToInt((float)(self.StunnedUntil-view.OnlineSeconds))+" 秒":""), _label);
                GUI.Label(new Rect(Screen.width / 2f - 5, Screen.height / 2f - 12, 20, 24), "+");
                var voicePanel = GetComponent<CampVoicePanel>();
                if (voicePanel != null)
                {
                    string voiceText = voicePanel.StatusText();
                    float voiceHeight = _label.CalcHeight(new GUIContent(voiceText), 280) + 12;
                    GUI.Box(new Rect(Screen.width - 310, 18, 292, voiceHeight), "");
                    GUI.Label(new Rect(Screen.width - 304, 24, 280, voiceHeight), voiceText, _label);
                }
                string footer = Controls.Hint + "\n" + Session.Feedback + "\n1/2 工具 · E 互动 · Q 抛 · B 含/吐 · L 梯子 · Tab 物品/表演 · Esc 菜单";
                float footerHeight = _label.CalcHeight(new GUIContent(footer), Screen.width - 60) + 16;
                GUI.Box(new Rect(18, Screen.height - footerHeight - 18, Screen.width - 36, footerHeight), "");
                GUI.Label(new Rect(30, Screen.height - footerHeight - 10, Screen.width - 60, footerHeight - 16), footer, _label);
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
                Settings();
            }
            else
            {
                GUILayout.Label("金币 " + view.Self.Balance + " · 耐久 " + Mathf.CeilToInt(view.Self.Durability) + " · 梯子 " + view.Self.LadderStock, _label);
                if (Panel == "sale" || Panel == "bag" || Panel == "cabinet" || Panel == "cutter") Inventory(view);
                if (Panel == "wager") Wager(view);
                if (Panel == "road") Road(view);
                if (Panel == "vehicle") Vehicle(view);
                if (Panel == "bagloot")
                {
                    GUILayout.Label("公共掉落包 · 先到先取，个人柜与余额不在这里",_label);
                    foreach(var item in view.Items.Where(i=>i.Place==ItemPlace.Ground&&i.ContainerId==LootContainer))
                        if(Button("拿取 "+Name(item.Kind)+" × "+item.Quantity))Controls.Send("pickup",item.Id,item.Version);
                }
                if (Panel == "repair")
                {
                    GUILayout.Label("个人矿镐 " + view.Self.ToolLevel + " 级 · 探测附件 " + view.Self.DetectorLevel + " 级", _label);
                    if(view.Self.ToolLevel<view.Rules.MaxToolLevel && Button("升级矿镐并修满 · " + view.Rules.UpgradePrice)) Controls.Send("upgrade_tool",version:view.Self.ToolLevel);
                    if(view.Self.DetectorLevel<view.Rules.MaxDetectorLevel && Button("升级探测附件 · " + view.Rules.DetectorUpgradePrice)) Controls.Send("upgrade_detector",version:view.Self.DetectorLevel);
                    if (Button("金币维修 · " + view.Rules.RepairPrice)) Controls.Send("repair");
                    if (Button("铁料维修 · " + view.Rules.RepairIronCost)) Controls.Send("repair_material", "iron");
                    if (Button("石料维修 · " + view.Rules.RepairStoneCost)) Controls.Send("repair_material", "stone");
                }
                if (Panel == "bag")
                {
                    GUILayout.Label("动作 · 不消耗矿物，X 随时取消", _label);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("抽烟斗")) Controls.Send("smoke");
                    if (GUILayout.Button("举瓶喝一口")) Controls.Send("drink");
                    if (GUILayout.Button("取消动作")) Controls.Send("cancel_action");
                    GUILayout.EndHorizontal();
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
                    GUILayout.Label(view.RoadUnlocked ? "进阶道路已通 · 可步行或乘车前往北侧新坑" : "下一步：个人升级 → 自愿修车开路 → 进阶采矿", _label);
                    if(view.Vehicle!=null && view.Vehicle.Seats.Contains(view.Self.Id) && Button("停车并安全下车"))Controls.Send("vehicle_exit",view.Vehicle.Id,view.Vehicle.Version);
                    if (Session.Authority != null && Button(view.PvpEnabled ? "切换为和平营地（关闭击晕/搜袋）" : "开启击晕与搜袋")) Controls.Send("pvp", amount:view.PvpEnabled?0:1);
                    GUILayout.Label("鼠标灵敏度 " + Controls.Sensitivity.ToString("F1"));
                    Controls.Sensitivity = GUILayout.HorizontalSlider(Controls.Sensitivity, .3f, 6f);
                    GUILayout.Label("视野 " + Mathf.RoundToInt(Controls.FieldOfView));
                    Controls.FieldOfView = GUILayout.HorizontalSlider(Controls.FieldOfView, 60, 100);
                    Settings();
                    if (Button(view.Self.EscapeAt > 0 ? "取消返回营地" : "返回营地 · 4 秒引导，战利品留原地")) Controls.Send(view.Self.EscapeAt > 0 ? "cancel_escape" : "escape");
                    GUILayout.Label("最近保存 " + view.SavedUtc, _label);
                    if (Button(Session.Authority != null ? "保存并结束会话" : "离开营地（随身货物留包）")) Session.EndSession(false);
                }
                if (Button("返回游戏")) { Controls.SaveSettings(); Panel = ""; }
            }
            GUILayout.EndScrollView(); GUILayout.Label(Session.Feedback, _label);
            if (Button(view?.Self != null && Session.Authority == null ? "离开并退出（随身货物留包）" : "保存并退出游戏")) Session.EndSession(true);
            GUILayout.EndArea();
        }
        private void Road(CampView view)
        {
            GUILayout.Label("公共旧车与进阶道路", _title);
            GUILayout.Label("金币 " + view.RoadCoinsPaid + " / " + view.RoadRequiredCoins + " · 铁料 " + view.RoadIronPaid + " / " + view.RoadRequiredIron, _label);
            GUILayout.Label("分次自愿贡献，只使用自己的余额和手中/口袋铁料；达到目标后所有人可用。", _label);
            if(!view.RoadUnlocked)
            {
                GUILayout.Label("本次贡献金币"); _roadCoins=GUILayout.TextField(_roadCoins,10);
                if(long.TryParse(_roadCoins,out long coins) && Button("确认贡献 " + coins + " 金币"))Controls.Send("contribute_coins",version:view.RoadVersion,amount:coins);
                GUILayout.Label("本次贡献铁料"); _roadIron=GUILayout.TextField(_roadIron,3);
                if(long.TryParse(_roadIron,out long iron) && Button("确认交出 " + iron + " 铁料"))Controls.Send("contribute_iron",version:view.RoadVersion,amount:iron);
            }
            else
            {
                GUILayout.Label("已修好 · 公共四座车在路旁；没上车的人可步行前往。",_label);
                if(view.Vehicle!=null && Button("回收卡住的空车 · 货物留在旧处"))Controls.Send("vehicle_recover",view.Vehicle.Id,view.Vehicle.Version);
            }
            foreach(var entry in view.Contributions)
                GUILayout.Label((view.Players.Find(p=>p.Id==entry.PlayerId)?.Name??"矿工") + "：" + entry.Coins + " 金币 / " + entry.Iron + " 铁料",_label);
        }
        private void Settings()
        {
            var sound = GetComponent<CampSound>();
            if (sound != null)
            {
                GUILayout.Label("音效音量 " + Mathf.RoundToInt(sound.Effects * 100) + "%");
                sound.Effects = GUILayout.HorizontalSlider(sound.Effects, 0, 1);
                GUILayout.Label("环境音量 " + Mathf.RoundToInt(sound.Ambience * 100) + "%");
                sound.Ambience = GUILayout.HorizontalSlider(sound.Ambience, 0, 1);
            }
            GetComponent<CampVoicePanel>()?.Settings();
            GetComponent<CampDisplaySettings>()?.Settings();
            if (Button("保存当前设置")) Controls.SaveSettings();
        }
        private void Vehicle(CampView view)
        {
            var car=view.Vehicle;if(car==null)return;
            GUILayout.Label("营地运输车 · 四座公共货架",_title);
            if(!view.RoadUnlocked)
            {GUILayout.Label("旧车损坏，请到旁边维护台分次贡献修复。",_label);if(Button("查看公共修复"))Panel="road";return;}
            GUILayout.Label("W/S 油门与倒车，A/D 转向；E 停车下车。工具留身，车货属于公共货架。",_label);
            int ownSeat=car.Seats.IndexOf(view.Self.Id);
            if(ownSeat>=0)
            {if(Button("停车下车")){Controls.Send("vehicle_exit",car.Id,car.Version);Panel="";}}
            else for(int seat=0;seat<car.Seats.Count;seat++)
            {
                string title=seat==0?"驾驶座":"乘客座 " + seat;
                if(string.IsNullOrEmpty(car.Seats[seat]))
                {if(Button("坐入 " + title)){Controls.Send("vehicle_enter",car.Id,car.Version,seat);Panel="";}}
                else GUILayout.Label(title + " · " + (view.Players.Find(p=>p.Id==car.Seats[seat])?.Name??"矿工"),_label);
            }
            GUILayout.Space(10);GUILayout.Label("停车后任何靠近的人都可装卸；私人柜仍只归本人。",_label);
            for(int slot=0;slot<car.CargoSlots;slot++)
            {
                var cargo=view.Items.Find(i=>i.Place==ItemPlace.Vehicle&&i.ContainerId==CampMap.VehicleCargoContainer(slot));
                if(cargo==null)GUILayout.Label("货位 " + (slot+1) + " · 空",_label);
                else if(Button("货位 " + (slot+1) + " · 取下 " + Name(cargo.Kind)))Controls.Send("vehicle_unload",cargo.Id,cargo.Version);
            }
            int free=Enumerable.Range(0,car.CargoSlots).Where(slot=>!view.Items.Any(i=>i.Place==ItemPlace.Vehicle&&i.ContainerId==CampMap.VehicleCargoContainer(slot))).DefaultIfEmpty(-1).First();
            if(free>=0 && ownSeat<0)foreach(var cargo in view.Items.Where(i=>i.OwnerId==view.Self.Id&&(i.Place==ItemPlace.Hand||i.Place==ItemPlace.Pocket)))
                if(Button("装入货位 " + (free+1) + "：" + Name(cargo.Kind)))Controls.Send("vehicle_load",cargo.Id,cargo.Version,free);
        }
        private void Inventory(CampView view)
        {
            foreach (var item in view.Items.Where(i => i.OwnerId == view.Self.Id))
            {
                GUILayout.Label(Name(item.Kind) + " × " + item.Quantity + " · " + Place(item.Place) + (item.Kind==ItemKind.RawStone?" · "+(item.RevealStage==0?"未开":item.RevealStage==1?"已开窗":"全开 · "+item.PublicCore):""));
                if (!string.IsNullOrEmpty(item.OwnClue)) GUILayout.Label(item.OwnClue, _label);
                if (item.WindowBand>0) GUILayout.Label("公开窗口："+(item.WindowBand==1?"浅色石纹":item.WindowBand==2?"零星矿脉":"明亮矿脉")+"（不等于全开价格）", _label);
                GUILayout.BeginHorizontal();
                if (Panel == "sale" && (item.Place == ItemPlace.Hand || item.Place == ItemPlace.Pocket) && GUILayout.Button("出售 · " + item.SaleQuote)) Controls.Send("sell", item.Id, item.Version);
                if (item.Place == ItemPlace.Hand && GUILayout.Button("收入口袋")) Controls.Send("pocket", item.Id, item.Version);
                if (item.Place == ItemPlace.Pocket && GUILayout.Button("取到手中")) Controls.Send("unpocket", item.Id, item.Version);
                if ((item.Place==ItemPlace.Hand||item.Place==ItemPlace.Pocket)&&item.Kind==ItemKind.Gold&&GUILayout.Button("藏嘴")) Controls.Send("mouth",item.Id,item.Version);
                if (item.Place==ItemPlace.Mouth&&GUILayout.Button("吐出")) Controls.Send("spit",item.Id,item.Version);
                if (Panel == "cabinet")
                {
                    if (item.Place == ItemPlace.Cabinet) { if (GUILayout.Button("取出")) Controls.Send("cabinet_take", item.Id, item.Version); }
                    else if (GUILayout.Button("放入个人柜")) Controls.Send("cabinet_store", item.Id, item.Version);
                }
                GUILayout.EndHorizontal();
                if (item.Kind==ItemKind.RawStone&&(item.Place==ItemPlace.Hand||item.Place==ItemPlace.Pocket))
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("粗检（私有线索）")) Controls.Send("inspect",item.Id,item.Version);
                    if (Panel=="cutter")
                    {
                        if(item.RevealStage==0&&GUILayout.Button("开窗 · "+view.Rules.WindowPrice)) Controls.Send("window",item.Id,item.Version);
                        if(item.RevealStage<2&&GUILayout.Button("全开 · "+view.Rules.OpenPrice)) Controls.Send("open",item.Id,item.Version);
                    }
                    GUILayout.EndHorizontal();
                }
            }
        }
        private void Wager(CampView view)
        {
            GUILayout.Label("两人各放一块未全开原石与等额金币。双方确认才托管；胜者拿赌金，平局退款，各自矿芯送回个人柜。",_label);
            var w=view.Wager;
            if(w!=null)
            {
                string a=view.Players.Find(p=>p.Id==w.PlayerA)?.Name??"矿工", b=view.Players.Find(p=>p.Id==w.PlayerB)?.Name??"矿工";
                GUILayout.Label(a+" / "+b+" · 每人 "+w.Stake+" 金币",_label);
                foreach(string id in new[]{w.StoneA,w.StoneB})
                {
                    var item=view.Items.Find(i=>i.Id==id);
                    GUILayout.Label(item==null?"等待提出原石":"提出原石："+(item.WasInspected?"已粗检":"未粗检")+" · "+(item.RevealStage==0?"未开窗":"窗口档 "+item.WindowBand),_label);
                }
                GUILayout.Label(w.Phase==WagerPhase.Draft?"待确认："+(w.ConfirmedA?"A ✓":"A …")+" / "+(w.ConfirmedB?"B ✓":"B …"):
                    w.Phase==WagerPhase.Locked?"已托管，正在揭晓（无法撤单）":w.Phase==WagerPhase.Cancelled?"本局已取消":
                    "已结算："+w.ValueA+" 对 "+w.ValueB+" · "+(string.IsNullOrEmpty(w.WinnerId)?"平局退款":(view.Players.Find(p=>p.Id==w.WinnerId)?.Name??"矿工")+" 赢得赌金"),_label);
                if(w.Phase==WagerPhase.Draft&&(w.PlayerA==view.Self.Id||w.PlayerB==view.Self.Id))
                {if(Button("确认本局 · 最终确认后锁定资产"))Controls.Send("wager_confirm",w.Id,w.Version);if(Button("取消草案"))Controls.Send("wager_cancel",w.Id,w.Version);}
                if(w.Phase==WagerPhase.Locked)return;
            }
            foreach(var p in view.Players.Where(p=>p.Online&&p.Id!=view.Self.Id))
                if(GUILayout.Toggle(_opponent==p.Id,"对手："+p.Name,GUI.skin.button))_opponent=p.Id;
            foreach(var i in view.Items.Where(i=>i.OwnerId==view.Self.Id&&i.Kind==ItemKind.RawStone&&i.RevealStage<2&&(i.Place==ItemPlace.Hand||i.Place==ItemPlace.Pocket)))
                if(GUILayout.Toggle(_stone==i.Id,"原石："+(i.RevealStage==1?"已开窗":"未开")+" · "+i.Id.Substring(Mathf.Max(0,i.Id.Length-10)),GUI.skin.button))_stone=i.Id;
            GUILayout.Label("共同赌金（改变金额或石头会清除双方确认）",_label);_stake=GUILayout.TextField(_stake,10);
            if(Button("提出 / 更新我的原石与金额")&&long.TryParse(_stake,out long amount))Controls.Send("wager_offer",_stone,w?.Version??0,amount,_opponent);
        }
        private void Transfers(CampView view)
        {
            GUILayout.Label("转账金额（确认即支付）"); _amount = GUILayout.TextField(_amount, 10);
            foreach (var player in view.Players.Where(p => p.Id != view.Self.Id))
                if (Button("确认转给 " + player.Name) && long.TryParse(_amount, out long amount)) Controls.Send("transfer", amount: amount, other: player.Id);
        }
        private static bool Button(string text) => GUILayout.Button(text, GUILayout.Height(34));
        private static string Name(ItemKind kind) => kind == ItemKind.Stone ? "石料" : kind == ItemKind.Iron ? "铁料" : kind == ItemKind.Gold ? "金粒" : kind == ItemKind.HeavyOre ? "沉重大矿" : "未知原石";
        private static string Place(ItemPlace place) => place == ItemPlace.Hand ? "手持" : place == ItemPlace.Pocket ? "口袋" : place == ItemPlace.Cabinet ? "个人柜" : place==ItemPlace.Mouth?"嘴藏":place==ItemPlace.Escrow?"赌桌托管":"货架";
        private void OnDestroy() { if (_font != null && _font != InterfaceFont) Destroy(_font); }
    }
}
