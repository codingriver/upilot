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
        public bool affectedWorkComplete;
        public string[] affectedCommandIds = Array.Empty<string>();
        public string[] affectedComponents;
        public bool Active => status == "accepted" || status == "running";

        internal bool Expire(long now)
        {
            if (!Active || now < deadlineAtUtcMs) return false;
            Finish("timed_out", "SERVICE_RESTART_TIMEOUT",
                "The maintenance deadline elapsed. Services may still recover; business was not replayed.", now);
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

        internal ServiceMaintenanceRecord Accept(ServiceRestartRequest request, int timeout, int unityPid, string[] affected)
        {
            var duplicate = FindDuplicate(request);
            if (duplicate != null) return duplicate;
            if (Current?.Active == true)
                throw new ServiceMaintenanceException("SERVICE_RESTART_BUSY", "Maintenance already active: " + Current.maintenanceId);
            // Compare-and-set prevents an old request from becoming a new restart after journal replacement.
            if ((request.expectedMaintenanceId ?? "") != (Current?.maintenanceId ?? ""))
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Latest maintenance identity changed; inspect status.");
            var now = _now();
            var record = new ServiceMaintenanceRecord
            {
                maintenanceId = request.maintenanceId, projectPath = Path.GetFullPath(request.expectedProjectPath),
                target = request.target, reason = request.reason, expectedMaintenanceId = request.expectedMaintenanceId ?? "",
                oldServerProcessId = request.expectedServerProcessId, oldBridgeSessionId = request.expectedBridgeSessionId,
                unityProcessId = unityPid, restartTimeoutSeconds = timeout, acceptedAtUtcMs = now,
                deadlineAtUtcMs = checked(now + timeout * 1000L), affectedCommandIds = affected ?? Array.Empty<string>(),
                affectedWorkComplete = false,
                affectedComponents = request.target == "server" ? new[] { "server", "bridge" } : new[] { "bridge" },
            };
            Write(record);
            Current = record;
            return record;
        }

        internal bool Expire()
        {
            if (Current?.Expire(_now()) != true) return false;
            Save();
            return true;
        }

        internal void Save() { if (Current != null) Write(Current); }

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
            bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!_recoveryChecked || !string.IsNullOrEmpty(StorageError))
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

        private static void RequireGate(ServiceRestartRequest request)
        {
            ServiceMaintenanceJournal.RequireAuthorization(ReadSettings(), UPilotProjectConfig.ProjectRoot);
            if (!ServiceMaintenanceJournal.SamePath(request.expectedProjectPath, UPilotProjectConfig.ProjectRoot))
                throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Project identity changed.");
            var bridge = UPilotBridge.Instance;
            var state = bridge.GetEditorExecutionContext("service.restart");
            if (!state.ready || !state.authoritative || state.isStale || state.playModeState != "edit" ||
                EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPaused ||
                EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new ServiceMaintenanceException("SERVICE_RESTART_EDITOR_NOT_READY", "Authoritative stable EditMode is required; mode is never changed.");
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
                if (r.target == "server" && string.IsNullOrEmpty(r.serverRestartId))
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
            ServiceMaintenanceJournal.RequireAuthorization(ReadSettings(), UPilotProjectConfig.ProjectRoot);
            var phase = UPilotBridge.Instance.GetEditorExecutionContext("service.restart.continue").compilePhase;
            if (!UPilotBridge.IsMainEditorProcess() || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isPaused || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                new[] { "queued", "compiling", "compiler_finished", "domain_reload", "verifying" }.Contains(phase))
                throw new ServiceMaintenanceException("SERVICE_RESTART_EDITOR_NOT_READY", "Editor state changed before the next stop/start.");
        }

        private static void Execute(ServiceMaintenanceRecord r)
        {
            UPilotQuickStart.SuppressAutomaticRepairForMaintenance();
            _executing = true;
            try
            {
                RequireContinuation();
                if (r.target == "bridge")
                {
                    UPilotBridge.Instance.Stop();
                    RequireContinuation();
                    r.startAttempted = true;
                    Journal.Save();
                    UPilotBridge.Instance.EnsureStarted();
                }
                else
                {
                    UPilotMcpServerManager.Instance.RestartPreparedServer(
                        maintenanceDeadlineUtcMs: r.deadlineAtUtcMs, expectedProcessId: r.oldServerProcessId);
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
            if (!_recoveryChecked) return;
            var r = Current;
            if (r?.Active != true) return;
            try
            {
                if (Journal.Expire())
                {
                    UPilotMcpServerManager.Instance.EndMaintenanceObservation(r.serverRestartId);
                    return;
                }
                if (r.status == "accepted")
                {
                    if (Now < _dispatchAfter) return;
                    if (!Journal.Dispatch(() =>
                    {
                        RequireGate(OriginalRequest(r));
                        if (UPilotQuickStart.IsRepairing || UPilotMcpServerManager.Instance.IsServiceTransitionActive)
                            throw new ServiceMaintenanceException("SERVICE_RESTART_BUSY", "Another service transition began before dispatch.");
                    }, Execute)) return;
                }
                if (r.target == "server")
                {
                    var restart = UPilotServerRestartDiagnostics.Current;
                    if (restart == null || restart.operationId != r.serverRestartId)
                        throw new ServiceMaintenanceException("SERVICE_RESTART_RECOVERY_REQUIRED", "Server restart identity changed.");
                    var changed = r.phase != restart.phase || r.newServerProcessId != restart.newProcessId ||
                        r.newBridgeSessionId != (restart.newBridgeSessionId ?? "") ||
                        r.startAttempted != (restart.portsReleasedAtUtcMs > 0);
                    r.newServerProcessId = restart.newProcessId;
                    r.newBridgeSessionId = restart.newBridgeSessionId ?? "";
                    r.startAttempted = restart.portsReleasedAtUtcMs > 0;
                    r.phase = restart.phase;
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
                    _nextProbe = Now + 500;
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

        private static async Task ProbeBridge(ServiceMaintenanceRecord r)
        {
            _probeRunning = true;
            try
            {
                var manager = UPilotMcpServerManager.Instance;
                var status = await manager.GetFreshStatusAsync(r.deadlineAtUtcMs);
                if (!r.Active || Journal.Expire()) return;
                if (status.ProcessId != r.oldServerProcessId || status.ProcessOwnership != McpProcessOwnership.CurrentUPilot)
                    throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", "Bridge-only maintenance must not replace the Server.");
                var bridge = UPilotBridge.Instance.GetStatus();
                if (!string.IsNullOrEmpty(bridge.AuthenticationError))
                    throw new ServiceMaintenanceException("SERVICE_RESTART_IDENTITY_CHANGED", bridge.AuthenticationError);
                if (!bridge.IsAuthenticated || bridge.SessionId == r.oldBridgeSessionId) return;
                await manager.VerifyReadOnlyRoundTripAsync(status, bridge.SessionId, r.deadlineAtUtcMs);
                if (!r.Active || Journal.Expire()) return;
                r.newServerProcessId = status.ProcessId ?? 0;
                r.newBridgeSessionId = bridge.SessionId;
                r.readOnlyVerified = true;
                r.Finish("succeeded", "", "", Now);
                Journal.Save();
            }
            catch (ServiceMaintenanceException ex) { Fail(ex.Code, ex.Message); }
            catch (InvalidOperationException ex) { Fail("SERVICE_RESTART_VALIDATION_FAILED", ex.Message); }
            catch (Exception)
            {
                // A transient probe failure consumes time, not another stop/start attempt.
                if (r.Active && Now >= r.deadlineAtUtcMs) Fail("SERVICE_RESTART_TIMEOUT", "Maintenance deadline elapsed.");
            }
            finally { _probeRunning = false; }
        }

        private static void Fail(string code, string message)
        {
            var r = Current;
            if (r?.Active != true) return;
            r.Finish(code == "SERVICE_RESTART_TIMEOUT" ? "timed_out" : "failed", code, message, Now);
            try { Journal.Save(); }
            catch (Exception ex) { _storageError = ex.Message; }
            UPilotMcpServerManager.Instance.EndMaintenanceObservation(r.serverRestartId);
        }
    }
}
