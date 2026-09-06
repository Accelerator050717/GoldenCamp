using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    // These exercise the single authoritative writer. They are not transport or
    // multi-process evidence; the real network acceptance run is separate.
    public sealed class CampAuthorityTests
    {
        private const string FirstId = "11111111111111111111111111111111";
        private const string SecondId = "22222222222222222222222222222222";
        private const string Credential = "test-only-credential-01234567890123456789";
        private CampState _saved;
        private int _saveCount;
        private bool _failSave;

        [SetUp]
        public void SetUp()
        {
            _saved = null;
            _saveCount = 0;
            _failSave = false;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompetingPickupCommandsWithSameItemVersionHaveExactlyOneWinner(bool secondArrivesFirst)
        {
            var state = World();
            state.Items.Add(Item("contested", ItemPlace.Ground));
            var authority = Authority(state);
            string winner = secondArrivesFirst ? SecondId : FirstId;
            string loser = secondArrivesFirst ? FirstId : SecondId;

            var first = authority.Execute(winner, Command("pickup-first", "pickup", "contested"));
            var second = authority.Execute(loser, Command("pickup-second", "pickup", "contested"));

            Assert.That(new[] { first, second }.Count(r => r.Success), Is.EqualTo(1));
            Assert.That(second.Code, Is.EqualTo("stale"));
            Assert.That(authority.State.Items.Count, Is.EqualTo(1));
            var item = authority.State.Items.Single();
            Assert.That(item.Place, Is.EqualTo(ItemPlace.Hand));
            Assert.That(item.OwnerId, Is.EqualTo(winner));
            Assert.That(item.Version, Is.EqualTo(1));
            Assert.That(_saved.Items.Single().OwnerId, Is.EqualTo(winner));
            Assert.DoesNotThrow(() => CampAuthority.Validate(authority.State));
        }

        [Test]
        public void ReplayedSaleAfterReloadPaysOnceAndChangedRequestIsRejected()
        {
            var state = World();
            state.Items.Add(Item("sale", ItemPlace.Hand, FirstId, ItemKind.Gold));
            var authority = Authority(state);
            var command = Command("sale-once", "sell", "sale");

            var first = authority.Execute(FirstId, command);
            Assert.That(first.Success, Is.True);
            Assert.That(first.Amount, Is.EqualTo(55));
            // Receipt must survive the same codec boundary used for persistence.
            var restarted = Authority(CampCodec.Clone(_saved));
            int savesBeforeReplay = _saveCount;
            string beforeReplay = CampCodec.Encode(restarted.State);
            var replay = restarted.Execute(FirstId, command);
            var changed = Command("sale-once", "sell", "a-different-item");
            var conflict = restarted.Execute(FirstId, changed);

            Assert.That(replay.Success, Is.True);
            Assert.That(replay.Amount, Is.EqualTo(first.Amount));
            Assert.That(replay.Revision, Is.EqualTo(first.Revision));
            Assert.That(conflict.Success, Is.False);
            Assert.That(conflict.Code, Is.EqualTo("request_conflict"));
            Assert.That(CampCodec.Encode(restarted.State), Is.EqualTo(beforeReplay));
            Assert.That(_saveCount, Is.EqualTo(savesBeforeReplay));
            Assert.That(Player(restarted, FirstId).Balance, Is.EqualTo(155));
            Assert.That(restarted.State.Items.Single().Place, Is.EqualTo(ItemPlace.Sold));
            Assert.That(restarted.State.Items.Single().Version, Is.EqualTo(1));
        }

        [Test]
        public void ReplayedTransferPaysOnceAndChangedAmountIsRejected()
        {
            var authority = Authority(World());
            var command = Transfer("transfer-once", 30);
            var first = authority.Execute(FirstId, command);
            var restarted = Authority(CampCodec.Clone(_saved));
            string committed = CampCodec.Encode(restarted.State);
            var replay = restarted.Execute(FirstId, command);
            var conflict = restarted.Execute(FirstId, Transfer("transfer-once", 31));

            Assert.That(first.Success, Is.True);
            Assert.That(replay.Success, Is.True);
            Assert.That(replay.Revision, Is.EqualTo(first.Revision));
            Assert.That(conflict.Code, Is.EqualTo("request_conflict"));
            Assert.That(CampCodec.Encode(restarted.State), Is.EqualTo(committed));
            Assert.That(Player(restarted, FirstId).Balance, Is.EqualTo(70));
            Assert.That(Player(restarted, SecondId).Balance, Is.EqualTo(50));
            Assert.That(restarted.State.Players.Sum(p => p.Balance), Is.EqualTo(120));
            Assert.That(restarted.State.Receipts.Count, Is.EqualTo(1));
        }

        [TestCase("sell")]
        [TestCase("transfer")]
        public void DiskFailurePublishesNoAssetsOrReceiptAndSameRequestCanRetry(string action)
        {
            var state = World();
            state.Items.Add(Item("sale", ItemPlace.Hand, FirstId, ItemKind.Gold));
            var authority = Authority(state);
            var command = action == "sell" ? Command("retry", "sell", "sale") : Transfer("retry", 30);
            string original = CampCodec.Encode(authority.State);
            _failSave = true;

            var failed = authority.Execute(FirstId, command);

            Assert.That(failed.Success, Is.False);
            Assert.That(failed.Code, Is.EqualTo("save_failed"));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(original));
            Assert.That(_saved, Is.Null);
            _failSave = false;
            var retried = authority.Execute(FirstId, command);
            Assert.That(retried.Success, Is.True);
            Assert.That(authority.State.Receipts.Count, Is.EqualTo(1));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(CampCodec.Encode(authority.State)));
            Assert.That(Player(authority, FirstId).Balance, Is.EqualTo(action == "sell" ? 155 : 70));
            if (action == "sell") Assert.That(authority.State.Items.Single().Place, Is.EqualTo(ItemPlace.Sold));
            string committed = CampCodec.Encode(authority.State);
            authority.Execute(FirstId, command);
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(committed));
        }

        [Test]
        public void CommitOfLiveStateDoesNotChangeRevisionTimestampOrAssetsWhenPersistenceFails()
        {
            var state = World();
            state.Revision = 41;
            state.SavedUtc = "2026-09-05T01:02:03.0000000Z";
            state.Items.Add(Item("retained", ItemPlace.Hand, FirstId, ItemKind.Gold));
            var authority = Authority(state);
            var liveArgument = authority.State;
            string before = CampCodec.Encode(liveArgument);
            _failSave = true;

            Assert.That(() => authority.Commit(liveArgument), Throws.TypeOf<IOException>());

            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before));
            Assert.That(CampCodec.Encode(liveArgument), Is.EqualTo(before));
            Assert.That(_saved, Is.Null);
            Assert.That(_saveCount, Is.Zero);
        }

        [TestCase(0L)]
        [TestCase(-1L)]
        [TestCase(long.MinValue)]
        [TestCase(long.MaxValue)]
        [TestCase(1000000001L)]
        public void InvalidTransferAmountNeverChangesAssets(long amount)
        {
            var authority = Authority(World());
            string before = Assets(authority.State);
            var result = authority.Execute(FirstId, Transfer("invalid-amount", amount));

            Assert.That(result.Success, Is.False);
            Assert.That(result.Code, Is.EqualTo("amount"));
            Assert.That(Assets(authority.State), Is.EqualTo(before));
        }

        [Test]
        public void InsufficientFundsAndReceiverOverflowLeaveBothBalancesUnchanged()
        {
            var authority = Authority(World());
            string before = Assets(authority.State);
            var insufficient = authority.Execute(FirstId, Transfer("insufficient", 101));
            Assert.That(insufficient.Code, Is.EqualTo("funds"));
            Assert.That(Assets(authority.State), Is.EqualTo(before));

            var state = World();
            state.Players.Single(p => p.Id == SecondId).Balance = long.MaxValue - 5;
            authority = Authority(state, new CampRules { MoneyLimit = long.MaxValue });
            before = Assets(authority.State);
            var overflow = authority.Execute(FirstId, Transfer("overflow", 10));
            Assert.That(overflow.Success, Is.False);
            Assert.That(overflow.Code, Is.EqualTo("money_limit"));
            Assert.That(Assets(authority.State), Is.EqualTo(before));
        }

        [Test]
        public void LargeSaleUsesLongArithmeticAndLimitFailureDoesNotConsumeItem()
        {
            var state = World();
            var item = Item("large-sale", ItemPlace.Pocket, FirstId, ItemKind.Gold);
            item.Quantity = int.MaxValue;
            state.Items.Add(item);
            var authority = Authority(state);
            string before = Assets(authority.State);
            var failed = authority.Execute(FirstId, Command("large-limited", "sell", item.Id));
            Assert.That(failed.Success, Is.False);
            Assert.That(failed.Code, Is.EqualTo("money_limit"));
            Assert.That(Assets(authority.State), Is.EqualTo(before));

            authority = Authority(CampCodec.Clone(state), new CampRules { MoneyLimit = long.MaxValue });
            var result = authority.Execute(FirstId, Command("large-allowed", "sell", item.Id));
            long expected = 55L * int.MaxValue;
            Assert.That(result.Success, Is.True);
            Assert.That(result.Amount, Is.EqualTo(expected));
            Assert.That(Player(authority, FirstId).Balance, Is.EqualTo(100L + expected));
        }

        [TestCase("distance")]
        [TestCase("blocked")]
        public void RejectedPickupDoesNotChangeItemOrBalances(string reason)
        {
            var state = World();
            var item = Item("unreachable", ItemPlace.Ground);
            if (reason == "distance") item.Position = Vector3.forward * 4;
            state.Items.Add(item);
            var authority = new CampAuthority(state, new CampRules(), Persist, (a, b) => reason != "blocked");
            string before = Assets(authority.State);
            var result = authority.Execute(FirstId, Command("rejected-pickup", "pickup", item.Id));
            Assert.That(result.Success, Is.False);
            Assert.That(result.Code, Is.EqualTo(reason));
            Assert.That(Assets(authority.State), Is.EqualTo(before));
        }

        [Test]
        public void StableIdentitySurvivesRenameAndReloadWithoutResettingAssets()
        {
            var state = World();
            var original = state.Players.Single(p => p.Id == FirstId);
            original.ToolLevel = 3;
            original.DetectorLevel = 2;
            original.ColorIndex = 2;
            state.Items.Add(Item("owned", ItemPlace.Cabinet, FirstId));
            var authority = Authority(state);

            authority.Join(FirstId, Credential, "改名后的矿工");
            var restarted = Authority(CampCodec.Clone(_saved));
            var joined = restarted.Join(FirstId, Credential, "再次改名");

            Assert.That(restarted.State.Players.Count, Is.EqualTo(2));
            Assert.That(joined.Id, Is.EqualTo(FirstId));
            Assert.That(joined.Name, Is.EqualTo("再次改名"));
            Assert.That(joined.Balance, Is.EqualTo(100));
            Assert.That(joined.ToolLevel, Is.EqualTo(3));
            Assert.That(joined.DetectorLevel, Is.EqualTo(2));
            Assert.That(joined.ColorIndex, Is.EqualTo(2));
            Assert.That(joined.CredentialHash, Is.EqualTo(CampAuthority.Hash(Credential)));
            Assert.That(joined.CredentialHash, Is.Not.EqualTo(Credential));
            Assert.That(restarted.State.Items.Single().OwnerId, Is.EqualTo(FirstId));
        }

        [Test]
        public void WrongCredentialCannotRenameOrClaimStableIdentity()
        {
            var authority = Authority(World());
            string before = CampCodec.Encode(authority.State);
            Assert.That(() => authority.Join(FirstId, new string('x', 40), "冒用者"), Throws.Exception);
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before));
            Assert.That(_saveCount, Is.Zero);
        }

        [Test]
        public void NormalSaveRetainsCarryWhileRepeatedDisconnectDropsSmallItemsIntoOneBag()
        {
            var state = World();
            state.Items.Add(Item("heavy", ItemPlace.Hand, FirstId, ItemKind.HeavyOre));
            state.Items.Add(Item("pocket", ItemPlace.Pocket, FirstId));
            state.Items.Add(Item("mouth", ItemPlace.Mouth, FirstId));
            state.Items.Add(Item("safe", ItemPlace.Cabinet, FirstId));
            state.Items.Add(Item("bet", ItemPlace.Escrow, FirstId, ItemKind.RawStone));
            state.Items.Add(Item("bet-other", ItemPlace.Escrow, SecondId, ItemKind.RawStone));
            state.Items.Add(Item("vehicle", ItemPlace.Vehicle, FirstId));
            state.Items.Single(i => i.Id == "safe").ContainerId = "personal-cabinet";
            string wagerId = Guid.NewGuid().ToString("N");
            state.Items.Single(i => i.Id == "bet").ContainerId = "wager:" + wagerId;
            state.Items.Single(i => i.Id == "bet").HiddenValue = 50;
            state.Items.Single(i => i.Id == "bet-other").ContainerId = "wager:" + wagerId;
            state.Items.Single(i => i.Id == "bet-other").HiddenValue = 30;
            state.Items.Single(i => i.Id == "vehicle").ContainerId = "camp-cart";
            state.Wager = new CampWager
            {
                Id = wagerId, PlayerA = FirstId, PlayerB = SecondId, StoneA = "bet", StoneB = "bet-other",
                Version = 1, Phase = WagerPhase.Locked, Stake = 5, ConfirmedA = true, ConfirmedB = true,
                ValueA = 50, ValueB = 30
            };
            var authority = Authority(state);
            string before = Assets(authority.State);

            authority.Save();

            Assert.That(Assets(authority.State), Is.EqualTo(before));
            Assert.That(Assets(_saved), Is.EqualTo(before));
            authority.Disconnect(FirstId);
            var dropped = authority.State.Items.Where(i => i.Id == "pocket" || i.Id == "mouth").ToArray();
            Assert.That(dropped.All(i => i.Place == ItemPlace.Ground && i.OwnerId == ""), Is.True);
            Assert.That(dropped.Select(i => i.ContainerId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(dropped[0].ContainerId, Does.StartWith("offline-"));
            Assert.That(dropped.All(i => i.Version == 1), Is.True);
            var heavy = authority.State.Items.Single(i => i.Id == "heavy");
            Assert.That(heavy.Place, Is.EqualTo(ItemPlace.Ground));
            Assert.That(heavy.OwnerId, Is.Empty);
            Assert.That(heavy.ContainerId, Is.Empty);
            foreach (string safeId in new[] { "safe", "bet", "bet-other", "vehicle" })
            {
                Assert.That(JsonUtility.ToJson(authority.State.Items.Single(i => i.Id == safeId)),
                    Is.EqualTo(JsonUtility.ToJson(state.Items.Single(i => i.Id == safeId))));
            }
            Assert.That(JsonUtility.ToJson(Player(authority, FirstId)),
                Is.EqualTo(JsonUtility.ToJson(state.Players.Single(p => p.Id == FirstId))));
            string once = Assets(authority.State);
            authority.Disconnect(FirstId);
            Assert.That(Assets(authority.State), Is.EqualTo(once));
            Assert.That(authority.State.Items.Count, Is.EqualTo(7));
            Assert.That(Assets(_saved), Is.EqualTo(once));
        }

        [TestCase("duplicate-id")]
        [TestCase("two-hands")]
        [TestCase("two-mouths")]
        [TestCase("unknown-owner")]
        public void InvalidItemOwnershipCannotBeCommitted(string corruption)
        {
            var authority = Authority(World());
            var invalid = CampCodec.Clone(authority.State);
            if (corruption == "duplicate-id")
            {
                invalid.Items.Add(Item("duplicate", ItemPlace.Ground));
                invalid.Items.Add(Item("duplicate", ItemPlace.Pocket, FirstId));
            }
            else if (corruption == "unknown-owner")
                invalid.Items.Add(Item("orphan", ItemPlace.Cabinet, "missing-player"));
            else
            {
                var place = corruption == "two-hands" ? ItemPlace.Hand : ItemPlace.Mouth;
                invalid.Items.Add(Item("first", place, FirstId));
                invalid.Items.Add(Item("second", place, FirstId));
            }
            string before = CampCodec.Encode(authority.State);
            Assert.That(() => CampAuthority.Validate(invalid), Throws.Exception);
            Assert.That(() => authority.Commit(invalid), Throws.Exception);
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before));
            Assert.That(_saveCount, Is.Zero);
        }

        [TestCase("null-receipt")]
        [TestCase("missing-result")]
        [TestCase("duplicate-receipt-key")]
        [TestCase("negative-hidden-value")]
        public void InvalidReceiptOrNegativeRawValueCannotEnterCommittedState(string corruption)
        {
            var authority = Authority(World());
            var invalid = CampCodec.Clone(authority.State);
            var receipt = new CampReceipt
            {
                Key = FirstId + ":test-receipt", Fingerprint = CampAuthority.Hash("test receipt fingerprint"),
                Result = new CampResult { RequestId = "test-receipt", Success = true, Code = "ok", Message = "recorded" }
            };
            if (corruption == "null-receipt") invalid.Receipts.Add(null);
            else if (corruption == "missing-result")
            {
                receipt.Result = null;
                invalid.Receipts.Add(receipt);
            }
            else if (corruption == "duplicate-receipt-key")
            {
                invalid.Receipts.Add(receipt);
                invalid.Receipts.Add(new CampReceipt
                {
                    Key = receipt.Key, Fingerprint = CampAuthority.Hash("conflicting receipt fingerprint"),
                    Result = new CampResult { RequestId = "test-receipt", Success = true, Code = "ok", Message = "conflicting record" }
                });
            }
            else
            {
                var raw = Item("invalid-raw-value", ItemPlace.Hand, FirstId, ItemKind.RawStone);
                raw.RevealStage = 2;
                raw.HiddenValue = -50;
                invalid.Items.Add(raw);
            }
            string before = CampCodec.Encode(authority.State);

            Assert.That(() => CampAuthority.Validate(invalid), Throws.Exception);
            Assert.That(() => authority.Commit(invalid), Throws.Exception);

            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before));
            Assert.That(_saved, Is.Null);
            Assert.That(_saveCount, Is.Zero);
        }

        private CampAuthority Authority(CampState state, CampRules rules = null) =>
            new CampAuthority(state, rules ?? new CampRules(), Persist);

        private void Persist(CampState next)
        {
            if (_failSave) throw new IOException("Injected test-only save failure.");
            _saved = CampCodec.Clone(next);
            _saveCount++;
        }

        private static CampState World()
        {
            var state = CampAuthority.NewWorld("isolated authority test", 12345);
            state.Players.Add(new CampPlayer { Id = FirstId, Name = "甲", CredentialHash = CampAuthority.Hash(Credential), Balance = 100, Position = Vector3.zero });
            state.Players.Add(new CampPlayer { Id = SecondId, Name = "乙", CredentialHash = CampAuthority.Hash(Credential), Balance = 20, Position = Vector3.zero });
            return state;
        }

        private static CampItem Item(string id, ItemPlace place, string owner = "", ItemKind kind = ItemKind.Stone) =>
            new CampItem { Id = id, Place = place, OwnerId = owner, Kind = kind, Position = Vector3.forward };

        private static CampPlayer Player(CampAuthority authority, string id) => authority.State.Players.Single(p => p.Id == id);
        private static CampCommand Command(string request, string action, string target) =>
            new CampCommand { RequestId = request, Action = action, TargetId = target, ExpectedVersion = 0 };
        private static CampCommand Transfer(string request, long amount) =>
            new CampCommand { RequestId = request, Action = "transfer", OtherPlayerId = SecondId, Amount = amount };

        private static string Assets(CampState state)
        {
            // A rejected rule command may durably record its failure receipt.
            // Asset atomicity excludes only that audit data and snapshot metadata.
            var copy = CampCodec.Clone(state);
            copy.Receipts.Clear();
            copy.Revision = 0;
            copy.SavedUtc = null;
            return CampCodec.Encode(copy);
        }
    }
}
