using System.Linq;
using UnityEngine;

namespace GoldCamp.Demo
{
    public sealed class CampVoicePanel : MonoBehaviour
    {
        public CampVoice Voice;
        public CampSession Session;
        public void Settings()
        {
            GUILayout.Space(10); GUILayout.Label("附近语音 · 按住 V 说话");
            bool muted = GUILayout.Toggle(Voice.MicrophoneMuted, "静音我的麦克风");
            if (muted != Voice.MicrophoneMuted) Voice.SetMuted(muted);
            GUILayout.Label("语音音量 " + Mathf.RoundToInt(Voice.Volume * 100) + "%");
            Voice.Volume = GUILayout.HorizontalSlider(Voice.Volume, 0, 1);
            GUILayout.Label("麦克风：" + (Voice.Device.Length == 0 ? "系统默认" : Voice.Device));
            if (GUILayout.Button("刷新麦克风列表")) Voice.RefreshDevices();
            if (GUILayout.Toggle(Voice.Device.Length == 0, "使用系统默认", GUI.skin.button) && Voice.Device.Length != 0) Voice.SelectDevice("");
            foreach (string device in Voice.Devices)
                if (GUILayout.Toggle(Voice.Device == device, device, GUI.skin.button) && Voice.Device != device) Voice.SelectDevice(device);
            GUILayout.Label(Voice.Status);
            var view = Session.View;
            if (view?.Self != null) foreach (var player in view.Players.Where(p => p.Online && p.Id != view.Self.Id))
            {
                bool peerMuted = GUILayout.Toggle(Voice.IsMuted(player.Id), "静音 " + player.Name);
                if (peerMuted != Voice.IsMuted(player.Id)) Voice.MutePlayer(player.Id, peerMuted);
            }
            GUILayout.Label("3 米内清晰，20 米外听不到；可使用耳机减少回声。");
        }
        public string StatusText()
        {
            var view = Session.View;
            if (view?.Self == null) return Voice.Status;
            string text = Voice.Capturing ? (Voice.LocalLevel > .003f ? "● 正在说话" : "◯ 麦克风开启") : Voice.Status;
            foreach (var player in view.Players.Where(p => p.Online && p.Id != view.Self.Id))
                if (Voice.IsSpeaking(player.Id)) text += "\n● " + player.Name;
            return text;
        }
    }
}
