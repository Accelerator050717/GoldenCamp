using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoldCamp.Demo
{
    // Network DTO only. Audio, sequence histories and rate limits never enter CampState.
    [Serializable]
    public sealed class CampVoiceFrame
    {
        public int Sequence;
        public int Stream;
        public byte[] Data;
        public string Speaker;
    }

    // The session resolves a live peer to trustedActor BEFORE calling this router.
    // One owner thread calls every method; this class does not access Unity objects,
    // Authority, transport, capture or playback. now is monotonic, unscaled seconds.
    public sealed class CampVoiceRouter
    {
        public const double PacketsPerSecond = 50d;
        public const int BurstPackets = 10;

        private sealed class ActorStream
        {
            public double Tokens = BurstPackets;
            public double LastTime;
            public int Stream;
            public int Sequence = -1;
            public bool Stopped;
        }

        private readonly Dictionary<string, ActorStream> _actors =
            new Dictionary<string, ActorStream>(StringComparer.Ordinal);

        public int TrackedActors => _actors.Count;

        // Null means rejection. Accepted packets own a copy of Data; forwarding must
        // use THIS result, never the client frame or its client-supplied Speaker.
        public CampVoiceFrame Accept(string trustedActor, CampVoiceFrame frame, double now)
        {
            if (string.IsNullOrWhiteSpace(trustedActor) || !CampVoicePacket.ValidTime(now) ||
                !CampVoicePacket.Valid(frame)) return null;

            bool exists = _actors.TryGetValue(trustedActor, out ActorStream state);
            if (!exists) state = new ActorStream { LastTime = now };
            if (now < state.LastTime || frame.Stream < state.Stream ||
                (frame.Stream == state.Stream && (state.Stopped || frame.Sequence <= state.Sequence)))
                return null;

            double tokens = Math.Min(BurstPackets,
                state.Tokens + (now - state.LastTime) * PacketsPerSecond);
            if (tokens + 1e-9d < 1d) return null;

            CampVoiceFrame accepted = CampVoicePacket.Copy(frame, trustedActor);
            state.Tokens = Math.Max(0d, tokens - 1d);
            state.LastTime = now;
            state.Stream = frame.Stream;
            state.Sequence = frame.Sequence;
            state.Stopped = false;
            if (!exists) _actors.Add(trustedActor, state);
            return accepted;
        }

        // Use for an authenticated stop/mute event. Keep rate limits and stream high
        // watermarks: restarting PTT must increase Stream, not revive old audio.
        public void StopActor(string trustedActor)
        {
            if (trustedActor != null && _actors.TryGetValue(trustedActor, out ActorStream state))
                state.Stopped = true;
        }

        // Connection-lifetime reset only. The session must first reject/remove the
        // old peer so its in-flight packets cannot enter this fresh actor lifetime.
        public void ResetActor(string trustedActor)
        {
            if (trustedActor != null) _actors.Remove(trustedActor);
        }

        public void Clear() => _actors.Clear();
    }

    public static class CampVoiceProximity
    {
        public const float FullVolumeRange = 3f;
        public const float DefaultMaximumRange = 20f;

        public static bool CanHear(Vector3 a, Vector3 b, float maxRange = DefaultMaximumRange) =>
            Gain(a, b, maxRange) > 0f;

        // Pure distance: no physics query or wall penalty. Exactly maxRange is silent.
        public static float Gain(Vector3 a, Vector3 b, float maxRange = DefaultMaximumRange)
        {
            if (!Finite(a) || !Finite(b) || !Finite(maxRange) || maxRange <= 0f) return 0f;
            double x = (double)a.x - b.x;
            double y = (double)a.y - b.y;
            double z = (double)a.z - b.z;
            double distance = Math.Sqrt(x * x + y * y + z * z);
            if (distance >= maxRange) return 0f;
            if (distance <= FullVolumeRange) return 1f;
            return (float)((maxRange - distance) / (maxRange - FullVolumeRange));
        }

        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public enum CampVoiceJitterResult
    {
        Waiting,
        Packet,
        Concealment,
        Stopped
    }

    // One buffer per trusted remote speaker, owned by one decode worker. Push and
    // Tick must be serialized on that owner; the audio callback only reads PCM.
    // Stream increases for each PTT onset. A new stream resets the decoder upstream;
    // Stopped tells the consumer to reset decoder/PCM, even if a fresh packet has
    // already begun another prefill. Timeout can resume; an explicit Stop closes it.
    public sealed class CampVoiceJitter
    {
        public const int Capacity = 10;
        public const double PrebufferSeconds = 0.060d;
        public const double FrameSeconds = 0.020d;
        public const double SilenceSeconds = 0.500d;
        private const double ClockTolerance = 1e-9d;

        private readonly CampVoiceFrame[] _packets = new CampVoiceFrame[Capacity];
        private int _count;
        private int _stream;
        private int _highestSeen = -1;
        private long _consumedThrough = -1;
        private long _expected;
        private bool _active;
        private bool _playing;
        private bool _stopPending;
        private bool _resumeAllowed;
        private double _nextTick;
        private double _lastAcceptedAt;
        private double _lastTime = double.NegativeInfinity;

        public int Count => _count;
        public int CurrentStream => _stream;
        public bool IsActive => _active;

        public bool Push(CampVoiceFrame frame, double now)
        {
            if (!CampVoicePacket.Valid(frame) || !ObserveTime(now)) return false;
            Expire(now);
            if (frame.Stream < _stream) return false;
            if (frame.Stream == _stream && !_active)
            {
                if (!_resumeAllowed || frame.Sequence <= _highestSeen || frame.Sequence <= _consumedThrough)
                    return false;
                // Everything seen before timeout is retired, including unplayed
                // packets that were cleared. Fresh packets can still reorder above
                // this floor. Sequence freshness alone does not prove transit age.
                _consumedThrough = Math.Max(_consumedThrough, _highestSeen);
            }

            if (frame.Stream > _stream)
            {
                _stream = frame.Stream;
                _highestSeen = -1;
                _consumedThrough = -1;
                _active = false;
            }
            if (!_active)
            {
                EmptyQueue();
                _active = true;
                _playing = false;
                _resumeAllowed = false;
                _expected = frame.Sequence;
                _nextTick = now + PrebufferSeconds;
            }

            if (frame.Sequence <= _consumedThrough || (_playing && frame.Sequence < _expected)) return false;
            for (int i = 0; i < _count; i++)
                if (_packets[i].Sequence == frame.Sequence) return false;

            // Bound both memory AND the sequence window. A huge gap cannot create
            // minutes of PLC or allow ancient queued voice to build up latency.
            long highest = _count == 0 ? frame.Sequence : Math.Max(frame.Sequence, _packets[_count - 1].Sequence);
            long oldestAllowed = highest - (Capacity - 1L);
            if (frame.Sequence < oldestAllowed) return false;
            DropBefore(oldestAllowed);
            if (_playing && _expected < oldestAllowed) _expected = oldestAllowed;

            int index = _count;
            while (index > 0 && _packets[index - 1].Sequence > frame.Sequence)
            {
                _packets[index] = _packets[index - 1];
                index--;
            }
            _packets[index] = CampVoicePacket.Copy(frame, frame.Speaker);
            _count++;
            _highestSeen = Math.Max(_highestSeen, frame.Sequence);
            _lastAcceptedAt = now;
            return true;
        }

        // Take one due 20 ms frame. A main-thread consumer may call this in a bounded
        // loop to fill PCM after a short frame delay. At most ten deadlines remain
        // due after a long stall; older deadlines and packets are skipped together.
        public CampVoiceJitterResult Tick(double now, out CampVoiceFrame frame)
        {
            frame = null;
            if (!ObserveTime(now)) return CampVoiceJitterResult.Waiting;
            Expire(now);
            if (_stopPending)
            {
                _stopPending = false;
                return CampVoiceJitterResult.Stopped;
            }
            if (!_active || now + ClockTolerance < _nextTick) return CampVoiceJitterResult.Waiting;

            if (!_playing)
            {
                _playing = true;
                _expected = _packets[0].Sequence;
            }
            double lateFrames = Math.Floor(Math.Max(0d, now - _nextTick + ClockTolerance) / FrameSeconds);
            long skipped = (long)Math.Min(int.MaxValue + 1d, Math.Max(0d, lateFrames - (Capacity - 1d)));
            _expected = Math.Min(int.MaxValue + 1L, _expected + skipped);
            _nextTick = lateFrames > int.MaxValue ? now + FrameSeconds :
                _nextTick + (skipped + 1L) * FrameSeconds;
            DropBefore(_expected);

            if (_expected > int.MaxValue)
            {
                Stop();
                _stopPending = false;
                return CampVoiceJitterResult.Stopped;
            }

            CampVoiceJitterResult result = CampVoiceJitterResult.Concealment;
            if (_count > 0 && _packets[0].Sequence == _expected)
            {
                frame = _packets[0];
                RemoveFirst();
                result = CampVoiceJitterResult.Packet;
            }
            _consumedThrough = Math.Max(_consumedThrough, _expected);
            _expected++;
            return result;
        }

        // Explicit mute/PTT-stop closes the current stream, even after a resumable
        // timeout. Clear is only for a removed peer/session whose packets cannot return.
        public void Stop() => StopCurrent(false);

        private void StopCurrent(bool allowResume)
        {
            if (_active) _stopPending = true;
            _active = false;
            _playing = false;
            _resumeAllowed = allowResume;
            _expected = 0;
            _nextTick = 0d;
            _lastAcceptedAt = 0d;
            EmptyQueue();
        }

        public void Clear()
        {
            Stop();
            _stopPending = false;
            _stream = 0;
            _highestSeen = -1;
            _consumedThrough = -1;
            _lastTime = double.NegativeInfinity;
        }

        private bool ObserveTime(double now)
        {
            if (!CampVoicePacket.ValidTime(now) || now < _lastTime) return false;
            _lastTime = now;
            return true;
        }

        private void Expire(double now)
        {
            if (_active && now - _lastAcceptedAt + ClockTolerance >= SilenceSeconds) StopCurrent(true);
        }

        private void DropBefore(long sequence)
        {
            while (_count > 0 && _packets[0].Sequence < sequence) RemoveFirst();
        }

        private void RemoveFirst()
        {
            for (int i = 1; i < _count; i++) _packets[i - 1] = _packets[i];
            _packets[--_count] = null;
        }

        private void EmptyQueue()
        {
            Array.Clear(_packets, 0, _packets.Length);
            _count = 0;
        }
    }

    internal static class CampVoicePacket
    {
        internal static bool ValidTime(double now) => !double.IsNaN(now) && !double.IsInfinity(now) && now >= 0d;

        internal static bool Valid(CampVoiceFrame frame)
        {
            if (frame == null || frame.Sequence < 0 || frame.Stream <= 0 || frame.Data == null ||
                frame.Data.Length < 1 || frame.Data.Length > CampVoiceCodec.MaximumPacketBytes) return false;
            try { return CampVoiceCodec.HasSupportedPacketFormat(frame.Data); }
            catch (ArgumentException) { return false; }
            catch (IndexOutOfRangeException) { return false; }
        }

        internal static CampVoiceFrame Copy(CampVoiceFrame frame, string speaker) => new CampVoiceFrame
        {
            Sequence = frame.Sequence,
            Stream = frame.Stream,
            Speaker = speaker,
            Data = (byte[])frame.Data.Clone()
        };
    }
}
