using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Single durable plan, atomically replaced before effects; no serialized CLR resources.</summary>
    internal sealed class AutomationStepRunStore
    {
        private readonly string _path;
        [Serializable] private sealed class LegacyItem
        {
            public string instanceId, stepId, arguments, phase;
            public double timeoutSeconds, pollIntervalSeconds, cleanupTimeoutSeconds;
            public bool allowSkipped;
        }
        [Serializable] private sealed class LegacyPlan
        {
            public int version;
            public LegacyItem[] steps;
            public AutomationLogRule[] logPolicy;
        }
        internal AutomationStepRunStore(string path) { _path = path; }
        internal static string EditorIdentity
        {
            get
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                return process.Id + ":" + process.StartTime.ToUniversalTime().Ticks;
            }
        }
        internal static string Hash(AutomationStepPlan plan, int gizmosPolicyVersion = 0)
        {
            string json = JsonUtility.ToJson(plan);
            if (gizmosPolicyVersion == 0)
            {
                // Preserve the exact pre-Gizmos projection for active and historical records.
                json = JsonUtility.ToJson(new LegacyPlan
                {
                    version = plan.version, logPolicy = plan.logPolicy,
                    steps = plan.steps.Select(item => item == null ? null : new LegacyItem
                    {
                        instanceId = item.instanceId, stepId = item.stepId, arguments = item.arguments, phase = item.phase,
                        timeoutSeconds = item.timeoutSeconds, pollIntervalSeconds = item.pollIntervalSeconds,
                        cleanupTimeoutSeconds = item.cleanupTimeoutSeconds, allowSkipped = item.allowSkipped,
                    }).ToArray(),
                });
            }
            else if (gizmosPolicyVersion != 1) throw new InvalidDataException("STEP_GIZMOS_POLICY_UNSUPPORTED");
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(json)).Select(b => b.ToString("x2")));
        }
        internal static string HashText(string value)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
        }
        private string CompilationFenceKey(AutomationStepRun run) =>
            "UPilot.Step.Compilation." + HashText(Path.GetFullPath(_path) + "\n" + run.runId);
        internal void FenceCompilation(AutomationStepRun run) =>
            SessionState.SetString(CompilationFenceKey(run), JsonUtility.ToJson(run));
        internal AutomationStepRun CompilationFence(AutomationStepRun run)
        {
            string json = SessionState.GetString(CompilationFenceKey(run), "");
            if (string.IsNullOrEmpty(json)) return null;
            var fence = JsonUtility.FromJson<AutomationStepRun>(json);
            if (fence == null || fence.runId != run.runId || fence.operationId != run.operationId
                || fence.planHash != run.planHash || fence.serviceLifecycleId != run.serviceLifecycleId
                || fence.editorIdentity != EditorIdentity || fence.editorIdentity != run.editorIdentity
                || !fence.terminal || fence.status != "Failed" || fence.compilationStartedAtUtcMs <= 0
                || fence.error?.code != "STEP_COMPILATION_STARTED")
                throw new InvalidDataException("STEP_COMPILATION_FENCE_INVALID");
            return fence;
        }
        internal void ClearCompilationFence(AutomationStepRun run) => SessionState.EraseString(CompilationFenceKey(run));
        private string FinallyFailureFenceKey(AutomationStepRun run) =>
            "UPilot.Step.FinallyFailure." + HashText(Path.GetFullPath(_path) + "\n" + run.runId);
        internal void FenceFinallyFailure(AutomationStepRun run) =>
            SessionState.SetString(FinallyFailureFenceKey(run), JsonUtility.ToJson(run));
        internal AutomationStepRun FinallyFailureFence(AutomationStepRun run)
        {
            string json = SessionState.GetString(FinallyFailureFenceKey(run), "");
            if (string.IsNullOrEmpty(json)) return null;
            var fence = JsonUtility.FromJson<AutomationStepRun>(json);
            if (fence == null || fence.runId != run.runId || fence.operationId != run.operationId
                || fence.planHash != run.planHash || fence.serviceLifecycleId != run.serviceLifecycleId
                || fence.editorIdentity != EditorIdentity || fence.editorIdentity != run.editorIdentity
                || !fence.terminal || (fence.status != "Failed" && fence.status != "TimedOut")
                || fence.finallyFailureAtUtcMs <= 0 || fence.finallyCleanupDeadlineUtcMs <= 0
                || string.IsNullOrEmpty(fence.cleanupOutcome) || string.IsNullOrEmpty(fence.reportOutcome))
                throw new InvalidDataException("STEP_FINALLY_FAILURE_FENCE_INVALID");
            return fence;
        }
        internal void ClearFinallyFailureFence(AutomationStepRun run) => SessionState.EraseString(FinallyFailureFenceKey(run));
        private static void CheckDispositionPath(string path)
        {
            // A junction/symlink must not redirect the execution fence or its backup.
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("STEP_DISPOSITION_PATH_UNSAFE");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        private string DispositionPath(string operationId)
        {
            string path = Path.Combine(_path + ".dispositions", HashText(operationId) + ".json");
            CheckDispositionPath(path); CheckDispositionPath(path + ".backup"); CheckDispositionPath(path + ".tmp");
            return path;
        }
        internal AutomationStepRun Disposed(string operationId)
        {
            string path = DispositionPath(operationId);
            if (!File.Exists(path)) return null;
            var run = JsonUtility.FromJson<AutomationStepRun>(File.ReadAllText(path));
            var d = run?.disposition;
            if (d == null || run.operationId != operationId || d.operationId != operationId || d.runId != run.runId
                || string.IsNullOrEmpty(d.requestId) || run.status != "Released" || !run.terminal
                || run.cleanupPending || run.recoveryRequired || run.planHash != Hash(run.plan, run.gizmosPolicyVersion)
                || Path.GetFullPath(d.backupPath) != Path.GetFullPath(path + ".backup")
                || !File.Exists(d.backupPath) || new FileInfo(d.backupPath).Length != d.backupBytes
                || HashText(File.ReadAllText(d.backupPath)) != d.backupSha256)
                throw new InvalidDataException("STEP_DISPOSITION_INVALID");
            var original = JsonUtility.FromJson<AutomationStepRun>(File.ReadAllText(d.backupPath));
            if (original.runId != run.runId || original.operationId != operationId || original.planHash != run.planHash)
                throw new InvalidDataException("STEP_DISPOSITION_IDENTITY_INVALID");
            return run;
        }
        internal AutomationStepRun Historical(string runId, string operationId = null)
        {
            if (operationId != null)
            {
                var run = Disposed(operationId);
                return run?.runId == runId ? run : null;
            }
            string root = _path + ".dispositions";
            CheckDispositionPath(root);
            if (!Directory.Exists(root)) return null;
            foreach (string path in Directory.EnumerateFiles(root, "*.json"))
            {
                var run = JsonUtility.FromJson<AutomationStepRun>(File.ReadAllText(path));
                if (run?.runId == runId) return Disposed(run.operationId);
            }
            return null;
        }
        internal AutomationStepRun Release(AutomationStepRun original, string requestId, string reason, long now)
        {
            if (Disposed(original.operationId) != null) throw new InvalidOperationException("STEP_ALREADY_RELEASED");
            string path = DispositionPath(original.operationId), backup = path + ".backup";
            string json = JsonUtility.ToJson(original, true);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Never overwrite an uncertain prior preparation.
            using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            if (File.ReadAllText(backup) != json) throw new IOException("STEP_BACKUP_VERIFICATION_FAILED");
            var released = AutomationStepJson.Copy(original);
            released.disposition = new AutomationStepDisposition { requestId = requestId, reason = reason,
                runId = original.runId, operationId = original.operationId, originalStatus = original.status,
                originalTerminal = original.terminal, disposedAtUtcMs = now, backupPath = Path.GetFullPath(backup),
                backupBytes = new FileInfo(backup).Length, backupSha256 = HashText(json) };
            released.status = "Released"; released.terminal = true; released.cleanupPending = false;
            released.recoveryRequired = false;
            SaveJson(path, JsonUtility.ToJson(released, true));
            return Disposed(original.operationId); // Durable execution fence precedes clearing Busy.
        }
        internal void Save(AutomationStepRun run)
        {
            var disposed = Disposed(run.operationId);
            if (disposed != null && (run.status != "Released" || run.disposition?.requestId != disposed.disposition.requestId))
                throw new InvalidOperationException("STEP_RUN_RELEASED");
            // Resource helpers persist Stop/Cancel/Gizmos intent through this same entry.
            // The reload fence must never overwrite their newer disk intent with an older snapshot.
            if (run.terminal && run.finallyFailureAtUtcMs > 0) FenceFinallyFailure(run);
            SaveJson(_path, JsonUtility.ToJson(run, true));
        }
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
                || run.plan == null || run.planHash != Hash(run.plan, run.gizmosPolicyVersion) || run.steps.Count != run.plan.steps.Length
                || run.cursor < 0 || run.cursor > run.steps.Count)
                throw new InvalidDataException("STEP_STORE_INVALID");
            var disposed = Disposed(run.operationId);
            if (disposed == null && (run.status == "Released" || !string.IsNullOrEmpty(run.disposition?.requestId)))
                throw new InvalidDataException("STEP_DISPOSITION_MISSING");
            return disposed ?? run;
        }
    }
}
