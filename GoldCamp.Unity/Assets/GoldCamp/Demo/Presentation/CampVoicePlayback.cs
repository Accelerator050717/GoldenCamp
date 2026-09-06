using System;
using System.Threading;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Unity's mixer requests small DSP blocks here. Streaming AudioClip reader
    // callbacks can prefetch bursts and are unsuitable as a live PCM clock.
    public sealed class CampVoicePlayback : MonoBehaviour
    {
        public sealed class Counters { public long Played, Nonzero, Requested, Maximum; }
        public CampPcmRing Ring;
        public Counters Metrics;
        public float Gain;
        private int _outputRate = 48000;
        private readonly CampVoicePcmPlayer _player = new CampVoicePcmPlayer();

        private void OnEnable()
        { _outputRate = AudioSettings.outputSampleRate; AudioSettings.OnAudioConfigurationChanged += Changed; }
        private void Changed(bool deviceChanged) => Volatile.Write(ref _outputRate, AudioSettings.outputSampleRate);
        private void OnDisable() => AudioSettings.OnAudioConfigurationChanged -= Changed;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            // No Unity API, network, codec, lock or allocation on this callback.
            var ring = Volatile.Read(ref Ring); var metrics = Metrics;
            _player.Render(ring, data, channels, Volatile.Read(ref _outputRate),
                Volatile.Read(ref Gain), out int consumed, out int nonzero);
            if (metrics == null) return;
            Interlocked.Add(ref metrics.Played, consumed); Interlocked.Add(ref metrics.Nonzero, nonzero);
            Interlocked.Add(ref metrics.Requested, data.Length);
            long old = Interlocked.Read(ref metrics.Maximum);
            if (data.Length > old) Interlocked.CompareExchange(ref metrics.Maximum, data.Length, old);
        }
    }
}
