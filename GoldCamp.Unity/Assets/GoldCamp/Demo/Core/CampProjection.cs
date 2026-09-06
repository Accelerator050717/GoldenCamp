using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    [Serializable]
    public sealed class PublicMiner
    {
        public string Id, Name;
        public int ColorIndex, ToolLevel;
        public int VehicleSeat = -1;
        public Vector3 Position;
        public float Yaw;
        public float Pitch;
        public bool CheeksFull;
        public bool Online;
        public string Action, ClimbingLadderId;
        public double ActionUntil, StunnedUntil;
    }
    [Serializable]
    public sealed class OwnAccount
    {
        public string Id;
        public long Balance;
        public int ToolLevel, DetectorLevel;
        public float Durability;
        public int LadderStock;
        public double EscapeAt;
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
        public string AssistantId;
        public bool WasInspected;
        public int WindowBand;
        public string OwnClue;
    }
    [Serializable]
    public sealed class VisibleCell
    {
        public string Id, PitId;
        public int X, Depth, Z, Version, RequiredTool;
        public bool Dug;
        public float Health;
    }
    [Serializable]
    public sealed class VisibleWager
    {
        public string Id, PlayerA, PlayerB, StoneA, StoneB, WinnerId;
        public int Version;
        public WagerPhase Phase;
        public long Stake, ValueA, ValueB;
        public bool ConfirmedA, ConfirmedB;
    }
    [Serializable]
    public sealed class CampView
    {
        public string WorldId, Name, SavedUtc;
        public long Revision;
        public OwnAccount Self;
        public List<PublicMiner> Players = new List<PublicMiner>();
        public List<VisibleItem> Items = new List<VisibleItem>();
        public List<VisibleCell> Cells = new List<VisibleCell>();
        public List<CampLadder> Ladders = new List<CampLadder>();
        public double OnlineSeconds;
        public CampRules Rules;
        public bool PvpEnabled;
        public VisibleWager Wager;
        public CampVehicle Vehicle;
        public bool RoadUnlocked;
        public long RoadRequiredCoins, RoadCoinsPaid;
        public int RoadRequiredIron, RoadIronPaid, RoadVersion;
        public List<CampContribution> Contributions = new List<CampContribution>();
    }
    public static class CampProjection
    {
        public static CampView ForPlayer(CampAuthority authority, string playerId, ICollection<string> onlineIds = null)
        {
            CampState state = authority.State;
            var view = new CampView { WorldId = state.WorldId, Name = state.Name, Revision = state.Revision,
                SavedUtc = state.SavedUtc, OnlineSeconds = state.OnlineSeconds, Rules = authority.Rules, PvpEnabled = state.PvpEnabled };
            view.RoadUnlocked = state.RoadUnlocked; view.RoadRequiredCoins = state.RoadRequiredCoins; view.RoadCoinsPaid = state.RoadCoinsPaid;
            view.RoadRequiredIron = state.RoadRequiredIron; view.RoadIronPaid = state.RoadIronPaid; view.RoadVersion = state.RoadVersion;
            view.Contributions.AddRange(state.Contributions.Select(c => new CampContribution { PlayerId=c.PlayerId,Coins=c.Coins,Iron=c.Iron }));
            if (state.Vehicle != null) view.Vehicle = JsonUtility.FromJson<CampVehicle>(JsonUtility.ToJson(state.Vehicle));
            foreach (var p in state.Players)
            {
                view.Players.Add(new PublicMiner { Id = p.Id, Name = p.Name, Position = p.Position,
                    Yaw = p.Yaw, Pitch = p.Pitch, ColorIndex = p.ColorIndex, ToolLevel = p.ToolLevel,
                    Online = onlineIds == null || onlineIds.Contains(p.Id), Action = p.Action, ActionUntil = p.ActionUntil,
                    StunnedUntil = p.StunnedUntil, ClimbingLadderId = p.ClimbingLadderId,
                    VehicleSeat = state.Vehicle?.Seats.IndexOf(p.Id) ?? -1,
                    CheeksFull = state.Items.Exists(i => i.OwnerId == p.Id && i.Place == ItemPlace.Mouth) });
                if (p.Id == playerId) view.Self = new OwnAccount { Id = p.Id, Balance = p.Balance,
                    ToolLevel = p.ToolLevel, DetectorLevel = p.DetectorLevel, Durability = p.Durability,
                    LadderStock = p.LadderStock, EscapeAt = p.EscapeAt };
            }
            foreach (var i in state.Items)
            {
                if (i.Place == ItemPlace.Sold) continue;
                bool offered = state.Wager?.Phase == WagerPhase.Draft && i.Kind == ItemKind.RawStone &&
                    ((state.Wager.StoneA == i.Id && state.Wager.PlayerA == i.OwnerId) || (state.Wager.StoneB == i.Id && state.Wager.PlayerB == i.OwnerId));
                if ((i.Place == ItemPlace.Cabinet || i.Place == ItemPlace.Pocket || i.Place == ItemPlace.Mouth) && i.OwnerId != playerId && !(offered && i.Place == ItemPlace.Pocket)) continue;
                view.Items.Add(new VisibleItem { Id = i.Id, OwnerId = i.OwnerId, ContainerId = i.ContainerId,
                    Kind = i.Kind, Place = i.Place, Position = i.Position, Version = i.Version,
                    Quantity = i.Quantity, RevealStage = i.RevealStage, SaleQuote = authority.Price(i),
                    PublicCore = i.RevealStage >= 2 ? i.CoreKind : "", AssistantId = i.AssistantId,
                    WasInspected = i.InspectedBy != null && i.InspectedBy.Count > 0,
                    WindowBand = i.RevealStage >= 1 ? i.RawWindowBand : 0,
                    OwnClue = i.InspectedBy != null && i.InspectedBy.Contains(playerId) ? RawClue(i.RawClueBand) : "" });
            }
            foreach (var c in state.Cells)
                view.Cells.Add(new VisibleCell { Id = c.Id, PitId = c.PitId, X = c.X, Depth = c.Depth, Z = c.Z,
                    Version = c.Version, RequiredTool = c.RequiredTool, Health = c.Health, Dug = c.Dug });
            view.Ladders.AddRange(state.Ladders.Select(l => new CampLadder { Id = l.Id, CellId = l.CellId,
                PitId = l.PitId, X = l.X, Depth = l.Depth, Z = l.Z, Version = l.Version }));
            if (state.Wager != null)
            {
                var w = state.Wager;
                view.Wager = new VisibleWager { Id = w.Id, Version = w.Version, Phase = w.Phase, PlayerA = w.PlayerA,
                    PlayerB = w.PlayerB, StoneA = w.StoneA, StoneB = w.StoneB, Stake = w.Stake,
                    ConfirmedA = w.ConfirmedA, ConfirmedB = w.ConfirmedB, WinnerId = w.Phase == WagerPhase.Settled ? w.WinnerId : "",
                    ValueA = w.Phase == WagerPhase.Settled ? w.ValueA : 0, ValueB = w.Phase == WagerPhase.Settled ? w.ValueB : 0 };
            }
            return view;
        }
        public static string RawClue(int band) => band <= 1 ? "粗检：反应偏弱，仍有不确定性。" : band == 2 ? "粗检：有持续反应，可能含矿。" : "粗检：反应较强，价格需开石确定。";
    }
}
