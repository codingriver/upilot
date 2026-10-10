using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal static class UPilotTestRunStore
    {
        // Test-only fault boundary: production callers never assign this hook.
        internal static Action<string> BeforeAtomicReplaceForTests;
        internal static Action<string> BeforeActivePointerClearForTests;

        internal static void Save(string directory, TestRunResultPayload snapshot, bool active, bool clearActive)
        {
            ValidateRunGuid(snapshot?.runGuid);
            WithLock(directory, () => SaveLocked(directory, snapshot, active, clearActive));
        }

        // UTF supplies its run GUID only after Execute schedules the job. Persist ownership
        // first, before constructing an API or registering callbacks; never launch on IO failure.
        internal static void Admit(string directory, TestRunResultPayload admission)
        {
            ValidateRunGuid(admission?.runGuid);
            ValidateLifecycle(admission.serviceLifecycleId);
            if (!IsAdmission(admission) || !admission.runGuid.StartsWith("admission-", StringComparison.Ordinal)
                || !Guid.TryParseExact(admission.runGuid.Substring("admission-".Length), "N", out _))
                throw new ArgumentException("A distinct TestRuns admission identity is required.");
            WithLock(directory, () =>
            {
                RetirePreviousLifetimeLocked(directory, admission.serviceLifecycleId);
                if (File.Exists(Path.Combine(directory, "active-run.txt")))
                    throw new IOException("A TestRuns owner in the current service lifetime already exists; runner start refused.");
                SaveLocked(directory, admission, true, false, admitting: true);
            });
        }

        internal static void RetirePreviousLifetime(string directory, string lifecycle)
        {
            ValidateLifecycle(lifecycle);
            WithLock(directory, () => RetirePreviousLifetimeLocked(directory, lifecycle));
        }

        private static void RetirePreviousLifetimeLocked(string directory, string lifecycle)
        {
            string activePath = Path.Combine(directory, "active-run.txt");
            if (!File.Exists(activePath)) return;
            string owner = File.ReadAllText(activePath).Trim();
            TestRunResultPayload previous;
            try
            {
                ValidateRunGuid(owner);
                previous = Read(Path.Combine(directory, owner + ".json"));
                if (previous == null || previous.runGuid != owner)
                    throw new IOException("The active pointer has no matching TestRuns snapshot.");
                ValidateLifecycle(previous.serviceLifecycleId);
            }
            catch (Exception ex)
            {
                throw new IOException("Active TestRuns ownership is unconfirmed; runner start refused.", ex);
            }
            if (previous.serviceLifecycleId == lifecycle) return;
            // Retire only the activity index. Results, cleanup evidence and deadlines stay
            // byte-for-byte history; no previous-lifetime work is restored or replayed.
            ClearOwnedPointer(activePath, owner);
        }

        private static void SaveLocked(string directory, TestRunResultPayload snapshot, bool active,
            bool clearActive, bool admitting = false)
        {
            string activePath = Path.Combine(directory, "active-run.txt");
            string owner = File.Exists(activePath) ? File.ReadAllText(activePath).Trim() : null;
            if ((active || clearActive) && owner != null && owner != snapshot.runGuid
                && !AdmissionOwns(directory, owner, snapshot))
                throw new IOException("Active TestRuns pointer belongs to another run; commit refused.");
            if (active && !clearActive && IsAdmission(snapshot) && owner == null && !admitting)
                throw new IOException("The TestRuns admission is no longer active; commit refused.");
            string path = Path.Combine(directory, snapshot.runGuid + ".json");
            SnapshotHeader previous = ReadHeader(path);
            if (previous.exists && (previous.snapshotSequence > snapshot.snapshotSequence
                || (previous.endedAt > 0 && snapshot.endedAt == 0)))
                throw new IOException("A newer TestRuns snapshot or terminal result already exists.");
            long sequence = Math.Max(snapshot.snapshotSequence, previous.snapshotSequence) + 1;
            var copy = snapshot.ShallowCopyForPersistence();
            copy.snapshotSequence = sequence;
            copy.persistenceError = "";
            AtomicWrite(path, JsonUtility.ToJson(copy, true));
            snapshot.snapshotSequence = sequence;
            WritePointerIfChanged(Path.Combine(directory, "last-run.txt"), snapshot.runGuid);
            if (clearActive)
            {
                if (owner != null) ClearOwnedPointer(activePath, owner);
            }
            else if (active)
                WritePointerIfChanged(activePath, snapshot.runGuid);
            snapshot.persistenceError = "";
        }

        private static bool IsAdmission(TestRunResultPayload snapshot) =>
            !string.IsNullOrEmpty(snapshot.runAdmissionId) && snapshot.runGuid == snapshot.runAdmissionId;

        private static bool AdmissionOwns(string directory, string owner, TestRunResultPayload snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.runAdmissionId) || owner != snapshot.runAdmissionId) return false;
            ValidateLifecycle(snapshot.serviceLifecycleId);
            var admission = Read(Path.Combine(directory, owner + ".json"));
            return admission != null && IsAdmission(admission) && admission.runGuid == owner
                && admission.serviceLifecycleId == snapshot.serviceLifecycleId && admission.endedAt == 0;
        }

        private static void ClearOwnedPointer(string path, string owner)
        {
            if (!File.Exists(path) || File.ReadAllText(path).Trim() != owner)
                throw new IOException("Active TestRuns pointer changed before commit.");
            BeforeActivePointerClearForTests?.Invoke(path);
            if (!File.Exists(path) || File.ReadAllText(path).Trim() != owner)
                throw new IOException("Active TestRuns pointer changed before commit.");
            File.Delete(path);
        }

        private static void ValidateRunGuid(string runGuid)
        {
            if (string.IsNullOrWhiteSpace(runGuid) || runGuid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || runGuid == "." || runGuid == "..")
                throw new ArgumentException("A safe runGuid file identity is required.");
        }

        private static void ValidateLifecycle(string lifecycle)
        {
            if (!Guid.TryParseExact(lifecycle, "N", out _))
                throw new IOException("A verified TestRuns service lifetime is required.");
        }

        private static void WithLock(string directory, Action action)
        {
            Directory.CreateDirectory(directory);
            using var hash = SHA256.Create();
            string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                Path.GetFullPath(directory).ToUpperInvariant()))).Replace("-", "");
            using var mutex = new Mutex(false, "UPilot.TestRuns." + key);
            bool acquired;
            try { acquired = mutex.WaitOne(5000); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another TestRuns writer owns the persistence directory.");
            try { action(); }
            finally { mutex.ReleaseMutex(); }
        }

        private static void WritePointerIfChanged(string path, string runGuid)
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), runGuid, StringComparison.Ordinal))
                return;
            AtomicWrite(path, runGuid);
        }

        private static SnapshotHeader ReadHeader(string path)
        {
            if (!File.Exists(path)) return default;
            var scanner = new JsonSnapshotHeaderScanner();
            byte[] buffer = new byte[16 * 1024];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.SequentialScan))
            {
                int count;
                while (!scanner.Complete && (count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int index = 0; index < count && !scanner.Complete; index++)
                        scanner.Accept(buffer[index]);
                }
            }
            scanner.FinishNumber();
            if (scanner.Complete)
            {
                return new SnapshotHeader
                {
                    exists = true,
                    snapshotSequence = scanner.SnapshotSequence,
                    endedAt = scanner.EndedAt,
                };
            }

            // Compatibility fallback for an older or non-standard snapshot shape. Normal
            // snapshots use the streaming header scan and never deserialize large result lists.
            var previous = Read(path);
            return new SnapshotHeader
            {
                exists = previous != null,
                snapshotSequence = previous?.snapshotSequence ?? 0,
                endedAt = previous?.endedAt ?? 0,
            };
        }

        internal static TestRunResultPayload Read(string path)
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var snapshot = JsonUtility.FromJson<TestRunResultPayload>(reader.ReadToEnd());
            if (snapshot == null) return null;
            string pointer = Path.Combine(Path.GetDirectoryName(path), "active-run.txt");
            // A terminal candidate is not committed while its active pointer remains.
            // Read errors propagate: an unreadable pointer is not evidence of release.
            if ((snapshot.cleanupSucceeded || snapshot.disposition != null) && File.Exists(pointer)
                && (File.ReadAllText(pointer).Trim() == snapshot.runGuid
                    || (!string.IsNullOrEmpty(snapshot.runAdmissionId)
                        && File.ReadAllText(pointer).Trim() == snapshot.runAdmissionId)))
            {
                snapshot.status = "cleanup";
                snapshot.phase = "cleanup";
                snapshot.cleanupStatus = "committing";
                snapshot.cleanupPending = true;
                snapshot.cleanupSucceeded = false;
                snapshot.terminal = false;
                snapshot.isRunning = true;
                snapshot.unresolvedResources ??= new System.Collections.Generic.List<string>();
                if (!snapshot.unresolvedResources.Contains("run-guid:" + snapshot.runGuid))
                    snapshot.unresolvedResources.Add("run-guid:" + snapshot.runGuid);
            }
            return snapshot;
        }

        internal static void AtomicWrite(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                BeforeAtomicReplaceForTests?.Invoke(path);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private struct SnapshotHeader
        {
            internal bool exists;
            internal long snapshotSequence;
            internal long endedAt;
        }

        private sealed class JsonSnapshotHeaderScanner
        {
            private const string SnapshotSequenceName = "snapshotSequence";
            private const string EndedAtName = "endedAt";
            private int _depth;
            private bool _inString;
            private bool _escaped;
            private bool _capturingKey;
            private bool _expectingProperty;
            private bool _afterKey;
            private int _keyLength;
            private bool _snapshotKeyMatch;
            private bool _endedAtKeyMatch;
            private int _target;
            private bool _waitingForValue;
            private bool _parsingNumber;
            private bool _negative;
            private long _number;
            private bool _hasSnapshotSequence;
            private bool _hasEndedAt;

            internal long SnapshotSequence { get; private set; }
            internal long EndedAt { get; private set; }
            internal bool Complete => _hasSnapshotSequence && _hasEndedAt;

            internal void Accept(byte current)
            {
                if (_inString)
                {
                    if (_escaped)
                    {
                        _escaped = false;
                        if (_capturingKey) InvalidateKey();
                        return;
                    }
                    if (current == (byte)'\\')
                    {
                        _escaped = true;
                        return;
                    }
                    if (current == (byte)'"')
                    {
                        _inString = false;
                        if (_capturingKey)
                        {
                            _capturingKey = false;
                            _target = _snapshotKeyMatch && _keyLength == SnapshotSequenceName.Length ? 1
                                : _endedAtKeyMatch && _keyLength == EndedAtName.Length ? 2 : 0;
                            _afterKey = true;
                            _expectingProperty = false;
                        }
                        return;
                    }
                    if (_capturingKey) AcceptKeyByte(current);
                    return;
                }

                if (_parsingNumber)
                {
                    if (IsDigit(current))
                    {
                        _number = checked(_number * 10 + current - (byte)'0');
                        return;
                    }
                    FinishNumber();
                }

                if (_waitingForValue)
                {
                    if (IsWhitespace(current)) return;
                    if (current == (byte)'-')
                    {
                        _negative = true;
                        _parsingNumber = true;
                        _waitingForValue = false;
                        return;
                    }
                    if (IsDigit(current))
                    {
                        _number = current - (byte)'0';
                        _parsingNumber = true;
                        _waitingForValue = false;
                        return;
                    }
                    ResetTarget();
                }

                if (current == (byte)'"')
                {
                    _inString = true;
                    _capturingKey = _depth == 1 && _expectingProperty;
                    if (_capturingKey)
                    {
                        _keyLength = 0;
                        _snapshotKeyMatch = true;
                        _endedAtKeyMatch = true;
                    }
                    return;
                }

                if (current == (byte)'{' || current == (byte)'[')
                {
                    _depth++;
                    if (_depth == 1) _expectingProperty = true;
                    return;
                }
                if (current == (byte)'}' || current == (byte)']')
                {
                    _depth--;
                    return;
                }
                if (_depth != 1) return;
                if (_afterKey)
                {
                    if (IsWhitespace(current)) return;
                    _afterKey = false;
                    if (current == (byte)':' && _target != 0)
                    {
                        _waitingForValue = true;
                        return;
                    }
                    ResetTarget();
                }
                if (current == (byte)',')
                {
                    _expectingProperty = true;
                    ResetTarget();
                }
            }

            internal void FinishNumber()
            {
                if (!_parsingNumber) return;
                long value = _negative ? -_number : _number;
                if (_target == 1)
                {
                    SnapshotSequence = value;
                    _hasSnapshotSequence = true;
                }
                else if (_target == 2)
                {
                    EndedAt = value;
                    _hasEndedAt = true;
                }
                _parsingNumber = false;
                ResetTarget();
            }

            private void AcceptKeyByte(byte current)
            {
                if (_snapshotKeyMatch
                    && (_keyLength >= SnapshotSequenceName.Length || current != (byte)SnapshotSequenceName[_keyLength]))
                    _snapshotKeyMatch = false;
                if (_endedAtKeyMatch
                    && (_keyLength >= EndedAtName.Length || current != (byte)EndedAtName[_keyLength]))
                    _endedAtKeyMatch = false;
                _keyLength++;
            }

            private void InvalidateKey()
            {
                _snapshotKeyMatch = false;
                _endedAtKeyMatch = false;
                _keyLength++;
            }

            private void ResetTarget()
            {
                _afterKey = false;
                _waitingForValue = false;
                _negative = false;
                _number = 0;
                _target = 0;
            }

            private static bool IsWhitespace(byte value) => value == (byte)' ' || value == (byte)'\t'
                || value == (byte)'\r' || value == (byte)'\n';

            private static bool IsDigit(byte value) => value >= (byte)'0' && value <= (byte)'9';
        }
    }
}
