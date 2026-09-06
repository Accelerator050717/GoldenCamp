using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Presentation only: derives feedback from public views, never changes gameplay
    // state or records microphone audio. All synthesis and Unity access occur once
    // on the main thread; these cached clips have no streaming/audio callbacks.
    public sealed class CampSound : MonoBehaviour
    {
        public CampSession Session;
        public float Effects = .65f;
        public float Ambience = .25f;

        private const int SampleRate = 22050;
        private const int MaximumMiners = 4;
        private const float StepDistance = 1.65f;
        private const double FreshViewSeconds = 1d;
        private readonly Dictionary<string, MinerSound> _miners = new Dictionary<string, MinerSound>(MaximumMiners);
        private readonly List<string> _removed = new List<string>(MaximumMiners);
        private AudioClip _pick, _repair, _craft, _upgrade, _step, _campClip, _engineClip;
        private AudioSource _camp, _engine;
        private CampView _view;
        private string _world;
        private double _viewTime;
        private bool _ready, _subscribed;

        private sealed class MinerSound
        {
            public AudioSource Source;
            public Vector3 Position;
            public string Action;
            public double ActionUntil, ViewTime;
            public float Travel;
            public bool WasWalking, Seen;
        }

        private void Start()
        {
            if (Session == null) Session = GetComponent<CampSession>();
            if (Session == null) { enabled = false; return; }
            if (!Session.IsTestSession)
            {
                Effects = Level(PlayerPrefs.GetFloat("Demo.EffectsVolume", .65f));
                Ambience = Level(PlayerPrefs.GetFloat("Demo.AmbienceVolume", .25f));
            }
            _pick = Impact("Pick striking stone", .20f, 740f, 1200f, 38f, 11);
            _repair = ToolTaps("Tool repair", false);
            _craft = ToolTaps("Ladder woodwork", true);
            _upgrade = UpgradeTone();
            _step = Impact("Gravel footstep", .13f, 82f, 133f, 34f, 23);
            _campClip = CampLoop();
            _engineClip = EngineLoop();
            _camp = Source("Camp wind and fire", new Vector3(0f, .6f, -10f));
            _camp.clip = _campClip; _camp.loop = true;
            _engine = Source("Truck engine", CampMap.VehicleHome + Vector3.up * .7f);
            _engine.clip = _engineClip; _engine.loop = true;
            _ready = true;
            Subscribe();
        }

        private void OnEnable()
        {
            if (_ready) Subscribe();
        }

        private void Subscribe()
        {
            if (_subscribed || !isActiveAndEnabled || Session == null) return;
            Session.ViewChanged += ObserveView;
            _subscribed = true;
            ObserveView(Session.View);
        }

        private void Unsubscribe()
        {
            if (_subscribed && Session != null) Session.ViewChanged -= ObserveView;
            _subscribed = false;
        }

        public void SavePreferences()
        {
            if (Session == null || Session.IsTestSession) return;
            Effects = Level(Effects); Ambience = Level(Ambience);
            PlayerPrefs.SetFloat("Demo.EffectsVolume", Effects);
            PlayerPrefs.SetFloat("Demo.AmbienceVolume", Ambience);
            PlayerPrefs.Save();
        }

        private void ObserveView(CampView view)
        {
            if (!_ready || !isActiveAndEnabled) return;
            if (view?.Self == null || string.IsNullOrEmpty(view.WorldId) || view.Players == null || !Finite(view.OnlineSeconds))
            { EndView(); return; }
            if (_world != view.WorldId)
            {
                ClearMiners();
                _world = view.WorldId;
                _viewTime = view.OnlineSeconds;
            }
            else if (view.OnlineSeconds < _viewTime) return;
            _view = view;
            _viewTime = view.OnlineSeconds;
            foreach (MinerSound sound in _miners.Values) sound.Seen = false;
            foreach (PublicMiner miner in view.Players)
            {
                if (miner == null || !miner.Online || string.IsNullOrEmpty(miner.Id) || !Finite(miner.Position)) continue;
                bool walking = miner.VehicleSeat < 0 && string.IsNullOrEmpty(miner.ClimbingLadderId) &&
                    miner.StunnedUntil <= view.OnlineSeconds;
                if (!_miners.TryGetValue(miner.Id, out MinerSound sound))
                {
                    if (_miners.Count >= MaximumMiners) continue;
                    sound = new MinerSound { Source = Source("Miner feedback", miner.Position + Vector3.up * .6f) };
                    _miners.Add(miner.Id, sound);
                    Seed(sound, miner, view.OnlineSeconds, walking);
                    continue; // Joining/reconnecting never replays an already active action.
                }
                sound.Seen = true;
                sound.Source.transform.position = miner.Position + Vector3.up * .6f;
                sound.Source.volume = Level(Effects);
                double elapsed = view.OnlineSeconds - sound.ViewTime;
                bool fresh = elapsed >= 0d && elapsed <= FreshViewSeconds;
                bool changedAction = miner.Action != sound.Action || miner.ActionUntil > sound.ActionUntil + .00001d;
                if (fresh && changedAction && Finite(miner.ActionUntil) && miner.ActionUntil > view.OnlineSeconds)
                {
                    AudioClip clip = ActionClip(miner.Action);
                    if (clip != null) sound.Source.PlayOneShot(clip, miner.Action == "mine" ? .65f : .5f);
                }

                Vector3 travel = miner.Position - sound.Position;
                float horizontal = new Vector2(travel.x, travel.z).magnitude;
                if (fresh && elapsed > 0d && walking && sound.WasWalking && horizontal <= 4f && Math.Abs(travel.y) <= .35f)
                {
                    sound.Travel += horizontal;
                    if (sound.Travel >= StepDistance)
                    {
                        sound.Travel %= StepDistance;
                        sound.Source.PlayOneShot(_step, .30f); // At most one step per fresh snapshot, no catch-up burst.
                    }
                }
                else sound.Travel = 0f;
                Seed(sound, miner, view.OnlineSeconds, walking);
            }
            _removed.Clear();
            foreach (var pair in _miners) if (!pair.Value.Seen) _removed.Add(pair.Key);
            foreach (string id in _removed)
            {
                DestroySource(_miners[id].Source);
                _miners.Remove(id);
            }
            UpdateLoops();
        }

        private static void Seed(MinerSound sound, PublicMiner miner, double time, bool walking)
        {
            sound.Position = miner.Position;
            sound.Action = miner.Action;
            sound.ActionUntil = miner.ActionUntil;
            sound.ViewTime = time;
            sound.WasWalking = walking;
            sound.Seen = true;
        }

        private AudioClip ActionClip(string action)
        {
            switch (action)
            {
                case "mine": return _pick;
                case "repair": return _repair;
                case "craft": return _craft;
                case "upgrade": return _upgrade;
                default: return null;
            }
        }

        private void Update()
        {
            if (!_ready) return;
            if (Session == null || Session.View?.Self == null) { EndView(); return; }
            foreach (MinerSound sound in _miners.Values) sound.Source.volume = Level(Effects);
            UpdateLoops();
        }

        private void UpdateLoops()
        {
            if (_view == null) return;
            _camp.volume = Level(Ambience) * .15f;
            if (!_camp.isPlaying) _camp.Play();
            CampVehicle vehicle = _view.Vehicle;
            bool validVehicle = _view.RoadUnlocked && vehicle != null && Finite(vehicle.Position) && Finite(vehicle.Speed);
            bool occupied = validVehicle && vehicle.Seats != null && vehicle.Seats.Exists(id => !string.IsNullOrEmpty(id));
            float speed = validVehicle ? Math.Abs(vehicle.Speed) : 0f;
            if (!validVehicle || (!occupied && speed <= .05f)) { if (_engine.isPlaying) _engine.Stop(); return; }
            _engine.transform.position = vehicle.Position + Vector3.up * .7f;
            float motion = Mathf.Clamp01(speed / 8f);
            _engine.pitch = .8f + .65f * motion;
            _engine.volume = Level(Effects) * (.06f + .19f * motion);
            if (!_engine.isPlaying) _engine.Play();
        }

        private AudioSource Source(string name, Vector3 position)
        {
            var root = new GameObject(name);
            root.transform.SetParent(transform, false);
            root.transform.position = position;
            var source = root.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 3f;
            source.maxDistance = 20f;
            source.dopplerLevel = 0f;
            source.volume = Level(Effects);
            return source;
        }

        private void EndView()
        {
            if (_view == null && _world == null) return;
            _view = null; _world = null;
            ClearMiners();
            if (_camp != null) _camp.Stop();
            if (_engine != null) _engine.Stop();
        }

        private void ClearMiners()
        {
            foreach (MinerSound sound in _miners.Values) DestroySource(sound.Source);
            _miners.Clear();
            _removed.Clear();
        }

        private void OnDisable()
        {
            Unsubscribe();
            foreach (MinerSound sound in _miners.Values) sound.Source.Stop();
            if (_camp != null) _camp.Pause();
            if (_engine != null) _engine.Pause();
            _view = null; _world = null; // Re-enable seeds a fresh snapshot rather than replaying old action audio.
        }

        private void OnDestroy()
        {
            Unsubscribe();
            ClearMiners();
            DestroySource(_camp); DestroySource(_engine);
            foreach (AudioClip clip in new[] { _pick, _repair, _craft, _upgrade, _step, _campClip, _engineClip })
                if (clip != null) Destroy(clip);
        }

        private static void DestroySource(AudioSource source)
        {
            if (source == null) return;
            source.Stop();
            Destroy(source.gameObject);
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static float Level(float value) => Finite(value) ? Mathf.Clamp01(value) : 0f;

        private static AudioClip Clip(string name, float[] samples)
        {
            var clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
            clip.hideFlags = HideFlags.DontSave;
            clip.SetData(samples, 0);
            return clip;
        }

        private static float Noise(ref uint state)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0xffff) / 32767.5f - 1f;
        }

        private static AudioClip Impact(string name, float duration, float first, float second, float decay, uint seed)
        {
            var samples = new float[(int)(duration * SampleRate)];
            float low = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                double time = (double)i / SampleRate;
                low += .12f * (Noise(ref seed) - low);
                double tone = .40d * Math.Sin(2d * Math.PI * first * time) + .20d * Math.Sin(2d * Math.PI * second * time);
                double envelope = Math.Min(1d, time * 1200d) * Math.Exp(-time * decay);
                double tail = Math.Min(1d, (samples.Length - 1 - i) / 80d);
                samples[i] = (float)((tone + low * .22f) * envelope * tail);
            }
            return Clip(name, samples);
        }

        private static AudioClip ToolTaps(string name, bool wood)
        {
            var samples = new float[(int)(SampleRate * .34f)];
            for (int i = 0; i < samples.Length; i++)
            {
                double time = (double)i / SampleRate;
                double value = 0d;
                for (int tap = 0; tap < 3; tap++)
                {
                    double local = time - tap * .09d;
                    if (local < 0d) continue;
                    double frequency = wood ? 170d + tap * 18d : 640d + tap * 75d;
                    value += (.25d * Math.Sin(2d * Math.PI * frequency * local) +
                        .1d * Math.Sin(2d * Math.PI * frequency * 1.57d * local)) *
                        Math.Min(1d, local * 1100d) * Math.Exp(-local * (wood ? 38d : 45d));
                }
                samples[i] = (float)(value * Math.Min(1d, (samples.Length - 1 - i) / 80d));
            }
            return Clip(name, samples);
        }

        private static AudioClip UpgradeTone()
        {
            var samples = new float[SampleRate / 2];
            double[] notes = { 440d, 554.365d, 659.255d };
            for (int i = 0; i < samples.Length; i++)
            {
                double time = (double)i / SampleRate;
                double value = 0d;
                for (int note = 0; note < notes.Length; note++)
                {
                    double local = time - note * .11d;
                    if (local < 0d || local > .22d) continue;
                    double envelope = Math.Sin(Math.PI * local / .22d);
                    value += .22d * envelope * envelope * Math.Sin(2d * Math.PI * notes[note] * local);
                }
                samples[i] = (float)value;
            }
            return Clip("Equipment upgrade", samples);
        }

        private static AudioClip CampLoop()
        {
            var samples = new float[SampleRate * 4];
            uint seed = 71;
            float wind = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                double time = (double)i / SampleRate;
                wind += .006f * (Noise(ref seed) - wind); // Low-pass rumble, not broadband hiss over voices.
                double gust = .6d + .4d * Math.Sin(2d * Math.PI * .25d * time);
                double fire = 0d;
                for (int pop = 0; pop < 5; pop++)
                {
                    double local = time - (.23d + pop * .71d);
                    if (local >= 0d && local < .12d)
                        fire += .035d * Math.Sin(2d * Math.PI * (145d + pop * 11d) * local) *
                            Math.Min(1d, local * 1200d) * Math.Exp(-local * 45d);
                }
                double edge = Math.Min(1d, Math.Min(i, samples.Length - 1 - i) / (SampleRate * .04d));
                samples[i] = (float)((wind * .9d * gust + .035d * Math.Sin(2d * Math.PI * 42d * time) + fire) * edge);
            }
            return Clip("Soft camp wind and fire", samples);
        }

        private static AudioClip EngineLoop()
        {
            var samples = new float[SampleRate];
            for (int i = 0; i < samples.Length; i++)
            {
                double time = (double)i / SampleRate;
                double pulse = .78d + .22d * Math.Sin(2d * Math.PI * 6d * time);
                samples[i] = (float)(pulse * (.28d * Math.Sin(2d * Math.PI * 48d * time) +
                    .10d * Math.Sin(2d * Math.PI * 96d * time) + .045d * Math.Sin(2d * Math.PI * 144d * time)));
            }
            return Clip("Small truck engine", samples);
        }
    }
}
