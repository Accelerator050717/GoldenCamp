using NUnit.Framework;

namespace GoldCamp.Demo.Tests
{
    public sealed class CampVoicePcmPlayerTests
    {
        [TestCase(48000, 2)]
        [TestCase(44100, 2)]
        [TestCase(16000, 1)]
        public void MixerClockConsumesOneSecondOfMonoAndWritesEveryOutputChannel(int rate, int channels)
        {
            var ring = new CampPcmRing(3200); var player = new CampVoicePcmPlayer();
            var input = new float[320]; for (int i = 0; i < input.Length; i++) input[i] = .2f;
            var output = new float[rate / 50 * channels];
            int consumedTotal = 0;
            ring.Write(input); // A live jitter prebuffer, then one frame per 20 ms.
            for (int frame = 0; frame < 50; frame++)
            {
                Assert.That(ring.Write(input), Is.EqualTo(320));
                player.Render(ring, output, channels, rate, .5f, out int consumed, out _);
                consumedTotal += consumed;
                foreach (float value in output) Assert.That(value, Is.EqualTo(.1f).Within(.000001f));
            }
            Assert.That(consumedTotal, Is.InRange(16000, 16003));
        }

        [Test]
        public void StreamReplacementDropsOldHistoryAndUnderrunOutputsSilence()
        {
            var player = new CampVoicePcmPlayer(); var first = new CampPcmRing(3200);
            first.Write(new float[] { .8f, .8f, .8f });
            var output = new float[6]; player.Render(first, output, 2, 48000, 1, out _, out _);
            var second = new CampPcmRing(3200);
            player.Render(second, output, 2, 48000, 1, out int read, out _);
            Assert.That(read, Is.Zero); Assert.That(output, Is.All.Zero);
            second.Write(new float[] { .3f, .3f, .3f });
            player.Render(second, new float[30], 2, 48000, 0, out _, out _);
            player.Render(second, output, 2, 48000, 1, out _, out _);
            Assert.That(output, Is.All.Zero);
        }

        [Test]
        public void GainZeroAndInvalidOutputConfigurationCannotLeakSamples()
        {
            var ring = new CampPcmRing(32); ring.Write(new float[] { 1, 1, 1, 1 });
            var player = new CampVoicePcmPlayer(); var output = new float[4];
            player.Render(ring, output, 2, 48000, 0, out _, out _); Assert.That(output, Is.All.Zero);
            player.Render(ring, output, 0, 48000, 1, out _, out _); Assert.That(output, Is.All.Zero);
        }
    }
}
