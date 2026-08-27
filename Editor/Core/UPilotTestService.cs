// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] public class TestRunMessage     { public TestRunPayload payload; }
    [Serializable] public class TestRunPayload     { public string testMode = "EditMode"; public string testFilter = ""; }

    [Serializable] public class TestListMessage    { public TestListPayload payload; }
    [Serializable] public class TestListPayload    { public string testMode = "EditMode"; }

    [Serializable] public class TestCancelMessage  { public TestCancelPayload payload; }
    [Serializable] public class TestCancelPayload  { public string runGuid = ""; }
    [Serializable] public class TestResultsMessage { public TestResultsPayload payload; }
    [Serializable] public class TestResultsPayload { public string runGuid = ""; }

    [Serializable]
    public class TestResultItemPayload
    {
        public string testName;
        public string testStatus;  // Passed, Failed, Skipped, Inconclusive
        public float  duration;
        public string message;
        public string stackTrace;
    }

    [Serializable]
    public class TestRunResultPayload
    {
        public string status;  // started, running, cancel_requested, cleanup, completed, no_tests, failed, aborted
        public string phase;
        public string testMode;
        public string runGuid;
        public string currentTest;
        public long   startedAt;
        public long   lastProgressAt;
        public bool   isRunning;
        public bool   cancelRequested;
        public bool   cancelAccepted;
        public int    cancelAttemptCount;
        public long   stopRequestedAt;
        public long   stoppingAt;
        public long   cleanupStartedAt;
        public long   endedAt;
        public bool   cleanupPending;
        public bool   forceStopAttempted;
        public bool   forceStopSucceeded;
        public string forceStopError;
        public List<string> cleanupErrors = new List<string>();
        public List<string> unresolvedResources = new List<string>();
        public int    total;
        public int    passed;
        public int    failed;
        public int    skipped;
        public bool   noTests;
        public string discoveryStatus;
        public List<TestResultItemPayload> results = new List<TestResultItemPayload>();
    }

    [Serializable]
    public class TestListResultPayload
    {
        public string testMode;
        public List<string> tests = new List<string>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotTestService
    {
        public static UPilotTestService Instance { get; private set; }

        private readonly UPilotBridge _bridge;
        private TestRunResultPayload _lastResults;
        private bool _isRunning;
        private UnityEngine.Object _activeApi;
        private object _activeCallback;
        private string _activeRunGuid;
        private string _pendingTerminalStatus;
        private bool _cleanupScheduled;
        private long _forceStopDeadline;
        private bool _forceStopRequested;
        private long _recoveryDeadline;
        private static bool s_recoveryCallbackAttached;

        private static string PersistenceDirectory => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Library", "UPilot", "TestRuns"));
        private static string ActiveRunPointerPath => Path.Combine(PersistenceDirectory, "active-run.txt");
        private static string LastRunPointerPath => Path.Combine(PersistenceDirectory, "last-run.txt");

        [InitializeOnLoadMethod]
        private static void BootstrapPersistedRunRecovery()
        {
            // The bridge is initialized later than Unity Test Framework's post-PlayMode
            // resume path. Reattach during editor assembly initialization so a short test
            // cannot finish before the MCP-facing service has been constructed.
            if (Instance == null)
                _ = new UPilotTestService(null);
        }

        public UPilotTestService(UPilotBridge bridge)
        {
            _bridge = bridge;
            Instance = this;
            RecoverPersistedState();
            if (_isRunning && !s_recoveryCallbackAttached)
                EditorApplication.update += ReattachPersistedRun;
        }

        public TestRunResultPayload GetStatusSnapshot()
        {
            return SnapshotStatus();
        }

        public TestRunResultPayload CancelActiveRun(string runGuid = "")
        {
            string knownRunGuid = _activeRunGuid ?? _lastResults?.runGuid;
            if (!string.IsNullOrWhiteSpace(runGuid)
                && !string.Equals(runGuid, knownRunGuid, StringComparison.Ordinal))
                throw new InvalidOperationException($"Active test run does not match runGuid: {runGuid}");
            RequestCancel(force: false);
            return SnapshotStatus();
        }

        public TestRunResultPayload ForceResetActiveRun()
        {
            RequestCancel(force: true);
            return SnapshotStatus();
        }

        public TestRunResultPayload ForceCleanupActiveRun(string runGuid = "")
        {
            string knownRunGuid = _activeRunGuid ?? _lastResults?.runGuid;
            if (!string.IsNullOrWhiteSpace(runGuid)
                && !string.Equals(runGuid, knownRunGuid, StringComparison.Ordinal))
                throw new InvalidOperationException($"Active test run does not match runGuid: {runGuid}");
            RequestCancel(force: true);
            return SnapshotStatus();
        }

        public void RegisterCommands()
        {
            _bridge.Router.Register("test.run",     HandleRunAsync);
            _bridge.Router.Register("test.status",  HandleStatusAsync);
            _bridge.Router.Register("test.cancel",  HandleCancelAsync);
            _bridge.Router.Register("test.force_cleanup", HandleForceCleanupAsync);
            _bridge.Router.Register("test.force_reset", HandleForceResetAsync);
            _bridge.Router.Register("test.results", HandleResultsAsync);
            _bridge.Router.Register("test.list",    HandleListAsync);
        }

        // ── test.run ────────────────────────────────────────────────────────────

        private async Task HandleRunAsync(string id, string json, CancellationToken token)
        {
            var opCtx = UPilotOperationTracker.Instance.GetContext(id);
            var msg = JsonUtility.FromJson<TestRunMessage>(json);
            var p   = msg?.payload ?? new TestRunPayload();

            if (_isRunning)
            {
                await _bridge.SendErrorAsync(id, "TEST_ALREADY_RUNNING", "A test run is already in progress.", token, "test.run");
                return;
            }

            string mode = NormalizeTestMode(p.testMode);
            var filterDesc = p.testFilter ?? "(all)";
            opCtx?.Step("准备运行测试", $"mode={mode} filter={filterDesc}");

            var tcs = new TaskCompletionSource<TestRunResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    _isRunning = true;
                    long startedAt = NowMs();
                    _lastResults = new TestRunResultPayload
                    {
                        testMode = mode,
                        status = "started",
                        phase = "starting",
                        startedAt = startedAt,
                        lastProgressAt = startedAt,
                        isRunning = true,
                    };
                    _activeRunGuid = null;
                    _pendingTerminalStatus = null;
                    PersistSnapshot();

                    // Use TestRunner API via reflection since it's in a separate assembly
                    // UnityEditor.TestTools.TestRunner.Api.TestRunnerApi
                    var apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                    if (apiType == null)
                    {
                        throw new Exception("TestRunnerApi not found. Ensure Test Framework package is installed.");
                    }

                    var api = ScriptableObject.CreateInstance(apiType);
                    _activeApi = api;

                    // Create filter
                    var filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
                    if (filterType == null)
                    {
                        throw new Exception("Test Filter type not found.");
                    }

                    var filter = Activator.CreateInstance(filterType);

                    // Set testMode
                    var testModeEnum = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
                    if (testModeEnum != null)
                    {
                        var modeValue = mode == "PlayMode" ? Enum.Parse(testModeEnum, "PlayMode") : Enum.Parse(testModeEnum, "EditMode");
                        var testModeField = filterType.GetField("testMode");
                        if (testModeField != null)
                            testModeField.SetValue(filter, modeValue);
                    }

                    // Set filter if specified. Preserve the historical exact-name
                    // behavior, while allowing class/namespace isolation through
                    // the Unity Test Framework's regex-capable groupNames field.
                    if (!string.IsNullOrEmpty(p.testFilter))
                    {
                        const string regexPrefix = "regex:";
                        if (p.testFilter.StartsWith(regexPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            var groupNamesField = filterType.GetField("groupNames");
                            if (groupNamesField != null)
                                groupNamesField.SetValue(filter, new[] { p.testFilter.Substring(regexPrefix.Length) });
                        }
                        else
                        {
                            var testNamesField = filterType.GetField("testNames");
                            if (testNamesField != null)
                                testNamesField.SetValue(filter, new[] { p.testFilter });
                        }
                    }

                    // Create ExecutionSettings
                    var execSettingsType = FindType("UnityEditor.TestTools.TestRunner.Api.ExecutionSettings");
                    if (execSettingsType == null)
                    {
                        throw new Exception("ExecutionSettings type not found.");
                    }

                    // Unity 6000.6.0a2 removed the parameterless constructor; use the params Filter[] ctor
                    object execSettings;
                    var filterArrayType = filterType.MakeArrayType();
                    var ctor = execSettingsType.GetConstructor(new[] { filterArrayType });
                    if (ctor != null)
                    {
                        var arr = Array.CreateInstance(filterType, 1);
                        arr.SetValue(filter, 0);
                        execSettings = ctor.Invoke(new object[] { arr });
                    }
                    else
                    {
                        // Fallback: try parameterless ctor for older Unity versions
                        execSettings = Activator.CreateInstance(execSettingsType);
                        var filtersField = execSettingsType.GetField("filters") ?? execSettingsType.GetField("filter");
                        if (filtersField != null)
                        {
                            if (filtersField.FieldType.IsArray)
                            {
                                var arr = Array.CreateInstance(filterType, 1);
                                arr.SetValue(filter, 0);
                                filtersField.SetValue(execSettings, arr);
                            }
                            else
                            {
                                filtersField.SetValue(execSettings, filter);
                            }
                        }
                    }

                    // Register callbacks
                    var callbacksType = FindType("UnityEditor.TestTools.TestRunner.Api.ICallbacks");
                    if (callbacksType != null)
                    {
                        var callbackInstance = CreateCallbackProxy(callbacksType);
                        RegisterCallbacks(apiType, api, callbacksType, callbackInstance);
                        _activeCallback = callbackInstance;
                    }
                    else
                    {
                        throw new Exception("Test Runner ICallbacks type not found.");
                    }

                    // Execute
                    var executeMethod = apiType.GetMethod("Execute");
                    if (executeMethod == null)
                        throw new Exception("TestRunnerApi.Execute method not found.");

                    object executeResult = executeMethod.Invoke(api, new[] { execSettings });
                    _activeRunGuid = Convert.ToString(executeResult);
                    _lastResults.runGuid = _activeRunGuid;
                    _lastResults.lastProgressAt = NowMs();

                    // Return immediately. The registered callback owns the real lifecycle and
                    // transitions test.results to completed/failed only after RunFinished.
                    _lastResults.status = "running";
                    _lastResults.phase = "running";
                    PersistSnapshot();
                    tcs.SetResult(_lastResults);
                }
                catch (Exception ex)
                {
                    _pendingTerminalStatus = "failed";
                    if (_lastResults != null)
                    {
                        _lastResults.status = "cleanup";
                        _lastResults.phase = "cleanup";
                        _lastResults.cleanupPending = true;
                        _lastResults.cleanupStartedAt = NowMs();
                        _lastResults.lastProgressAt = NowMs();
                    }
                    CleanupActiveRun();
                    tcs.SetException(ex);
                }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "test.run", SnapshotStatus(), token);
            }
            catch (Exception ex)
            {
                var root = ex is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : ex;
                await _bridge.SendErrorAsync(id, "TEST_RUN_FAILED", root.Message, token, "test.run");
            }
        }

        // ── test.results ────────────────────────────────────────────────────────

        private async Task HandleStatusAsync(string id, string json, CancellationToken token)
        {
            await _bridge.SendResultAsync(id, "test.status", SnapshotStatus(), token);
        }

        private async Task HandleCancelAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<TestCancelMessage>(json)?.payload ?? new TestCancelPayload();
            string knownRunGuid = _activeRunGuid ?? _lastResults?.runGuid;
            if (!string.IsNullOrWhiteSpace(payload.runGuid)
                && !string.Equals(payload.runGuid, knownRunGuid, StringComparison.Ordinal))
            {
                await _bridge.SendErrorAsync(id, "TEST_RUN_NOT_FOUND", $"Active test run does not match runGuid: {payload.runGuid}", token, "test.cancel");
                return;
            }

            RequestCancel(force: false);
            await _bridge.SendResultAsync(id, "test.cancel", SnapshotStatus(), token);
        }

        private async Task HandleForceResetAsync(string id, string json, CancellationToken token)
        {
            await HandleForceCleanupCoreAsync(id, new TestCancelPayload(), "test.force_reset", token);
        }

        private async Task HandleForceCleanupAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<TestCancelMessage>(json)?.payload ?? new TestCancelPayload();
            await HandleForceCleanupCoreAsync(id, payload, "test.force_cleanup", token);
        }

        private async Task HandleForceCleanupCoreAsync(
            string id,
            TestCancelPayload payload,
            string responseName,
            CancellationToken token)
        {
            string knownRunGuid = _activeRunGuid ?? _lastResults?.runGuid;
            if (!string.IsNullOrWhiteSpace(payload.runGuid)
                && !string.Equals(payload.runGuid, knownRunGuid, StringComparison.Ordinal))
            {
                await _bridge.SendErrorAsync(id, "TEST_RUN_NOT_FOUND", $"Active test run does not match runGuid: {payload.runGuid}", token, responseName);
                return;
            }
            // Force cleanup still starts with the Test Framework's supported cancel API.
            // State is intentionally retained until RunFinished and callback cleanup complete.
            RequestCancel(force: true);
            await _bridge.SendResultAsync(id, responseName, SnapshotStatus(), token);
        }

        private async Task HandleResultsAsync(string id, string json, CancellationToken token)
        {
            var payload = JsonUtility.FromJson<TestResultsMessage>(json)?.payload ?? new TestResultsPayload();
            var result = string.IsNullOrWhiteSpace(payload.runGuid)
                ? SnapshotStatus()
                : LoadPersistedSnapshot(payload.runGuid) ?? new TestRunResultPayload
                {
                    status = "none",
                    phase = "not_found",
                    runGuid = payload.runGuid,
                    isRunning = false,
                };
            await _bridge.SendResultAsync(id, "test.results", result, token);
        }

        // ── test.list ───────────────────────────────────────────────────────────

        private async Task HandleListAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<TestListMessage>(json);
            var p   = msg?.payload ?? new TestListPayload();

            string mode = NormalizeTestMode(p.testMode);

            var tcs = new TaskCompletionSource<TestListResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = new TestListResultPayload { testMode = mode };

                    // Use the same authoritative discovery tree as TestRunnerApi.Execute.
                    // Assembly reflection cannot reproduce parameterized/generated FullName
                    // values and causes valid test filters to complete with NoTests.
                    var apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                    var adaptorType = FindType("UnityEditor.TestTools.TestRunner.Api.ITestAdaptor");
                    var testModeType = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
                    if (apiType == null || adaptorType == null || testModeType == null)
                        throw new Exception("Unity Test Runner discovery API is unavailable.");

                    var api = ScriptableObject.CreateInstance(apiType);
                    var retrieve = apiType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(method => method.Name == "RetrieveTestList" &&
                                                  method.GetParameters().Length == 2 &&
                                                  method.GetParameters()[0].ParameterType == testModeType);
                    if (retrieve == null)
                        throw new Exception("TestRunnerApi.RetrieveTestList(TestMode, callback) was not found.");

                    Action<object> onRetrieved = root =>
                    {
                        try
                        {
                            CollectDiscoveredLeafTests(root, result.tests);
                            result.tests.Sort(StringComparer.Ordinal);
                            tcs.TrySetResult(result);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(api);
                        }
                    };

                    Type callbackType = typeof(Action<>).MakeGenericType(adaptorType);
                    Type wrapperType = typeof(TestListCallback<>).MakeGenericType(adaptorType);
                    object wrapper = Activator.CreateInstance(wrapperType, onRetrieved);
                    Delegate callback = Delegate.CreateDelegate(callbackType, wrapper, wrapperType.GetMethod(nameof(TestListCallback<object>.Invoke)));
                    object modeValue = Enum.Parse(testModeType, mode);
                    retrieve.Invoke(api, new[] { modeValue, callback });
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "test.list", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "TEST_LIST_FAILED", ex.Message, token, "test.list");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static string NormalizeTestMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return "EditMode";
            if (mode.Equals("PlayMode", StringComparison.OrdinalIgnoreCase)) return "PlayMode";
            return "EditMode";
        }

        private TestRunResultPayload SnapshotStatus()
        {
            if (_lastResults == null)
                return new TestRunResultPayload { status = "none", testMode = "", isRunning = false };

            _lastResults.isRunning = _isRunning;
            _lastResults.runGuid = _activeRunGuid ?? _lastResults.runGuid;
            RefreshUnresolvedResources();
            return _lastResults;
        }

        private void RequestCancel(bool force)
        {
            if (!_isRunning || _lastResults == null)
                return;

            if (_lastResults.cancelRequested)
            {
                if (force)
                    ScheduleForceStop();
                return;
            }

            _lastResults.cancelRequested = true;
            _lastResults.cancelAttemptCount++;
            _lastResults.stopRequestedAt = _lastResults.stopRequestedAt > 0
                ? _lastResults.stopRequestedAt
                : NowMs();
            _lastResults.status = force ? "stopping" : "cancel_requested";
            _lastResults.phase = _lastResults.status;
            _lastResults.lastProgressAt = NowMs();

            if (string.IsNullOrWhiteSpace(_activeRunGuid))
                return;

            Type apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
            MethodInfo cancel = apiType?.GetMethod(
                "CancelTestRun",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (cancel == null)
                throw new Exception("TestRunnerApi.CancelTestRun(string) was not found.");

            _lastResults.cancelAccepted = Convert.ToBoolean(cancel.Invoke(null, new object[] { _activeRunGuid }));
            if (_lastResults.cancelAccepted)
            {
                _lastResults.status = "stopping";
                _lastResults.phase = "stopping";
                _lastResults.stoppingAt = _lastResults.stoppingAt > 0
                    ? _lastResults.stoppingAt
                    : NowMs();
                ScheduleCancelCompletionMonitor();
            }
            PersistSnapshot();
            if (force)
                ScheduleForceStop();
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static void CollectDiscoveredLeafTests(object node, List<string> tests)
        {
            if (node == null)
                return;

            Type nodeType = node.GetType();
            bool isSuite = (bool)(nodeType.GetProperty("IsSuite")?.GetValue(node) ?? false);
            var children = nodeType.GetProperty("Children")?.GetValue(node) as IEnumerable;
            bool hasChildren = false;
            if (children != null)
            {
                foreach (object child in children)
                {
                    hasChildren = true;
                    CollectDiscoveredLeafTests(child, tests);
                }
            }

            if (!isSuite && !hasChildren)
            {
                string fullName = nodeType.GetProperty("FullName")?.GetValue(node) as string;
                if (!string.IsNullOrEmpty(fullName))
                    tests.Add(fullName);
            }
        }

        private sealed class TestListCallback<T>
        {
            private readonly Action<object> _callback;

            public TestListCallback(Action<object> callback)
            {
                _callback = callback;
            }

            public void Invoke(T root)
            {
                _callback(root);
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type != null) return type;
            }
            return null;
        }

        private object CreateCallbackProxy(Type callbacksType)
        {
            var create = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(method => method.Name == "Create" && method.IsGenericMethodDefinition);
            var proxy = create.MakeGenericMethod(callbacksType, typeof(TestCallbackProxy)).Invoke(null, null);
            ((TestCallbackProxy)proxy).Initialize(this);
            return proxy;
        }

        private static void RegisterCallbacks(Type apiType, object api, Type callbacksType, object callback)
        {
            var register = apiType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "RegisterCallbacks" && method.IsGenericMethodDefinition);
            if (register == null)
                throw new Exception("TestRunnerApi.RegisterCallbacks method not found.");

            register = register.MakeGenericMethod(callbacksType);
            var args = register.GetParameters()
                .Select((parameter, index) => index == 0
                    ? callback
                    : (parameter.HasDefaultValue ? parameter.DefaultValue : Activator.CreateInstance(parameter.ParameterType)))
                .ToArray();
            register.Invoke(api, args);
        }

        private void OnRunStarted()
        {
            if (_lastResults != null)
            {
                _lastResults.status = "running";
                _lastResults.phase = "running";
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
            }
        }

        private void OnTestStarted(object test)
        {
            if (_lastResults == null)
                return;

            if (Convert.ToBoolean(GetProperty(test, "IsSuite") ?? false))
                return;

            _lastResults.currentTest = Convert.ToString(GetProperty(test, "FullName"))
                ?? Convert.ToString(GetProperty(test, "Name"))
                ?? string.Empty;
            _lastResults.phase = "test";
            _lastResults.lastProgressAt = NowMs();
            PersistSnapshot();
        }

        private void OnTestFinished()
        {
            if (_lastResults != null)
            {
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
            }
        }

        private void OnRunFinished(object rootResult)
        {
            try
            {
                var results = new List<TestResultItemPayload>();
                CollectLeafResults(rootResult, results);
                _pendingTerminalStatus = ApplyRunResults(_lastResults, results, _lastResults.cancelRequested);
            }
            catch (Exception ex)
            {
                _pendingTerminalStatus = "failed";
                _lastResults.failed = Math.Max(1, _lastResults.failed);
                _lastResults.results.Add(new TestResultItemPayload
                {
                    testName = "UPilot.TestRunner.Callback",
                    testStatus = "Failed",
                    message = ex.Message,
                    stackTrace = ex.ToString(),
                });
                _lastResults.total = _lastResults.results.Count;
            }
            finally
            {
                _lastResults.status = "cleanup";
                _lastResults.phase = "cleanup";
                _lastResults.cleanupPending = true;
                _lastResults.cleanupStartedAt = _lastResults.cleanupStartedAt > 0
                    ? _lastResults.cleanupStartedAt
                    : NowMs();
                _lastResults.currentTest = null;
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
                ScheduleCleanup();
            }
        }

        internal static string ApplyRunResults(TestRunResultPayload target, List<TestResultItemPayload> results, bool canceled)
        {
            target.results = results ?? new List<TestResultItemPayload>();
            target.total = target.results.Count;
            target.passed = target.results.Count(item => item.testStatus == "Passed");
            target.failed = target.results.Count(item => item.testStatus == "Failed");
            target.skipped = target.results.Count(item => item.testStatus == "Skipped" || item.testStatus == "Inconclusive");
            target.noTests = target.total == 0;
            target.discoveryStatus = target.noTests ? "no_tests" : "tests_discovered";
            if (canceled) return "aborted";
            if (target.noTests) return "no_tests";
            return target.failed > 0 ? "failed" : "completed";
        }

        private static void CollectLeafResults(object result, List<TestResultItemPayload> output)
        {
            if (result == null) return;
            var children = GetProperty(result, "Children") as IEnumerable;
            var childList = children?.Cast<object>().Where(child => child != null).ToList()
                ?? new List<object>();
            if (childList.Count > 0)
            {
                foreach (var child in childList)
                    CollectLeafResults(child, output);
                return;
            }

            var test = GetProperty(result, "Test");
            if (Convert.ToBoolean(GetProperty(test, "IsSuite") ?? false))
                return;
            var status = Convert.ToString(GetProperty(result, "TestStatus")) ?? "Inconclusive";
            output.Add(new TestResultItemPayload
            {
                testName = Convert.ToString(GetProperty(test, "FullName"))
                    ?? Convert.ToString(GetProperty(test, "Name"))
                    ?? "(unknown)",
                testStatus = status,
                duration = Convert.ToSingle(GetProperty(result, "Duration") ?? 0f),
                message = Convert.ToString(GetProperty(result, "Message")) ?? string.Empty,
                stackTrace = Convert.ToString(GetProperty(result, "StackTrace")) ?? string.Empty,
            });
        }

        private static object GetProperty(object target, string propertyName)
        {
            return target?.GetType()
                .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(target);
        }

        private void CleanupActiveRun()
        {
            EditorApplication.update -= ForceStopTick;
            EditorApplication.update -= CleanupActiveRunFromUpdate;
            EditorApplication.update -= ReattachPersistedRun;
            EditorApplication.update -= RecoveredRunWatchdog;
            s_recoveryCallbackAttached = false;
            EditorApplication.delayCall -= CleanupActiveRun;
            _cleanupScheduled = false;
            try
            {
                var api = _activeApi;
                var callback = _activeCallback;
                _activeApi = null;
                _activeCallback = null;

                if (api != null && callback != null)
                {
                    try
                    {
                        var unregister = api.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(method => method.Name == "UnregisterCallbacks" && method.IsGenericMethodDefinition);
                        var callbacksType = callback.GetType().GetInterfaces()
                            .FirstOrDefault(type => type.FullName == "UnityEditor.TestTools.TestRunner.Api.ICallbacks");
                        if (unregister != null && callbacksType != null)
                            unregister.MakeGenericMethod(callbacksType).Invoke(api, new[] { callback });
                    }
                    catch (Exception ex)
                    {
                        _lastResults?.cleanupErrors.Add($"callback-unregister: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (api != null)
                    UnityEngine.Object.DestroyImmediate(api);
            }
            finally
            {
                if (_lastResults != null)
                {
                    _lastResults.cleanupPending = false;
                    _lastResults.isRunning = false;
                    _lastResults.currentTest = null;
                    _lastResults.lastProgressAt = NowMs();
                    if (!string.IsNullOrWhiteSpace(_pendingTerminalStatus))
                        _lastResults.status = _pendingTerminalStatus;
                    _lastResults.phase = _lastResults.status;
                    _lastResults.endedAt = NowMs();
                    _lastResults.unresolvedResources.Clear();
                    PersistSnapshot(clearActivePointer: true);
                }

                _isRunning = false;
                _activeRunGuid = null;
                _pendingTerminalStatus = null;
                _forceStopRequested = false;
            }
        }

        private void ScheduleCleanup()
        {
            if (_cleanupScheduled)
                return;
            _cleanupScheduled = true;
            EditorApplication.delayCall += CleanupActiveRun;
            EditorApplication.update += CleanupActiveRunFromUpdate;
        }

        private void CleanupActiveRunFromUpdate()
        {
            CleanupActiveRun();
        }

        private void ScheduleForceStop()
        {
            if (!_isRunning || _lastResults == null)
                return;
            _forceStopRequested = true;
            _forceStopDeadline = NowMs() + 5000;
            EditorApplication.update -= ForceStopTick;
            EditorApplication.update += ForceStopTick;
        }

        private void ScheduleCancelCompletionMonitor()
        {
            if (!_isRunning || _lastResults == null)
                return;
            EditorApplication.update -= ForceStopTick;
            EditorApplication.update += ForceStopTick;
        }

        private void ForceStopTick()
        {
            if (!_isRunning || _lastResults == null)
            {
                EditorApplication.update -= ForceStopTick;
                return;
            }
            try
            {
                Type apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                FieldInfo holderField = apiType?.GetField("m_testJobDataHolder", BindingFlags.NonPublic | BindingFlags.Static);
                object holder = holderField?.GetValue(null);
                MethodInfo getRunner = holder?.GetType().GetMethod("GetRunner", BindingFlags.Public | BindingFlags.Instance);
                object runner = getRunner?.Invoke(holder, new object[] { _activeRunGuid });

                // CancelTestRun can unregister the runner without invoking ICallbacks.RunFinished.
                // Once the framework no longer owns the job, it is safe to finish UPilot cleanup.
                if (runner == null)
                {
                    if (_forceStopRequested)
                    {
                        _lastResults.forceStopAttempted = true;
                        _lastResults.forceStopSucceeded = true;
                    }
                    FinalizeCancelledRun();
                    return;
                }

                if (!_forceStopRequested || NowMs() < _forceStopDeadline)
                    return;

                EditorApplication.update -= ForceStopTick;
                _lastResults.forceStopAttempted = true;
                MethodInfo stopRun = runner?.GetType().GetMethod("StopRun", BindingFlags.NonPublic | BindingFlags.Instance);
                if (stopRun == null)
                    throw new Exception("Test Framework active runner cleanup entry was not found.");

                stopRun.Invoke(runner, null);
                _lastResults.forceStopSucceeded = true;
                FinalizeCancelledRun();
            }
            catch (Exception ex)
            {
                Exception root = ex is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : ex;
                _lastResults.forceStopError = root.Message;
                _lastResults.lastProgressAt = NowMs();
            }
        }

        private void FinalizeCancelledRun()
        {
            EditorApplication.update -= ForceStopTick;
            _forceStopRequested = false;
            _lastResults.status = "cleanup";
            _lastResults.phase = "cleanup";
            _lastResults.cleanupPending = true;
            _lastResults.cleanupStartedAt = _lastResults.cleanupStartedAt > 0
                ? _lastResults.cleanupStartedAt
                : NowMs();
            _lastResults.currentTest = null;
            _lastResults.lastProgressAt = NowMs();
            _pendingTerminalStatus = "aborted";
            PersistSnapshot();
            ScheduleCleanup();
        }

        private void RefreshUnresolvedResources()
        {
            if (_lastResults == null)
                return;
            _lastResults.unresolvedResources.Clear();
            if (_isRunning)
                _lastResults.unresolvedResources.Add("test-runner-job");
            if (!string.IsNullOrWhiteSpace(_activeRunGuid))
                _lastResults.unresolvedResources.Add($"run-guid:{_activeRunGuid}");
            if (_activeApi != null)
                _lastResults.unresolvedResources.Add("test-runner-api");
            if (_activeCallback != null)
                _lastResults.unresolvedResources.Add("test-callback");
            _lastResults.cleanupPending = (_lastResults.cancelRequested || _lastResults.status == "cleanup")
                && _lastResults.unresolvedResources.Count > 0;
        }

        private void RecoverPersistedState()
        {
            string runGuid = ReadPointer(ActiveRunPointerPath);
            bool active = !string.IsNullOrWhiteSpace(runGuid);
            if (!active)
                runGuid = ReadPointer(LastRunPointerPath);
            if (string.IsNullOrWhiteSpace(runGuid))
                return;

            _lastResults = LoadPersistedSnapshot(runGuid);
            if (_lastResults == null)
                return;

            _activeRunGuid = active ? runGuid : null;
            _isRunning = active;
            _lastResults.isRunning = _isRunning;
            if (active)
            {
                _lastResults.phase = "recovering_after_reload";
                _recoveryDeadline = NowMs() + 10000;
            }
        }

        private void ReattachPersistedRun()
        {
            if (!_isRunning || string.IsNullOrWhiteSpace(_activeRunGuid) || _activeCallback != null)
            {
                EditorApplication.update -= ReattachPersistedRun;
                return;
            }
            try
            {
                Type apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                Type callbacksType = FindType("UnityEditor.TestTools.TestRunner.Api.ICallbacks");
                if (apiType == null || callbacksType == null)
                {
                    if (NowMs() < _recoveryDeadline)
                        return;
                    MarkRecoveredRunOrphaned();
                    return;
                }
                var api = ScriptableObject.CreateInstance(apiType);
                var callback = CreateCallbackProxy(callbacksType);
                RegisterCallbacks(apiType, api, callbacksType, callback);
                _activeApi = api;
                _activeCallback = callback;
                _lastResults.phase = "running_recovered";
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
                s_recoveryCallbackAttached = true;
                EditorApplication.update -= ReattachPersistedRun;
                EditorApplication.update += RecoveredRunWatchdog;
            }
            catch (Exception ex)
            {
                string error = $"callback-reattach: {ex.GetType().Name}: {ex.Message}";
                if (!_lastResults.cleanupErrors.Contains(error))
                    _lastResults.cleanupErrors.Add(error);
                PersistSnapshot();
            }
        }

        private void MarkRecoveredRunOrphaned()
        {
            EditorApplication.update -= ReattachPersistedRun;
            EditorApplication.update -= RecoveredRunWatchdog;
            s_recoveryCallbackAttached = false;
            _lastResults.status = "failed";
            _lastResults.phase = "orphaned_after_reload";
            _lastResults.cleanupPending = false;
            _lastResults.isRunning = false;
            _lastResults.endedAt = NowMs();
            _lastResults.cleanupErrors.Add("The persisted Test Runner job was no longer active after Domain Reload before a terminal callback was recovered.");
            _isRunning = false;
            _activeRunGuid = null;
            PersistSnapshot(clearActivePointer: true);
        }

        private void RecoveredRunWatchdog()
        {
            if (!_isRunning)
            {
                EditorApplication.update -= RecoveredRunWatchdog;
                return;
            }

            if (NowMs() >= _recoveryDeadline && !IsFrameworkRunActive(_activeRunGuid))
                MarkRecoveredRunOrphaned();
        }

        private static bool IsFrameworkRunActive(string runGuid)
        {
            if (string.IsNullOrWhiteSpace(runGuid))
                return false;
            Type apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
            MethodInfo isRunning = apiType?.GetMethod(
                "IsRunning", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            return isRunning != null && Convert.ToBoolean(isRunning.Invoke(null, new object[] { runGuid }));
        }

        private static bool IsNonTerminal(string status)
        {
            return status == "started" || status == "running" || status == "running_recovered"
                || status == "cancel_requested" || status == "stopping" || status == "cleanup";
        }

        private void PersistSnapshot(bool clearActivePointer = false)
        {
            if (_lastResults == null || string.IsNullOrWhiteSpace(_lastResults.runGuid ?? _activeRunGuid))
                return;
            string runGuid = _lastResults.runGuid ?? _activeRunGuid;
            _lastResults.runGuid = runGuid;
            try
            {
                Directory.CreateDirectory(PersistenceDirectory);
                string path = GetRunPath(runGuid);
                string temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, JsonUtility.ToJson(_lastResults, true));
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temporaryPath, path);
                File.WriteAllText(LastRunPointerPath, runGuid);
                if (clearActivePointer)
                {
                    if (File.Exists(ActiveRunPointerPath))
                        File.Delete(ActiveRunPointerPath);
                }
                else if (_isRunning || IsNonTerminal(_lastResults.status))
                {
                    File.WriteAllText(ActiveRunPointerPath, runGuid);
                }
            }
            catch (Exception ex)
            {
                if (!_lastResults.cleanupErrors.Any(item => item.StartsWith("persistence:", StringComparison.Ordinal)))
                    _lastResults.cleanupErrors.Add($"persistence: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static TestRunResultPayload LoadPersistedSnapshot(string runGuid)
        {
            try
            {
                string path = GetRunPath(runGuid);
                return File.Exists(path)
                    ? JsonUtility.FromJson<TestRunResultPayload>(File.ReadAllText(path))
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string GetRunPath(string runGuid)
        {
            string safeName = new string((runGuid ?? string.Empty)
                .Where(character => char.IsLetterOrDigit(character) || character == '-' || character == '_')
                .ToArray());
            return Path.Combine(PersistenceDirectory, $"{safeName}.json");
        }

        private static string ReadPointer(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty; }
            catch { return string.Empty; }
        }

        public class TestCallbackProxy : DispatchProxy
        {
            private UPilotTestService _service;

            public void Initialize(UPilotTestService service)
            {
                _service = service;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case "RunStarted":
                        _service.OnRunStarted();
                        break;
                    case "RunFinished":
                        _service.OnRunFinished(args != null && args.Length > 0 ? args[0] : null);
                        break;
                    case "TestStarted":
                        _service.OnTestStarted(args != null && args.Length > 0 ? args[0] : null);
                        break;
                    case "TestFinished":
                        _service.OnTestFinished();
                        break;
                }
                return null;
            }
        }

    }
}
