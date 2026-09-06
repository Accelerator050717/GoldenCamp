using System;

namespace GoldCamp.Demo
{
    // Audio-thread owned resampling state. The producer swaps whole rings on
    // stream changes; no old interpolation sample survives that boundary.
    public sealed class CampVoicePcmPlayer
    {
        private CampPcmRing _ring;
        private readonly float[] _one = new float[1];
        private double _phase;
        private float _left, _right;
        private bool _ready;

        public void Render(CampPcmRing ring, Span<float> output, int channels, int outputRate,
            float gain, out int consumed, out int nonzero)
        {
            consumed = nonzero = 0;
            if (ring == null || channels < 1 || outputRate < 8000 || outputRate > 192000 || output.Length % channels != 0)
            { output.Clear(); return; }
            if (!ReferenceEquals(ring, _ring))
            { _ring = ring; _ready = false; _phase = 0; _left = _right = 0; }
            gain = float.IsNaN(gain) ? 0 : Math.Max(0, Math.Min(1, gain));
            double step = (double)CampVoiceCodec.SampleRate / outputRate;
            if (!_ready)
            {
                _left = Read(ref consumed, ref nonzero); _right = Read(ref consumed, ref nonzero); _ready = true;
            }
            for (int at = 0; at < output.Length; at += channels)
            {
                float sample = (_left + (_right - _left) * (float)_phase) * gain;
                for (int channel = 0; channel < channels; channel++) output[at + channel] = sample;
                _phase += step;
                while (_phase >= 1)
                { _phase -= 1; _left = _right; _right = Read(ref consumed, ref nonzero); }
            }
        }

        private float Read(ref int consumed, ref int nonzero)
        {
            if (_ring.Read(_one) == 0) return 0;
            consumed++; if (Math.Abs(_one[0]) > .0001f) nonzero++;
            return _one[0];
        }
    }
}
