using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Single durable plan, atomically replaced before effects; no serialized CLR resources.</summary>
    internal sealed class AutomationStepRunStore
    {
        private readonly string _path;
        internal AutomationStepRunStore(string path) { _path = path; }
        internal static string EditorIdentity
        {
            get
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                return process.Id + ":" + process.StartTime.ToUniversalTime().Ticks;
            }
        }
        internal static string Hash(AutomationStepPlan plan)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(plan))).Select(b => b.ToString("x2")));
        }
        internal void Save(AutomationStepRun run)
            => SaveJson(_path, JsonUtility.ToJson(run, true));
        internal static void SaveJson(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        internal AutomationStepRun Load()
        {
            if (!File.Exists(_path)) return null;
            var run = JsonUtility.FromJson<AutomationStepRun>(File.ReadAllText(_path));
            if (run == null || run.version != 1 || string.IsNullOrEmpty(run.runId)
                || run.plan == null || run.planHash != Hash(run.plan) || run.steps.Count != run.plan.steps.Length
                || run.cursor < 0 || run.cursor > run.steps.Count)
                throw new InvalidDataException("STEP_STORE_INVALID");
            return run;
        }
    }
}
