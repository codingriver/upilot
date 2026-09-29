// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    internal sealed class ServiceRestartRequest
    {
        public string maintenanceId, target, reason, expectedProjectPath, expectedBridgeSessionId;
        public string expectedMaintenanceId = "";
        public int expectedServerProcessId;
    }

    [Serializable]
    internal sealed class ServiceRestartMessage { public ServiceRestartRequest payload; }

    [Serializable]
    internal sealed class ServiceMaintenanceRecord
    {
        public int schemaVersion = 1;
        public string maintenanceId, projectPath, target, reason, expectedMaintenanceId;
        public string status = "accepted", phase = "accepted", errorCode = "", error = "", failurePhase = "";
        public string nextAction = "Reconnect and inspect aiServiceMaintenance; do not replay this request.";
        public string oldBridgeSessionId, newBridgeSessionId = "", serverRestartId = "";
        public int unityProcessId, oldServerProcessId, newServerProcessId, restartTimeoutSeconds;
        public long acceptedAtUtcMs, deadlineAtUtcMs, endedAtUtcMs;
        public bool stopAttempted, startAttempted, readOnlyVerified;
        public bool healthVerified;
        public int healthProbeCount;
        public long healthProbeStartedAtUtcMs, healthProbeElapsedMs;
        public string healthProbeError = "";
        public bool affectedWorkComplete;
        public bool humanConfirmed, queueReset;
        public string queueResetError = "";
        public bool underlyingExecutionStopped; // Not asserted by resetting UPilot.
        public string[] affectedCommandIds = Array.Empty<string>();
        public string[] affectedComponents;
        public bool Active => status == "accepted" || status == "running";

        internal bool Expire(long now)
        {
            if (!Active || now < deadlineAtUtcMs) return false;
            Finish("timed_out", "SERVICE_RESTART_TIMEOUT",
                phase == "health_query" && !healthVerified && deadlineAtUtcMs - acceptedAtUtcMs == 600000
                    ? UPilotServerRestartDiagnostics.HealthTimeoutMessage + " 最近错误：" + healthProbeError
                    : "重启验证期限已到；未通过：" + phase + "。最近错误：" + healthProbeError, now);
            return true;
        }

        internal void Finish(string terminal, string code, string message, long now)
        {
            if (!Active) return;
            // A late success cannot turn an expired operation into a successful one.
            if (terminal == "succeeded" && Expire(now)) return;
            failurePhase = terminal == "succeeded" ? "" : phase;
            status = terminal;
            errorCode = code;
            error = message;
            endedAtUtcMs = now;
            nextAction = terminal == "succeeded" ? "Refresh the client tool list if needed; do not replay business."
                : "Inspect current service identity and the original task results before any new maintenance.";
        }
    }

    internal sealed class ServiceMaintenanceException : Exception
    {
        internal readonly string Code;
        internal ServiceMaintenanceException(string code, string message) : base(message) { Code = code; }
    }

    // The journal and deadline rules are independent of Unity so tests never stop real services.
    internal sealed class ServiceMaintenanceJournal
    {
        private readonly string _path;
        private readonly Func<long> _now;
        internal ServiceMaintenanceRecord Current { get; private set; }
        internal string LoadError { get; private set; }

        internal ServiceMaintenanceJournal(string path, Func<long> now)
        {
            _path = path;
            _now = now;
            try
            {
                if (!File.Exists(path)) return;
                Current = JsonUtility.FromJson<ServiceMaintenanceRecord>(File.ReadAllText(path));
                if (Current == null || Current.schemaVersion != 1 || string.IsNullOrEmpty(Current.maintenanceId) ||
                    Current.deadlineAtUtcMs <= Current.acceptedAtUtcMs ||
                    !new[] { "accepted", "running", "succeeded", "failed", "timed_out" }.Contains(Current.status))
                    throw new InvalidDataException("Invalid maintenance record.");
            }
            catch (Exception ex) { Current = null; LoadError = ex.Message; }
        }

        internal ServiceMaintenanceRecord FindDuplicate(ServiceRestartRequest request)
        {
            if (LoadError != null)
                throw new ServiceMaintenanceException("SERVICE_RESTART_RECOVERY_REQUIRED", LoadError);
            if (Current?.maintenanceId != request.maintenanceId) return null;
            if (Current.target != request.target || Current.reason != request.reason ||
                Current.expectedMaintenanceId != request.expectedMaintenanceId ||
                !SamePath(Current.projectPath, request.expectedProjectPath) ||
                Current.oldServerProcessId != request.expectedServerProcessId ||
                Current.oldBridgeSessionId != request.expectedBridgeSessionId)
                throw new ServiceMaintenanceException("SERVICE_RESTART_REQUEST_CONFLICT", "Maintenance ID was reused with different arguments.");
            return Current;
        }

        internal ServiceMaintenanceRecord Accept(ServiceRestartRequest request, int timeout, int unityPid, string[] affected, bool humanConfirmed = false)
        {
            // A human-confirmed emergency reset must not depend on a readable old archive.
            // MCP requests still require the exact previously observed identity.
            var archiveError = LoadError;
            if (humanConfirmed && LoadError != null) { LoadError = null; Current = null; }
            var duplicate = FindDuplicate(request);
            if (duplicate != null) return duplicate;
            if (Current?.Active == true)
                return Current; // Project-local concurrent requests share the original deadline and dispatch.
            // Compare-and-set prevents an old request from becoming a new restart after journal replacement.
            if ((request.expectedMaintenanceId ?? "") != (Current?.maintenanceId ?? ""))
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Latest maintenance identity changed; inspect status.");
            var now = _now();
            timeout = Math.Max(30, Math.Min(600, timeout));
            var record = new ServiceMaintenanceRecord
            {
                maintenanceId = request.maintenanceId, projectPath = Path.GetFullPath(request.expectedProjectPath),
                target = request.target, reason = request.reason, expectedMaintenanceId = request.expectedMaintenanceId ?? "",
                oldServerProcessId = request.expectedServerProcessId, oldBridgeSessionId = request.expectedBridgeSessionId,
                unityProcessId = unityPid, restartTimeoutSeconds = timeout, acceptedAtUtcMs = now,
                deadlineAtUtcMs = checked(now + timeout * 1000L), affectedCommandIds = affected ?? Array.Empty<string>(),
                affectedWorkComplete = false, humanConfirmed = humanConfirmed,
                queueResetError = archiveError == null ? "" : "Previous journal: " + archiveError,
                affectedComponents = new[] { "server", "bridge" },
            };
            Current = record;
            Save();
            return record;
        }

        internal bool Expire()
        {
            if (Current?.Expire(_now()) != true) return false;
            Save();
            return true;
        }

        internal void Save()
        {
            if (Current == null) return;
            try { Write(Current); }
            catch (Exception ex) { Current.queueResetError = "Journal: " + ex.Message; }
        }

        internal bool Dispatch(Action validate, Action<ServiceMaintenanceRecord> execute)
        {
            var record = Current;
            if (record?.status != "accepted" || Expire()) return false;
            validate();
            if (Expire()) return false;
            record.status = "running";
            record.phase = "stop_start";
            record.stopAttempted = true;
            Save();
            execute(record);
            return true;
        }

        private void Write(ServiceMaintenanceRecord record)
        {
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(temp, JsonUtility.ToJson(record, true));
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                throw new ServiceMaintenanceException("SERVICE_RESTART_PERSIST_FAILED", ex.Message);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { /* Preserve the original persistence failure. */ }
            }
        }

        internal static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            try { return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        internal static UPilotAiServiceMaintenanceConfig ParseSettings(string json)
        {
            try
            {
                using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max);
                var root = XElement.Load(reader);
                if ((string)root.Attribute("type") != "object") throw new FormatException("Configuration must be an object.");
                if (root.Elements("aiServiceMaintenance").Count() > 1) throw new FormatException("Duplicate maintenance configuration.");
                var section = root.Element("aiServiceMaintenance");
                if (section == null) return new UPilotAiServiceMaintenanceConfig();
                if ((string)section.Attribute("type") != "object") throw new FormatException("aiServiceMaintenance must be an object.");
                if (section.Elements().GroupBy(x => x.Name).Any(x => x.Count() > 1)) throw new FormatException("Duplicate maintenance setting.");
                var timeout = section.Element("restartTimeoutSeconds");
                var seconds = 120;
                if (timeout != null && ((string)timeout.Attribute("type") != "number" ||
                    !int.TryParse(timeout.Value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 30 || seconds > 600))
                    throw new FormatException("restartTimeoutSeconds must be an integer between 30 and 600.");
                var automatic = section.Element("autoHardStopOnSoftFailure");
                if (automatic != null && (string)automatic.Attribute("type") != "boolean")
                    throw new FormatException("autoHardStopOnSoftFailure must be a boolean.");
                var approved = section.Element("approved");
                if (approved != null && (string)approved.Attribute("type") != "boolean")
                    throw new FormatException("approved must be a boolean.");
                foreach (var name in new[] { "projectPath", "approvedAtUtc" })
                    if (section.Element(name) != null && (string)section.Element(name).Attribute("type") != "string")
                        throw new FormatException(name + " must be a string.");
                return new UPilotAiServiceMaintenanceConfig
                {
                    approved = approved != null && approved.Value == "true",
                    projectPath = (string)section.Element("projectPath") ?? "",
                    approvedAtUtc = (string)section.Element("approvedAtUtc") ?? "",
                    restartTimeoutSeconds = seconds,
                    autoHardStopOnSoftFailure = section.Element("autoHardStopOnSoftFailure")?.Value == "true",
                };
            }
            catch (Exception ex) { throw new ServiceMaintenanceException("SERVICE_MAINTENANCE_CONFIG_INVALID", ex.Message); }
        }

        internal static void RequireAuthorization(UPilotAiServiceMaintenanceConfig config, string project)
        {
            if (!config.approved || !SamePath(config.projectPath, project))
                throw new ServiceMaintenanceException("SERVICE_MAINTENANCE_NOT_APPROVED",
                    "Enable AI service maintenance in this project's UPilot settings. Do not edit config to self-authorize.");
        }
    }

    [InitializeOnLoad]
    internal static class UPilotServiceMaintenance
    {
        private static ServiceMaintenanceJournal _journal;
        private static bool _executing, _probeRunning, _recoveryChecked;
        private static long _dispatchAfter, _nextProbe;
        private static CancellationTokenSource _probeCancellation;
        private static string _storageError = "";
        internal static string RecordPath => Path.Combine(UPilotProjectConfig.ProjectRoot, "Library", "UPilot", "service-maintenance.json");
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static ServiceMaintenanceJournal Journal => _journal ??= new ServiceMaintenanceJournal(RecordPath, () => Now);
        internal static ServiceMaintenanceRecord Current => Journal.Current;
        internal static bool IsActive => Current?.Active == true;
        internal static bool IsExecuting => _executing;
        internal static string StorageError => _storageError.Length > 0 ? _storageError : Journal.LoadError ?? "";

        static UPilotServiceMaintenance()
        {
            if (!UPilotBridge.IsMainEditorProcess()) return;
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += () => _probeCancellation?.Cancel();
            EditorApplication.quitting += () => _probeCancellation?.Cancel();
            EditorApplication.delayCall += Recover;
        }

        internal static UPilotAiServiceMaintenanceConfig ReadSettings()
        {
            try
            {
                return ServiceMaintenanceJournal.ParseSettings(File.Exists(UPilotProjectConfig.ConfigPath)
                    ? File.ReadAllText(UPilotProjectConfig.ConfigPath) : "{}");
            }
            catch (ServiceMaintenanceException) { throw; }
            catch (Exception ex) { throw new ServiceMaintenanceException("SERVICE_MAINTENANCE_CONFIG_INVALID", ex.Message); }
        }

        internal static void Register(UPilotBridge bridge)
        {
            bridge.Router.Register(new CommandDescriptor("service.restart", "service", false, true, "blocked"),
                (id, json, token) => Handle(bridge, id, json, token));
        }

        private static async Task Handle(UPilotBridge bridge, string id, string json, CancellationToken token)
        {
            var completion = new TaskCompletionSource<ServiceMaintenanceRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.EnqueueControl(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!_recoveryChecked)
                        throw new ServiceMaintenanceException("SERVICE_RESTART_RECOVERY_REQUIRED", "Maintenance journal is recovering or could not be persisted.");
                    var request = JsonUtility.FromJson<ServiceRestartMessage>(json)?.payload;
                    if (request == null || !Guid.TryParseExact(request.maintenanceId, "D", out _) ||
                        (request.target != "bridge" && request.target != "server") ||
                        string.IsNullOrWhiteSpace(request.reason) || request.reason.Length > 512 ||
                        request.expectedServerProcessId <= 0 || string.IsNullOrWhiteSpace(request.expectedBridgeSessionId))
                        throw new ServiceMaintenanceException("INVALID_PAYLOAD", "Supply a UUID maintenanceId, target, bounded reason and exact service identities.");
                    var duplicate = Journal.FindDuplicate(request);
                    if (duplicate != null) { completion.SetResult(duplicate); return; }
                    RequireGate(request);
                    if (IsActive) { completion.SetResult(Current); return; }
                    if (UPilotQuickStart.IsRepairing || UPilotMcpServerManager.Instance.IsServiceTransitionActive)
                        throw new ServiceMaintenanceException("SERVICE_RESTART_BUSY", "A manual restart or automatic repair is active.");
                    var affected = UPilotOperationTracker.Instance.GetEntriesCopy()
                        .Where(x => !x.CompletedAt.HasValue && x.CommandId != id).Select(x => x.CommandId).Take(64).ToArray();
                    var settings = ReadSettings();
                    var result = Journal.Accept(request, settings.restartTimeoutSeconds,
                        System.Diagnostics.Process.GetCurrentProcess().Id, affected);
                    _dispatchAfter = Now + 500;
                    completion.SetResult(result);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            try { await bridge.SendResultAsync(id, "service.restart", await completion.Task, token); }
            catch (ServiceMaintenanceException ex)
            {
                await bridge.SendErrorAsync(id, ex.Code, ex.Message, token, "service.restart",
                    new ErrorDetailPayload
                    {
                        stage = Current?.phase ?? "preflight",
                        sideEffectsMayHaveOccurred = false,
                        nextAction = "Inspect aiServiceMaintenance in unity_mcp_status; do not replay the request.",
                    });
            }
            catch (Exception ex)
            {
                await bridge.SendErrorAsync(id, "SERVICE_RESTART_REQUEST_FAILED", ex.Message, token, "service.restart");
            }
        }

        // Human confirmation is supplied only by the local settings button, never by MCP payload.
        internal static ServiceMaintenanceRecord HardStopFromSettings()
        {
            if (IsActive) return Current;
            var bridge = UPilotBridge.Instance;
            var manager = UPilotMcpServerManager.Instance;
            int timeout = 120;
            try { timeout = ReadSettings().restartTimeoutSeconds; } catch { /* Human control remains available. */ }
            var record = Journal.Accept(new ServiceRestartRequest
            {
                maintenanceId = Guid.NewGuid().ToString("D"), target = "server",
                reason = "Human-confirmed project hard stop", expectedProjectPath = UPilotProjectConfig.ProjectRoot,
                expectedBridgeSessionId = bridge.GetStatus().SessionId ?? "",
                expectedServerProcessId = manager.GetStatus().ProcessId ?? 0,
                expectedMaintenanceId = Current?.maintenanceId ?? "",
            }, timeout, System.Diagnostics.Process.GetCurrentProcess().Id, Array.Empty<string>(), humanConfirmed: true);
            record.humanConfirmed = true;
            Journal.Save();
            _recoveryChecked = true;
            _dispatchAfter = Now;
            return record;
        }

        private static void RequireGate(ServiceRestartRequest request)
        {
            ServiceMaintenanceJournal.RequireAuthorization(ReadSettings(), UPilotProjectConfig.ProjectRoot);
            if (!ServiceMaintenanceJournal.SamePath(request.expectedProjectPath, UPilotProjectConfig.ProjectRoot))
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Project identity changed.");
            var bridge = UPilotBridge.Instance;
            var status = UPilotMcpServerManager.Instance.GetStatus();
            if (!UPilotBridge.IsMainEditorProcess() || bridge.GetStatus().SessionId != request.expectedBridgeSessionId ||
                status.ProcessId != request.expectedServerProcessId || status.ProcessOwnership != McpProcessOwnership.CurrentUPilot)
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Server/Bridge identity or current-project process ownership changed.");
        }

        private static ServiceRestartRequest OriginalRequest(ServiceMaintenanceRecord r) => new()
        {
            maintenanceId = r.maintenanceId, target = r.target, reason = r.reason, expectedProjectPath = r.projectPath,
            expectedServerProcessId = r.oldServerProcessId, expectedBridgeSessionId = r.oldBridgeSessionId,
            expectedMaintenanceId = r.expectedMaintenanceId,
        };

        private static void Recover()
        {
            // Update may recover before the delayed callback; never recover a newly accepted request twice.
            if (_recoveryChecked) return;
            try
            {
                var r = Current;
                if (r?.Active != true) return;
                if (Journal.Expire()) return;
                if (!ServiceMaintenanceJournal.SamePath(r.projectPath, UPilotProjectConfig.ProjectRoot) ||
                    r.unityProcessId != System.Diagnostics.Process.GetCurrentProcess().Id || r.status == "accepted")
                {
                    Fail("SERVICE_RESTART_RECOVERY_REQUIRED", "Maintenance was interrupted before a recoverable execution identity; start was not replayed.");
                    return;
                }
                // Observation can resume, never dispatch a second stop/start after reload.
                if ((r.target == "server" || r.target == "bridge") && string.IsNullOrEmpty(r.serverRestartId))
                    Fail("SERVICE_RESTART_RECOVERY_REQUIRED", "Replacement restart identity is missing; inspect original evidence.");
            }
            catch (Exception ex) { _storageError = ex.Message; }
            finally { _recoveryChecked = true; }
        }

        // This check must also work while the Bridge is intentionally disconnected.
        internal static void RequireContinuation()
        {
            var r = Current;
            if (r?.Active != true || Now >= r.deadlineAtUtcMs)
                throw new ServiceMaintenanceException("SERVICE_RESTART_TIMEOUT", "Maintenance is no longer active.");
            if (!r.humanConfirmed) ServiceMaintenanceJournal.RequireAuthorization(ReadSettings(), UPilotProjectConfig.ProjectRoot);
            if (!UPilotBridge.IsMainEditorProcess())
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Main Editor identity is required.");
        }

        private static void Execute(ServiceMaintenanceRecord r)
        {
            UPilotQuickStart.SuppressAutomaticRepairForMaintenance();
            _executing = true;
            try
            {
                RequireContinuation();
                var resetError = UPilotBridge.Instance.ResetActiveWork();
                r.queueReset = string.IsNullOrEmpty(resetError);
                if (!string.IsNullOrEmpty(resetError)) r.queueResetError += resetError;
                try { Journal.Save(); } catch (Exception ex) { r.queueResetError += ex.Message; }
                {
                    UPilotMcpServerManager.Instance.RestartPreparedServer(
                        maintenanceDeadlineUtcMs: r.deadlineAtUtcMs, expectedProcessId: r.oldServerProcessId, acceptedAtUtcMs: r.acceptedAtUtcMs);
                    var restart = UPilotServerRestartDiagnostics.Current;
                    if (restart == null || restart.maintenanceDeadlineUtcMs != r.deadlineAtUtcMs ||
                        restart.requestedAtUtcMs < r.acceptedAtUtcMs)
                        throw new ServiceMaintenanceException("SERVICE_RESTART_FAILED", "No matching Server restart identity was created.");
                    r.serverRestartId = restart.operationId;
                }
            }
            finally { _executing = false; }
            r.phase = "verifying";
            Journal.Save();
        }

        private static void Update()
        {
            if (!_recoveryChecked)
            {
                // delayCall depends on Inspector updates and may not run while the Editor is unattended.
                Recover();
                return;
            }
            // Journal errors are displayed, never used as an activity lease.
            var r = Current;
            if (r?.Active != true) return;
            try
            {
                var observedRestart = UPilotServerRestartDiagnostics.Current;
                if ((r.target == "server" || r.target == "bridge") && observedRestart?.operationId == r.serverRestartId)
                {
                    r.healthVerified = UPilotServerRestartDiagnostics.HasPassedHealthGate(observedRestart);
                    r.phase = r.healthVerified ? UPilotServerRestartDiagnostics.FirstUnpassedGate(observedRestart) : "health_query";
                    r.healthProbeStartedAtUtcMs = observedRestart.statusProbeStartedAtUtcMs;
                    r.healthProbeElapsedMs = Math.Max(0, observedRestart.statusProbeEndedAtUtcMs - observedRestart.statusProbeStartedAtUtcMs);
                    r.healthProbeCount = observedRestart.statusProbeCount;
                    r.healthProbeError = observedRestart.statusProbeError;
                    if (observedRestart.status == "failed")
                    {
                        Fail(observedRestart.errorCode, observedRestart.error);
                        return;
                    }
                    if (observedRestart.newProcessId > 0 && !r.healthVerified) r.phase = "health_query";
                }
                if (Journal.Expire())
                {
                    _probeCancellation?.Cancel();
                    UPilotMcpServerManager.Instance.EndMaintenanceObservation(r.serverRestartId);
                    return;
                }
                if (r.status == "accepted")
                {
                    if (Now < _dispatchAfter) return;
                    if (!Journal.Dispatch(() =>
                    {
                        if (!r.humanConfirmed) RequireGate(OriginalRequest(r));
                        if (UPilotQuickStart.IsRepairing || UPilotMcpServerManager.Instance.IsServiceTransitionActive)
                            throw new ServiceMaintenanceException("SERVICE_RESTART_BUSY", "Another service transition began before dispatch.");
                    }, Execute)) return;
                }
                if ((r.target == "server" || r.target == "bridge"))
                {
                    var restart = UPilotServerRestartDiagnostics.Current;
                    if (restart == null || restart.operationId != r.serverRestartId)
                        throw new ServiceMaintenanceException("SERVICE_RESTART_RECOVERY_REQUIRED", "Server restart identity changed.");
                    var phase = UPilotServerRestartDiagnostics.FirstUnpassedGate(restart);
                    var changed = r.phase != phase || r.newServerProcessId != restart.newProcessId ||
                        r.newBridgeSessionId != (restart.newBridgeSessionId ?? "") ||
                        r.startAttempted != (restart.portsReleasedAtUtcMs > 0);
                    r.newServerProcessId = restart.newProcessId;
                    r.newBridgeSessionId = restart.newBridgeSessionId ?? "";
                    r.startAttempted = restart.portsReleasedAtUtcMs > 0;
                    r.phase = phase;
                    if (restart.status == "succeeded")
                    {
                        r.readOnlyVerified = restart.readOnlyVerified;
                        r.Finish("succeeded", "", "", Now);
                    }
                    else if (restart.status != "running")
                        r.Finish(restart.errorCode == "SERVICE_RESTART_TIMEOUT" ? "timed_out" : "failed",
                            restart.errorCode, restart.error, Now);
                    if (changed || !r.Active) Journal.Save();
                }
                else if (!_probeRunning && Now >= _nextProbe)
                {
                    _ = ProbeBridge(r);
                }
            }
            catch (Exception ex)
            {
                if (ex is ServiceMaintenanceException persistence && persistence.Code == "SERVICE_RESTART_PERSIST_FAILED")
                {
                    _storageError = ex.Message;
                    UPilotMcpServerManager.Instance.EndMaintenanceObservation(r.serverRestartId);
                }
                Fail((ex as ServiceMaintenanceException)?.Code ?? "SERVICE_RESTART_FAILED", ex.Message);
            }
        }

        // Missing identity is retryable; only positive, conflicting evidence is a mismatch.
        internal static bool BridgeIdentityChanged(McpServerStatus status, int expectedPid, string project) =>
            (status.HealthEndpointResponded && ((status.HealthServerProcessId > 0 && status.HealthServerProcessId != expectedPid) ||
                (!string.IsNullOrWhiteSpace(status.HealthProjectPath) && !ServiceMaintenanceJournal.SamePath(status.HealthProjectPath, project)))) ||
            (status.ProcessOwnership == McpProcessOwnership.Foreign) ||
            (status.ProcessId > 0 && status.ProcessId != expectedPid);

        private static async Task ProbeBridge(ServiceMaintenanceRecord r)
        {
            _probeRunning = true;
            var cancellation = new CancellationTokenSource();
            _probeCancellation = cancellation;
            r.healthProbeCount++;
            r.healthProbeStartedAtUtcMs = Now;
            if (!r.healthVerified) r.phase = "health_query";
            try
            {
                using (var process = System.Diagnostics.Process.GetProcessById(r.oldServerProcessId))
                    if (process.HasExited) throw new ServiceMaintenanceException("SERVICE_RESTART_PROCESS_EXITED", "The original Server exited.");
                var manager = UPilotMcpServerManager.Instance;
                var status = await manager.GetRestartStatusAsync(r.deadlineAtUtcMs, cancellation.Token);
                if (!ReferenceEquals(Current, r) || !r.Active || Journal.Expire() || cancellation.IsCancellationRequested) return;
                r.healthProbeError = status.ErrorMessage ?? "";
                if (status.HealthEndpointResponded && !r.healthVerified) { r.healthVerified = true; r.phase = "identity"; }
                r.healthProbeElapsedMs = Math.Max(0, Now - r.healthProbeStartedAtUtcMs);
                Journal.Save();
                if (BridgeIdentityChanged(status, r.oldServerProcessId, r.projectPath))
                    throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Bridge-only maintenance observed a different Server PID/project/owner.");
                var bridge = UPilotBridge.Instance.GetStatus();
                if (!string.IsNullOrEmpty(bridge.AuthenticationError) && bridge.AuthenticationFailureAtUtcMs >= r.acceptedAtUtcMs)
                    throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", bridge.AuthenticationError);
                if (!UPilotMcpServerManager.IsVerifiedRestartHealth(status, r.oldServerProcessId, r.projectPath)) return;
                r.healthVerified = true;
                if (r.phase == "identity" || r.phase == "health_query" || r.phase == "verifying") r.phase = "bridge_session";
                var issues = UPilotDeploymentDiagnostics.Observe(bridge, status);
                if (issues.Any(issue => issue.Confirmed && issue.Code != "timeout" && issue.Code != "authentication"))
                    throw new ServiceMaintenanceException("SERVICE_RESTART_VALIDATION_FAILED", string.Join("\n", issues.Select(issue => issue.Message)));
                if (!bridge.IsAuthenticated || !bridge.IsWsOpen || bridge.SessionId == r.oldBridgeSessionId) return;
                if (r.phase == "bridge_session") r.phase = "deployment";
                if (issues.Length > 0) return;
                r.phase = "readonly";
                await manager.VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, r.deadlineAtUtcMs, cancellation.Token);
                if (!ReferenceEquals(Current, r) || !r.Active || Journal.Expire() || cancellation.IsCancellationRequested) return;
                var verifiedBridge = UPilotBridge.Instance.GetStatus();
                if (!verifiedBridge.IsWsOpen || !verifiedBridge.IsAuthenticated || verifiedBridge.SessionId != bridge.SessionId) return;
                r.newServerProcessId = status.ProcessId ?? 0;
                r.newBridgeSessionId = bridge.SessionId;
                r.readOnlyVerified = true;
                r.Finish("succeeded", "", "", Now);
                Journal.Save();
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(Current, r) || !r.Active || cancellation.IsCancellationRequested) return;
                r.healthProbeError = UPilotMcpServerManager.DescribeProbeException(ex);
                r.healthProbeElapsedMs = Math.Max(0, Now - r.healthProbeStartedAtUtcMs);
                if (Journal.Expire()) return;
                if (ex is ServiceMaintenanceException specific) Fail(specific.Code, specific.Message);
                else if (ex is ArgumentException) Fail("SERVICE_RESTART_PROCESS_EXITED", "The original Server process no longer exists.");
                else if (ex is InvalidOperationException) Fail("SERVICE_RESTART_VALIDATION_FAILED", r.healthProbeError);
                else Journal.Save(); // Transient transport errors consume only the original budget.
            }
            finally
            {
                if (ReferenceEquals(_probeCancellation, cancellation))
                {
                    _probeRunning = false;
                    _nextProbe = Now + UPilotMcpServerManager.RestartProbeIntervalMs;
                    _probeCancellation = null;
                    if (ReferenceEquals(Current, r) && r.Active)
                    {
                        try { Journal.Save(); }
                        catch (Exception ex) { _storageError = ex.Message; }
                    }
                }
                cancellation.Dispose();
            }
        }

        private static void Fail(string code, string message)
        {
            var r = Current;
            if (r?.Active != true) return;
            _probeCancellation?.Cancel();
            r.Finish(code == "SERVICE_RESTART_TIMEOUT" ? "timed_out" : "failed", code, message, Now);
            try { Journal.Save(); }
            catch (Exception ex) { _storageError = ex.Message; }
            UPilotMcpServerManager.Instance.EndMaintenanceObservation(r.serverRestartId);
        }
    }
}
