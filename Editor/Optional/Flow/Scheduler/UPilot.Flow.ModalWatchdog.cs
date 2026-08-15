using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodingRiver.UPilot.Flow
{
    /// <summary>
    /// Background watchdog for Unity native modal menus. It never terminates Unity:
    /// recovery is limited to posting Escape to a top-level window owned by the exact
    /// current Unity process id.
    /// </summary>
    public static class UPilotFlowModalWatchdog
    {
        private const uint WmKeyDown = 0x0100;
        private const uint WmKeyUp = 0x0101;
        private const int VkEscape = 0x1B;

        private sealed class Watch : IDisposable
        {
            private readonly string _watchId = Guid.NewGuid().ToString("N");
            private readonly string _executionId;
            private readonly string _owner;
            private readonly string _modalType;
            private readonly int _processId;
            private IntPtr _windowHandle;
            private Timer _timer;
            private IDisposable _registryLease;
            private int _disposed;
            private int _recoveryAttempted;

            public Watch(ActionContext context, string modalType, int timeoutMs)
            {
                _executionId = context?.Options?.ExecutionId ?? string.Empty;
                _owner = string.IsNullOrWhiteSpace(context?.CurrentStepId)
                    ? (context?.CurrentCaseName ?? "flow-action")
                    : context.CurrentStepId;
                _modalType = string.IsNullOrWhiteSpace(modalType) ? "native-modal" : modalType;
                _processId = Process.GetCurrentProcess().Id;
                _windowHandle = ResolveUnityWindow(_processId);
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
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        error = "Native modal recovery is only available on Windows.";
                    }
                    else
                    {
                        int attempted = 0;
                        int posted = 0;
                        foreach (IntPtr handle in ResolveUnityWindows(_processId, _windowHandle))
                        {
                            attempted++;
                            if (PostEscape(handle))
                                posted++;
                        }

                        if (attempted == 0)
                        {
                            error = "No visible top-level windows owned by the current Unity process were found.";
                        }
                        else
                        {
                            succeeded = posted > 0;
                            source = $"win32_postmessage_escape_all_windows:{posted}/{attempted}";
                            if (!succeeded)
                                error = $"PostMessage failed with Win32 error {Marshal.GetLastWin32Error()}.";
                        }
                    }
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

        private static IntPtr ResolveUnityWindow(int processId)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return IntPtr.Zero;

            IntPtr foreground = GetForegroundWindow();
            if (IsOwnedUnityWindow(foreground, processId))
                return foreground;

            using (Process process = Process.GetCurrentProcess())
            {
                IntPtr main = process.MainWindowHandle;
                if (IsOwnedUnityWindow(main, processId))
                    return main;
            }

            IntPtr match = IntPtr.Zero;
            EnumWindows((handle, _) =>
            {
                if (!IsOwnedUnityWindow(handle, processId))
                    return true;
                match = handle;
                return false;
            }, IntPtr.Zero);
            return match;
        }

        private static IEnumerable<IntPtr> ResolveUnityWindows(int processId, IntPtr preferred)
        {
            var seen = new HashSet<IntPtr>();
            if (IsOwnedUnityWindow(preferred, processId) && seen.Add(preferred))
                yield return preferred;

            IntPtr foreground = GetForegroundWindow();
            if (IsOwnedUnityWindow(foreground, processId) && seen.Add(foreground))
                yield return foreground;

            using (Process process = Process.GetCurrentProcess())
            {
                IntPtr main = process.MainWindowHandle;
                if (IsOwnedUnityWindow(main, processId) && seen.Add(main))
                    yield return main;
            }

            var matches = new List<IntPtr>();
            EnumWindows((handle, _) =>
            {
                if (IsOwnedUnityWindow(handle, processId) && seen.Add(handle))
                    matches.Add(handle);
                return true;
            }, IntPtr.Zero);

            foreach (IntPtr handle in matches)
                yield return handle;
        }

        private static bool PostEscape(IntPtr handle)
        {
            bool down = PostMessage(handle, WmKeyDown, new IntPtr(VkEscape), IntPtr.Zero);
            bool up = PostMessage(handle, WmKeyUp, new IntPtr(VkEscape), IntPtr.Zero);
            return down && up;
        }

        private static bool IsOwnedUnityWindow(IntPtr handle, int processId)
        {
            if (handle == IntPtr.Zero || !IsWindow(handle) || !IsWindowVisible(handle))
                return false;
            GetWindowThreadProcessId(handle, out uint ownerProcessId);
            return ownerProcessId == (uint)processId;
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new EmptyDisposable();
            public void Dispose() { }
        }

        private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
