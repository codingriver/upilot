using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class ExpectedModalPayload
    {
        public string title;
        public string[] buttons;
        public string clickButton;
    }

    [Serializable]
    public sealed class ModalObservationPayload
    {
        public string commandId;
        public bool terminal;
        public bool commandReturned;
        public bool waitingForModalUi;
        public bool actionAttempted;
        public bool actionPosted;
        public bool budgetElapsed;
        public string state = "command_pending";
        public string targetInstanceId;
        public string targetTitle;
        public int processId;
        public long ownerWindowHandle;
        public long modalWindowHandle;
        public string title;
        public string modalClass;
        public string[] buttons = Array.Empty<string>();
        public string resultJson;
        public string error;
        public string nextAction = "Query unity_operation_get with the same commandId; do not repeat the command.";
    }

    public static class UPilotModalObserver
    {
        internal const int Capacity = 256;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Watch> Watches = new Dictionary<string, Watch>();

        public sealed class Watch : IDisposable
        {
            private readonly object _gate = new object();
            private readonly ExpectedModalPayload _expected;
            private readonly bool _escapeMenu;
            private readonly long _deadline;
            private readonly ModalObservationPayload _state;
            private Timer _timer;
            private bool _disposed;

            internal Watch(string id, IntPtr owner, string targetId, string targetTitle, ExpectedModalPayload expected, bool escapeMenu, int budgetMs)
            {
                _expected = expected;
                _escapeMenu = escapeMenu;
                _deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(500, Math.Min(budgetMs, 30000));
                _state = new ModalObservationPayload
                {
                    commandId = id, processId = Process.GetCurrentProcess().Id,
                    ownerWindowHandle = owner.ToInt64(), targetInstanceId = targetId ?? "", targetTitle = targetTitle ?? "",
                };
                _timer = new Timer(_ => Observe(), null, 100, 100);
            }

            public ModalObservationPayload Snapshot()
            {
                lock (_gate) return JsonUtility.FromJson<ModalObservationPayload>(JsonUtility.ToJson(_state));
            }

            public void Complete(object result, Exception error = null)
            {
                lock (_gate)
                {
                    _state.commandReturned = true;
                    _state.terminal = true;
                    _state.waitingForModalUi = false;
                    _state.state = error == null ? "command_returned" : "command_failed";
                    _state.error = error?.Message ?? "";
                    _state.resultJson = result == null ? "" : JsonUtility.ToJson(result);
                }
                Dispose();
            }

            public void RecoverGenericMenu() => Observe(true);
            public bool HasActiveModal()
            {
                lock (_gate) return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                    FindModals(_state.processId, new IntPtr(_state.ownerWindowHandle), _state.targetTitle).Count > 0;
            }

            private void Observe(bool forceEscape = false)
            {
                lock (_gate)
                {
                    if (_disposed || _state.commandReturned) return;
                    try
                    {
                        _state.budgetElapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= _deadline;
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            var candidates = FindModals(_state.processId, new IntPtr(_state.ownerWindowHandle), _state.targetTitle);
                            _state.waitingForModalUi = candidates.Count > 0;
                            if (candidates.Count == 1)
                            {
                                var modal = candidates[0];
                                _state.modalWindowHandle = modal.Handle.ToInt64();
                                _state.title = modal.Title;
                                _state.modalClass = modal.ClassName;
                                _state.buttons = modal.Buttons.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                                _state.state = "waitingForModalUi";
                                bool confirm = !_state.budgetElapsed && MatchesExpectedModal(_expected, modal.Title, _state.buttons);
                                bool escape = _escapeMenu && modal.ClassName == "#32768" && (_state.budgetElapsed || forceEscape);
                                if (!_state.actionAttempted && (confirm || escape))
                                {
                                    _state.actionAttempted = true;
                                    // Recheck ownership immediately before the one permitted post.
                                    if (OwnedModal(modal.Handle, _state.processId, new IntPtr(_state.ownerWindowHandle), _state.targetTitle))
                                        _state.actionPosted = confirm
                                            ? PostMessage(modal.Buttons[_expected.clickButton], 0x00F5, IntPtr.Zero, IntPtr.Zero)
                                            : PostMessage(modal.Handle, 0x0100, new IntPtr(0x1B), IntPtr.Zero);
                                }
                            }
                        }
                        if (_state.budgetElapsed) Dispose();
                    }
                    catch (Exception ex) { _state.error = ex.Message; Dispose(); }
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        }

        public static bool MatchesExpectedModal(ExpectedModalPayload expected, string title, string[] buttons) =>
            expected != null && !string.IsNullOrEmpty(expected.title) && expected.title == title &&
            expected.buttons != null && expected.buttons.Length > 0 && !string.IsNullOrEmpty(expected.clickButton) &&
            expected.buttons.Contains(expected.clickButton) &&
            expected.buttons.Distinct(StringComparer.Ordinal).Count() == expected.buttons.Length &&
            new HashSet<string>(expected.buttons, StringComparer.Ordinal).SetEquals(buttons ?? Array.Empty<string>());

        internal static bool IsEmptyExpectedModal(ExpectedModalPayload expected) =>
            expected != null && string.IsNullOrEmpty(expected.title) &&
            (expected.buttons == null || expected.buttons.Length == 0) && string.IsNullOrEmpty(expected.clickButton);

        internal static bool IsOwnerlessGenericMenuCandidate(string modalClass, string targetTitle,
            string ownerTitle, bool ownerless, bool overlapsTarget) =>
            modalClass == "#32768" && ownerless && overlapsTarget && !string.IsNullOrEmpty(targetTitle) &&
            string.Equals(ownerTitle, targetTitle, StringComparison.Ordinal);

        internal static string NormalizeNativeButtonLabel(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('&') < 0) return value ?? "";
            var normalized = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] != '&')
                {
                    normalized.Append(value[index]);
                    continue;
                }
                if (index + 1 < value.Length && value[index + 1] == '&')
                {
                    normalized.Append('&');
                    index++;
                }
            }
            return normalized.ToString();
        }

        public static Watch Arm(string commandId, EditorWindow target = null, ExpectedModalPayload expected = null,
            bool escapeGenericMenu = false, int budgetMs = 5000)
        {
            IntPtr owner = IntPtr.Zero;
            string targetId = "";
            string targetTitle = "";
            if (target != null)
            {
                targetId = UPilotEntityIds.ToWireId(target).ToString();
                targetTitle = target.titleContent?.text ?? "";
                UPilotWindowDiagnostics.TryGetMappedWindowHandle(target, false, out owner, out _);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var foreground = GetForegroundWindow();
                GetWindowThreadProcessId(foreground, out uint pid);
                int currentPid = Process.GetCurrentProcess().Id;
                if (pid == (uint)currentPid) owner = OwnerRoot(foreground);
                if (owner == IntPtr.Zero) owner = FindUniqueUnityRootWindow(currentPid);
            }
            lock (Gate)
            {
                if (Watches.TryGetValue(commandId, out var existing) && !existing.Snapshot().terminal)
                    throw new InvalidOperationException("MODAL_COMMAND_ALREADY_OBSERVED: query the original commandId.");
                foreach (var key in Watches.Where(pair => pair.Value.Snapshot().terminal).Select(pair => pair.Key).Take(Math.Max(0, Watches.Count - Capacity + 1)).ToArray())
                    Watches.Remove(key);
                if (Watches.Count >= Capacity)
                    throw new InvalidOperationException("MODAL_OBSERVER_CAPACITY: resolve pending commands before starting another.");
                var watch = new Watch(commandId, owner, targetId, targetTitle, expected, escapeGenericMenu, budgetMs);
                Watches[commandId] = watch;
                return watch;
            }
        }

        public static ModalObservationPayload Get(string commandId)
        {
            lock (Gate) return Watches.TryGetValue(commandId ?? "", out var watch) ? watch.Snapshot() : null;
        }

        public static async Task<object> RunAsync<T>(UPilotBridge bridge, string commandId, Func<T> action,
            ExpectedModalPayload expected = null, bool escapeGenericMenu = false, Func<EditorWindow> target = null)
        {
            if (IsEmptyExpectedModal(expected)) expected = null;
            if (expected != null && !MatchesExpectedModal(expected, expected.title, expected.buttons))
                throw new ArgumentException("expectedModal requires an exact title, distinct button whitelist and one clickButton.");
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            bridge.EnqueueTracked(commandId, () =>
            {
                Watch watch = null;
                try
                {
                    watch = Arm(commandId, target?.Invoke(), expected, escapeGenericMenu);
                    var result = action();
                    if (escapeGenericMenu)
                        _ = CompleteAfterGenericMenuRecoveryAsync(watch, result, completion);
                    else
                    {
                        watch.Complete(result);
                        completion.TrySetResult(result);
                    }
                }
                catch (Exception ex) { watch?.Complete(null, ex); completion.TrySetException(ex); }
            });
            if (await Task.WhenAny(completion.Task, Task.Delay(5200)).ConfigureAwait(false) == completion.Task)
                return await completion.Task.ConfigureAwait(false);
            return (object)Get(commandId) ?? new ModalObservationPayload { commandId = commandId };
        }

        private static async Task CompleteAfterGenericMenuRecoveryAsync<T>(
            Watch watch, T result, TaskCompletionSource<T> completion)
        {
            try
            {
                while (true)
                {
                    var state = watch.Snapshot();
                    if (state.actionAttempted || state.budgetElapsed || !string.IsNullOrEmpty(state.error))
                    {
                        watch.Complete(result, string.IsNullOrEmpty(state.error) ? null : new InvalidOperationException(state.error));
                        if (string.IsNullOrEmpty(state.error)) completion.TrySetResult(result);
                        else completion.TrySetException(new InvalidOperationException(state.error));
                        return;
                    }
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                watch.Complete(null, ex);
                completion.TrySetException(ex);
            }
        }

        private sealed class ModalWindow
        {
            public IntPtr Handle;
            public string Title;
            public string ClassName;
            public readonly Dictionary<string, IntPtr> Buttons = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        }

        private static List<ModalWindow> FindModals(int pid, IntPtr owner, string targetTitle)
        {
            var result = new List<ModalWindow>();
            if (owner == IntPtr.Zero) return result;
            EnumWindows((handle, _) =>
            {
                if (!OwnedModal(handle, pid, owner, targetTitle)) return true;
                string className = WindowText(handle, true);
                if (className != "#32770" && className != "#32768") return true;
                var modal = new ModalWindow { Handle = handle, Title = WindowText(handle, false), ClassName = className };
                bool duplicate = false;
                EnumChildWindows(handle, (child, __) =>
                {
                    if (WindowText(child, true) != "Button" || !IsWindowVisible(child)) return true;
                    string text = NormalizeNativeButtonLabel(WindowText(child, false));
                    if (modal.Buttons.ContainsKey(text)) duplicate = true;
                    else modal.Buttons[text] = child;
                    return true;
                }, IntPtr.Zero);
                if (duplicate) modal.Buttons.Clear();
                result.Add(modal);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static bool OwnedModal(IntPtr handle, int pid, IntPtr owner, string targetTitle)
        {
            if (owner == IntPtr.Zero || handle == owner || !IsWindowVisible(handle) || !IsWindow(owner)) return false;
            GetWindowThreadProcessId(handle, out uint actual);
            GetWindowThreadProcessId(owner, out uint ownerPid);
            if (actual != (uint)pid || ownerPid != (uint)pid) return false;
            if (IsOwnerlessGenericMenuCandidate(WindowText(handle, true), targetTitle, WindowText(owner, false),
                    GetWindow(handle, 4) == IntPtr.Zero, WindowsOverlap(handle, owner))) return true;
            int depth = 0;
            for (IntPtr parent = GetWindow(handle, 4); parent != IntPtr.Zero && depth++ < 16; parent = GetWindow(parent, 4))
                if (parent == owner) return true;
            if (string.IsNullOrEmpty(targetTitle) || !string.Equals(
                    WindowText(handle, false), targetTitle + " - " + L10n.Tr("Unsaved Changes Detected"),
                    StringComparison.Ordinal)) return false;
            IntPtr modalRoot = OwnerRoot(handle);
            IntPtr targetRoot = OwnerRoot(owner);
            return modalRoot != IntPtr.Zero && modalRoot == targetRoot;
        }

        private static IntPtr OwnerRoot(IntPtr handle)
        {
            IntPtr root = handle;
            for (int depth = 0; root != IntPtr.Zero && depth < 16; depth++)
            {
                IntPtr next = GetWindow(root, 4);
                if (next == IntPtr.Zero) return root;
                root = next;
            }
            return IntPtr.Zero;
        }

        private static IntPtr FindUniqueUnityRootWindow(int pid)
        {
            IntPtr match = IntPtr.Zero;
            int count = 0;
            EnumWindows((handle, _) =>
            {
                GetWindowThreadProcessId(handle, out uint actualPid);
                if (actualPid != (uint)pid || !IsWindowVisible(handle) || GetWindow(handle, 4) != IntPtr.Zero ||
                    !string.Equals(WindowText(handle, true), "UnityContainerWndClass", StringComparison.Ordinal)) return true;
                match = handle;
                count++;
                return true;
            }, IntPtr.Zero);
            return count == 1 ? match : IntPtr.Zero;
        }

        private static bool WindowsOverlap(IntPtr first, IntPtr second)
        {
            return GetWindowRect(first, out var a) && GetWindowRect(second, out var b) &&
                   a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
        }

        private static string WindowText(IntPtr handle, bool className)
        {
            var buffer = new StringBuilder(512);
            if (className) GetClassName(handle, buffer, buffer.Capacity);
            else SendMessageTimeout(handle, 0x000D, new IntPtr(buffer.Capacity), buffer, 0x0002, 100, out _);
            return buffer.ToString();
        }

        private delegate bool EnumProc(IntPtr handle, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int left, top, right, bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr handle, EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder text, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr handle, uint message, IntPtr wParam, StringBuilder text, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    }
}
