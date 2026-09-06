using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoldCamp.Demo
{
    [Serializable]
    public sealed class PublicMiner
    {
        public string Id, Name;
        public int ColorIndex, ToolLevel;
        public Vector3 Position;
        public float Yaw;
        public bool CheeksFull;
    }
    [Serializable]
    public sealed class OwnAccount
    {
        public string Id;
        public long Balance;
        public int ToolLevel, DetectorLevel;
        public float Durability;
    }
    [Serializable]
    public sealed class VisibleItem
    {
        public string Id, OwnerId, ContainerId;
        public ItemKind Kind;
        public ItemPlace Place;
        public Vector3 Position;
        public int Version, Quantity, RevealStage;
        public long SaleQuote;
        public string PublicCore;
    }
    [Serializable]
    public sealed class CampView
    {
        public string WorldId, Name, SavedUtc;
        public long Revision;
        public OwnAccount Self;
        public List<PublicMiner> Players = new List<PublicMiner>();
        public List<VisibleItem> Items = new List<VisibleItem>();
    }
    public static class CampProjection
    {
        public static CampView ForPlayer(CampAuthority authority, string playerId)
        {
            CampState state = authority.State;
            var view = new CampView { WorldId = state.WorldId, Name = state.Name, Revision = state.Revision, SavedUtc = state.SavedUtc };
            foreach (var p in state.Players)
            {
                view.Players.Add(new PublicMiner { Id = p.Id, Name = p.Name, Position = p.Position,
                    Yaw = p.Yaw, ColorIndex = p.ColorIndex, ToolLevel = p.ToolLevel,
                    CheeksFull = state.Items.Exists(i => i.OwnerId == p.Id && i.Place == ItemPlace.Mouth) });
                if (p.Id == playerId) view.Self = new OwnAccount { Id = p.Id, Balance = p.Balance,
                    ToolLevel = p.ToolLevel, DetectorLevel = p.DetectorLevel, Durability = p.Durability };
            }
            foreach (var i in state.Items)
            {
                if (i.Place == ItemPlace.Sold) continue;
                if ((i.Place == ItemPlace.Cabinet || i.Place == ItemPlace.Pocket || i.Place == ItemPlace.Mouth) && i.OwnerId != playerId) continue;
                view.Items.Add(new VisibleItem { Id = i.Id, OwnerId = i.OwnerId, ContainerId = i.ContainerId,
                    Kind = i.Kind, Place = i.Place, Position = i.Position, Version = i.Version,
                    Quantity = i.Quantity, RevealStage = i.RevealStage, SaleQuote = authority.Price(i),
                    PublicCore = i.RevealStage >= 2 ? i.CoreKind : "" });
            }
            return view;
        }
    }
}
