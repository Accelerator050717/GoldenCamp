using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed partial class CampAuthority
    {
        // A separate deterministic stream leaves every existing starter cell untouched.
        private const int AdvancedSeedSalt = 0x67AC012D;

        public void InitializeProgression()
        {
            if (State.ProgressionInitialized) return;
            Require(State.MineInitialized, "progression", "请先初始化初级矿坑。");
            var next = CampCodec.Clone(State);
            next.Contributions ??= new List<CampContribution>();
            next.Vehicles ??= new List<CampVehicle>();
            Require(next.Contributions.Count == 0 && next.Vehicles.Count == 0 &&
                !next.Items.Any(i => i.Place == ItemPlace.Vehicle) && !next.Cells.Any(c => c.PitId == CampMap.AdvancedPitId),
                "save", "已有进阶数据，拒绝覆盖或重新生成。");
            Require(Rules.AdvancedWidth > 0 && Rules.AdvancedLength > 0 && Rules.AdvancedDepth > 0 &&
                checked((long)Rules.AdvancedWidth * Rules.AdvancedLength * Rules.AdvancedDepth) <= 100000 &&
                float.IsFinite(Rules.AdvancedHealthMultiplier) && Rules.AdvancedHealthMultiplier > 0 &&
                Rules.AdvancedValueDepthBonus >= 0 && Rules.RoadPrice >= 0 && Rules.RoadIron >= 0 &&
                Rules.VehicleCargoSlots >= 1 && Rules.VehicleCargoSlots <= 8,
                "rules", "进阶矿区或公共修复配置无效。");
            next.AdvancedWidth = Rules.AdvancedWidth;
            next.AdvancedLength = Rules.AdvancedLength;
            next.AdvancedDepth = Rules.AdvancedDepth;
            next.RoadRequiredCoins = Rules.RoadPrice;
            next.RoadRequiredIron = Rules.RoadIron;
            next.RoadCoinsPaid = 0;
            next.RoadIronPaid = 0;
            next.RoadVersion = 0;
            next.RoadUnlocked = next.RoadRequiredCoins == 0 && next.RoadRequiredIron == 0;
            var random = new System.Random(next.Seed ^ AdvancedSeedSalt);
            for (int depth = 0; depth < next.AdvancedDepth; depth++)
            for (int z = 0; z < next.AdvancedLength; z++)
            for (int x = 0; x < next.AdvancedWidth; x++)
            {
                ItemKind kind = x == 0 ? (random.Next(2) == 0 ? ItemKind.Stone : ItemKind.Iron)
                    : PickYield(random, checked(depth + Rules.AdvancedValueDepthBonus));
                var cell = new CampCell
                {
                    Id = CampMap.CellId(CampMap.AdvancedPitId, x, depth, z), PitId = CampMap.AdvancedPitId,
                    X = x, Depth = depth, Z = z, RequiredTool = x == 0 ? 1 : 2, YieldKind = kind, YieldCount = 1,
                    Health = (Rules.BaseCellHealth + Rules.DepthHealthIncrease * depth) * Rules.AdvancedHealthMultiplier,
                    CoreKind = ""
                };
                Require(float.IsFinite(cell.Health) && cell.Health > 0, "rules", "进阶岩格耐久配置无效。");
                if (kind == ItemKind.RawStone)
                {
                    int core = random.Next(4);
                    cell.CoreKind = new[] { "stone", "iron", "gold", "rich_gold" }[core];
                    int baseValue = core == 0 ? Rules.StonePrice : core == 1 ? Rules.IronPrice
                        : core == 2 ? Rules.GoldPrice : Rules.HeavyPrice;
                    cell.HiddenValue = checked(Math.Max(0, baseValue) * random.Next(1, 4));
                    cell.RawWindowBand = RawValueBand(cell.HiddenValue);
                    cell.RawClueBand = RawPrivateBand(cell.Id, cell.RawWindowBand);
                }
                next.Cells.Add(cell);
            }
            next.Vehicles.Add(new CampVehicle { Id = CampMap.VehicleId, Position = CampMap.VehicleHome,
                CargoSlots = Rules.VehicleCargoSlots, Seats = new List<string> { "", "", "", "" } });
            next.ProgressionInitialized = true;
            Commit(next);
        }

        private CampResult ApplyProgression(CampState state, CampPlayer actor, CampCommand command)
        {
            switch (command.Action)
            {
                case "upgrade_tool": return UpgradeEquipment(state, actor, command, true);
                case "upgrade_detector": return UpgradeEquipment(state, actor, command, false);
                case "contribute_coins": return ContributeRoad(state, actor, command, false);
                case "contribute_iron": return ContributeRoad(state, actor, command, true);
                case "vehicle_enter": return EnterVehicle(state, actor, command);
                case "vehicle_exit": return ExitVehicle(state, actor, command);
                case "vehicle_load": return LoadVehicle(state, actor, command);
                case "vehicle_unload": return UnloadVehicle(state, actor, command);
                case "vehicle_recover": return RecallEmptyVehicle(state, actor, command);
                default: return ApplySocial(state, actor, command);
            }
        }

        private CampResult UpgradeEquipment(CampState state, CampPlayer actor, CampCommand command, bool tool)
        {
            RequireOutsideVehicle(state, actor);
            RequireStationAccess(actor, CampMap.RepairPoint);
            int level = tool ? actor.ToolLevel : actor.DetectorLevel;
            int maximum = tool ? Rules.MaxToolLevel : Rules.MaxDetectorLevel;
            int price = tool ? Rules.UpgradePrice : Rules.DetectorUpgradePrice;
            Require(command.ExpectedVersion == level, "stale", "装备等级已变化，请重新查看。");
            Require(level < maximum, "upgrade_max", "该装备已经达到本轮最高等级。");
            Require(price >= 0 && actor.Balance >= price, "funds", "升级费用不足。");
            actor.Balance -= price;
            if (tool) { actor.ToolLevel++; actor.Durability = 100; }
            else actor.DetectorLevel++;
            ClearSocialAction(actor);
            ShowAction(state, actor, "upgrade", .7f);
            return Ok(tool ? "矿镐已升级并修复。" : "探测器已升级。", price);
        }

        private CampResult ContributeRoad(CampState state, CampPlayer actor, CampCommand command, bool iron)
        {
            var car = RequireProgressionVehicle(state);
            RequireOutsideVehicle(state, actor);
            RequireMaintenanceAccess(actor);
            Require(!state.RoadUnlocked, "unlocked", "旧车与进阶道路已经完成修复。");
            Require(command.ExpectedVersion == state.RoadVersion, "stale", "公共修复进度已变化，请重新查看。");
            long remaining = iron ? state.RoadRequiredIron - state.RoadIronPaid : state.RoadRequiredCoins - state.RoadCoinsPaid;
            Require(command.Amount > 0 && command.Amount <= remaining, "amount", "贡献数量必须为正数且不超过当前缺额。");
            if (iron)
            {
                Require(command.Amount <= int.MaxValue && MaterialCount(state, actor, ItemKind.Iron) >= command.Amount,
                    "materials", "自己携带的铁料不足。");
                ConsumeMaterial(state, actor, ItemKind.Iron, (int)command.Amount);
                state.RoadIronPaid = checked(state.RoadIronPaid + (int)command.Amount);
            }
            else
            {
                Require(actor.Balance >= command.Amount, "funds", "自己的余额不足。");
                actor.Balance -= command.Amount;
                state.RoadCoinsPaid = checked(state.RoadCoinsPaid + command.Amount);
            }
            var contribution = state.Contributions.Find(c => c.PlayerId == actor.Id);
            if (contribution == null)
            {
                contribution = new CampContribution { PlayerId = actor.Id };
                state.Contributions.Add(contribution);
            }
            if (iron) contribution.Iron = checked(contribution.Iron + (int)command.Amount);
            else contribution.Coins = checked(contribution.Coins + command.Amount);
            state.RoadVersion++;
            state.RoadUnlocked = state.RoadCoinsPaid == state.RoadRequiredCoins && state.RoadIronPaid == state.RoadRequiredIron;
            return Ok(state.RoadUnlocked ? "公共修复完成；旧车与进阶道路已开放。" : "已记录你的自愿贡献；其他成员不会自动扣款。", command.Amount);
        }

        private CampResult EnterVehicle(CampState state, CampPlayer actor, CampCommand command)
        {
            var car = RequireCarCommand(state, command);
            Require(state.RoadUnlocked, "road_locked", "请先完成旧车与道路的公共修复。");
            RequireOutsideVehicle(state, actor);
            RequireVehicleAccess(actor, car);
            RequireStopped(car);
            Require(command.Amount >= 0 && command.Amount < 4, "seat", "座位编号无效。");
            RequireHandsFree(state, actor);
            Require(actor.StunnedUntil <= state.OnlineSeconds && actor.EscapeAt <= 0 &&
                actor.ActionUntil <= state.OnlineSeconds && string.IsNullOrEmpty(actor.ClimbingLadderId) &&
                !state.Items.Any(i => i.AssistantId == actor.Id), "busy", "请先结束当前动作再上车。");
            int seat = (int)command.Amount;
            Require(string.IsNullOrEmpty(car.Seats[seat]), "seat_occupied", "该座位已有乘客。");
            car.Seats[seat] = actor.Id;
            car.Speed = car.Steering = 0;
            car.Version++;
            actor.Position = CampMap.SeatPoint(car, seat);
            ClearSocialAction(actor);
            return Ok(seat == 0 ? "已坐上驾驶位。" : "已自愿上车。", seat);
        }

        private CampResult ExitVehicle(CampState state, CampPlayer actor, CampCommand command)
        {
            var car = RequireCarCommand(state, command);
            int seat = car.Seats.IndexOf(actor.Id);
            Require(seat >= 0, "seat", "你没有占用这辆车的座位。");
            RequireNear(actor.Position, CampMap.SeatPoint(car, seat), Rules.VehicleReach);
            Require(TryVehicleExit(car, seat, out Vector3 exit, ExitReservations(state, car, actor.Id)), "blocked", "车侧暂时没有安全的下车空间；座位与货物均保留。");
            car.Speed = car.Steering = 0;
            car.Seats[seat] = "";
            car.Version++;
            actor.Position = exit;
            ClearSocialAction(actor);
            return Ok("车辆已停稳，你已下车。");
        }

        private CampResult LoadVehicle(CampState state, CampPlayer actor, CampCommand command)
        {
            var car = RequireProgressionVehicle(state);
            Require(state.RoadUnlocked, "road_locked", "车辆尚未修复。");
            RequireOutsideVehicle(state, actor);
            RequireVehicleAccess(actor, car);
            RequireStopped(car);
            var item = GetItem(state, command);
            Require(item.OwnerId == actor.Id && IsCarriedMaterial(item), "ownership", "只能装载自己手中或口袋里的货物。");
            Require(command.Amount >= 0 && command.Amount < car.CargoSlots, "cargo_slot", "货架位置无效。");
            int slot = (int)command.Amount;
            string container = CampMap.VehicleCargoContainer(slot);
            Require(!state.Items.Any(i => i.Place == ItemPlace.Vehicle && i.ContainerId == container), "cargo_occupied", "该货架已有货物。");
            SetPlace(item, ItemPlace.Vehicle, "", container, CampMap.CargoPoint(car, slot));
            car.Version++;
            return Ok("已装入公共货架；停车后任何成员均可取用。", slot);
        }

        private CampResult UnloadVehicle(CampState state, CampPlayer actor, CampCommand command)
        {
            var car = RequireProgressionVehicle(state);
            Require(state.RoadUnlocked, "road_locked", "车辆尚未修复。");
            RequireOutsideVehicle(state, actor);
            RequireVehicleAccess(actor, car);
            RequireStopped(car);
            RequireHandsFree(state, actor);
            Require(!state.Items.Any(i => i.AssistantId == actor.Id), "assisting", "请先退出协助搬运。");
            var item = GetItem(state, command);
            Require(item.Place == ItemPlace.Vehicle && TryCargoSlot(item, car, out _), "cargo", "该物品不在这辆车的货架上。");
            SetPlace(item, ItemPlace.Hand, actor.Id, "", actor.Position + Vector3.up);
            car.Version++;
            return Ok("已取下这件公共货物。");
        }

        private CampResult RecallEmptyVehicle(CampState state, CampPlayer actor, CampCommand command)
        {
            var car = RequireCarCommand(state, command);
            Require(state.RoadUnlocked, "road_locked", "车辆尚未修复。");
            RequireOutsideVehicle(state, actor);
            RequireMaintenanceAccess(actor);
            RequireStopped(car);
            Require(car.Seats.All(string.IsNullOrEmpty), "passengers", "请等待所有乘客离车后再回收。");
            Require(Vector3.Distance(car.Position, CampMap.VehicleHome) > .5f, "near_home", "车辆已经在维护点附近，无需回收。");
            Require(!state.Players.Any(p => _isOnline(p.Id) &&
                Mathf.Abs(p.Position.x - CampMap.VehicleHome.x) < 1.45f &&
                Mathf.Abs(p.Position.z - CampMap.VehicleHome.z) < 2.5f &&
                Mathf.Abs(p.Position.y - CampMap.VehicleHome.y) < 2f),
                "parking_occupied", "有人站在停车位内，请移到车位侧边再回收。");
            // The recalled vehicle has zero yaw. Probe its center and four body
            // corners before detaching any cargo or changing the vehicle position.
            foreach (var offset in new[] { Vector3.zero, new Vector3(-1.05f, 0, -2.15f),
                new Vector3(1.05f, 0, -2.15f), new Vector3(-1.05f, 0, 2.15f), new Vector3(1.05f, 0, 2.15f) })
                Require(_freePlacement(CampMap.VehicleHome + offset + Vector3.up * .9f, .3f),
                    "parking_blocked", "停车位有障碍，空车与原货物均保留在旧位置。");
            var cargo = state.Items.Where(i => i.Place == ItemPlace.Vehicle).ToList();
            var positions = new Dictionary<string, Vector3>();
            var rotation = Quaternion.Euler(0, car.Yaw, 0);
            Vector3 bagPoint = car.Position + rotation * new Vector3(0, .55f, -3.2f);
            bool hasSmall = cargo.Any(i => i.Kind != ItemKind.HeavyOre);
            Require(!hasSmall || _freePlacement(bagPoint, .4f), "blocked", "旧车后方没有留货空间；回收未生效。");
            foreach (var item in cargo)
            {
                Require(TryCargoSlot(item, car, out int slot), "save", "公共货架记录损坏。");
                Vector3 point = item.Kind == ItemKind.HeavyOre
                    ? car.Position + rotation * new Vector3(slot % 2 == 0 ? -2.8f : 2.8f, .95f, -1.5f - slot / 2 * 2f)
                    : bagPoint;
                Require(item.Kind != ItemKind.HeavyOre || _freePlacement(point, .85f), "blocked", "旧车旁没有放下大矿的空间；回收未生效。");
                positions.Add(item.Id, point);
            }
            string bag = "recovery-" + Guid.NewGuid().ToString("N");
            foreach (var item in cargo)
                SetPlace(item, ItemPlace.Ground, "", item.Kind == ItemKind.HeavyOre ? "" : bag, positions[item.Id]);
            // The old cargo is already detached in this same transaction. Only the
            // empty, original vehicle changes region; no ore is delivered for free.
            car.Position = CampMap.VehicleHome;
            car.Yaw = car.Speed = car.Steering = 0;
            car.Version++;
            return Ok("空车已回到维护点；原货物全部留在旧车附近。", cargo.Count);
        }

        private static CampVehicle RequireProgressionVehicle(CampState state)
        {
            Require(state.ProgressionInitialized && state.Vehicle != null, "progression", "公共车辆尚未初始化。");
            return state.Vehicle;
        }

        private static CampVehicle RequireCarCommand(CampState state, CampCommand command)
        {
            var car = RequireProgressionVehicle(state);
            Require(command.TargetId == car.Id, "vehicle", "车辆不存在。");
            Require(command.ExpectedVersion == car.Version, "stale", "车辆状态已变化，请重新操作。");
            return car;
        }

        private static void RequireOutsideVehicle(CampState state, CampPlayer actor) =>
            Require(state.Vehicle == null || !state.Vehicle.Seats.Contains(actor.Id), "seated", "请先安全下车。");

        private static void RequireStopped(CampVehicle car) => Require(Mathf.Abs(car.Speed) <= .05f, "moving", "请先停车。");

        // The root places the vehicle CampTarget at this collider/interaction center.
        // The ground-level vehicle Position is not a valid unobstructed LOS endpoint.
        private void RequireVehicleAccess(CampPlayer actor, CampVehicle car)
        {
            Require(_isOnline(actor.Id), "offline", "请先加入营地。");
            RequireNear(actor.Position, car.Position, Rules.VehicleReach);
            Require(_lineOfSight(actor.Position + Vector3.up * 1.6f, CampMap.VehicleInteractionPoint(car)), "blocked", "车辆被遮挡。");
        }

        private void RequireMaintenanceAccess(CampPlayer actor) =>
            RequireStationAccess(actor, CampMap.VehicleMaintenancePoint);

        private List<Vector3> ExitReservations(CampState state, CampVehicle car, string actorId) => state.Players
            .Where(p => p.Id != actorId && _isOnline(p.Id) && !car.Seats.Contains(p.Id)).Select(p => p.Position).ToList();

        private bool TryVehicleExit(CampVehicle car, int seat, out Vector3 point, ICollection<Vector3> reserved = null)
        {
            float side = seat % 2 == 0 ? -1 : 1;
            float z = CampMap.SeatOffset(seat).z;
            var rotation = Quaternion.Euler(0, car.Yaw, 0);
            Vector3 start = CampMap.SeatPoint(car, seat) + Vector3.up * 1.6f;
            foreach (var offset in new[] { new Vector3(side * 2.25f, 0, z), new Vector3(-side * 2.25f, 0, z),
                new Vector3(side * .55f, 0, 3), new Vector3(side * .55f, 0, -3) })
            {
                Vector3 candidate = car.Position + rotation * offset;
                point = candidate;
                Vector3 middle = candidate + Vector3.up * .9f;
                if ((reserved == null || reserved.All(p => Vector3.Distance(p, candidate) >= .75f)) &&
                    _freePlacement(middle, .35f) && _lineOfSight(start, middle)) return true;
            }
            point = default;
            return false;
        }

        private void DisconnectVehicle(CampState state, string actorId)
        {
            var car = state.Vehicle;
            if (car == null) return;
            int seat = car.Seats.IndexOf(actorId);
            if (seat < 0) return;
            var actor = state.Players.Find(p => p.Id == actorId);
            // A blocked disconnect must still release occupancy. Keep the miner at
            // this car, just above its body, rather than transporting cargo to camp.
            actor.Position = TryVehicleExit(car, seat, out Vector3 exit, ExitReservations(state, car, actorId)) ? exit
                : CampMap.SeatPoint(car, seat) + Vector3.up * 1.1f;
            car.Seats[seat] = "";
            car.Speed = car.Steering = 0;
            car.Version++;
        }

        public void RecoverProgression()
        {
            if (!State.ProgressionInitialized) return;
            var next = CampCodec.Clone(State);
            var car = next.Vehicle;
            bool changed = car.Speed != 0 || car.Steering != 0 || car.Seats.Any(s => !string.IsNullOrEmpty(s));
            if (!changed) return;
            var reserved = ExitReservations(next, car, "");
            for (int seat = 0; seat < car.Seats.Count; seat++)
            {
                if (string.IsNullOrEmpty(car.Seats[seat])) continue;
                var actor = next.Players.Find(p => p.Id == car.Seats[seat]);
                Require(TryVehicleExit(car, seat, out Vector3 exit, reserved), "blocked", "保存的车辆被完全封住，已保留原存档与全部货物。");
                actor.Position = exit;
                reserved.Add(exit);
                car.Seats[seat] = "";
                ClearSocialAction(actor);
            }
            car.Speed = car.Steering = 0;
            car.Version++;
            Commit(next);
        }

        private static bool TryCargoSlot(CampItem item, CampVehicle car, out int slot)
        {
            slot = -1;
            string prefix = "vehicle:" + car.Id + ":";
            return item.ContainerId != null && item.ContainerId.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(item.ContainerId.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out slot) &&
                slot >= 0 && slot < car.CargoSlots && item.ContainerId == CampMap.VehicleCargoContainer(slot);
        }

        private static void ValidateProgression(CampState state)
        {
            Require(state.Vehicles != null && state.Contributions != null, "save", "车辆或公共贡献集合缺失。");
            var cargo = state.Items.Where(i => i.Place == ItemPlace.Vehicle).ToList();
            var advanced = state.Cells.Where(c => c.PitId == CampMap.AdvancedPitId).ToList();
            if (!state.ProgressionInitialized)
            {
                Require(state.Vehicles.Count == 0 && state.Contributions.Count == 0 && cargo.Count == 0 && advanced.Count == 0 &&
                    !state.RoadUnlocked && state.RoadRequiredCoins == 0 && state.RoadRequiredIron == 0 &&
                    state.RoadCoinsPaid == 0 && state.RoadIronPaid == 0 && state.RoadVersion == 0,
                    "save", "未初始化的进阶系统出现车辆或付费数据。");
                return;
            }
            Require(state.MineInitialized && state.Vehicles.Count == 1 && state.Vehicles[0] != null &&
                state.RoadRequiredCoins >= 0 && state.RoadRequiredIron >= 0 && state.RoadCoinsPaid >= 0 && state.RoadIronPaid >= 0 &&
                state.RoadCoinsPaid <= state.RoadRequiredCoins && state.RoadIronPaid <= state.RoadRequiredIron && state.RoadVersion >= 0,
                "save", "公共修复记录损坏。");
            Require(state.RoadUnlocked == (state.RoadCoinsPaid == state.RoadRequiredCoins && state.RoadIronPaid == state.RoadRequiredIron),
                "save", "公共解锁与已支付贡献不一致。");
            Require(state.Contributions.All(c => c != null && c.Coins >= 0 && c.Iron >= 0 && state.Players.Any(p => p.Id == c.PlayerId)) &&
                state.Contributions.Select(c => c.PlayerId).Distinct().Count() == state.Contributions.Count,
                "save", "公共贡献归属损坏。");
            Require(state.Contributions.Aggregate(0L, (sum, c) => checked(sum + c.Coins)) == state.RoadCoinsPaid &&
                state.Contributions.Sum(c => (long)c.Iron) == state.RoadIronPaid, "save", "贡献账目与已支付总数不一致。");
            var car = state.Vehicle;
            Require(car.Id == CampMap.VehicleId && CampCodec.IsFinite(car.Position) && float.IsFinite(car.Yaw) &&
                float.IsFinite(car.Speed) && float.IsFinite(car.Steering) && car.Version >= 0 && car.CargoSlots >= 1 && car.CargoSlots <= 8 &&
                car.Seats != null && car.Seats.Count == 4 && car.Seats.All(s => s != null && (s.Length == 0 || state.Players.Any(p => p.Id == s))),
                "save", "车辆状态或乘客数据损坏。");
            var occupied = car.Seats.Where(s => !string.IsNullOrEmpty(s)).ToList();
            Require(occupied.Distinct().Count() == occupied.Count, "save", "同一乘客重复占座。");
            Require(state.RoadUnlocked || (occupied.Count == 0 && car.Speed == 0 && cargo.Count == 0), "save", "未修复车辆出现乘客或货物。");
            var slots = new HashSet<int>();
            foreach (var item in cargo)
                Require(string.IsNullOrEmpty(item.OwnerId) && string.IsNullOrEmpty(item.AssistantId) &&
                    TryCargoSlot(item, car, out int slot) && slots.Add(slot), "save", "车辆货架位置或唯一归属损坏。");
            Require(state.AdvancedWidth > 0 && state.AdvancedLength > 0 && state.AdvancedDepth > 0 &&
                (long)advanced.Count == checked((long)state.AdvancedWidth * state.AdvancedLength * state.AdvancedDepth),
                "save", "进阶矿坑单元不完整。");
            foreach (var cell in advanced)
                Require(cell.X >= 0 && cell.X < state.AdvancedWidth && cell.Z >= 0 && cell.Z < state.AdvancedLength &&
                    cell.Depth >= 0 && cell.Depth < state.AdvancedDepth && cell.Id == CampMap.CellId(CampMap.AdvancedPitId, cell.X, cell.Depth, cell.Z) &&
                    Enum.IsDefined(typeof(ItemKind), cell.YieldKind) && (cell.X == 0
                        ? cell.RequiredTool == 1 && (cell.YieldKind == ItemKind.Stone || cell.YieldKind == ItemKind.Iron)
                        : cell.RequiredTool == 2), "save", "进阶矿格位置或工具门槛损坏。");
        }
    }
}
