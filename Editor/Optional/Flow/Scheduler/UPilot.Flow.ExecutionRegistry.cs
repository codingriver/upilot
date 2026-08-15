using System;
using System.Collections.Generic;
using System.Linq;

namespace CodingRiver.UPilot.Flow
{
    [Serializable]
    public sealed class UPilotFlowExecutionSnapshot
    {
        public string executionId;
        public string source;
        public string status;
        public string phase;
        public string currentCase;
        public string currentStep;
        public int currentStepIndex = -1;
        public long startedAt;
        public long lastProgressAt;
        public bool cancelRequested;
        public bool cancelAccepted;
        public int cancelAttemptCount;
        public long stopRequestedAt;
        public long stoppingAt;
        public long cleanupStartedAt;
        public long endedAt;
        public bool cleanupPending;
        public List<string> cleanupErrors = new List<string>();
        public List<string> unresolvedResources = new List<string>();
        public bool paused;
        public string pauseReason;
        public string pauseOwner;
        public string pauseToken;
        public long pausedAt;
        public long pauseDeadline;
        public string pauseTimeoutPolicy;
        public int activeLeaseCount;
        public bool waitingForModalUi;
        public int activeModalCount;
        public string modalOwner;
        public string modalType;
        public long modalStartedAt;
        public long modalDeadline;
        public bool modalRecoveryAttempted;
        public bool modalRecoverySucceeded;
        public string modalRecoveryReason;
        public string modalRecoverySource;
        public string modalRecoveryError;
        public long actionStartedAt;
        public long actionDeadline;
        public int actionTimeoutMs;
        public string progressDetail;
    }

    /// <summary>
    /// Process-wide Flow execution registry. Every adapter and direct TestRunner call
    /// uses this registry so a run can be observed and stopped without restarting Unity.
    /// </summary>
    public static class UPilotFlowExecutionRegistry
    {
        private sealed class Entry
        {
            public readonly UPilotFlowExecutionSnapshot Snapshot = new UPilotFlowExecutionSnapshot();
            public readonly HashSet<RuntimeController> Controllers = new HashSet<RuntimeController>();
            public readonly List<Action> CancelActions = new List<Action>();
            public string PendingTerminalStatus;
        }

        private sealed class Lease : IDisposable
        {
            private string _executionId;
            private Action _cancelAction;
            private string _terminalStatus;

            public Lease(string executionId, Action cancelAction)
            {
                _executionId = executionId;
                _cancelAction = cancelAction;
            }

            public void Dispose()
            {
                string id = _executionId;
                Action cancelAction = _cancelAction;
                _executionId = null;
                _cancelAction = null;
                if (!string.IsNullOrEmpty(id))
                    Release(id, _terminalStatus, cancelAction);
            }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static string _latestExecutionId;

        public static IDisposable Register(string executionId, string source, Action cancelAction = null)
        {
            executionId = string.IsNullOrWhiteSpace(executionId) ? Guid.NewGuid().ToString("N") : executionId;
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                {
                    long now = NowMs();
                    entry = new Entry();
                    entry.Snapshot.executionId = executionId;
                    entry.Snapshot.source = string.IsNullOrWhiteSpace(source) ? "direct" : source;
                    entry.Snapshot.status = "running";
                    entry.Snapshot.phase = "starting";
                    entry.Snapshot.startedAt = now;
                    entry.Snapshot.lastProgressAt = now;
                    Entries.Add(executionId, entry);
                }

                if (cancelAction != null)
                    entry.CancelActions.Add(cancelAction);
                entry.Snapshot.activeLeaseCount++;
                entry.Snapshot.cleanupPending = entry.Snapshot.cancelRequested;
                _latestExecutionId = executionId;
            }

            return new Lease(executionId, cancelAction);
        }

        public static string EnsureExecutionId(TestOptions options)
        {
            options ??= new TestOptions();
            if (string.IsNullOrWhiteSpace(options.ExecutionId))
                options.ExecutionId = Guid.NewGuid().ToString("N");
            return options.ExecutionId;
        }

        public static void AttachContext(string executionId, ExecutionContext context)
        {
            if (string.IsNullOrWhiteSpace(executionId) || context?.RuntimeController == null)
                return;

            RuntimeController controller = context.RuntimeController;
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                    return;
                entry.Controllers.Add(controller);
                entry.Snapshot.currentCase = context.CaseName;
                entry.Snapshot.phase = "case";
                entry.Snapshot.lastProgressAt = NowMs();
                controller.StateChanged = () => SyncController(executionId, controller);
            }
            SyncController(executionId, controller);
        }

        public static void DetachContext(string executionId, ExecutionContext context)
        {
            RuntimeController controller = context?.RuntimeController;
            if (controller == null)
                return;
            lock (Gate)
            {
                if (Entries.TryGetValue(executionId, out Entry entry))
                {
                    entry.Controllers.Remove(controller);
                    entry.Snapshot.currentCase = null;
                    entry.Snapshot.currentStep = null;
                    entry.Snapshot.currentStepIndex = -1;
                    entry.Snapshot.lastProgressAt = NowMs();
                    RefreshCleanupStateLocked(entry);
                }
            }
            controller.StateChanged = null;
        }

        public static void StepStarted(string executionId, int index, ExecutableStep step)
        {
            Mutate(executionId, snapshot =>
            {
                long now = NowMs();
                snapshot.currentStepIndex = index;
                snapshot.currentStep = step?.DisplayName;
                snapshot.phase = step?.Phase.ToString().ToLowerInvariant() ?? "step";
                snapshot.actionStartedAt = now;
                snapshot.actionTimeoutMs = step?.TimeoutMs ?? 0;
                snapshot.actionDeadline = snapshot.actionTimeoutMs > 0
                    ? now + snapshot.actionTimeoutMs
                    : 0;
                snapshot.progressDetail = "action_started";
            });
        }

        public static void StepCompleted(string executionId)
        {
            Mutate(executionId, snapshot =>
            {
                snapshot.phase = snapshot.paused ? "waiting_for_resume" : "case";
                snapshot.actionDeadline = 0;
                snapshot.actionTimeoutMs = 0;
                snapshot.progressDetail = "action_completed";
            });
        }

        public static void ActionProgress(string executionId, string detail)
        {
            if (string.IsNullOrWhiteSpace(executionId))
                return;
            Mutate(executionId, snapshot =>
            {
                snapshot.progressDetail = string.IsNullOrWhiteSpace(detail)
                    ? "action_progress"
                    : detail;
            });
        }

        public static void ModalWaitStarted(string executionId, string owner, string modalType, long deadline)
        {
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                    return;
                long now = NowMs();
                entry.Snapshot.waitingForModalUi = true;
                entry.Snapshot.activeModalCount++;
                entry.Snapshot.modalOwner = owner;
                entry.Snapshot.modalType = modalType;
                entry.Snapshot.modalStartedAt = now;
                entry.Snapshot.modalDeadline = deadline;
                entry.Snapshot.modalRecoveryAttempted = false;
                entry.Snapshot.modalRecoverySucceeded = false;
                entry.Snapshot.modalRecoveryReason = null;
                entry.Snapshot.modalRecoverySource = null;
                entry.Snapshot.modalRecoveryError = null;
                entry.Snapshot.phase = "waiting_for_modal_ui";
                entry.Snapshot.lastProgressAt = now;
                RefreshCleanupStateLocked(entry);
            }
        }

        public static void ModalRecoveryAttempted(
            string executionId,
            string reason,
            string source,
            bool succeeded,
            string error)
        {
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                    return;
                entry.Snapshot.modalRecoveryAttempted = true;
                entry.Snapshot.modalRecoverySucceeded = succeeded;
                entry.Snapshot.modalRecoveryReason = reason;
                entry.Snapshot.modalRecoverySource = source;
                entry.Snapshot.modalRecoveryError = error;
                entry.Snapshot.lastProgressAt = NowMs();
                if (!succeeded && !string.IsNullOrWhiteSpace(error))
                    entry.Snapshot.cleanupErrors.Add($"modal-watchdog: {error}");
            }
        }

        public static void ModalWaitEnded(string executionId)
        {
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                    return;
                entry.Snapshot.activeModalCount = Math.Max(0, entry.Snapshot.activeModalCount - 1);
                entry.Snapshot.waitingForModalUi = entry.Snapshot.activeModalCount > 0;
                if (!entry.Snapshot.waitingForModalUi)
                {
                    entry.Snapshot.modalDeadline = 0;
                    if (!entry.Snapshot.cancelRequested && !entry.Snapshot.paused)
                        entry.Snapshot.phase = "step";
                }
                entry.Snapshot.lastProgressAt = NowMs();
                RefreshCleanupStateLocked(entry);
            }
        }

        public static bool Pause(string executionId, string owner = "mcp", string reason = "manual")
        {
            return ForControllers(executionId, controller => controller.Pause(owner, reason));
        }

        public static bool Resume(string executionId)
        {
            return ForControllers(executionId, controller => controller.Resume());
        }

        public static bool RequestStop(string executionId)
        {
            List<Action> cancelActions;
            List<RuntimeController> controllers;
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                    return false;
                if (entry.Snapshot.status == "completed" || entry.Snapshot.status == "failed" || entry.Snapshot.status == "aborted")
                    return true;
                if (entry.Snapshot.cancelAccepted)
                    return true;
                long now = NowMs();
                entry.Snapshot.cancelRequested = true;
                entry.Snapshot.cancelAccepted = true;
                entry.Snapshot.cancelAttemptCount++;
                entry.Snapshot.stopRequestedAt = entry.Snapshot.stopRequestedAt > 0 ? entry.Snapshot.stopRequestedAt : now;
                entry.Snapshot.status = "cancel_requested";
                entry.Snapshot.phase = "cancel_requested";
                entry.Snapshot.cleanupPending = entry.Snapshot.activeLeaseCount > 0;
                entry.Snapshot.lastProgressAt = now;
                cancelActions = entry.CancelActions.ToList();
                controllers = entry.Controllers.ToList();
                RefreshCleanupStateLocked(entry);
            }

            var cleanupErrors = new List<string>();
            for (int index = 0; index < cancelActions.Count; index++)
            {
                try { cancelActions[index](); }
                catch (Exception ex) { cleanupErrors.Add($"cancel-action[{index}]: {ex.GetType().Name}: {ex.Message}"); }
            }
            for (int index = 0; index < controllers.Count; index++)
            {
                try { controllers[index].Stop(); }
                catch (Exception ex) { cleanupErrors.Add($"runtime-controller[{index}]: {ex.GetType().Name}: {ex.Message}"); }
            }

            lock (Gate)
            {
                if (Entries.TryGetValue(executionId, out Entry entry))
                {
                    long now = NowMs();
                    entry.Snapshot.status = entry.Snapshot.activeLeaseCount > 0 ? "stopping" : "aborted";
                    entry.Snapshot.phase = entry.Snapshot.activeLeaseCount > 0 ? "stopping" : "aborted";
                    entry.Snapshot.stoppingAt = entry.Snapshot.stoppingAt > 0 ? entry.Snapshot.stoppingAt : now;
                    entry.Snapshot.cleanupErrors.AddRange(cleanupErrors);
                    entry.Snapshot.lastProgressAt = now;
                    RefreshCleanupStateLocked(entry);
                    if (entry.Snapshot.activeLeaseCount == 0)
                        FinalizeLocked(entry, "aborted");
                }
            }
            return true;
        }

        public static void MarkTerminal(string executionId, string terminalStatus)
        {
            lock (Gate)
            {
                if (Entries.TryGetValue(executionId, out Entry entry))
                {
                    entry.PendingTerminalStatus = terminalStatus;
                    if (entry.Snapshot.cancelRequested && entry.Snapshot.activeLeaseCount > 0)
                    {
                        entry.Snapshot.status = "cleanup";
                        entry.Snapshot.phase = "cleanup";
                        entry.Snapshot.cleanupPending = true;
                        entry.Snapshot.cleanupStartedAt = entry.Snapshot.cleanupStartedAt > 0
                            ? entry.Snapshot.cleanupStartedAt
                            : NowMs();
                    }
                    else if (entry.Snapshot.activeLeaseCount == 0)
                    {
                        FinalizeLocked(entry, terminalStatus);
                    }
                    RefreshCleanupStateLocked(entry);
                }
            }
        }

        public static UPilotFlowExecutionSnapshot Get(string executionId = null)
        {
            lock (Gate)
            {
                string id = string.IsNullOrWhiteSpace(executionId) ? _latestExecutionId : executionId;
                return id != null && Entries.TryGetValue(id, out Entry entry) ? Clone(entry.Snapshot) : null;
            }
        }

        public static List<UPilotFlowExecutionSnapshot> List()
        {
            lock (Gate)
            {
                return Entries.Values.Select(entry => Clone(entry.Snapshot))
                    .OrderByDescending(snapshot => snapshot.startedAt)
                    .ToList();
            }
        }

        private static bool ForControllers(string executionId, Action<RuntimeController> action)
        {
            List<RuntimeController> controllers;
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                    return false;
                controllers = entry.Controllers.ToList();
            }
            foreach (RuntimeController controller in controllers)
                action(controller);
            return controllers.Count > 0;
        }

        private static void SyncController(string executionId, RuntimeController controller)
        {
            Mutate(executionId, snapshot =>
            {
                snapshot.paused = controller.IsPaused;
                snapshot.pauseReason = controller.PauseReason;
                snapshot.pauseOwner = controller.PauseOwner;
                snapshot.pauseToken = controller.PauseToken;
                snapshot.pausedAt = controller.PausedAt;
                snapshot.pauseDeadline = controller.PauseDeadline;
                snapshot.pauseTimeoutPolicy = controller.PauseTimeoutPolicy;
                snapshot.phase = controller.IsPaused ? "waiting_for_resume" : (controller.IsStopped ? "stopping" : snapshot.phase);
            });
        }

        private static void Mutate(string executionId, Action<UPilotFlowExecutionSnapshot> mutate)
        {
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                    return;
                mutate(entry.Snapshot);
                entry.Snapshot.lastProgressAt = NowMs();
            }
        }

        private static void Release(string executionId, string terminalStatus, Action cancelAction)
        {
            lock (Gate)
            {
                if (!Entries.TryGetValue(executionId, out Entry entry))
                    return;
                if (cancelAction != null)
                    entry.CancelActions.Remove(cancelAction);
                entry.Snapshot.activeLeaseCount = Math.Max(0, entry.Snapshot.activeLeaseCount - 1);
                if (entry.Snapshot.activeLeaseCount == 0 && !string.IsNullOrWhiteSpace(terminalStatus))
                    entry.PendingTerminalStatus = terminalStatus;
                entry.Snapshot.lastProgressAt = NowMs();
                if (entry.Snapshot.activeLeaseCount == 0)
                {
                    FinalizeLocked(entry, entry.Snapshot.cancelRequested
                        ? "aborted"
                        : (entry.PendingTerminalStatus ?? "completed"));
                }
                else if (entry.Snapshot.cancelRequested)
                {
                    entry.Snapshot.status = "cleanup";
                    entry.Snapshot.phase = "cleanup";
                    entry.Snapshot.cleanupStartedAt = entry.Snapshot.cleanupStartedAt > 0
                        ? entry.Snapshot.cleanupStartedAt
                        : NowMs();
                }
                RefreshCleanupStateLocked(entry);
            }
        }

        private static void FinalizeLocked(Entry entry, string terminalStatus)
        {
            entry.Snapshot.cleanupPending = false;
            entry.Snapshot.currentCase = null;
            entry.Snapshot.currentStep = null;
            entry.Snapshot.currentStepIndex = -1;
            entry.Snapshot.actionDeadline = 0;
            entry.Snapshot.actionTimeoutMs = 0;
            entry.Snapshot.paused = false;
            entry.Snapshot.waitingForModalUi = false;
            entry.Snapshot.activeModalCount = 0;
            entry.Snapshot.modalDeadline = 0;
            entry.Snapshot.status = string.IsNullOrWhiteSpace(terminalStatus) ? "completed" : terminalStatus;
            entry.Snapshot.phase = entry.Snapshot.status;
            entry.Snapshot.endedAt = NowMs();
            entry.Snapshot.unresolvedResources.Clear();
        }

        private static void RefreshCleanupStateLocked(Entry entry)
        {
            entry.Snapshot.unresolvedResources.Clear();
            if (entry.Snapshot.activeLeaseCount > 0)
                entry.Snapshot.unresolvedResources.Add($"execution-lease:{entry.Snapshot.activeLeaseCount}");
            if (entry.Controllers.Count > 0)
                entry.Snapshot.unresolvedResources.Add($"runtime-controller:{entry.Controllers.Count}");
            if (entry.CancelActions.Count > 0)
                entry.Snapshot.unresolvedResources.Add($"cancel-action:{entry.CancelActions.Count}");
            if (entry.Snapshot.activeModalCount > 0)
                entry.Snapshot.unresolvedResources.Add($"modal-ui:{entry.Snapshot.activeModalCount}");
            entry.Snapshot.cleanupPending = entry.Snapshot.cancelRequested
                && entry.Snapshot.unresolvedResources.Count > 0;
        }

        private static UPilotFlowExecutionSnapshot Clone(UPilotFlowExecutionSnapshot source)
        {
            return new UPilotFlowExecutionSnapshot
            {
                executionId = source.executionId,
                source = source.source,
                status = source.status,
                phase = source.phase,
                currentCase = source.currentCase,
                currentStep = source.currentStep,
                currentStepIndex = source.currentStepIndex,
                startedAt = source.startedAt,
                lastProgressAt = source.lastProgressAt,
                cancelRequested = source.cancelRequested,
                cancelAccepted = source.cancelAccepted,
                cancelAttemptCount = source.cancelAttemptCount,
                stopRequestedAt = source.stopRequestedAt,
                stoppingAt = source.stoppingAt,
                cleanupStartedAt = source.cleanupStartedAt,
                endedAt = source.endedAt,
                cleanupPending = source.cleanupPending,
                paused = source.paused,
                pauseReason = source.pauseReason,
                pauseOwner = source.pauseOwner,
                pauseToken = source.pauseToken,
                pausedAt = source.pausedAt,
                pauseDeadline = source.pauseDeadline,
                pauseTimeoutPolicy = source.pauseTimeoutPolicy,
                activeLeaseCount = source.activeLeaseCount,
                waitingForModalUi = source.waitingForModalUi,
                activeModalCount = source.activeModalCount,
                modalOwner = source.modalOwner,
                modalType = source.modalType,
                modalStartedAt = source.modalStartedAt,
                modalDeadline = source.modalDeadline,
                modalRecoveryAttempted = source.modalRecoveryAttempted,
                modalRecoverySucceeded = source.modalRecoverySucceeded,
                modalRecoveryReason = source.modalRecoveryReason,
                modalRecoverySource = source.modalRecoverySource,
                modalRecoveryError = source.modalRecoveryError,
                actionStartedAt = source.actionStartedAt,
                actionDeadline = source.actionDeadline,
                actionTimeoutMs = source.actionTimeoutMs,
                progressDetail = source.progressDetail,
                cleanupErrors = source.cleanupErrors?.ToList() ?? new List<string>(),
                unresolvedResources = source.unresolvedResources?.ToList() ?? new List<string>(),
            };
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
