using System;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    public sealed class CampSnapshotCapacityTests
    {
        [Test]
        public void FullyExcavatedFourPlayerCampFitsReliableSnapshotEnvelope()
        {
            var authority = CreateFullCamp();
            var online = authority.State.Players.Select(player => player.Id).ToArray();
            foreach (string actor in online)
            {
                var view = CampProjection.ForPlayer(authority, actor, online);
                string raw = JsonUtility.ToJson(view);
                string packet = JsonUtility.ToJson(new CampPacket { Type = "view", Payload = raw });
                int bytes = Encoding.UTF8.GetByteCount(packet);
                Assert.That(bytes, Is.GreaterThan(CampTransport.MaximumPayloadBytes), "Retain the previously failing legal long-lived world.");
                var assembler = new CampSnapshotAssembler();
                string restored = null;
                foreach (var part in CampSnapshotCodec.Pack(raw))
                {
                    string wire = JsonUtility.ToJson(new CampPacket { Type = "view-part", Payload = JsonUtility.ToJson(part) });
                    Assert.That(Encoding.UTF8.GetByteCount(wire), Is.LessThanOrEqualTo(CampTransport.MaximumPayloadBytes));
                    if (assembler.TryAccept(part, 1, out string json)) restored = json;
                }
                Assert.That(restored, Is.EqualTo(raw), "No minerals, ladders, assets or private projection fields may be trimmed to fit.");
            }
        }

        public static CampAuthority CreateFullCamp()
        {
            var authority = new CampAuthority(CampAuthority.NewWorld("永久营地容量回归", 6), new CampRules(), _ => { });
            string[] ids = {
                "a1111111111111111111111111111111", "b2222222222222222222222222222222",
                "c3333333333333333333333333333333", "d4444444444444444444444444444444"
            };
            for (int i = 0; i < ids.Length; i++) authority.Join(ids[i], new string((char)('a' + i), 48), "矿工 " + (i + 1));
            authority.InitializeMining();
            authority.InitializeProgression();
            var state = CampCodec.Clone(authority.State);
            state.RoadCoinsPaid = state.RoadRequiredCoins;
            state.RoadIronPaid = state.RoadRequiredIron;
            state.RoadUnlocked = true;
            state.RoadVersion = 2;
            state.Contributions.Add(new CampContribution { PlayerId = ids[0], Coins = state.RoadRequiredCoins, Iron = state.RoadRequiredIron });
            foreach (var player in state.Players) { player.ToolLevel = 2; player.LadderStock = 0; }
            // A long-lived finite mine fixture, not a gameplay speed or earned-money
            // test: replenishing river materials can fund these facilities over time.
            // Keep initialized mineral kinds, core values, IDs and coordinates.
            foreach (var cell in state.Cells)
            {
                cell.Dug = true; cell.Health = 0; cell.Version = 10;
                float height = cell.YieldKind == ItemKind.HeavyOre ? .65f : .35f;
                for (int i = 0; i < cell.YieldCount; i++)
                    state.Items.Add(new CampItem { Id = "yield:" + cell.Id + ":" + i, Kind = cell.YieldKind,
                        Place = ItemPlace.Ground, Position = CampMap.CellCenter(cell, authority.Rules) + Vector3.up * (height - authority.Rules.CellSize * .5f),
                        HiddenValue = cell.HiddenValue, CoreKind = cell.CoreKind, RawClueBand = cell.RawClueBand, RawWindowBand = cell.RawWindowBand });
                state.Ladders.Add(new CampLadder { Id = "ladder:" + cell.Id, CellId = cell.Id, PitId = cell.PitId,
                    X = cell.X, Depth = cell.Depth, Z = cell.Z, Version = 1 });
            }
            Assert.That(state.Cells.Count, Is.EqualTo(224));
            Assert.That(state.Ladders.Count, Is.EqualTo(224));
            Assert.That(state.Items.Select(item => item.Id).Distinct().Count(), Is.EqualTo(state.Items.Count));
            foreach (var ladder in state.Ladders.Where(ladder => ladder.Depth > 0))
                Assert.That(state.Ladders.Any(upper => upper.PitId == ladder.PitId && upper.X == ladder.X && upper.Z == ladder.Z && upper.Depth == ladder.Depth - 1), Is.True);
            CampAuthority.Validate(state);
            authority.Commit(state);
            return authority;
        }
    }
}
