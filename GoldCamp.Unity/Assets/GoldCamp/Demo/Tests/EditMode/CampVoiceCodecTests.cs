using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Concentus;
using Concentus.Enums;
using NUnit.Framework;

namespace GoldCamp.Demo.Tests
{
    // Synthetic PCM tests only: no microphone, AudioSource, file capture or network.
    public sealed class CampVoiceCodecTests
    {
        [Test]
        public void RealOpusRoundTripKeepsContinuousSineFiniteAndAudible()
        {
            using (var encoder = new CampVoiceEncoder(16000))
            using (var decoder = new CampVoiceDecoder())
            {
                var packet = new byte[400];
                var decoded = new float[320];
                double energy = 0d;
                for (int frame = 0; frame < 12; frame++)
                {
                    float[] source = Tone(16000, 440f, frame);
                    int bytes = encoder.EncodeFrame(source, packet);
                    Assert.That(bytes, Is.GreaterThan(0));
                    Assert.That(bytes, Is.LessThanOrEqualTo(400));
                    Assert.That(CampVoiceCodec.HasSupportedPacketFormat(packet.AsSpan(0, bytes)), Is.True);
                    Assert.That(decoder.TryDecodePacket(packet.AsSpan(0, bytes), decoded, out int samples), Is.True);
                    Assert.That(samples, Is.EqualTo(320));
                    energy += Energy(decoded);
                }
                Assert.That(energy / 12d, Is.GreaterThan(.005d), "A real sine must survive the lossy codec with nonzero energy.");
            }
        }

        [Test]
        public void Speex48kTo16kConsumesAllCaptureAcrossSmallOutputBuffers()
        {
            using (var encoder = new CampVoiceEncoder(48000))
            {
                var output = new float[97];
                int producedTotal = 0;
                double energyTotal = 0d;
                for (int block = 0; block < 30; block++)
                {
                    float[] input = Tone(48000, 440f, block);
                    int offset = 0;
                    while (offset < input.Length)
                    {
                        int produced = encoder.Resample(input.AsSpan(offset), output, out int consumed);
                        Assert.That(consumed, Is.GreaterThanOrEqualTo(0));
                        Assert.That(consumed, Is.LessThanOrEqualTo(input.Length - offset));
                        Assert.That(produced, Is.GreaterThanOrEqualTo(0));
                        Assert.That(produced, Is.LessThanOrEqualTo(output.Length));
                        Assert.That(consumed + produced, Is.GreaterThan(0));
                        offset += consumed;
                        producedTotal += produced;
                        for (int i = 0; i < produced; i++)
                        {
                            Assert.That(IsFinite(output[i]), Is.True);
                            energyTotal += output[i] * output[i];
                        }
                    }
                    Assert.That(offset, Is.EqualTo(960));
                    Assert.That(producedTotal, Is.LessThanOrEqualTo((block + 1) * 320 + 1));
                }
                Assert.That(producedTotal, Is.EqualTo(9600).Within(1));
                Assert.That(energyTotal / producedTotal, Is.GreaterThan(.005d));
            }
        }

        [Test]
        public void Speex48kTo16kAttenuatesToneAboveTheNewNyquistLimit()
        {
            using (var encoder = new CampVoiceEncoder(48000))
            {
                var output = new float[320];
                double energy = 0d;
                int measured = 0;
                for (int block = 0; block < 20; block++)
                {
                    float[] input = Tone(48000, 11000f, block);
                    int offset = 0;
                    while (offset < input.Length)
                    {
                        int produced = encoder.Resample(input.AsSpan(offset), output, out int consumed);
                        Assert.That(consumed + produced, Is.GreaterThan(0));
                        offset += consumed;
                        if (block < 4) continue; // Exclude the filter startup transient.
                        for (int i = 0; i < produced; i++) { energy += output[i] * output[i]; measured++; }
                    }
                }
                Assert.That(measured, Is.GreaterThan(3000));
                Assert.That(Math.Sqrt(energy / measured), Is.LessThan(.03d),
                    "Dropping every third input sample would alias this tone at high amplitude.");
            }
        }

        [Test]
        public void CaptureDownmixHonorsInterleavedFrameConsumptionAndRejectsInvalidInput()
        {
            using (var encoder = new CampVoiceEncoder(16000, 2))
            {
                var input = new float[640];
                for (int i = 0; i < 320; i++) { input[i * 2] = .6f; input[i * 2 + 1] = -.2f; }
                var output = new float[73];
                int produced = encoder.Resample(input, output, out int consumed);
                Assert.That(consumed, Is.EqualTo(73), "Consumed count is source frames, not stereo floats.");
                Assert.That(produced, Is.EqualTo(73));
                for (int i = 0; i < produced; i++) Assert.That(output[i], Is.EqualTo(.2f).Within(.00001f));
                Assert.Throws<ArgumentException>(() => encoder.Resample(new float[3], output, out _));
                Assert.Throws<ArgumentException>(() => encoder.Resample(input, input, out _));
                input[1] = float.NaN;
                Assert.Throws<ArgumentException>(() => encoder.Resample(input, output, out _));
            }
            using (var encoder = new CampVoiceEncoder(16000))
            {
                Assert.Throws<ArgumentException>(() => encoder.EncodeFrame(new float[319], new byte[400]));
                Assert.Throws<ArgumentException>(() => encoder.EncodeFrame(new float[320], new byte[399]));
                var invalid = new float[320]; invalid[5] = float.PositiveInfinity;
                Assert.Throws<ArgumentException>(() => encoder.EncodeFrame(invalid, new byte[400]));
            }
        }

        [Test]
        public void InvalidPacketHeadersFailClosedAndDoNotReplayDestinationAudio()
        {
            var cases = new List<byte[]> { Array.Empty<byte>(), new byte[401], new byte[] { 3 }, new byte[] { 11, 255 } };
            using (var raw = OpusCodecFactory.CreateEncoder(16000, 1, OpusApplication.OPUS_APPLICATION_VOIP))
            {
                var buffer = new byte[400];
                foreach (int wrongSamples in new[] { 160, 640 })
                {
                    int length = raw.Encode(new short[wrongSamples], wrongSamples, buffer, buffer.Length);
                    var wrongDuration = new byte[length]; Array.Copy(buffer, wrongDuration, length);
                    cases.Add(wrongDuration);
                }
            }
            using (var rawStereo = OpusCodecFactory.CreateEncoder(16000, 2, OpusApplication.OPUS_APPLICATION_VOIP))
            {
                rawStereo.ForceChannels = 2;
                var buffer = new byte[400];
                int length = rawStereo.Encode(new short[640], 320, buffer, buffer.Length);
                var stereo = new byte[length]; Array.Copy(buffer, stereo, length); cases.Add(stereo);
            }
            using (var decoder = new CampVoiceDecoder())
            {
                var destination = new float[320];
                foreach (byte[] packet in cases)
                {
                    for (int i = 0; i < destination.Length; i++) destination[i] = .8f;
                    Assert.That(CampVoiceCodec.HasSupportedPacketFormat(packet), Is.False);
                    Assert.That(decoder.TryDecodePacket(packet, destination, out int samples), Is.False);
                    Assert.That(samples, Is.EqualTo(0));
                    for (int i = 0; i < destination.Length; i++) Assert.That(destination[i], Is.EqualTo(0f));
                }
            }
        }

        [Test]
        public void LossConcealmentIsExplicitBoundedAndResetRemovesSpeakerHistory()
        {
            using (var encoder = new CampVoiceEncoder(16000))
            using (var decoder = new CampVoiceDecoder())
            {
                var packet = new byte[400];
                var pcm = new float[320];
                for (int frame = 0; frame < 5; frame++)
                {
                    int bytes = encoder.EncodeFrame(Tone(16000, 500f, frame), packet);
                    Assert.That(decoder.TryDecodePacket(packet.AsSpan(0, bytes), pcm, out _), Is.True);
                }
                Assert.That(decoder.TryConcealLostFrame(pcm, out int concealed), Is.True);
                Assert.That(concealed, Is.EqualTo(320));
                Energy(pcm); // Also verifies every concealed sample is finite and bounded.
                decoder.Reset();
                Assert.That(decoder.TryConcealLostFrame(pcm, out concealed), Is.True);
                Assert.That(concealed, Is.EqualTo(320));
                Assert.That(Energy(pcm), Is.LessThan(.0000001d));
            }
        }

        [Test]
        public void PcmRingWrapsRejectsFullSuffixAndZeroFillsShortReads()
        {
            var ring = new CampPcmRing(5);
            Assert.That(ring.Write(new[] { .1f, .2f, .3f, .4f }), Is.EqualTo(4));
            var first = new float[3];
            Assert.That(ring.Read(first), Is.EqualTo(3));
            Assert.That(first, Is.EqualTo(new[] { .1f, .2f, .3f }));
            Assert.That(ring.Write(new[] { .5f, .6f, .7f, .8f, .9f }), Is.EqualTo(4));
            Assert.That(ring.Write(new[] { .9f }), Is.EqualTo(0));
            var wrapped = new float[7];
            Assert.That(ring.Read(wrapped), Is.EqualTo(5));
            Assert.That(wrapped, Is.EqualTo(new[] { .4f, .5f, .6f, .7f, .8f, 0f, 0f }));
            Assert.That(ring.Write(new[] { .4f, .5f }), Is.EqualTo(2));
            ring.ResetWhenStopped();
            Assert.That(ring.Read(wrapped), Is.EqualTo(0));
            Assert.That(wrapped, Is.EqualTo(new float[7]));
        }

        [Test]
        public void PcmRingSingleProducerConsumerPreservesOrderAcrossManyWraps()
        {
            const int total = 8192;
            var ring = new CampPcmRing(127);
            var input = new float[total];
            for (int i = 0; i < total; i++) input[i] = i / (float)total;
            int stop = 0;
            var producer = new Thread(() =>
            {
                int offset = 0;
                while (offset < total && Volatile.Read(ref stop) == 0)
                {
                    int written = ring.Write(input.AsSpan(offset, Math.Min(57, total - offset)));
                    offset += written;
                    if (written == 0) Thread.Yield();
                }
            }) { IsBackground = true };
            producer.Start();
            try
            {
                var output = new float[113];
                var timeout = Stopwatch.StartNew();
                int received = 0;
                while (received < total && timeout.ElapsedMilliseconds < 3000)
                {
                    int read = ring.Read(output);
                    for (int i = 0; i < read; i++) Assert.That(output[i], Is.EqualTo(input[received++]));
                    if (read == 0) Thread.Yield();
                }
                Assert.That(received, Is.EqualTo(total));
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                Assert.That(producer.Join(1000), Is.True);
            }
        }

        [Test]
        public void WorkerProducesReal48kPacketsWithOriginalStreamAndSequence()
        {
            using (var worker = new CampVoiceEncodeWorker(48000))
            using (var reference = new CampVoiceEncoder(48000))
            using (var decoder = new CampVoiceDecoder())
            {
                Assert.That(worker.InputBlockSamples, Is.EqualTo(960));
                var decoded = new float[320];
                var resampled = new float[640];
                var packets = new List<CampVoiceEncoded>();
                int referenceSamples = 0;
                double energy = 0d;
                for (int sequence = 0; sequence < 6; sequence++)
                {
                    float[] input = Tone(48000, 440f, sequence);
                    int offset = 0;
                    while (offset < input.Length)
                    {
                        int produced = reference.Resample(input.AsSpan(offset), resampled, out int consumed);
                        Assert.That(consumed + produced, Is.GreaterThan(0));
                        offset += consumed;
                        referenceSamples += produced;
                    }
                    Assert.That(worker.TryPush(input, 7, sequence), Is.True);
                    // Waiting for consumption, not a packet, permits legitimate partial
                    // resampler output to carry across capture-block boundaries.
                    WaitUntil(() => worker.InputBlockCount == 0, "Worker did not consume capture PCM.");
                    while (worker.TryPop(out CampVoiceEncoded ready)) packets.Add(ready);
                }
                Assert.That(packets.Count, Is.EqualTo(referenceSamples / 320), "Do not pad a partial frame into an extra packet.");
                Assert.That(packets.Count, Is.GreaterThan(0));
                int previous = -1;
                foreach (CampVoiceEncoded encoded in packets)
                {
                    Assert.That(encoded.Stream, Is.EqualTo(7));
                    Assert.That(encoded.Sequence, Is.GreaterThan(previous));
                    Assert.That(encoded.Sequence, Is.LessThan(6));
                    previous = encoded.Sequence;
                    Assert.That(encoded.Data.Length, Is.LessThanOrEqualTo(400));
                    Assert.That(IsFinite(encoded.Rms), Is.True);
                    Assert.That(encoded.Rms, Is.GreaterThan(0f));
                    Assert.That(decoder.TryDecodePacket(encoded.Data, decoded, out int count), Is.True);
                    Assert.That(count, Is.EqualTo(320));
                    energy += Energy(decoded);
                }
                Assert.That(energy / packets.Count, Is.GreaterThan(.005d));
                Assert.That(worker.Error, Is.EqualTo(""));
            }
        }

        [Test]
        public void WorkerQueuesStayBoundedAndDisposeStopsWithoutLiveCodecDisposal()
        {
            var worker = new CampVoiceEncodeWorker(16000);
            try
            {
                var timeout = Stopwatch.StartNew();
                float[] input = Tone(16000, 440f, 0);
                int sequence = 0;
                while (worker.DroppedOutputPackets == 0 && timeout.ElapsedMilliseconds < 5000)
                {
                    worker.TryPush(input, 1, sequence++); // Intentionally overload capture; arrays remain immutable.
                    Assert.That(worker.InputBlockCount, Is.LessThanOrEqualTo(5));
                    Assert.That(worker.OutputPacketCount, Is.LessThanOrEqualTo(10));
                    Thread.Sleep(1);
                }
                Assert.That(worker.OutputPacketCount, Is.EqualTo(10));
                Assert.That(worker.DroppedOutputPackets, Is.GreaterThan(0));
                Assert.That(worker.Error, Is.EqualTo(""));
            }
            finally
            {
                var elapsed = Stopwatch.StartNew();
                worker.Dispose();
                Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(2000));
                WaitUntil(() => worker.IsStopped, "Encoder worker did not finish after shutdown.");
                Assert.That(worker.TryPush(new float[320], 1, 500), Is.False);
                Assert.That(worker.TryPop(out _), Is.False);
                Assert.That(worker.InputBlockCount, Is.EqualTo(0));
                Assert.That(worker.OutputPacketCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void WorkerStreamSwitchDiscardsQueuedOldGeneration()
        {
            using (var worker = new CampVoiceEncodeWorker(16000))
            {
                Assert.That(worker.TryPush(Tone(16000, 440f, 0), 1, 0), Is.True);
                WaitUntil(() => worker.OutputPacketCount > 0, "Initial stream did not produce a packet.");
                Assert.That(worker.TryPush(Tone(16000, 660f, 0), 2, 100), Is.True);
                CampVoiceEncoded encoded = default;
                WaitUntil(() => worker.TryPop(out encoded), "Replacement stream did not produce a packet.");
                Assert.That(encoded.Stream, Is.EqualTo(2));
                Assert.That(encoded.Sequence, Is.EqualTo(100));
                Assert.That(worker.Error, Is.EqualTo(""));
            }
        }

        [Test]
        public void WorkerReportsBadCaptureAsAnErrorAndTerminatesSafely()
        {
            using (var worker = new CampVoiceEncodeWorker(16000))
            {
                Assert.That(worker.TryPush(new float[319], 1, 0), Is.False);
                Assert.That(worker.Error, Is.Not.EqualTo(""));
                var bad = new float[320]; bad[8] = float.NaN;
                Assert.That(worker.TryPush(bad, 1, 0), Is.True);
                WaitUntil(() => worker.IsStopped, "Invalid capture did not stop the failed worker.");
                Assert.That(worker.Error, Does.Contain("non-finite"));
                Assert.That(worker.TryPop(out _), Is.False);
            }
        }

        private static float[] Tone(int rate, float hz, int block)
        {
            int count = rate / 50;
            var result = new float[count];
            for (int i = 0; i < count; i++) result[i] = .5f * (float)Math.Sin(2d * Math.PI * hz * (block * count + i) / rate);
            return result;
        }

        private static bool IsFinite(float sample) => !float.IsNaN(sample) && !float.IsInfinity(sample);

        private static double Energy(float[] samples)
        {
            double sum = 0d;
            foreach (float value in samples)
            {
                Assert.That(IsFinite(value), Is.True);
                Assert.That(Math.Abs(value), Is.LessThanOrEqualTo(1f));
                sum += (double)value * value;
            }
            return sum / samples.Length;
        }

        private static void WaitUntil(Func<bool> condition, string message)
        {
            var timeout = Stopwatch.StartNew();
            bool satisfied = condition();
            while (!satisfied && timeout.ElapsedMilliseconds < 3000)
            {
                Thread.Sleep(1);
                satisfied = condition();
            }
            Assert.That(satisfied, Is.True, message);
        }
    }
}
