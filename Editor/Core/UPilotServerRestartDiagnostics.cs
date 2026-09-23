// -----------------------------------------------------------------------
// UPilot Editor - MCP server restart persistence and diagnostics.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    internal sealed class UPilotServerRestartRecord
    {
        public int schemaVersion = 1;
        public string operationId;
        public string projectPath;
        public string status = "running";
        public string phase = "intent_persisted";
        public int oldProcessId;
        public int newProcessId;
        public string oldBridgeSessionId;
        public string newBridgeSessionId;
        public long requestedAtUtcMs;
        public long maintenanceDeadlineUtcMs;
        public long oldProcessStopRequestedAtUtcMs;
        public long portsReleasedAtUtcMs;
        public long newProcessStartedAtUtcMs;
        public long newProcessCreatedAtTicks;
        public long healthVerifiedAtUtcMs;
        public long bridgeVerifiedAtUtcMs;
        public long endedAtUtcMs;
        public long updatedAtUtcMs;
        public bool healthVerified;
        public bool projectIdentityVerified;
        public bool bridgeVerified;
        public bool deploymentVerified;
        public bool readOnlyVerified;
        public string failurePhase;
        public string healthProjectPath;
        public bool exitObserved;
        public int exitCode;
        public string errorCode;
        public string error;
        public string stderrTail;
        public string diagnosticSource;
        public string nextAction;
    }

    internal static class UPilotServerRestartDiagnostics
    {
        internal const int DiagnosticTailLimit = 4096;
        private static readonly object Sync = new();
        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static UPilotServerRestartRecord s_record;

        internal static string RecordPath => Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? ".",
            "Library", "UPilot", "server-restart.json");

        internal static UPilotServerRestartRecord Begin(
            string projectPath,
            int oldProcessId,
            string oldBridgeSessionId,
            long maintenanceDeadlineUtcMs = 0)
        {
            var now = UtcNowMs();
            var record = new UPilotServerRestartRecord
            {
                operationId = "restart-" + Guid.NewGuid().ToString("N"),
                projectPath = NormalizePath(projectPath),
                oldProcessId = Math.Max(0, oldProcessId),
                oldBridgeSessionId = oldBridgeSessionId ?? "",
                requestedAtUtcMs = now,
                maintenanceDeadlineUtcMs = maintenanceDeadlineUtcMs,
                oldProcessStopRequestedAtUtcMs = now,
                updatedAtUtcMs = now,
                nextAction = "Wait for the replacement MCP Server and Bridge session to be verified.",
            };
            lock (Sync)
            {
                s_record = record;
                if (!TryWriteRecord(s_record, RecordPath, out var error))
                {
                    s_record = null;
                    throw new InvalidOperationException(
                        "MCP Server restart intent could not be persisted: " + error);
                }
            }
            return record;
        }

        internal static void RecordPortsReleased(string operationId)
        {
            Update(operationId, record =>
            {
                record.phase = "ports_released";
                record.portsReleasedAtUtcMs = UtcNowMs();
            });
        }

        internal static void RecordProcessStarted(string operationId, int processId, long createdAtTicks = 0)
        {
            if (processId <= 0) return;
            Update(operationId, record =>
            {
                record.phase = "process_started";
                record.newProcessId = processId;
                record.newProcessStartedAtUtcMs = UtcNowMs();
                record.newProcessCreatedAtTicks = createdAtTicks;
            });
        }

        internal static void RecordHealthVerified(
            string operationId,
            int processId,
            string healthProjectPath)
        {
            Update(operationId, record =>
            {
                if (record.newProcessId != processId) return;
                record.healthVerified = true;
                record.projectIdentityVerified = SamePath(record.projectPath, healthProjectPath);
                record.healthProjectPath = NormalizePath(healthProjectPath);
                record.healthVerifiedAtUtcMs = UtcNowMs();
                record.phase = record.bridgeVerified ? "verified" : "health_verified";
                TryComplete(record);
            });
        }

        internal static void RecordBridgeVerified(string operationId, string bridgeSessionId)
        {
            Update(operationId, record =>
            {
                if (string.IsNullOrWhiteSpace(bridgeSessionId) ||
                    string.Equals(bridgeSessionId, record.oldBridgeSessionId, StringComparison.Ordinal))
                    return;
                record.bridgeVerified = true;
                record.newBridgeSessionId = bridgeSessionId;
                record.bridgeVerifiedAtUtcMs = UtcNowMs();
                record.phase = record.healthVerified && record.projectIdentityVerified
                    ? "verified"
                    : "bridge_verified";
                TryComplete(record);
            });
        }

        internal static void RecordProcessExited(
            string operationId,
            int processId,
            int exitCode,
            string diagnosticTail,
            string diagnosticSource)
        {
            Update(operationId, record =>
            {
                if (record.newProcessId != processId) return;
                record.exitObserved = true;
                record.exitCode = exitCode;
                FailRecord(
                    record,
                    "server_process_exited",
                    $"Replacement MCP Server PID {processId} exited with code {exitCode} before restart verification completed.",
                    diagnosticTail,
                    diagnosticSource,
                    "Inspect the bounded server diagnostic tail, correct the startup failure, then request one new restart.");
            });
        }

        internal static void RecordFailure(
            string operationId,
            string errorCode,
            string error,
            string diagnosticTail = "",
            string diagnosticSource = "",
            string nextAction = "Inspect restart diagnostics before requesting one new restart.")
        {
            Update(operationId, record =>
                FailRecord(record, errorCode, error, diagnosticTail, diagnosticSource, nextAction));
        }

        internal static void RecordCanceled(string operationId, string reason)
        {
            Update(operationId, record =>
            {
                record.status = "canceled";
                record.phase = "canceled";
                record.errorCode = "restart_canceled";
                record.error = reason ?? "MCP Server restart was canceled.";
                record.nextAction = "Query the current MCP status before deciding whether a new restart is needed.";
                record.endedAtUtcMs = UtcNowMs();
            });
        }

        internal static bool IsActive(string operationId)
        {
            lock (Sync)
            {
                EnsureLoadedLocked();
                return s_record != null &&
                       string.Equals(s_record.operationId, operationId, StringComparison.Ordinal) &&
                       string.Equals(s_record.status, "running", StringComparison.Ordinal);
            }
        }

        internal static bool TryGetActive(out UPilotServerRestartRecord record)
        {
            lock (Sync)
            {
                EnsureLoadedLocked();
                record = s_record;
                return record != null && string.Equals(record.status, "running", StringComparison.Ordinal);
            }
        }

        internal static UPilotServerRestartRecord Current
        {
            get { lock (Sync) { EnsureLoadedLocked(); return s_record; } }
        }

        internal static void RecordDeploymentVerified(string operationId)
        {
            Update(operationId, record =>
            {
                record.deploymentVerified = true;
                record.readOnlyVerified = true;
                record.phase = "deployment_verified";
                TryComplete(record);
            });
        }

        internal static string ReadServerLogTail(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = (int)Math.Min(DiagnosticTailLimit, stream.Length);
                if (length <= 0) return "";
                stream.Seek(-length, SeekOrigin.End);
                var buffer = new byte[length];
                var read = stream.Read(buffer, 0, length);
                return Bound(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (Exception ex)
            {
                return Bound("Unable to read server log tail: " + ex.Message);
            }
        }

        private static void Update(string operationId, Action<UPilotServerRestartRecord> update)
        {
            if (string.IsNullOrWhiteSpace(operationId) || update == null) return;
            lock (Sync)
            {
                EnsureLoadedLocked();
                if (s_record == null ||
                    !string.Equals(s_record.operationId, operationId, StringComparison.Ordinal) ||
                    !string.Equals(s_record.status, "running", StringComparison.Ordinal))
                    return;
                update(s_record);
                s_record.updatedAtUtcMs = UtcNowMs();
                PersistLocked();
            }
        }

        private static void TryComplete(UPilotServerRestartRecord record)
        {
            if (record.maintenanceDeadlineUtcMs > 0 && UtcNowMs() >= record.maintenanceDeadlineUtcMs)
            {
                FailRecord(record, "SERVICE_RESTART_TIMEOUT", "Maintenance deadline elapsed.", "", "",
                    "Inspect the original maintenance; do not replay start.");
                return;
            }
            if (!record.healthVerified || !record.projectIdentityVerified || !record.bridgeVerified ||
                !record.deploymentVerified || !record.readOnlyVerified)
                return;
            record.status = "succeeded";
            record.phase = "completed";
            record.endedAtUtcMs = UtcNowMs();
            record.errorCode = "";
            record.error = "";
            record.nextAction = "";
        }

        private static void FailRecord(
            UPilotServerRestartRecord record,
            string errorCode,
            string error,
            string diagnosticTail,
            string diagnosticSource,
            string nextAction)
        {
            record.status = "failed";
            record.failurePhase = record.phase;
            record.phase = "failed";
            record.errorCode = string.IsNullOrWhiteSpace(errorCode) ? "restart_failed" : errorCode;
            record.error = Bound(error);
            record.stderrTail = Bound(diagnosticTail);
            record.diagnosticSource = diagnosticSource ?? "";
            record.nextAction = nextAction ?? "";
            record.endedAtUtcMs = UtcNowMs();
        }

        private static void EnsureLoadedLocked()
        {
            if (s_record != null) return;
            TryLoadRecord(RecordPath, out s_record, out _);
        }

        private static void PersistLocked()
        {
            if (s_record == null) return;
            if (!TryWriteRecord(s_record, RecordPath, out var error))
                Logger.LogWarning("SERVER", "MCP Server restart record could not be persisted: " + error);
        }

        private static bool TryWriteRecord(UPilotServerRestartRecord record, string path, out string error)
        {
            var temporary = "";
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(record, true), Utf8NoBom);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(temporary) && File.Exists(temporary)) File.Delete(temporary);
                }
                catch { }
            }
        }

        private static bool TryLoadRecord(
            string path,
            out UPilotServerRestartRecord record,
            out string error)
        {
            record = null;
            error = "missing";
            try
            {
                if (!File.Exists(path)) return false;
                record = JsonUtility.FromJson<UPilotServerRestartRecord>(File.ReadAllText(path, Encoding.UTF8));
                if (record == null || string.IsNullOrWhiteSpace(record.operationId))
                {
                    record = null;
                    error = "corrupt";
                    return false;
                }
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private static bool SamePath(string left, string right) =>
            !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

        private static string Bound(string value)
        {
            value ??= "";
            return value.Length <= DiagnosticTailLimit
                ? value
                : value.Substring(value.Length - DiagnosticTailLimit, DiagnosticTailLimit);
        }

        private static long UtcNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        internal static UPilotServerRestartRecord CreateRecordForTests(
            string projectPath,
            int oldProcessId,
            string oldBridgeSessionId) => new()
        {
            operationId = "restart-test",
            projectPath = NormalizePath(projectPath),
            oldProcessId = oldProcessId,
            oldBridgeSessionId = oldBridgeSessionId ?? "",
            requestedAtUtcMs = 1,
            updatedAtUtcMs = 1,
        };

        internal static void RecordProcessStartedForTests(UPilotServerRestartRecord record, int processId)
        {
            record.newProcessId = processId;
            record.newProcessStartedAtUtcMs = 2;
            record.phase = "process_started";
        }

        internal static void RecordHealthVerifiedForTests(
            UPilotServerRestartRecord record,
            int processId,
            string healthProjectPath)
        {
            if (record.newProcessId != processId) return;
            record.healthVerified = true;
            record.projectIdentityVerified = SamePath(record.projectPath, healthProjectPath);
            record.healthProjectPath = NormalizePath(healthProjectPath);
            TryComplete(record);
        }

        internal static void RecordBridgeVerifiedForTests(
            UPilotServerRestartRecord record,
            string bridgeSessionId)
        {
            if (string.IsNullOrWhiteSpace(bridgeSessionId) ||
                string.Equals(bridgeSessionId, record.oldBridgeSessionId, StringComparison.Ordinal))
                return;
            record.bridgeVerified = true;
            record.newBridgeSessionId = bridgeSessionId;
            TryComplete(record);
        }

        internal static void RecordDeploymentVerifiedForTests(UPilotServerRestartRecord record)
        {
            record.deploymentVerified = true;
            record.readOnlyVerified = true;
            TryComplete(record);
        }

        internal static bool TryWriteRecordForTests(
            UPilotServerRestartRecord record,
            string path,
            out string error) => TryWriteRecord(record, path, out error);

        internal static bool TryLoadRecordForTests(
            string path,
            out UPilotServerRestartRecord record,
            out string error) => TryLoadRecord(path, out record, out error);

        internal static string BoundForTests(string value) => Bound(value);
    }
}
