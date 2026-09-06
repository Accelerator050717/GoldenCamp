using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed partial class CampAuthority
    {
        public void InitializeMining()
        {
            if (State.MineInitialized) return;
            var next = CampCodec.Clone(State);
            next.Cells ??= new List<CampCell>();
            next.Ladders ??= new List<CampLadder>();
            Require(next.Cells.Count == 0 && next.Ladders.Count == 0, "save", "矿坑已有数据，拒绝重新生成或覆盖。");
            Require(float.IsFinite(Rules.CellSize) && Rules.CellSize > 0 && Rules.PitWidth > 0
                && Rules.PitLength > 0 && Rules.PitDepth > 0, "rules", "矿坑尺寸配置无效。");
            Require(Rules.BaseCellHealth > 0 && Rules.DepthHealthIncrease >= 0
                && Rules.InitialLadders >= 0 && Rules.RiverRespawnSeconds > 0, "rules", "采矿配置无效。");

            var random = new System.Random(next.Seed);
            for (int depth = 0; depth < Rules.PitDepth; depth++)
            for (int z = 0; z < Rules.PitLength; z++)
            for (int x = 0; x < Rules.PitWidth; x++)
            {
                var kind = PickYield(random, depth);
                var cell = new CampCell
                {
                    Id = CampMap.CellId(x, depth, z), PitId = CampMap.StarterPit,
                    X = x, Depth = depth, Z = z,
                    Health = Rules.BaseCellHealth + Rules.DepthHealthIncrease * depth,
                    YieldKind = kind, YieldCount = 1, RequiredTool = 1, CoreKind = ""
                };
                if (kind == ItemKind.RawStone)
                {
                    // The core and value are rolled exactly once, never at reveal/sale.
                    int core = random.Next(4);
                    cell.CoreKind = new[] { "stone", "iron", "gold", "rich_gold" }[core];
                    int baseValue = core == 0 ? Rules.StonePrice : core == 1 ? Rules.IronPrice
                        : core == 2 ? Rules.GoldPrice : Rules.HeavyPrice;
                    cell.HiddenValue = checked(Math.Max(0, baseValue) * random.Next(1, 4));
                }
                next.Cells.Add(cell);
            }

            for (int i = 0; i < 4; i++)
            {
                string id = "river:" + i;
                Require(!next.Items.Any(item => item.Id == id), "save", "河滩已有资源记录，拒绝覆盖。");
                next.Items.Add(new CampItem
                {
                    Id = id, Kind = i % 2 == 0 ? ItemKind.Stone : ItemKind.Iron,
                    Place = ItemPlace.Ground, ContainerId = "river",
                    Position = CampMap.RiverPoint + new Vector3(i % 2 == 0 ? -1 : 1, .35f, i < 2 ? -1 : 1)
                });
            }
            foreach (var player in next.Players)
                player.LadderStock = Math.Max(player.LadderStock, Rules.InitialLadders);
            next.MineCellSize = Rules.CellSize;
            next.MineWidth = Rules.PitWidth;
            next.MineLength = Rules.PitLength;
            next.MineDepth = Rules.PitDepth;
            next.NextRiverSpawn = next.OnlineSeconds + Rules.RiverRespawnSeconds;
            next.MineInitialized = true;
            Commit(next);
        }

        private ItemKind PickYield(System.Random random, int depth)
        {
            int extra = checked(depth * Rules.DepthValueWeight);
            int[] weights = { Rules.StoneWeight, Rules.IronWeight, checked(Rules.GoldWeight + extra),
                checked(Rules.HeavyWeight + extra), checked(Rules.RawWeight + extra) };
            Require(Rules.DepthValueWeight >= 0 && weights.All(weight => weight >= 0), "rules", "出矿权重配置无效。");
            int total = weights.Aggregate(0, (sum, weight) => checked(sum + weight));
            Require(total > 0, "rules", "至少需要一种可生成矿物。");
            int roll = random.Next(total);
            for (int i = 0; i < weights.Length; i++)
            {
                if (roll < weights[i]) return (ItemKind)i;
                roll -= weights[i];
            }
            throw new InvalidOperationException("出矿配置无效。");
        }

        private CampResult ApplyMining(CampState state, CampPlayer actor, CampCommand command)
        {
            // Release/cancel remains available even during a stun or escape channel.
            if (command.Action == "cancel_escape")
            {
                actor.EscapeAt = 0;
                if (actor.Action == "escape") { actor.Action = ""; actor.ActionUntil = 0; }
                return Ok("已取消返回营地。");
            }
            if (command.Action == "unclip")
            {
                actor.ClimbingLadderId = "";
                return Ok("已离开梯子。");
            }
            if (command.Action == "leave_assist")
            {
                var item = GetItem(state, command);
                Require(item.AssistantId == actor.Id, "ownership", "你没有协助搬运该矿物。");
                item.AssistantId = "";
                item.Version++;
                return Ok("已停止协助搬运。");
            }

            Require(actor.StunnedUntil <= state.OnlineSeconds, "stunned", "暂时无法行动。");
            Require(actor.EscapeAt <= 0, "escaping", "正在返回营地；可先取消。");
            switch (command.Action)
            {
                case "mine": return Mine(state, actor, command);
                case "detect": return Detect(state, actor, command);
                case "repair": return Repair(state, actor, false, command.TargetId);
                case "repair_material": return Repair(state, actor, true, command.TargetId);
                case "craft_ladder":
                    Require(actor.LadderStock < int.MaxValue, "capacity", "梯子库存已满。");
                    ConsumeMaterial(state, actor, ItemKind.Stone, Rules.LadderStoneCost);
                    actor.LadderStock++;
                    ShowAction(state, actor, "craft", .6f);
                    return Ok("已制作一段梯子。", 1);
                case "place_ladder": return PlaceLadder(state, actor, command);
                case "remove_ladder": return RemoveLadder(state, actor, command);
                case "climb": return Climb(state, actor, command);
                case "assist": return Assist(state, actor, command);
                case "pocket":
                {
                    var item = GetItem(state, command);
                    Require(item.OwnerId == actor.Id && item.Place == ItemPlace.Hand, "ownership", "只能收起自己手里的矿物。");
                    Require(item.Kind != ItemKind.HeavyOre, "heavy", "大矿无法放入口袋。");
                    RequirePocketSpace(state, actor);
                    SetPlace(item, ItemPlace.Pocket, actor.Id, "", actor.Position);
                    return Ok("矿物已放入口袋。");
                }
                case "unpocket":
                {
                    var item = GetItem(state, command);
                    Require(item.OwnerId == actor.Id && item.Place == ItemPlace.Pocket, "ownership", "这不是你的口袋物品。");
                    RequireHandsFree(state, actor);
                    SetPlace(item, ItemPlace.Hand, actor.Id, "", actor.Position + Vector3.up);
                    return Ok("已从口袋拿出矿物。");
                }
                case "cabinet_store":
                {
                    var item = GetItem(state, command);
                    Require(item.OwnerId == actor.Id && IsCarriedMaterial(item), "ownership", "只能存放自己携带的物品。");
                    Require(item.Kind != ItemKind.HeavyOre, "heavy", "大矿放不进安全柜，请使用货架搬运。");
                    RequireCabinetAccess(actor);
                    SetPlace(item, ItemPlace.Cabinet, actor.Id, "cabinet:" + actor.Id, CampMap.CabinetPoint(actor.ColorIndex));
                    return Ok("物品已存入你的安全柜。");
                }
                case "cabinet_take":
                {
                    var item = GetItem(state, command);
                    Require(item.OwnerId == actor.Id && item.Place == ItemPlace.Cabinet
                        && item.ContainerId == "cabinet:" + actor.Id, "ownership", "只能取出自己安全柜里的物品。");
                    RequireCabinetAccess(actor);
                    RequireHandsFree(state, actor);
                    SetPlace(item, ItemPlace.Hand, actor.Id, "", actor.Position + Vector3.up);
                    return Ok("已从安全柜取出物品。");
                }
                case "escape":
                    Require(Rules.EscapeSeconds > 0, "rules", "返回营地时间配置无效。");
                    actor.EscapeAt = state.OnlineSeconds + Rules.EscapeSeconds;
                    actor.ClimbingLadderId = "";
                    foreach (var item in state.Items.Where(item => item.AssistantId == actor.Id))
                    { item.AssistantId = ""; item.Version++; }
                    ShowAction(state, actor, "escape", Rules.EscapeSeconds);
                    return Ok("正在返回营地，随身矿物会留在原地；移动或取消可中止。");
                default: throw new RuleFailure("action", "未知操作。");
            }
        }

        private CampResult Mine(CampState state, CampPlayer actor, CampCommand command)
        {
            var cell = GetCell(state, command);
            Require(!cell.Dug, "dug", "这块岩层已经挖空。");
            RequireHandsFree(state, actor);
            Require(string.IsNullOrEmpty(actor.ClimbingLadderId), "climbing", "请先离开梯子再挥镐。");
            Require(!state.Items.Any(item => item.AssistantId == actor.Id), "assisting", "请先停止协助搬运。");
            Require(actor.ToolLevel >= cell.RequiredTool, "tool", "这块岩层需要更强的矿镐。");
            Require(state.OnlineSeconds >= actor.NextMineTime, "cooldown", "请等下一次挥镐。");
            Require(Rules.MineInterval > 0 && Rules.BaseMineDamage > 0 && Rules.WornMineDamage > 0
                && Rules.DurabilityPerHit >= 0, "rules", "矿镐配置无效。");
            RequireMineAim(state, actor, cell, command.Direction);
            float damage = (actor.Durability > 0 ? Rules.BaseMineDamage : Rules.WornMineDamage) * Math.Max(1, actor.ToolLevel);
            actor.Durability = Mathf.Max(0, actor.Durability - Rules.DurabilityPerHit);
            actor.NextMineTime = state.OnlineSeconds + Rules.MineInterval;
            ShowAction(state, actor, "mine", Rules.MineInterval);
            cell.Health = Mathf.Max(0, cell.Health - damage);
            cell.Version++;
            if (cell.Health > 0) return Ok(actor.Durability > 0 ? "敲击岩层。" : "矿镐已磨损，仍能低效开挖。");

            cell.Dug = true;
            for (int i = 0; i < cell.YieldCount; i++)
            {
                string id = "yield:" + cell.Id + ":" + i;
                Require(!state.Items.Any(item => item.Id == id), "save", "该矿格已有产物记录，拒绝重复生成。");
                float height = cell.YieldKind == ItemKind.HeavyOre ? .65f : .35f;
                state.Items.Add(new CampItem
                {
                    Id = id, Kind = cell.YieldKind, Place = ItemPlace.Ground,
                    Position = CampMap.CellCenter(cell, Rules) + Vector3.up * (height - Rules.CellSize * .5f),
                    HiddenValue = cell.HiddenValue, CoreKind = cell.CoreKind
                });
            }
            return Ok("岩层已挖开，矿物留在坑内。", cell.YieldCount);
        }

        private void RequireMineAim(CampState state, CampPlayer actor, CampCell target, Vector3 direction)
        {
            Require(CampCodec.IsFinite(direction) && direction.sqrMagnitude > .0001f, "aim", "请瞄准可挖岩层。");
            var eye = actor.Position + Vector3.up * 1.6f;
            var ray = new Ray(eye, direction.normalized);
            var targetBounds = new Bounds(CampMap.CellCenter(target, Rules), Vector3.one * Rules.CellSize);
            Require(!targetBounds.Contains(eye), "blocked", "不能从实体岩层内部开挖。");
            Require(targetBounds.IntersectRay(ray, out float hitDistance) && hitDistance >= 0
                && hitDistance <= Rules.Reach, "aim", "准星没有对准距离内的岩层表面。");
            Vector3 hitPoint = ray.GetPoint(hitDistance);
            foreach (var cell in state.Cells)
            {
                if (cell.Dug || cell.Id == target.Id) continue;
                var bounds = new Bounds(CampMap.CellCenter(cell, Rules), Vector3.one * Rules.CellSize);
                Require(!bounds.Contains(eye) && (!bounds.IntersectRay(ray, out float distance)
                    || distance >= hitDistance - .001f), "blocked", "前方岩层尚未挖开。");
            }
            Require(IsExposedAt(state, target, hitPoint), "blocked", "请先挖开通往这一面的岩层。");
            Require(_lineOfSight(eye, CampMap.CellCenter(target, Rules)), "blocked", "岩层被遮挡。");
        }

        private bool IsExposedAt(CampState state, CampCell cell, Vector3 point)
        {
            Vector3 relative = point - CampMap.CellCenter(cell, Rules);
            float edge = Rules.CellSize * .5f - .002f;
            if (relative.y >= edge && (cell.Depth == 0 || CellIsDug(state, cell.X, cell.Depth - 1, cell.Z))) return true;
            if (relative.y <= -edge && CellIsDug(state, cell.X, cell.Depth + 1, cell.Z)) return true;
            if (relative.x >= edge && CellIsDug(state, cell.X + 1, cell.Depth, cell.Z)) return true;
            if (relative.x <= -edge && CellIsDug(state, cell.X - 1, cell.Depth, cell.Z)) return true;
            if (relative.z >= edge && CellIsDug(state, cell.X, cell.Depth, cell.Z + 1)) return true;
            return relative.z <= -edge && CellIsDug(state, cell.X, cell.Depth, cell.Z - 1);
        }

        private static bool CellIsDug(CampState state, int x, int depth, int z) =>
            state.Cells.Any(cell => cell.PitId == CampMap.StarterPit && cell.X == x && cell.Depth == depth && cell.Z == z && cell.Dug);

        private CampResult Detect(CampState state, CampPlayer actor, CampCommand command)
        {
            Require(CampCodec.IsFinite(command.Direction), "aim", "探测方向无效。");
            float range = Rules.DetectRange * (1 + Math.Max(0, actor.DetectorLevel) * .25f);
            Require(float.IsFinite(range) && range > 0, "rules", "探测范围配置无效。");
            string playerId = !string.IsNullOrEmpty(command.OtherPlayerId) ? command.OtherPlayerId : command.TargetId;
            var target = state.Players.Find(player => player.Id == playerId);
            ShowAction(state, actor, "detect", .8f);
            if (target != null)
            {
                Require(target.Id != actor.Id, "target", "请瞄准另一位矿工。");
                RequireNear(actor.Position, target.Position, range);
                Require(_lineOfSight(actor.Position + Vector3.up * 1.6f, target.Position + Vector3.up), "blocked", "矿工被遮挡。");
                bool reaction = state.Items.Any(item => item.OwnerId == target.Id &&
                    (IsCarriedMaterial(item) || item.Place == ItemPlace.Mouth));
                return Ok(reaction ? "对方身上有矿物反应。" : "对方身上没有明显矿物反应。");
            }

            Vector3 facing = command.Direction.sqrMagnitude > .0001f ? command.Direction.normalized
                : Quaternion.Euler(0, actor.Yaw, 0) * Vector3.forward;
            var eye = actor.Position + Vector3.up * 1.6f;
            var detected = state.Cells.Where(cell => !cell.Dug && cell.YieldKind != ItemKind.Stone)
                .Select(cell => CampMap.CellCenter(cell, Rules) - eye)
                .Where(offset => offset.magnitude <= range && Vector3.Dot(offset.normalized, facing) > .25f)
                .OrderBy(offset => offset.sqrMagnitude).ToList();
            if (detected.Count == 0) return Ok("这个方向没有明显矿物反应。");
            Vector3 nearest = detected[0];
            if (Mathf.Abs(nearest.y) > new Vector2(nearest.x, nearest.z).magnitude)
                return Ok(nearest.y < 0 ? "下方有矿物反应。" : "上方有矿物反应。");
            var right = Vector3.Cross(Vector3.up, facing).normalized;
            float side = Vector3.Dot(nearest.normalized, right);
            return Ok(side > .3f ? "右前方有矿物反应。" : side < -.3f ? "左前方有矿物反应。" : "前方有矿物反应。");
        }

        private CampResult Repair(CampState state, CampPlayer actor, bool useMaterial, string choice)
        {
            RequireNear(actor.Position, CampMap.RepairPoint, Rules.Reach);
            Require(actor.Durability < 100f, "repaired", "矿镐无需维修。");
            if (useMaterial)
            {
                Require(string.IsNullOrEmpty(choice) || choice == "iron" || choice == "stone", "material", "请选择铁料或石料维修。");
                bool useIron = choice == "iron" || (string.IsNullOrEmpty(choice)
                    && MaterialCount(state, actor, ItemKind.Iron) >= Rules.RepairIronCost);
                ConsumeMaterial(state, actor, useIron ? ItemKind.Iron : ItemKind.Stone,
                    useIron ? Rules.RepairIronCost : Rules.RepairStoneCost);
            }
            else
            {
                Require(Rules.RepairPrice >= 0 && actor.Balance >= Rules.RepairPrice, "funds", "维修费用不足，可改用铁料或石料。");
                actor.Balance -= Rules.RepairPrice;
            }
            actor.Durability = 100f;
            ShowAction(state, actor, "repair", .6f);
            return Ok("矿镐已修好。");
        }

        private static bool IsCarriedMaterial(CampItem item) => item.Place == ItemPlace.Hand || item.Place == ItemPlace.Pocket;
        private static long MaterialCount(CampState state, CampPlayer actor, ItemKind kind) =>
            state.Items.Where(item => item.OwnerId == actor.Id && item.Kind == kind && IsCarriedMaterial(item)).Sum(item => (long)item.Quantity);

        private static void ConsumeMaterial(CampState state, CampPlayer actor, ItemKind kind, int count)
        {
            Require(count > 0, "rules", "材料配方配置无效。");
            Require(MaterialCount(state, actor, kind) >= count, "materials", "携带的材料不足；只使用手中和口袋里的材料。");
            foreach (var item in state.Items.Where(item => item.OwnerId == actor.Id && item.Kind == kind && IsCarriedMaterial(item)))
            {
                int take = Math.Min(count, item.Quantity);
                if (take == item.Quantity) SetPlace(item, ItemPlace.Sold, actor.Id, "material", item.Position);
                else { item.Quantity -= take; item.Version++; }
                count -= take;
                if (count == 0) break;
            }
        }

        private CampResult PlaceLadder(CampState state, CampPlayer actor, CampCommand command)
        {
            var cell = GetCell(state, command);
            Require(cell.Dug, "rock", "先挖空岩格才能放置梯子。");
            Require(actor.LadderStock > 0, "ladders", "没有可放置的梯子，可用石料制作。");
            Require(!state.Ladders.Any(ladder => ladder.CellId == cell.Id), "occupied", "这里已有梯子。");
            Require(cell.Depth == 0 || state.Ladders.Any(ladder => ladder.PitId == cell.PitId
                && ladder.X == cell.X && ladder.Z == cell.Z && ladder.Depth == cell.Depth - 1), "support", "请从入口沿同一列逐段向下铺梯。");
            var placed = new CampLadder { Id = "ladder:" + cell.Id, CellId = cell.Id, PitId = cell.PitId,
                X = cell.X, Depth = cell.Depth, Z = cell.Z };
            RequireLadderReach(actor, placed);
            cell.Version++;
            placed.Version = cell.Version;
            state.Ladders.Add(placed);
            actor.LadderStock--;
            return Ok("梯子已固定。");
        }

        private CampResult RemoveLadder(CampState state, CampPlayer actor, CampCommand command)
        {
            var ladder = GetLadder(state, command);
            RequireLadderReach(actor, ladder);
            Require(!state.Players.Any(player => player.ClimbingLadderId == ladder.Id), "occupied", "有人正在这段梯子上，不能拆除。");
            Require(!state.Ladders.Any(other => other.PitId == ladder.PitId && other.X == ladder.X
                && other.Z == ladder.Z && other.Depth > ladder.Depth), "support", "下面还有依赖这段的梯子，请从最下端回收。");
            Require(actor.LadderStock < int.MaxValue, "capacity", "梯子库存已满。");
            var cell = state.Cells.Find(candidate => candidate.Id == ladder.CellId);
            Require(cell != null && cell.Dug, "save", "梯子所在矿格无效。");
            cell.Version++;
            state.Ladders.Remove(ladder);
            actor.LadderStock++;
            return Ok("已回收一段梯子。", 1);
        }

        private CampResult Climb(CampState state, CampPlayer actor, CampCommand command)
        {
            var ladder = GetLadder(state, command);
            RequireLadderReach(actor, ladder);
            Require(!state.Items.Any(item => item.AssistantId == actor.Id), "assisting", "请先停止协助搬运，再上梯子。");
            Require(state.Cells.Any(cell => cell.Id == ladder.CellId && cell.Dug), "rock", "这段梯子不可达。");
            actor.ClimbingLadderId = ladder.Id;
            return Ok("已扶住梯子；沿上下方向移动可攀爬。");
        }

        private CampResult Assist(CampState state, CampPlayer actor, CampCommand command)
        {
            var item = GetItem(state, command);
            Require(item.Kind == ItemKind.HeavyOre && item.Place == ItemPlace.Hand && item.OwnerId != actor.Id,
                "target", "只能协助另一位矿工搬运手中的大矿。");
            Require(string.IsNullOrEmpty(item.AssistantId), "occupied", "已有矿工协助搬运。");
            RequireHandsFree(state, actor);
            Require(string.IsNullOrEmpty(actor.ClimbingLadderId), "climbing", "请先离开梯子。");
            Require(!state.Items.Any(other => other.AssistantId == actor.Id), "assisting", "你正在协助另一块大矿。");
            var carrier = state.Players.Find(player => player.Id == item.OwnerId);
            Require(carrier != null && carrier.StunnedUntil <= state.OnlineSeconds && carrier.EscapeAt <= 0,
                "carrier", "主搬运者暂时无法搬运。");
            RequireNear(actor.Position, carrier.Position, Rules.Reach);
            RequireNear(actor.Position, item.Position, Rules.Reach);
            Require(_lineOfSight(actor.Position + Vector3.up * 1.6f, item.Position), "blocked", "矿物被遮挡。");
            item.AssistantId = actor.Id;
            item.Version++;
            return Ok("已协助搬运；保持靠近，也可随时退出。");
        }

        private static CampCell GetCell(CampState state, CampCommand command)
        {
            var cell = state.Cells.Find(candidate => candidate.Id == command.TargetId);
            Require(cell != null, "cell", "这里不是可操作的矿格。");
            Require(cell.Version == command.ExpectedVersion, "stale", "岩格状态已变化，请重新操作。");
            return cell;
        }

        private static CampLadder GetLadder(CampState state, CampCommand command)
        {
            var ladder = state.Ladders.Find(candidate => candidate.Id == command.TargetId);
            Require(ladder != null, "ladder", "梯子不存在。");
            Require(ladder.Version == command.ExpectedVersion, "stale", "梯子状态已变化，请重新操作。");
            return ladder;
        }

        private void RequireLadderReach(CampPlayer actor, CampLadder ladder)
        {
            Vector3 center = CampMap.LadderCenter(ladder, Rules);
            RequireNear(actor.Position, center, Rules.Reach);
            Require(_lineOfSight(actor.Position + Vector3.up * 1.6f, center), "blocked", "梯子被遮挡。");
        }

        private static void RequireHandsFree(CampState state, CampPlayer actor) =>
            Require(!state.Items.Any(item => item.OwnerId == actor.Id && item.Place == ItemPlace.Hand), "hands_full", "请先放下手中物品。");

        private void RequirePocketSpace(CampState state, CampPlayer actor) =>
            Require(state.Items.Count(item => item.OwnerId == actor.Id && item.Place == ItemPlace.Pocket) < Rules.PocketSlots,
                "pocket_full", "口袋已满。");

        private void RequireCabinetAccess(CampPlayer actor)
        {
            var cabinet = CampMap.CabinetPoint(actor.ColorIndex);
            RequireNear(actor.Position, cabinet, Rules.Reach);
            Require(_lineOfSight(actor.Position + Vector3.up * 1.6f, cabinet + Vector3.up * 1.3f), "blocked", "安全柜被遮挡。");
        }

        private static void ShowAction(CampState state, CampPlayer actor, string action, float duration)
        {
            actor.Action = action;
            actor.ActionUntil = state.OnlineSeconds + duration;
        }
    }
}
