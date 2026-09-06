using UnityEngine;

namespace GoldCamp.Runtime
{
    [DisallowMultipleComponent]
    public sealed class M0Hud : MonoBehaviour
    {
        public FirstPersonController Controller;
        public OreCarrier Carrier;
        public M0Input Input;

        private GUIStyle _text;
        private GUIStyle _center;
        private GUIStyle _title;
        private GUIStyle _button;

        private void Awake()
        {
            if (Controller == null) Controller = GetComponent<FirstPersonController>();
            if (Carrier == null) Carrier = GetComponent<OreCarrier>();
            if (Input == null) Input = GetComponent<M0Input>();
        }

        private void OnGUI()
        {
            if (Input == null || Carrier == null)
                return;
            if (_text == null)
            {
                _text = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true };
                _text.normal.textColor = Color.white;
                _center = new GUIStyle(_text) { alignment = TextAnchor.MiddleCenter };
                _title = new GUIStyle(_center) { fontSize = 30, fontStyle = FontStyle.Bold };
                _button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            }

            float scale = Mathf.Max(0.65f, Screen.height / 900f);
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float width = Screen.width / scale;
            float height = Screen.height / scale;
            GUI.Box(new Rect(18, 18, 465, 122), GUIContent.none);
            GUI.Label(new Rect(32, 27, 440, 31), "GOLDCAMP / M0 GRAYBOX", _text);
            GUI.Label(new Rect(32, 59, 440, 69), "WASD Move   Mouse Look   Space Jump\nE Pick up / Drop   Q Throw   R Reset\nEsc Pause / Release mouse", _text);

            if (Input.IsPaused)
            {
                Rect panel = new Rect(width / 2f - 220, height / 2f - 200, 440, 370);
                GUI.Box(panel, GUIContent.none);
                GUI.Label(new Rect(panel.x, panel.y + 21, panel.width, 55), "PAUSED", _title);
                if (GUI.Button(new Rect(panel.x + 45, panel.y + 99, 350, 58), "Resume", _button))
                    Input.SetPaused(false);
                if (GUI.Button(new Rect(panel.x + 45, panel.y + 176, 350, 58), "Reset position", _button))
                {
                    if (Controller != null) Controller.ResetPlayer();
                    Input.SetPaused(false);
                }
                if (GUI.Button(new Rect(panel.x + 45, panel.y + 253, 350, 58), "Quit game", _button))
                {
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                }
            }
            else
            {
                GUI.Label(new Rect(width / 2f - 14, height / 2f - 18, 28, 36), "+", _title);
                string prompt = Carrier.HeldOre != null ? "E  Drop ore     Q  Throw ore"
                    : Carrier.FocusedOre != null ? "E  Pick up ore" : "Aim at the yellow ore";
                GUI.Box(new Rect(width / 2f - 330, height - 103, 660, 82), GUIContent.none);
                GUI.Label(new Rect(width / 2f - 320, height - 102, 640, 41), prompt, _center);
                GUI.Label(new Rect(width / 2f - 320, height - 65, 640, 37), Carrier.Feedback, _center);
            }
            GUI.matrix = previousMatrix;
        }
    }
}
