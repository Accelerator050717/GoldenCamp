using UnityEngine;

namespace GoldCamp.Demo
{
    [CreateAssetMenu(menuName = "GoldCamp/Demo Rules")]
    public sealed class DemoConfig : ScriptableObject
    {
        public CampRules Rules = new CampRules();
    }
}
