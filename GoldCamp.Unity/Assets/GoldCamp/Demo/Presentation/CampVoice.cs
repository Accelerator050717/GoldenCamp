using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Microphone and Unity audio objects stay on the main thread. Encoders run on
    // a bounded worker. A playback callback only drains its SPSC PCM ring.
    public sealed class CampVoice : MonoBehaviour
    {
        public CampSession Session;
        public bool MicrophoneMuted { get; private set; }
        public float Volume = .85f;
        public string Device { get; private set; } = "";
        public string[] Devices { get; private set; } = Array.Empty<string>();
        public string Status { get; private set; } = "按住 V 说话";
        public bool Capturing => _capture != null || _tone;
        public float LocalLevel { get; private set; }
        private readonly Dictionary<string, Speaker> _speakers = new Dictionary<string, Speaker>();
        private readonly HashSet<string> _muted = new HashSet<string>();
        private AudioClip _capture;
        private CampVoiceEncodeWorker _encoder;
        private string _captureDevice, _world, _testMode = "off";
        private int _capturePosition, _stream, _sequence, _sourceFrames;
        private bool _tone, _attempted;
        private double _nextTone, _lastCaptureRead, _lastCapturePump, _nextDiagnostics;
        private long _toneSamples;
        private string _testMute = "";
        public long CapturedSamples, SentPackets, ReceivedPackets, DecodedPackets, RejectedPackets;
        private readonly CampVoicePlayback.Counters _playbackMetrics = new CampVoicePlayback.Counters();
        private long _ringDroppedSamples;

        private sealed class Speaker : IDisposable
        {
            public readonly CampVoiceJitter Jitter = new CampVoiceJitter();
            public readonly CampVoiceDecoder Decoder = new CampVoiceDecoder();
            public readonly float[] Pcm = new float[CampVoiceCodec.FrameSamples];
            public CampPcmRing Ring = new CampPcmRing(3200);
            public GameObject Root;
            public AudioSource Source;
            public AudioClip Clip;
            public CampVoicePlayback Playback;
            public int Stream;
            public double SpokeAt;
            public void Dispose()
            {
                if (Source != null) Source.Stop();
                if (Root != null) UnityEngine.Object.Destroy(Root);
                if (Clip != null) UnityEngine.Object.Destroy(Clip);
                Decoder.Dispose();
                // Callback may be finishing its last read; never reset/reuse that ring.
            }
        }

        private void Start()
        {
            if (Session == null) Session = GetComponent<CampSession>();
            if (!Session.IsTestSession)
            {
                MicrophoneMuted = PlayerPrefs.GetInt("Demo.MicrophoneMuted", 0) != 0;
                Device = PlayerPrefs.GetString("Demo.Microphone", "");
                Volume = PlayerPrefs.GetFloat("Demo.VoiceVolume", .85f);
            }
            RefreshDevices();
            Session.VoiceReceived += Receive;
            Session.ViewChanged += ObserveView;
        }

        private void ObserveView(CampView view)
        {
            if (_world != view.WorldId) { StopCapture(); ClearSpeakers(); _world = view.WorldId; _attempted = false; }
            foreach (string id in _speakers.Keys.ToArray())
                if (!view.Players.Any(p => p.Id == id && p.Online)) RemoveSpeaker(id);
        }

        public void RefreshDevices()
        {
            try { Devices = Microphone.devices ?? Array.Empty<string>(); }
            catch (Exception) { Devices = Array.Empty<string>(); }
            if (Device.Length > 0 && !Devices.Contains(Device)) Device = "";
        }
        public void SelectDevice(string device)
        { StopCapture(); Device = device ?? ""; _attempted = false; SavePreferences(); }
        public void SetMuted(bool muted)
        { MicrophoneMuted = muted; if (muted) StopCapture(); SavePreferences(); }
        public bool IsMuted(string id) => _muted.Contains(id);
        public void MutePlayer(string id, bool muted)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (muted) { _muted.Add(id); RemoveSpeaker(id); } else _muted.Remove(id);
        }
        public bool IsSpeaking(string id) => _speakers.TryGetValue(id, out var speaker) &&
            Time.realtimeSinceStartupAsDouble - speaker.SpokeAt < .25;
        public void SavePreferences()
        {
            if (Session == null || Session.IsTestSession) return;
            PlayerPrefs.SetInt("Demo.MicrophoneMuted", MicrophoneMuted ? 1 : 0);
            PlayerPrefs.SetString("Demo.Microphone", Device);
            PlayerPrefs.SetFloat("Demo.VoiceVolume", Mathf.Clamp01(Volume)); PlayerPrefs.Save();
        }

        private void Update()
        {
            if (Session == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            var view = Session.View;
            if (view?.Self == null)
            { StopCapture(); ClearSpeakers(); _world = null; _attempted = false; return; }
            if (_world != view.WorldId)
            { StopCapture(); ClearSpeakers(); _world = view.WorldId; _attempted = false; }
            bool test = Session.IsTestSession && _testMode != "off";
            var hud = GetComponent<CampHud>();
            bool wantsCapture = !MicrophoneMuted && (test || (!Application.isBatchMode &&
                Application.isFocused && string.IsNullOrEmpty(hud?.Panel) && Input.GetKey(KeyCode.V)));
            if (!wantsCapture)
            {
                StopCapture(); _attempted = false;
                Status = MicrophoneMuted ? "麦克风已静音" : Devices.Length == 0 ? "未检测到麦克风" : "按住 V 说话";
            }
            else if (!Capturing && !_attempted)
            {
                _attempted = true;
                try { StartCapture(test && _testMode == "tone", now); }
                catch (Exception error) { StopCapture(); Status = "麦克风未启动：" + error.Message; }
            }
            if (Capturing) PumpCapture(now);
            foreach (string id in _speakers.Keys.ToArray())
            {
                var miner = view.Players.Find(p => p.Id == id && p.Online);
                if (miner == null || _muted.Contains(id)) { RemoveSpeaker(id); continue; }
                var speaker = _speakers[id];
                speaker.Root.transform.position = miner.Position + Vector3.up * 1.4f;
                var listener = view.Players.Find(p => p.Id == view.Self.Id);
                Volatile.Write(ref speaker.Playback.Gain, listener == null ? 0 : Mathf.Clamp01(Volume) *
                    CampVoiceProximity.Gain(listener.Position, miner.Position));
                int frames = 0;
                while (frames++ < 5)
                {
                    var result = speaker.Jitter.Tick(now, out var packet);
                    if (result == CampVoiceJitterResult.Stopped)
                    { speaker.Decoder.Reset(); ResetBuffer(speaker); speaker.SpokeAt = 0; break; }
                    if (result == CampVoiceJitterResult.Waiting) break;
                    bool okay;
                    if (result == CampVoiceJitterResult.Packet)
                    {
                        if (speaker.Stream != packet.Stream)
                        { speaker.Decoder.Reset(); ResetBuffer(speaker); speaker.Stream = packet.Stream; }
                        okay = speaker.Decoder.TryDecodePacket(packet.Data, speaker.Pcm, out _);
                        if (okay) DecodedPackets++; else RejectedPackets++;
                    }
                    else okay = speaker.Decoder.TryConcealLostFrame(speaker.Pcm, out _);
                    if (!okay) continue;
                    float square = 0; foreach (float sample in speaker.Pcm) square += sample * sample;
                    if (square > .003f * .003f * speaker.Pcm.Length) speaker.SpokeAt = now;
                    _ringDroppedSamples += speaker.Pcm.Length - speaker.Ring.Write(speaker.Pcm);
                }
            }
            if (Session.IsTestSession && now >= _nextDiagnostics)
            {
                _nextDiagnostics = now + .25;
                WriteDiagnostics();
            }
        }

        private void StartCapture(bool tone, double now)
        {
            _stream = _stream == int.MaxValue ? 1 : _stream + 1; _sequence = 0;
            _capturePosition = 0; _lastCaptureRead = now; _lastCapturePump = now; LocalLevel = 0;
            if (tone)
            {
                _tone = true; _nextTone = now; _toneSamples = 0;
                _sourceFrames = CampVoiceCodec.FrameSamples;
                _encoder = new CampVoiceEncodeWorker(16000, 1);
                Status = "开发测试：合成音发送中"; return;
            }
            RefreshDevices();
            if (Devices.Length == 0) throw new InvalidOperationException("没有可用设备，请检查系统麦克风设置");
            _captureDevice = string.IsNullOrEmpty(Device) ? null : Device;
            Microphone.GetDeviceCaps(_captureDevice, out int minimum, out int maximum);
            int rate = maximum == 0 ? 16000 : Mathf.Clamp(16000, Math.Max(8000, minimum), maximum);
            _capture = Microphone.Start(_captureDevice, true, 1, rate);
            if (_capture == null) throw new InvalidOperationException("设备拒绝录音，请选择其他麦克风");
            _sourceFrames = _capture.frequency / 50;
            _encoder = new CampVoiceEncodeWorker(_capture.frequency, _capture.channels);
            Status = "V 说话中 · 不录音保存";
        }

        private void PumpCapture(double now)
        {
            try
            {
                if (_tone)
                {
                    int count = 0;
                    while (now >= _nextTone && count++ < 5)
                    {
                        _nextTone += .02;
                        var samples = new float[320];
                        for (int i = 0; i < samples.Length; i++) samples[i] = .08f * (float)Math.Sin(2 * Math.PI * 440 * _toneSamples++ / 16000);
                        _encoder.TryPush(samples, _stream, _sequence++);
                    }
                    if (now - _nextTone > .1) { _nextTone = now; _stream++; _sequence = 0; }
                }
                else
                {
                    int position = Microphone.GetPosition(_captureDevice);
                    if (position < 0 || !Microphone.IsRecording(_captureDevice))
                        throw new InvalidOperationException("录音设备已断开，松开 V 后重试");
                    if (now - _lastCapturePump > .25)
                    {
                        // A paused main thread missed PCM time, not a device read.
                        // Start a fresh stream so remote PLC watermarks cannot reject
                        // the resumed voice as old sequence numbers.
                        _capturePosition = position; _lastCaptureRead = now;
                        _stream++; _sequence = 0;
                    }
                    if (now - _lastCaptureRead > 2)
                        throw new InvalidOperationException("录音设备无数据，松开 V 后重试");
                    _lastCapturePump = now; // A stall drops old PCM; only actual reads refresh the device timeout.
                    int available = (position - _capturePosition + _capture.samples) % _capture.samples;
                    int count = 0;
                    while (available >= _sourceFrames && count++ < 5)
                    {
                        var samples = new float[_sourceFrames * _capture.channels];
                        if (!_capture.GetData(samples, _capturePosition)) throw new InvalidOperationException("读取麦克风失败");
                        _capturePosition = (_capturePosition + _sourceFrames) % _capture.samples;
                        available -= _sourceFrames; _lastCaptureRead = now;
                        CapturedSamples += _sourceFrames;
                        _encoder.TryPush(samples, _stream, _sequence++);
                    }
                }
                while (_encoder.TryPop(out var encoded))
                {
                    if (encoded.Stream != _stream) continue;
                    LocalLevel = encoded.Rms;
                    if (Session.SendVoice(new CampVoiceFrame { Stream = encoded.Stream, Sequence = encoded.Sequence, Data = encoded.Data })) SentPackets++;
                }
                if (!string.IsNullOrEmpty(_encoder.Error)) throw new InvalidOperationException(_encoder.Error);
            }
            catch (Exception error) { StopCapture(); Status = "语音停止：" + error.Message; }
        }

        private void StopCapture()
        {
            if (_capture != null)
            {
                try { Microphone.End(_captureDevice); } catch (Exception) { }
                Destroy(_capture); _capture = null;
            }
            _tone = false; _encoder?.Dispose(); _encoder = null; LocalLevel = 0;
        }

        private void Receive(CampVoiceFrame frame)
        {
            var view = Session.View;
            if (!isActiveAndEnabled || view?.Self == null || frame == null || frame.Speaker == view.Self.Id || _muted.Contains(frame.Speaker ?? "") ||
                !view.Players.Any(p => p.Id == frame.Speaker && p.Online)) return;
            ReceivedPackets++;
            if (!_speakers.TryGetValue(frame.Speaker, out var speaker))
            {
                if (_speakers.Count >= 3) return;
                speaker = new Speaker();
                speaker.Root = new GameObject("Nearby voice"); speaker.Root.transform.SetParent(transform, false);
                speaker.Source = speaker.Root.AddComponent<AudioSource>();
                speaker.Source.loop = true; speaker.Source.playOnAwake = false;
                // Proximity/volume is applied in the DSP filter; do not attenuate twice.
                speaker.Source.spatialBlend = 0; speaker.Source.dopplerLevel = 0; speaker.Source.volume = 1;
                speaker.Playback = speaker.Root.AddComponent<CampVoicePlayback>();
                speaker.Playback.Ring = speaker.Ring; speaker.Playback.Metrics = _playbackMetrics;
                speaker.Clip = AudioClip.Create("Voice mixer carrier", 16000, 1, 16000, false);
                speaker.Source.clip = speaker.Clip; speaker.Source.Play();
                _speakers.Add(frame.Speaker, speaker);
            }
            if (!speaker.Jitter.Push(frame, Time.realtimeSinceStartupAsDouble)) RejectedPackets++;
        }
        private void RemoveSpeaker(string id)
        { if (_speakers.TryGetValue(id, out var speaker)) { speaker.Dispose(); _speakers.Remove(id); } }
        private void ClearSpeakers()
        { foreach (var speaker in _speakers.Values) speaker.Dispose(); _speakers.Clear(); }
        private static void ResetBuffer(Speaker speaker)
        { speaker.Ring = new CampPcmRing(3200); Volatile.Write(ref speaker.Playback.Ring, speaker.Ring); }

        // Only reachable through the marked Development driver. No audio is written.
        public void SetTestMode(string mode, string mutePlayer = "")
        {
            if (Session == null || !Session.IsTestSession || !Debug.isDebugBuild) return;
            if (mode != "off" && mode != "tone" && mode != "mic") return;
            StopCapture(); _attempted = false; _testMode = mode;
            if (_testMute.Length > 0) MutePlayer(_testMute, false);
            _testMute = mutePlayer ?? ""; if (_testMute.Length > 0) MutePlayer(_testMute, true);
        }
        [Serializable] private sealed class Diagnostics
        {
            public string Mode, Status, Device;
            public bool Capturing;
            public int Devices, Speakers, Stream;
            public float Level;
            public long CapturedSamples, SentPackets, ReceivedPackets, DecodedPackets, RejectedPackets, PlayedSamples, PlayedNonzero;
            public long CallbackRequestedSamples, CallbackMaximumSamples, RingDroppedSamples;
        }
        private void WriteDiagnostics()
        {
            var data = new Diagnostics { Mode = _testMode, Status = Status, Device = Device, Devices = Devices.Length,
                Capturing = Capturing, Speakers = _speakers.Count, Stream = _stream, Level = LocalLevel, CapturedSamples = CapturedSamples,
                SentPackets = SentPackets, ReceivedPackets = ReceivedPackets, DecodedPackets = DecodedPackets,
                RejectedPackets = RejectedPackets, PlayedSamples = Interlocked.Read(ref _playbackMetrics.Played), PlayedNonzero = Interlocked.Read(ref _playbackMetrics.Nonzero),
                CallbackRequestedSamples = Interlocked.Read(ref _playbackMetrics.Requested), CallbackMaximumSamples = Interlocked.Read(ref _playbackMetrics.Maximum), RingDroppedSamples = _ringDroppedSamples };
            try { File.WriteAllText(Path.Combine(Session.TestRoot, "voice.json"), JsonUtility.ToJson(data)); } catch (IOException) { }
        }
        private void OnDisable() { StopCapture(); ClearSpeakers(); }
        private void OnDestroy()
        { if (Session != null) { Session.VoiceReceived -= Receive; Session.ViewChanged -= ObserveView; } StopCapture(); ClearSpeakers(); }
    }
}
