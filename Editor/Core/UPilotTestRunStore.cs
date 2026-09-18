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
                var previous = Read(path);
                if (previous != null && (previous.snapshotSequence > snapshot.snapshotSequence
                    || (previous.endedAt > 0 && snapshot.endedAt == 0)))
                    throw new IOException("A newer TestRuns snapshot or terminal result already exists.");
                long sequence = Math.Max(snapshot.snapshotSequence, previous?.snapshotSequence ?? 0) + 1;
                var copy = JsonUtility.FromJson<TestRunResultPayload>(JsonUtility.ToJson(snapshot));
                copy.snapshotSequence = sequence;
                copy.persistenceError = "";
                AtomicWrite(path, JsonUtility.ToJson(copy, true));
                snapshot.snapshotSequence = sequence;
                snapshot.persistenceError = "";
                AtomicWrite(Path.Combine(directory, "last-run.txt"), snapshot.runGuid);
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
                    AtomicWrite(activePath, snapshot.runGuid);
            }
            finally { mutex.ReleaseMutex(); }
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
    }
}
