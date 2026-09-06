using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed partial class CampAuthority
    {
        private double _nextWorldTick;
        public void TickWorld(ICollection<string> onlineIds)
        {
            if (State.OnlineSeconds < _nextWorldTick) return;
            _nextWorldTick = State.OnlineSeconds + .5;
            bool escape = State.Players.Any(p => onlineIds.Contains(p.Id) && p.EscapeAt > 0 && p.EscapeAt <= State.OnlineSeconds);
            bool helpers = State.Items.Any(i => !string.IsNullOrEmpty(i.AssistantId) &&
                (!onlineIds.Contains(i.AssistantId) || !onlineIds.Contains(i.OwnerId) ||
                 Vector3.Distance(State.Players.Find(p => p.Id == i.AssistantId).Position, State.Players.Find(p => p.Id == i.OwnerId).Position) > 4f));
            bool river = State.MineInitialized && State.OnlineSeconds >= State.NextRiverSpawn &&
                !State.Players.Any(p => onlineIds.Contains(p.Id) && Vector3.Distance(p.Position, CampMap.RiverPoint) < 10f);
            if (!escape && !helpers && !river) return;
            var next = CampCodec.Clone(State);
            foreach (var p in next.Players.Where(p => onlineIds.Contains(p.Id) && p.EscapeAt > 0 && p.EscapeAt <= next.OnlineSeconds))
            {
                DropCargo(next, p, "escape");
                p.Position = Spawn; p.EscapeAt = 0; p.ClimbingLadderId = "";
            }
            foreach (var item in next.Items.Where(i => !string.IsNullOrEmpty(i.AssistantId)))
                if (!onlineIds.Contains(item.AssistantId) || !onlineIds.Contains(item.OwnerId) ||
                    Vector3.Distance(next.Players.Find(p => p.Id == item.AssistantId).Position, next.Players.Find(p => p.Id == item.OwnerId).Position) > 4f)
                { item.AssistantId = ""; item.Version++; }
            if (river)
            {
                for (int slot = 0; slot < 4; slot++)
                {
                    string prefix = "river:" + slot;
                    if (next.Items.Any(i => i.Id.StartsWith(prefix, StringComparison.Ordinal) && i.Place != ItemPlace.Sold)) continue;
                    next.Items.Add(new CampItem { Id = prefix + ":" + Guid.NewGuid().ToString("N"), Kind = slot % 2 == 0 ? ItemKind.Stone : ItemKind.Iron,
                        Position = CampMap.RiverPoint + new Vector3(slot % 2 * 1.2f, .5f, slot / 2 * 1.2f) });
                }
                next.NextRiverSpawn = next.OnlineSeconds + Rules.RiverRespawnSeconds;
            }
            Commit(next);
        }

        public void LandHard(string playerId)
        {
            var next = CampCodec.Clone(State);
            var player = next.Players.Find(p => p.Id == playerId);
            if (player == null || player.StunnedUntil > next.OnlineSeconds || player.ProtectionUntil > next.OnlineSeconds) return;
            DropCargo(next, player, "fall");
            player.StunnedUntil = next.OnlineSeconds + 1;
            player.ProtectionUntil = player.StunnedUntil + Rules.ProtectionSeconds;
            player.ClimbingLadderId = "";
            Commit(next);
        }

        private static void DropCargo(CampState state, CampPlayer player, string reason)
        {
            string bag = reason + "-" + Guid.NewGuid().ToString("N");
            foreach (var item in state.Items.Where(i => i.OwnerId == player.Id &&
                (i.Place == ItemPlace.Hand || i.Place == ItemPlace.Pocket || i.Place == ItemPlace.Mouth)))
                SetPlace(item, ItemPlace.Ground, "", item.Kind == ItemKind.HeavyOre ? "" : bag, player.Position + Vector3.up * .5f);
            foreach (var item in state.Items.Where(i => i.AssistantId == player.Id)) { item.AssistantId = ""; item.Version++; }
        }
    }
}
