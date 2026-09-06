using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed class CampDisplaySettings : MonoBehaviour
    {
        public CampSession Session;
        public CampControls Controls;
        private int _width = 1280, _height = 720;
        private bool _fullscreen;
        private void Start()
        {
            if (Application.isBatchMode) return;
            _width = Screen.width; _height = Screen.height;
            _fullscreen = Screen.fullScreen;
            if (Session.IsTestSession) return;
            int width = PlayerPrefs.GetInt("Demo.WindowWidth", 0), height = PlayerPrefs.GetInt("Demo.WindowHeight", 0);
            if (width >= 960 && height >= 600 && width <= 3840 && height <= 2160)
            {
                _width = width; _height = height;
                _fullscreen = PlayerPrefs.GetInt("Demo.Fullscreen", 0) != 0;
                Apply();
            }
        }
        public void Settings()
        {
            GUILayout.Space(10); GUILayout.Label("显示与舒适度");
            Controls.CameraSway = GUILayout.Toggle(Controls.CameraSway, "轻微行走镜头摇晃（默认关闭）");
            _fullscreen = GUILayout.Toggle(_fullscreen, "无边框全屏");
            GUILayout.BeginHorizontal();
            foreach (var size in new[] { new Vector2Int(960,600), new Vector2Int(1280,720), new Vector2Int(1600,900), new Vector2Int(1920,1080) })
                if (GUILayout.Toggle(_width == size.x && _height == size.y, size.x + " × " + size.y, GUI.skin.button))
                { _width = size.x; _height = size.y; }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("应用窗口设置")) Apply();
        }
        private void Apply()
        {
            Screen.SetResolution(_width, _height, _fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed);
            if (Session.IsTestSession) return;
            PlayerPrefs.SetInt("Demo.WindowWidth", _width); PlayerPrefs.SetInt("Demo.WindowHeight", _height);
            PlayerPrefs.SetInt("Demo.Fullscreen", _fullscreen ? 1 : 0); PlayerPrefs.Save();
        }
    }
}
