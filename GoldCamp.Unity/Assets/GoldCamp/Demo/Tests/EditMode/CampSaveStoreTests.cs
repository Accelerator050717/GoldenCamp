using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GoldCamp.Demo.Tests
{
    public sealed class CampSaveStoreTests
    {
        private string _directory;
        private string _directoryName;
        private CampSaveStore _store;

        [SetUp]
        public void SetUp()
        {
            _directoryName = "GoldCamp-Demo-EditMode-" + Guid.NewGuid().ToString("N");
            _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), _directoryName));
            _store = new CampSaveStore(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            _store?.Dispose();
            _store = null;
            if (string.IsNullOrEmpty(_directory) || !Directory.Exists(_directory)) return;
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), _directoryName));
            string actual = Path.GetFullPath(_directory);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Assert.That(actual, Is.EqualTo(expected).IgnoreCase, "Only this test's generated directory can be removed.");
            Assert.That(actual.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase), Is.True);
            Assert.That(Path.GetFileName(actual), Is.EqualTo(_directoryName));
            Assert.That(_directoryName, Does.StartWith("GoldCamp-Demo-EditMode-"));
            Assert.That((File.GetAttributes(actual) & FileAttributes.ReparsePoint) == 0, Is.True);
            Directory.Delete(actual, true);
        }

        [Test]
        public void SnapshotRoundTripRetainsWorldIdentityAssetsAndDurableReceipts()
        {
            Assert.That(_store.Exists, Is.False);
            var original = Snapshot(1);
            _store.Save(original);
            Assert.That(_store.Exists, Is.True);
            Assert.That(File.Exists(_store.MainPath + ".bak1"), Is.True);
            Assert.That(File.Exists(_store.MainPath + ".bak2"), Is.True);
            string originalBytes = File.ReadAllText(_store.MainPath);

            _store.Dispose();
            _store = new CampSaveStore(_directory);
            var loaded = _store.Load();

            Assert.That(CampCodec.Encode(loaded), Is.EqualTo(CampCodec.Encode(original)));
            Assert.That(File.ReadAllText(_store.MainPath), Is.EqualTo(originalBytes));
            Assert.That(_store.RecoveryNotice, Is.Null.Or.Empty);
        }

        [Test]
        public void CorruptMainAndNewestBackupRecoverOldestBackupWithoutOverwritingEvidence()
        {
            var oldest = Snapshot(1);
            _store.Save(oldest);
            _store.Save(RevisionOf(oldest, 2));
            _store.Save(RevisionOf(oldest, 3));
            File.WriteAllText(_store.MainPath, "test-only corrupt main");
            File.WriteAllText(_store.MainPath + ".bak1", "test-only corrupt newest backup");
            string oldBackupBytes = File.ReadAllText(_store.MainPath + ".bak2");

            var recovered = _store.Load();

            Assert.That(CampCodec.Encode(recovered), Is.EqualTo(CampCodec.Encode(oldest)));
            Assert.That(_store.RecoveryNotice, Is.Not.Null.And.Not.Empty);
            Assert.That(File.ReadAllText(_store.MainPath), Is.EqualTo("test-only corrupt main"));
            Assert.That(File.ReadAllText(_store.MainPath + ".bak1"), Is.EqualTo("test-only corrupt newest backup"));
            Assert.That(File.ReadAllText(_store.MainPath + ".bak2"), Is.EqualTo(oldBackupBytes));
        }

        [Test]
        public void CorruptionOfEveryCommittedCopyFailsWithoutCreatingAnEmptyWorld()
        {
            _store.Save(Snapshot(1));
            foreach (string path in SnapshotPaths()) File.WriteAllText(path, "test-only invalid snapshot");

            Assert.That(() => _store.Load(), Throws.TypeOf<InvalidDataException>());

            Assert.That(_store.Exists, Is.True);
            foreach (string path in SnapshotPaths())
                Assert.That(File.ReadAllText(path), Is.EqualTo("test-only invalid snapshot"));
        }

        [TestCase("envelope-format")]
        [TestCase("envelope-schema")]
        [TestCase("payload-schema")]
        public void FutureVersionIsRejectedInsteadOfSilentlyRollingBackToOlderBackup(string field)
        {
            _store.Save(Snapshot(1));
            var envelope = JsonUtility.FromJson<Envelope>(File.ReadAllText(_store.MainPath));
            if (field == "envelope-format") envelope.FormatVersion = 999;
            else if (field == "envelope-schema") envelope.SchemaVersion = 999;
            else
            {
                var payload = JsonUtility.FromJson<CampState>(envelope.Payload);
                payload.SchemaVersion = 999;
                envelope.Payload = JsonUtility.ToJson(payload);
                envelope.Sha256 = CampAuthority.Hash(envelope.Payload);
            }
            string unsupported = JsonUtility.ToJson(envelope);
            File.WriteAllText(_store.MainPath, unsupported);
            string firstBackup = File.ReadAllText(_store.MainPath + ".bak1");
            string secondBackup = File.ReadAllText(_store.MainPath + ".bak2");

            Assert.That(() => _store.Load(), Throws.TypeOf<NotSupportedException>());

            Assert.That(File.ReadAllText(_store.MainPath), Is.EqualTo(unsupported));
            Assert.That(File.ReadAllText(_store.MainPath + ".bak1"), Is.EqualTo(firstBackup));
            Assert.That(File.ReadAllText(_store.MainPath + ".bak2"), Is.EqualTo(secondBackup));
        }

        [TestCase("after-temp-flush")]
        [TestCase("before-main-replace")]
        public void InterruptedWriteKeepsPreviousCommittedSnapshotAndAllowsExplicitRetry(string stage)
        {
            var committed = Snapshot(1);
            var pending = RevisionOf(committed, 2);
            _store.Save(committed);
            string originalMain = File.ReadAllText(_store.MainPath);
            int injections = 0;
            _store.BeforeStage = current =>
            {
                if (current != stage) return;
                injections++;
                throw new IOException("Injected test-only interruption at " + stage);
            };

            Assert.That(() => _store.Save(pending), Throws.TypeOf<IOException>());

            Assert.That(injections, Is.EqualTo(1), "The intended write boundary must actually be reached.");
            Assert.That(File.ReadAllText(_store.MainPath), Is.EqualTo(originalMain));
            Assert.That(CampCodec.Encode(_store.Load()), Is.EqualTo(CampCodec.Encode(committed)));
            _store.BeforeStage = null;
            _store.Save(pending);
            Assert.That(CampCodec.Encode(_store.Load()), Is.EqualTo(CampCodec.Encode(pending)));
        }

        [Test]
        public void RepeatedPrecommitFailuresRetainPreviousDifferentSuccessfulGeneration()
        {
            var first = Snapshot(1);
            var second = RevisionOf(first, 2);
            var third = RevisionOf(first, 3);
            var pending = RevisionOf(first, 4);
            _store.Save(first);
            _store.Save(second);
            _store.Save(third);
            string committedMain = File.ReadAllText(_store.MainPath);
            int injections = 0;
            _store.BeforeStage = stage =>
            {
                if (stage != "before-main-replace") return;
                injections++;
                throw new IOException("Injected repeated precommit failure.");
            };

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                Assert.That(() => _store.Save(pending), Throws.TypeOf<IOException>());
                Assert.That(injections, Is.EqualTo(attempt));
                Assert.That(File.ReadAllText(_store.MainPath), Is.EqualTo(committedMain));
                var backupRevisions = SnapshotPaths().Skip(1).Select(path => ReadSnapshot(path).Revision).ToArray();
                Assert.That(backupRevisions, Does.Contain(2L),
                    "Retries must not replace every older successful generation with revision 3.");
                Assert.That(backupRevisions.All(revision => revision <= 3), Is.True,
                    "Uncommitted revision 4 must not become a recovery candidate.");
            }

            _store.BeforeStage = null;
            // Damage all copies of the latest successful generation. The retained
            // different generation must still recover its original assets intact.
            foreach (string path in SnapshotPaths())
                if (ReadSnapshot(path).Revision == 3) File.WriteAllText(path, "test-only latest-generation corruption");
            var recovered = _store.Load();
            Assert.That(CampCodec.Encode(recovered), Is.EqualTo(CampCodec.Encode(second)));
            Assert.That(_store.RecoveryNotice, Is.Not.Null.And.Not.Empty);
        }

        [TestCase("after-temp-flush")]
        [TestCase("before-main-replace")]
        public void InterruptedFirstWriteIsNotMistakenForACommittedCamp(string failureStage)
        {
            int injections = 0;
            _store.BeforeStage = stage =>
            {
                if (stage != failureStage) return;
                injections++;
                throw new IOException("Injected initial write interruption.");
            };
            Assert.That(() => _store.Save(Snapshot(1)), Throws.TypeOf<IOException>());
            Assert.That(injections, Is.EqualTo(1));
            Assert.That(File.Exists(_store.MainPath), Is.False);
            Assert.That(_store.Exists, Is.False);
            _store.BeforeStage = null;
            Assert.That(() => _store.Load(), Throws.Exception);
            Assert.That(File.Exists(_store.MainPath), Is.False);
        }

        [Test]
        public void DirectoryHasOnlyOneWriterAndCanReopenAfterOwnerDisposes()
        {
            var committed = Snapshot(1);
            _store.Save(committed);
            Assert.That(() =>
            {
                using (var unexpected = new CampSaveStore(_directory)) { }
            }, Throws.TypeOf<IOException>());

            _store.Dispose();
            _store = new CampSaveStore(_directory);

            Assert.That(CampCodec.Encode(_store.Load()), Is.EqualTo(CampCodec.Encode(committed)));
        }

        private string[] SnapshotPaths() =>
            new[] { _store.MainPath, _store.MainPath + ".bak1", _store.MainPath + ".bak2" };

        private static CampState ReadSnapshot(string path) =>
            CampCodec.Decode(JsonUtility.FromJson<Envelope>(File.ReadAllText(path)).Payload);

        private static CampState Snapshot(long revision)
        {
            const string playerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string otherPlayerId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string wagerId = Guid.NewGuid().ToString("N");
            var state = CampAuthority.NewWorld("test-only permanent camp", 13579);
            state.Revision = revision;
            state.SavedUtc = "2026-09-05T00:00:00.0000000Z";
            state.OnlineSeconds = 654.25;
            state.PvpEnabled = false;
            state.Players.Add(new CampPlayer
            {
                Id = playerId, CredentialHash = CampAuthority.Hash("test-only credential"), Name = "测试矿工",
                Balance = 456, Position = new Vector3(1, 2, 3), Yaw = 45,
                ToolLevel = 2, DetectorLevel = 1, Durability = 72.5f, ColorIndex = 3
            });
            state.Players.Add(new CampPlayer
            {
                Id = otherPlayerId, CredentialHash = CampAuthority.Hash("test-only other credential"), Name = "测试对手",
                Balance = 300, Position = new Vector3(2, 2, 3), ColorIndex = 1
            });
            foreach (var place in new[] { ItemPlace.Ground, ItemPlace.Hand, ItemPlace.Pocket, ItemPlace.Mouth,
                         ItemPlace.Cabinet, ItemPlace.Vehicle, ItemPlace.Escrow, ItemPlace.Sold })
            {
                state.Items.Add(new CampItem
                {
                    Id = "ore-" + place, Kind = ItemKind.RawStone, Place = place,
                    OwnerId = place == ItemPlace.Ground ? "" : playerId,
                    ContainerId = place == ItemPlace.Cabinet ? "cabinet-a" : place == ItemPlace.Vehicle ? "cart-a" : place == ItemPlace.Escrow ? "wager:" + wagerId : "",
                    Position = new Vector3(4, 5, 6), Velocity = new Vector3(.1f, .2f, .3f), Version = 7,
                    HiddenValue = 600, CoreKind = "gold-core", RevealStage = 1,
                    InspectedBy = new System.Collections.Generic.List<string> { playerId }
                });
            }
            // Escrow is part of a complete locked transaction, never an orphan
            // location. Roundtrip and fault tests retain both original stones.
            state.Items.Add(new CampItem
            {
                Id = "ore-Escrow-second", Kind = ItemKind.RawStone, Place = ItemPlace.Escrow,
                OwnerId = otherPlayerId, ContainerId = "wager:" + wagerId, Position = new Vector3(5, 5, 6),
                Version = 7, HiddenValue = 600, CoreKind = "gold-core", RevealStage = 1
            });
            state.Wager = new CampWager
            {
                Id = wagerId, PlayerA = playerId, PlayerB = otherPlayerId,
                StoneA = "ore-Escrow", StoneB = "ore-Escrow-second", StoneVersionA = 7, StoneVersionB = 7,
                Version = 1, Phase = WagerPhase.Locked, Stake = 20, ConfirmedA = true, ConfirmedB = true,
                ValueA = 600, ValueB = 600
            };
            state.Receipts.Add(new CampReceipt
            {
                Key = playerId + ":durable-operation", Fingerprint = CampAuthority.Hash("test-only fingerprint"),
                Result = new CampResult { RequestId = "durable-operation", Success = true, Code = "ok", Message = "已保存", Amount = 55, Revision = revision }
            });
            return state;
        }

        private static CampState RevisionOf(CampState source, long revision)
        {
            var copy = CampCodec.Clone(source);
            copy.Revision = revision;
            copy.Name = "test-only revision " + revision;
            copy.Players[0].Balance += revision;
            return copy;
        }

        [Serializable]
        private sealed class Envelope
        {
            public int FormatVersion;
            public int SchemaVersion;
            public string Payload;
            public string Sha256;
        }
    }
}
