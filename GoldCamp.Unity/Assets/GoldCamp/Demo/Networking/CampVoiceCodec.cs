using System;
using System.Threading;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;

namespace GoldCamp.Demo
{
    // Dependency: Concentus-Unity.Runtime (package com.adrenak.concentus-unity 1.0.1).
    // Pin: https://github.com/adrenak/concentus-unity.git?path=/Assets/Concentus-Unity#f1564332c43ddf36f9129c5fca51d73c7b47d1fb
    // Preserve the dependency's Assets/Concentus-Unity/LICENSE in distributions:
    // https://github.com/adrenak/concentus-unity/blob/f1564332c43ddf36f9129c5fca51d73c7b47d1fb/Assets/Concentus-Unity/LICENSE
    // This file captures no microphone, accesses no network, and stores no audio files.
    public static class CampVoiceCodec
    {
        public const int SampleRate = 16000;
        public const int Channels = 1;
        public const int FrameMilliseconds = 20;
        public const int FrameSamples = 320;
        public const int Bitrate = 24000;
        public const int Complexity = 3;
        public const int MaximumPacketBytes = 400;

        // Header validation is deliberately separate from full decoder validation.
        // Empty network packets are invalid; loss concealment has its own explicit API.
        public static bool HasSupportedPacketFormat(ReadOnlySpan<byte> packet)
        {
            if (packet.Length == 0 || packet.Length > MaximumPacketBytes) return false;
            return OpusPacketInfo.GetNumEncodedChannels(packet) == Channels &&
                OpusPacketInfo.GetNumSamples(packet, SampleRate) == FrameSamples;
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static float Clip(float value) => value < -1f ? -1f : value > 1f ? 1f : value;

        internal static void RequireFrameDestination(Span<float> destination)
        {
            if (destination.Length < FrameSamples)
                throw new ArgumentException("A decoded frame needs at least 320 mono samples.", nameof(destination));
        }
    }

    // One instance per local encoding stream. All methods, Reset and Dispose belong
    // to the same worker/owner thread; none belongs in the audio playback callback.
    public sealed class CampVoiceEncoder : IDisposable
    {
        private const int MixBlockFrames = 1024;
        private readonly IOpusEncoder _encoder;
        private readonly IResampler _resampler;
        private readonly float[] _mixed = new float[MixBlockFrames];
        private readonly short[] _frame = new short[CampVoiceCodec.FrameSamples];
        private bool _disposed;

        public int SourceSampleRate { get; }
        public int SourceChannels { get; }
        public int ResamplerLatencySamples => _resampler?.OutputLatencySamples ?? 0;

        public CampVoiceEncoder(int sourceSampleRate, int sourceChannels = 1)
        {
            if (sourceSampleRate < 8000 || sourceSampleRate > 192000)
                throw new ArgumentOutOfRangeException(nameof(sourceSampleRate), "Capture rate must be 8000 to 192000 Hz.");
            if (sourceChannels < 1 || sourceChannels > 8)
                throw new ArgumentOutOfRangeException(nameof(sourceChannels), "Capture needs 1 to 8 interleaved channels.");
            SourceSampleRate = sourceSampleRate;
            SourceChannels = sourceChannels;
            _encoder = OpusCodecFactory.CreateEncoder(CampVoiceCodec.SampleRate, CampVoiceCodec.Channels,
                OpusApplication.OPUS_APPLICATION_VOIP);
            try
            {
                _encoder.Bitrate = CampVoiceCodec.Bitrate;
                _encoder.Complexity = CampVoiceCodec.Complexity;
                _encoder.UseVBR = true;
                _encoder.UseDTX = false;
                _encoder.UseInbandFEC = false; // Reordering/FEC policy belongs to the network jitter buffer.
                if (sourceSampleRate != CampVoiceCodec.SampleRate)
                {
                    _resampler = ResamplerFactory.CreateResampler(1, sourceSampleRate, CampVoiceCodec.SampleRate, 5);
                    // Speex retains its filter history across calls. Its documented real-time
                    // mode retains the initial latency; do not SkipZeroes or reset every block.
                }
            }
            catch
            {
                _resampler?.Dispose();
                _encoder.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Downmix and genuinely resample capture PCM into 16 kHz mono. Return value is
        /// output samples; inputFramesConsumed counts frames, NOT interleaved floats.
        /// Retain input.Slice(inputFramesConsumed * SourceChannels) if output fills.
        /// Accumulate exactly 320 output samples before EncodeFrame. Source and output
        /// must not overlap. Processing uses fixed scratch storage and no growing queue.
        /// </summary>
        public int Resample(ReadOnlySpan<float> input, Span<float> output, out int inputFramesConsumed)
        {
            ThrowIfDisposed();
            inputFramesConsumed = 0;
            if (input.Length % SourceChannels != 0)
                throw new ArgumentException("Capture input must contain complete interleaved frames.", nameof(input));
            if (input.Overlaps(output))
                throw new ArgumentException("Capture input and resampled output must not overlap.", nameof(output));
            // Validate before advancing the resampler so bad capture does not partly mutate a stream.
            for (int i = 0; i < input.Length; i++)
                if (!CampVoiceCodec.Finite(input[i]))
                    throw new ArgumentException("Capture PCM contains a non-finite sample.", nameof(input));

            int inputFrames = input.Length / SourceChannels;
            int written = 0;
            while (inputFramesConsumed < inputFrames && written < output.Length)
            {
                int blockFrames = Math.Min(MixBlockFrames, inputFrames - inputFramesConsumed);
                if (_resampler == null) blockFrames = Math.Min(blockFrames, output.Length - written);
                int sourceStart = inputFramesConsumed * SourceChannels;
                for (int frame = 0; frame < blockFrames; frame++)
                {
                    double sum = 0d;
                    for (int channel = 0; channel < SourceChannels; channel++)
                        sum += CampVoiceCodec.Clip(input[sourceStart + frame * SourceChannels + channel]);
                    _mixed[frame] = (float)(sum / SourceChannels);
                }
                int consumed = blockFrames;
                int produced;
                if (_resampler == null)
                {
                    produced = blockFrames;
                    _mixed.AsSpan(0, produced).CopyTo(output.Slice(written, produced));
                }
                else
                {
                    produced = output.Length - written;
                    _resampler.Process(0, _mixed.AsSpan(0, blockFrames), ref consumed,
                        output.Slice(written), ref produced);
                    if (consumed < 0 || consumed > blockFrames || produced < 0 || produced > output.Length - written)
                        throw new InvalidOperationException("Speex returned invalid consumption counts.");
                    for (int i = 0; i < produced; i++)
                    {
                        float sample = output[written + i];
                        if (!CampVoiceCodec.Finite(sample))
                            throw new InvalidOperationException("Speex produced a non-finite sample; reset this stream.");
                        output[written + i] = CampVoiceCodec.Clip(sample);
                    }
                }
                inputFramesConsumed += consumed;
                written += produced;
                if (consumed == 0 && produced == 0) break;
            }
            return written;
        }

        /// <summary>
        /// Encode exactly one 20 ms mono frame. Destination must have at least 400 bytes;
        /// only the returned prefix is an Opus packet. Caller owns both spans and must
        /// copy/transfer that prefix before reusing a queued network buffer.
        /// </summary>
        public int EncodeFrame(ReadOnlySpan<float> monoFrame, Span<byte> destination)
        {
            ThrowIfDisposed();
            if (monoFrame.Length != CampVoiceCodec.FrameSamples)
                throw new ArgumentException("Encoding requires exactly 320 mono samples at 16000 Hz.", nameof(monoFrame));
            if (destination.Length < CampVoiceCodec.MaximumPacketBytes)
                throw new ArgumentException("The encoder destination needs at least 400 bytes.", nameof(destination));
            for (int i = 0; i < monoFrame.Length; i++)
            {
                float value = monoFrame[i];
                if (!CampVoiceCodec.Finite(value))
                    throw new ArgumentException("Voice PCM contains a non-finite sample.", nameof(monoFrame));
                value = CampVoiceCodec.Clip(value);
                _frame[i] = value >= 1f ? short.MaxValue : value <= -1f ? short.MinValue : (short)(value * 32768f);
            }
            // The pinned float overload allocates a temporary short[] itself. Reuse ours.
            int count = _encoder.Encode(_frame.AsSpan(), CampVoiceCodec.FrameSamples, destination,
                CampVoiceCodec.MaximumPacketBytes);
            if (count < 1 || count > CampVoiceCodec.MaximumPacketBytes ||
                !CampVoiceCodec.HasSupportedPacketFormat(destination.Slice(0, count)))
                throw new InvalidOperationException("Encoder produced an unsupported voice packet.");
            return count;
        }

        public void Reset()
        {
            ThrowIfDisposed();
            _encoder.ResetState();
            _resampler?.ResetMem();
            Array.Clear(_mixed, 0, _mixed.Length);
            Array.Clear(_frame, 0, _frame.Length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _resampler?.Dispose(); }
            finally
            {
                _encoder.Dispose();
                Array.Clear(_mixed, 0, _mixed.Length);
                Array.Clear(_frame, 0, _frame.Length);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CampVoiceEncoder));
        }
    }

    // One instance per remote speaker AND stream generation, owned by one worker.
    // Call only in playout order. It intentionally knows nothing about network sequence IDs.
    public sealed class CampVoiceDecoder : IDisposable
    {
        private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(CampVoiceCodec.SampleRate, 1);
        private readonly short[] _frame = new short[CampVoiceCodec.FrameSamples];
        private bool _disposed;

        public bool TryDecodePacket(ReadOnlySpan<byte> packet, Span<float> destination, out int samplesWritten)
        {
            ThrowIfDisposed();
            CampVoiceCodec.RequireFrameDestination(destination);
            samplesWritten = 0;
            destination.Slice(0, CampVoiceCodec.FrameSamples).Clear();
            if (!CampVoiceCodec.HasSupportedPacketFormat(packet)) return false;
            return TryDecode(packet, destination, out samplesWritten);
        }

        // The jitter scheduler, not an empty network packet, requests one missing 20 ms frame.
        public bool TryConcealLostFrame(Span<float> destination, out int samplesWritten)
        {
            ThrowIfDisposed();
            CampVoiceCodec.RequireFrameDestination(destination);
            destination.Slice(0, CampVoiceCodec.FrameSamples).Clear();
            return TryDecode(ReadOnlySpan<byte>.Empty, destination, out samplesWritten);
        }

        private bool TryDecode(ReadOnlySpan<byte> packet, Span<float> destination, out int samplesWritten)
        {
            samplesWritten = 0;
            try
            {
                int count = _decoder.Decode(packet, _frame.AsSpan(), CampVoiceCodec.FrameSamples, false);
                if (count != CampVoiceCodec.FrameSamples)
                {
                    Reset();
                    return false;
                }
                for (int i = 0; i < count; i++) destination[i] = _frame[i] / 32768f;
                samplesWritten = count;
                return true;
            }
            catch (OpusException) { Reset(); return false; }
            catch (ArgumentException) { Reset(); return false; }
            catch (IndexOutOfRangeException) { Reset(); return false; }
        }

        public void Reset()
        {
            ThrowIfDisposed();
            _decoder.ResetState();
            Array.Clear(_frame, 0, _frame.Length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _decoder.Dispose();
            Array.Clear(_frame, 0, _frame.Length);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CampVoiceDecoder));
        }
    }

    /// <summary>
    /// Fixed-capacity mono PCM FIFO: exactly ONE producer calls Write and ONE consumer
    /// calls Read. Producer/consumer never overwrite each other's index. Full writes
    /// accept only the fitting prefix (drop newest); empty reads fill the rest with zero.
    /// The audio callback needs only Read: no locks, waiting, allocations, Unity or codecs.
    /// Producer provides finite normalized PCM. ResetWhenStopped requires BOTH sides stopped.
    /// </summary>
    public sealed class CampPcmRing
    {
        private readonly float[] _samples;
        private int _read;
        private int _write;
        public int Capacity => _samples.Length - 1;

        public CampPcmRing(int capacitySamples)
        {
            if (capacitySamples < 1 || capacitySamples == int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(capacitySamples));
            _samples = new float[capacitySamples + 1]; // One sentinel slot distinguishes full from empty.
        }

        // Return count accepted; caller decides how to report/drop the unaccepted suffix.
        public int Write(ReadOnlySpan<float> source)
        {
            int write = _write;
            int read = Volatile.Read(ref _read);
            int used = write >= read ? write - read : _samples.Length - read + write;
            int count = Math.Min(source.Length, Capacity - used);
            int first = Math.Min(count, _samples.Length - write);
            source.Slice(0, first).CopyTo(_samples.AsSpan(write, first));
            source.Slice(first, count - first).CopyTo(_samples.AsSpan(0, count - first));
            Volatile.Write(ref _write, Advance(write, count));
            return count;
        }

        // Return actual queued samples, while ALWAYS filling the complete destination.
        public int Read(Span<float> destination)
        {
            int read = _read;
            int write = Volatile.Read(ref _write);
            int available = write >= read ? write - read : _samples.Length - read + write;
            int count = Math.Min(destination.Length, available);
            int first = Math.Min(count, _samples.Length - read);
            _samples.AsSpan(read, first).CopyTo(destination.Slice(0, first));
            _samples.AsSpan(0, count - first).CopyTo(destination.Slice(first, count - first));
            destination.Slice(count).Clear();
            Volatile.Write(ref _read, Advance(read, count));
            return count;
        }

        public void ResetWhenStopped()
        {
            // Not a concurrent API. Stop the codec producer AND audio consumer first.
            Array.Clear(_samples, 0, _samples.Length);
            Volatile.Write(ref _read, 0);
            Volatile.Write(ref _write, 0);
        }

        private int Advance(int index, int count)
        {
            int untilEnd = _samples.Length - index;
            return count >= untilEnd ? count - untilEnd : index + count;
        }
    }
}
