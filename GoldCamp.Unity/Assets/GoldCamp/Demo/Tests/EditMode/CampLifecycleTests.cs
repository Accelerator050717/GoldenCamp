using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    // TickWorld consumes the authoritative online clock. These tests advance that
    // input explicitly and verify state transitions, not frame timing or physics.
    public sealed class CampLifecycleTests
    {
        private const string MinerId = "88888888888888888888888888888888";
        private const string HelperId = "99999999999999999999999999999999";
        private const string MinerCredential = "lifecycle miner credential 0123456789";
        private static readonly string[] BothOnline = { MinerId, HelperId };
        private CampState _saved;
        private bool _failSave;
        private int _requestNumber;

        [SetUp]
        public void SetUp()
        {
            _saved = null;
            _failSave = false;
            _requestNumber = 0;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EscapeWaitsFourOnlineSecondsThenLeavesCargoAtOldPositionAndPreservesCamp(bool heavyHand)
        {
            var authority = CreateCamp(true, heavyHand);
            var preserved = CampCodec.Clone(authority.State);
            Vector3 oldPosition = Player(authority, MinerId).Position;
            string cargoBefore = Cargo(authority.State);
            double start = authority.State.OnlineSeconds;

            AssertSuccess(authority.Execute(MinerId, Command("escape")));
            double due = Player(authority, MinerId).EscapeAt;
            Assert.That(due - start, Is.EqualTo(4d).Within(.0001));
            Assert.That(Player(authority, MinerId).Position, Is.EqualTo(oldPosition));
            Assert.That(Cargo(authority.State), Is.EqualTo(cargoBefore));
            SetTime(authority, due - .5);
            authority.TickWorld(BothOnline);
            Assert.That(Player(authority, MinerId).Position, Is.EqualTo(oldPosition));
            Assert.That(Cargo(authority.State), Is.EqualTo(cargoBefore));

            SetTime(authority, due);
            authority.TickWorld(BothOnline);

            Assert.That(Player(authority, MinerId).Position, Is.EqualTo(CampAuthority.Spawn));
            Assert.That(Player(authority, MinerId).EscapeAt, Is.Zero);
            Assert.That(Player(authority, MinerId).ClimbingLadderId, Is.Empty);
            AssertDroppedCargo(authority.State, oldPosition, "escape-", heavyHand);
            AssertCampAssetsPreserved(preserved, authority.State);
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(CampCodec.Encode(authority.State)));
            string once = Cargo(authority.State);
            SetTime(authority, due + 1);
            authority.TickWorld(BothOnline);
            Assert.That(Cargo(authority.State), Is.EqualTo(once));
        }

        [Test]
        public void CancelledEscapeDoesNotMoveOrDropAssetsAfterOriginalDeadline()
        {
            var authority = CreateCamp(true);
            var before = CampCodec.Clone(authority.State);
            string cargoBefore = Cargo(authority.State);
            Vector3 oldPosition = Player(authority, MinerId).Position;
            AssertSuccess(authority.Execute(MinerId, Command("escape")));
            double originalDeadline = Player(authority, MinerId).EscapeAt;
            AssertSuccess(authority.Execute(MinerId, Command("cancel_escape")));

            SetTime(authority, originalDeadline + 1);
            authority.TickWorld(BothOnline);

            Assert.That(Player(authority, MinerId).Position, Is.EqualTo(oldPosition));
            Assert.That(Player(authority, MinerId).EscapeAt, Is.Zero);
            Assert.That(Cargo(authority.State), Is.EqualTo(cargoBefore));
            AssertCampAssetsPreserved(before, authority.State);
        }

        [TestCase("climb")]
        [TestCase("escape")]
        public void DisconnectCancelsTransientActivityAndReconnectCannotResumeEscapeOrOccupyLadder(string activity)
        {
            var authority = CreateCamp(true);
            var ladder = authority.State.Ladders.Single();
            if (activity == "climb")
            {
                AssertSuccess(authority.Execute(MinerId, Command("climb", ladder.Id, ladder.Version)));
                Assert.That(Player(authority, MinerId).ClimbingLadderId, Is.EqualTo(ladder.Id));
            }
            else
            {
                AssertSuccess(authority.Execute(MinerId, Command("escape")));
                Assert.That(Player(authority, MinerId).EscapeAt, Is.GreaterThan(authority.State.OnlineSeconds));
            }
            var before = CampCodec.Clone(authority.State);
            Vector3 oldPosition = Player(authority, MinerId).Position;
            double pastAnyEscapeDeadline = authority.State.OnlineSeconds + authority.Rules.EscapeSeconds + 1;

            authority.Disconnect(MinerId);

            var disconnected = Player(authority, MinerId);
            Assert.That(disconnected.ClimbingLadderId, Is.Empty);
            Assert.That(disconnected.EscapeAt, Is.Zero);
            Assert.That(disconnected.Action, Is.Empty);
            Assert.That(disconnected.ActionUntil, Is.Zero);
            AssertDroppedCargo(authority.State, oldPosition, "offline-", false);
            AssertCampAssetsPreserved(before, authority.State);
            string droppedOnce = Cargo(authority.State);
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(CampCodec.Encode(authority.State)));

            var restarted = new CampAuthority(CampCodec.Clone(_saved), authority.Rules, Persist);
            restarted.InitializeMining();
            restarted.Join(MinerId, MinerCredential, "重连矿工");
            Vector3 reconnectedPosition = Player(restarted, MinerId).Position;
            SetTime(restarted, pastAnyEscapeDeadline);
            restarted.TickWorld(BothOnline);

            Assert.That(Player(restarted, MinerId).Position, Is.EqualTo(reconnectedPosition));
            Assert.That(Player(restarted, MinerId).ClimbingLadderId, Is.Empty);
            Assert.That(Player(restarted, MinerId).EscapeAt, Is.Zero);
            Assert.That(Cargo(restarted.State), Is.EqualTo(droppedOnce));
            AssertCampAssetsPreserved(before, restarted.State);
            Move(restarted, HelperId, oldPosition);
            var currentLadder = restarted.State.Ladders.Single();
            AssertSuccess(restarted.Execute(HelperId, Command("remove_ladder", currentLadder.Id, currentLadder.Version)));
            Assert.That(restarted.State.Ladders, Is.Empty, "An offline or reconnected miner must not remain a ghost ladder occupant.");
            Assert.That(Cargo(restarted.State), Is.EqualTo(droppedOnce));
        }

        [Test]
        public void RiverRefreshUsesOnlineClockWaitsForNearbyPlayerAndNeverRevivesSoldIdOrDugPit()
        {
            var authority = CreateCamp(false);
            const string soldId = "river:0";
            Move(authority, MinerId, Ore(authority, soldId).Position);
            AssertSuccess(authority.Execute(MinerId, Command("pickup", soldId, Ore(authority, soldId).Version)));
            Move(authority, MinerId, CampAuthority.SalePoint);
            AssertSuccess(authority.Execute(MinerId, Command("sell", soldId, Ore(authority, soldId).Version)));
            var preserved = CampCodec.Clone(authority.State);
            string soldRecord = JsonUtility.ToJson(Ore(authority, soldId));
            int originalCount = authority.State.Items.Count;
            double due = authority.State.NextRiverSpawn;

            // Old wall-clock metadata alone must not advance a persisted online timer.
            var oldSnapshot = CampCodec.Clone(authority.State);
            oldSnapshot.SavedUtc = "2000-01-01T00:00:00.0000000Z";
            authority = new CampAuthority(oldSnapshot, authority.Rules, Persist);
            double unchangedOnlineClock = authority.State.OnlineSeconds;
            authority.TickWorld(Array.Empty<string>());
            Assert.That(authority.State.OnlineSeconds, Is.EqualTo(unchangedOnlineClock));
            Assert.That(authority.State.Items.Count, Is.EqualTo(originalCount));
            Assert.That(authority.State.NextRiverSpawn, Is.EqualTo(due));
            SetTime(authority, due - .5);
            authority.TickWorld(BothOnline);
            Assert.That(authority.State.Items.Count, Is.EqualTo(originalCount));

            Move(authority, MinerId, CampMap.RiverPoint);
            SetTime(authority, due);
            authority.TickWorld(BothOnline);
            Assert.That(authority.State.Items.Count, Is.EqualTo(originalCount));
            Assert.That(authority.State.NextRiverSpawn, Is.EqualTo(due));

            Move(authority, MinerId, CampAuthority.Spawn);
            SetTime(authority, due + .5);
            authority.TickWorld(BothOnline);

            Assert.That(authority.State.Items.Count, Is.EqualTo(originalCount + 1));
            var replacement = authority.State.Items.Single(i => i.Id.StartsWith(soldId + ":", StringComparison.Ordinal));
            Assert.That(replacement.Id, Is.Not.EqualTo(soldId));
            Assert.That(replacement.Kind, Is.EqualTo(ItemKind.Stone));
            Assert.That(replacement.Place, Is.EqualTo(ItemPlace.Ground));
            Assert.That(JsonUtility.ToJson(Ore(authority, soldId)), Is.EqualTo(soldRecord));
            Assert.That(authority.State.Items.Select(i => i.Id).Distinct().Count(), Is.EqualTo(authority.State.Items.Count));
            Assert.That(authority.State.NextRiverSpawn, Is.EqualTo(authority.State.OnlineSeconds + authority.Rules.RiverRespawnSeconds));
            AssertCampAssetsPreserved(preserved, authority.State);
            var restarted = new CampAuthority(CampCodec.Clone(_saved), authority.Rules, Persist);
            restarted.InitializeMining();
            Assert.That(restarted.State.Items.Count, Is.EqualTo(originalCount + 1));
            Assert.That(JsonUtility.ToJson(Ore(restarted, soldId)), Is.EqualTo(soldRecord));
            Assert.That(CellsAndLadders(restarted.State), Is.EqualTo(CellsAndLadders(preserved)));
        }

        [TestCase("far")]
        [TestCase("helper-offline")]
        [TestCase("carrier-offline")]
        public void DistantOrOfflineParticipantClearsAssistanceWithoutChangingOwnership(string reason)
        {
            var authority = CreateCamp(true, true);
            ConfigureAssistance(authority, reason == "far");
            var before = CampCodec.Clone(authority.State);
            var heldBefore = Ore(authority, "cargo-hand");
            int oldVersion = heldBefore.Version;
            Vector3 helperPosition = Player(authority, HelperId).Position;
            ICollection<string> online = reason == "helper-offline" ? new[] { MinerId }
                : reason == "carrier-offline" ? new[] { HelperId } : BothOnline;

            authority.TickWorld(online);

            var held = Ore(authority, "cargo-hand");
            Assert.That(held.AssistantId, Is.Empty);
            Assert.That(held.Version, Is.EqualTo(oldVersion + 1));
            Assert.That(held.OwnerId, Is.EqualTo(MinerId));
            Assert.That(held.Place, Is.EqualTo(ItemPlace.Hand));
            Assert.That(held.Position, Is.EqualTo(heldBefore.Position));
            Assert.That(Player(authority, HelperId).Position, Is.EqualTo(helperPosition));
            AssertCampAssetsPreserved(before, authority.State);
            Assert.That(_saved.Items.Single(i => i.Id == held.Id).AssistantId, Is.Empty);
            string cleared = Cargo(authority.State);
            SetTime(authority, authority.State.OnlineSeconds + 1);
            authority.TickWorld(online);
            Assert.That(Cargo(authority.State), Is.EqualTo(cleared));
        }

        [Test]
        public void HardLandingHasFiniteStunAndProtectionWithoutRepeatedCargoDrops()
        {
            var authority = CreateCamp(true);
            var preserved = CampCodec.Clone(authority.State);
            Vector3 oldPosition = Player(authority, MinerId).Position;
            double now = authority.State.OnlineSeconds;
            authority.LandHard(MinerId);
            var landed = Player(authority, MinerId);
            double stunEnd = landed.StunnedUntil;
            double protectionEnd = landed.ProtectionUntil;

            Assert.That(double.IsNaN(stunEnd) || double.IsInfinity(stunEnd), Is.False);
            Assert.That(stunEnd, Is.GreaterThan(now));
            Assert.That(stunEnd - now, Is.LessThanOrEqualTo(authority.Rules.StunSeconds));
            Assert.That(protectionEnd, Is.EqualTo(stunEnd + authority.Rules.ProtectionSeconds));
            AssertDroppedCargo(authority.State, oldPosition, "fall-", false);
            AssertCampAssetsPreserved(preserved, authority.State);
            string firstDrop = Cargo(authority.State);
            string firstCommitted = CampCodec.Encode(authority.State);
            authority.LandHard(MinerId);
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(firstCommitted));

            SetTime(authority, stunEnd + .25);
            Mutate(authority, state => state.Items.Add(new CampItem
            {
                Id = "new-cargo-during-protection", Kind = ItemKind.Stone, Place = ItemPlace.Hand,
                OwnerId = MinerId, Position = oldPosition + Vector3.up
            }));
            string protectedState = CampCodec.Encode(authority.State);
            authority.LandHard(MinerId);
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(protectedState));
            Assert.That(Cargo(authority.State), Is.EqualTo(firstDrop));
            Assert.That(Ore(authority, "new-cargo-during-protection").Place, Is.EqualTo(ItemPlace.Hand));

            SetTime(authority, protectionEnd + .01);
            authority.LandHard(MinerId);
            Assert.That(Ore(authority, "new-cargo-during-protection").Place, Is.EqualTo(ItemPlace.Ground));
            Assert.That(Player(authority, MinerId).StunnedUntil, Is.GreaterThan(protectionEnd));
            Assert.That(Cargo(authority.State), Is.EqualTo(firstDrop), "The first bag must not move or duplicate on a later impact.");
            AssertCampAssetsPreserved(preserved, authority.State);
        }

        [TestCase("escape")]
        [TestCase("river")]
        [TestCase("helper")]
        [TestCase("fall")]
        public void PersistenceFailureDoesNotPublishLifecycleAssetChanges(string transition)
        {
            var authority = CreateCamp(true, transition == "helper");
            if (transition == "escape")
            {
                AssertSuccess(authority.Execute(MinerId, Command("escape")));
                SetTime(authority, Player(authority, MinerId).EscapeAt);
            }
            else if (transition == "river")
            {
                Mutate(authority, state =>
                {
                    var river = state.Items.Single(i => i.Id == "river:0");
                    river.Place = ItemPlace.Sold;
                    river.OwnerId = MinerId;
                    river.Version++;
                    state.OnlineSeconds = state.NextRiverSpawn;
                });
            }
            else if (transition == "helper") ConfigureAssistance(authority, true);
            string liveBefore = CampCodec.Encode(authority.State);
            string durableBefore = CampCodec.Encode(_saved);
            _failSave = true;

            Assert.That(() =>
            {
                if (transition == "fall") authority.LandHard(MinerId);
                else authority.TickWorld(BothOnline);
            }, Throws.TypeOf<IOException>());

            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(liveBefore));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(durableBefore));
        }

        private CampAuthority CreateCamp(bool cargo, bool heavyHand = false)
        {
            var state = CampAuthority.NewWorld("lifecycle test camp", 97531);
            state.Players.Add(new CampPlayer { Id = MinerId, Name = "甲", CredentialHash = CampAuthority.Hash(MinerCredential),
                Position = CampAuthority.Spawn, Balance = 123, ToolLevel = 1, DetectorLevel = 2, ColorIndex = 0 });
            state.Players.Add(new CampPlayer { Id = HelperId, Name = "乙", CredentialHash = CampAuthority.Hash("lifecycle helper credential"),
                Position = CampAuthority.Spawn + Vector3.right * 3, Balance = 45, ColorIndex = 1 });
            var authority = new CampAuthority(state, new CampRules(), Persist);
            authority.InitializeMining();
            string cellId = CampMap.CellId(0, 0, 0);
            for (int hit = 0; hit < 10 && !authority.State.Cells.Single(c => c.Id == cellId).Dug; hit++)
            {
                var cell = authority.State.Cells.Single(c => c.Id == cellId);
                Mutate(authority, next =>
                {
                    var player = next.Players.Single(p => p.Id == MinerId);
                    player.Position = CampMap.CellCenter(cell, authority.Rules) + Vector3.up * (authority.Rules.CellSize * .5f);
                    next.OnlineSeconds = Math.Max(next.OnlineSeconds, player.NextMineTime + .01);
                });
                var mine = Command("mine", cellId, cell.Version);
                mine.Direction = (CampMap.CellCenter(cell, authority.Rules) - (Player(authority, MinerId).Position + Vector3.up * 1.6f)).normalized;
                AssertSuccess(authority.Execute(MinerId, mine));
            }
            var dug = authority.State.Cells.Single(c => c.Id == cellId);
            Assert.That(dug.Dug, Is.True);
            AssertSuccess(authority.Execute(MinerId, Command("place_ladder", cellId, dug.Version)));
            Mutate(authority, next =>
            {
                var player = next.Players.Single(p => p.Id == MinerId);
                player.Position = CampMap.CellCenter(dug, authority.Rules) - Vector3.up * (authority.Rules.CellSize * .5f);
                next.Items.Add(new CampItem { Id = "protected-cabinet", Kind = ItemKind.Gold, Place = ItemPlace.Cabinet,
                    OwnerId = MinerId, ContainerId = "cabinet:" + MinerId, Position = CampMap.CabinetPoint(0) });
                if (!cargo) return;
                next.Items.Add(new CampItem { Id = "cargo-hand", Kind = heavyHand ? ItemKind.HeavyOre : ItemKind.Gold,
                    Place = ItemPlace.Hand, OwnerId = MinerId, Position = player.Position + Vector3.up });
                next.Items.Add(new CampItem { Id = "cargo-pocket", Kind = ItemKind.Iron,
                    Place = ItemPlace.Pocket, OwnerId = MinerId, Position = player.Position });
                next.Items.Add(new CampItem { Id = "cargo-mouth", Kind = ItemKind.RawStone,
                    Place = ItemPlace.Mouth, OwnerId = MinerId, Position = player.Position, HiddenValue = 250, CoreKind = "gold" });
            });
            return authority;
        }

        private void ConfigureAssistance(CampAuthority authority, bool far)
        {
            Move(authority, HelperId, Player(authority, MinerId).Position + Vector3.right);
            var hand = Ore(authority, "cargo-hand");
            AssertSuccess(authority.Execute(HelperId, Command("assist", hand.Id, hand.Version)));
            if (far) Move(authority, HelperId, Player(authority, MinerId).Position + Vector3.right * 6);
        }

        private void Persist(CampState state)
        {
            if (_failSave) throw new IOException("Injected lifecycle persistence failure.");
            _saved = CampCodec.Clone(state);
        }

        private CampCommand Command(string action, string target = "", int version = 0) =>
            new CampCommand { RequestId = "lifecycle-test-" + ++_requestNumber, Action = action, TargetId = target, ExpectedVersion = version };
        private static CampPlayer Player(CampAuthority authority, string id) => authority.State.Players.Single(p => p.Id == id);
        private static CampItem Ore(CampAuthority authority, string id) => authority.State.Items.Single(i => i.Id == id);
        private static void SetTime(CampAuthority authority, double time) => Mutate(authority, state => state.OnlineSeconds = time);
        private static void Move(CampAuthority authority, string id, Vector3 position) =>
            Mutate(authority, state => state.Players.Single(p => p.Id == id).Position = position);

        private static void Mutate(CampAuthority authority, Action<CampState> edit)
        {
            var next = CampCodec.Clone(authority.State);
            edit(next);
            authority.Commit(next);
        }

        private static string Cargo(CampState state) => string.Join("\n", state.Items.Where(i => i.Id.StartsWith("cargo-", StringComparison.Ordinal))
            .OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => JsonUtility.ToJson(i)));

        private static string CellsAndLadders(CampState state) =>
            string.Join("\n", state.Cells.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => JsonUtility.ToJson(c))) + "\nLADDERS\n" +
            string.Join("\n", state.Ladders.OrderBy(l => l.Id, StringComparer.Ordinal).Select(l => JsonUtility.ToJson(l)));

        private static void AssertDroppedCargo(CampState state, Vector3 oldPosition, string bagPrefix, bool heavyHand)
        {
            var cargo = state.Items.Where(i => i.Id.StartsWith("cargo-", StringComparison.Ordinal)).ToArray();
            Assert.That(cargo.Length, Is.EqualTo(3));
            Assert.That(cargo.All(i => i.Place == ItemPlace.Ground && i.OwnerId == ""), Is.True);
            Assert.That(cargo.All(i => i.Position == oldPosition + Vector3.up * .5f), Is.True);
            var bagItems = cargo.Where(i => i.Kind != ItemKind.HeavyOre).ToArray();
            Assert.That(bagItems.Select(i => i.ContainerId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(bagItems[0].ContainerId, Does.StartWith(bagPrefix));
            if (heavyHand) Assert.That(cargo.Single(i => i.Id == "cargo-hand").ContainerId, Is.Empty);
            Assert.That(cargo.Single(i => i.Id == "cargo-mouth").HiddenValue, Is.EqualTo(250));
            Assert.That(cargo.Single(i => i.Id == "cargo-mouth").CoreKind, Is.EqualTo("gold"));
        }

        private static void AssertCampAssetsPreserved(CampState before, CampState after)
        {
            Assert.That(after.WorldId, Is.EqualTo(before.WorldId));
            Assert.That(after.Seed, Is.EqualTo(before.Seed));
            Assert.That(after.MineInitialized, Is.EqualTo(before.MineInitialized));
            Assert.That(CellsAndLadders(after), Is.EqualTo(CellsAndLadders(before)));
            foreach (var oldPlayer in before.Players)
            {
                var player = after.Players.Single(p => p.Id == oldPlayer.Id);
                Assert.That(player.Balance, Is.EqualTo(oldPlayer.Balance));
                Assert.That(player.ToolLevel, Is.EqualTo(oldPlayer.ToolLevel));
                Assert.That(player.DetectorLevel, Is.EqualTo(oldPlayer.DetectorLevel));
                Assert.That(player.Durability, Is.EqualTo(oldPlayer.Durability));
                Assert.That(player.LadderStock, Is.EqualTo(oldPlayer.LadderStock));
                Assert.That(player.CredentialHash, Is.EqualTo(oldPlayer.CredentialHash));
            }
            Assert.That(JsonUtility.ToJson(after.Items.Single(i => i.Id == "protected-cabinet")),
                Is.EqualTo(JsonUtility.ToJson(before.Items.Single(i => i.Id == "protected-cabinet"))));
        }

        private static void AssertSuccess(CampResult result) =>
            Assert.That(result.Success, Is.True, result.Code + ": " + result.Message);
    }
}
