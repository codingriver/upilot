using System;
using System.Collections.Generic;
using System.Threading;

namespace CodingRiver.UPilot.Flow
{
    public interface IFlowModalObserver : IDisposable
    {
        void Recover();
        bool ActionPosted { get; }
        string Error { get; }
    }
    /// <summary>
    /// Background watchdog for Unity native modal menus. It never terminates Unity:
    /// recovery is limited to posting Escape to a top-level window owned by the exact
    /// current Unity process id.
    /// </summary>
    public static class UPilotFlowModalWatchdog
    {
        public static Func<UnityEditor.EditorWindow, int, IFlowModalObserver> CreateObserver;

        private sealed class Watch : IDisposable
        {
            private readonly string _watchId = Guid.NewGuid().ToString("N");
            private readonly string _executionId;
            private readonly string _owner;
            private readonly string _modalType;
            private Timer _timer;
            private IDisposable _registryLease;
            private int _disposed;
            private int _recoveryAttempted;
            private readonly IFlowModalObserver _observer;

            public Watch(ActionContext context, string modalType, int timeoutMs)
            {
                _executionId = context?.Options?.ExecutionId ?? string.Empty;
                _owner = string.IsNullOrWhiteSpace(context?.CurrentStepId)
                    ? (context?.CurrentCaseName ?? "flow-action")
                    : context.CurrentStepId;
                _modalType = string.IsNullOrWhiteSpace(modalType) ? "native-modal" : modalType;
                _observer = CreateObserver?.Invoke(UnityEditor.EditorWindow.focusedWindow, timeoutMs);
                long deadline = NowMs() + Math.Max(500, timeoutMs);

                if (!string.IsNullOrWhiteSpace(_executionId))
                {
                    _registryLease = UPilotFlowExecutionRegistry.Register(
                        _executionId,
                        "modal-watchdog",
                        () => Recover("stop_requested"));
                    UPilotFlowExecutionRegistry.ModalWaitStarted(
                        _executionId,
                        _owner,
                        _modalType,
                        deadline);
                    AddWatch(_executionId, _watchId, this);
                }

                _timer = new Timer(
                    _ => Recover("deadline_elapsed"),
                    null,
                    Math.Max(500, timeoutMs),
                    Timeout.Infinite);
            }

            public void Recover(string reason)
            {
                if (Volatile.Read(ref _disposed) != 0
                    || Interlocked.Exchange(ref _recoveryAttempted, 1) != 0)
                    return;

                bool succeeded = false;
                string source = "none";
                string error = string.Empty;
                try
                {
                    _observer?.Recover();
                    succeeded = _observer?.ActionPosted ?? false;
                    source = "core_exact_owner_generic_menu";
                    error = _observer?.Error ?? "Core modal observer adapter unavailable.";
                }
                catch (Exception ex)
                {
                    error = $"{ex.GetType().Name}: {ex.Message}";
                }

                if (!string.IsNullOrWhiteSpace(_executionId))
                {
                    UPilotFlowExecutionRegistry.ModalRecoveryAttempted(
                        _executionId,
                        reason,
                        source,
                        succeeded,
                        error);
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _timer?.Dispose();
                _observer?.Dispose();
                _timer = null;
                if (!string.IsNullOrWhiteSpace(_executionId))
                {
                    RemoveWatch(_executionId, _watchId);
                    UPilotFlowExecutionRegistry.ModalWaitEnded(_executionId);
                }
                _registryLease?.Dispose();
                _registryLease = null;
            }
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Dictionary<string, Watch>> Watches =
            new Dictionary<string, Dictionary<string, Watch>>(StringComparer.Ordinal);

        public static IDisposable Arm(ActionContext context, string modalType, int? timeoutMs = null)
        {
            if (context?.Options?.EnableModalWatchdog == false)
                return EmptyDisposable.Instance;

            int configuredTimeout = timeoutMs
                ?? context?.Options?.ModalWatchdogTimeoutMs
                ?? 5000;
            return new Watch(context, modalType, configuredTimeout);
        }

        public static void RequestRecovery(string executionId, string reason = "stop_requested")
        {
            List<Watch> watches = new List<Watch>();
            lock (Gate)
            {
                if (!string.IsNullOrWhiteSpace(executionId)
                    && Watches.TryGetValue(executionId, out Dictionary<string, Watch> active))
                    watches.AddRange(active.Values);
            }
            foreach (Watch watch in watches)
                watch.Recover(reason);
        }

        private static void AddWatch(string executionId, string watchId, Watch watch)
        {
            lock (Gate)
            {
                if (!Watches.TryGetValue(executionId, out Dictionary<string, Watch> active))
                {
                    active = new Dictionary<string, Watch>(StringComparer.Ordinal);
                    Watches.Add(executionId, active);
                }
                active[watchId] = watch;
            }
        }

        private static void RemoveWatch(string executionId, string watchId)
        {
            lock (Gate)
            {
                if (!Watches.TryGetValue(executionId, out Dictionary<string, Watch> active))
                    return;
                active.Remove(watchId);
                if (active.Count == 0)
                    Watches.Remove(executionId);
            }
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new EmptyDisposable();
            public void Dispose() { }
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
