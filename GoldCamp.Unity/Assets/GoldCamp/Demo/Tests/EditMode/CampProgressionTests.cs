using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    // Domain acceptance only. Fixture positions and speeds do not prove driving,
    // collision, rendered seats, input, or transport; those need the actual player route.
    public sealed class CampProgressionTests
    {
        private const string A = "a1111111111111111111111111111111";
        private const string B = "b2222222222222222222222222222222";
        private const string C = "c3333333333333333333333333333333";
        private const string D = "d4444444444444444444444444444444";
        private readonly HashSet<string> _online = new HashSet<string>();
        private CampState _saved;
        private int _saves, _request, _item;
        private bool _failSave, _sight, _free;

        [SetUp]
        public void SetUp()
        {
            _online.Clear();
            foreach (string id in new[] { A, B, C, D }) _online.Add(id);
            _saved = null;
            _saves = _request = _item = 0;
            _failSave = false;
            _sight = _free = true;
        }

        [Test]
        public void InitializationAddsAdvancedMineOnceWithoutChangingExistingDugPitYieldOrLadder()
        {
            var authority = Create(false);
            var first = authority.State.Cells.First(c => c.PitId == CampMap.StarterPit && c.Depth == 0);
            string cellId = first.Id;
            Move(authority, A, CampMap.CellCenter(first, authority.Rules) + Vector3.up * (authority.Rules.CellSize * .5f));
            for (int hit = 0; hit < 40 && !Cell(authority, cellId).Dug; hit++)
            {
                Mutate(authority, s => s.OnlineSeconds += authority.Rules.MineInterval + .1);
                var command = Command("mine", cellId, Cell(authority, cellId).Version);
                command.Direction = (CampMap.CellCenter(Cell(authority, cellId), authority.Rules) -
                    (Player(authority, A).Position + Vector3.up * 1.6f)).normalized;
                Success(authority.Execute(A, command));
            }
            Assert.That(Cell(authority, cellId).Dug, Is.True);
            Success(authority.Execute(A, Command("place_ladder", cellId, Cell(authority, cellId).Version)));
            string starter = StarterCells(authority.State), items = Items(authority.State), ladders = Ladders(authority.State);
            int stock = Player(authority, A).LadderStock;

            authority.InitializeProgression();

            Assert.That(authority.State.ProgressionInitialized, Is.True);
            Assert.That(StarterCells(authority.State), Is.EqualTo(starter));
            Assert.That(Items(authority.State), Is.EqualTo(items));
            Assert.That(Ladders(authority.State), Is.EqualTo(ladders));
            Assert.That(Player(authority, A).LadderStock, Is.EqualTo(stock));
            Assert.That(authority.State.Vehicles.Count, Is.EqualTo(1));
            Assert.That(authority.State.Vehicle.Id, Is.EqualTo(CampMap.VehicleId));
            Assert.That(authority.State.Vehicle.CargoSlots, Is.EqualTo(authority.Rules.VehicleCargoSlots));
            Assert.That(authority.State.RoadRequiredCoins, Is.EqualTo(authority.Rules.RoadPrice));
            Assert.That(authority.State.RoadRequiredIron, Is.EqualTo(authority.Rules.RoadIron));
            int expected = authority.Rules.AdvancedWidth * authority.Rules.AdvancedLength * authority.Rules.AdvancedDepth;
            Assert.That(authority.State.Cells.Count(c => c.PitId != CampMap.StarterPit), Is.EqualTo(expected));
            Assert.That(authority.State.RoadUnlocked, Is.False);
            string initialized = CampCodec.Encode(authority.State);
            int saves = _saves;
            authority.InitializeProgression();
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(initialized));
            Assert.That(_saves, Is.EqualTo(saves));
        }

        [Test]
        public void AdvancedSeedContentsAreFixedAndKeepBasicResourceColumnForUnupgradedPlayers()
        {
            var first = Create(seed: 19473);
            var second = Create(seed: 19473);
            Assert.That(first.State.WorldId, Is.Not.EqualTo(second.State.WorldId));
            Assert.That(AdvancedCells(second.State), Is.EqualTo(AdvancedCells(first.State)));
            var advanced = first.State.Cells.Where(c => c.PitId != CampMap.StarterPit).ToArray();
            Assert.That(advanced, Is.Not.Empty);
            Assert.That(advanced.Where(c => c.X == 0).All(c => c.RequiredTool == 1 &&
                (c.YieldKind == ItemKind.Stone || c.YieldKind == ItemKind.Iron)), Is.True);
            Assert.That(advanced.Where(c => c.X != 0).All(c => c.RequiredTool == 2), Is.True);
            Assert.That(advanced.All(c => !c.Dug && c.Health > 0 && c.YieldCount > 0), Is.True);
            var restored = NewAuthority(CampCodec.Clone(first.State), first.Rules);
            string before = CampCodec.Encode(restored.State);
            restored.InitializeMining();
            restored.InitializeProgression();
            Assert.That(CampCodec.Encode(restored.State), Is.EqualTo(before));
            Assert.DoesNotThrow(() => CampAuthority.Validate(restored.State));
        }

        [Test]
        public void InitializationFailureLeavesOldWorldUntouchedAndRetryCreatesOnlyOneCarAndMine()
        {
            var authority = Create(false);
            string before = CampCodec.Encode(authority.State), saved = CampCodec.Encode(_saved);
            _failSave = true;
            Assert.Throws<IOException>(() => authority.InitializeProgression());
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(saved));
            _failSave = false;
            authority.InitializeProgression();
            Assert.That(authority.State.Vehicles.Count, Is.EqualTo(1));
            Assert.That(authority.State.Cells.Select(c => c.Id).Distinct().Count(), Is.EqualTo(authority.State.Cells.Count));
            Assert.That(AdvancedCells(_saved), Is.EqualTo(AdvancedCells(authority.State)));
        }

        [Test]
        public void MaintenanceUsesTheVisibleDeskEvenAfterTheTruckHasLeft()
        {
            var authority = Create();
            Vector3 front = CampMap.VehicleMaintenancePoint + Vector3.forward * 1.5f;
            Assert.That(Vector3.Distance(front, CampMap.VehicleHome), Is.GreaterThan(authority.Rules.VehicleReach));
            Move(authority, A, front);
            Success(Contribute(authority, A, "contribute_coins", 1));
            GiveIron(authority, A, 1);
            Success(Contribute(authority, A, "contribute_iron", 1));
            RelocateCarFixture(authority, new Vector3(26, 0, 45), 0);
            Success(Contribute(authority, A, "contribute_coins", 1));
            _sight = false;
            FailsUnchanged(authority, A, RoadCommand(authority, "contribute_coins", 1));
            _sight = true;
            Move(authority, A, CampMap.VehicleHome + Vector3.right * 3);
            FailsUnchanged(authority, A, RoadCommand(authority, "contribute_coins", 1));
        }

        [Test]
        public void PartialContributionsUseOnlyVolunteeredAssetsAndUnlockWhenBothRequirementsAreMet()
        {
            var authority = Create();
            _online.Remove(C);
            string absent = JsonUtility.ToJson(Player(authority, C));
            long aBalance = Player(authority, A).Balance, bBalance = Player(authority, B).Balance;
            Success(Contribute(authority, A, "contribute_coins", 200));
            GiveIron(authority, A, 4);
            Success(Contribute(authority, A, "contribute_iron", 4));
            Assert.That(authority.State.RoadUnlocked, Is.False);
            Assert.That(authority.State.RoadCoinsPaid, Is.EqualTo(200));
            Assert.That(authority.State.RoadIronPaid, Is.EqualTo(4));
            Success(Contribute(authority, B, "contribute_coins", authority.State.RoadRequiredCoins - 200));
            Assert.That(authority.State.RoadUnlocked, Is.False, "Coins alone cannot substitute for missing iron.");
            GiveIron(authority, B, 4);
            Success(Contribute(authority, B, "contribute_iron", 4));
            Assert.That(authority.State.RoadUnlocked, Is.True);
            Assert.That(Player(authority, A).Balance, Is.EqualTo(aBalance - 200));
            Assert.That(Player(authority, B).Balance, Is.EqualTo(bBalance - (authority.State.RoadRequiredCoins - 200)));
            Assert.That(JsonUtility.ToJson(Player(authority, C)), Is.EqualTo(absent));
            Assert.That(authority.State.Contributions.Count, Is.EqualTo(2));
            foreach (string actor in new[] { A, B })
            {
                var contribution = authority.State.Contributions.Single(c => c.PlayerId == actor);
                Assert.That(contribution.Iron, Is.EqualTo(4));
                Assert.That(contribution.Coins, Is.EqualTo(200));
            }
            Assert.That(authority.State.Items.Count(i => i.Id.StartsWith("progression-iron-") && i.Place == ItemPlace.Sold), Is.EqualTo(8));
            Assert.That(authority.State.Contributions.Sum(c => c.Coins), Is.EqualTo(authority.State.RoadCoinsPaid));
            Assert.That(authority.State.Contributions.Sum(c => c.Iron), Is.EqualTo(authority.State.RoadIronPaid));
            Assert.That(_saved.RoadUnlocked, Is.True);
        }

        [Test]
        public void SinglePlayerCanSubmitEightSeparateIronPiecesInTwoPocketLoads()
        {
            var authority = Create();
            Success(Contribute(authority, A, "contribute_coins", authority.State.RoadRequiredCoins));
            for (int trip = 0; trip < 2; trip++)
            {
                GiveIron(authority, A, 4);
                Assert.That(authority.State.Items.Count(i => i.OwnerId == A && i.Place == ItemPlace.Pocket), Is.EqualTo(4));
                Success(Contribute(authority, A, "contribute_iron", 4));
                Assert.That(authority.State.Items.Count(i => i.OwnerId == A && i.Place == ItemPlace.Pocket), Is.Zero);
            }
            Assert.That(authority.State.RoadUnlocked, Is.True);
            Assert.That(authority.State.Contributions.Count, Is.EqualTo(1));
            Assert.That(authority.State.Contributions[0].Iron, Is.EqualTo(8));
            AssertUnique(authority.State);
        }

        [Test]
        public void IllegalInsufficientDistantAndObstructedContributionsLeaveAssetsUnchanged()
        {
            var authority = Create();
            foreach (string action in new[] { "contribute_coins", "contribute_iron" })
            foreach (long amount in new[] { -1L, 0L, long.MaxValue })
                FailsUnchanged(authority, A, RoadCommand(authority, action, amount));
            FailsUnchanged(authority, A, RoadCommand(authority, "contribute_coins", authority.State.RoadRequiredCoins + 1));
            GiveIron(authority, B, 4);
            FailsUnchanged(authority, A, RoadCommand(authority, "contribute_iron", 1));
            Mutate(authority, s => s.Players.Single(p => p.Id == A).Balance = 0);
            FailsUnchanged(authority, A, RoadCommand(authority, "contribute_coins", 1));
            Move(authority, B, CampMap.VehicleHome + Vector3.forward * 20);
            FailsUnchanged(authority, B, RoadCommand(authority, "contribute_iron", 1));
            Move(authority, B, CampMap.VehicleHome + Vector3.left * 3);
            _sight = false;
            FailsUnchanged(authority, B, RoadCommand(authority, "contribute_iron", 1));
        }

        [Test]
        public void CompetingFinalContributionChargesExactlyOnePlayerAndUnlocksOnce()
        {
            var authority = Create();
            PayAllIron(authority, A);
            Success(Contribute(authority, A, "contribute_coins", authority.State.RoadRequiredCoins - 1));
            var first = RoadCommand(authority, "contribute_coins", 1);
            var second = RoadCommand(authority, "contribute_coins", 1);
            long a = Player(authority, A).Balance, b = Player(authority, B).Balance;
            var results = new[] { authority.Execute(A, first), authority.Execute(B, second) };
            Assert.That(results.Count(r => r.Success), Is.EqualTo(1));
            Success(results[0]);
            Assert.That(authority.State.RoadUnlocked, Is.True);
            Assert.That(Player(authority, A).Balance, Is.EqualTo(a - 1));
            Assert.That(Player(authority, B).Balance, Is.EqualTo(b));
            Assert.That(authority.State.RoadCoinsPaid, Is.EqualTo(authority.State.RoadRequiredCoins));
            FailsUnchanged(authority, B, RoadCommand(authority, "contribute_coins", 1));
        }

        [Test]
        public void ContributionReplayAndConflictingRequestCannotDebitOrCreateContributionTwice()
        {
            var authority = Create();
            var command = RoadCommand(authority, "contribute_coins", 50);
            Success(authority.Execute(A, command));
            string committed = CampCodec.Encode(authority.State);
            int saves = _saves;
            Success(authority.Execute(A, command));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(committed));
            Assert.That(_saves, Is.EqualTo(saves));
            command.Amount = 51;
            var conflict = authority.Execute(A, command);
            Assert.That(conflict.Success, Is.False);
            Assert.That(conflict.Code, Is.EqualTo("request_conflict"));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(committed));
            Assert.That(_saves, Is.EqualTo(saves));
        }

        [Test]
        public void FinalIronPersistenceFailurePublishesNeitherUnlockNorConsumedItemAndSameRequestCanRetry()
        {
            var authority = Create();
            Success(Contribute(authority, A, "contribute_coins", authority.State.RoadRequiredCoins));
            GiveIron(authority, A, 4);
            Success(Contribute(authority, A, "contribute_iron", 4));
            GiveIron(authority, A, 3);
            Success(Contribute(authority, A, "contribute_iron", 3));
            GiveIron(authority, A, 1);
            var command = RoadCommand(authority, "contribute_iron", 1);
            FailsToPersistUnchanged(authority, A, command);
            Assert.That(authority.State.RoadUnlocked, Is.False);
            Assert.That(authority.State.RoadIronPaid, Is.EqualTo(7));
            _failSave = false;
            Success(authority.Execute(A, command));
            Assert.That(authority.State.RoadUnlocked, Is.True);
            Assert.That(authority.State.RoadIronPaid, Is.EqualTo(8));
            Assert.That(authority.State.Items.Count(i => i.OwnerId == A && i.Place == ItemPlace.Pocket), Is.Zero);
        }

        [Test]
        public void ToolUpgradeChargesOnlyBuyerRestoresDurabilityAndCannotBeBoughtTwice()
        {
            var authority = Create();
            Move(authority, A, CampMap.RepairPoint);
            Mutate(authority, s => s.Players.Single(p => p.Id == A).Durability = 0);
            string other = JsonUtility.ToJson(Player(authority, B));
            long balance = Player(authority, A).Balance;
            var command = Command("upgrade_tool", version: Player(authority, A).ToolLevel);
            Success(authority.Execute(A, command));
            Assert.That(Player(authority, A).ToolLevel, Is.EqualTo(2));
            Assert.That(Player(authority, A).Durability, Is.EqualTo(100));
            Assert.That(Player(authority, A).Balance, Is.EqualTo(balance - authority.Rules.UpgradePrice));
            Assert.That(JsonUtility.ToJson(Player(authority, B)), Is.EqualTo(other));
            string after = CampCodec.Encode(authority.State);
            Success(authority.Execute(A, command));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(after));
            FailsUnchanged(authority, A, Command("upgrade_tool", version: 1));
            FailsUnchanged(authority, A, Command("upgrade_tool", version: 2));
            Assert.That(_saved.Players.Single(p => p.Id == A).ToolLevel, Is.EqualTo(2));
        }

        [Test]
        public void DetectorUpgradeChangesOnlyBuyerDetectorAndSurvivesSaveReload()
        {
            var authority = Create();
            Move(authority, B, CampMap.RepairPoint);
            Mutate(authority, s => s.Players.Single(p => p.Id == B).Durability = 62.5f);
            long balance = Player(authority, B).Balance;
            float durability = Player(authority, B).Durability;
            Success(authority.Execute(B, Command("upgrade_detector", version: 0)));
            Assert.That(Player(authority, B).Balance, Is.EqualTo(balance - authority.Rules.DetectorUpgradePrice));
            Assert.That(Player(authority, B).DetectorLevel, Is.EqualTo(1));
            Assert.That(Player(authority, B).ToolLevel, Is.EqualTo(1));
            Assert.That(Player(authority, B).Durability, Is.EqualTo(durability));
            Assert.That(Player(authority, A).DetectorLevel, Is.Zero);
            FailsUnchanged(authority, B, Command("upgrade_detector", version: 1));
            var reloaded = NewAuthority(CampCodec.Clone(_saved), authority.Rules);
            Assert.That(Player(reloaded, B).DetectorLevel, Is.EqualTo(1));
            Assert.That(Player(reloaded, B).Balance, Is.EqualTo(balance - authority.Rules.DetectorUpgradePrice));
        }

        [Test]
        public void BothUpgradeRoutesRejectBadLocationFundsAndPersistenceWithoutPartialProgress()
        {
            foreach (string action in new[] { "upgrade_tool", "upgrade_detector" })
            {
                var authority = Create();
                int version = action == "upgrade_tool" ? 1 : 0;
                FailsUnchanged(authority, A, Command(action, version: version));
                Move(authority, A, CampMap.RepairPoint);
                Mutate(authority, s => s.Players.Single(p => p.Id == A).Balance = 0);
                FailsUnchanged(authority, A, Command(action, version: version));
                Mutate(authority, s => s.Players.Single(p => p.Id == A).Balance = 1000);
                var command = Command(action, version: version);
                FailsToPersistUnchanged(authority, A, command);
                _failSave = false;
                Success(authority.Execute(A, command));
            }
        }

        [Test]
        public void FourSeatsAreVoluntaryUniqueAndDoNotMovePlayersWhoHaveNotEntered()
        {
            var authority = Unlocked();
            var waiting = new[] { B, C, D }.ToDictionary(id => id, id => Player(authority, id).Position);
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_enter", 4));
            Success(Enter(authority, A, 0));
            foreach (var pair in waiting) Assert.That(Player(authority, pair.Key).Position, Is.EqualTo(pair.Value));
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_enter", 1));
            Success(Enter(authority, B, 1));
            Success(Enter(authority, C, 2));
            Success(Enter(authority, D, 3));
            Assert.That(authority.State.Vehicle.Seats, Is.EqualTo(new[] { A, B, C, D }));
            Assert.That(authority.State.Vehicle.Seats.Distinct().Count(), Is.EqualTo(4));
            Assert.That(_saved.Vehicle.Seats, Is.EqualTo(new[] { A, B, C, D }));
        }

        [Test]
        public void CompetingDriverSeatClaimsHaveOneWinnerAndLosingPlayerStaysOutside()
        {
            var authority = Unlocked();
            var first = VehicleCommand(authority, "vehicle_enter", 0);
            var second = VehicleCommand(authority, "vehicle_enter", 0);
            Vector3 outside = Player(authority, B).Position;
            var results = new[] { authority.Execute(A, first), authority.Execute(B, second) };
            Assert.That(results.Count(r => r.Success), Is.EqualTo(1));
            Assert.That(authority.State.Vehicle.Seats[0], Is.EqualTo(A));
            Assert.That(Player(authority, B).Position, Is.EqualTo(outside));
            FailsUnchanged(authority, B, VehicleCommand(authority, "vehicle_enter", 0));
            Assert.That(authority.State.Vehicle.Seats.Count(s => !string.IsNullOrEmpty(s)), Is.EqualTo(1));
        }

        [Test]
        public void SeatEntryRejectsLockedMovingOfflineHandFullAndBusyActors()
        {
            var locked = Create();
            FailsUnchanged(locked, A, VehicleCommand(locked, "vehicle_enter", 0));
            var authority = Unlocked();
            Mutate(authority, s => s.Vehicle.Speed = 2);
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_enter", 0));
            Mutate(authority, s => s.Vehicle.Speed = 0);
            _online.Remove(A);
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_enter", 0));
            _online.Add(A);
            string hand = Give(authority, A, ItemKind.Gold, ItemPlace.Hand);
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_enter", 0));
            Success(ItemAction(authority, A, "pocket", hand));
            Success(authority.Execute(A, Command("smoke")));
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_enter", 0));
            Success(authority.Execute(A, Command("cancel_action")));
            Success(Enter(authority, A, 0));
            FailsUnchanged(authority, A, Command("escape"));
            Assert.That(Ore(authority, hand).Place, Is.EqualTo(ItemPlace.Pocket));
        }

        [Test]
        public void UnsafeExitKeepsSeatAndSafeExitWhileStunnedStopsCarWithoutLosingPocketCargo()
        {
            var authority = Unlocked();
            string pocket = Give(authority, A, ItemKind.Gold, ItemPlace.Pocket);
            string mouth = Give(authority, A, ItemKind.Gold, ItemPlace.Mouth);
            Success(Enter(authority, A, 0));
            Mutate(authority, s =>
            {
                s.Vehicle.Speed = 3; s.Vehicle.Steering = .5f;
                s.Players.Single(p => p.Id == A).StunnedUntil = s.OnlineSeconds + 2;
            });
            _free = false;
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_exit"));
            Assert.That(authority.State.Vehicle.Seats[0], Is.EqualTo(A));
            _free = true;
            Success(authority.Execute(A, VehicleCommand(authority, "vehicle_exit")));
            Assert.That(authority.State.Vehicle.Seats.All(string.IsNullOrEmpty), Is.True);
            Assert.That(authority.State.Vehicle.Speed, Is.Zero);
            Assert.That(authority.State.Vehicle.Steering, Is.Zero);
            Assert.That(Ore(authority, pocket).Place, Is.EqualTo(ItemPlace.Pocket));
            Assert.That(Ore(authority, mouth).Place, Is.EqualTo(ItemPlace.Mouth));
            Assert.That(Ore(authority, mouth).OwnerId, Is.EqualTo(A));
        }

        [Test]
        public void CargoSlotCompetitionKeepsOneOriginalHeavyOreAndClearsItsAssistant()
        {
            var authority = Unlocked();
            string heavy = Give(authority, A, ItemKind.HeavyOre, ItemPlace.Hand);
            string other = Give(authority, B, ItemKind.Gold, ItemPlace.Hand);
            Mutate(authority, s => s.Items.Single(i => i.Id == heavy).AssistantId = C);
            var loadA = ItemCommand(authority, "vehicle_load", heavy, 0);
            var loadB = ItemCommand(authority, "vehicle_load", other, 0);
            Success(authority.Execute(A, loadA));
            FailsUnchanged(authority, B, loadB);
            var cargo = Ore(authority, heavy);
            Assert.That(cargo.Place, Is.EqualTo(ItemPlace.Vehicle));
            Assert.That(cargo.OwnerId, Is.Empty);
            Assert.That(cargo.AssistantId, Is.Empty);
            Assert.That(cargo.ContainerId, Is.EqualTo(CampMap.VehicleCargoContainer(0)));
            Assert.That(cargo.Version, Is.GreaterThan(loadA.ExpectedVersion));
            Assert.That(Ore(authority, other).Place, Is.EqualTo(ItemPlace.Hand));
            Assert.That(authority.State.Items.Count(i => i.Place == ItemPlace.Vehicle), Is.EqualTo(1));
            AssertUnique(authority.State);
        }

        [Test]
        public void PublicCargoUnloadingContendsOnSameItemVersionAndTransfersOriginalIdOnlyOnce()
        {
            var authority = Unlocked();
            string item = Give(authority, A, ItemKind.RawStone, ItemPlace.Hand);
            Success(ItemAction(authority, A, "vehicle_load", item, 0));
            var first = ItemCommand(authority, "vehicle_unload", item);
            var second = ItemCommand(authority, "vehicle_unload", item);
            var results = new[] { authority.Execute(B, first), authority.Execute(C, second) };
            Assert.That(results.Count(r => r.Success), Is.EqualTo(1));
            Assert.That(Ore(authority, item).Place, Is.EqualTo(ItemPlace.Hand));
            Assert.That(Ore(authority, item).OwnerId, Is.EqualTo(B));
            Assert.That(Ore(authority, item).ContainerId, Is.Empty);
            Assert.That(Ore(authority, item).HiddenValue, Is.EqualTo(83));
            Assert.That(authority.State.Items.Count(i => i.Id == item), Is.EqualTo(1));
            Assert.That(authority.State.Items.Any(i => i.Place == ItemPlace.Vehicle), Is.False);
            string after = CampCodec.Encode(authority.State);
            Success(authority.Execute(B, first));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(after));
        }

        [Test]
        public void CargoRejectsWrongOwnerMovingCarOccupiedSlotsInvalidSlotsAndFullHands()
        {
            var authority = Unlocked();
            string item = Give(authority, A, ItemKind.Gold, ItemPlace.Hand);
            FailsUnchanged(authority, B, ItemCommand(authority, "vehicle_load", item, 0));
            foreach (long slot in new[] { -1L, (long)authority.State.Vehicle.CargoSlots, long.MaxValue })
                FailsUnchanged(authority, A, ItemCommand(authority, "vehicle_load", item, slot));
            Mutate(authority, s => s.Vehicle.Speed = 1);
            FailsUnchanged(authority, A, ItemCommand(authority, "vehicle_load", item, 0));
            Mutate(authority, s => s.Vehicle.Speed = 0);
            Success(ItemAction(authority, A, "vehicle_load", item, 0));
            for (int slot = 1; slot < authority.State.Vehicle.CargoSlots; slot++)
            {
                string next = Give(authority, A, ItemKind.Stone, ItemPlace.Hand);
                Success(ItemAction(authority, A, "vehicle_load", next, slot));
            }
            string extra = Give(authority, A, ItemKind.Iron, ItemPlace.Hand);
            FailsUnchanged(authority, A, ItemCommand(authority, "vehicle_load", extra, 0));
            FailsUnchanged(authority, A, ItemCommand(authority, "vehicle_unload", item));
            Assert.That(authority.State.Items.Count(i => i.Place == ItemPlace.Vehicle), Is.EqualTo(authority.State.Vehicle.CargoSlots));
            Move(authority, B, authority.State.Vehicle.Position + Vector3.right * 30);
            FailsUnchanged(authority, B, ItemCommand(authority, "vehicle_unload", item));
            Move(authority, B, authority.State.Vehicle.Position + Vector3.left * 3);
            _sight = false;
            FailsUnchanged(authority, B, ItemCommand(authority, "vehicle_unload", item));
            AssertUnique(authority.State);
        }

        [Test]
        public void CargoLoadAndUnloadSaveFailuresCannotLoseOrDuplicateTheItem()
        {
            var authority = Unlocked();
            string item = Give(authority, A, ItemKind.HeavyOre, ItemPlace.Hand);
            var load = ItemCommand(authority, "vehicle_load", item, 0);
            FailsToPersistUnchanged(authority, A, load);
            _failSave = false;
            Success(authority.Execute(A, load));
            var unload = ItemCommand(authority, "vehicle_unload", item);
            FailsToPersistUnchanged(authority, B, unload);
            _failSave = false;
            Success(authority.Execute(B, unload));
            Assert.That(Ore(authority, item).OwnerId, Is.EqualTo(B));
            Assert.That(Ore(authority, item).Place, Is.EqualTo(ItemPlace.Hand));
            AssertUnique(authority.State);
        }

        [Test]
        public void DriverDisconnectParksCarRetainsPassengerAndCarCargoAndDropsOnlyDriverCarry()
        {
            var authority = Unlocked();
            string cargo = Give(authority, A, ItemKind.HeavyOre, ItemPlace.Hand);
            Success(ItemAction(authority, A, "vehicle_load", cargo, 0));
            string pocket = Give(authority, A, ItemKind.Gold, ItemPlace.Pocket);
            string mouth = Give(authority, A, ItemKind.Gold, ItemPlace.Mouth);
            Success(Enter(authority, A, 0));
            Success(Enter(authority, B, 1));
            RelocateCarFixture(authority, new Vector3(27, 0, 43), 4);
            Vector3 oldPosition = authority.State.Vehicle.Position;
            string storedCargo = JsonUtility.ToJson(Ore(authority, cargo));
            long balance = Player(authority, A).Balance;
            _online.Remove(A);
            string beforeDisconnect = CampCodec.Encode(authority.State), savedBeforeDisconnect = CampCodec.Encode(_saved);
            _failSave = true;
            Assert.Throws<IOException>(() => authority.Disconnect(A));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(beforeDisconnect));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(savedBeforeDisconnect));
            _failSave = false;
            authority.Disconnect(A);
            Assert.That(authority.State.Vehicle.Position, Is.EqualTo(oldPosition));
            Assert.That(authority.State.Vehicle.Speed, Is.Zero);
            Assert.That(authority.State.Vehicle.Steering, Is.Zero);
            Assert.That(authority.State.Vehicle.Seats[0], Is.Empty);
            Assert.That(authority.State.Vehicle.Seats[1], Is.EqualTo(B));
            Assert.That(JsonUtility.ToJson(Ore(authority, cargo)), Is.EqualTo(storedCargo));
            Assert.That(Player(authority, A).Balance, Is.EqualTo(balance));
            var dropped = new[] { Ore(authority, pocket), Ore(authority, mouth) };
            Assert.That(dropped.All(i => i.Place == ItemPlace.Ground && i.OwnerId == ""), Is.True);
            Assert.That(dropped[0].ContainerId, Does.StartWith("offline-"));
            Assert.That(dropped[1].ContainerId, Is.EqualTo(dropped[0].ContainerId));
            Assert.That(dropped.All(i => Vector3.Distance(i.Position, oldPosition) < 5f), Is.True);
            string items = Items(authority.State);
            authority.Disconnect(A);
            Assert.That(Items(authority.State), Is.EqualTo(items));
            AssertUnique(authority.State);
        }

        [Test]
        public void RecoveryRejectsPassengersAndOccupiedHomeThenMovesOnlySameEmptyCar()
        {
            var authority = Unlocked();
            string small = Give(authority, A, ItemKind.Gold, ItemPlace.Hand);
            Success(ItemAction(authority, A, "vehicle_load", small, 0));
            string heavy = Give(authority, A, ItemKind.HeavyOre, ItemPlace.Hand);
            Success(ItemAction(authority, A, "vehicle_load", heavy, 1));
            Success(Enter(authority, B, 1));
            RelocateCarFixture(authority, new Vector3(26, 0, 45), 0);
            Vector3 old = authority.State.Vehicle.Position;
            Move(authority, A, CampMap.VehicleMaintenancePoint + Vector3.forward * 1.5f);
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_recover"));
            Success(authority.Execute(B, VehicleCommand(authority, "vehicle_exit")));
            Vector3 passengerExit = Player(authority, B).Position;
            Move(authority, B, CampMap.VehicleHome);
            FailsUnchanged(authority, A, VehicleCommand(authority, "vehicle_recover"));
            Move(authority, B, passengerExit);
            var recover = VehicleCommand(authority, "vehicle_recover");
            FailsToPersistUnchanged(authority, A, recover);
            _failSave = false;
            Success(authority.Execute(A, recover));
            Assert.That(authority.State.Vehicles.Count, Is.EqualTo(1));
            Assert.That(authority.State.Vehicle.Id, Is.EqualTo(CampMap.VehicleId));
            Assert.That(authority.State.Vehicle.Position, Is.EqualTo(CampMap.VehicleHome));
            Assert.That(authority.State.Vehicle.Speed, Is.Zero);
            Assert.That(authority.State.Vehicle.Seats.All(string.IsNullOrEmpty), Is.True);
            Assert.That(authority.State.Items.Any(i => i.Place == ItemPlace.Vehicle), Is.False);
            foreach (string id in new[] { small, heavy })
            {
                Assert.That(Ore(authority, id).Place, Is.EqualTo(ItemPlace.Ground));
                Assert.That(Ore(authority, id).OwnerId, Is.Empty);
                Assert.That(Vector3.Distance(Ore(authority, id).Position, old), Is.LessThan(5f));
                Assert.That(Vector3.Distance(Ore(authority, id).Position, CampMap.VehicleHome), Is.GreaterThan(20f));
            }
            Assert.That(Ore(authority, small).ContainerId, Does.StartWith("recovery-"));
            Assert.That(Ore(authority, heavy).ContainerId, Is.Empty);
            string after = CampCodec.Encode(authority.State);
            Success(authority.Execute(A, recover));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(after));
            AssertUnique(authority.State);
        }

        [Test]
        public void ReloadRecoveryClearsStaleSeatsAndMovementButPreservesCarCargoAndProgressExactlyOnce()
        {
            var authority = Unlocked();
            string cargo = Give(authority, A, ItemKind.RawStone, ItemPlace.Hand);
            Success(ItemAction(authority, A, "vehicle_load", cargo, 0));
            Success(Enter(authority, A, 0));
            Success(Enter(authority, B, 1));
            RelocateCarFixture(authority, new Vector3(25, 0, 44), 5);
            string cargoBefore = JsonUtility.ToJson(Ore(authority, cargo));
            string contributions = Contributions(authority.State), cells = AllCells(authority.State);
            Vector3 parked = authority.State.Vehicle.Position;
            _online.Clear();
            var reloaded = NewAuthority(CampCodec.Clone(_saved), authority.Rules);
            string before = CampCodec.Encode(reloaded.State);
            _failSave = true;
            Assert.Throws<IOException>(() => reloaded.RecoverProgression());
            Assert.That(CampCodec.Encode(reloaded.State), Is.EqualTo(before));
            _failSave = false;
            reloaded.RecoverProgression();
            Assert.That(reloaded.State.Vehicle.Position, Is.EqualTo(parked));
            Assert.That(reloaded.State.Vehicle.Speed, Is.Zero);
            Assert.That(reloaded.State.Vehicle.Steering, Is.Zero);
            Assert.That(reloaded.State.Vehicle.Seats.All(string.IsNullOrEmpty), Is.True);
            Assert.That(JsonUtility.ToJson(Ore(reloaded, cargo)), Is.EqualTo(cargoBefore));
            Assert.That(Contributions(reloaded.State), Is.EqualTo(contributions));
            Assert.That(AllCells(reloaded.State), Is.EqualTo(cells));
            Assert.That(reloaded.State.RoadUnlocked, Is.True);
            foreach (string actor in new[] { A, B })
                Assert.That(Vector3.Distance(Player(reloaded, actor).Position, parked), Is.LessThan(5f));
            string recovered = CampCodec.Encode(reloaded.State);
            int saves = _saves;
            reloaded.RecoverProgression();
            reloaded.InitializeProgression();
            Assert.That(CampCodec.Encode(reloaded.State), Is.EqualTo(recovered));
            Assert.That(_saves, Is.EqualTo(saves));
        }

        [Test]
        public void ValidationRejectsDuplicateSeatsCargoSlotsUnknownOwnersAndContributionMismatch()
        {
            var authority = Unlocked();
            string first = Give(authority, A, ItemKind.Gold, ItemPlace.Hand);
            Success(ItemAction(authority, A, "vehicle_load", first, 0));
            string second = Give(authority, A, ItemKind.Stone, ItemPlace.Hand);
            Success(ItemAction(authority, A, "vehicle_load", second, 1));
            AssertInvalid(authority, s => { s.Vehicle.Seats[0] = A; s.Vehicle.Seats[1] = A; });
            AssertInvalid(authority, s => s.Vehicle.Seats[0] = "unknown-player");
            AssertInvalid(authority, s => s.Items.Single(i => i.Id == second).ContainerId = CampMap.VehicleCargoContainer(0));
            AssertInvalid(authority, s => s.Items.Single(i => i.Id == first).OwnerId = A);
            AssertInvalid(authority, s => s.Items.Single(i => i.Id == first).ContainerId = "vehicle:missing:0");
            AssertInvalid(authority, s => s.Contributions[0].Coins++);
            AssertInvalid(authority, s => s.RoadUnlocked = false);
            AssertInvalid(authority, s => s.Vehicle.Speed = float.NaN);
            AssertInvalid(authority, s => s.Vehicle.Seats.RemoveAt(3));
            AssertUnique(authority.State);
        }

        private CampAuthority Create(bool initializeProgression = true, int seed = 52431)
        {
            var state = CampAuthority.NewWorld("D domain fixture", seed);
            int color = 0;
            foreach (string id in new[] { A, B, C, D })
                state.Players.Add(new CampPlayer { Id = id, Name = "Miner " + color, ColorIndex = color++,
                    CredentialHash = CampAuthority.Hash("isolated-progression-fixture-credential-" + id),
                    Balance = 2000, Position = CampMap.VehicleHome + Vector3.left * 3 });
            var authority = NewAuthority(state, new CampRules());
            authority.InitializeMining();
            if (initializeProgression) authority.InitializeProgression();
            return authority;
        }

        private CampAuthority NewAuthority(CampState state, CampRules rules) => new CampAuthority(state, rules, Persist,
            (from, to) => _sight, (position, radius) => _free, id => _online.Contains(id), id => id == A);
        private void Persist(CampState state)
        {
            if (_failSave) throw new IOException("Injected progression persistence failure.");
            _saved = CampCodec.Clone(state);
            _saves++;
        }
        private CampAuthority Unlocked()
        {
            var authority = Create();
            Success(Contribute(authority, A, "contribute_coins", authority.State.RoadRequiredCoins));
            PayAllIron(authority, A);
            Assert.That(authority.State.RoadUnlocked, Is.True);
            return authority;
        }
        private void PayAllIron(CampAuthority authority, string actor)
        {
            while (authority.State.RoadIronPaid < authority.State.RoadRequiredIron)
            {
                int count = Math.Min(4, authority.State.RoadRequiredIron - authority.State.RoadIronPaid);
                GiveIron(authority, actor, count);
                Success(Contribute(authority, actor, "contribute_iron", count));
            }
        }
        private void GiveIron(CampAuthority authority, string actor, int count)
        {
            for (int i = 0; i < count; i++) Give(authority, actor, ItemKind.Iron, ItemPlace.Pocket, "progression-iron-");
        }
        private string Give(CampAuthority authority, string actor, ItemKind kind, ItemPlace place, string prefix = "progression-item-")
        {
            string id = prefix + ++_item;
            Mutate(authority, s => s.Items.Add(new CampItem { Id = id, Kind = kind, Place = place, OwnerId = actor,
                Position = s.Players.Single(p => p.Id == actor).Position + Vector3.up,
                HiddenValue = kind == ItemKind.RawStone ? 83 : 0, CoreKind = kind == ItemKind.RawStone ? "gold" : "" }));
            return id;
        }
        private CampCommand Command(string action, string target = "", int version = 0, long amount = 0) =>
            new CampCommand { RequestId = "progression-test-" + ++_request, Action = action, TargetId = target,
                ExpectedVersion = version, Amount = amount };
        private CampCommand RoadCommand(CampAuthority authority, string action, long amount) =>
            Command(action, version: authority.State.RoadVersion, amount: amount);
        private CampResult Contribute(CampAuthority authority, string actor, string action, long amount) =>
            authority.Execute(actor, RoadCommand(authority, action, amount));
        private CampCommand VehicleCommand(CampAuthority authority, string action, long amount = 0) =>
            Command(action, CampMap.VehicleId, authority.State.Vehicle.Version, amount);
        private CampResult Enter(CampAuthority authority, string actor, int seat) =>
            authority.Execute(actor, VehicleCommand(authority, "vehicle_enter", seat));
        private CampCommand ItemCommand(CampAuthority authority, string action, string id, long amount = 0) =>
            Command(action, id, Ore(authority, id).Version, amount);
        private CampResult ItemAction(CampAuthority authority, string actor, string action, string id, long amount = 0) =>
            authority.Execute(actor, ItemCommand(authority, action, id, amount));
        private static CampPlayer Player(CampAuthority authority, string id) => authority.State.Players.Single(p => p.Id == id);
        private static CampItem Ore(CampAuthority authority, string id) => authority.State.Items.Single(i => i.Id == id);
        private static CampCell Cell(CampAuthority authority, string id) => authority.State.Cells.Single(c => c.Id == id);
        private static void Move(CampAuthority authority, string id, Vector3 position) =>
            Mutate(authority, s => s.Players.Single(p => p.Id == id).Position = position);
        private static void Mutate(CampAuthority authority, Action<CampState> change)
        {
            var next = CampCodec.Clone(authority.State);
            change(next);
            authority.Commit(next);
        }
        private static void RelocateCarFixture(CampAuthority authority, Vector3 position, float speed) => Mutate(authority, s =>
        {
            s.Vehicle.Position = position; s.Vehicle.Speed = speed; s.Vehicle.Steering = speed == 0 ? 0 : .4f;
            for (int seat = 0; seat < 4; seat++)
                if (!string.IsNullOrEmpty(s.Vehicle.Seats[seat]))
                    s.Players.Single(p => p.Id == s.Vehicle.Seats[seat]).Position = CampMap.SeatPoint(s.Vehicle, seat);
            for (int slot = 0; slot < s.Vehicle.CargoSlots; slot++)
            {
                var item = s.Items.SingleOrDefault(i => i.Place == ItemPlace.Vehicle && i.ContainerId == CampMap.VehicleCargoContainer(slot));
                if (item != null) item.Position = CampMap.CargoPoint(s.Vehicle, slot);
            }
        });
        private static string StarterCells(CampState state) => Rows(state.Cells.Where(c => c.PitId == CampMap.StarterPit).OrderBy(c => c.Id));
        private static string AdvancedCells(CampState state) => Rows(state.Cells.Where(c => c.PitId != CampMap.StarterPit).OrderBy(c => c.Id));
        private static string AllCells(CampState state) => Rows(state.Cells.OrderBy(c => c.Id));
        private static string Items(CampState state) => Rows(state.Items.OrderBy(i => i.Id));
        private static string Ladders(CampState state) => Rows(state.Ladders.OrderBy(l => l.Id));
        private static string Contributions(CampState state) => Rows(state.Contributions.OrderBy(c => c.PlayerId));
        private static string Rows<T>(IEnumerable<T> rows) => string.Join("\n", rows.Select(row => JsonUtility.ToJson(row)));
        private static string Assets(CampState state)
        {
            var copy = CampCodec.Clone(state);
            copy.Receipts.Clear(); copy.Revision = 0; copy.SavedUtc = null;
            return CampCodec.Encode(copy);
        }
        private static void Success(CampResult result) => Assert.That(result.Success, Is.True, result.Code + ": " + result.Message);
        private static void AssertUnique(CampState state)
        {
            Assert.That(state.Items.Select(i => i.Id).Distinct().Count(), Is.EqualTo(state.Items.Count));
            Assert.DoesNotThrow(() => CampAuthority.Validate(state));
        }
        private static void AssertInvalid(CampAuthority authority, Action<CampState> corrupt)
        {
            var next = CampCodec.Clone(authority.State);
            corrupt(next);
            Assert.That(() => CampAuthority.Validate(next), Throws.Exception);
        }
        private static void FailsUnchanged(CampAuthority authority, string actor, CampCommand command)
        {
            string before = Assets(authority.State);
            var result = authority.Execute(actor, command);
            Assert.That(result.Success, Is.False, command.Action + " unexpectedly succeeded.");
            Assert.That(result.Code, Is.Not.EqualTo("save_failed"), "A rejected command must still persist its real rule result: " + result.Message);
            Assert.That(Assets(authority.State), Is.EqualTo(before), command.Action + " changed assets on rejection.");
        }
        private void FailsToPersistUnchanged(CampAuthority authority, string actor, CampCommand command)
        {
            string before = CampCodec.Encode(authority.State), saved = CampCodec.Encode(_saved);
            int saves = _saves;
            _failSave = true;
            var result = authority.Execute(actor, command);
            Assert.That(result.Success, Is.False);
            Assert.That(result.Code, Is.EqualTo("save_failed"));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before), "Failed persistence must not publish even a revision or receipt.");
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(saved));
            Assert.That(_saves, Is.EqualTo(saves));
        }
    }
}
