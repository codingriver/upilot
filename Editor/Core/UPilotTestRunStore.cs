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
            if (string.IsNullOrWhiteSpace(snapshot?.runGuid) || snapshot.runGuid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || snapshot.runGuid == "." || snapshot.runGuid == "..")
                throw new ArgumentException("A safe runGuid file identity is required.");
            Directory.CreateDirectory(directory);
            using var hash = SHA256.Create();
            string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                Path.GetFullPath(directory).ToUpperInvariant()))).Replace("-", "");
            using var mutex = new Mutex(false, "UPilot.TestRuns." + key);
            bool acquired;
            try { acquired = mutex.WaitOne(5000); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Another TestRuns writer owns the persistence directory.");
            try
            {
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
                snapshot.persistenceError = "";
                WritePointerIfChanged(Path.Combine(directory, "last-run.txt"), snapshot.runGuid);
                string activePath = Path.Combine(directory, "active-run.txt");
                if (clearActive)
                {
                    if (File.Exists(activePath) && File.ReadAllText(activePath).Trim() == snapshot.runGuid)
                    {
                        BeforeActivePointerClearForTests?.Invoke(activePath);
                        File.Delete(activePath);
                    }
                }
                else if (active)
                    WritePointerIfChanged(activePath, snapshot.runGuid);
            }
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
            return JsonUtility.FromJson<TestRunResultPayload>(reader.ReadToEnd());
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
