using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Single host writer. Successful commands publish only after the whole causal
    // snapshot is durable, so a sold item cannot return while its payout survives.
    public sealed partial class CampAuthority
    {
        public CampState State { get; private set; }
        public CampRules Rules { get; }
        private readonly Action<CampState> _persist;
        private readonly Func<Vector3, Vector3, bool> _lineOfSight;
        public static readonly Vector3 Spawn = new Vector3(0, 0, -4);
        public static readonly Vector3 SalePoint = new Vector3(0, 0, 0);

        public CampAuthority(CampState state, CampRules rules, Action<CampState> persist,
            Func<Vector3, Vector3, bool> lineOfSight = null)
        {
            State = state ?? throw new ArgumentNullException(nameof(state));
            Rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _persist = persist ?? throw new ArgumentNullException(nameof(persist));
            _lineOfSight = lineOfSight ?? ((a, b) => true);
            Validate(State);
        }

        public static CampState NewWorld(string name, int seed)
        {
            return new CampState { WorldId = Guid.NewGuid().ToString("N"), Name = name, Seed = seed };
        }

        public CampPlayer Join(string id, string credential, string name)
        {
            Require(Guid.TryParseExact(id, "N", out _) && credential != null && credential.Length >= 32 && credential.Length <= 128,
                "identity", "玩家身份无效。");
            var next = CampCodec.Clone(State);
            var player = next.Players.Find(p => p.Id == id);
            string hash = Hash(credential);
            if (player == null)
            {
                player = new CampPlayer { Id = id, CredentialHash = hash,
                    Name = SafeName(name), Position = Spawn, ColorIndex = next.Players.Count % 4 };
                next.Players.Add(player);
            }
            else
            {
                Require(player.CredentialHash == hash, "identity", "身份凭据不匹配，无法使用这个账户。");
                player.Name = SafeName(name);
            }
            Commit(next);
            return State.Players.Find(p => p.Id == id);
        }

        public CampResult Execute(string actorId, CampCommand command)
        {
            if (command == null || string.IsNullOrWhiteSpace(command.RequestId) || command.RequestId.Length > 96)
                return Fail(command?.RequestId, "request", "请求编号无效。");
            string key = actorId + ":" + command.RequestId;
            string fingerprint = Hash(JsonUtility.ToJson(command));
            var prior = State.Receipts.Find(r => r.Key == key);
            if (prior != null)
                return prior.Fingerprint == fingerprint ? prior.Result : Fail(command.RequestId, "request_conflict", "重复请求内容不一致。");
            var next = CampCodec.Clone(State);
            CampResult result;
            try
            {
                var actor = next.Players.Find(p => p.Id == actorId);
                Require(actor != null, "identity", "请先加入营地。");
                result = Apply(next, actor, command);
            }
            catch (RuleFailure error)
            {
                next = CampCodec.Clone(State);
                result = Fail(command.RequestId, error.Code, error.Message);
            }
            result.RequestId = command.RequestId;
            result.Revision = next.Revision + 1;
            next.Receipts.Add(new CampReceipt { Key = key, Fingerprint = fingerprint, Result = result });
            try { Commit(next); }
            catch (Exception error)
            {
                // Caller may retry the same ID after storage is repaired. No success
                // or live balance/item changes are published on a failed disk write.
                return Fail(command.RequestId, "save_failed", "保存失败，操作未生效：" + error.GetType().Name);
            }
            return result;
        }

        private CampResult Apply(CampState state, CampPlayer actor, CampCommand command)
        {
            switch (command.Action)
            {
                case "pickup":
                {
                    var item = GetItem(state, command);
                    Require(item.Place == ItemPlace.Ground, "occupied", "物品已被拿走。");
                    Require(!state.Items.Any(i => i.Place == ItemPlace.Hand && i.OwnerId == actor.Id), "hands_full", "手中已有物品。");
                    RequireNear(actor.Position, item.Position, Rules.Reach);
                    Require(_lineOfSight(actor.Position + Vector3.up * 1.6f, item.Position), "blocked", "物品被遮挡。");
                    SetPlace(item, ItemPlace.Hand, actor.Id, "", actor.Position + Vector3.up);
                    return Ok("已拿起矿物。");
                }
                case "drop":
                case "throw":
                {
                    var item = GetItem(state, command);
                    Require(item.Place == ItemPlace.Hand && item.OwnerId == actor.Id, "ownership", "你没有持有该物品。");
                    Require(CampCodec.IsFinite(command.Direction), "direction", "方向无效。");
                    Vector3 direction = command.Direction.sqrMagnitude > .01f ? command.Direction.normalized : Vector3.forward;
                    Vector3 end = actor.Position + Vector3.up + direction * 1.2f;
                    Require(_lineOfSight(actor.Position + Vector3.up, end), "blocked", "没有足够放置空间。");
                    SetPlace(item, ItemPlace.Ground, "", "", end);
                    item.Velocity = command.Action == "throw" ? direction * 7f + Vector3.up : Vector3.zero;
                    return Ok(command.Action == "throw" ? "矿物已抛出。" : "矿物已放下。");
                }
                case "sell":
                {
                    var item = GetItem(state, command);
                    RequireNear(actor.Position, SalePoint, 4f);
                    Require(item.OwnerId == actor.Id && (item.Place == ItemPlace.Hand || item.Place == ItemPlace.Pocket), "ownership", "只能出售自己携带的货物。");
                    long price = Price(item);
                    Require(actor.Balance <= Rules.MoneyLimit - price, "money_limit", "余额达到上限。");
                    actor.Balance += price;
                    SetPlace(item, ItemPlace.Sold, actor.Id, "", item.Position);
                    return Ok("成交：" + price + " 金币。", price);
                }
                case "transfer":
                {
                    var receiver = state.Players.Find(p => p.Id == command.OtherPlayerId);
                    Require(receiver != null && receiver.Id != actor.Id, "recipient", "请选择另一位营地成员。");
                    Require(command.Amount > 0 && command.Amount <= Rules.MoneyLimit, "amount", "金额无效。");
                    Require(actor.Balance >= command.Amount, "funds", "余额不足。");
                    Require(receiver.Balance <= Rules.MoneyLimit - command.Amount, "money_limit", "对方余额达到上限。");
                    actor.Balance -= command.Amount;
                    receiver.Balance += command.Amount;
                    return Ok("转账完成。", command.Amount);
                }
                default: throw new RuleFailure("action", "未知操作。");
            }
        }

        public void Disconnect(string playerId)
        {
            var next = CampCodec.Clone(State);
            var actor = next.Players.Find(p => p.Id == playerId);
            if (actor == null) return;
            string bag = "offline-" + Guid.NewGuid().ToString("N");
            foreach (var item in next.Items.Where(i => i.OwnerId == playerId &&
                (i.Place == ItemPlace.Hand || i.Place == ItemPlace.Pocket || i.Place == ItemPlace.Mouth)))
                SetPlace(item, ItemPlace.Ground, "", item.Kind == ItemKind.HeavyOre ? "" : bag, actor.Position + Vector3.up * .5f);
            foreach (var item in next.Items.Where(i => i.AssistantId == playerId)) item.AssistantId = "";
            Commit(next);
        }

        // Host shutdown uses Save(), not Disconnect for every online player.
        public void Save() => Commit(CampCodec.Clone(State));
        public void Commit(CampState next)
        {
            Validate(next);
            // Detach even when a caller accidentally passes the current instance.
            next = CampCodec.Clone(next);
            next.Revision = State.Revision + 1;
            next.SavedUtc = DateTime.UtcNow.ToString("O");
            _persist(next);
            State = next;
        }

        public long Price(CampItem item)
        {
            int value = item.Kind == ItemKind.Stone ? Rules.StonePrice : item.Kind == ItemKind.Iron ? Rules.IronPrice
                : item.Kind == ItemKind.Gold ? Rules.GoldPrice : item.Kind == ItemKind.HeavyOre ? Rules.HeavyPrice
                : item.RevealStage >= 2 ? item.HiddenValue : Rules.RawUnopenedPrice;
            return checked((long)value * item.Quantity);
        }
        private static CampItem GetItem(CampState state, CampCommand command)
        {
            var item = state.Items.Find(i => i.Id == command.TargetId);
            Require(item != null, "item", "物品不存在。");
            Require(item.Version == command.ExpectedVersion, "stale", "物品状态已变化，请重试。");
            return item;
        }
        private static void SetPlace(CampItem item, ItemPlace place, string owner, string container, Vector3 position)
        {
            item.Place = place; item.OwnerId = owner; item.ContainerId = container; item.Position = position;
            item.Velocity = Vector3.zero; item.AssistantId = ""; item.Version++;
        }
        private static void RequireNear(Vector3 a, Vector3 b, float distance) => Require(Vector3.Distance(a, b) <= distance, "distance", "请再靠近一些。");
        private static CampResult Ok(string message, long amount = 0) => new CampResult { Success = true, Code = "ok", Message = message, Amount = amount };
        private static CampResult Fail(string id, string code, string message) => new CampResult { RequestId = id, Success = false, Code = code, Message = message };
        private static void Require(bool condition, string code, string message) { if (!condition) throw new RuleFailure(code, message); }
        private sealed class RuleFailure : Exception
        {
            public readonly string Code;
            public RuleFailure(string code, string message) : base(message) { Code = code; }
        }
        public static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        private static string SafeName(string name) => string.IsNullOrWhiteSpace(name) ? "矿工" : name.Trim().Substring(0, Math.Min(name.Trim().Length, 24));

        public static void Validate(CampState state)
        {
            Require(state != null && state.Players != null && state.Items != null && state.Receipts != null, "save", "存档集合缺失。");
            Require(state.SchemaVersion == 1 && Guid.TryParseExact(state.WorldId, "N", out _), "save", "存档版本或世界 ID 无效。");
            Require(state.Players.All(p => p != null && !string.IsNullOrEmpty(p.Id) && p.Balance >= 0 && CampCodec.IsFinite(p.Position)), "save", "玩家数据无效。");
            Require(state.Players.Select(p => p.Id).Distinct().Count() == state.Players.Count, "save", "玩家 ID 重复。");
            Require(state.Items.All(i => i != null && !string.IsNullOrEmpty(i.Id) && i.Quantity > 0 && i.HiddenValue >= 0 &&
                Enum.IsDefined(typeof(ItemKind), i.Kind) && Enum.IsDefined(typeof(ItemPlace), i.Place) &&
                i.Version >= 0 && CampCodec.IsFinite(i.Position) && CampCodec.IsFinite(i.Velocity)), "save", "矿物数据无效。");
            Require(state.Items.Select(i => i.Id).Distinct().Count() == state.Items.Count, "save", "矿物 ID 重复。");
            Require(state.Receipts.All(r => r != null && !string.IsNullOrEmpty(r.Key) && !string.IsNullOrEmpty(r.Fingerprint) && r.Result != null), "save", "交易回执损坏。");
            Require(state.Receipts.Select(r => r.Key).Distinct().Count() == state.Receipts.Count, "save", "交易回执编号重复。");
            foreach (var item in state.Items)
                if (item.Place != ItemPlace.Ground && item.Place != ItemPlace.Vehicle)
                    Require(state.Players.Any(p => p.Id == item.OwnerId), "save", "物品归属无效。");
            foreach (var player in state.Players)
            {
                Require(state.Items.Count(i => i.OwnerId == player.Id && i.Place == ItemPlace.Hand) <= 1, "save", "手持归属重复。");
                Require(state.Items.Count(i => i.OwnerId == player.Id && i.Place == ItemPlace.Mouth) <= 1, "save", "嘴槽归属重复。");
            }
        }
    }
}
