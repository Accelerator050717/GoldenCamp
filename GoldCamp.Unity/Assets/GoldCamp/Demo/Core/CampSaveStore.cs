using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace GoldCamp.Demo
{
    /// <summary>
    /// Owns one explicitly selected world directory for its lifetime. A save becomes
    /// committed only when world.camp.json is atomically installed. Temporary files
    /// are never load candidates, and loading a backup never rewrites the main file.
    /// </summary>
    public sealed class CampSaveStore : IDisposable
    {
        private const int CurrentFormatVersion = 1;
        private const int CurrentSchemaVersion = 1;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly object _gate = new object();
        private readonly string _backup1Path;
        private readonly string _backup2Path;
        private FileStream _directoryLock;

        public string MainPath { get; }
        public string RecoveryNotice { get; private set; } = "";

        /// <summary>
        /// Test-only fault injection: "after-temp-flush" runs before backup changes;
        /// "before-main-replace" runs after backup preparation, before commitment.
        /// Exceptions propagate, leaving the previously committed main file intact.
        /// </summary>
        public Action<string> BeforeStage { get; set; }

        public bool Exists
        {
            get
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    // A damaged or interrupted world must not appear to be a new world.
                    return File.Exists(MainPath) || File.Exists(_backup1Path) || File.Exists(_backup2Path);
                }
            }
        }

        public CampSaveStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("必须明确指定营地存档目录。", nameof(directory));

            var fullDirectory = Path.GetFullPath(directory);
            Directory.CreateDirectory(fullDirectory);
            MainPath = Path.Combine(fullDirectory, "world.camp.json");
            _backup1Path = MainPath + ".bak1";
            _backup2Path = MainPath + ".bak2";
            // Keep the handle, not the mere presence of this file, as the lock.
            // The OS releases it after a crash. Never unlink it while others may open it.
            _directoryLock = new FileStream(Path.Combine(fullDirectory, ".camp.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public CampState Load()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var files = InspectFiles();
                var current = SelectCommitted(files);
                RecoveryNotice = ReferenceEquals(current, files.Main) ? "" :
                    "主存档损坏，已从 " + Path.GetFileName(current.Path) +
                    " 读取最近的有效备份；原文件保留，进度回到该次成功保存。";
                return current.State;
            }
        }

        public void Save(CampState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            lock (_gate)
            {
                ThrowIfDisposed();
                if (state.SchemaVersion > CurrentSchemaVersion)
                    throw FutureVersion();

                // Serialize a detached snapshot before touching any saved files.
                var payload = CampCodec.Encode(state);
                var snapshot = DecodeAndValidate(payload);
                var bytes = Utf8.GetBytes(JsonUtility.ToJson(new Envelope
                {
                    FormatVersion = CurrentFormatVersion,
                    SchemaVersion = CurrentSchemaVersion,
                    Payload = payload,
                    Sha256 = Hash(payload)
                }));
                var files = InspectFiles();
                bool firstSave = !files.Main.Exists && !files.Backup1.Exists && !files.Backup2.Exists;
                var current = firstSave ? null : SelectCommitted(files);
                if (current != null && current.State.WorldId != snapshot.WorldId)
                    throw new InvalidDataException("存档目录属于另一个营地；请为新营地选择独立目录。");

                string temporary = WriteTemporary(MainPath, bytes);
                bool committed = false;
                try
                {
                    BeforeStage?.Invoke("after-temp-flush");
                    if (firstSave)
                    {
                        // Installing the main file last is the initial commit point.
                        // If interrupted before that point, orphan backups cannot load.
                        PrepareBackup(_backup2Path, bytes, false);
                        PrepareBackup(_backup1Path, bytes, false);
                    }
                    else if (files.Main.IsValid)
                    {
                        if (files.Backup1.IsValid && SameBytes(files.Backup1.Bytes, current.Bytes))
                        {
                            // A failed attempt already backed up this main. Preserve
                            // the older generation through any further failed retries.
                            if (!files.Backup2.IsValid)
                                PrepareBackup(_backup2Path, current.Bytes, files.Backup2.IsCorrupt);
                        }
                        else
                        {
                            // Preserve the older valid backup before replacing backup 1.
                            var older = files.Backup1.IsValid ? files.Backup1 :
                                files.Backup2.IsValid ? files.Backup2 : current;
                            PrepareBackup(_backup2Path, older.Bytes, files.Backup2.IsCorrupt);
                            PrepareBackup(_backup1Path, current.Bytes, files.Backup1.IsCorrupt);
                        }
                    }
                    else
                    {
                        // Never rotate a corrupt main over a valid recovery copy.
                        if (!files.Backup2.IsValid)
                            PrepareBackup(_backup2Path, current.Bytes, files.Backup2.IsCorrupt);
                        if (!files.Backup1.IsValid)
                            PrepareBackup(_backup1Path, current.Bytes, files.Backup1.IsCorrupt);
                    }

                    BeforeStage?.Invoke("before-main-replace");
                    Install(temporary, MainPath, files.Main.IsCorrupt);
                    committed = true;
                    // No fallible persistence or test hook follows the commit point.
                    RecoveryNotice = files.Main.IsCorrupt
                        ? "已保存恢复后的营地；损坏的旧主档已另存保留。" : "";
                }
                catch
                {
                    // These two files did not exist before this initial attempt.
                    // A handled failure can clean up its own uncommitted copies;
                    // a process crash may leave them, which SelectCommitted refuses.
                    if (firstSave && !committed)
                    {
                        RemoveOwnTemporary(_backup1Path);
                        RemoveOwnTemporary(_backup2Path);
                    }
                    throw;
                }
                finally
                {
                    RemoveOwnTemporary(temporary);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _directoryLock?.Dispose();
                _directoryLock = null;
            }
        }

        private FileSet InspectFiles()
        {
            // Inspect every committed filename before making changes. An unfamiliar
            // future version in any of them must be preserved instead of rotated away.
            var result = new FileSet
            {
                Main = Inspect(MainPath),
                Backup1 = Inspect(_backup1Path),
                Backup2 = Inspect(_backup2Path)
            };
            string worldId = null;
            foreach (var file in new[] { result.Main, result.Backup1, result.Backup2 })
            {
                if (!file.IsValid) continue;
                if (worldId != null && worldId != file.State.WorldId)
                    throw new InvalidDataException("主档与备份的营地 ID 不一致；保留所有文件，拒绝混用。");
                worldId = file.State.WorldId;
            }
            return result;
        }

        private static SavedFile SelectCommitted(FileSet files)
        {
            if (!files.Main.Exists)
            {
                if (files.Backup1.Exists || files.Backup2.Exists)
                    throw new InvalidDataException("主存档缺失，无法确认备份是否来自已提交的首次保存；保留现有文件，不自动创建替代档。");
                throw new FileNotFoundException("该目录没有已保存的营地。");
            }
            if (files.Main.IsValid) return files.Main;
            if (files.Backup1.IsValid) return files.Backup1;
            if (files.Backup2.IsValid) return files.Backup2;
            throw new InvalidDataException("主存档和备份均无效；保留所有文件，不创建或覆盖营地。");
        }

        private static SavedFile Inspect(string path)
        {
            var result = new SavedFile { Path = path, Exists = File.Exists(path) };
            if (!result.Exists) return result;
            // Access and I/O failures propagate: they do not prove a file is corrupt.
            result.Bytes = File.ReadAllBytes(path);
            try
            {
                var envelope = JsonUtility.FromJson<Envelope>(Utf8.GetString(result.Bytes));
                if (envelope == null) throw Corrupt();
                if (envelope.FormatVersion > CurrentFormatVersion || envelope.SchemaVersion > CurrentSchemaVersion)
                    throw FutureVersion();
                if (string.IsNullOrEmpty(envelope.Payload)) throw Corrupt();

                // Check payload version before its checksum: a future save is never
                // permission to load an older backup, even if this file is damaged.
                var header = JsonUtility.FromJson<SchemaHeader>(envelope.Payload);
                if (header != null && header.SchemaVersion > CurrentSchemaVersion)
                    throw FutureVersion();
                if (envelope.FormatVersion != CurrentFormatVersion || envelope.SchemaVersion != CurrentSchemaVersion
                    || header == null || header.SchemaVersion != CurrentSchemaVersion
                    || !string.Equals(envelope.Sha256, Hash(envelope.Payload), StringComparison.OrdinalIgnoreCase))
                    throw Corrupt();
                result.State = DecodeAndValidate(envelope.Payload);
            }
            catch (NotSupportedException) { throw; }
            catch (InvalidDataException) { /* Retain bytes for diagnosis; try another committed file. */ }
            catch (ArgumentException) { /* Malformed JSON or UTF-8 is not a valid snapshot. */ }
            return result;
        }

        private static CampState DecodeAndValidate(string payload)
        {
            try
            {
                var state = CampCodec.Decode(payload);
                CampAuthority.Validate(state);
                return state;
            }
            catch (Exception)
            {
                // Codec/validation exceptions may contain payload details; expose no
                // world contents, player credentials, or parser excerpts to callers.
                throw Corrupt();
            }
        }

        private static string Hash(string payload)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(payload))).Replace("-", "").ToLowerInvariant();
        }

        private static string WriteTemporary(string destination, byte[] bytes)
        {
            string path = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                return path;
            }
            catch
            {
                RemoveOwnTemporary(path);
                throw;
            }
        }

        private static void PrepareBackup(string destination, byte[] bytes, bool preserveCorrupt)
        {
            string temporary = WriteTemporary(destination, bytes);
            try { Install(temporary, destination, preserveCorrupt); }
            finally { RemoveOwnTemporary(temporary); }
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static void Install(string temporary, string destination, bool preserveCorrupt)
        {
            if (File.Exists(destination))
            {
                string preserved = preserveCorrupt ? destination + ".corrupt-" + Guid.NewGuid().ToString("N") : null;
                // Same-directory replacement is atomic. Do not substitute delete/move
                // if the filesystem cannot support it: leave the old main untouched.
                File.Replace(temporary, destination, preserved);
            }
            else File.Move(temporary, destination);
        }

        private static void RemoveOwnTemporary(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* Cleanup must never turn a durable commit into a reported failure. */ }
        }

        private void ThrowIfDisposed()
        {
            if (_directoryLock == null) throw new ObjectDisposedException(nameof(CampSaveStore));
        }

        private static InvalidDataException Corrupt() => new InvalidDataException("存档内容或校验无效；保留现有文件。");
        private static NotSupportedException FutureVersion() => new NotSupportedException("检测到未来版本存档；请使用兼容版本，保留现有文件并拒绝降级回档。");

        [Serializable]
        private sealed class Envelope
        {
            public int FormatVersion;
            public int SchemaVersion;
            public string Payload;
            public string Sha256;
        }

        [Serializable]
        private sealed class SchemaHeader
        {
            public int SchemaVersion;
        }

        private sealed class SavedFile
        {
            public string Path;
            public bool Exists;
            public byte[] Bytes;
            public CampState State;
            public bool IsValid => State != null;
            public bool IsCorrupt => Exists && !IsValid;
        }

        private sealed class FileSet
        {
            public SavedFile Main;
            public SavedFile Backup1;
            public SavedFile Backup2;
        }
    }
}
