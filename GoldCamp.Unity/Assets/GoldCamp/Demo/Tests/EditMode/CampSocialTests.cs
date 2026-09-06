using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    // Normal authority commands exercise the social rules. Online membership,
    // position, clock and storage faults are explicit host inputs, not network proof.
    public sealed class CampSocialTests
    {
        private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
        private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb2";
        private const string C = "ccccccccccccccccccccccccccccccc3";
        private const string RawA = "social-raw-a";
        private const string RawB = "social-raw-b";
        private const string SpareA = "social-raw-a-spare";
        private readonly HashSet<string> _online = new HashSet<string>();
        private CampState _saved;
        private bool _failSave;
        private bool _clearSight;
        private bool _freePlacement;
        private int _request;

        [SetUp]
        public void SetUp()
        {
            _online.Clear();
            _online.UnionWith(new[] { A, B, C });
            _saved = null;
            _failSave = false;
            _clearSight = true;
            _freePlacement = true;
            _request = 0;
        }

        [Test]
        public void AbsentWagerSurvivesRepeatedCodecRoundTripsAndOrdinaryCommandPersists()
        {
            var authority = Create(false);
            var state = authority.State;
            Assert.That(state.Wager, Is.Null);
            for (int round = 0; round < 4; round++)
            {
                state = CampCodec.Decode(CampCodec.Encode(state));
                Assert.That(state.Wager, Is.Null, "Decode must not materialize an empty wager.");
                state = CampCodec.Clone(state);
                Assert.That(state.Wager, Is.Null, "Clone must preserve absence of a wager.");
                Assert.DoesNotThrow(() => CampAuthority.Validate(state));
            }
            authority = Authority(state, authority.Rules);
            var transfer = Command("transfer");
            transfer.OtherPlayerId = B;
            transfer.Amount = 25;

            AssertSuccess(authority.Execute(A, transfer));

            Assert.That(authority.State.Wager, Is.Null);
            Assert.That(_saved.Wager, Is.Null);
            Assert.That(Player(authority, A).Balance, Is.EqualTo(975));
            Assert.That(Player(authority, B).Balance, Is.EqualTo(1025));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(3000));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(CampCodec.Encode(authority.State)));
        }

        [Test]
        public void RawTruthAndPrivateClueSurviveWindowTransferCabinetAndReloadWithoutRerolling()
        {
            var authority = Create(true, 734123, 168321);
            Move(authority, A, CampMap.CutterPoint);
            Move(authority, B, CampMap.CutterPoint + Vector3.right);
            string truth = RawTruth(Ore(authority, RawA));
            var firstInspection = ItemCommand(authority, A, "inspect", RawA);
            var secondInspection = ItemCommand(authority, A, "inspect", RawA);
            AssertSuccess(firstInspection);
            AssertSuccess(secondInspection);
            Assert.That(secondInspection.Message, Is.EqualTo(firstInspection.Message));
            Assert.That(Ore(authority, RawA).InspectedBy.Count(id => id == A), Is.EqualTo(1));
            Assert.That(Player(authority, A).Balance, Is.EqualTo(1000));

            var own = View(authority, A).Items.Single(i => i.Id == RawA);
            var other = View(authority, B).Items.Single(i => i.Id == RawA);
            Assert.That(own.OwnClue, Is.EqualTo(CampProjection.RawClue(2)));
            Assert.That(other.OwnClue, Is.Empty);
            Assert.That(other.WasInspected, Is.True);
            Assert.That(other.WindowBand, Is.Zero);
            Assert.That(other.PublicCore, Is.Empty);
            AssertPrivateRawJson(authority, B, 734123, "private-core-a");

            var window = Command("window", RawA, Ore(authority, RawA).Version);
            AssertSuccess(authority.Execute(A, window));
            Assert.That(Player(authority, A).Balance, Is.EqualTo(1000 - authority.Rules.WindowPrice));
            Assert.That(Ore(authority, RawA).RevealStage, Is.EqualTo(1));
            Assert.That(View(authority, B).Items.Single(i => i.Id == RawA).WindowBand, Is.EqualTo(3));
            Assert.That(View(authority, B).Items.Single(i => i.Id == RawA).PublicCore, Is.Empty);
            string windowed = CampCodec.Encode(authority.State);
            AssertSuccess(authority.Execute(A, window));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(windowed));

            AssertSuccess(ItemCommand(authority, B, "pocket", RawB));
            AssertSuccess(ItemCommand(authority, A, "drop", RawA, Vector3.forward));
            AssertSuccess(ItemCommand(authority, B, "pickup", RawA));
            AssertSuccess(ItemCommand(authority, B, "inspect", RawA));
            Assert.That(View(authority, B).Items.Single(i => i.Id == RawA).OwnClue, Is.EqualTo(own.OwnClue));
            Move(authority, B, CampMap.CabinetPoint(1));
            AssertSuccess(ItemCommand(authority, B, "cabinet_store", RawA));
            authority = Reload(authority);
            authority.RecoverSocial();
            Assert.That(RawTruth(Ore(authority, RawA)), Is.EqualTo(truth));
            Assert.That(Ore(authority, RawA).OwnerId, Is.EqualTo(B));
            Assert.That(Ore(authority, RawA).Place, Is.EqualTo(ItemPlace.Cabinet));
            AssertSuccess(ItemCommand(authority, B, "cabinet_take", RawA));
            Move(authority, B, CampMap.CutterPoint);
            var open = Command("open", RawA, Ore(authority, RawA).Version);
            AssertSuccess(authority.Execute(B, open));
            Assert.That(Ore(authority, RawA).RevealStage, Is.EqualTo(2));
            Assert.That(RawTruth(Ore(authority, RawA)), Is.EqualTo(truth));
            Assert.That(authority.Price(Ore(authority, RawA)), Is.EqualTo(734123));
            Assert.That(Player(authority, B).Balance, Is.EqualTo(1000 - authority.Rules.OpenPrice));
            string opened = CampCodec.Encode(authority.State);
            AssertSuccess(authority.Execute(B, open));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(opened));
            AssertUniqueItems(authority.State);
        }

        [TestCase("stake")]
        [TestCase("stone")]
        public void ChangedWagerTermsClearConfirmationsAndDraftCancellationPreservesAllAssets(string change)
        {
            var authority = Create();
            string originalItems = Items(authority.State);
            long originalMoney = TotalMoney(authority.State);
            PrepareDraft(authority, 20);
            int oldVersion = authority.State.Wager.Version;
            AssertSuccess(Confirm(authority, A));
            Assert.That(authority.State.Wager.ConfirmedA, Is.True);
            if (change == "stake") AssertSuccess(Offer(authority, B, RawB, A, 25));
            else AssertSuccess(Offer(authority, A, SpareA, B, 20));

            Assert.That(authority.State.Wager.ConfirmedA, Is.False);
            Assert.That(authority.State.Wager.ConfirmedB, Is.False);
            Assert.That(authority.State.Wager.Version, Is.GreaterThan(oldVersion));
            Assert.That(authority.Execute(A, Command("wager_confirm", authority.State.Wager.Id, oldVersion)).Success, Is.False);
            AssertSuccess(authority.Execute(A, Command("wager_cancel", authority.State.Wager.Id, authority.State.Wager.Version)));
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Cancelled));
            Assert.That(Items(authority.State), Is.EqualTo(originalItems));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(originalMoney));
            Assert.That(authority.State.Items.Any(i => i.Place == ItemPlace.Escrow), Is.False);
            var restarted = Reload(authority);
            string cancelled = CampCodec.Encode(restarted.State);
            restarted.RecoverSocial();
            Assert.That(CampCodec.Encode(restarted.State), Is.EqualTo(cancelled));
        }

        [Test]
        public void DraftDisconnectCancelsWithoutChargingStakeOrTransferringTheOtherPlayersStone()
        {
            var authority = Create();
            PrepareDraft(authority, 20);
            AssertSuccess(Confirm(authority, A));
            long originalMoney = TotalMoney(authority.State);
            string firstStone = JsonUtility.ToJson(Ore(authority, RawA));
            Vector3 departedPosition = Player(authority, B).Position;
            _online.Remove(B);

            authority.Disconnect(B);

            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Cancelled));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(originalMoney));
            Assert.That(JsonUtility.ToJson(Ore(authority, RawA)), Is.EqualTo(firstStone));
            Assert.That(Ore(authority, RawB).Place, Is.EqualTo(ItemPlace.Ground));
            Assert.That(Ore(authority, RawB).Position, Is.EqualTo(departedPosition + Vector3.up * .5f));
            Assert.That(authority.State.Items.Any(i => i.Place == ItemPlace.Escrow), Is.False);
            AssertUniqueItems(authority.State);
        }

        [Test]
        public void ReloadingUnconfirmedDraftCancelsWithoutChargingOrEscrowingAnyAsset()
        {
            var authority = Create();
            PrepareDraft(authority, 20);
            AssertSuccess(Confirm(authority, A));
            string originalItems = Items(authority.State);
            long originalMoney = TotalMoney(authority.State);
            authority = Reload(authority);
            authority.RecoverSocial();
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Cancelled));
            Assert.That(Items(authority.State), Is.EqualTo(originalItems));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(originalMoney));
            string recovered = CampCodec.Encode(authority.State);
            authority.RecoverSocial();
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(recovered));
        }

        [TestCase("inspect")]
        [TestCase("open")]
        [TestCase("sell")]
        public void ChangingOfferedStoneInvalidatesConsentWithoutUndoingValidProcessing(string action)
        {
            var authority = Create();
            PrepareDraft(authority, 20);
            AssertSuccess(Confirm(authority, B));
            Move(authority, A, action == "sell" ? CampAuthority.SalePoint : CampMap.CutterPoint);
            AssertSuccess(ItemCommand(authority, A, action, RawA));
            Move(authority, A, CampMap.WagerPoint);
            string processedItems = Items(authority.State);
            long processedMoney = TotalMoney(authority.State);

            var result = Confirm(authority, A);

            Assert.That(result.Success, Is.False);
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Draft));
            Assert.That(authority.State.Wager.ConfirmedA, Is.False);
            Assert.That(authority.State.Wager.ConfirmedB, Is.False);
            Assert.That(Items(authority.State), Is.EqualTo(processedItems));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(processedMoney));
            Assert.That(authority.State.Items.Any(i => i.Place == ItemPlace.Escrow), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LockedDisconnectAndReloadSettleExactlyOnceWithOriginalStoneOwners(bool tie)
        {
            var authority = Create(true, 170, tie ? 170 : 70);
            long originalMoney = TotalMoney(authority.State);
            PrepareDraft(authority, 20);
            AssertSuccess(Confirm(authority, A));
            var lockCommand = Command("wager_confirm", authority.State.Wager.Id, authority.State.Wager.Version);
            AssertSuccess(authority.Execute(B, lockCommand));
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Locked));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(originalMoney));
            Assert.That(Player(authority, A).Balance, Is.EqualTo(980));
            Assert.That(Player(authority, B).Balance, Is.EqualTo(980));
            Assert.That(Ore(authority, RawA).Place, Is.EqualTo(ItemPlace.Escrow));
            Assert.That(Ore(authority, RawB).Place, Is.EqualTo(ItemPlace.Escrow));
            foreach (string viewer in new[] { A, B, C })
            {
                var projected = View(authority, viewer);
                Assert.That(projected.Wager.ValueA, Is.Zero);
                Assert.That(projected.Wager.ValueB, Is.Zero);
                Assert.That(projected.Wager.WinnerId, Is.Empty);
                AssertPrivateRawJson(authority, viewer, 170, "private-core-a", false);
            }
            _online.Remove(B);
            authority.Disconnect(B);
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Locked));
            Assert.That(Ore(authority, RawB).Place, Is.EqualTo(ItemPlace.Escrow));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(originalMoney));

            authority = Reload(authority);
            authority.RecoverSocial();

            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Settled));
            Assert.That(Player(authority, A).Balance, Is.EqualTo(tie ? 1000 : 1020));
            Assert.That(Player(authority, B).Balance, Is.EqualTo(tie ? 1000 : 980));
            Assert.That(authority.State.Wager.WinnerId, Is.EqualTo(tie ? "" : A));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(originalMoney));
            foreach (var pair in new[] { new[] { RawA, A }, new[] { RawB, B } })
            {
                var ore = Ore(authority, pair[0]);
                Assert.That(ore.OwnerId, Is.EqualTo(pair[1]));
                Assert.That(ore.Place, Is.EqualTo(ItemPlace.Cabinet));
                Assert.That(ore.ContainerId, Is.EqualTo("cabinet:" + pair[1]));
                Assert.That(ore.RevealStage, Is.EqualTo(2));
            }
            Assert.That(authority.State.Wager.ValueA, Is.EqualTo(authority.Price(Ore(authority, RawA))));
            Assert.That(authority.State.Wager.ValueB, Is.EqualTo(authority.Price(Ore(authority, RawB))));
            Assert.That(View(authority, C).Wager.ValueA, Is.EqualTo(authority.State.Wager.ValueA));
            string once = CampCodec.Encode(authority.State);
            authority.RecoverSocial();
            authority.TickWorld(_online);
            AssertSuccess(authority.Execute(B, lockCommand));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(once));
            AssertUniqueItems(authority.State);
        }

        [Test]
        public void LockedStoneCannotBeSoldOpenedWithdrawnOrCancelled()
        {
            var authority = Create();
            LockWager(authority, 20);
            foreach (string action in new[] { "sell", "window", "open", "pickup" })
            {
                Move(authority, A, action == "sell" ? CampAuthority.SalePoint : CampMap.CutterPoint);
                string before = Assets(authority.State);
                Assert.That(ItemCommand(authority, A, action, RawA).Success, Is.False, action);
                Assert.That(Assets(authority.State), Is.EqualTo(before), action);
            }
            Move(authority, A, CampMap.WagerPoint);
            string locked = Assets(authority.State);
            Assert.That(authority.Execute(A, Command("wager_cancel", authority.State.Wager.Id, authority.State.Wager.Version)).Success, Is.False);
            Assert.That(Offer(authority, A, SpareA, B, 25).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(locked));
        }

        [Test]
        public void LockAndRecoveryStorageFailuresPublishNeitherPartialEscrowNorPartialPrize()
        {
            var authority = Create();
            PrepareDraft(authority, 20);
            AssertSuccess(Confirm(authority, A));
            var lastConfirmation = Command("wager_confirm", authority.State.Wager.Id, authority.State.Wager.Version);
            string beforeLock = CampCodec.Encode(authority.State);
            string durableBeforeLock = CampCodec.Encode(_saved);
            _failSave = true;
            var failedLock = authority.Execute(B, lastConfirmation);
            Assert.That(failedLock.Success, Is.False);
            Assert.That(failedLock.Code, Is.EqualTo("save_failed"));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(beforeLock));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(durableBeforeLock));

            _failSave = false;
            AssertSuccess(authority.Execute(B, lastConfirmation));
            authority = Reload(authority);
            string beforeSettlement = CampCodec.Encode(authority.State);
            string durableBeforeSettlement = CampCodec.Encode(_saved);
            _failSave = true;
            Assert.That(() => authority.RecoverSocial(), Throws.TypeOf<IOException>());
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(beforeSettlement));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(durableBeforeSettlement));
            _failSave = false;
            authority.RecoverSocial();
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Settled));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(3000));
            AssertUniqueItems(authority.State);
        }

        [Test]
        public void LockedPrizeReserveBlocksExcessIncomingMoneyAndStillAllowsExactLimitSettlement()
        {
            var authority = Create();
            Mutate(authority, state =>
            {
                state.Players.Single(p => p.Id == A).Balance = authority.Rules.MoneyLimit - 30;
                state.Items.Add(new CampItem { Id = "reserve-sale-gold", Kind = ItemKind.Gold, Place = ItemPlace.Pocket,
                    OwnerId = A, Position = CampMap.WagerPoint });
            });
            long conserved = TotalMoney(authority.State);
            LockWager(authority, 20);
            var tooMuch = Command("transfer");
            tooMuch.OtherPlayerId = A;
            tooMuch.Amount = 11;
            string before = Assets(authority.State);
            Assert.That(authority.Execute(C, tooMuch).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(before));
            Move(authority, A, CampAuthority.SalePoint);
            before = Assets(authority.State);
            Assert.That(ItemCommand(authority, A, "sell", "reserve-sale-gold").Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(before));

            var exact = Command("transfer");
            exact.OtherPlayerId = A;
            exact.Amount = 10;
            AssertSuccess(authority.Execute(C, exact));
            authority.RecoverSocial();
            Assert.That(Player(authority, A).Balance, Is.EqualTo(authority.Rules.MoneyLimit));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(conserved));
            Assert.That(Ore(authority, "reserve-sale-gold").Place, Is.EqualTo(ItemPlace.Pocket));
            AssertUniqueItems(authority.State);
        }

        [Test]
        public void FinalConfirmationRechecksFundsAndOnlineOpponentWithoutPartialLock()
        {
            var authority = Create();
            PrepareDraft(authority, 20);
            AssertSuccess(Confirm(authority, A));
            var spend = Command("transfer");
            spend.OtherPlayerId = C;
            spend.Amount = 990;
            AssertSuccess(authority.Execute(A, spend));
            string lowFunds = Assets(authority.State);
            Assert.That(Confirm(authority, B).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(lowFunds));
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Draft));

            var restoreFunds = Command("transfer");
            restoreFunds.OtherPlayerId = A;
            restoreFunds.Amount = 990;
            AssertSuccess(authority.Execute(C, restoreFunds));
            _online.Remove(A);
            string offline = Assets(authority.State);
            var offlineResult = Confirm(authority, B);
            Assert.That(offlineResult.Success, Is.False);
            Assert.That(offlineResult.Code, Is.EqualTo("opponent"));
            Assert.That(Assets(authority.State), Is.EqualTo(offline));
            Assert.That(authority.State.Items.Any(i => i.Place == ItemPlace.Escrow), Is.False);
        }

        [TestCase("window")]
        [TestCase("open")]
        public void RawProcessingPersistenceFailureDoesNotChargeOrReveal(string action)
        {
            var authority = Create();
            Move(authority, A, CampMap.CutterPoint);
            var command = Command(action, RawA, Ore(authority, RawA).Version);
            string before = CampCodec.Encode(authority.State);
            string durable = CampCodec.Encode(_saved);
            _failSave = true;

            var result = authority.Execute(A, command);

            Assert.That(result.Code, Is.EqualTo("save_failed"));
            Assert.That(result.Success, Is.False);
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(durable));
            _failSave = false;
            AssertSuccess(authority.Execute(A, command));
            Assert.That(Player(authority, A).Balance, Is.EqualTo(1000 - (action == "window" ? authority.Rules.WindowPrice : authority.Rules.OpenPrice)));
        }

        [TestCase("smoke")]
        [TestCase("drink")]
        public void MouthAndPerformanceConflictCanBeCancelledWithoutSwallowingOrDuplicatingGold(string performance)
        {
            var authority = Create(false);
            Mutate(authority, state => state.Items.Add(new CampItem { Id = "mouth-gold", Kind = ItemKind.Gold,
                Place = ItemPlace.Hand, OwnerId = A, Position = Player(authority, A).Position + Vector3.up }));
            AssertSuccess(ItemCommand(authority, A, "mouth", "mouth-gold"));
            Assert.That(Ore(authority, "mouth-gold").Place, Is.EqualTo(ItemPlace.Mouth));
            Assert.That(View(authority, B).Players.Single(p => p.Id == A).CheeksFull, Is.True);
            Assert.That(View(authority, B).Items.Any(i => i.Id == "mouth-gold"), Is.False);
            string occupied = Assets(authority.State);
            Assert.That(authority.Execute(A, Command(performance)).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(occupied));

            _freePlacement = false;
            Assert.That(ItemCommand(authority, A, "spit", "mouth-gold", Vector3.forward).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(occupied));
            _freePlacement = true;
            AssertSuccess(ItemCommand(authority, A, "spit", "mouth-gold", Vector3.forward));
            Assert.That(Ore(authority, "mouth-gold").Place, Is.EqualTo(ItemPlace.Ground));
            Assert.That(Ore(authority, "mouth-gold").OwnerId, Is.Empty);
            string goldOnGround = JsonUtility.ToJson(Ore(authority, "mouth-gold"));
            AssertSuccess(authority.Execute(A, Command(performance)));
            var observed = View(authority, B).Players.Single(p => p.Id == A);
            Assert.That(observed.Action, Is.EqualTo(performance));
            Assert.That(observed.ActionUntil, Is.GreaterThan(authority.State.OnlineSeconds));
            AssertSuccess(authority.Execute(A, Command("cancel_action")));
            AssertActionCleared(Player(authority, A));
            Assert.That(JsonUtility.ToJson(Ore(authority, "mouth-gold")), Is.EqualTo(goldOnGround));
            Assert.That(TotalMoney(authority.State), Is.EqualTo(3000));
            AssertUniqueItems(authority.State);
        }

        [TestCase(ItemKind.HeavyOre, 1, false)]
        [TestCase(ItemKind.RawStone, 1, false)]
        [TestCase(ItemKind.Gold, 2, false)]
        [TestCase(ItemKind.Gold, 1, true)]
        public void MouthRejectsWrongSizeOrOccupiedSlotWithoutChangingEitherItem(ItemKind kind, int quantity, bool occupied)
        {
            var authority = Create(false);
            Mutate(authority, state =>
            {
                state.Items.Add(new CampItem { Id = "mouth-candidate", Kind = kind, Quantity = quantity,
                    OwnerId = A, Place = ItemPlace.Hand, Position = CampMap.WagerPoint + Vector3.up });
                if (occupied) state.Items.Add(new CampItem { Id = "existing-mouth-gold", Kind = ItemKind.Gold,
                    OwnerId = A, Place = ItemPlace.Mouth, Position = CampMap.WagerPoint + Vector3.up });
            });
            string before = Assets(authority.State);
            Assert.That(ItemCommand(authority, A, "mouth", "mouth-candidate").Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(before));
            AssertUniqueItems(authority.State);
        }

        [Test]
        public void ValidAttackDropsOnlyHandAndMouthAndProtectionPreventsRepeatedControl()
        {
            var authority = CombatCamp();
            Mutate(authority, state => state.Items.Add(new CampItem { Id = "victim-mouth-gold", Kind = ItemKind.Gold,
                Place = ItemPlace.Hand, OwnerId = B, Position = Player(authority, B).Position + Vector3.up }));
            AssertSuccess(ItemCommand(authority, B, "mouth", "victim-mouth-gold"));
            Mutate(authority, state => state.Items.Add(new CampItem { Id = "victim-hand-gold", Kind = ItemKind.Gold,
                Place = ItemPlace.Hand, OwnerId = B, Position = Player(authority, B).Position + Vector3.up }));
            var permanentBefore = CampCodec.Clone(authority.State);
            string pockets = VictimPockets(authority.State);
            double hitAt = authority.State.OnlineSeconds;

            AssertSuccess(PlayerCommand(authority, A, "attack", B));

            var victim = Player(authority, B);
            Assert.That(victim.StunnedUntil, Is.EqualTo(hitAt + authority.Rules.StunSeconds));
            Assert.That(victim.ProtectionUntil, Is.EqualTo(victim.StunnedUntil + authority.Rules.ProtectionSeconds));
            Assert.That(Ore(authority, "victim-hand-gold").Place, Is.EqualTo(ItemPlace.Ground));
            Assert.That(Ore(authority, "victim-mouth-gold").Place, Is.EqualTo(ItemPlace.Ground));
            Assert.That(Ore(authority, "victim-hand-gold").OwnerId, Is.Empty);
            Assert.That(Ore(authority, "victim-mouth-gold").OwnerId, Is.Empty);
            Assert.That(VictimPockets(authority.State), Is.EqualTo(pockets));
            AssertPermanentPlayerAssets(permanentBefore, authority.State);
            SetTime(authority, hitAt + authority.Rules.MineInterval + .1);
            string stunned = Assets(authority.State);
            Assert.That(PlayerCommand(authority, A, "attack", B).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(stunned));
            SetTime(authority, victim.StunnedUntil + .1);
            string protectedState = Assets(authority.State);
            Assert.That(PlayerCommand(authority, A, "attack", B).Success, Is.False);
            Assert.That(PlayerCommand(authority, A, "rob", B).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(protectedState));
            AssertUniqueItems(authority.State);
        }

        [Test]
        public void RobRequiresOneFullSecondAndTransfersOnlyTheFrozenPocketItemOnce()
        {
            var authority = CombatCamp();
            var permanentBefore = CampCodec.Clone(authority.State);
            var command = AimedCommand(authority, A, "rob", B);
            AssertSuccess(authority.Execute(A, command));
            double due = Player(authority, A).ActionUntil;
            Assert.That(due - authority.State.OnlineSeconds, Is.EqualTo(1d).Within(.0001));
            Assert.That(Player(authority, A).ActionItemId, Is.EqualTo("victim-pocket-a"));
            Assert.That(View(authority, B).Players.Single(p => p.Id == A).Action, Is.EqualTo("rob"));
            SetTime(authority, due - .5);
            authority.TickWorld(_online);
            Assert.That(Ore(authority, "victim-pocket-a").OwnerId, Is.EqualTo(B));
            Assert.That(Player(authority, A).Action, Is.EqualTo("rob"));
            SetTime(authority, due);
            authority.TickWorld(_online);

            Assert.That(Ore(authority, "victim-pocket-a").OwnerId, Is.EqualTo(A));
            Assert.That(Ore(authority, "victim-pocket-a").Place, Is.EqualTo(ItemPlace.Hand));
            Assert.That(authority.State.Items.Count(i => i.OwnerId == B && i.Place == ItemPlace.Pocket), Is.EqualTo(2));
            Assert.That(Ore(authority, "victim-pocket-b").OwnerId, Is.EqualTo(B));
            Assert.That(Ore(authority, "victim-pocket-c").OwnerId, Is.EqualTo(B));
            AssertActionCleared(Player(authority, A));
            AssertPermanentPlayerAssets(permanentBefore, authority.State);
            string once = CampCodec.Encode(authority.State);
            AssertSuccess(authority.Execute(A, command));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(once));
            string transferred = Items(authority.State);
            SetTime(authority, due + 1);
            authority.TickWorld(_online);
            Assert.That(Items(authority.State), Is.EqualTo(transferred));
            AssertUniqueItems(authority.State);
        }

        [Test]
        public void CompetingRobActionsTransferOneFrozenItemAndLoserCannotSwitchToAnotherSlot()
        {
            var authority = CombatCamp();
            AssertSuccess(PlayerCommand(authority, A, "rob", B));
            AssertSuccess(PlayerCommand(authority, C, "rob", B));
            Assert.That(Player(authority, A).ActionItemId, Is.EqualTo(Player(authority, C).ActionItemId));
            SetTime(authority, Player(authority, A).ActionUntil);
            authority.TickWorld(_online);
            Assert.That(authority.State.Items.Count(i => i.Id.StartsWith("victim-pocket-", StringComparison.Ordinal) && i.OwnerId != B), Is.EqualTo(1));
            Assert.That(Ore(authority, "victim-pocket-b").OwnerId, Is.EqualTo(B));
            Assert.That(Ore(authority, "victim-pocket-c").OwnerId, Is.EqualTo(B));
            AssertActionCleared(Player(authority, A));
            AssertActionCleared(Player(authority, C));
            AssertUniqueItems(authority.State);
        }

        [TestCase("cancel")]
        [TestCase("distance")]
        [TestCase("offline")]
        [TestCase("item-version")]
        [TestCase("blocked")]
        [TestCase("robber-hit")]
        public void InterruptedRobCannotStealOrSubstituteAnotherPocketItem(string interruption)
        {
            var authority = CombatCamp();
            AssertSuccess(PlayerCommand(authority, A, "rob", B));
            double due = Player(authority, A).ActionUntil;
            if (interruption == "cancel") AssertSuccess(authority.Execute(A, Command("cancel_action")));
            else if (interruption == "distance") Move(authority, B, Vector3.forward * 5);
            else if (interruption == "offline") _online.Remove(B);
            else if (interruption == "item-version") AssertSuccess(ItemCommand(authority, B, "unpocket", "victim-pocket-a"));
            else if (interruption == "blocked") _clearSight = false;
            else AssertSuccess(PlayerCommand(authority, B, "attack", A));
            string itemsAfterInterruption = Items(authority.State);
            SetTime(authority, due + .1);
            authority.TickWorld(_online);

            AssertActionCleared(Player(authority, A));
            Assert.That(Items(authority.State), Is.EqualTo(itemsAfterInterruption));
            Assert.That(authority.State.Items.Where(i => i.Id.StartsWith("victim-pocket-", StringComparison.Ordinal)).All(i => i.OwnerId == B), Is.True);
            Assert.That(authority.State.Items.Any(i => i.OwnerId == A && i.Place == ItemPlace.Hand), Is.False);
            Assert.That(TotalMoney(authority.State), Is.EqualTo(3000));
            AssertUniqueItems(authority.State);
        }

        [TestCase("attack")]
        [TestCase("rob-complete")]
        public void ViolencePersistenceFailurePublishesNoStunDropOrStolenItem(string operation)
        {
            var authority = CombatCamp();
            if (operation == "rob-complete")
            {
                AssertSuccess(PlayerCommand(authority, A, "rob", B));
                SetTime(authority, Player(authority, A).ActionUntil);
            }
            else Mutate(authority, state => state.Items.Add(new CampItem { Id = "attack-failure-hand", Kind = ItemKind.Gold,
                Place = ItemPlace.Hand, OwnerId = B, Position = Vector3.forward + Vector3.up }));
            string before = CampCodec.Encode(authority.State);
            string durable = CampCodec.Encode(_saved);
            _failSave = true;

            if (operation == "rob-complete") Assert.That(() => authority.TickWorld(_online), Throws.TypeOf<IOException>());
            else
            {
                var result = PlayerCommand(authority, A, "attack", B);
                Assert.That(result.Success, Is.False);
                Assert.That(result.Code, Is.EqualTo("save_failed"));
            }
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(durable));
        }

        [Test]
        public void OnlyHostCanDisablePvpAndDisabledPvpPreservesNormalProcessing()
        {
            var authority = CombatCamp();
            var disable = Command("pvp");
            disable.Amount = 0;
            Assert.That(authority.Execute(B, disable).Success, Is.False);
            Assert.That(authority.State.PvpEnabled, Is.True);
            AssertSuccess(authority.Execute(A, disable));
            Assert.That(authority.State.PvpEnabled, Is.False);
            string before = Assets(authority.State);
            Assert.That(PlayerCommand(authority, A, "attack", B).Success, Is.False);
            Assert.That(PlayerCommand(authority, A, "rob", B).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(before));
            Mutate(authority, state => state.Items.Add(Raw("pvp-off-raw", A, 15, "iron", 2, 2, ItemPlace.Hand)));
            Move(authority, A, CampMap.CutterPoint);
            AssertSuccess(ItemCommand(authority, A, "open", "pvp-off-raw"));
            Assert.That(Ore(authority, "pvp-off-raw").RevealStage, Is.EqualTo(2));
            Assert.That(Player(authority, A).Balance, Is.EqualTo(1000 - authority.Rules.OpenPrice));
        }

        private CampAuthority Create(bool rawStones = true, int valueA = 170, int valueB = 70)
        {
            var state = CampAuthority.NewWorld("social rules test", 86420);
            state.OnlineSeconds = 10;
            int index = 0;
            foreach (string id in new[] { A, B, C })
            {
                state.Players.Add(new CampPlayer { Id = id, Name = "miner-" + index,
                    CredentialHash = CampAuthority.Hash("test-only social credential " + id), Balance = 1000,
                    Position = CampMap.WagerPoint + Vector3.right * (index - 1), ColorIndex = index,
                    ToolLevel = 2, DetectorLevel = 1, Durability = 84 });
                state.Items.Add(new CampItem { Id = "safe-" + id, Kind = ItemKind.Gold, Place = ItemPlace.Cabinet,
                    OwnerId = id, ContainerId = "cabinet:" + id, Position = CampMap.CabinetPoint(index) });
                index++;
            }
            if (rawStones)
            {
                state.Items.Add(Raw(RawA, A, valueA, "private-core-a", 2, 3, ItemPlace.Hand));
                state.Items.Add(Raw(RawB, B, valueB, "private-core-b", 1, 1, ItemPlace.Hand));
                state.Items.Add(Raw(SpareA, A, 40, "private-spare-core", 1, 2, ItemPlace.Pocket));
            }
            return Authority(state, new CampRules());
        }

        private CampAuthority CombatCamp()
        {
            var authority = Create(false);
            Mutate(authority, state =>
            {
                state.Players.Single(p => p.Id == A).Position = Vector3.zero;
                state.Players.Single(p => p.Id == B).Position = Vector3.forward * 1.1f;
                state.Players.Single(p => p.Id == C).Position = Vector3.right;
                foreach (string suffix in new[] { "a", "b", "c" })
                    state.Items.Add(new CampItem { Id = "victim-pocket-" + suffix, Kind = ItemKind.Stone,
                        Place = ItemPlace.Pocket, OwnerId = B, Position = Vector3.forward });
            });
            return authority;
        }

        private CampAuthority Authority(CampState state, CampRules rules) => new CampAuthority(state, rules, Persist,
            (from, to) => _clearSight, (position, radius) => _freePlacement, id => _online.Contains(id), id => id == A);
        private CampAuthority Reload(CampAuthority authority) => Authority(CampCodec.Clone(_saved), authority.Rules);
        private void Persist(CampState state)
        {
            if (_failSave) throw new IOException("Injected social persistence failure.");
            _saved = CampCodec.Clone(state);
        }
        private static CampItem Raw(string id, string owner, int value, string core, int clue, int window, ItemPlace place) =>
            new CampItem { Id = id, OwnerId = owner, Kind = ItemKind.RawStone, Place = place, Position = CampMap.WagerPoint + Vector3.up,
                HiddenValue = value, CoreKind = core, RawClueBand = clue, RawWindowBand = window };
        private CampCommand Command(string action, string target = "", int version = 0) =>
            new CampCommand { RequestId = "social-test-" + ++_request, Action = action, TargetId = target, ExpectedVersion = version };
        private CampCommand AimedCommand(CampAuthority authority, string actor, string action, string target)
        {
            var command = Command(action, target);
            command.Direction = (Player(authority, target).Position + Vector3.up - (Player(authority, actor).Position + Vector3.up * 1.6f)).normalized;
            return command;
        }
        private CampResult PlayerCommand(CampAuthority authority, string actor, string action, string target) =>
            authority.Execute(actor, AimedCommand(authority, actor, action, target));
        private CampResult ItemCommand(CampAuthority authority, string actor, string action, string itemId, Vector3 direction = default)
        {
            var command = Command(action, itemId, Ore(authority, itemId).Version);
            command.Direction = direction;
            return authority.Execute(actor, command);
        }
        private CampResult Offer(CampAuthority authority, string actor, string itemId, string opponent, long stake)
        {
            var command = Command("wager_offer", itemId, authority.State.Wager?.Version ?? 0);
            command.OtherPlayerId = opponent;
            command.Amount = stake;
            return authority.Execute(actor, command);
        }
        private void PrepareDraft(CampAuthority authority, long stake)
        {
            AssertSuccess(Offer(authority, A, RawA, B, stake));
            AssertSuccess(Offer(authority, B, RawB, A, stake));
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Draft));
        }
        private CampResult Confirm(CampAuthority authority, string actor) =>
            authority.Execute(actor, Command("wager_confirm", authority.State.Wager.Id, authority.State.Wager.Version));
        private void LockWager(CampAuthority authority, long stake)
        {
            PrepareDraft(authority, stake);
            AssertSuccess(Confirm(authority, A));
            AssertSuccess(Confirm(authority, B));
            Assert.That(authority.State.Wager.Phase, Is.EqualTo(WagerPhase.Locked));
        }
        private static CampPlayer Player(CampAuthority authority, string id) => authority.State.Players.Single(p => p.Id == id);
        private static CampItem Ore(CampAuthority authority, string id) => authority.State.Items.Single(i => i.Id == id);
        private CampView View(CampAuthority authority, string viewer) =>
            JsonUtility.FromJson<CampView>(JsonUtility.ToJson(CampProjection.ForPlayer(authority, viewer, _online)));
        private static void Move(CampAuthority authority, string id, Vector3 position) => Mutate(authority, state =>
        {
            state.Players.Single(p => p.Id == id).Position = position;
        });
        private static void SetTime(CampAuthority authority, double time) => Mutate(authority, state => state.OnlineSeconds = time);
        private static void Mutate(CampAuthority authority, Action<CampState> mutation)
        {
            var next = CampCodec.Clone(authority.State);
            mutation(next);
            authority.Commit(next);
        }
        private static long TotalMoney(CampState state) => state.Players.Sum(p => p.Balance) +
            (state.Wager != null && state.Wager.Phase == WagerPhase.Locked ? checked(state.Wager.Stake * 2) : 0);
        private static string RawTruth(CampItem item) =>
            item.HiddenValue + "|" + item.CoreKind + "|" + item.RawClueBand + "|" + item.RawWindowBand;
        private static string Items(CampState state) => string.Join("\n", state.Items.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => JsonUtility.ToJson(i)));
        private static string VictimPockets(CampState state) => string.Join("\n", state.Items
            .Where(i => i.Id.StartsWith("victim-pocket-", StringComparison.Ordinal)).OrderBy(i => i.Id).Select(i => JsonUtility.ToJson(i)));
        private static string Assets(CampState state)
        {
            var copy = CampCodec.Clone(state);
            copy.Receipts.Clear();
            copy.Revision = 0;
            copy.SavedUtc = null;
            return CampCodec.Encode(copy);
        }
        private static void AssertUniqueItems(CampState state)
        {
            Assert.That(state.Items.Select(i => i.Id).Distinct().Count(), Is.EqualTo(state.Items.Count));
            Assert.DoesNotThrow(() => CampAuthority.Validate(state));
        }
        private static void AssertActionCleared(CampPlayer player)
        {
            Assert.That(player.Action, Is.Empty);
            Assert.That(player.ActionUntil, Is.Zero);
            Assert.That(player.ActionTargetId, Is.Empty);
            Assert.That(player.ActionItemId, Is.Empty);
            Assert.That(player.ActionItemVersion, Is.Zero);
        }
        private static void AssertPermanentPlayerAssets(CampState before, CampState after)
        {
            foreach (var previous in before.Players)
            {
                var current = after.Players.Single(p => p.Id == previous.Id);
                Assert.That(current.Balance, Is.EqualTo(previous.Balance));
                Assert.That(current.ToolLevel, Is.EqualTo(previous.ToolLevel));
                Assert.That(current.DetectorLevel, Is.EqualTo(previous.DetectorLevel));
                Assert.That(current.Durability, Is.EqualTo(previous.Durability));
                Assert.That(current.LadderStock, Is.EqualTo(previous.LadderStock));
                Assert.That(current.CredentialHash, Is.EqualTo(previous.CredentialHash));
                Assert.That(JsonUtility.ToJson(after.Items.Single(i => i.Id == "safe-" + previous.Id)),
                    Is.EqualTo(JsonUtility.ToJson(before.Items.Single(i => i.Id == "safe-" + previous.Id))));
            }
        }
        private void AssertPrivateRawJson(CampAuthority authority, string viewer, int value, string core, bool checkNumericValue = true)
        {
            string json = JsonUtility.ToJson(CampProjection.ForPlayer(authority, viewer, _online));
            foreach (string field in new[] { "\"HiddenValue\"", "\"CoreKind\"", "\"InspectedBy\"", "\"CredentialHash\"" })
                Assert.That(json, Does.Not.Contain(field));
            Assert.That(json, Does.Not.Contain(core));
            if (checkNumericValue) Assert.That(json, Does.Not.Contain(value.ToString()));
            foreach (var player in authority.State.Players) Assert.That(json, Does.Not.Contain(player.CredentialHash));
        }
        private static void AssertSuccess(CampResult result) => Assert.That(result.Success, Is.True, result.Code + ": " + result.Message);
    }
}
