using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    // Host-domain geometry and transactions only. Fixture positions deliberately
    // isolate rule boundaries; they are not player movement or physics evidence.
    public sealed class CampProgressionMiningTests
    {
        private const string MinerId = "e5555555555555555555555555555555";
        private CampState _saved;
        private int _request, _saves;
        private bool _failSave;

        [SetUp]
        public void SetUp()
        {
            _saved = null;
            _request = _saves = 0;
            _failSave = false;
        }

        [Test]
        public void LockedAdvancedMineRejectsEvenAnAimedUpgradedToolWithoutChangingEitherPit()
        {
            var authority = Create(unlocked: false, toolLevel: 2);
            string target = Id(CampMap.AdvancedPitId, 0, 0);
            PrepareTop(authority, target);
            var result = RejectUnchanged(authority, MineCommand(authority, target));
            Assert.That(result.Code, Is.EqualTo("road_locked"));
            Assert.That(Cell(authority, target).Dug, Is.False);
            Assert.That(Yields(authority.State, target), Is.Empty);
            Assert.That(authority.State.RoadCoinsPaid, Is.Zero);
            Assert.That(authority.State.RoadIronPaid, Is.Zero);
        }

        [Test]
        public void WornBasicToolCanMineAdvancedOrdinaryColumnButHardRockRequiresPurchasedUpgrade()
        {
            var authority = Create();
            string ordinary = Id(CampMap.AdvancedPitId, 0, 0);
            string hard = Id(CampMap.AdvancedPitId, 1, 0);
            Mutate(authority, s => { s.Players[0].Durability = 0; s.Players[0].Balance = 0; });
            Assert.That(Cell(authority, ordinary).RequiredTool, Is.EqualTo(1));
            Assert.That(new[] { ItemKind.Stone, ItemKind.Iron }, Does.Contain(Cell(authority, ordinary).YieldKind));
            Finish(authority, ordinary);
            Assert.That(Yields(authority.State, ordinary).Length, Is.EqualTo(1));
            Assert.That(Player(authority).Balance, Is.Zero);
            Assert.That(Player(authority).Durability, Is.Zero);
            PrepareTop(authority, hard);
            Assert.That(RejectUnchanged(authority, MineCommand(authority, hard)).Code, Is.EqualTo("tool"));
            Assert.That(Yields(authority.State, hard), Is.Empty);

            // Funds and location are fixture setup; the actual upgrade still uses
            // the public purchase command before attempting the same hard cell.
            Mutate(authority, s => { s.Players[0].Balance = authority.Rules.UpgradePrice; s.Players[0].Position = CampMap.RepairPoint; });
            Success(authority.Execute(MinerId, Command("upgrade_tool", version: Player(authority).ToolLevel)));
            Assert.That(Player(authority).ToolLevel, Is.EqualTo(2));
            Assert.That(Player(authority).Balance, Is.Zero);
            Finish(authority, hard);
            Assert.That(Cell(authority, hard).Dug, Is.True);
            Assert.That(Yields(authority.State, hard).Length, Is.EqualTo(1));
        }

        [Test]
        public void DugStarterNeighborCannotExposeAnAdvancedOuterSideAtTheSameGridCoordinates()
        {
            var authority = Create(toolLevel: 2);
            Assert.That(authority.Rules.PitWidth, Is.GreaterThan(authority.Rules.AdvancedWidth),
                "This fixture needs an existing starter neighbor beyond the advanced pit boundary.");
            int outsideX = authority.Rules.AdvancedWidth;
            string starterNeighbor = Id(CampMap.StarterPit, outsideX, 0);
            string target = Id(CampMap.AdvancedPitId, outsideX - 1, 0);
            Finish(authority, starterNeighbor);
            string starterBefore = PitCells(authority.State, CampMap.StarterPit);
            Assert.That(authority.State.Cells.Any(c => c.PitId == CampMap.AdvancedPitId && c.X == outsideX && c.Depth == 0 && c.Z == 0), Is.False);

            Vector3 center = CampMap.CellCenter(Cell(authority, target), authority.Rules);
            Vector3 eye = center + Vector3.right * (authority.Rules.CellSize * .5f + .6f);
            Prepare(authority, eye - Vector3.up * 1.6f);
            var command = MineCommand(authority, target);
            command.Direction = Vector3.left;
            // No other advanced cell lies in front of this ray: the exposure rule
            // itself must reject borrowing the dug starter x=4 neighbor.
            Assert.That(RejectUnchanged(authority, command).Code, Is.EqualTo("blocked"));
            Assert.That(Cell(authority, target).Dug, Is.False);
            Assert.That(Yields(authority.State, target), Is.Empty);
            float health = Cell(authority, target).Health;
            PrepareTop(authority, target);
            Success(authority.Execute(MinerId, MineCommand(authority, target)));
            Assert.That(Cell(authority, target).Health, Is.LessThan(health), "The same cell remains legitimately accessible from its top.");
            Assert.That(PitCells(authority.State, CampMap.StarterPit), Is.EqualTo(starterBefore));
        }

        [Test]
        public void AdvancedLadderNeedsItsOwnUpperSupportAndCannotBlockRemovalOfStarterLadder()
        {
            var authority = Create();
            string starterTop = Id(CampMap.StarterPit, 0, 0);
            string advancedTop = Id(CampMap.AdvancedPitId, 0, 0);
            string advancedLower = Id(CampMap.AdvancedPitId, 0, 1);
            Finish(authority, starterTop);
            Place(authority, starterTop);
            string starterLadder = "ladder:" + starterTop;
            Finish(authority, advancedTop);
            Finish(authority, advancedLower);
            PrepareTop(authority, advancedLower);
            Assert.That(RejectUnchanged(authority, Command("place_ladder", advancedLower,
                Cell(authority, advancedLower).Version)).Code, Is.EqualTo("support"));
            Assert.That(authority.State.Ladders.Count, Is.EqualTo(1));

            Place(authority, advancedTop);
            Place(authority, advancedLower);
            Assert.That(authority.State.Ladders.Count, Is.EqualTo(3));
            PrepareTop(authority, advancedTop);
            var advancedLadder = authority.State.Ladders.Single(l => l.CellId == advancedTop);
            Assert.That(RejectUnchanged(authority, Command("remove_ladder", advancedLadder.Id, advancedLadder.Version)).Code, Is.EqualTo("support"));
            PrepareTop(authority, starterTop);
            var originalLadder = authority.State.Ladders.Single(l => l.Id == starterLadder);
            Success(authority.Execute(MinerId, Command("remove_ladder", originalLadder.Id, originalLadder.Version)));
            Assert.That(authority.State.Ladders.Count, Is.EqualTo(2));
            Assert.That(authority.State.Ladders.All(l => l.PitId == CampMap.AdvancedPitId), Is.True);
            Assert.That(authority.State.Ladders.Select(l => l.CellId), Is.EquivalentTo(new[] { advancedTop, advancedLower }));
        }

        [Test]
        public void ReloadRetainsAdvancedDugCellsYieldsLaddersAndSavedGeometryDespiteNewConfig()
        {
            var authority = Create(toolLevel: 2);
            string upper = Id(CampMap.AdvancedPitId, 0, 0), lower = Id(CampMap.AdvancedPitId, 0, 1);
            Finish(authority, upper);
            Place(authority, upper);
            Finish(authority, lower);
            Place(authority, lower);
            authority.Save();
            string committed = CampCodec.Encode(_saved);
            Vector3 originalCenter = CampMap.CellCenter(Cell(authority, lower), authority.Rules);
            var ladder = authority.State.Ladders.Single(l => l.CellId == lower);
            Vector3 originalLadderCenter = CampMap.LadderCenter(ladder, authority.Rules);
            var changedRules = new CampRules { CellSize = 7, PitWidth = 2, PitLength = 2, PitDepth = 2,
                AdvancedWidth = 1, AdvancedLength = 1, AdvancedDepth = 1, BaseCellHealth = 9999, GoldWeight = 9999 };
            var reloaded = new CampAuthority(CampCodec.Clone(_saved), changedRules, Persist);
            int saves = _saves;
            reloaded.InitializeMining();
            reloaded.InitializeProgression();
            Assert.That(CampCodec.Encode(reloaded.State), Is.EqualTo(committed));
            Assert.That(_saves, Is.EqualTo(saves));
            Assert.That(reloaded.Rules.CellSize, Is.EqualTo(authority.State.MineCellSize));
            Assert.That(reloaded.Rules.AdvancedWidth, Is.EqualTo(authority.State.AdvancedWidth));
            Assert.That(reloaded.Rules.AdvancedLength, Is.EqualTo(authority.State.AdvancedLength));
            Assert.That(reloaded.Rules.AdvancedDepth, Is.EqualTo(authority.State.AdvancedDepth));
            Assert.That(reloaded.Rules.PitWidth, Is.EqualTo(authority.State.MineWidth));
            Assert.That(reloaded.Rules.PitLength, Is.EqualTo(authority.State.MineLength));
            Assert.That(reloaded.Rules.PitDepth, Is.EqualTo(authority.State.MineDepth));
            Assert.That(CampMap.CellCenter(Cell(reloaded, lower), reloaded.Rules), Is.EqualTo(originalCenter));
            Assert.That(CampMap.LadderCenter(reloaded.State.Ladders.Single(l => l.CellId == lower), reloaded.Rules), Is.EqualTo(originalLadderCenter));
            Vector3 matchingStarter = CampMap.CellCenter(Cell(reloaded, Id(CampMap.StarterPit, 0, 1)), reloaded.Rules);
            Assert.That(originalCenter - matchingStarter, Is.EqualTo(CampMap.AdvancedOrigin - CampMap.PitOrigin));
            foreach (string id in new[] { upper, lower })
            {
                Assert.That(Cell(reloaded, id).Dug, Is.True);
                Assert.That(Yields(reloaded.State, id).Length, Is.EqualTo(1));
                PrepareTop(reloaded, id);
                Assert.That(RejectUnchanged(reloaded, MineCommand(reloaded, id)).Code, Is.EqualTo("dug"));
            }
            Assert.DoesNotThrow(() => CampAuthority.Validate(reloaded.State));
        }

        [Test]
        public void FailedAdvancedFinalHitCannotPublishHoleOrYieldAndRetryProducesSameIdOnce()
        {
            var authority = Create(toolLevel: 2);
            string target = Id(CampMap.AdvancedPitId, 0, 0);
            float damage = authority.Rules.BaseMineDamage * Player(authority).ToolLevel;
            for (int hit = 0; hit < 20 && Cell(authority, target).Health > damage; hit++)
            {
                PrepareTop(authority, target);
                Success(authority.Execute(MinerId, MineCommand(authority, target)));
            }
            Assert.That(Cell(authority, target).Health, Is.InRange(float.Epsilon, damage));
            PrepareTop(authority, target);
            var finalHit = MineCommand(authority, target);
            string before = CampCodec.Encode(authority.State), saved = CampCodec.Encode(_saved);
            string starter = PitCells(authority.State, CampMap.StarterPit);
            _failSave = true;
            var failed = authority.Execute(MinerId, finalHit);
            Assert.That(failed.Code, Is.EqualTo("save_failed"));
            Assert.That(failed.Success, Is.False);
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(before));
            Assert.That(CampCodec.Encode(_saved), Is.EqualTo(saved));
            Assert.That(Yields(authority.State, target), Is.Empty);
            _failSave = false;
            Success(authority.Execute(MinerId, finalHit));
            Assert.That(Cell(authority, target).Dug, Is.True);
            Assert.That(Yields(authority.State, target).Select(i => i.Id), Is.EqualTo(new[] { "yield:" + target + ":0" }));
            Assert.That(PitCells(authority.State, CampMap.StarterPit), Is.EqualTo(starter));
            string committed = CampCodec.Encode(authority.State);
            Success(authority.Execute(MinerId, finalHit));
            Assert.That(CampCodec.Encode(authority.State), Is.EqualTo(committed));
        }

        private CampAuthority Create(bool unlocked = true, int toolLevel = 1)
        {
            var state = CampAuthority.NewWorld("Two pit rule fixture", 58263);
            state.Players.Add(new CampPlayer { Id = MinerId, Name = "Pit tester", ToolLevel = toolLevel,
                CredentialHash = CampAuthority.Hash("isolated-two-pit-test-credential"), Balance = 1000, LadderStock = 4 });
            var authority = new CampAuthority(state, new CampRules(), Persist);
            authority.InitializeMining();
            authority.InitializeProgression();
            if (unlocked)
                Mutate(authority, s =>
                {
                    // Historical paid progress is fixture setup, not an alternative
                    // purchase implementation; contributions have their own tests.
                    s.RoadCoinsPaid = s.RoadRequiredCoins; s.RoadIronPaid = s.RoadRequiredIron;
                    s.RoadUnlocked = true; s.RoadVersion = 1;
                    s.Contributions.Add(new CampContribution { PlayerId = MinerId, Coins = s.RoadRequiredCoins, Iron = s.RoadRequiredIron });
                });
            return authority;
        }
        private void Persist(CampState state)
        {
            if (_failSave) throw new IOException("Injected advanced mine save failure.");
            _saved = CampCodec.Clone(state);
            _saves++;
        }
        private static string Id(string pit, int x, int depth) => CampMap.CellId(pit, x, depth, 0);
        private static CampPlayer Player(CampAuthority authority) => authority.State.Players.Single(p => p.Id == MinerId);
        private static CampCell Cell(CampAuthority authority, string id) => authority.State.Cells.Single(c => c.Id == id);
        private static CampItem[] Yields(CampState state, string id) => state.Items.Where(i => i.Id.StartsWith("yield:" + id + ":", StringComparison.Ordinal)).ToArray();
        private CampCommand Command(string action, string target = "", int version = 0) =>
            new CampCommand { RequestId = "two-pit-" + ++_request, Action = action, TargetId = target, ExpectedVersion = version };
        private CampCommand MineCommand(CampAuthority authority, string id)
        {
            var command = Command("mine", id, Cell(authority, id).Version);
            command.Direction = (CampMap.CellCenter(Cell(authority, id), authority.Rules) - (Player(authority).Position + Vector3.up * 1.6f)).normalized;
            return command;
        }
        private static void Prepare(CampAuthority authority, Vector3 feet) => Mutate(authority, s =>
        {
            var actor = s.Players.Single(p => p.Id == MinerId);
            actor.Position = feet;
            s.OnlineSeconds = Math.Max(s.OnlineSeconds, Math.Max(actor.NextMineTime, actor.ActionUntil) + .1);
        });
        private static void PrepareTop(CampAuthority authority, string id) =>
            Prepare(authority, CampMap.CellCenter(Cell(authority, id), authority.Rules) + Vector3.up * (authority.Rules.CellSize * .5f));
        private void Finish(CampAuthority authority, string id)
        {
            int maximumHits = Mathf.CeilToInt(Cell(authority, id).Health / authority.Rules.WornMineDamage) + 2;
            for (int hit = 0; hit < maximumHits && !Cell(authority, id).Dug; hit++)
            {
                PrepareTop(authority, id);
                Success(authority.Execute(MinerId, MineCommand(authority, id)));
            }
            Assert.That(Cell(authority, id).Dug, Is.True, "A reachable cell must finish within its configured damage bound.");
        }
        private void Place(CampAuthority authority, string id)
        {
            PrepareTop(authority, id);
            Success(authority.Execute(MinerId, Command("place_ladder", id, Cell(authority, id).Version)));
        }
        private static void Mutate(CampAuthority authority, Action<CampState> change)
        {
            var next = CampCodec.Clone(authority.State);
            change(next);
            authority.Commit(next);
        }
        private static string PitCells(CampState state, string pit) => string.Join("\n",
            state.Cells.Where(c => c.PitId == pit).OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => JsonUtility.ToJson(c)));
        private static string Assets(CampState state)
        {
            var copy = CampCodec.Clone(state);
            copy.Receipts.Clear(); copy.Revision = 0; copy.SavedUtc = null;
            return CampCodec.Encode(copy);
        }
        private static CampResult RejectUnchanged(CampAuthority authority, CampCommand command)
        {
            string before = Assets(authority.State);
            var result = authority.Execute(MinerId, command);
            Assert.That(result.Success, Is.False, "Unexpected success: " + command.Action);
            Assert.That(result.Code, Is.Not.EqualTo("save_failed"), result.Message);
            Assert.That(Assets(authority.State), Is.EqualTo(before));
            return result;
        }
        private static void Success(CampResult result) => Assert.That(result.Success, Is.True, result.Code + ": " + result.Message);
    }
}
