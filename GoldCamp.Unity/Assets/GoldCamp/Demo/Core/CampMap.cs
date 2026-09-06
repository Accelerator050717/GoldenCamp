using UnityEngine;

namespace GoldCamp.Demo
{
    // Shared spatial contract; actual rock contents remain in the authority state.
    public static class CampMap
    {
        public const string StarterPit = "starter";
        public static readonly Vector3 PitOrigin = new Vector3(-6, 0, 20);
        public static readonly Vector3 RepairPoint = new Vector3(-6, 0, 1);
        public static readonly Vector3 CutterPoint = new Vector3(5, 0, 0);
        public static readonly Vector3 WagerPoint = new Vector3(5, 0, -7);
        public static readonly Vector3 RiverPoint = new Vector3(12, 0, 5);
        public static Vector3 CellCenter(int x, int depth, int z, CampRules rules) =>
            PitOrigin + new Vector3((x + .5f) * rules.CellSize, -(depth + .5f) * rules.CellSize, (z + .5f) * rules.CellSize);
        public static Vector3 CellCenter(CampCell cell, CampRules rules) => CellCenter(cell.X, cell.Depth, cell.Z, rules);
        public static Vector3 LadderCenter(CampLadder ladder, CampRules rules) =>
            CellCenter(ladder.X, ladder.Depth, ladder.Z, rules) + Vector3.forward * (rules.CellSize * .5f - .25f);
        public static Vector3 CabinetPoint(int index) => new Vector3(index < 2 ? -10 : 10, 0, -5 - (index % 2) * 6);
        public static string CellId(int x, int depth, int z) => StarterPit + ":" + x + ":" + depth + ":" + z;
    }
}
