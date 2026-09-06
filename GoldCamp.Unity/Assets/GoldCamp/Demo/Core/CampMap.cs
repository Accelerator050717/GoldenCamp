using UnityEngine;

namespace GoldCamp.Demo
{
    // Shared spatial contract; actual rock contents remain in the authority state.
    public static class CampMap
    {
        public const string StarterPit = "starter";
        public const string AdvancedPitId = "advanced", VehicleId = "camp-truck";
        public static readonly Vector3 PitOrigin = new Vector3(-6, 0, 20);
        public static readonly Vector3 AdvancedOrigin = new Vector3(18, 0, 48);
        public static readonly Vector3 VehicleHome = new Vector3(20, 0, -7);
        public static readonly Vector3 VehicleMaintenancePoint = new Vector3(16.6f, 0, -5);
        public static readonly Vector3 RepairPoint = new Vector3(-6, 0, 1);
        public static readonly Vector3 CutterPoint = new Vector3(5, 0, 0);
        public static readonly Vector3 WagerPoint = new Vector3(5, 0, -7);
        public static readonly Vector3 RiverPoint = new Vector3(12, 0, 5);
        public static Vector3 CellCenter(int x, int depth, int z, CampRules rules) =>
            CellCenter(StarterPit, x, depth, z, rules);
        public static Vector3 CellCenter(string pitId, int x, int depth, int z, CampRules rules) =>
            (pitId == AdvancedPitId ? AdvancedOrigin : PitOrigin) + new Vector3((x + .5f) * rules.CellSize, -(depth + .5f) * rules.CellSize, (z + .5f) * rules.CellSize);
        public static Vector3 CellCenter(CampCell cell, CampRules rules) => CellCenter(cell.PitId, cell.X, cell.Depth, cell.Z, rules);
        public static Vector3 LadderCenter(CampLadder ladder, CampRules rules) =>
            CellCenter(ladder.PitId, ladder.X, ladder.Depth, ladder.Z, rules) + Vector3.forward * (rules.CellSize * .5f - .25f);
        public static Vector3 CabinetPoint(int index) => new Vector3(index < 2 ? -10 : 10, 0, -5 - (index % 2) * 6);
        public static string CellId(int x, int depth, int z) => CellId(StarterPit, x, depth, z);
        public static string CellId(string pitId, int x, int depth, int z) => pitId + ":" + x + ":" + depth + ":" + z;
        public static string VehicleCargoContainer(int slot) => "vehicle:" + VehicleId + ":" + slot;
        public static Vector3 VehicleInteractionPoint(CampVehicle car) => car.Position + Vector3.up * 1.05f;
        public static Vector3 SeatOffset(int seat) => new Vector3(seat % 2 == 0 ? -.55f : .55f, .65f, seat < 2 ? .65f : -.35f);
        public static Vector3 SeatPoint(CampVehicle car, int seat) => car.Position + Quaternion.Euler(0, car.Yaw, 0) * SeatOffset(seat);
        public static Vector3 CargoPoint(CampVehicle car, int slot) => car.Position + Quaternion.Euler(0, car.Yaw, 0) *
            new Vector3(slot % 2 == 0 ? -.45f : .45f, 1.35f + slot / 4 * .5f, -1.35f - (slot / 2 % 2) * .45f);
    }
}
