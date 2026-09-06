using System;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    public sealed class CampVoiceRoutingTests
    {
        private byte[] _packet;

        [SetUp]
        public void EncodeRealTwentyMillisecondVoicePacket()
        {
            var pcm = new float[CampVoiceCodec.FrameSamples];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (float)(0.25d * Math.Sin(2d * Math.PI * 440d * i / CampVoiceCodec.SampleRate));
            var destination = new byte[CampVoiceCodec.MaximumPacketBytes];
            using (var encoder = new CampVoiceEncoder(CampVoiceCodec.SampleRate))
            {
                int length = encoder.EncodeFrame(pcm, destination);
                _packet = new byte[length];
                Array.Copy(destination, _packet, length);
            }
            Assert.That(CampVoiceCodec.HasSupportedPacketFormat(_packet), Is.True);
        }

        [Test]
        public void RouterUsesTrustedSpeakerAndOwnsForwardedDataWithoutMutatingClientFrame()
        {
            var router = new CampVoiceRouter();
            CampVoiceFrame incoming = Frame(7, 2, "victim-forged-by-client");
            CampVoiceFrame accepted = router.Accept("authenticated-miner", incoming, 1d);
            Assert.That(accepted, Is.Not.Null);
            Assert.That(accepted.Speaker, Is.EqualTo("authenticated-miner"));
            Assert.That(accepted.Sequence, Is.EqualTo(7));
            Assert.That(accepted.Stream, Is.EqualTo(2));
            Assert.That(incoming.Speaker, Is.EqualTo("victim-forged-by-client"));
            Assert.That(accepted.Data, Is.EqualTo(_packet));
            Assert.That(accepted.Data, Is.Not.SameAs(incoming.Data));
            incoming.Data[0] ^= 0xff;
            incoming.Sequence = 99;
            Assert.That(accepted.Data, Is.EqualTo(_packet));
            Assert.That(accepted.Sequence, Is.EqualTo(7));
        }

        [Test]
        public void RouterRejectsBadBoundsAndShortOpusHeadersWithoutCreatingActorState()
        {
            var router = new CampVoiceRouter();
            var invalid = new[]
            {
                null,
                WithData(null),
                WithData(Array.Empty<byte>()),
                WithData(new byte[CampVoiceCodec.MaximumPacketBytes + 1]),
                WithData(new byte[] { 0x03 }), // Opus code 3 requires a second framing byte.
                Frame(-1),
                Frame(0, 0),
                Frame(0, -1)
            };
            foreach (CampVoiceFrame frame in invalid)
                Assert.That(router.Accept("actor", frame, 0d), Is.Null);
            Assert.That(router.Accept(null, Frame(), 0d), Is.Null);
            Assert.That(router.Accept(" ", Frame(), 0d), Is.Null);
            Assert.That(router.TrackedActors, Is.Zero);
            Assert.That(router.Accept("actor", Frame(), 0d), Is.Not.Null);
        }

        [Test]
        public void RouterBurstIsTenAndEachActorRefillsFiftyPacketsPerSecondIndependently()
        {
            var router = new CampVoiceRouter();
            for (int sequence = 0; sequence < 10; sequence++)
                Assert.That(router.Accept("a", Frame(sequence), 0d), Is.Not.Null);
            Assert.That(router.Accept("a", Frame(10), 0d), Is.Null);
            Assert.That(router.Accept("a", Frame(10), 0.019d), Is.Null);
            Assert.That(router.Accept("b", Frame(), 0.019d), Is.Not.Null);
            for (int index = 1; index <= 50; index++)
                Assert.That(router.Accept("a", Frame(index + 9), index * 0.020d), Is.Not.Null);
            Assert.That(router.Accept("a", Frame(60), 1d), Is.Null);
            Assert.That(router.TrackedActors, Is.EqualTo(2));
            // A long pause never accumulates more than the ten-packet burst.
            for (int sequence = 60; sequence < 70; sequence++)
                Assert.That(router.Accept("a", Frame(sequence), 100d), Is.Not.Null);
            Assert.That(router.Accept("a", Frame(70), 100d), Is.Null);
        }

        [Test]
        public void RouterRejectsReplayAndOlderStreamsWithoutSpendingFreshPacketBudget()
        {
            var router = new CampVoiceRouter();
            Assert.That(router.Accept("a", Frame(2), 0d), Is.Not.Null);
            for (int attempt = 0; attempt < 30; attempt++)
            {
                Assert.That(router.Accept("a", Frame(2), 0d), Is.Null);
                Assert.That(router.Accept("a", Frame(1), 0d), Is.Null);
            }
            Assert.That(router.Accept("a", Frame(0, 2), 0d), Is.Not.Null);
            Assert.That(router.Accept("a", Frame(int.MaxValue, 1), 0d), Is.Null);
            for (int sequence = 1; sequence <= 8; sequence++)
                Assert.That(router.Accept("a", Frame(sequence, 2), 0d), Is.Not.Null);
            Assert.That(router.Accept("a", Frame(9, 2), 0d), Is.Null);
        }

        [Test]
        public void RouterStopKeepsWatermarkAndRateLimitWhileReconnectAndClearResetLifetimes()
        {
            var router = new CampVoiceRouter();
            Assert.That(router.Accept("a", Frame(8, 5), 0d), Is.Not.Null);
            Assert.That(router.Accept("b", Frame(8, 5), 0d), Is.Not.Null);
            router.StopActor("a");
            Assert.That(router.Accept("a", Frame(9, 5), 0d), Is.Null);
            Assert.That(router.Accept("a", Frame(0, 6), 0d), Is.Not.Null);
            for (int sequence = 1; sequence <= 8; sequence++)
                Assert.That(router.Accept("a", Frame(sequence, 6), 0d), Is.Not.Null);
            router.StopActor("a");
            Assert.That(router.Accept("a", Frame(0, 7), 0d), Is.Null);
            router.ResetActor("a");
            Assert.That(router.Accept("a", Frame(0, 1), 0d), Is.Not.Null);
            Assert.That(router.Accept("b", Frame(0, 1), 0d), Is.Null);
            router.Clear();
            Assert.That(router.TrackedActors, Is.Zero);
            Assert.That(router.Accept("b", Frame(0, 1), 0d), Is.Not.Null);
        }

        [Test]
        public void RouterInvalidOrBackwardTimeCannotAdmitPacketsOrAdvanceTheirSequence()
        {
            var router = new CampVoiceRouter();
            Assert.That(router.Accept("a", Frame(), 1d), Is.Not.Null);
            foreach (double time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0.9d })
                Assert.That(router.Accept("a", Frame(1), time), Is.Null);
            Assert.That(router.Accept("a", Frame(1), 1d), Is.Not.Null);
        }

        [TestCase(0f, 1f)]
        [TestCase(3f, 1f)]
        [TestCase(11.5f, 0.5f)]
        [TestCase(20f, 0f)]
        [TestCase(21f, 0f)]
        public void ProximityHasThreeMeterFullVolumeAndTwentyMeterCutoff(float distance, float gain)
        {
            Vector3 listener = Vector3.right * distance;
            Assert.That(CampVoiceProximity.Gain(Vector3.zero, listener), Is.EqualTo(gain).Within(0.00001f));
            Assert.That(CampVoiceProximity.CanHear(Vector3.zero, listener), Is.EqualTo(gain > 0f));
            Assert.That(CampVoiceProximity.Gain(listener, Vector3.zero), Is.EqualTo(gain).Within(0.00001f));
        }

        [Test]
        public void ProximityUsesFullThreeDimensionalDistanceAndRejectsNonFiniteGeometry()
        {
            Assert.That(CampVoiceProximity.Gain(Vector3.zero, new Vector3(0f, 6.5f, 0f), 10f),
                Is.EqualTo(0.5f).Within(0.00001f));
            Assert.That(CampVoiceProximity.Gain(Vector3.zero, Vector3.right, 2f), Is.EqualTo(1f));
            Assert.That(CampVoiceProximity.Gain(Vector3.zero, Vector3.right * 2f, 2f), Is.Zero);
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.That(CampVoiceProximity.CanHear(new Vector3(value, 0f, 0f), Vector3.zero), Is.False);
                Assert.That(CampVoiceProximity.CanHear(Vector3.zero, new Vector3(0f, value, 0f)), Is.False);
                Assert.That(CampVoiceProximity.Gain(Vector3.zero, Vector3.zero, value), Is.Zero);
            }
            Assert.That(CampVoiceProximity.Gain(Vector3.zero, Vector3.zero, 0f), Is.Zero);
            Assert.That(CampVoiceProximity.Gain(Vector3.zero, Vector3.zero, -1f), Is.Zero);
            Assert.That(CampVoiceProximity.Gain(Vector3.one * float.MaxValue, -Vector3.one * float.MaxValue), Is.Zero);
        }

        [Test]
        public void JitterPrefillsSixtyMillisecondsThenEmitsRealDecodablePacketsEveryTwentyMilliseconds()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(0), 0d), Is.True);
            Assert.That(jitter.Push(Frame(1), 0.020d), Is.True);
            Assert.That(jitter.Push(Frame(2), 0.040d), Is.True);
            Assert.That(jitter.Tick(0.059d, out CampVoiceFrame early), Is.EqualTo(CampVoiceJitterResult.Waiting));
            Assert.That(early, Is.Null);
            AssertPacket(jitter, 0.060d, 0);
            Assert.That(jitter.Tick(0.060d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            Assert.That(jitter.Tick(0.079d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            CampVoiceFrame frame = AssertPacket(jitter, 0.080d, 1);
            using (var decoder = new CampVoiceDecoder())
            {
                var decoded = new float[CampVoiceCodec.FrameSamples];
                Assert.That(decoder.TryDecodePacket(frame.Data, decoded, out int count), Is.True);
                Assert.That(count, Is.EqualTo(CampVoiceCodec.FrameSamples));
                Assert.That(Array.Exists(decoded, sample => Math.Abs(sample) > 0.001f), Is.True);
            }
            AssertPacket(jitter, 0.100d, 2);
            Assert.That(jitter.Count, Is.Zero);
        }

        [Test]
        public void JitterOrdersEarlyAndLaterArrivalsAndRejectsDuplicatesAndAlreadyPlayedPackets()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(10), 0d), Is.True);
            Assert.That(jitter.Push(Frame(8), 0.010d), Is.True);
            Assert.That(jitter.Push(Frame(9), 0.020d), Is.True);
            Assert.That(jitter.Push(Frame(9), 0.030d), Is.False);
            AssertPacket(jitter, 0.060d, 8);
            Assert.That(jitter.Push(Frame(8), 0.061d), Is.False);
            Assert.That(jitter.Push(Frame(12), 0.062d), Is.True);
            Assert.That(jitter.Push(Frame(11), 0.063d), Is.True);
            AssertPacket(jitter, 0.080d, 9);
            AssertPacket(jitter, 0.100d, 10);
            AssertPacket(jitter, 0.120d, 11);
            AssertPacket(jitter, 0.140d, 12);
        }

        [Test]
        public void MissingSequenceRequestsExactlyOnePlcFrameAndLateArrivalCannotReplaceIt()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(0), 0d), Is.True);
            Assert.That(jitter.Push(Frame(2), 0.020d), Is.True);
            using (var decoder = new CampVoiceDecoder())
            {
                var pcm = new float[CampVoiceCodec.FrameSamples];
                CampVoiceFrame initial = AssertPacket(jitter, 0.060d, 0);
                Assert.That(decoder.TryDecodePacket(initial.Data, pcm, out _), Is.True);
                Assert.That(jitter.Tick(0.080d, out CampVoiceFrame missing), Is.EqualTo(CampVoiceJitterResult.Concealment));
                Assert.That(missing, Is.Null);
                Assert.That(decoder.TryConcealLostFrame(pcm, out int concealed), Is.True);
                Assert.That(concealed, Is.EqualTo(CampVoiceCodec.FrameSamples));
                Assert.That(jitter.Tick(0.080d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
                Assert.That(jitter.Push(Frame(1), 0.081d), Is.False);
                AssertPacket(jitter, 0.100d, 2);
            }
        }

        [Test]
        public void JitterKeepsOnlyNewestTenPacketsAndRejectsOlderOverflowArrivals()
        {
            var jitter = new CampVoiceJitter();
            for (int sequence = 0; sequence <= 10; sequence++)
            {
                Assert.That(jitter.Push(Frame(sequence), 0d), Is.True);
                Assert.That(jitter.Count, Is.LessThanOrEqualTo(10));
            }
            Assert.That(jitter.Count, Is.EqualTo(10));
            Assert.That(jitter.Push(Frame(0), 0d), Is.False);
            for (int sequence = 1; sequence <= 10; sequence++)
                AssertPacket(jitter, 0.060d + (sequence - 1) * 0.020d, sequence);
            Assert.That(jitter.Count, Is.Zero);
        }

        [Test]
        public void JitterLargeSequenceGapDropsAncientQueueInsteadOfProducingUnboundedPlc()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(0), 0d), Is.True);
            Assert.That(jitter.Push(Frame(1000000), 0.010d), Is.True);
            Assert.That(jitter.Count, Is.EqualTo(1));
            Assert.That(jitter.Push(Frame(1), 0.020d), Is.False);
            AssertPacket(jitter, 0.060d, 1000000);
        }

        [Test]
        public void NewStreamDiscardsQueuedOldAudioAndNeverAcceptsAnOlderStreamAgain()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(8, 1), 0d), Is.True);
            Assert.That(jitter.Push(Frame(9, 1), 0.010d), Is.True);
            Assert.That(jitter.Push(Frame(0, 2), 0.020d), Is.True);
            Assert.That(jitter.CurrentStream, Is.EqualTo(2));
            Assert.That(jitter.Count, Is.EqualTo(1));
            Assert.That(jitter.Push(Frame(int.MaxValue, 1), 0.030d), Is.False);
            Assert.That(jitter.Tick(0.060d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            Assert.That(AssertPacket(jitter, 0.080d, 0).Stream, Is.EqualTo(2));
        }

        [Test]
        public void HalfSecondSilenceClearsAudioAndResumesSameStreamOnlyAboveItsSeenHistory()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(8, 4), 0d), Is.True);
            Assert.That(jitter.Push(Frame(10, 4), 0.020d), Is.True);
            AssertPacket(jitter, 0.060d, 8);
            Assert.That(jitter.Tick(0.080d, out _), Is.EqualTo(CampVoiceJitterResult.Concealment));
            Assert.That(jitter.Tick(0.520d, out _), Is.EqualTo(CampVoiceJitterResult.Stopped));
            Assert.That(jitter.IsActive, Is.False);
            Assert.That(jitter.Count, Is.Zero);
            Assert.That(jitter.Tick(0.530d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            Assert.That(jitter.Push(Frame(9, 4), 0.540d), Is.False);
            Assert.That(jitter.Push(Frame(10, 4), 0.540d), Is.False);
            Assert.That(jitter.Push(Frame(int.MaxValue, 3), 0.550d), Is.False);
            Assert.That(jitter.Push(Frame(11, 4), 0.560d), Is.True);
            Assert.That(jitter.Push(Frame(10, 4), 0.570d), Is.False);
            Assert.That(jitter.Tick(0.619d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            Assert.That(AssertPacket(jitter, 0.620d, 11).Stream, Is.EqualTo(4));
        }

        [Test]
        public void TimeoutResumeAlsoRejectsSequencesAlreadyConsumedByLossConcealment()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(0), 0d), Is.True);
            AssertPacket(jitter, 0.060d, 0);
            Assert.That(jitter.Tick(0.080d, out _), Is.EqualTo(CampVoiceJitterResult.Concealment));
            Assert.That(jitter.Tick(0.100d, out _), Is.EqualTo(CampVoiceJitterResult.Concealment));
            Assert.That(jitter.Tick(0.500d, out _), Is.EqualTo(CampVoiceJitterResult.Stopped));
            Assert.That(jitter.Push(Frame(1), 0.510d), Is.False);
            Assert.That(jitter.Push(Frame(2), 0.520d), Is.False);
            Assert.That(jitter.Push(Frame(3), 0.530d), Is.True);
            AssertPacket(jitter, 0.590d, 3);
        }

        [Test]
        public void InvalidOrReplayPacketsCannotKeepInterruptedAudioAlive()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(0), 0d), Is.True);
            Assert.That(jitter.Push(Frame(0), 0.400d), Is.False);
            Assert.That(jitter.Push(WithData(new byte[] { 0x03 }), 0.490d), Is.False);
            Assert.That(jitter.Tick(0.500d, out _), Is.EqualTo(CampVoiceJitterResult.Stopped));
            Assert.That(jitter.Count, Is.Zero);
            // Expiration is also enforced when the next event is arrival, not Tick.
            jitter.Clear();
            Assert.That(jitter.Push(Frame(0), 1d), Is.True);
            Assert.That(jitter.Push(Frame(1), 1.500d), Is.True);
            // The stop notification survives immediate fresh arrival so the
            // consumer resets old decoded PCM before the resumed stream plays.
            Assert.That(jitter.Tick(1.500d, out _), Is.EqualTo(CampVoiceJitterResult.Stopped));
            Assert.That(jitter.IsActive, Is.True);
            Assert.That(jitter.Count, Is.EqualTo(1));
            Assert.That(jitter.Tick(1.559d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            AssertPacket(jitter, 1.560d, 1);
        }

        [Test]
        public void JitterStopPreservesWatermarkAndClearPermitsANewConnectionLifetime()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(9, 8), 1d), Is.True);
            jitter.Stop();
            Assert.That(jitter.Count, Is.Zero);
            Assert.That(jitter.IsActive, Is.False);
            Assert.That(jitter.Tick(1d, out _), Is.EqualTo(CampVoiceJitterResult.Stopped));
            Assert.That(jitter.Tick(1d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            Assert.That(jitter.Push(Frame(10, 8), 1d), Is.False);
            Assert.That(jitter.Push(Frame(0, 9), 1d), Is.True);
            jitter.Clear();
            Assert.That(jitter.CurrentStream, Is.Zero);
            Assert.That(jitter.Tick(0d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            Assert.That(jitter.Push(Frame(0, 1), 0d), Is.True);
            AssertPacket(jitter, 0.060d, 0);
        }

        [Test]
        public void JitterRejectsInvalidPacketsAndClockValuesWithoutReplacingQueuedAudio()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(0), 1d), Is.True);
            foreach (CampVoiceFrame bad in new[] { null, WithData(null), WithData(Array.Empty<byte>()),
                WithData(new byte[401]), WithData(new byte[] { 0x03 }), Frame(-1), Frame(0, 0) })
                Assert.That(jitter.Push(bad, 1d), Is.False);
            foreach (double badTime in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0.9d })
            {
                Assert.That(jitter.Push(Frame(1), badTime), Is.False);
                Assert.That(jitter.Tick(badTime, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            }
            Assert.That(jitter.Count, Is.EqualTo(1));
            AssertPacket(jitter, 1.060d, 0);
        }

        [Test]
        public void ShortFrameDelayCanCatchUpDueFramesButCannotInventExtraAudioAtSameTimestamp()
        {
            var jitter = new CampVoiceJitter();
            for (int sequence = 0; sequence < 4; sequence++)
                Assert.That(jitter.Push(Frame(sequence), 0d), Is.True);
            AssertPacket(jitter, 0.060d, 0);
            AssertPacket(jitter, 0.100d, 1);
            AssertPacket(jitter, 0.100d, 2);
            Assert.That(jitter.Tick(0.100d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            AssertPacket(jitter, 0.120d, 3);
        }

        [Test]
        public void LongConsumerStallSkipsOldDeadlinesAndHasAtMostTenDueFrames()
        {
            var jitter = new CampVoiceJitter();
            for (int sequence = 0; sequence < 10; sequence++)
                Assert.That(jitter.Push(Frame(sequence), 0d), Is.True);
            AssertPacket(jitter, 0.060d, 0);
            for (int frame = 0; frame < 10; frame++)
            {
                Assert.That(jitter.Tick(0.460d, out CampVoiceFrame stale), Is.EqualTo(CampVoiceJitterResult.Concealment));
                Assert.That(stale, Is.Null);
            }
            Assert.That(jitter.Tick(0.460d, out _), Is.EqualTo(CampVoiceJitterResult.Waiting));
            Assert.That(jitter.Count, Is.Zero);
        }

        [Test]
        public void MaximumSequenceDoesNotWrapAndANewStreamCanRestartAtZero()
        {
            var jitter = new CampVoiceJitter();
            Assert.That(jitter.Push(Frame(int.MaxValue), 0d), Is.True);
            AssertPacket(jitter, 0.060d, int.MaxValue);
            Assert.That(jitter.Tick(0.080d, out _), Is.EqualTo(CampVoiceJitterResult.Stopped));
            Assert.That(jitter.Push(Frame(0), 0.080d), Is.False);
            Assert.That(jitter.Push(Frame(0, 2), 0.080d), Is.True);
            AssertPacket(jitter, 0.140d, 0);
        }

        [Test]
        public void IndependentRemoteBuffersOwnPacketsAndDoNotShareStopOrSequenceState()
        {
            var a = new CampVoiceJitter();
            var b = new CampVoiceJitter();
            CampVoiceFrame incoming = Frame(8, 1, "a");
            Assert.That(a.Push(incoming, 0d), Is.True);
            Assert.That(b.Push(Frame(0, 1, "b"), 0d), Is.True);
            incoming.Sequence = 99;
            incoming.Speaker = "tampered";
            incoming.Data[0] ^= 0xff;
            CampVoiceFrame owned = AssertPacket(a, 0.060d, 8);
            Assert.That(owned.Speaker, Is.EqualTo("a"));
            Assert.That(owned.Data, Is.EqualTo(_packet));
            a.Stop();
            Assert.That(a.Tick(0.060d, out _), Is.EqualTo(CampVoiceJitterResult.Stopped));
            Assert.That(AssertPacket(b, 0.060d, 0).Speaker, Is.EqualTo("b"));
        }

        private CampVoiceFrame Frame(int sequence = 0, int stream = 1, string speaker = "client-claim") =>
            new CampVoiceFrame
            {
                Sequence = sequence,
                Stream = stream,
                Speaker = speaker,
                Data = (byte[])_packet.Clone()
            };

        private CampVoiceFrame WithData(byte[] data)
        {
            CampVoiceFrame frame = Frame();
            frame.Data = data;
            return frame;
        }

        private static CampVoiceFrame AssertPacket(CampVoiceJitter jitter, double now, int sequence)
        {
            Assert.That(jitter.Tick(now, out CampVoiceFrame frame), Is.EqualTo(CampVoiceJitterResult.Packet));
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.Sequence, Is.EqualTo(sequence));
            return frame;
        }
    }
}
