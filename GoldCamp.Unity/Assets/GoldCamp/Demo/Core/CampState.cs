using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoldCamp.Demo
{
    public enum ItemKind { Stone, Iron, Gold, HeavyOre, RawStone }
    public enum ItemPlace { Ground, Hand, Pocket, Mouth, Cabinet, Vehicle, Escrow, Sold }

    [Serializable]
    public sealed class CampRules
    {
        public float Reach = 3f;
        public float WalkSpeed = 4.5f;
        public float SoloCarryMultiplier = .33f;
        public float TeamCarryMultiplier = .7f;
        public float AutoSaveSeconds = 30f;
        public int PocketSlots = 4;
        public long MoneyLimit = 1000000000;
        public int StonePrice = 3;
        public int IronPrice = 12;
        public int GoldPrice = 55;
        public int HeavyPrice = 140;
        public int RawUnopenedPrice = 35;
        public int RepairPrice = 12;
        public int UpgradePrice = 180;
        public int RoadPrice = 400;
        public int RoadIron = 8;
        public int WindowPrice = 3;
        public int OpenPrice = 5;
        public float StunSeconds = 2f;
        public float ProtectionSeconds = 4f;
        public float RiverRespawnSeconds = 600f;
        public float CellSize = 2f;
        public int PitWidth = 6;
        public int PitLength = 6;
        public int PitDepth = 4;
        public float MineInterval = .55f;
        public float BaseMineDamage = 35f;
        public float WornMineDamage = 8f;
        public float DurabilityPerHit = .35f;
        public float BaseCellHealth = 90f;
        public float DepthHealthIncrease = 20f;
        public int StoneWeight = 48;
        public int IronWeight = 27;
        public int GoldWeight = 10;
        public int HeavyWeight = 8;
        public int RawWeight = 7;
        public int DepthValueWeight = 2;
        public int LadderStoneCost = 2;
        public int RepairIronCost = 1;
        public int RepairStoneCost = 3;
        public int InitialLadders = 2;
        public float DetectRange = 6f;
        public float EscapeSeconds = 4f;
        public float JumpSpeed = 6f;
        public float Gravity = 18f;
        public float ClimbSpeed = 2f;
    }

    [Serializable]
    public sealed class CampPlayer
    {
        public string Id;
        public string CredentialHash;
        public string Name;
        public int ColorIndex;
        public Vector3 Position;
        public float Yaw;
        public float Pitch;
        public long Balance;
        public int ToolLevel = 1;
        public float Durability = 100f;
        public int DetectorLevel;
        public int LadderStock = 2;
        public string ClimbingLadderId = "";
        public double NextMineTime;
        public double EscapeAt;
        public double StunnedUntil;
        public double ProtectionUntil;
        public string Action = "";
        public double ActionUntil;
    }

    [Serializable]
    public sealed class CampItem
    {
        public string Id;
        public ItemKind Kind;
        public ItemPlace Place;
        public string OwnerId = "";
        public string ContainerId = "";
        public Vector3 Position;
        public Vector3 Velocity;
        public int Version;
        public int Quantity = 1;
        // Host/save only. Never send this type directly to a client.
        public int HiddenValue;
        public string CoreKind;
        public int RevealStage;
        public List<string> InspectedBy = new List<string>();
        public string AssistantId = "";
    }

    [Serializable]
    public sealed class CampReceipt
    {
        public string Key;
        public string Fingerprint;
        public CampResult Result;
    }

    [Serializable]
    public sealed class CampCell
    {
        public string Id, PitId;
        public int X, Depth, Z, Version;
        public float Health;
        public bool Dug;
        // Host-only fixed contents. CampView uses VisibleCell instead.
        public ItemKind YieldKind;
        public int YieldCount = 1, HiddenValue, RequiredTool = 1;
        public string CoreKind;
    }

    [Serializable]
    public sealed class CampLadder
    {
        public string Id, CellId, PitId;
        public int X, Depth, Z, Version;
    }

    [Serializable]
    public sealed class CampState
    {
        public int SchemaVersion = 1;
        public string WorldId;
        public string Name;
        public int Seed;
        public long Revision;
        public string SavedUtc;
        public double OnlineSeconds;
        public bool PvpEnabled = true;
        public bool MineInitialized;
        public float MineCellSize;
        public int MineWidth, MineLength, MineDepth;
        public double NextRiverSpawn;
        public List<CampPlayer> Players = new List<CampPlayer>();
        public List<CampItem> Items = new List<CampItem>();
        public List<CampReceipt> Receipts = new List<CampReceipt>();
        public List<CampCell> Cells = new List<CampCell>();
        public List<CampLadder> Ladders = new List<CampLadder>();
    }

    [Serializable]
    public sealed class CampCommand
    {
        public string RequestId;
        public string Action;
        public string TargetId;
        public string OtherPlayerId;
        public int ExpectedVersion;
        public long Amount;
        public Vector3 Direction;
    }

    [Serializable]
    public sealed class CampResult
    {
        public string RequestId;
        public bool Success;
        public string Code;
        public string Message;
        public long Amount;
        public long Revision;
    }

    public static class CampCodec
    {
        public static string Encode(CampState state) => JsonUtility.ToJson(state);
        public static CampState Decode(string json)
        {
            var state = JsonUtility.FromJson<CampState>(json);
            // Additive v0.3 checkpoint migration: A worlds predate finite mines.
            if (state != null && !state.MineInitialized)
            {
                state.Cells ??= new List<CampCell>();
                state.Ladders ??= new List<CampLadder>();
            }
            if (state == null || state.SchemaVersion != 1 || !Guid.TryParseExact(state.WorldId, "N", out _)
                || state.Players == null || state.Items == null || state.Receipts == null)
                throw new InvalidOperationException("不支持或损坏的营地存档；保留原档，不创建替代档。");
            return state;
        }
        public static CampState Clone(CampState state) => Decode(Encode(state));
        public static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
