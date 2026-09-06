using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed partial class CampAuthority
    {
        private CampResult ApplySocial(CampState state, CampPlayer actor, CampCommand command)
        {
            switch (command.Action)
            {
                case "inspect":
                case "window":
                case "open":
                    return ProcessRaw(state, actor, command);
                case "wager_offer": return OfferWager(state, actor, command);
                case "wager_confirm": return ConfirmWager(state, actor, command);
                case "wager_cancel":
                {
                    var wager = RequireWager(state, actor, command);
                    Require(wager.Phase == WagerPhase.Draft, "wager_locked", "本局已锁定或结束，不能取消。");
                    CancelWager(wager);
                    return Ok("提议已取消；原石与金币仍由各自保管。");
                }
                case "mouth":
                {
                    var item = GetItem(state, command);
                    Require(item.OwnerId == actor.Id && IsCarriedMaterial(item), "ownership", "只能藏起自己携带的小金粒。");
                    Require(item.Kind == ItemKind.Gold && item.Quantity == 1, "mouth_size", "嘴槽只能容纳一颗小金粒。");
                    Require(!state.Items.Any(i => i.OwnerId == actor.Id && i.Place == ItemPlace.Mouth), "mouth_full", "嘴里已经有矿物。");
                    RequireSocialIdle(state, actor);
                    SetPlace(item, ItemPlace.Mouth, actor.Id, "", actor.Position + Vector3.up * 1.5f);
                    return Ok("已藏入口中；脸颊会鼓起。");
                }
                case "spit":
                {
                    var item = GetItem(state, command);
                    Require(item.OwnerId == actor.Id && item.Place == ItemPlace.Mouth, "ownership", "嘴里没有这件物品。");
                    Require(CampCodec.IsFinite(command.Direction), "direction", "方向无效。");
                    Vector3 direction = command.Direction.sqrMagnitude > .01f ? command.Direction.normalized : Vector3.forward;
                    Vector3 end = actor.Position + Vector3.up * 1.3f + direction * 1.2f;
                    Require(_lineOfSight(actor.Position + Vector3.up * 1.3f, end) && _freePlacement(end, .4f), "blocked", "前方没有安全的吐出空间。");
                    SetPlace(item, ItemPlace.Ground, "", "", end);
                    item.Velocity = direction * 2f;
                    return Ok("金粒已吐出。");
                }
                case "smoke":
                case "drink":
                    RequireSocialIdle(state, actor);
                    RequireSocialHands(state, actor);
                    Require(!state.Items.Any(i => i.OwnerId == actor.Id && i.Place == ItemPlace.Mouth), "mouth_full", "请先吐出口中的矿物。");
                    StartSocialAction(state, actor, command.Action, command.Action == "smoke" ? Rules.SmokeSeconds : Rules.DrinkSeconds);
                    return Ok(command.Action == "smoke" ? "开始吸烟表演；随时可以取消。" : "开始喝酒表演；随时可以取消。");
                case "cancel_action":
                    ClearSocialAction(actor);
                    return Ok("动作已取消。");
                case "attack": return AttackPlayer(state, actor, command);
                case "rob": return BeginRob(state, actor, command);
                case "pvp":
                    Require(_isHost(actor.Id), "host_only", "只有房主可以修改攻击与抢夺设置。");
                    Require(command.Amount == 0 || command.Amount == 1, "amount", "开关值无效。");
                    state.PvpEnabled = command.Amount == 1;
                    if (!state.PvpEnabled)
                        foreach (var player in state.Players.Where(p => p.Action == "rob" || p.Action == "attack")) ClearSocialAction(player);
                    return Ok(state.PvpEnabled ? "攻击与抢夺已开启。" : "攻击与抢夺已关闭。");
                default: return Fail(command.RequestId, "action", "未知操作。");
            }
        }

        private CampResult ProcessRaw(CampState state, CampPlayer actor, CampCommand command)
        {
            var item = GetItem(state, command);
            Require(item.Kind == ItemKind.RawStone && item.Quantity == 1, "raw", "请选择一块独立原石。");
            Require(item.OwnerId == actor.Id && IsCarriedMaterial(item), "ownership", "只能处理自己携带的原石。");
            EnsureRawBands(item);
            if (command.Action == "inspect")
            {
                if (!item.InspectedBy.Contains(actor.Id))
                {
                    item.InspectedBy.Add(actor.Id);
                    item.Version++;
                }
                return Ok(CampProjection.RawClue(item.RawClueBand));
            }
            RequireStationAccess(actor, CampMap.CutterPoint);
            bool window = command.Action == "window";
            Require(window ? item.RevealStage == 0 : item.RevealStage < 2, "revealed", "原石已完成这一步加工。");
            int fee = window ? Rules.WindowPrice : Rules.OpenPrice;
            Require(fee >= 0 && actor.Balance >= fee, "funds", "加工费不足。");
            actor.Balance -= fee;
            item.RevealStage = window ? 1 : 2;
            item.Version++;
            return Ok(window ? "开窗完成；窗口线索已公开。" : "完整开石完成；矿芯和收购估值已公开。", fee);
        }

        private CampResult OfferWager(CampState state, CampPlayer actor, CampCommand command)
        {
            RequireStationAccess(actor, CampMap.WagerPoint);
            var opponent = state.Players.Find(p => p.Id == command.OtherPlayerId);
            Require(opponent != null && opponent.Id != actor.Id && _isOnline(opponent.Id) && _isOnline(actor.Id), "opponent", "请选择在线的另一位矿工。");
            RequireStationAccess(opponent, CampMap.WagerPoint);
            Require(command.Amount > 0 && command.Amount <= Rules.MoneyLimit / 2, "amount", "赌注金额无效。");
            var stone = state.Items.Find(i => i.Id == command.TargetId);
            Require(IsWagerStone(stone, actor.Id), "raw", "只能提出自己携带且未全开的单块原石。");
            var wager = state.Wager;
            Require(command.ExpectedVersion == (wager?.Version ?? 0), "stale", "赌桌条件已变化，请重新查看。");
            Require(wager == null || wager.Phase != WagerPhase.Locked, "wager_locked", "本局正在结算。");
            if (wager == null || wager.Phase == WagerPhase.Settled || wager.Phase == WagerPhase.Cancelled)
            {
                wager = new CampWager { Id = Guid.NewGuid().ToString("N"), PlayerA = actor.Id, PlayerB = opponent.Id,
                    StoneA = "", StoneB = "", Version = wager?.Version ?? 0, Phase = WagerPhase.Draft, Stake = command.Amount };
                state.Wager = wager;
            }
            else
            {
                Require((wager.PlayerA == actor.Id && wager.PlayerB == opponent.Id) || (wager.PlayerB == actor.Id && wager.PlayerA == opponent.Id),
                    "wager_busy", "赌桌已有其他人的提议；请先取消原提议。");
            }
            bool sideA = wager.PlayerA == actor.Id;
            bool changed = wager.Stake != command.Amount || (sideA ? wager.StoneA : wager.StoneB) != stone.Id ||
                (sideA ? wager.StoneVersionA : wager.StoneVersionB) != stone.Version;
            if (changed)
            {
                wager.Stake = command.Amount;
                if (sideA) { wager.StoneA = stone.Id; wager.StoneVersionA = stone.Version; }
                else { wager.StoneB = stone.Id; wager.StoneVersionB = stone.Version; }
                wager.ConfirmedA = wager.ConfirmedB = false;
                wager.Version++;
            }
            return Ok("条件已提出；双方确认之前不会托管金币或原石。");
        }

        private CampResult ConfirmWager(CampState state, CampPlayer actor, CampCommand command)
        {
            var wager = RequireWager(state, actor, command);
            Require(wager.Phase == WagerPhase.Draft, "wager_locked", "本局已锁定或结束。");
            // An offer can be moved, inspected or sold before locking. Persist the
            // invalidation itself, rather than silently retaining either old consent.
            if (InvalidateDraftStones(state, wager))
                return Fail(command.RequestId, "wager_changed", "原石状态已变化，旧确认已清除；请重新提出条件。");
            Require(!string.IsNullOrEmpty(wager.StoneA) && !string.IsNullOrEmpty(wager.StoneB), "wager_incomplete", "双方都需要先提出原石。");
            var a = state.Players.Find(p => p.Id == wager.PlayerA);
            var b = state.Players.Find(p => p.Id == wager.PlayerB);
            Require(_isOnline(a.Id) && _isOnline(b.Id), "opponent", "双方需要在线确认。");
            RequireStationAccess(a, CampMap.WagerPoint);
            RequireStationAccess(b, CampMap.WagerPoint);
            Require(a.StunnedUntil <= state.OnlineSeconds && b.StunnedUntil <= state.OnlineSeconds && a.EscapeAt <= 0 && b.EscapeAt <= 0,
                "busy", "双方暂时无法开始赌局。");
            Require(a.Balance >= wager.Stake && b.Balance >= wager.Stake, "funds", "一方赌金不足。");
            Require(wager.Stake <= Rules.MoneyLimit / 2 && a.Balance <= Rules.MoneyLimit - wager.Stake && b.Balance <= Rules.MoneyLimit - wager.Stake,
                "money_limit", "请先为本局可能获得的赌金留出余额空间。");
            if (actor.Id == a.Id) wager.ConfirmedA = true; else wager.ConfirmedB = true;
            if (!wager.ConfirmedA || !wager.ConfirmedB) return Ok("已确认当前条件，等待另一位矿工确认。");
            var stoneA = state.Items.Find(i => i.Id == wager.StoneA);
            var stoneB = state.Items.Find(i => i.Id == wager.StoneB);
            a.Balance -= wager.Stake;
            b.Balance -= wager.Stake;
            // Full-open prices are frozen with the same hidden value/quantity used
            // by Price; neither full truth nor final result is published yet.
            wager.ValueA = checked((long)stoneA.HiddenValue * stoneA.Quantity);
            wager.ValueB = checked((long)stoneB.HiddenValue * stoneB.Quantity);
            SetPlace(stoneA, ItemPlace.Escrow, a.Id, "wager:" + wager.Id, CampMap.WagerPoint + Vector3.left * .5f + Vector3.up);
            SetPlace(stoneB, ItemPlace.Escrow, b.Id, "wager:" + wager.Id, CampMap.WagerPoint + Vector3.right * .5f + Vector3.up);
            wager.StoneVersionA = stoneA.Version;
            wager.StoneVersionB = stoneB.Version;
            wager.Phase = WagerPhase.Locked;
            wager.Version++;
            return Ok("双方原石与赌金已正式托管，随后揭晓并结算。", wager.Stake);
        }

        private static CampWager RequireWager(CampState state, CampPlayer actor, CampCommand command)
        {
            var wager = state.Wager;
            Require(wager != null && wager.Id == command.TargetId, "wager", "赌局不存在。");
            Require(wager.PlayerA == actor.Id || wager.PlayerB == actor.Id, "ownership", "只有本局参与者可以操作。");
            Require(wager.Version == command.ExpectedVersion, "stale", "赌局条件已变化，请重新查看。");
            return wager;
        }

        private static bool IsWagerStone(CampItem item, string owner) => item != null && item.OwnerId == owner &&
            IsCarriedMaterial(item) && item.Kind == ItemKind.RawStone && item.Quantity == 1 && item.RevealStage < 2;

        private static bool DraftStoneInvalid(CampState state, string id, string owner, int version)
        {
            if (string.IsNullOrEmpty(id)) return false;
            var item = state.Items.Find(i => i.Id == id);
            return !IsWagerStone(item, owner) || item.Version != version;
        }

        private static bool InvalidateDraftStones(CampState state, CampWager wager)
        {
            bool a = DraftStoneInvalid(state, wager.StoneA, wager.PlayerA, wager.StoneVersionA);
            bool b = DraftStoneInvalid(state, wager.StoneB, wager.PlayerB, wager.StoneVersionB);
            if (!a && !b) return false;
            if (a) { wager.StoneA = ""; wager.StoneVersionA = 0; }
            if (b) { wager.StoneB = ""; wager.StoneVersionB = 0; }
            wager.ConfirmedA = wager.ConfirmedB = false;
            wager.Version++;
            return true;
        }

        private static void CancelWager(CampWager wager)
        {
            wager.Phase = WagerPhase.Cancelled;
            wager.ConfirmedA = wager.ConfirmedB = false;
            wager.Version++;
        }

        private void SettleWager(CampState state)
        {
            var wager = state.Wager;
            if (wager == null || wager.Phase != WagerPhase.Locked) return;
            ValidateSocial(state);
            var a = state.Players.Find(p => p.Id == wager.PlayerA);
            var b = state.Players.Find(p => p.Id == wager.PlayerB);
            long pot = checked(wager.Stake * 2);
            long payoutA = wager.ValueA > wager.ValueB ? pot : wager.ValueA == wager.ValueB ? wager.Stake : 0;
            long payoutB = pot - payoutA;
            // Reservations prevent concurrent income from using this space. If a
            // changed configuration cannot hold it, keep escrow intact and fail.
            Require(a.Balance <= Rules.MoneyLimit - payoutA && b.Balance <= Rules.MoneyLimit - payoutB, "money_limit", "结算余额空间不足；托管仍保留。");
            a.Balance = checked(a.Balance + payoutA);
            b.Balance = checked(b.Balance + payoutB);
            ReturnWagerStone(state.Items.Find(i => i.Id == wager.StoneA), a);
            ReturnWagerStone(state.Items.Find(i => i.Id == wager.StoneB), b);
            wager.WinnerId = wager.ValueA == wager.ValueB ? "" : payoutA > 0 ? a.Id : b.Id;
            wager.Phase = WagerPhase.Settled;
            wager.Version++;
        }

        private static void ReturnWagerStone(CampItem stone, CampPlayer owner)
        {
            stone.RevealStage = 2;
            SetPlace(stone, ItemPlace.Cabinet, owner.Id, "cabinet:" + owner.Id, CampMap.CabinetPoint(owner.ColorIndex) + Vector3.up);
        }

        private long ReservedCredit(CampState state, string playerId)
        {
            var wager = state.Wager;
            return wager != null && wager.Phase == WagerPhase.Locked && (wager.PlayerA == playerId || wager.PlayerB == playerId)
                ? checked(wager.Stake * 2) : 0;
        }

        private CampResult AttackPlayer(CampState state, CampPlayer actor, CampCommand command)
        {
            Require(state.PvpEnabled, "pvp_disabled", "房主已关闭攻击与抢夺。");
            RequireSocialIdle(state, actor);
            RequireSocialHands(state, actor);
            Require(state.OnlineSeconds >= actor.NextMineTime, "cooldown", "矿镐尚未准备好。");
            var target = RequirePlayerAim(state, actor, command, Rules.AttackReach);
            Require(target.StunnedUntil <= state.OnlineSeconds && target.ProtectionUntil <= state.OnlineSeconds, "protected", "对方正在恢复保护期。");
            target.StunnedUntil = state.OnlineSeconds + Rules.StunSeconds;
            target.ProtectionUntil = target.StunnedUntil + Rules.ProtectionSeconds;
            target.EscapeAt = 0;
            target.ClimbingLadderId = "";
            ClearSocialAction(target);
            foreach (var item in state.Items.Where(i => i.OwnerId == target.Id && (i.Place == ItemPlace.Hand || i.Place == ItemPlace.Mouth)))
                SetPlace(item, ItemPlace.Ground, "", "", target.Position + Vector3.up * .5f);
            foreach (var item in state.Items.Where(i => i.AssistantId == target.Id)) { item.AssistantId = ""; item.Version++; }
            if (state.Wager != null && state.Wager.Phase == WagerPhase.Draft &&
                (state.Wager.PlayerA == target.Id || state.Wager.PlayerB == target.Id)) CancelWager(state.Wager);
            actor.NextMineTime = state.OnlineSeconds + Rules.MineInterval;
            StartSocialAction(state, actor, "attack", .3f);
            return Ok("有效击中；手持货和嘴中矿物掉落，永久资产不受影响。");
        }

        private CampResult BeginRob(CampState state, CampPlayer actor, CampCommand command)
        {
            Require(state.PvpEnabled, "pvp_disabled", "房主已关闭攻击与抢夺。");
            RequireSocialIdle(state, actor);
            RequireSocialHands(state, actor);
            var target = RequirePlayerAim(state, actor, command, Rules.RobReach);
            Require(target.StunnedUntil > state.OnlineSeconds || target.ProtectionUntil <= state.OnlineSeconds, "protected", "对方正在恢复保护期。");
            var item = state.Items.Where(i => i.OwnerId == target.Id && i.Place == ItemPlace.Pocket && i.Kind != ItemKind.HeavyOre)
                .OrderBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault();
            Require(item != null, "pocket_empty", "没有可拿走的普通口袋货物。");
            StartSocialAction(state, actor, "rob", Rules.RobSeconds);
            actor.ActionTargetId = target.Id;
            actor.ActionItemId = item.Id;
            actor.ActionItemVersion = item.Version;
            return Ok("开始搜取一个口袋格；需保持距离，随时可取消。");
        }

        private CampPlayer RequirePlayerAim(CampState state, CampPlayer actor, CampCommand command, float reach)
        {
            var target = state.Players.Find(p => p.Id == command.TargetId);
            Require(target != null && target.Id != actor.Id && _isOnline(target.Id) && _isOnline(actor.Id), "target", "目标矿工不在线。");
            RequireNear(actor.Position, target.Position, reach);
            Vector3 eye = actor.Position + Vector3.up * 1.6f;
            Vector3 point = target.Position + Vector3.up;
            Require(CampCodec.IsFinite(command.Direction) && command.Direction.sqrMagnitude > .01f &&
                Vector3.Dot(command.Direction.normalized, (point - eye).normalized) >= .7f, "aim", "请瞄准目标矿工。");
            Require(_lineOfSight(eye, point), "blocked", "目标被遮挡。");
            return target;
        }

        private static void RequireSocialIdle(CampState state, CampPlayer actor) =>
            Require(actor.ActionUntil <= state.OnlineSeconds || (actor.Action != "rob" && actor.Action != "smoke" && actor.Action != "drink"),
                "busy", "请先取消当前动作。");

        private static void RequireSocialHands(CampState state, CampPlayer actor)
        {
            RequireHandsFree(state, actor);
            Require(string.IsNullOrEmpty(actor.ClimbingLadderId), "climbing", "请先离开梯子。");
            Require(!state.Items.Any(i => i.AssistantId == actor.Id), "assisting", "请先退出协助搬运。");
        }

        private static void ClearSocialAction(CampPlayer player)
        {
            player.Action = ""; player.ActionUntil = 0;
            player.ActionTargetId = ""; player.ActionItemId = ""; player.ActionItemVersion = 0;
        }

        private static void StartSocialAction(CampState state, CampPlayer player, string action, float duration)
        {
            Require(duration > 0 && float.IsFinite(duration), "config", "动作时长配置无效。");
            ClearSocialAction(player);
            player.Action = action;
            player.ActionUntil = state.OnlineSeconds + duration;
        }

        private void RequireStationAccess(CampPlayer actor, Vector3 station)
        {
            RequireNear(actor.Position, station, Rules.Reach);
            Require(_lineOfSight(actor.Position + Vector3.up * 1.6f, station + Vector3.up * 1.3f), "blocked", "工作台被遮挡。");
        }

        private bool RobStillValid(CampState state, CampPlayer actor, ICollection<string> onlineIds)
        {
            var target = state.Players.Find(p => p.Id == actor.ActionTargetId);
            var item = state.Items.Find(i => i.Id == actor.ActionItemId);
            return state.PvpEnabled && onlineIds.Contains(actor.Id) && target != null && onlineIds.Contains(target.Id) &&
                actor.StunnedUntil <= state.OnlineSeconds && actor.EscapeAt <= 0 && string.IsNullOrEmpty(actor.ClimbingLadderId) &&
                !state.Items.Any(i => (i.OwnerId == actor.Id && i.Place == ItemPlace.Hand) || i.AssistantId == actor.Id) &&
                (target.StunnedUntil > state.OnlineSeconds || target.ProtectionUntil <= state.OnlineSeconds) &&
                Vector3.Distance(actor.Position, target.Position) <= Rules.RobReach &&
                _lineOfSight(actor.Position + Vector3.up * 1.6f, target.Position + Vector3.up) &&
                item != null && item.Version == actor.ActionItemVersion && item.OwnerId == target.Id &&
                item.Place == ItemPlace.Pocket && item.Kind != ItemKind.HeavyOre;
        }

        private bool NeedsSocialTick(CampState state)
        {
            var wager = state.Wager;
            if (wager != null && (wager.Phase == WagerPhase.Locked || (wager.Phase == WagerPhase.Draft &&
                (!_isOnline(wager.PlayerA) || !_isOnline(wager.PlayerB) ||
                 DraftStoneInvalid(state, wager.StoneA, wager.PlayerA, wager.StoneVersionA) ||
                 DraftStoneInvalid(state, wager.StoneB, wager.PlayerB, wager.StoneVersionB))))) return true;
            return state.Players.Any(p => p.Action == "rob" || ((p.Action == "smoke" || p.Action == "drink" || p.Action == "attack") &&
                (p.ActionUntil <= state.OnlineSeconds || p.StunnedUntil > state.OnlineSeconds || !_isOnline(p.Id))));
        }

        // Mutate the caller's transaction only; TickWorld/Disconnect own persistence.
        private void TickSocial(CampState state, ICollection<string> onlineIds)
        {
            var wager = state.Wager;
            if (wager != null && wager.Phase == WagerPhase.Draft)
            {
                if (!onlineIds.Contains(wager.PlayerA) || !onlineIds.Contains(wager.PlayerB)) CancelWager(wager);
                else InvalidateDraftStones(state, wager);
            }
            SettleWager(state);
            foreach (var actor in state.Players)
            {
                if (actor.Action == "rob")
                {
                    if (!RobStillValid(state, actor, onlineIds)) ClearSocialAction(actor);
                    else if (actor.ActionUntil <= state.OnlineSeconds)
                    {
                        var item = state.Items.Find(i => i.Id == actor.ActionItemId);
                        SetPlace(item, ItemPlace.Hand, actor.Id, "", actor.Position + Vector3.up);
                        ClearSocialAction(actor);
                    }
                }
                else if ((actor.Action == "smoke" || actor.Action == "drink" || actor.Action == "attack") &&
                    (actor.ActionUntil <= state.OnlineSeconds || actor.StunnedUntil > state.OnlineSeconds || !onlineIds.Contains(actor.Id)))
                    ClearSocialAction(actor);
            }
        }

        private void DisconnectSocial(CampState state, string playerId)
        {
            foreach (var player in state.Players.Where(p => p.Id == playerId || p.ActionTargetId == playerId)) ClearSocialAction(player);
            var wager = state.Wager;
            if (wager != null && wager.Phase == WagerPhase.Draft && (wager.PlayerA == playerId || wager.PlayerB == playerId)) CancelWager(wager);
        }

        public void RecoverSocial()
        {
            var next = CampCodec.Clone(State);
            bool changed = false;
            foreach (var cell in next.Cells.Where(c => c.YieldKind == ItemKind.RawStone))
            {
                if (cell.RawWindowBand == 0) { cell.RawWindowBand = RawValueBand(cell.HiddenValue); changed = true; }
                if (cell.RawClueBand == 0) { cell.RawClueBand = RawPrivateBand(cell.Id, cell.RawWindowBand); changed = true; }
            }
            foreach (var item in next.Items.Where(i => i.Kind == ItemKind.RawStone)) changed |= EnsureRawBands(item);
            if (next.Wager != null && next.Wager.Phase == WagerPhase.Draft) { CancelWager(next.Wager); changed = true; }
            if (next.Wager != null && next.Wager.Phase == WagerPhase.Locked) { SettleWager(next); changed = true; }
            foreach (var player in next.Players.Where(p => p.Action == "rob" || p.Action == "smoke" || p.Action == "drink" || p.Action == "attack" ||
                !string.IsNullOrEmpty(p.ActionItemId) || !string.IsNullOrEmpty(p.ActionTargetId)))
            { ClearSocialAction(player); changed = true; }
            if (changed) Commit(next);
        }

        private static bool EnsureRawBands(CampItem item)
        {
            bool changed = false;
            if (item.InspectedBy == null) { item.InspectedBy = new List<string>(); changed = true; }
            if (item.RawWindowBand == 0) { item.RawWindowBand = RawValueBand(item.HiddenValue); changed = true; }
            if (item.RawClueBand == 0) { item.RawClueBand = RawPrivateBand(item.Id, item.RawWindowBand); changed = true; }
            return changed;
        }

        private static int RawValueBand(int value) => value < 15 ? 1 : value < 100 ? 2 : 3;
        private static int RawPrivateBand(string id, int windowBand) =>
            Math.Max(1, Math.Min(3, windowBand + Convert.ToInt32(Hash(id).Substring(0, 2), 16) % 3 - 1));

        private static void ValidateSocial(CampState state)
        {
            Require(double.IsFinite(state.OnlineSeconds) && state.OnlineSeconds >= 0, "save", "世界活动时间无效。");
            foreach (var player in state.Players)
            {
                Require(double.IsFinite(player.ActionUntil) && player.ActionUntil >= 0 &&
                    double.IsFinite(player.StunnedUntil) && player.StunnedUntil >= 0 && double.IsFinite(player.ProtectionUntil) && player.ProtectionUntil >= 0,
                    "save", "角色动作时间损坏。");
                if (player.Action == "rob")
                    Require(player.ActionUntil > 0 && player.ActionItemVersion >= 0 &&
                        state.Players.Any(p => p.Id == player.ActionTargetId && p.Id != player.Id) && state.Items.Any(i => i.Id == player.ActionItemId),
                        "save", "搜取动作目标损坏。");
            }
            foreach (var item in state.Items)
            {
                Require(item.RevealStage >= 0 && item.RevealStage <= 2 && item.RawClueBand >= 0 && item.RawClueBand <= 3 &&
                    item.RawWindowBand >= 0 && item.RawWindowBand <= 3, "save", "原石公开状态损坏。");
                if (item.InspectedBy != null)
                    Require(item.InspectedBy.Distinct().Count() == item.InspectedBy.Count && item.InspectedBy.All(id => !string.IsNullOrWhiteSpace(id)),
                        "save", "原石粗检记录损坏。");
            }
            foreach (var cell in state.Cells)
                Require(cell.RawClueBand >= 0 && cell.RawClueBand <= 3 && cell.RawWindowBand >= 0 && cell.RawWindowBand <= 3, "save", "矿格固定线索损坏。");
            var wager = state.Wager;
            var escrow = state.Items.Where(i => i.Place == ItemPlace.Escrow).ToList();
            if (wager == null) { Require(escrow.Count == 0, "save", "存在无赌局的托管原石。"); return; }
            Require(Guid.TryParseExact(wager.Id, "N", out _) && wager.Version >= 0 && Enum.IsDefined(typeof(WagerPhase), wager.Phase) &&
                wager.PlayerA != wager.PlayerB && state.Players.Any(p => p.Id == wager.PlayerA) && state.Players.Any(p => p.Id == wager.PlayerB) &&
                wager.Stake > 0 && wager.Stake <= long.MaxValue / 2 && wager.StoneVersionA >= 0 && wager.StoneVersionB >= 0 && wager.ValueA >= 0 && wager.ValueB >= 0,
                "save", "赌局条款损坏。");
            Require(string.IsNullOrEmpty(wager.StoneA) || string.IsNullOrEmpty(wager.StoneB) || wager.StoneA != wager.StoneB, "save", "同一原石不能占据两份赌注。");
            if (wager.Phase != WagerPhase.Locked)
            {
                Require(escrow.Count == 0, "save", "非锁定赌局不能保留托管物。");
                if (wager.Phase == WagerPhase.Draft || wager.Phase == WagerPhase.Cancelled)
                    Require(wager.ValueA == 0 && wager.ValueB == 0 && string.IsNullOrEmpty(wager.WinnerId), "save", "未锁定赌局出现结算数据。");
                else
                    Require(!string.IsNullOrEmpty(wager.StoneA) && !string.IsNullOrEmpty(wager.StoneB) &&
                        wager.WinnerId == (wager.ValueA == wager.ValueB ? "" : wager.ValueA > wager.ValueB ? wager.PlayerA : wager.PlayerB),
                        "save", "赌局结算标记损坏。");
                return;
            }
            Require(wager.ConfirmedA && wager.ConfirmedB && string.IsNullOrEmpty(wager.WinnerId) && escrow.Count == 2, "save", "锁定赌局缺少确认或托管物。");
            ValidateEscrowStone(escrow.Find(i => i.Id == wager.StoneA), wager, wager.PlayerA, wager.StoneVersionA, wager.ValueA);
            ValidateEscrowStone(escrow.Find(i => i.Id == wager.StoneB), wager, wager.PlayerB, wager.StoneVersionB, wager.ValueB);
        }

        private static void ValidateEscrowStone(CampItem stone, CampWager wager, string owner, int version, long value) =>
            Require(stone != null && stone.Kind == ItemKind.RawStone && stone.Quantity == 1 && stone.OwnerId == owner &&
                stone.ContainerId == "wager:" + wager.Id && stone.Version == version && stone.RevealStage < 2 &&
                value == (long)stone.HiddenValue * stone.Quantity, "save", "托管原石与冻结估值不一致，已保留存档并拒绝结算。");
    }
}
