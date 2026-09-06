using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GoldCamp.Demo
{
    [Serializable]
    public sealed class CampSnapshotPart
    {
        public string Id;
        public int Index, Count, RawBytes;
        public string Data;
    }

    public static class CampSnapshotCodec
    {
        public const int MaximumRawBytes = 8 * 1024 * 1024;
        public const int MaximumCompressedBytes = 1024 * 1024;
        public const int ChunkBytes = 24 * 1024;
        public const int MaximumParts = 48;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public static List<CampSnapshotPart> Pack(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            int rawCount = Utf8.GetByteCount(json);
            if (rawCount > MaximumRawBytes) throw new InvalidDataException("Snapshot exceeds the raw byte limit.");
            byte[] raw = Utf8.GetBytes(json);
            byte[] packed;
            using (var output = new LimitedOutput())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw, 0, raw.Length);
                packed = output.ToArray();
            }
            int count = (packed.Length + ChunkBytes - 1) / ChunkBytes;
            if (count < 1 || count > MaximumParts) throw new InvalidDataException("Snapshot exceeds the part limit.");
            string id = Guid.NewGuid().ToString("N");
            var parts = new List<CampSnapshotPart>(count);
            for (int i = 0; i < count; i++)
            {
                int offset = i * ChunkBytes, length = Math.Min(ChunkBytes, packed.Length - offset);
                parts.Add(new CampSnapshotPart { Id = id, Index = i, Count = count, RawBytes = rawCount,
                    Data = Convert.ToBase64String(packed, offset, length) });
            }
            return parts;
        }

        internal static bool TryUnpack(byte[] packed, int expectedBytes, out string json)
        {
            json = null;
            if (packed == null || packed.Length < 18 || packed.Length > MaximumCompressedBytes ||
                expectedBytes < 0 || expectedBytes > MaximumRawBytes || packed[0] != 0x1f || packed[1] != 0x8b ||
                packed[2] != 8 || packed[3] != 0 || ReadUInt32(packed, packed.Length - 4) != (uint)expectedBytes) return false;
            try
            {
                using (var input = new MemoryStream(packed, false))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream(Math.Min(expectedBytes, 8192)))
                {
                    byte[] scratch = new byte[8192];
                    uint crc = uint.MaxValue;
                    int total = 0, read;
                    while ((read = gzip.Read(scratch, 0, scratch.Length)) > 0)
                    {
                        if (read > expectedBytes - total) return false;
                        for (int i = 0; i < read; i++)
                        {
                            crc ^= scratch[i];
                            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
                        }
                        output.Write(scratch, 0, read); total += read;
                    }
                    // Explicit trailer checks reject truncated streams even on runtimes
                    // whose GZipStream reports EOF instead of a missing-trailer error.
                    if (total != expectedBytes || ~crc != ReadUInt32(packed, packed.Length - 8)) return false;
                    json = Utf8.GetString(output.GetBuffer(), 0, total);
                    return true;
                }
            }
            catch (InvalidDataException) { return false; }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static uint ReadUInt32(byte[] data, int at) =>
            (uint)data[at] | (uint)data[at + 1] << 8 | (uint)data[at + 2] << 16 | (uint)data[at + 3] << 24;

        private sealed class LimitedOutput : Stream
        {
            private readonly MemoryStream _buffer = new MemoryStream();
            public byte[] ToArray() => _buffer.ToArray();
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count < 0 || count > MaximumCompressedBytes - _buffer.Length)
                    throw new InvalidDataException("Snapshot exceeds the compressed byte limit.");
                _buffer.Write(buffer, offset, count);
            }
            public override void WriteByte(byte value)
            {
                if (_buffer.Length >= MaximumCompressedBytes) throw new InvalidDataException("Snapshot exceeds the compressed byte limit.");
                _buffer.WriteByte(value);
            }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _buffer.Length;
            public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
        }
    }

    // Main-thread use only. Holds at most one compressed snapshot, never a backlog.
    public sealed class CampSnapshotAssembler
    {
        public const double ExpirySeconds = 5;
        private string _id, _lastCompleted;
        private int _rawBytes, _received, _total;
        private byte[][] _parts;
        private double _started, _lastNow;
        private bool _haveClock;

        public bool TryAccept(CampSnapshotPart part, double monotonicNow, out string json)
        {
            json = null;
            if (double.IsNaN(monotonicNow) || double.IsInfinity(monotonicNow) || monotonicNow < 0 ||
                (_haveClock && monotonicNow < _lastNow)) return false;
            _haveClock = true; _lastNow = monotonicNow;
            if (_parts != null && monotonicNow - _started >= ExpirySeconds) ResetActive();
            if (part == null || !Guid.TryParseExact(part.Id, "N", out _) || part.Id == _lastCompleted ||
                part.Count < 1 || part.Count > CampSnapshotCodec.MaximumParts || part.Index < 0 || part.Index >= part.Count ||
                part.RawBytes < 0 || part.RawBytes > CampSnapshotCodec.MaximumRawBytes || string.IsNullOrEmpty(part.Data) ||
                part.Data.Length > (CampSnapshotCodec.ChunkBytes + 2) / 3 * 4 ||
                (long)(part.Count - 1) * CampSnapshotCodec.ChunkBytes >= CampSnapshotCodec.MaximumCompressedBytes) return false;
            // Another snapshot cannot evict the active one before its five-second expiry.
            if (_parts != null && part.Id != _id) return false;
            if (_parts != null && (part.Count != _parts.Length || part.RawBytes != _rawBytes))
            { ResetActive(); return false; }
            byte[] bytes;
            try { bytes = Convert.FromBase64String(part.Data); }
            catch (FormatException) { return false; }
            if (bytes.Length == 0 || bytes.Length > CampSnapshotCodec.ChunkBytes ||
                (part.Index < part.Count - 1 && bytes.Length != CampSnapshotCodec.ChunkBytes) ||
                Convert.ToBase64String(bytes) != part.Data) return false;
            if (_parts == null)
            { _id = part.Id; _rawBytes = part.RawBytes; _parts = new byte[part.Count][]; _started = monotonicNow; }
            if (_parts[part.Index] != null)
            {
                // Identical repeats are ignored; conflicting content retires the assembly.
                byte[] prior = _parts[part.Index];
                bool same = prior.Length == bytes.Length;
                for (int i = 0; same && i < prior.Length; i++) same = prior[i] == bytes[i];
                if (!same) ResetActive();
                return false;
            }
            if (bytes.Length > CampSnapshotCodec.MaximumCompressedBytes - _total) { ResetActive(); return false; }
            _parts[part.Index] = bytes; _received++; _total += bytes.Length;
            if (_received != _parts.Length) return false;
            var packed = new byte[_total];
            int offset = 0;
            foreach (byte[] chunk in _parts) { Buffer.BlockCopy(chunk, 0, packed, offset, chunk.Length); offset += chunk.Length; }
            int rawBytes = _rawBytes; string finished = _id;
            ResetActive();
            if (!CampSnapshotCodec.TryUnpack(packed, rawBytes, out json)) return false;
            _lastCompleted = finished;
            return true;
        }

        public void Clear()
        { ResetActive(); _lastCompleted = null; _haveClock = false; _lastNow = 0; }
        private void ResetActive()
        { _parts = null; _id = null; _rawBytes = _received = _total = 0; _started = 0; }
    }
}
