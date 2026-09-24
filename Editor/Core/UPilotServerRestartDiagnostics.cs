// -----------------------------------------------------------------------
// UPilot Editor - MCP server restart persistence and diagnostics.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;

namespace CodingRiver.UPilot
{
    [Serializable]
    internal sealed class UPilotRestartGate
    {
        public string key;
        public string state;
        public long startedAtUtcMs;
        public long endedAtUtcMs;
        public string evidenceCode;
        public string evidence;
    }

    [Serializable]
    internal sealed class UPilotRestartHistory
    {
        public UPilotServerRestartRecord[] records = Array.Empty<UPilotServerRestartRecord>();
    }

    [Serializable]
    internal sealed class UPilotServerRestartRecord
    {
        public int schemaVersion = 3;
        public long restartGeneration;
        public string stopOrigin;
        public string stopRequestId;
        public long stopRequestedAtUtcMs;
        public int stopTargetProcessId;
        public long stopTargetCreatedAtTicks;
        public string supersededByOperationId;
        public string subsequentRecoveryStatus;
        public long subsequentVerifiedAtUtcMs;
        public int subsequentPid;
        public string subsequentInstanceId;
        public string operationId;
        public string projectPath;
        public string bridgeVersion;
        public string bridgeChannel;
        public string bridgeInstallSource;
        public bool bridgeIsMain;
        public string packageRoot;
        public string expectedEntry;
        public string serverVersion;
        public string serverChannel;
        public string serverInstallSource;
        public string actualEntry;
        public string actualModule;
        public int healthProcessId;
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
        public long deploymentVerifiedAtUtcMs;
        public long readOnlyVerifiedAtUtcMs;
        public long statusProbeStartedAtUtcMs;
        public long statusProbeEndedAtUtcMs;
        public int statusProbeCount;
        public string statusProbeOutcome;
        public string statusProbeFailureStage;
        public string statusProbeCancellationReason;
        public string statusProbeError;
        public long lastBridgeConnectedAtUtcMs;
        public long lastBridgeAuthenticatedAtUtcMs;
        public long lastBridgeDisconnectedAtUtcMs;
        public string lastBridgeCloseCode;
        public string lastBridgeCloseReason;
        public long lastBridgeOversizeAtUtcMs;
        public string lastBridgeOversizeSource;
        public int lastBridgeOversizeActualBytes;
        public int lastBridgeOversizeLimitBytes;
        public int bridgeOversizeCount;
        public long recoveredAtUtcMs;
        public int recoveryProcessId;
        public string recoveryBridgeSessionId;
        public bool recoveryReadOnlyVerified;
        public UPilotRestartGate[] gateDiagnostics = Array.Empty<UPilotRestartGate>();
        public long endedAtUtcMs;
        public long updatedAtUtcMs;
        public bool healthVerified;
        public bool projectIdentityVerified;
        public bool bridgeVerified;
        public bool deploymentVerified;
        public bool readOnlyVerified;
        public string failurePhase;
        // JsonUtility cannot serialize Nullable<T>. Presence flags distinguish old records
        // (unobserved) from a measured zero or false result.
        public bool hasIdentityProbe;
        public long identityProbeElapsedMs;
        public long candidateCollectionMs;
        public long portQueryMs;
        public long commandLineQueryMs;
        public int candidateCount;
        public int verifiedCount;
        public bool hasCommandLineQueryExitCode;
        public int commandLineQueryExitCode;
        public string commandLineQueryFailure;
        public bool bridgeStopAttempted;
        public bool hasBridgeStopConfirmation;
        public bool bridgeStopConfirmed;
        public bool bridgeRestoreAttempted;
        public string bridgeRestoreResult;
        public string bridgeRestoreError;
        public bool serverStopAttempted;
        public bool hasOldServerExitConfirmation;
        public bool oldServerExitConfirmed;
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

        internal static string HistoryPath => Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? ".",
            "Library", "UPilot", "server-restart-history.json");

        internal static UPilotServerRestartRecord[] ReadHistory(out string warning) => ReadHistoryAt(HistoryPath, out warning);

        internal static UPilotServerRestartRecord[] ReadHistoryAt(string path, out string warning)
        {
            warning = "";
            if (!File.Exists(path)) return Array.Empty<UPilotServerRestartRecord>();
            try
            {
                var json = File.ReadAllText(path, Encoding.UTF8);
                if (!json.Contains("\"records\"")) throw new InvalidDataException("Restart history has no records field.");
                var history = JsonUtility.FromJson<UPilotRestartHistory>(json);
                if (history?.records == null || history.records.Any(r => r == null || string.IsNullOrEmpty(r.operationId)))
                    throw new InvalidDataException("Invalid restart history records.");
                return history.records;
            }
            catch (Exception ex)
            {
                warning = "历史诊断读取失败，原文件已保留：" + ex.Message;
                try
                {
                    var backup = path + ".corrupt";
                    if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
                    if (File.Exists(backup)) warning += "；损坏文件备份：" + backup;
                }
                catch (Exception backupError) { warning += "；备份失败：" + backupError.Message; }
                return Array.Empty<UPilotServerRestartRecord>();
            }
        }

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
                bridgeVersion = UPilotDeploymentDiagnostics.Version,
                bridgeChannel = UPilotDeploymentDiagnostics.Channel,
                bridgeInstallSource = UPilotDeploymentDiagnostics.InstallSource,
                bridgeIsMain = UPilotDeploymentDiagnostics.IsMain,
                packageRoot = UPilotDeploymentDiagnostics.PackageRoot,
                expectedEntry = UPilotDeploymentDiagnostics.PythonEntry,
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

        internal static void RecordGeneration(string operationId, long generation)
        {
            Update(operationId, record => record.restartGeneration = generation);
        }

        internal static void MarkGate(string operationId, string key, string state, string evidenceCode = "", string evidence = "")
        {
            Update(operationId, record => MarkGateCore(record, key, state, evidenceCode, evidence));
        }

        private static void MarkGateCore(UPilotServerRestartRecord record, string key, string state,
            string evidenceCode = "", string evidence = "")
        {
            var gates = record.gateDiagnostics ?? Array.Empty<UPilotRestartGate>();
            var gate = gates.FirstOrDefault(g => g.key == key);
            if (gate == null)
            {
                gate = new UPilotRestartGate { key = key, startedAtUtcMs = UtcNowMs() };
                record.gateDiagnostics = gates.Concat(new[] { gate }).OrderBy(g => Array.IndexOf(GateKeys, g.key)).ToArray();
            }
            gate.state = state;
            if (state == "running") gate.endedAtUtcMs = 0;
            if (state == "passed" || state == "failed" || state == "canceled") gate.endedAtUtcMs = UtcNowMs();
            gate.evidenceCode = Bound(evidenceCode);
            gate.evidence = Bound(evidence);
        }

        internal static void BeginStatusProbe(string operationId)
        {
            Update(operationId, record =>
            {
                record.statusProbeCount++;
                record.statusProbeStartedAtUtcMs = UtcNowMs();
                record.statusProbeEndedAtUtcMs = 0;
                record.statusProbeOutcome = "running";
                MarkGateCore(record, "health", "running");
            });
        }

        internal static void EndStatusProbe(string operationId, string outcome, string stage, string cancellation, string error)
        {
            Update(operationId, record =>
            {
                record.statusProbeEndedAtUtcMs = UtcNowMs();
                record.statusProbeOutcome = outcome ?? "unknown";
                record.statusProbeFailureStage = Bound(stage);
                record.statusProbeCancellationReason = Bound(cancellation);
                record.statusProbeError = Bound(error);
                if (outcome == "success") MarkGateCore(record, "health", "passed", "health_ok");
                else if (outcome == "process_identity_mismatch" || outcome == "project_identity_mismatch")
                {
                    MarkGateCore(record, "health", "passed", "health_ok");
                    MarkGateCore(record, "identity", "failed", outcome, error);
                }
                else if (outcome == "lifecycle_canceled") MarkGateCore(record, "health", "canceled", outcome, error);
                else MarkGateCore(record, "health", "failed", outcome, error);
            });
        }

        internal static void RecordBridgeSnapshot(string operationId, BridgeStatus status)
        {
            Update(operationId, record =>
            {
                record.lastBridgeConnectedAtUtcMs = status.LastConnectedAtUtcMs;
                record.lastBridgeAuthenticatedAtUtcMs = status.LastAuthenticatedAtUtcMs;
                record.lastBridgeDisconnectedAtUtcMs = status.LastDisconnectedAtUtcMs;
                if (status.LastDisconnectedAtUtcMs >= record.requestedAtUtcMs &&
                    !string.IsNullOrEmpty(status.LastCloseCode))
                {
                    record.lastBridgeCloseCode = Bound(status.LastCloseCode);
                    record.lastBridgeCloseReason = Bound(status.LastCloseReason);
                }
                if (status.LastOversizeAtUtcMs >= record.requestedAtUtcMs &&
                    status.LastOversizeAtUtcMs > record.lastBridgeOversizeAtUtcMs)
                {
                    record.lastBridgeOversizeAtUtcMs = status.LastOversizeAtUtcMs;
                    record.lastBridgeOversizeSource = Bound(status.LastOversizeSource);
                    record.lastBridgeOversizeActualBytes = status.LastOversizeActualBytes;
                    record.lastBridgeOversizeLimitBytes = status.LastOversizeLimitBytes;
                }
                record.bridgeOversizeCount = Math.Max(record.bridgeOversizeCount, status.OversizeCount);
            });
        }

        internal static void RecordServerBridgeDiagnostics(string operationId, UPilotServerHealth health)
        {
            if (health?.bridge_diagnostics == null) return;
            Update(operationId, record =>
            {
                var bridge = health.bridge_diagnostics;
                record.lastBridgeConnectedAtUtcMs = Math.Max(record.lastBridgeConnectedAtUtcMs, bridge.connected_at_ms);
                record.lastBridgeAuthenticatedAtUtcMs = Math.Max(record.lastBridgeAuthenticatedAtUtcMs, bridge.authenticated_at_ms);
                record.lastBridgeDisconnectedAtUtcMs = Math.Max(record.lastBridgeDisconnectedAtUtcMs, bridge.disconnected_at_ms);
                if (bridge.disconnected_at_ms >= record.requestedAtUtcMs &&
                    !string.IsNullOrEmpty(bridge.last_close_code))
                {
                    record.lastBridgeCloseCode = Bound(bridge.last_close_code);
                    record.lastBridgeCloseReason = Bound(bridge.last_close_reason);
                }
                if (bridge.oversize_at_ms >= record.requestedAtUtcMs &&
                    bridge.oversize_at_ms > record.lastBridgeOversizeAtUtcMs)
                {
                    record.lastBridgeOversizeAtUtcMs = bridge.oversize_at_ms;
                    record.lastBridgeOversizeSource = Bound(bridge.oversize_source);
                    record.lastBridgeOversizeActualBytes = bridge.oversize_actual_bytes;
                    record.lastBridgeOversizeLimitBytes = bridge.oversize_limit_bytes;
                }
                record.bridgeOversizeCount = Math.Max(record.bridgeOversizeCount, bridge.oversize_count);
                if (bridge.last_close_code == "1009" && bridge.oversize_at_ms >= record.requestedAtUtcMs)
                    record.lastBridgeCloseCode = "1009";
            });
        }

        internal static void RecordDeploymentIdentity(string operationId, UPilotServerHealth health)
        {
            if (health == null) return;
            Update(operationId, record =>
            {
                record.serverVersion = Bound(health.server_version);
                record.serverChannel = Bound(health.build_channel);
                record.serverInstallSource = Bound(health.server_install_source);
                record.actualEntry = Bound(health.server_entry_path);
                record.actualModule = Bound(health.server_module_root);
                record.healthProcessId = health.server_pid;
                record.healthProjectPath = NormalizePath(health.configured_project_path);
            });
        }

        internal static void RecordOldProcessId(string operationId, int processId)
        {
            Update(operationId, record => record.oldProcessId = processId);
        }

        internal static void RecordPhase(string operationId, string phase)
        {
            Update(operationId, record => record.phase = phase);
        }

        internal static void RecordIdentityProbe(string operationId, long elapsedMs,
            long candidateCollectionMs, long portQueryMs, long commandLineQueryMs,
            int candidateCount, int verifiedCount, int? queryExitCode, string queryFailure)
        {
            Update(operationId, record =>
            {
                record.hasIdentityProbe = true;

                record.identityProbeElapsedMs = elapsedMs;
                record.candidateCollectionMs = candidateCollectionMs;
                record.portQueryMs = portQueryMs;
                record.commandLineQueryMs = commandLineQueryMs;
                record.candidateCount = candidateCount;
                record.verifiedCount = verifiedCount;
                record.hasCommandLineQueryExitCode = queryExitCode.HasValue;
                record.commandLineQueryExitCode = queryExitCode.GetValueOrDefault();
                record.commandLineQueryFailure = Bound(queryFailure);
                if (queryFailure == "timeout")
                    MarkGateCore(record, "old_identity", "failed", "process_identity_timeout",
                        $"候选 {candidateCount}，确认 {verifiedCount}；归属识别 {UPilotRestartDiagnosticView.Duration(1, elapsedMs + 1)}；未停止未核实的进程");
            });
        }

        internal static void RecordStopProgress(string operationId, bool? bridgeAttempted = null,
            bool? bridgeConfirmed = null, bool? serverAttempted = null, bool? exitConfirmed = null)
        {
            Update(operationId, record =>
            {
                if (bridgeAttempted == true) record.bridgeStopAttempted = true;
                if (bridgeConfirmed.HasValue)
                {
                    MarkGateCore(record, "bridge_stop", bridgeConfirmed.Value ? "passed" : "failed");
                    record.hasBridgeStopConfirmation = true;
                    record.bridgeStopConfirmed = bridgeConfirmed.Value;
                }
                if (serverAttempted == true) record.serverStopAttempted = true;
                if (exitConfirmed.HasValue)
                {
                    MarkGateCore(record, "old_exit", exitConfirmed.Value ? "passed" : "failed",
                        exitConfirmed.Value && record.oldProcessId <= 0 && !record.serverStopAttempted
                            ? "no_old_process" : exitConfirmed.Value ? "exit_verified" : "exit_unconfirmed",
                        exitConfirmed.Value && record.oldProcessId <= 0 && !record.serverStopAttempted
                            ? "未发现旧进程，无需停止" : exitConfirmed.Value ? "已确认旧 Server 退出" : "旧 Server 退出未确认");
                    record.hasOldServerExitConfirmation = true;
                    record.oldServerExitConfirmed = exitConfirmed.Value;
                }
            });
        }

        internal static void RecordBridgeRestore(string operationId, bool attempted, string result, string error)
        {
            Update(operationId, record =>
            {
                record.bridgeRestoreAttempted = attempted;
                record.bridgeRestoreResult = result;
                record.bridgeRestoreError = Bound(error);
            });
        }

        internal static void RecordPortsReleased(string operationId)
        {
            Update(operationId, record =>
            {
                record.phase = "ports_released";
                record.portsReleasedAtUtcMs = UtcNowMs();
                MarkGateCore(record, "ports", "passed");
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
                MarkGateCore(record, "new_process", "passed", "process_started", $"PID {processId}");
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
                if (string.IsNullOrWhiteSpace(record.healthProjectPath))
                    record.healthProjectPath = NormalizePath(healthProjectPath);
                record.healthVerifiedAtUtcMs = UtcNowMs();
                MarkGateCore(record, "health", "passed");
                MarkGateCore(record, "identity", record.projectIdentityVerified ? "passed" : "failed",
                    record.projectIdentityVerified ? "project_matched" : "project_mismatch", record.healthProjectPath);
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
                MarkGateCore(record, "bridge_session", "passed", "session_replaced", bridgeSessionId);
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

        internal static void RecordStop(string operationId, long generation, UPilotServerStopOrigin origin,
            string requestId, long requestedAtUtcMs, int targetProcessId, long targetCreatedAtTicks)
        {
            Update(operationId, record =>
            {
                if (record.restartGeneration != generation || record.status != "running") return;
                record.stopOrigin = origin.ToString();
                record.stopRequestId = requestId ?? "";
                record.stopRequestedAtUtcMs = requestedAtUtcMs;
                record.stopTargetProcessId = targetProcessId;
                record.stopTargetCreatedAtTicks = targetCreatedAtTicks;
                bool user = origin == UPilotServerStopOrigin.User;
                bool shutdown = origin == UPilotServerStopOrigin.EditorShutdown;
                var reason = user ? "MCP Server restart was interrupted by an explicit stop request."
                    : "MCP Server restart was superseded by " + origin + ".";
                MarkGateCore(record, FirstUnpassedGate(record), "canceled", user ? "user_stop" : "lifecycle_stop", reason);
                record.status = user ? "canceled" : "superseded";
                record.phase = record.status;
                record.errorCode = user ? "restart_canceled" : "restart_superseded";
                record.error = reason;
                record.nextAction = shutdown ? "Editor is shutting down; do not automatically restart."
                    : "Query current MCP status and identity before deciding whether recovery is needed.";
                record.endedAtUtcMs = UtcNowMs();
            });
        }

        internal static void RecordSupersedingOperation(string operationId, string successorId)
        {
            UpdateHistorical(operationId, r => r.supersededByOperationId = successorId ?? "");
        }

        private static void UpdateHistorical(string operationId, Action<UPilotServerRestartRecord> change)
        {
            lock (Sync)
            {
                EnsureLoadedLocked();
                var current = s_record;
                var record = current?.operationId == operationId ? current :
                    ReadHistoryAt(HistoryPath, out _).FirstOrDefault(r => r.operationId == operationId);
                if (record == null || record.status != "superseded") return;
                change(record);
                record.updatedAtUtcMs = UtcNowMs();
                if (record == current) PersistLocked();
                else if (!TryArchive(record, HistoryPath, out var error))
                    Logger.LogWarning("SERVER", "Superseded restart history could not be persisted: " + error);
            }
        }

        internal static void RecordSupersededRecoveryFailure(string operationId, string reason)
        {
            UpdateHistorical(operationId, r =>
            {
                if (r.subsequentVerifiedAtUtcMs == 0)
                    r.subsequentRecoveryStatus = "failed: " + Bound(reason);
            });
        }

        internal static void ObserveSupersededRecovery(string operationId, int processId, string instanceId,
            string projectPath, bool fullyVerified)
        {
            if (!fullyVerified || processId <= 0 || string.IsNullOrEmpty(instanceId)) return;
            UpdateHistorical(operationId, r =>
            {
                if (!SamePath(r.projectPath, projectPath) || r.subsequentVerifiedAtUtcMs > 0) return;
                r.subsequentRecoveryStatus = "verified";
                r.subsequentVerifiedAtUtcMs = UtcNowMs();
                r.subsequentPid = processId;
                r.subsequentInstanceId = instanceId;
            });
        }

        internal static void RecordCanceled(string operationId, string reason)
        {
            Update(operationId, record =>
            {
                MarkGateCore(record, FirstUnpassedGate(record), "canceled", "lifecycle_canceled", reason);
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
                record.deploymentVerifiedAtUtcMs = UtcNowMs();
                MarkGateCore(record, "deployment", "passed");
                record.phase = "deployment_verified";
                TryComplete(record);
            });
        }

        internal static void RecordReadOnlyVerified(string operationId)
        {
            Update(operationId, record =>
            {
                record.readOnlyVerified = true;
                record.readOnlyVerifiedAtUtcMs = UtcNowMs();
                MarkGateCore(record, "readonly", "passed");
                record.phase = "read_only_verified";
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
            var failedGate = FirstUnpassedGate(record);
            // A terminal verification deadline must not erase a more precise probe/round-trip failure.
            if (!(record.gateDiagnostics ?? Array.Empty<UPilotRestartGate>())
                .Any(g => g.key == failedGate && g.state == "failed" && !string.IsNullOrEmpty(g.evidenceCode)))
                MarkGateCore(record, failedGate, "failed", errorCode, error);
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

        internal static readonly string[] GateKeys = { "old_identity", "bridge_stop", "old_exit", "ports",
            "new_process", "health", "identity", "bridge_session", "deployment", "readonly" };

        internal static string FirstUnpassedGate(UPilotServerRestartRecord record) =>
            GateKeys.FirstOrDefault(key => !(record.gateDiagnostics ?? Array.Empty<UPilotRestartGate>())
                .Any(g => g.key == key && g.state == "passed")) ?? "readonly";

        internal static void ObserveRecovery(string operationId, int processId, string sessionId,
            string projectPath, bool healthVerified, bool deploymentVerified, bool readOnlyVerified)
        {
            lock (Sync)
            {
                EnsureLoadedLocked();
                var r = s_record;
                if (r == null || r.operationId != operationId ||
                    !IsEligibleRecovery(r, processId, sessionId, projectPath,
                        healthVerified, deploymentVerified, readOnlyVerified)) return;
                r.recoveredAtUtcMs = UtcNowMs();
                r.recoveryProcessId = processId;
                r.recoveryBridgeSessionId = sessionId;
                r.recoveryReadOnlyVerified = true;
                r.updatedAtUtcMs = r.recoveredAtUtcMs;
                PersistLocked();
            }
        }

        internal static bool IsEligibleRecovery(UPilotServerRestartRecord r, int processId,
            string sessionId, string projectPath, bool healthVerified, bool deploymentVerified, bool readOnlyVerified) =>
            r != null && r.status == "failed" && r.recoveredAtUtcMs == 0 &&
            (r.errorCode == "restart_verification_timeout" || r.errorCode == "SERVICE_RESTART_TIMEOUT") &&
            r.newProcessId > 0 && r.newProcessId == processId &&
            !string.IsNullOrEmpty(r.newBridgeSessionId) && r.newBridgeSessionId == sessionId &&
            SamePath(r.projectPath, projectPath) && healthVerified && deploymentVerified && readOnlyVerified;

        private static void EnsureLoadedLocked()
        {
            if (s_record != null) return;
            TryLoadRecord(RecordPath, out s_record, out _);
        }

        private static void PersistLocked()
        {
            if (s_record == null) return;
            if (!TryWriteRecord(s_record, RecordPath, out var error))
            {
                Logger.LogWarning("SERVER", "MCP Server restart record could not be persisted: " + error);
                return;
            }
            if (s_record.status == "running") return;
            if (!TryArchive(s_record, HistoryPath, out error))
                Logger.LogWarning("SERVER", "MCP Server restart history could not be persisted: " + error);
        }

        internal static bool TryArchive(UPilotServerRestartRecord record, string path, out string error)
        {
            var history = ReadHistoryAt(path, out error);
            if (!string.IsNullOrEmpty(error))
            {
                // Preserve the original and a stable copy; never silently replace corrupt evidence.
                try
                {
                    var backup = path + ".corrupt";
                    if (File.Exists(path) && !File.Exists(backup)) File.Copy(path, backup);
                    error += File.Exists(backup) ? "；备份：" + backup : "";
                }
                catch (Exception ex) { error += "；备份失败：" + ex.Message; }
                return false;
            }
            var updated = new UPilotRestartHistory
            {
                records = history.Where(r => r.operationId != record.operationId).Append(record)
                    .OrderByDescending(r => r.requestedAtUtcMs).Take(10).ToArray()
            };
            return TryWriteJson(JsonUtility.ToJson(updated, true), path, out error);
        }

        private static bool TryWriteRecord(UPilotServerRestartRecord record, string path, out string error)
        {
            return TryWriteJson(JsonUtility.ToJson(record, true), path, out error);
        }

        private static bool TryWriteJson(string json, string path, out string error)
        {
            var temporary = "";
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, json, Utf8NoBom);
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
                var json = File.ReadAllText(path, Encoding.UTF8);
                record = JsonUtility.FromJson<UPilotServerRestartRecord>(json);
                if (record != null && !json.Contains("\"schemaVersion\"")) record.schemaVersion = 1;
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
            TryComplete(record);
        }

        internal static void RecordReadOnlyVerifiedForTests(UPilotServerRestartRecord record)
        {
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
    // One formatter is shared by the main window, advanced diagnostics, and failure dialog.
    internal static class UPilotRestartDiagnosticView
    {
        private static readonly string[] Labels = { "旧进程归属确认", "旧 Bridge 停止确认", "旧 Server 退出确认",
            "端口释放确认", "新 Server 进程启动", "/health 成功响应", "PID 与项目身份验证",
            "新 Bridge 会话验证", "部署版本、渠道、入口验证", "实际只读往返验证" };

        internal static string Time(long utcMs, bool copied = false, TimeZoneInfo zone = null)
        {
            if (utcMs <= 0) return "未知";
            zone ??= TimeZoneInfo.Local;
            var time = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(utcMs), zone);
            return time.ToString(copied ? "yyyy-MM-dd HH:mm:ss zzz" : "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string Duration(long start, long end)
        {
            if (start <= 0 || end < start) return "未知";
            var seconds = (end - start) / 1000d;
            if (seconds < 1) return "<1 秒";
            return seconds.ToString(seconds == Math.Floor(seconds) ? "0" : "0.0",
                System.Globalization.CultureInfo.InvariantCulture) + " 秒";
        }

        internal static string ZoneLabel(TimeZoneInfo zone = null)
        {
            var offset = (zone ?? TimeZoneInfo.Local).GetUtcOffset(DateTime.Now);
            return "时间：本机时区 UTC" + (offset < TimeSpan.Zero ? "-" : "+") +
                offset.Duration().ToString(@"hh\:mm");
        }

        internal static string Result(UPilotServerRestartRecord record) => record == null ? "无重启记录" :
            record.status == "superseded" ?
                (record.subsequentVerifiedAtUtcMs > 0 ? "生命周期替代（服务随后恢复）" :
                    (!string.IsNullOrEmpty(record.subsequentRecoveryStatus) && record.subsequentRecoveryStatus.StartsWith("failed:")
                        ? "生命周期替代（后续恢复失败）" : "生命周期替代，等待恢复")) :
            record.recoveredAtUtcMs > 0 ? "失败（随后恢复）" : record.status switch
            {
                "succeeded" => "成功", "failed" => "失败", "canceled" => "已取消", _ => "进行中"
            };

        // Schema 1 has no gate timeline. Adapt only explicit positive observations.
        internal static UPilotRestartGate LegacyGate(UPilotServerRestartRecord r, string key)
        {
            if (r == null || r.schemaVersion >= 2) return null;
            bool observed;
            long at = 0;
            switch (key)
            {
                case "bridge_stop": observed = r.hasBridgeStopConfirmation && r.bridgeStopConfirmed; break;
                case "old_exit": observed = r.hasOldServerExitConfirmation && r.oldServerExitConfirmed; break;
                case "ports": observed = r.portsReleasedAtUtcMs > 0; at = r.portsReleasedAtUtcMs; break;
                case "new_process": observed = r.newProcessId > 0 && r.newProcessStartedAtUtcMs > 0;
                    at = r.newProcessStartedAtUtcMs; break;
                case "health": observed = r.healthVerified; at = r.healthVerifiedAtUtcMs; break;
                case "identity": observed = r.projectIdentityVerified; break;
                case "bridge_session": observed = r.bridgeVerified; at = r.bridgeVerifiedAtUtcMs; break;
                default: observed = false; break;
            }
            return observed ? new UPilotRestartGate { key = key, state = "passed", endedAtUtcMs = at,
                evidenceCode = "legacy_explicit", evidence = "旧版记录的明确通过证据，开始/耗时未知" } : null;
        }

        private static UPilotRestartGate DisplayGate(UPilotServerRestartRecord r, string key) =>
            Array.Find(r.gateDiagnostics ?? Array.Empty<UPilotRestartGate>(), g => g.key == key) ?? LegacyGate(r, key);

        internal static string GateState(UPilotRestartGate gate) => gate?.state switch
        {
            "passed" => "✓ 通过", "failed" => "✗ 失败", "running" => "… 检查中",
            "canceled" => "— 取消", "pending" => "· 未开始", _ => "? 未知"
        };

        internal static string FirstPending(UPilotServerRestartRecord record) =>
            record == null || record.schemaVersion < 2 ? "未知" :
                record.status == "succeeded" ? "无" :
                Label(UPilotServerRestartDiagnostics.FirstUnpassedGate(record));

        internal static string LastPassed(UPilotServerRestartRecord record)
        {
            if (record == null) return "未知";
            for (var i = UPilotServerRestartDiagnostics.GateKeys.Length - 1; i >= 0; i--)
                if (DisplayGate(record, UPilotServerRestartDiagnostics.GateKeys[i])?.state == "passed")
                    return Labels[i];
            return "无";
        }

        internal static string Label(string key)
        {
            var index = Array.IndexOf(UPilotServerRestartDiagnostics.GateKeys, key);
            return index >= 0 ? Labels[index] : "未知";
        }

        internal static string FailureCategory(UPilotServerRestartRecord r)
        {
            if (r == null || r.status == "succeeded") return "无";
            var failed = (r.gateDiagnostics ?? Array.Empty<UPilotRestartGate>())
                .FirstOrDefault(g => g.key == "readonly" && g.state == "failed" &&
                    (g.evidenceCode == "readonly_timeout" || g.evidenceCode == "readonly_mismatch"));
            if (failed != null) return failed.evidenceCode;
            var bridgeFailed = (r.gateDiagnostics ?? Array.Empty<UPilotRestartGate>())
                .FirstOrDefault(g => g.key == "bridge_session" && g.state == "failed" &&
                    (g.evidenceCode == "bridge_unavailable" || g.evidenceCode == "bridge_session_mismatch"));
            if (bridgeFailed != null) return bridgeFailed.evidenceCode;
            if ((string.IsNullOrEmpty(r.errorCode) || r.errorCode == "restart_verification_timeout" ||
                r.errorCode == "SERVICE_RESTART_TIMEOUT") &&
                r.lastBridgeCloseCode == "1009") return "websocket_close_1009";
            if ((string.IsNullOrEmpty(r.errorCode) || r.errorCode == "restart_verification_timeout" ||
                r.errorCode == "SERVICE_RESTART_TIMEOUT") &&
                r.lastBridgeOversizeActualBytes > 0) return "payload_oversize";
            if (!string.IsNullOrEmpty(r.statusProbeOutcome) &&
                r.statusProbeOutcome != "success" && r.statusProbeOutcome != "running")
                return r.statusProbeOutcome;
            return string.IsNullOrEmpty(r.errorCode) ? "unknown" : r.errorCode;
        }

        internal static string NextAction(UPilotServerRestartRecord r)
        {
            if (r == null) return "检查当前 Server 状态。";
            if (r.status == "canceled") return "用户主动停止；不会自动恢复。";
            if (r.status == "superseded") return r.subsequentVerifiedAtUtcMs > 0
                ? "服务随后通过完整验证；原操作仍为生命周期替代。"
                : (!string.IsNullOrEmpty(r.subsequentRecoveryStatus) && r.subsequentRecoveryStatus.StartsWith("failed:")
                    ? "后续恢复失败：" + r.subsequentRecoveryStatus.Substring(7)
                    : "等待包生命周期结束后检查当前状态并按需恢复。");
            if (r.recoveredAtUtcMs > 0) return "服务随后恢复；原中断任务不会自动重放。";
            var category = FailureCategory(r);
            if (r.status == "succeeded") return "无需操作。";
            if (category == "lifecycle_canceled") return "等待 Editor 生命周期操作结束，然后重新检查状态。";
            if (r.errorCode == "process_identity_timeout") return "核对候选 PID 和命令行，不要强制结束未核实进程。";
            if (r.errorCode == "deployment_mismatch") return "检查版本、渠道、入口与模块来源。";
            if (category == "project_identity_mismatch" || r.errorCode == "project_identity_mismatch") return "连接正确的项目端点，核对期望与实际项目完整路径。";
            if (category == "bridge_unavailable" || category == "bridge_session_mismatch")
                return "检查 Bridge 认证、会话 ID 和断开诊断；已连接不等于完成只读验证。";
            if (r.errorCode == "port_release_timeout") return "核对端口和已确认归属的 PID；不要结束未核实进程。";
            if (category == "websocket_close_1009" || category == "payload_oversize")
                return "Bridge 消息超限；缩小结果或修复分片，不要盲目重复重启。";
            var firstFailed = (r.gateDiagnostics ?? Array.Empty<UPilotRestartGate>())
                .FirstOrDefault(g => g.key == "readonly" && g.state == "failed");
            if (category == "readonly_timeout" || category == "readonly_mismatch" ||
                firstFailed?.evidenceCode == "readonly_timeout" || firstFailed?.evidenceCode == "readonly_mismatch")
                return "Bridge 会话已连接，但真实只读往返未通过；检查 Bridge 与 Server 日志。";
            if (category == "request_timeout" || r.statusProbeOutcome == "request_timeout") return "检查第一个未通过验证门及 Server 日志。";
            return string.IsNullOrWhiteSpace(r.nextAction) ? "检查 Server 日志与第一个未通过的验证门。" : r.nextAction;
        }

        private static string BytesOrUnknown(int bytes) => bytes > 0
            ? bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " 字节" : "未知";

        internal static string Summary(UPilotServerRestartRecord r, bool copied = false)
        {
            if (r == null) return "无服务重启记录";
            var sb = new StringBuilder();
            sb.AppendLine(ZoneLabel());
            sb.AppendLine("结果：" + Result(r) + "  阶段：" + (r.failurePhase ?? r.phase));
            sb.AppendLine("操作：" + r.operationId + (r.restartGeneration > 0 ? "  代次：" + r.restartGeneration : ""));
            if (!string.IsNullOrEmpty(r.stopOrigin))
                sb.AppendLine("停止来源：" + r.stopOrigin + "  请求：" + r.stopRequestId +
                    "  时间：" + Time(r.stopRequestedAtUtcMs, copied) +
                    "  目标 PID：" + r.stopTargetProcessId + "  创建 ticks：" + r.stopTargetCreatedAtTicks);
            if (!string.IsNullOrEmpty(r.supersededByOperationId))
                sb.AppendLine("后续修复操作：" + r.supersededByOperationId);
            if (r.subsequentVerifiedAtUtcMs > 0)
                sb.AppendLine("服务随后恢复：" + Time(r.subsequentVerifiedAtUtcMs, copied) +
                    "  PID：" + r.subsequentPid + "  实例：" + r.subsequentInstanceId);
            sb.AppendLine("请求：" + Time(r.requestedAtUtcMs, copied) + (r.status == "failed" ? "  失败时间：" : "  结束：") + Time(r.endedAtUtcMs, copied) +
                "  耗时：" + Duration(r.requestedAtUtcMs, r.endedAtUtcMs));
            sb.AppendLine("最后更新：" + Time(r.updatedAtUtcMs, copied));
            if (r.recoveredAtUtcMs > 0) sb.AppendLine("随后恢复：" + Time(r.recoveredAtUtcMs, copied) +
                "  恢复耗时：" + Duration(r.endedAtUtcMs, r.recoveredAtUtcMs));
            sb.AppendLine("最后通过：" + LastPassed(r) + "  首个未通过：" + FirstPending(r));
            sb.AppendLine("分类：" + FailureCategory(r));
            if (r.lastBridgeCloseCode == "1009" || r.lastBridgeOversizeActualBytes > 0)
                sb.AppendLine("Bridge 消息超过限制：close code " + (r.lastBridgeCloseCode ?? "未知") +
                    "；实际 " + BytesOrUnknown(r.lastBridgeOversizeActualBytes) +
                    "；当时上限 " + BytesOrUnknown(r.lastBridgeOversizeLimitBytes) +
                    "；来源 " + (r.lastBridgeOversizeSource ?? "未知"));
            sb.AppendLine("原始错误：" + (r.error ?? "无"));
            sb.AppendLine("下一步：" + NextAction(r));
            if (r.schemaVersion < 2) sb.AppendLine("旧版记录：部分验证时间或证据不可用。未按阶段推测验证结果。");
            return sb.ToString();
        }

        internal static string Gates(UPilotServerRestartRecord r, bool copied = false)
        {
            if (r == null) return "";
            var sb = new StringBuilder("验证门（状态 | 项目 | 发生时间 | 耗时 | 证据）\n");
            foreach (var key in UPilotServerRestartDiagnostics.GateKeys)
            {
                var gate = DisplayGate(r, key);
                sb.AppendLine((gate == null && r.schemaVersion >= 2 ? "· 未开始" : GateState(gate)) + " | " + Label(key) + " | " +
                    Time(gate?.endedAtUtcMs > 0 ? gate.endedAtUtcMs : gate?.startedAtUtcMs ?? 0, copied) +
                    " | " + Duration(gate?.startedAtUtcMs ?? 0, gate?.endedAtUtcMs ?? 0) +
                    " | 开始 " + Time(gate?.startedAtUtcMs ?? 0, copied) +
                    "；结束 " + Time(gate?.endedAtUtcMs ?? 0, copied) +
                    "；" + (gate?.evidenceCode ?? "") + " " + (gate?.evidence ?? ""));
            }
            return sb.ToString();
        }

        internal static void DrawGateTable(UPilotServerRestartRecord r, bool detailed = false)
        {
            if (r == null) return;
            EditorGUILayout.LabelField("验证门", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("状态", EditorStyles.miniBoldLabel, GUILayout.Width(65));
                GUILayout.Label("检查项", EditorStyles.miniBoldLabel, GUILayout.Width(138));
                GUILayout.Label("发生时间", EditorStyles.miniBoldLabel, GUILayout.Width(135));
                GUILayout.Label("耗时", EditorStyles.miniBoldLabel, GUILayout.Width(55));
                GUILayout.Label("证据或原因", EditorStyles.miniBoldLabel);
            }
            foreach (var key in UPilotServerRestartDiagnostics.GateKeys)
            {
                var gate = DisplayGate(r, key);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(gate == null && r.schemaVersion >= 2 ? "· 未开始" : GateState(gate),
                        EditorStyles.miniLabel, GUILayout.Width(65));
                    GUILayout.Label(Label(key), EditorStyles.miniLabel, GUILayout.Width(138));
                    GUILayout.Label(Time(gate?.endedAtUtcMs > 0 ? gate.endedAtUtcMs : gate?.startedAtUtcMs ?? 0),
                        EditorStyles.miniLabel, GUILayout.Width(135));
                    GUILayout.Label(Duration(gate?.startedAtUtcMs ?? 0, gate?.endedAtUtcMs ?? 0),
                        EditorStyles.miniLabel, GUILayout.Width(55));
                    GUILayout.Label((detailed ? "开始 " + Time(gate?.startedAtUtcMs ?? 0) +
                        "；结束 " + Time(gate?.endedAtUtcMs ?? 0) + "；" : "") +
                        (gate?.evidenceCode ?? "") + " " + (gate?.evidence ?? ""),
                        EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        internal static string Identity(UPilotServerRestartRecord r)
        {
            if (r == null) return "";
            var sb = new StringBuilder("身份（项目 | 期望值 | 实际值 | 判断）\n");
            void Row(string name, string expected, string actual, string verdict)
            {
                sb.AppendLine(name + " | " + (string.IsNullOrWhiteSpace(expected) ? "未知" : expected) + " | " +
                    (string.IsNullOrWhiteSpace(actual) ? "未知" : actual) + " | " + verdict);
            }
            var checkedIdentity = r.gateDiagnostics?.Any(g => g.key == "identity" && g.state == "passed") == true ||
                (r.schemaVersion < 2 && r.projectIdentityVerified);
            var checkedDeployment = r.gateDiagnostics?.Any(g => g.key == "deployment" && g.state == "passed") == true;
            Row("项目路径", r.projectPath, r.healthProjectPath, checkedIdentity ? "通过" : "未知/未通过");
            Row("Bridge 版本", r.bridgeVersion, r.bridgeVersion, "重启请求时本地记录");
            Row("Bridge 渠道", r.bridgeChannel, r.bridgeChannel, "重启请求时本地记录");
            Row("Bridge 安装方式", r.bridgeInstallSource, r.bridgeInstallSource, "重启请求时本地记录");
            Row("Server 版本", r.bridgeVersion, r.serverVersion, checkedDeployment ? "通过" : "未知/未通过");
            Row("Server 渠道", r.bridgeChannel, r.serverChannel, checkedDeployment ? "通过" : "未知/未通过");
            Row("Server 安装来源", r.bridgeInstallSource, r.serverInstallSource, checkedDeployment ? "通过" : "未知/未通过");
            Row("当前包路径", r.packageRoot, r.packageRoot, "重启请求时本地记录");
            Row("入口", r.expectedEntry, r.actualEntry, checkedDeployment ? "通过" : "未知/未通过");
            Row("模块", string.IsNullOrWhiteSpace(r.packageRoot) ? null :
                Path.Combine(r.packageRoot, "upilotserver~", "src", "upilot_mcp"),
                r.actualModule, checkedDeployment ? "通过" : "未知/未通过");
            Row("main", r.bridgeIsMain.ToString(), r.bridgeIsMain.ToString(), "重启请求时本地记录");
            Row("PID", r.newProcessId > 0 ? r.newProcessId.ToString() : "未知",
                r.healthProcessId > 0 ? r.healthProcessId.ToString() : "未知",
                checkedIdentity ? "通过" : "未知/未通过");
            Row("重启 operation ID", r.operationId, r.operationId, "记录身份");
            Row("Bridge session ID", r.oldBridgeSessionId, r.newBridgeSessionId, r.bridgeVerified ? "新会话" : "未知");
            return sb.ToString();
        }

        internal static string Full(UPilotServerRestartRecord r, bool copied = false)
        {
            if (r == null) return "无服务重启记录";
            return Summary(r, copied) + "\n" + Gates(r, copied) + "\n" + Identity(r) + Details(r, copied);
        }

        internal static string Details(UPilotServerRestartRecord r, bool copied = false)
        {
            if (r == null) return "";
            return "\nstatus probe：" + r.statusProbeCount + " 次；结果=" + (r.statusProbeOutcome ?? "未知") +
                "；阶段=" + (r.statusProbeFailureStage ?? "未知") + "；取消=" + (r.statusProbeCancellationReason ?? "无") +
                "\n最后 probe：" + Time(r.statusProbeStartedAtUtcMs, copied) + " → " +
                Time(r.statusProbeEndedAtUtcMs, copied) + "（" + Duration(r.statusProbeStartedAtUtcMs, r.statusProbeEndedAtUtcMs) + "）" +
                "\nprobe 错误：" + (r.statusProbeError ?? "") +
                "\n归属识别：" + (r.hasIdentityProbe ? Duration(1, r.identityProbeElapsedMs + 1) : "未知") + "；候选=" + r.candidateCount +
                "；确认=" + r.verifiedCount + "；候选收集=" +
                (r.hasIdentityProbe ? Duration(1, r.candidateCollectionMs + 1) : "未知") + "；端口查询=" +
                (r.hasIdentityProbe ? Duration(1, r.portQueryMs + 1) : "未知") +
                "；命令行查询=" + (r.hasIdentityProbe ? Duration(1, r.commandLineQueryMs + 1) : "未知") +
                "\nBridge：连接=" + Time(r.lastBridgeConnectedAtUtcMs, copied) +
                "；认证=" + Time(r.lastBridgeAuthenticatedAtUtcMs, copied) +
                "；断开=" + Time(r.lastBridgeDisconnectedAtUtcMs, copied) +
                "；close=" + (r.lastBridgeCloseCode ?? "未知") + " " + (r.lastBridgeCloseReason ?? "") +
                "\n超大消息：" + Time(r.lastBridgeOversizeAtUtcMs, copied) + "；来源=" + (r.lastBridgeOversizeSource ?? "未知") +
                "；实际=" + BytesOrUnknown(r.lastBridgeOversizeActualBytes) + "；上限=" + BytesOrUnknown(r.lastBridgeOversizeLimitBytes) +
                "；次数=" + r.bridgeOversizeCount +
                "\n恢复身份：PID " + (r.recoveredAtUtcMs > 0 ? r.recoveryProcessId.ToString() : "未知") +
                "；Bridge session=" + (r.recoveryBridgeSessionId ?? "未知") +
                "；只读验证=" + (r.recoveryReadOnlyVerified ? "通过" : "未知") +
                "\n原始错误：" + (r.error ?? "无") + "\n";
        }
    }

}
