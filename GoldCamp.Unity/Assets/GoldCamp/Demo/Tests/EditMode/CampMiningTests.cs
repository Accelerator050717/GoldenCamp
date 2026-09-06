using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    // Host-rule tests. Fixture positions, aim and clock do not constitute Unity
    // collision, player-input, transport or multi-process validation.
    public sealed class CampMiningTests
    {
        private const string MinerId = "55555555555555555555555555555555";
        private const string HelperId = "66666666666666666666666666666666";
        private const string ThirdId = "77777777777777777777777777777777";
        private CampState _saved;
        private int _saveCount;
        private int _requestNumber;

        [SetUp]
        public void SetUp()
        {
            _saved = null;
            _saveCount = 0;
            _requestNumber = 0;
        }

        [Test]
        public void FixedSeedGeneratesFiniteIdenticalCellContentsInDifferentWorlds()
        {
            var first = Create(13579);
            var second = Create(13579);
            first.InitializeMining();
            second.InitializeMining();

            Assert.That(first.State.WorldId, Is.Not.EqualTo(second.State.WorldId));
            int expectedCells = first.Rules.PitWidth * first.Rules.PitLength * first.Rules.PitDepth;
            Assert.That(first.State.Cells.Count, Is.EqualTo(expectedCells));
            Assert.That(first.State.Cells.Select(c => c.Id).Distinct().Count(), Is.EqualTo(expectedCells));
            Assert.That(first.State.Cells.All(c => !c.Dug && c.Health > 0), Is.True);
            Assert.That(first.State.Cells.All(c => c.X >= 0 && c.X < first.Rules.PitWidth &&
                c.Z >= 0 && c.Z < first.Rules.PitLength && c.Depth >= 0 && c.Depth < first.Rules.PitDepth), Is.True);
            Assert.That(CellContents(second.State), Is.EqualTo(CellContents(first.State)));

            string initialized = CampCodec.Encode(first.State);
            int savesBefore = _saveCount;
            first.InitializeMining();
            Assert.That(CampCodec.Encode(first.State), Is.EqualTo(initialized));
            Assert.That(_saveCount, Is.EqualTo(savesBefore), "Initialization must not reroll or resave an existing pit.");
        }

        [Test]
        public void CompetingFinalHitProducesOnceAndSavedDugCellCannotRegenerate()
        {
            var authority = Create();
            authority.InitializeMining();
            string cellId = CampMap.CellId(0, 0, 0);
            while (Cell(authority, cellId).Health > authority.Rules.BaseMineDamage)
                AssertSuccess(Mine(authority, cellId, MinerId));
            Assert.That(Cell(authority, cellId).Dug, Is.False);
            Assert.That(Cell(authority, cellId).YieldCount, Is.EqualTo(1));
            PrepareMining(authority, cellId, MinerId);
            PrepareMining(authority, cellId, HelperId);
            int contestedVersion = Cell(authority, cellId).Version;
            var firstCommand = MiningCommand(authority, cellId, MinerId, contestedVersion);
            var secondCommand = MiningCommand(authority, cellId, HelperId, contestedVersion);

            var first = authority.Execute(MinerId, firstCommand);
            var second = authority.Execute(HelperId, secondCommand);

            Assert.That(new[] { first, second }.Count(r => r.Success), Is.EqualTo(1));
            Assert.That(first.Success, Is.True, first.Code + ": " + first.Message);
            Assert.That(Cell(authority, cellId).Dug, Is.True);
            Assert.That(YieldItems(authority.State, cellId).Length, Is.EqualTo(1));
            string committed = CampCodec.Encode(authority.State);
            var replay = authority.Execute(MinerId, firstCommand);
            Assert.That(replay.Success, Is.True);
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(committed));

            var restarted = new CampAuthority(CampCodec.Clone(_saved), authority.Rules, Persist);
            string beforeInitialize = CampCodec.Encode(restarted.State);
            restarted.InitializeMining();
            Assert.That(CampCodec.Encode(restarted.State), Is.EqualTo(beforeInitialize));
            Assert.That(Cell(restarted, cellId).Dug, Is.True);
            Assert.That(YieldItems(restarted.State, cellId).Length, Is.EqualTo(1));
            PrepareMining(restarted, cellId, MinerId);
            var repeatDig = restarted.Execute(MinerId, MiningCommand(restarted, cellId, MinerId, Cell(restarted, cellId).Version));
            Assert.That(repeatDig.Success, Is.False);
            Assert.That(YieldItems(restarted.State, cellId).Length, Is.EqualTo(1));
        }

        [Test]
        public void ZeroMoneyAndWornOutToolStillMineBasicResourceWithoutBuyingRepair()
        {
            var authority = Create();
            authority.InitializeMining();
            Mutate(authority, state =>
            {
                var player = state.Players.Single(p => p.Id == MinerId);
                player.Balance = 0;
                player.Durability = 0;
                player.ToolLevel = 1;
            });
            var target = authority.State.Cells.First(c => c.Depth == 0 && c.RequiredTool == 1 &&
                (c.YieldKind == ItemKind.Stone || c.YieldKind == ItemKind.Iron));
            float initialHealth = target.Health;
            AssertSuccess(Mine(authority, target.Id, MinerId));
            Assert.That(Cell(authority, target.Id).Health, Is.LessThan(initialHealth));
            Assert.That(Cell(authority, target.Id).Health, Is.GreaterThanOrEqualTo(0));
            FinishMining(authority, target.Id, MinerId);

            Assert.That(Cell(authority, target.Id).Dug, Is.True);
            Assert.That(YieldItems(authority.State, target.Id), Is.Not.Empty);
            Assert.That(Player(authority, MinerId).Balance, Is.Zero);
            Assert.That(Player(authority, MinerId).Durability, Is.Zero);
            Assert.That(Player(authority, MinerId).ToolLevel, Is.EqualTo(1));
            Assert.That(Cell(_saved, target.Id).Dug, Is.True);
        }

        [TestCase("coins")]
        [TestCase("iron")]
        [TestCase("stone")]
        public void RepairRoutesChargeOnlyOnceAndFullToolDoesNotConsumeAnotherPayment(string route)
        {
            var authority = Create();
            authority.InitializeMining();
            Mutate(authority, state =>
            {
                var player = state.Players.Single(p => p.Id == MinerId);
                player.Position = CampMap.RepairPoint;
                player.Durability = 12;
                state.Items.Add(Item("own-repair-iron", ItemKind.Iron, ItemPlace.Pocket, MinerId, 10));
                state.Items.Add(Item("own-repair-stone", ItemKind.Stone, ItemPlace.Pocket, MinerId, 10));
                state.Items.Add(Item("other-repair-iron", ItemKind.Iron, ItemPlace.Pocket, HelperId, 10));
                state.Items.Add(Item("other-repair-stone", ItemKind.Stone, ItemPlace.Pocket, HelperId, 10));
            });
            var repair = Command(route == "coins" ? "repair" : "repair_material", route == "coins" ? "" : route);

            AssertSuccess(authority.Execute(MinerId, repair));

            Assert.That(Player(authority, MinerId).Durability, Is.EqualTo(100));
            Assert.That(Player(authority, MinerId).Balance, Is.EqualTo(route == "coins" ? 100 - authority.Rules.RepairPrice : 100));
            Assert.That(MaterialQuantity(authority.State, MinerId, ItemKind.Iron),
                Is.EqualTo(route == "iron" ? 10 - authority.Rules.RepairIronCost : 10));
            Assert.That(MaterialQuantity(authority.State, MinerId, ItemKind.Stone),
                Is.EqualTo(route == "stone" ? 10 - authority.Rules.RepairStoneCost : 10));
            Assert.That(MaterialQuantity(authority.State, HelperId, ItemKind.Iron), Is.EqualTo(10));
            Assert.That(MaterialQuantity(authority.State, HelperId, ItemKind.Stone), Is.EqualTo(10));

            var restarted = new CampAuthority(CampCodec.Clone(_saved), authority.Rules, Persist);
            string completed = CampCodec.Encode(restarted.State);
            AssertSuccess(restarted.Execute(MinerId, repair));
            Assert.That(CampCodec.Encode(restarted.State), Is.EqualTo(completed));
            string assets = Assets(restarted.State);
            var anotherRoute = Command(route == "coins" ? "repair_material" : "repair", "iron");
            Assert.That(restarted.Execute(MinerId, anotherRoute).Success, Is.False);
            Assert.That(Assets(restarted.State), Is.EqualTo(assets));
        }

        [Test]
        public void PocketAndCabinetRequireOwnershipAndPreserveTheSameItemThroughRoundTrip()
        {
            var authority = Create();
            authority.InitializeMining();
            Mutate(authority, state =>
            {
                state.Players.Single(p => p.Id == MinerId).Position = CampMap.CabinetPoint(0);
                state.Players.Single(p => p.Id == HelperId).Position = CampMap.CabinetPoint(0);
                state.Items.Add(Item("private-small-ore", ItemKind.Gold, ItemPlace.Hand, MinerId));
            });
            AssertSuccess(ItemCommand(authority, MinerId, "pocket", "private-small-ore"));
            Assert.That(Ore(authority, "private-small-ore").Place, Is.EqualTo(ItemPlace.Pocket));
            string privatePocket = Assets(authority.State);
            Assert.That(ItemCommand(authority, HelperId, "unpocket", "private-small-ore").Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(privatePocket));
            AssertSuccess(ItemCommand(authority, MinerId, "unpocket", "private-small-ore"));
            Assert.That(Ore(authority, "private-small-ore").Place, Is.EqualTo(ItemPlace.Hand));

            AssertSuccess(ItemCommand(authority, MinerId, "cabinet_store", "private-small-ore"));
            Assert.That(Ore(authority, "private-small-ore").Place, Is.EqualTo(ItemPlace.Cabinet));
            Assert.That(Ore(authority, "private-small-ore").ContainerId, Is.EqualTo("cabinet:" + MinerId));
            string privateCabinet = Assets(authority.State);
            Assert.That(ItemCommand(authority, HelperId, "cabinet_take", "private-small-ore").Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(privateCabinet));
            AssertSuccess(ItemCommand(authority, MinerId, "cabinet_take", "private-small-ore"));
            Assert.That(Ore(authority, "private-small-ore").Place, Is.EqualTo(ItemPlace.Hand));
            Assert.That(Ore(authority, "private-small-ore").OwnerId, Is.EqualTo(MinerId));
            Assert.That(authority.State.Items.Count(i => i.Id == "private-small-ore"), Is.EqualTo(1));
        }

        [TestCase("pocket")]
        [TestCase("cabinet_store")]
        public void HeavyOreCannotEnterSmallPrivateStorage(string action)
        {
            var authority = Create();
            authority.InitializeMining();
            Mutate(authority, state =>
            {
                state.Players.Single(p => p.Id == MinerId).Position = CampMap.CabinetPoint(0);
                state.Items.Add(Item("heavy-storage-rejected", ItemKind.HeavyOre, ItemPlace.Hand, MinerId));
            });
            string before = Assets(authority.State);
            var result = ItemCommand(authority, MinerId, action, "heavy-storage-rejected");
            Assert.That(result.Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(before));
            Assert.That(Ore(authority, "heavy-storage-rejected").Place, Is.EqualTo(ItemPlace.Hand));
        }

        [Test]
        public void FullPocketRejectsAnotherItemWithoutLosingTheHeldItem()
        {
            var authority = Create();
            authority.InitializeMining();
            Mutate(authority, state =>
            {
                for (int slot = 0; slot < authority.Rules.PocketSlots; slot++)
                    state.Items.Add(Item("pocket-slot-" + slot, ItemKind.Stone, ItemPlace.Pocket, MinerId));
                state.Items.Add(Item("overflow-pocket-item", ItemKind.Gold, ItemPlace.Hand, MinerId));
            });
            string before = Assets(authority.State);
            Assert.That(ItemCommand(authority, MinerId, "pocket", "overflow-pocket-item").Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(before));
        }

        [Test]
        public void OccupiedLadderAndSupportingSegmentCannotBeRemovedAndRecoveryRefundsOnce()
        {
            var authority = Create();
            authority.InitializeMining();
            string topCell = CampMap.CellId(0, 0, 0);
            string lowerCell = CampMap.CellId(0, 1, 0);
            FinishMining(authority, topCell, MinerId);
            FinishMining(authority, lowerCell, MinerId);
            int initialStock = Player(authority, MinerId).LadderStock;
            PlaceLadder(authority, topCell, MinerId);
            PlaceLadder(authority, lowerCell, MinerId);
            string topId = "ladder:" + topCell;
            string lowerId = "ladder:" + lowerCell;
            Assert.That(Player(authority, MinerId).LadderStock, Is.EqualTo(initialStock - 2));

            PrepareMining(authority, topCell, MinerId);
            string withSupport = Assets(authority.State);
            Assert.That(LadderCommand(authority, MinerId, "remove_ladder", topId).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(withSupport));

            PrepareMining(authority, lowerCell, HelperId);
            AssertSuccess(LadderCommand(authority, HelperId, "climb", lowerId));
            Assert.That(Player(authority, HelperId).ClimbingLadderId, Is.EqualTo(lowerId));
            PrepareMining(authority, lowerCell, MinerId);
            string occupied = Assets(authority.State);
            Assert.That(LadderCommand(authority, MinerId, "remove_ladder", lowerId).Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(occupied));
            AssertSuccess(authority.Execute(HelperId, Command("unclip")));

            var removeLower = Command("remove_ladder", lowerId, Ladder(authority, lowerId).Version);
            AssertSuccess(authority.Execute(MinerId, removeLower));
            Assert.That(Player(authority, MinerId).LadderStock, Is.EqualTo(initialStock - 1));
            string once = CampCodec.Encode(authority.State);
            AssertSuccess(authority.Execute(MinerId, removeLower));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(once));
            Assert.That(authority.Execute(MinerId, Command("remove_ladder", lowerId, removeLower.ExpectedVersion)).Success, Is.False);
            Assert.That(Player(authority, MinerId).LadderStock, Is.EqualTo(initialStock - 1));
            PrepareMining(authority, topCell, MinerId);
            AssertSuccess(LadderCommand(authority, MinerId, "remove_ladder", topId));
            Assert.That(authority.State.Ladders, Is.Empty);
            Assert.That(Player(authority, MinerId).LadderStock, Is.EqualTo(initialStock));
            var restarted = new CampAuthority(CampCodec.Clone(_saved), authority.Rules, Persist);
            restarted.InitializeMining();
            Assert.That(restarted.State.Ladders, Is.Empty);
            Assert.That(Player(restarted, MinerId).LadderStock, Is.EqualTo(initialStock));
            Assert.That(Cell(restarted, topCell).Dug && Cell(restarted, lowerCell).Dug, Is.True);
        }

        [Test]
        public void ReinstalledLadderRejectsOldVersionAndDoesNotRefundNewSegment()
        {
            var authority = Create();
            authority.InitializeMining();
            string cellId = CampMap.CellId(1, 0, 0);
            FinishMining(authority, cellId, MinerId);
            PlaceLadder(authority, cellId, MinerId);
            string ladderId = "ladder:" + cellId;
            int firstVersion = Ladder(authority, ladderId).Version;
            AssertSuccess(LadderCommand(authority, MinerId, "remove_ladder", ladderId));
            PlaceLadder(authority, cellId, MinerId);
            int installedStock = Player(authority, MinerId).LadderStock;
            Assert.That(Ladder(authority, ladderId).Version, Is.GreaterThan(firstVersion));

            var stale = authority.Execute(MinerId, Command("remove_ladder", ladderId, firstVersion));

            Assert.That(stale.Success, Is.False);
            Assert.That(authority.State.Ladders.Count(l => l.Id == ladderId), Is.EqualTo(1));
            Assert.That(Player(authority, MinerId).LadderStock, Is.EqualTo(installedStock));
        }

        [Test]
        public void AssistantIsExclusiveAndLeavingReleasesTheRoleWithoutMovingPlayers()
        {
            var authority = Create();
            authority.InitializeMining();
            Mutate(authority, state =>
            {
                foreach (var player in state.Players) player.Position = Vector3.zero;
                state.Items.Add(Item("first-heavy", ItemKind.HeavyOre, ItemPlace.Hand, MinerId));
            });
            Vector3 helperPosition = Player(authority, HelperId).Position;
            AssertSuccess(ItemCommand(authority, HelperId, "assist", "first-heavy"));
            Assert.That(Ore(authority, "first-heavy").AssistantId, Is.EqualTo(HelperId));
            Assert.That(Ore(authority, "first-heavy").OwnerId, Is.EqualTo(MinerId));
            Assert.That(Ore(authority, "first-heavy").Place, Is.EqualTo(ItemPlace.Hand));
            string assigned = Assets(authority.State);
            Assert.That(ItemCommand(authority, ThirdId, "assist", "first-heavy").Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(assigned));
            Assert.That(ItemCommand(authority, ThirdId, "leave_assist", "first-heavy").Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(assigned));

            Mutate(authority, state => state.Items.Add(Item("second-heavy", ItemKind.HeavyOre, ItemPlace.Hand, ThirdId)));
            string beforeSecond = Assets(authority.State);
            Assert.That(ItemCommand(authority, HelperId, "assist", "second-heavy").Success, Is.False);
            Assert.That(Assets(authority.State), Is.EqualTo(beforeSecond));
            Assert.That(Ore(authority, "second-heavy").AssistantId, Is.Empty);

            var leave = Command("leave_assist", "first-heavy", Ore(authority, "first-heavy").Version);
            AssertSuccess(authority.Execute(HelperId, leave));
            Assert.That(Ore(authority, "first-heavy").AssistantId, Is.Empty);
            Assert.That(Ore(authority, "first-heavy").OwnerId, Is.EqualTo(MinerId));
            Assert.That(Ore(authority, "first-heavy").Place, Is.EqualTo(ItemPlace.Hand));
            Assert.That(Player(authority, HelperId).Position, Is.EqualTo(helperPosition));
            string left = CampCodec.Encode(authority.State);
            AssertSuccess(authority.Execute(HelperId, leave));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(left));
            AssertSuccess(ItemCommand(authority, HelperId, "assist", "second-heavy"));
            Assert.That(Ore(authority, "second-heavy").AssistantId, Is.EqualTo(HelperId));
            Assert.That(authority.State.Items.Count(i => i.AssistantId == HelperId), Is.EqualTo(1));
            Assert.That(Player(authority, HelperId).Position, Is.EqualTo(helperPosition));
        }

        private CampAuthority Create(int seed = 13579)
        {
            var state = CampAuthority.NewWorld("mining rule test", seed);
            int colorIndex = 0;
            foreach (string id in new[] { MinerId, HelperId, ThirdId })
                state.Players.Add(new CampPlayer
                {
                    Id = id, Name = id.Substring(0, 1), CredentialHash = CampAuthority.Hash("test-only mining credential " + id),
                    Balance = 100, Durability = 100, ToolLevel = 1, Position = CampAuthority.Spawn, ColorIndex = colorIndex++
                });
            return new CampAuthority(state, new CampRules(), Persist);
        }

        private void Persist(CampState state)
        {
            _saved = CampCodec.Clone(state);
            _saveCount++;
        }

        private static void Mutate(CampAuthority authority, Action<CampState> edit)
        {
            var next = CampCodec.Clone(authority.State);
            edit(next);
            authority.Commit(next);
        }

        private CampCommand Command(string action, string target = "", int version = 0) =>
            new CampCommand { RequestId = "mining-test-" + ++_requestNumber, Action = action, TargetId = target, ExpectedVersion = version };

        private static CampPlayer Player(CampAuthority authority, string id) => authority.State.Players.Single(p => p.Id == id);
        private static CampCell Cell(CampAuthority authority, string id) => Cell(authority.State, id);
        private static CampCell Cell(CampState state, string id) => state.Cells.Single(c => c.Id == id);
        private static CampItem[] YieldItems(CampState state, string cellId) =>
            state.Items.Where(i => i.Id.StartsWith("yield:" + cellId + ":", StringComparison.Ordinal)).ToArray();

        private static CampItem Item(string id, ItemKind kind, ItemPlace place, string owner, int quantity = 1) =>
            new CampItem { Id = id, Kind = kind, Place = place, OwnerId = owner, Quantity = quantity, Position = Vector3.up };
        private static CampItem Ore(CampAuthority authority, string id) => authority.State.Items.Single(i => i.Id == id);
        private static CampLadder Ladder(CampAuthority authority, string id) => authority.State.Ladders.Single(l => l.Id == id);
        private static int MaterialQuantity(CampState state, string owner, ItemKind kind) =>
            state.Items.Where(i => i.OwnerId == owner && i.Kind == kind &&
                (i.Place == ItemPlace.Hand || i.Place == ItemPlace.Pocket)).Sum(i => i.Quantity);

        private CampResult ItemCommand(CampAuthority authority, string actor, string action, string itemId) =>
            authority.Execute(actor, Command(action, itemId, Ore(authority, itemId).Version));
        private CampResult LadderCommand(CampAuthority authority, string actor, string action, string ladderId) =>
            authority.Execute(actor, Command(action, ladderId, Ladder(authority, ladderId).Version));
        private void PlaceLadder(CampAuthority authority, string cellId, string actor)
        {
            PrepareMining(authority, cellId, actor);
            AssertSuccess(authority.Execute(actor, Command("place_ladder", cellId, Cell(authority, cellId).Version)));
        }

        private static string CellContents(CampState state) =>
            string.Join("\n", state.Cells.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => JsonUtility.ToJson(c)));

        private static void PrepareMining(CampAuthority authority, string cellId, string actorId)
        {
            var target = Cell(authority, cellId);
            Vector3 feet = CampMap.CellCenter(target, authority.Rules) + Vector3.up * (authority.Rules.CellSize * .5f);
            Mutate(authority, state =>
            {
                var player = state.Players.Single(p => p.Id == actorId);
                player.Position = feet;
                state.OnlineSeconds = Math.Max(state.OnlineSeconds, player.NextMineTime + .01);
            });
        }

        private CampCommand MiningCommand(CampAuthority authority, string cellId, string actorId, int version)
        {
            var command = Command("mine", cellId, version);
            command.Direction = (CampMap.CellCenter(Cell(authority, cellId), authority.Rules) -
                (Player(authority, actorId).Position + Vector3.up * 1.6f)).normalized;
            return command;
        }

        private CampResult Mine(CampAuthority authority, string cellId, string actorId)
        {
            PrepareMining(authority, cellId, actorId);
            return authority.Execute(actorId, MiningCommand(authority, cellId, actorId, Cell(authority, cellId).Version));
        }

        private void FinishMining(CampAuthority authority, string cellId, string actorId)
        {
            int maximumHits = Mathf.CeilToInt(Cell(authority, cellId).Health / authority.Rules.WornMineDamage) + 2;
            for (int hit = 0; hit < maximumHits && !Cell(authority, cellId).Dug; hit++)
                AssertSuccess(Mine(authority, cellId, actorId));
            Assert.That(Cell(authority, cellId).Dug, Is.True, "A finite reachable basic cell must finish within its configured damage bound.");
        }

        private static void AssertSuccess(CampResult result) =>
            Assert.That(result.Success, Is.True, result.Code + ": " + result.Message);

        private static string Assets(CampState state)
        {
            var copy = CampCodec.Clone(state);
            copy.Receipts.Clear();
            copy.Revision = 0;
            copy.SavedUtc = null;
            return CampCodec.Encode(copy);
        }
    }
}
