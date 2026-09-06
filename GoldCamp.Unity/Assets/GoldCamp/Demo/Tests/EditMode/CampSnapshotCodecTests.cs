using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    public sealed class CampSnapshotCodecTests
    {
        [Test]
        public void ActualFullCampRoundTripsAndEveryWireEnvelopeFitsTransportBudget()
        {
            var authority = CampSnapshotCapacityTests.CreateFullCamp();
            string[] online = authority.State.Players.Select(player => player.Id).ToArray();
            foreach (string actor in online)
            {
                string raw = JsonUtility.ToJson(CampProjection.ForPlayer(authority, actor, online));
                var parts = CampSnapshotCodec.Pack(raw);
                Assert.That(parts.Count, Is.InRange(1, CampSnapshotCodec.MaximumParts));
                var assembler = new CampSnapshotAssembler();
                string restored = null;
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    string wire = JsonUtility.ToJson(new CampPacket { Type = "view-part", Payload = JsonUtility.ToJson(parts[i]) });
                    Assert.That(Encoding.UTF8.GetByteCount(wire), Is.LessThanOrEqualTo(CampTransport.MaximumPayloadBytes));
                    bool complete = assembler.TryAccept(parts[i], 10, out string decoded);
                    Assert.That(complete, Is.EqualTo(i == 0));
                    if (complete) restored = decoded;
                }
                Assert.That(restored, Is.EqualTo(raw));
                var view = JsonUtility.FromJson<CampView>(restored);
                Assert.That(view.Cells.Count, Is.EqualTo(224));
                Assert.That(view.Ladders.Count, Is.EqualTo(224));
                Assert.That(view.Players.Count, Is.EqualTo(4));
                Assert.That(view.Self.Id, Is.EqualTo(actor));
            }
        }

        [Test]
        public void MultiplePartsCanArriveInReverseWithoutPublishingPartialJson()
        {
            string raw = RandomJson(160000);
            var parts = CampSnapshotCodec.Pack(raw);
            Assert.That(parts.Count, Is.GreaterThan(2));
            var assembler = new CampSnapshotAssembler();
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                bool complete = assembler.TryAccept(parts[i], 1, out string result);
                Assert.That(complete, Is.EqualTo(i == 0));
                Assert.That(result, i == 0 ? Is.EqualTo(raw) : Is.Null);
            }
            Assert.That(assembler.TryAccept(parts[0], 1.1, out _), Is.False, "A completed snapshot must not be replayed.");
            assembler.Clear();
            string restored = AcceptAll(assembler, parts, 2);
            Assert.That(restored, Is.EqualTo(raw), "A new connection lifetime may start cleanly.");
        }

        [Test]
        public void DuplicatePartCannotCompleteAndConflictingMetadataCannotMixSnapshots()
        {
            var parts = CampSnapshotCodec.Pack(RandomJson(100000));
            var assembler = new CampSnapshotAssembler();
            Assert.That(assembler.TryAccept(parts[0], 0, out _), Is.False);
            Assert.That(assembler.TryAccept(parts[0], .1, out _), Is.False);
            string restored = null;
            for (int i = 1; i < parts.Count; i++) if (assembler.TryAccept(parts[i], .2, out string value)) restored = value;
            Assert.That(restored, Is.Not.Null);
            assembler.Clear();
            Assert.That(assembler.TryAccept(parts[0], 1, out _), Is.False);
            var conflict = Copy(parts[1]); conflict.RawBytes++;
            Assert.That(assembler.TryAccept(conflict, 1.1, out _), Is.False);
            Assert.That(assembler.TryAccept(parts[parts.Count - 1], 1.2, out _), Is.False);
            assembler.Clear();
            Assert.That(assembler.TryAccept(parts[0], 2, out _), Is.False);
            conflict = Copy(parts[0]); conflict.Count++;
            Assert.That(assembler.TryAccept(conflict, 2.1, out _), Is.False);
        }

        [Test]
        public void ConflictingDuplicateBytesRetireThePartialSnapshot()
        {
            var parts = CampSnapshotCodec.Pack(RandomJson(100000));
            var assembler = new CampSnapshotAssembler();
            Assert.That(assembler.TryAccept(parts[0], 0, out _), Is.False);
            var conflict = Copy(parts[0]);
            byte[] bytes = Convert.FromBase64String(conflict.Data); bytes[20] ^= 1;
            conflict.Data = Convert.ToBase64String(bytes);
            Assert.That(assembler.TryAccept(conflict, .1, out _), Is.False);
            for (int i = 1; i < parts.Count; i++) Assert.That(assembler.TryAccept(parts[i], .2, out _), Is.False);
        }

        [Test]
        public void IncompleteSnapshotExpiresWithoutDuplicatesExtendingItsDeadline()
        {
            var old = CampSnapshotCodec.Pack(RandomJson(100000));
            var next = CampSnapshotCodec.Pack("{\"fresh\":true}");
            var assembler = new CampSnapshotAssembler();
            Assert.That(assembler.TryAccept(old[0], 10, out _), Is.False);
            Assert.That(assembler.TryAccept(old[0], 14.9, out _), Is.False);
            Assert.That(assembler.TryAccept(next[0], 14.99, out _), Is.False, "At most one incomplete snapshot is active.");
            Assert.That(assembler.TryAccept(next[0], 15, out string restored), Is.True);
            Assert.That(restored, Is.EqualTo("{\"fresh\":true}"));
            Assert.That(assembler.TryAccept(old[0], 14, out _), Is.False, "Clock reversal is invalid.");
        }

        [Test]
        public void InvalidLimitsIndicesIdsBase64AndClocksNeverPublishJson()
        {
            var valid = CampSnapshotCodec.Pack("{}")[0];
            var invalid = new[] { Copy(valid), Copy(valid), Copy(valid), Copy(valid), Copy(valid), Copy(valid), Copy(valid), Copy(valid) };
            invalid[0].Id = "not-a-guid";
            invalid[1].Count = CampSnapshotCodec.MaximumParts + 1;
            invalid[2].Index = -1;
            invalid[3].Index = invalid[3].Count;
            invalid[4].RawBytes = CampSnapshotCodec.MaximumRawBytes + 1;
            invalid[5].Data = "invalid base64!";
            invalid[6].Data = new string('A', 32772);
            invalid[7].Data += "\n";
            foreach (var part in invalid)
            {
                Assert.That(new CampSnapshotAssembler().TryAccept(part, 0, out string raw), Is.False);
                Assert.That(raw, Is.Null);
            }
            foreach (double clock in new[] { double.NaN, double.PositiveInfinity, -1d })
                Assert.That(new CampSnapshotAssembler().TryAccept(valid, clock, out _), Is.False);
            Assert.That(new CampSnapshotAssembler().TryAccept(null, 0, out _), Is.False);
        }

        [Test]
        public void BadGzipTruncatedTrailerWrongLengthAndInvalidUtf8AreRejected()
        {
            var valid = CampSnapshotCodec.Pack("{\"text\":\"淘金营地\"}")[0];
            byte[] packed = Convert.FromBase64String(valid.Data);
            var badHeader = Copy(valid); byte[] header = (byte[])packed.Clone(); header[0] = 0; badHeader.Data = Convert.ToBase64String(header);
            var badCrc = Copy(valid); byte[] crc = (byte[])packed.Clone(); crc[crc.Length - 8] ^= 1; badCrc.Data = Convert.ToBase64String(crc);
            var truncated = Copy(valid); truncated.Data = Convert.ToBase64String(packed, 0, packed.Length - 8);
            var wrongLength = Copy(valid); wrongLength.RawBytes++;
            var invalidText = Copy(valid); invalidText.RawBytes = 1; invalidText.Data = Convert.ToBase64String(Gzip(new byte[] { 0xff }));
            foreach (var part in new[] { badHeader, badCrc, truncated, wrongLength, invalidText })
            {
                Assert.That(new CampSnapshotAssembler().TryAccept(part, 0, out string raw), Is.False);
                Assert.That(raw, Is.Null);
            }
        }

        [Test]
        public void PackingAndInflationHonorIndependentByteCaps()
        {
            Assert.Throws<ArgumentNullException>(() => CampSnapshotCodec.Pack(null));
            Assert.Throws<InvalidDataException>(() => CampSnapshotCodec.Pack(new string('x', CampSnapshotCodec.MaximumRawBytes + 1)));
            Assert.Throws<InvalidDataException>(() => CampSnapshotCodec.Pack(RandomJson(1600000)), "Incompressible data hits the compressed cap while raw data is still allowed.");
            var underDeclared = new CampSnapshotPart { Id = Guid.NewGuid().ToString("N"), Index = 0, Count = 1,
                RawBytes = 32, Data = Convert.ToBase64String(Gzip(new byte[4096])) };
            Assert.That(new CampSnapshotAssembler().TryAccept(underDeclared, 0, out _), Is.False);
            // Match the advertised trailer length to force the streamed inflate guard,
            // not just an early ISIZE mismatch. CRC still covers the true 4096 bytes.
            byte[] packed = Convert.FromBase64String(underDeclared.Data);
            packed[packed.Length - 4] = 32; packed[packed.Length - 3] = packed[packed.Length - 2] = packed[packed.Length - 1] = 0;
            underDeclared.Data = Convert.ToBase64String(packed);
            Assert.That(new CampSnapshotAssembler().TryAccept(underDeclared, 0, out _), Is.False);
        }

        private static string AcceptAll(CampSnapshotAssembler assembler, System.Collections.Generic.List<CampSnapshotPart> parts, double now)
        {
            string result = null;
            foreach (var part in parts) if (assembler.TryAccept(part, now, out string value)) result = value;
            return result;
        }
        private static CampSnapshotPart Copy(CampSnapshotPart part) => new CampSnapshotPart
        { Id = part.Id, Index = part.Index, Count = part.Count, RawBytes = part.RawBytes, Data = part.Data };
        private static string RandomJson(int bytes)
        { var data = new byte[bytes]; new System.Random(14573).NextBytes(data); return "{\"noise\":\"" + Convert.ToBase64String(data) + "\"}"; }
        private static byte[] Gzip(byte[] raw)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) gzip.Write(raw, 0, raw.Length);
                return output.ToArray();
            }
        }
    }
}
