using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    public sealed class CampProjectionTests
    {
        private const string ViewerId = "33333333333333333333333333333333";
        private const string OtherId = "44444444444444444444444444444444";
        private const long ViewerBalance = 123456789;
        private const long OtherBalance = 987654321;
        private const int SecretValue = 876543219;
        private const string SecretCore = "host-only-core-marker-c4e8";
        private const string SecretInspection = "host-only-inspection-marker-a7f3";

        [Test]
        public void SerializedViewContainsOnlyOwnBalanceAndNoCredentialsOrHostReceipts()
        {
            var authority = Authority(World());
            var view = CampProjection.ForPlayer(authority, ViewerId);
            string json = JsonUtility.ToJson(view);
            var decoded = JsonUtility.FromJson<CampView>(json);

            Assert.That(decoded.Self.Id, Is.EqualTo(ViewerId));
            Assert.That(decoded.Self.Balance, Is.EqualTo(ViewerBalance));
            Assert.That(json, Does.Contain("\"Balance\":" + ViewerBalance));
            Assert.That(Regex.Matches(json, "\"Balance\"\\s*:").Count, Is.EqualTo(1));
            Assert.That(json, Does.Not.Contain(OtherBalance.ToString()));
            Assert.That(decoded.Players.Select(p => p.Id), Is.EquivalentTo(new[] { ViewerId, OtherId }));
            foreach (var player in decoded.Players)
            {
                string publicPlayer = JsonUtility.ToJson(player);
                Assert.That(publicPlayer, Does.Not.Contain("\"Balance\""));
                Assert.That(publicPlayer, Does.Not.Contain("\"CredentialHash\""));
                Assert.That(publicPlayer, Does.Not.Contain("\"DetectorLevel\""));
                Assert.That(publicPlayer, Does.Not.Contain("\"Durability\""));
            }
            foreach (var player in authority.State.Players)
                Assert.That(json, Does.Not.Contain(player.CredentialHash));
            foreach (string forbidden in new[] { "\"CredentialHash\"", "\"Receipts\"", "\"Fingerprint\"", "\"Seed\"",
                         "private-request-marker", "private-receipt-result-marker" })
                Assert.That(json, Does.Not.Contain(forbidden));
        }

        [Test]
        public void OtherPlayersPrivateSlotsAreAbsentWhileOwnSlotsAndPublicItemsRemainVisible()
        {
            var state = WithUnlockedVehicle(World());
            string[] initialPublicIds = state.Items.Select(i => i.Id).ToArray();
            foreach (var place in new[] { ItemPlace.Pocket, ItemPlace.Mouth, ItemPlace.Cabinet })
            {
                state.Items.Add(Item("own-" + place, place, ViewerId));
                var hidden = Item("private-other-" + place, place, OtherId);
                hidden.ContainerId = "private-container-" + place;
                state.Items.Add(hidden);
            }
            state.Items.Add(Item("world-ground", ItemPlace.Ground));
            state.Items.Add(Item("other-hand", ItemPlace.Hand, OtherId));
            var cargo = Item("public-cart", ItemPlace.Vehicle);
            cargo.ContainerId = CampMap.VehicleCargoContainer(0);
            cargo.Position = CampMap.CargoPoint(state.Vehicle, 0);
            state.Items.Add(cargo);
            state.Items.Add(Item("already-sold", ItemPlace.Sold, ViewerId));
            var authority = Authority(state);
            string json = JsonUtility.ToJson(CampProjection.ForPlayer(authority, ViewerId));
            var decoded = JsonUtility.FromJson<CampView>(json);

            Assert.That(decoded.Items.Select(i => i.Id), Is.EquivalentTo(new[]
            {
                "own-Pocket", "own-Mouth", "own-Cabinet", "world-ground", "other-hand", "public-cart"
            }.Concat(initialPublicIds)));
            foreach (var place in new[] { ItemPlace.Pocket, ItemPlace.Mouth, ItemPlace.Cabinet })
            {
                Assert.That(json, Does.Not.Contain("private-other-" + place));
                Assert.That(json, Does.Not.Contain("private-container-" + place));
                Assert.That(json, Does.Contain("\"Id\":\"own-" + place + "\""));
            }
            Assert.That(json, Does.Not.Contain("already-sold"));
            var publicHand = decoded.Items.Single(i => i.Id == "other-hand");
            Assert.That(publicHand.OwnerId, Is.EqualTo(OtherId));
            Assert.That(publicHand.Place, Is.EqualTo(ItemPlace.Hand));
            Assert.That(publicHand.Version, Is.EqualTo(6));
            Assert.That(publicHand.Quantity, Is.EqualTo(1));
            Assert.That(publicHand.Position, Is.EqualTo(new Vector3(2, 3, 4)));
            var publicCargo = decoded.Items.Single(i => i.Id == "public-cart");
            Assert.That(publicCargo.OwnerId, Is.Empty);
            Assert.That(publicCargo.Place, Is.EqualTo(ItemPlace.Vehicle));
            Assert.That(publicCargo.ContainerId, Is.EqualTo(CampMap.VehicleCargoContainer(0)));
            Assert.That(decoded.Players.Single(p => p.Id == OtherId).CheeksFull, Is.True,
                "The public cheek hint must not reveal the private mouth item ID or contents.");
        }

        [TestCase(0)]
        [TestCase(1)]
        public void UnopenedRawStoneJsonCannotRevealCoreValueOrInspectionHistory(int revealStage)
        {
            var state = World();
            var raw = Item("public-raw-stone", ItemPlace.Ground);
            raw.Kind = ItemKind.RawStone;
            raw.RevealStage = revealStage;
            raw.HiddenValue = SecretValue;
            raw.CoreKind = SecretCore;
            raw.InspectedBy.Add(SecretInspection);
            state.Items.Add(raw);
            var authority = Authority(state);
            string json = JsonUtility.ToJson(CampProjection.ForPlayer(authority, ViewerId));
            var projected = JsonUtility.FromJson<CampView>(json).Items.Single();

            Assert.That(projected.Id, Is.EqualTo(raw.Id));
            Assert.That(projected.RevealStage, Is.EqualTo(revealStage));
            Assert.That(projected.PublicCore, Is.Empty);
            Assert.That(projected.SaleQuote, Is.EqualTo(authority.Rules.RawUnopenedPrice));
            foreach (string forbidden in new[] { "\"HiddenValue\"", "\"CoreKind\"", "\"InspectedBy\"",
                         SecretValue.ToString(), SecretCore, SecretInspection })
                Assert.That(json, Does.Not.Contain(forbidden));

            // Secret changes must have no observable effect on the serialized view.
            // This catches indirect leakage through a quote as well as extra fields.
            var changedSecrets = CampCodec.Clone(state);
            changedSecrets.Items[0].HiddenValue = 234567891;
            changedSecrets.Items[0].CoreKind = "different-host-only-core";
            changedSecrets.Items[0].InspectedBy.Clear();
            changedSecrets.Items[0].InspectedBy.Add("different-host-only-inspector");
            string changedJson = JsonUtility.ToJson(CampProjection.ForPlayer(Authority(changedSecrets), ViewerId));
            Assert.That(changedJson, Is.EqualTo(json));
        }

        [Test]
        public void OpenedStoneRevealsExplicitPublicResultWithoutHostOnlyFieldNames()
        {
            var state = World();
            var raw = Item("opened-raw-stone", ItemPlace.Ground);
            raw.Kind = ItemKind.RawStone;
            raw.RevealStage = 2;
            raw.HiddenValue = 120;
            raw.CoreKind = "gold-core";
            raw.InspectedBy.Add(SecretInspection);
            state.Items.Add(raw);
            string json = JsonUtility.ToJson(CampProjection.ForPlayer(Authority(state), ViewerId));
            var projected = JsonUtility.FromJson<CampView>(json).Items.Single();

            Assert.That(projected.PublicCore, Is.EqualTo("gold-core"));
            Assert.That(projected.SaleQuote, Is.EqualTo(120));
            Assert.That(json, Does.Contain("\"PublicCore\":\"gold-core\""));
            Assert.That(json, Does.Contain("\"SaleQuote\":120"));
            foreach (string forbidden in new[] { "\"HiddenValue\"", "\"CoreKind\"", "\"InspectedBy\"", SecretInspection })
                Assert.That(json, Does.Not.Contain(forbidden));
        }

        [Test]
        public void UnknownViewerGetsNoAccountOrPrivateInventory()
        {
            var state = World();
            state.Items.Add(Item("viewer-private-pocket", ItemPlace.Pocket, ViewerId));
            state.Items.Add(Item("other-private-cabinet", ItemPlace.Cabinet, OtherId));
            var view = CampProjection.ForPlayer(Authority(state), "unknown-viewer");
            string json = JsonUtility.ToJson(view);

            Assert.That(view.Self, Is.Null);
            Assert.That(view.Items, Is.Empty);
            Assert.That(json, Does.Not.Contain(ViewerBalance.ToString()));
            Assert.That(json, Does.Not.Contain(OtherBalance.ToString()));
            Assert.That(json, Does.Not.Contain("viewer-private-pocket"));
            Assert.That(json, Does.Not.Contain("other-private-cabinet"));
            foreach (var player in state.Players)
                Assert.That(json, Does.Not.Contain(player.CredentialHash));
        }

        private static CampAuthority Authority(CampState state) =>
            new CampAuthority(state, new CampRules(), ignored => Assert.Fail("Creating a read-only view must not persist state."));

        private static CampState World()
        {
            var state = CampAuthority.NewWorld("projection test camp", 24681357);
            state.Players.Add(new CampPlayer { Id = ViewerId, Name = "甲", Balance = ViewerBalance,
                CredentialHash = CampAuthority.Hash("viewer test-only credential"), DetectorLevel = 3, Durability = 85.5f });
            state.Players.Add(new CampPlayer { Id = OtherId, Name = "乙", Balance = OtherBalance,
                CredentialHash = CampAuthority.Hash("other test-only credential"), DetectorLevel = 2, Durability = 68.25f });
            state.Receipts.Add(new CampReceipt
            {
                Key = OtherId + ":private-request-marker", Fingerprint = CampAuthority.Hash("private receipt fingerprint"),
                Result = new CampResult { RequestId = "private-request-marker", Success = true,
                    Message = "private-receipt-result-marker", Amount = OtherBalance }
            });
            return state;
        }

        private static CampState WithUnlockedVehicle(CampState state)
        {
            // Fixture preparation may initialize a world. The actual projection
            // authority below retains its assertion that viewing never persists.
            var setup = new CampAuthority(state, new CampRules(), ignored => { });
            setup.InitializeMining();
            setup.InitializeProgression();
            var next = CampCodec.Clone(setup.State);
            next.RoadCoinsPaid = next.RoadRequiredCoins;
            next.RoadIronPaid = next.RoadRequiredIron;
            next.RoadUnlocked = true;
            next.RoadVersion = 1;
            next.Contributions.Add(new CampContribution
                { PlayerId = OtherId, Coins = next.RoadRequiredCoins, Iron = next.RoadRequiredIron });
            setup.Commit(next);
            return CampCodec.Clone(setup.State);
        }

        private static CampItem Item(string id, ItemPlace place, string owner = "") =>
            new CampItem { Id = id, Kind = ItemKind.Gold, Place = place, OwnerId = owner,
                Position = new Vector3(2, 3, 4), Version = 6, Quantity = 1 };
    }
}
