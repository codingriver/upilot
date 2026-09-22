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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] public class TestRunMessage     { public TestRunPayload payload; }
    [Serializable] public class TestRunPayload     { public string testMode = "EditMode"; public string testFilter = ""; public string[] testNames; public string[] fixtures; public string[] assemblies; public string[] categories; public string matchMode = "union"; public bool requireAllSelectorsMatch = true; public string expectedSelectionDomain = ""; public string expectedSelectionSnapshotId = ""; public string requestId; public string operationId; public string dirtyScenePolicy = "block"; }

    [Serializable]
    public class TestDirtyScenePayload
    {
        public string scenePath;
        public string sceneName;
        public bool isActive;
        public bool isDirty;
    }

    internal sealed class TestRunPreflightException : InvalidOperationException
    {
        internal readonly string DirtyScenePolicy;
        internal readonly TestDirtyScenePayload[] DirtyScenes;

        internal TestRunPreflightException(string dirtyScenePolicy, TestDirtyScenePayload[] dirtyScenes)
            : base("Unity tests were not started because one or more open scenes have unsaved changes.")
        {
            DirtyScenePolicy = string.IsNullOrWhiteSpace(dirtyScenePolicy) ? "block" : dirtyScenePolicy;
            DirtyScenes = dirtyScenes ?? Array.Empty<TestDirtyScenePayload>();
        }
    }

    internal sealed class TestSelectionStaleException : InvalidOperationException
    {
        internal readonly TestListResultPayload Selection;

        internal TestSelectionStaleException(string message, TestListResultPayload selection)
            : base(message)
        {
            Selection = selection;
        }
    }

    [Serializable] public class TestListMessage    { public TestListPayload payload; }
    [Serializable] public class TestListPayload    { public string testMode = "EditMode"; public string testFilter = ""; public string[] testNames; public string[] fixtures; public string[] assemblies; public string[] categories; public string matchMode = "union"; public bool requireAllSelectorsMatch = true; }

    [Serializable]
    public class TestLeafIdentityPayload
    {
        public string assembly;
        public string fixture;
        public string fullName;
        public string uniqueName;
        public string id;
        public string[] categories;
    }

    [Serializable]
    public class TestSelectorMatchPayload
    {
        public string kind;
        public string selector;
        public int matchedCount;
        // Suggestions are diagnostic-only.  They never broaden a selector or
        // participate in TestRunnerApi.Execute.
        public List<string> candidates = new List<string>();
    }

    [Serializable] public class TestCancelMessage  { public TestCancelPayload payload; }
    [Serializable] public class TestCancelPayload  { public string runGuid = ""; }
    [Serializable] public class TestResultsMessage { public TestResultsPayload payload; }
    [Serializable]
    public class TestResultsPayload
    {
        public string runGuid = "";
        // The Server validates its opaque cursor before passing these bounded values to Unity.
        public bool incremental;
        public long afterEventSequence;
        public int expectedResultStreamVersion;
        public int eventCount = 100;
    }

    [Serializable]
    public class TestResultItemPayload
    {
        // Unique leaf identity is present for both the in-flight callback table
        // and the final TestRunner projection.  FullName is not sufficient for
        // parameterized leaves, and must not be used to merge callbacks.
        public string leafKey;
        public string testName;
        public string testStatus;  // Passed, Failed, Skipped, Inconclusive
        public float  duration;
        public string message;
        public string stackTrace;
    }

    [Serializable]
    public class TestRunEventPayload
    {
        public long sequence;
        public string kind;
        public string leafKey;
        public string testName;
        public string testStatus;
        public long observedAt;
    }

    [Serializable]
    public class TestRunResultPayload
    {
        public int resultStreamVersion = 1;
        public long nextEventSequence;
        public long earliestEventSequence = 1;
        public long lastDeliveredEventSequence;
        public bool cursorAccepted;
        public bool eventsTruncated;
        public List<TestRunEventPayload> events = new List<TestRunEventPayload>();
        public PlayModeTransitionRecord playModeTransition;
        public string status;  // started, running, cancel_requested, cleanup, completed, no_tests, failed, aborted
        public string phase;
        public string testMode;
        public string runGuid;
        public string originatingRequestId;
        public string originatingCommandId;
        public string operationId;
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
        public int    completedLeafCount;
        public int    passedSoFar;
        public int    failedSoFar;
        public int    skippedSoFar;
        public string lastCompletedTest;
        public string firstFailure;
        public bool   intermediate = true;
        public bool   noTests;
        public string discoveryStatus;
        public string requestedFilter;
        public string selectionDomain;
        public string selectionSnapshotId;
        public string expectedSelectionDomain;
        public string expectedSelectionSnapshotId;
        public int    discoveredCount;
        public int    matchedCount;
        public List<TestSelectorMatchPayload> selectors = new List<TestSelectorMatchPayload>();
        public int    discoveredAssemblyCount;
        public List<string> discoveredAssemblies = new List<string>();
        public string outcomeStatus;
        public string cleanupStatus;
        public bool   cleanupSucceeded;
        public bool   resultAuthoritative;
        public string terminalReason;
        public long   firstProgressDeadlineAt;
        public bool   firstProgressObserved;
        public bool   suspectedStuck;
        public string watchdogState;
        public string failureSignature;
        public string nextAction;
        public long snapshotSequence;
        public string persistenceError;
        public string cancelBinding;
        public string runnerState = "unknown";
        public string recoveryDiagnostic;
        public string callbackDomain;
        public string[] selectedLeafIdentities;
        public List<TestResultItemPayload> results = new List<TestResultItemPayload>();
    }

    [Serializable]
    public class TestListResultPayload
    {
        public string matchMode = "union";
        public bool requireAllSelectorsMatch = true;
        public bool selectionValid = true;
        public bool runnerStartAttempted;
        public string selectionDomain;
        public string selectionSnapshotId;
        public List<TestLeafIdentityPayload> selectedTests = new List<TestLeafIdentityPayload>();
        public string testMode;
        public string requestedFilter;
        public string discoveryStatus;
        public int discoveredCount;
        public int matchedCount;
        public int assemblyCount;
        public List<string> assemblies = new List<string>();
        public List<string> tests = new List<string>();
        public List<TestSelectorMatchPayload> selectors = new List<TestSelectorMatchPayload>();
        public List<TestSelectorMatchPayload> unmatchedSelectors = new List<TestSelectorMatchPayload>();
        public List<TestSelectorMatchPayload> duplicateSelectors = new List<TestSelectorMatchPayload>();
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotTestService
    {
        public static UPilotTestService Instance { get; private set; }

        private UPilotBridge _bridge;
        private TestRunResultPayload _lastResults;
        private volatile bool _isRunning;
        public bool IsRunning => _isRunning;
        private UnityEngine.Object _activeApi;
        private object _activeCallback;
        private string _activeRunGuid;
        private string _pendingTerminalStatus;
        private bool _cleanupScheduled;
        private long _forceStopDeadline;
        private bool _forceStopRequested;
        private long _recoveryDeadline;
        private int _recoveryInactiveObservations;
        private long _nextRecoveryProbeAt;
        private const long FirstProgressTimeoutMs = 15000;
        private static bool s_recoveryCallbackAttached;
        private Type _cancelApiType;
        private MethodInfo _cancelMethod;
        private static readonly string CallbackDomain = Guid.NewGuid().ToString("N");
        private long _nextCleanupProbeAt;
        private volatile bool _editorCleanupPending;
        internal Func<Type, UPilotTestRunnerAdapter> RunnerAdapterResolverForTests;
        internal Action<MethodInfo, Type, object> CallbackUnregisterInvokerForTests;
        internal Action<UnityEngine.Object> ApiDestroyerForTests;

        private static string PersistenceDirectory => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "Library", "UPilot", "TestRuns"));
        private static string ActiveRunPointerPath => Path.Combine(PersistenceDirectory, "active-run.txt");
        private static string LastRunPointerPath => Path.Combine(PersistenceDirectory, "last-run.txt");

        [InitializeOnLoadMethod]
        private static void BootstrapPersistedRunRecovery()
        {
            string processRole = UPilotBridge.DetermineProcessRole();
            if (!UPilotBridge.IsMainEditorProcess(processRole))
            {
                Debug.Log(
                    $"[UPilotTestService] Persisted test run recovery skipped for auxiliary Unity process role '{processRole}'.");
                return;
            }

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

        internal static UPilotTestService AttachBridge(UPilotBridge bridge)
        {
            var service = Instance ?? new UPilotTestService(bridge);
            service._bridge = bridge;
            return service;
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
            TestListResultPayload selection = null;
            try
            {
                ValidateExpectedSelectionIdentity(p.expectedSelectionDomain, p.expectedSelectionSnapshotId);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "TEST_SELECTORS_INVALID", ex.Message, token, "test.run");
                return;
            }
            if (p.testNames != null || p.fixtures != null || p.assemblies != null || p.categories != null || p.matchMode != "union")
            {
                try
                {
                    selection = await DiscoverTestsAsync(id, mode, p.testFilter, p.testNames, p.fixtures, p.assemblies, p.categories, p.matchMode, p.requireAllSelectorsMatch);
                }
                catch (Exception ex)
                {
                    await _bridge.SendErrorAsync(id, "TEST_SELECTORS_INVALID", ex.Message, token, "test.run");
                    return;
                }
                if (p.requireAllSelectorsMatch && !selection.selectionValid)
                {
                    await _bridge.SendErrorAsync(id, "TEST_SELECTOR_UNMATCHED", "One or more requested test selectors did not match discovery.", token, "test.run",
                        new ErrorDetailPayload
                        {
                            stage = "selection", runnerStartAttempted = false,
                            nextAction = "Call unity_test_list with the same selectors and choose exact discovered values before retrying.",
                            selection = selection,
                        });
                    return;
                }
                if (!SelectionSnapshotMatches(selection, p.expectedSelectionDomain, p.expectedSelectionSnapshotId))
                {
                    await SendSelectionStaleErrorAsync(id, selection, token);
                    return;
                }
                if (selection.matchedCount == 0)
                {
                    await _bridge.SendResultAsync(id, "test.run", new TestRunResultPayload
                    {
                        status = "no_tests", phase = "no_tests", testMode = mode, noTests = true,
                        discoveryStatus = selection.discoveryStatus, selectors = selection.selectors,
                        discoveredCount = selection.discoveredCount, matchedCount = 0,
                        cleanupStatus = "completed", cleanupSucceeded = true,
                        resultAuthoritative = true,
                        terminalReason = "No selector matched; TestRunnerApi.Execute was not called.",
                    }, token);
                    return;
                }
            }
            if (HasExpectedSelectionIdentity(p.expectedSelectionDomain, p.expectedSelectionSnapshotId) && selection == null)
            {
                await SendSelectionStaleErrorAsync(id, CreateUnavailableSelection(mode, p.testFilter), token,
                    "No current selection snapshot is available for this run; TestRunnerApi.Execute was not called.");
                return;
            }

            try
            {
                await EnsureCleanScenesBeforeRunnerAsync(id, p.dirtyScenePolicy);
            }
            catch (TestRunPreflightException ex)
            {
                await SendDirtyScenePreflightErrorAsync(id, ex, token);
                return;
            }
            var filterDesc = p.testFilter ?? "(all)";
            opCtx?.Step("准备运行测试", $"mode={mode} filter={filterDesc}");

            var tcs = new TaskCompletionSource<TestRunResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                if (_isRunning)
                {
                    tcs.TrySetException(new InvalidOperationException("A test run is already in progress."));
                    return;
                }
                try
                {
                    var finalPreparation = UPilotSceneService.PrepareForAutomation(p.dirtyScenePolicy);
                    EnsureCleanScenesBeforeRunner(finalPreparation.remainingDirtyScenes, p.dirtyScenePolicy);
                    if (!RunSelectionMatches(selection, p.expectedSelectionDomain, p.expectedSelectionSnapshotId))
                        throw new TestSelectionStaleException(
                            "The selection changed before TestRunnerApi.Execute; TestRunnerApi.Execute was not called.", selection);
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
                        requestedFilter = p.testFilter ?? string.Empty,
                        firstProgressDeadlineAt = startedAt + FirstProgressTimeoutMs,
                        firstProgressObserved = false,
                        watchdogState = "waiting_first_progress",
                        cleanupStatus = "not_started",
                        outcomeStatus = "pending",
                        resultAuthoritative = false,
                        callbackDomain = CallbackDomain,
                        selectionDomain = selection?.selectionDomain ?? string.Empty,
                        selectionSnapshotId = selection?.selectionSnapshotId ?? string.Empty,
                        expectedSelectionDomain = p.expectedSelectionDomain ?? string.Empty,
                        expectedSelectionSnapshotId = p.expectedSelectionSnapshotId ?? string.Empty,
                        selectedLeafIdentities = selection?.selectedTests.Select(test => test.uniqueName).ToArray(),
                        discoveredCount = selection?.discoveredCount ?? 0,
                        matchedCount = selection?.matchedCount ?? 0,
                        selectors = selection?.selectors ?? new List<TestSelectorMatchPayload>(),
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
                    if (selection == null && !string.IsNullOrEmpty(p.testFilter))
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

                    Array filters = selection == null ? Array.CreateInstance(filterType, 1)
                        : CreateSelectionFilters(selection, filterType, testModeEnum);
                    if (selection == null) filters.SetValue(filter, 0);

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
                        execSettings = ctor.Invoke(new object[] { filters });
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
                                filtersField.SetValue(execSettings, filters);
                            }
                            else
                            {
                                if (filters.Length != 1)
                                    throw new InvalidOperationException("Runner cannot represent assembly-isolated filters.");
                                filtersField.SetValue(execSettings, filters.GetValue(0));
                            }
                        }
                        else throw new InvalidOperationException("Runner filters field is unavailable; refusing an unfiltered run.");
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

                    _lastResults.originatingRequestId = p.requestId ?? id;
                    _lastResults.originatingCommandId = id;
                    _lastResults.operationId = p.operationId ?? "";
                    if (string.Equals(_lastResults.testMode, "PlayMode", StringComparison.OrdinalIgnoreCase))
                        UPilotPlayModeTransitions.RegisterIntent("testFramework", "play",
                            _lastResults.originatingRequestId, id, _lastResults.operationId, "unity_test_run");
                    object executeResult = executeMethod.Invoke(api, new[] { execSettings });
                    _activeRunGuid = Convert.ToString(executeResult);
                    _lastResults.runGuid = _activeRunGuid;
                    if (_activeCallback is TestCallbackProxy proxy) proxy.BindRun(_activeRunGuid);
                    UPilotPlayModeTransitions.AttachRunIdentity(id, _activeRunGuid);
                    _lastResults.lastProgressAt = NowMs();

                    // Return immediately. The registered callback owns the real lifecycle and
                    // transitions test.results to completed/failed only after RunFinished.
                    _lastResults.status = "running";
                    _lastResults.phase = "running";
                    PersistSnapshot();
                    EditorApplication.update -= TestProgressWatchdog;
                    EditorApplication.update += TestProgressWatchdog;
                    tcs.SetResult(_lastResults);
                }
                catch (Exception ex)
                {
                    if (ex is TestRunPreflightException)
                    {
                        _pendingTerminalStatus = null;
                        tcs.SetException(ex);
                        return;
                    }
                    if (ex is TestSelectionStaleException)
                    {
                        _pendingTerminalStatus = null;
                        tcs.SetException(ex);
                        return;
                    }
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
                if (root is TestRunPreflightException preflight)
                {
                    await SendDirtyScenePreflightErrorAsync(id, preflight, token);
                    return;
                }
                if (root is TestSelectionStaleException stale)
                {
                    await SendSelectionStaleErrorAsync(id, stale.Selection, token, stale.Message);
                    return;
                }
                await _bridge.SendErrorAsync(id, "TEST_RUN_FAILED", root.Message, token, "test.run");
            }
        }

        private async Task SendSelectionStaleErrorAsync(
            string id, TestListResultPayload selection, CancellationToken token,
            string message = "The supplied selection snapshot no longer matches discovery; TestRunnerApi.Execute was not called.")
        {
            await _bridge.SendErrorAsync(id, "TEST_SELECTION_STALE", message, token, "test.run",
                new ErrorDetailPayload
                {
                    stage = "selection",
                    runnerStartAttempted = false,
                    nextAction = "Call unity_test_list again and use its current selectionDomain and selectionSnapshotId before retrying.",
                    selection = selection,
                });
        }

        private async Task EnsureCleanScenesBeforeRunnerAsync(string id, string dirtyScenePolicy)
        {
            var tcs = new TaskCompletionSource<ScenePreparationResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(UPilotSceneService.PrepareForAutomation(dirtyScenePolicy)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            var prepared = await tcs.Task;
            EnsureCleanScenesBeforeRunner(prepared.remainingDirtyScenes, dirtyScenePolicy);
        }

        private async Task SendDirtyScenePreflightErrorAsync(
            string id, TestRunPreflightException error, CancellationToken token)
        {
            await _bridge.SendErrorAsync(
                id,
                "UNSAVED_SCENES",
                error.Message,
                token,
                "test.run",
                new ErrorDetailPayload
                {
                    stage = "preflight",
                    blockedReason = "UnsavedScenes",
                    nextAction = "Select autoSave or ignore in Advanced Settings, or resolve the listed dirty scenes manually.",
                    sideEffectsMayHaveOccurred = false,
                    runnerStartAttempted = false,
                    dirtyScenePolicy = error.DirtyScenePolicy,
                    dirtySceneAction = "blocked",
                    dirtySceneCount = error.DirtyScenes.Length,
                    dirtyScenes = error.DirtyScenes,
                });
        }

        private static SceneInfoPayload[] CaptureOpenSceneState()
        {
            var scenes = new SceneInfoPayload[UnityEngine.SceneManagement.SceneManager.sceneCount];
            for (int index = 0; index < scenes.Length; index++)
                scenes[index] = UPilotSceneService.BuildSceneInfo(
                    UnityEngine.SceneManagement.SceneManager.GetSceneAt(index));
            return scenes;
        }

        internal static void EnsureCleanScenesBeforeRunner(
            IEnumerable<SceneInfoPayload> scenes, string dirtyScenePolicy)
        {
            var dirtyScenes = (scenes ?? Array.Empty<SceneInfoPayload>())
                .Where(scene => scene != null && scene.isDirty)
                .Select(scene => new TestDirtyScenePayload
                {
                    scenePath = scene.scenePath ?? string.Empty,
                    sceneName = scene.sceneName ?? string.Empty,
                    isActive = scene.isActive,
                    isDirty = true,
                })
                .ToArray();
            if (dirtyScenes.Length > 0)
                throw new TestRunPreflightException(dirtyScenePolicy, dirtyScenes);
        }

        // ── test.results ────────────────────────────────────────────────────────

        private async Task HandleStatusAsync(string id, string json, CancellationToken token)
        {
            // Status is intentionally a small, polling-safe summary.  The leaf
            // table and durable event stream are available from test.results
            // (including its cursor protocol); returning either here encourages
            // clients to repeatedly consume the complete history.
            await _bridge.SendResultAsync(id, "test.status", CreateStatusSummary(SnapshotStatus()), token);
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
                : (string.Equals(payload.runGuid, _activeRunGuid ?? _lastResults?.runGuid, StringComparison.Ordinal)
                    ? SnapshotStatus() : LoadPersistedSnapshot(payload.runGuid)) ?? new TestRunResultPayload
                {
                    status = "none",
                    phase = "not_found",
                    runGuid = payload.runGuid,
                    isRunning = false,
                };
            if (payload.incremental)
            {
                if (string.IsNullOrWhiteSpace(payload.runGuid))
                {
                    await _bridge.SendErrorAsync(id, "TEST_RESULT_CURSOR_INVALID",
                        "Incremental test results require the established runGuid.", token, "test.results");
                    return;
                }
                if (string.Equals(result.phase, "not_found", StringComparison.Ordinal))
                {
                    await _bridge.SendResultAsync(id, "test.results", result, token);
                    return;
                }
                if (payload.expectedResultStreamVersion != 0
                    && payload.expectedResultStreamVersion != result.resultStreamVersion)
                {
                    await _bridge.SendErrorAsync(id, "TEST_RESULT_CURSOR_STREAM_MISMATCH",
                        "The result cursor belongs to a different result stream version.", token, "test.results");
                    return;
                }
                long earliest = GetEarliestEventSequence(result);
                if (payload.afterEventSequence < earliest - 1)
                {
                    await _bridge.SendErrorAsync(id, "TEST_RESULT_CURSOR_GAP",
                        "The requested cursor predates the retained test-result event window.", token, "test.results");
                    return;
                }
                result = CreateIncrementalResult(result, payload.afterEventSequence, payload.eventCount);
            }
            await _bridge.SendResultAsync(id, "test.results", result, token);
        }

        // ── test.list ───────────────────────────────────────────────────────────

        private async Task HandleListAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<TestListMessage>(json);
            var p   = msg?.payload ?? new TestListPayload();
            try
            {
                var result = await DiscoverTestsAsync(id, NormalizeTestMode(p.testMode), p.testFilter, p.testNames, p.fixtures, p.assemblies, p.categories, p.matchMode, p.requireAllSelectorsMatch);
                await _bridge.SendResultAsync(id, "test.list", result, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "TEST_LIST_FAILED", ex.Message, token, "test.list");
            }
        }

        private Task<TestListResultPayload> DiscoverTestsAsync(
            string id, string mode, string testFilter, string[] testNames, string[] fixtures,
            string[] assemblies = null, string[] categories = null, string matchMode = "union", bool requireAllSelectorsMatch = true)
        {
            ValidateSelectors(testFilter, testNames, fixtures, assemblies, categories, matchMode);
            var tcs = new TaskCompletionSource<TestListResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = new TestListResultPayload
                    {
                        testMode = mode,
                        requestedFilter = testFilter ?? string.Empty,
                        discoveryStatus = "discovering",
                    };

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
                            tcs.TrySetResult(ResolveTestSelection(root, mode, testFilter, testNames, fixtures, assemblies, categories, matchMode, requireAllSelectorsMatch));
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

            return tcs.Task;
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

        internal static TestRunResultPayload CreateIncrementalResult(TestRunResultPayload source,
            long afterEventSequence, int eventCount)
        {
            var copy = JsonUtility.FromJson<TestRunResultPayload>(JsonUtility.ToJson(source ?? new TestRunResultPayload()));
            copy.events ??= new List<TestRunEventPayload>();
            copy.earliestEventSequence = GetEarliestEventSequence(copy);
            int boundedCount = Math.Max(1, Math.Min(eventCount, 1000));
            copy.events = copy.events.Where(item => item.sequence > afterEventSequence)
                .OrderBy(item => item.sequence).Take(boundedCount).ToList();
            copy.lastDeliveredEventSequence = copy.events.Count == 0
                ? Math.Max(0, afterEventSequence)
                : copy.events[copy.events.Count - 1].sequence;
            copy.cursorAccepted = true;
            return copy;
        }

        internal static TestRunResultPayload CreateStatusSummary(TestRunResultPayload source)
        {
            var copy = JsonUtility.FromJson<TestRunResultPayload>(
                JsonUtility.ToJson(source ?? new TestRunResultPayload()));
            // These collections can contain every completed leaf and up to ten
            // thousand stream events.  Counts, first/last failure diagnostics,
            // stream version, and sequence boundaries remain available above.
            copy.results = new List<TestResultItemPayload>();
            copy.events = new List<TestRunEventPayload>();
            copy.cursorAccepted = false;
            copy.lastDeliveredEventSequence = 0;
            return copy;
        }

        internal static long GetEarliestEventSequence(TestRunResultPayload source)
        {
            if (source?.events == null || source.events.Count == 0)
                return Math.Max(1, (source?.nextEventSequence ?? 0) + 1);
            return source.events.Min(item => item.sequence);
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

            Type apiType = _activeApi != null ? _activeApi.GetType() : FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
            try
            {
                if (_cancelApiType != apiType || _cancelMethod == null)
                {
                    _cancelApiType = apiType;
                    _cancelMethod = ResolveRunnerAdapter(apiType).Cancel;
                }
            }
            catch (Exception ex)
            {
                Exception root = RootException(ex);
                string diagnostic = "Runner binding unavailable before cancellation; no cancel API was invoked. "
                    + root.GetType().Name + ": " + root.Message;
                _lastResults.runnerState = "unknown";
                _lastResults.cancelBinding = diagnostic;
                _lastResults.recoveryDiagnostic = diagnostic;
                _lastResults.nextAction = "Restore the verified Unity Test Framework binding, then retry cancellation for this runGuid.";
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
                throw new InvalidOperationException(diagnostic, root);
            }
            _lastResults.cancelBinding = _cancelMethod.DeclaringType.Assembly.FullName + "::" + _cancelMethod;
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

            PersistSnapshot();
            if (!string.IsNullOrWhiteSpace(_lastResults.persistenceError))
                throw new IOException("Cancellation intent could not be persisted; Runner was not called.");
            try
            {
                _lastResults.cancelAccepted = Convert.ToBoolean(_cancelMethod.Invoke(null, new object[] { _activeRunGuid }));
            }
            catch (Exception ex)
            {
                _lastResults.recoveryDiagnostic = "Cancel response is unknown: " + ex.Message;
                PersistSnapshot();
                ScheduleCancelCompletionMonitor();
                throw;
            }
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
            ScheduleCancelCompletionMonitor();
            if (force)
                ScheduleForceStop();
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        internal static MethodInfo ResolveCancelMethod(Type apiType)
        {
            var method = apiType?.GetMethod("CancelTestRun", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            if (method != null && method.ReturnType == typeof(bool)) return method;
            string candidates = apiType == null ? "(type unavailable)" : string.Join("; ",
                apiType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Where(item => item.Name.Contains("Cancel")).Select(item => item.ToString()));
            throw new InvalidOperationException("TEST_CANCEL_BINDING_UNAVAILABLE: assembly="
                + apiType?.Assembly.FullName + "; type=" + apiType?.FullName + "; candidates=" + candidates);
        }

        private static void CollectDiscoveredLeafTests(
            object node, List<TestLeafIdentityPayload> tests, HashSet<string> assemblies,
            string fixtureName = "", string assemblyName = "", string[] inheritedCategories = null)
        {
            if (node == null)
                return;

            Type nodeType = node.GetType();
            string typeName = Convert.ToString(GetProperty(GetProperty(node, "TypeInfo"), "FullName"));
            if (!string.IsNullOrWhiteSpace(typeName))
                fixtureName = typeName;
            bool isTestAssembly = (bool)(nodeType.GetProperty("IsTestAssembly")?.GetValue(node) ?? false);
            if (isTestAssembly)
            {
                assemblyName = Convert.ToString(nodeType.GetProperty("Name")?.GetValue(node));
                if (string.IsNullOrWhiteSpace(assemblyName))
                    assemblyName = Convert.ToString(nodeType.GetProperty("FullName")?.GetValue(node));
                if (!string.IsNullOrWhiteSpace(assemblyName))
                    assemblies.Add(assemblyName);
            }
            var categories = new HashSet<string>(inheritedCategories ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (GetProperty(node, "Categories") is IEnumerable categoryValues)
                foreach (object value in categoryValues)
                    if (value is string category && !string.IsNullOrEmpty(category)) categories.Add(category);

            bool isSuite = (bool)(nodeType.GetProperty("IsSuite")?.GetValue(node) ?? false);
            var children = nodeType.GetProperty("Children")?.GetValue(node) as IEnumerable;
            bool hasChildren = false;
            if (children != null)
            {
                foreach (object child in children)
                {
                    hasChildren = true;
                    CollectDiscoveredLeafTests(child, tests, assemblies, fixtureName, assemblyName, categories.ToArray());
                }
            }

            if (!isSuite && !hasChildren)
            {
                string fullName = nodeType.GetProperty("FullName")?.GetValue(node) as string;
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    assemblyName = Convert.ToString(GetProperty(node, "AssemblyName"));
                    object typeInfo = GetProperty(node, "TypeInfo");
                    object assembly = GetProperty(typeInfo, "Assembly");
                    if (string.IsNullOrWhiteSpace(assemblyName))
                        assemblyName = Convert.ToString(GetProperty(assembly, "Name"));
                }
                if (!string.IsNullOrWhiteSpace(assemblyName))
                    assemblies.Add(assemblyName);
                if (!string.IsNullOrEmpty(fullName))
                    tests.Add(new TestLeafIdentityPayload
                    {
                        assembly = assemblyName ?? "", fixture = fixtureName, fullName = fullName,
                        uniqueName = Convert.ToString(GetProperty(node, "UniqueName")),
                        id = Convert.ToString(GetProperty(node, "Id")),
                        categories = categories.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                    });
            }
        }

        public static void ValidateSelectors(string testFilter, string[] testNames, string[] fixtures,
            string[] assemblies = null, string[] categories = null, string matchMode = "union")
        {
            if (matchMode == null || (!string.Equals(matchMode, "union", StringComparison.OrdinalIgnoreCase) && !string.Equals(matchMode, "intersection", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("matchMode must be union or intersection.");
            var groups = new[] { testNames, fixtures, assemblies, categories };
            if (groups.All(group => group == null)) return;
            if (!string.IsNullOrWhiteSpace(testFilter))
                throw new ArgumentException("testFilter cannot be combined with selector arrays.");
            var selectors = groups.Where(group => group != null).SelectMany(group => group).ToArray();
            if (groups.Any(group => group != null && group.Length == 0) ||
                selectors.Length == 0 || selectors.Length > 256 || selectors.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Provide 1 to 256 non-empty exact selectors; empty arrays never mean all tests.");
        }

        internal static void ValidateExpectedSelectionIdentity(string expectedSelectionDomain,
            string expectedSelectionSnapshotId)
        {
            bool hasDomain = !string.IsNullOrWhiteSpace(expectedSelectionDomain);
            bool hasSnapshot = !string.IsNullOrWhiteSpace(expectedSelectionSnapshotId);
            if (hasDomain != hasSnapshot)
                throw new ArgumentException("expectedSelectionDomain and expectedSelectionSnapshotId must be supplied together.");
        }

        internal static bool SelectionSnapshotMatches(TestListResultPayload selection,
            string expectedSelectionDomain, string expectedSelectionSnapshotId)
        {
            ValidateExpectedSelectionIdentity(expectedSelectionDomain, expectedSelectionSnapshotId);
            // The expected identity is an optimistic-concurrency assertion, not a
            // substitute for validating the discovered selection itself.  Legacy
            // callers may omit it, but they must never be able to start a run from
            // a selection belonging to an old callback domain or with a forged
            // snapshot id.
            if (selection == null
                || !string.Equals(selection.selectionDomain, CallbackDomain, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(selection.selectionSnapshotId)
                || !string.Equals(selection.selectionSnapshotId, CreateSelectionSnapshotId(selection), StringComparison.Ordinal))
                return false;
            if (!HasExpectedSelectionIdentity(expectedSelectionDomain, expectedSelectionSnapshotId))
                return true;
            return string.Equals(expectedSelectionDomain, selection.selectionDomain, StringComparison.Ordinal)
                && string.Equals(expectedSelectionSnapshotId, selection.selectionSnapshotId, StringComparison.Ordinal);
        }

        internal static bool RunSelectionMatches(TestListResultPayload selection,
            string expectedSelectionDomain, string expectedSelectionSnapshotId)
        {
            ValidateExpectedSelectionIdentity(expectedSelectionDomain, expectedSelectionSnapshotId);
            if (selection == null)
                return !HasExpectedSelectionIdentity(expectedSelectionDomain, expectedSelectionSnapshotId);
            return SelectionSnapshotMatches(selection, expectedSelectionDomain, expectedSelectionSnapshotId);
        }

        private static bool HasExpectedSelectionIdentity(string expectedSelectionDomain,
            string expectedSelectionSnapshotId) =>
            !string.IsNullOrWhiteSpace(expectedSelectionDomain)
            && !string.IsNullOrWhiteSpace(expectedSelectionSnapshotId);

        private static TestListResultPayload CreateUnavailableSelection(string mode, string testFilter) =>
            new TestListResultPayload
            {
                testMode = mode,
                requestedFilter = testFilter ?? string.Empty,
                selectionDomain = CallbackDomain,
                selectionSnapshotId = string.Empty,
                runnerStartAttempted = false,
            };

        internal static string CreateSelectionSnapshotId(TestListResultPayload selection)
        {
            if (selection == null)
                return string.Empty;
            var canonical = new StringBuilder();
            void Append(string value)
            {
                string normalized = value ?? string.Empty;
                canonical.Append(normalized.Length).Append(':').Append(normalized).Append('|');
            }
            Append(selection.testMode);
            Append(selection.requestedFilter);
            Append(selection.matchMode);
            Append(selection.requireAllSelectorsMatch ? "true" : "false");
            Append(selection.discoveryStatus);
            Append(selection.discoveredCount.ToString());
            Append(selection.matchedCount.ToString());
            foreach (var test in (selection.selectedTests ?? new List<TestLeafIdentityPayload>())
                         .OrderBy(item => item.assembly, StringComparer.Ordinal)
                         .ThenBy(item => item.fixture, StringComparer.Ordinal)
                         .ThenBy(item => item.fullName, StringComparer.Ordinal)
                         .ThenBy(item => item.uniqueName, StringComparer.Ordinal))
            {
                Append(test.assembly);
                Append(test.fixture);
                Append(test.fullName);
                Append(test.uniqueName);
                Append(test.id);
                foreach (var category in (test.categories ?? Array.Empty<string>()).OrderBy(item => item, StringComparer.Ordinal))
                    Append(category);
            }
            foreach (var selector in (selection.selectors ?? new List<TestSelectorMatchPayload>()))
            {
                Append(selector.kind);
                Append(selector.selector);
                Append(selector.matchedCount.ToString());
                foreach (var candidate in (selector.candidates ?? new List<string>()).OrderBy(item => item, StringComparer.Ordinal))
                    Append(candidate);
            }
            foreach (var selector in (selection.unmatchedSelectors ?? new List<TestSelectorMatchPayload>()))
            {
                Append(selector.kind);
                Append(selector.selector);
                Append(selector.matchedCount.ToString());
                foreach (var candidate in (selector.candidates ?? new List<string>()).OrderBy(item => item, StringComparer.Ordinal))
                    Append(candidate);
            }
            foreach (var selector in (selection.duplicateSelectors ?? new List<TestSelectorMatchPayload>()))
            {
                Append(selector.kind);
                Append(selector.selector);
                Append(selector.matchedCount.ToString());
                foreach (var candidate in (selector.candidates ?? new List<string>()).OrderBy(item => item, StringComparer.Ordinal))
                    Append(candidate);
            }
            using var sha256 = SHA256.Create();
            return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString())))
                .Replace("-", string.Empty).ToLowerInvariant();
        }

        public static TestListResultPayload ResolveTestSelection(
            object root, string mode, string testFilter, string[] testNames = null, string[] fixtures = null,
            string[] assemblies = null, string[] categories = null, string matchMode = "union", bool requireAllSelectorsMatch = true)
        {
            ValidateSelectors(testFilter, testNames, fixtures, assemblies, categories, matchMode);
            testFilter = testFilter ?? string.Empty;
            matchMode = matchMode.ToLowerInvariant();
            var allTests = new List<TestLeafIdentityPayload>();
            var discoveredAssemblies = new HashSet<string>(StringComparer.Ordinal);
            CollectDiscoveredLeafTests(root, allTests, discoveredAssemblies);
            var result = new TestListResultPayload
            {
                testMode = mode, requestedFilter = testFilter, matchMode = matchMode,
                requireAllSelectorsMatch = requireAllSelectorsMatch, selectionDomain = CallbackDomain,
                discoveredCount = allTests.Count,
                assemblies = discoveredAssemblies.OrderBy(name => name, StringComparer.Ordinal).ToList(),
                assemblyCount = discoveredAssemblies.Count,
            };
            var groups = new[] { testNames, fixtures, assemblies, categories };
            var selected = new HashSet<TestLeafIdentityPayload>();
            if (groups.All(group => group == null))
            {
                var legacyNames = new HashSet<string>(FilterDiscoveredTests(allTests.Select(item => item.fullName).ToList(), testFilter), StringComparer.Ordinal);
                selected.UnionWith(allTests.Where(item => legacyNames.Contains(item.fullName) || item.fixture == testFilter));
            }
            else
            {
                bool first = true;
                string[] kinds = { "testName", "fixture", "assembly", "category" };
                for (int index = 0; index < groups.Length; index++)
                {
                    if (groups[index] == null) continue;
                    var groupMatches = new HashSet<TestLeafIdentityPayload>();
                    var seenSelectors = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string rawSelector in groups[index])
                    {
                        string selector = NormalizeSelector(kinds[index], rawSelector);
                        if (!seenSelectors.Add(selector))
                        {
                            result.duplicateSelectors.Add(new TestSelectorMatchPayload { kind = kinds[index], selector = selector });
                            continue;
                        }
                        var matches = allTests.Where(item => index == 0 ? item.fullName == selector :
                            index == 1 ? item.fixture == selector :
                            index == 2 ? NormalizeAssemblyName(item.assembly) == NormalizeAssemblyName(selector) :
                            item.categories.Contains(selector)).ToList();
                        var selectorMatch = new TestSelectorMatchPayload
                        {
                            kind = kinds[index],
                            selector = selector,
                            matchedCount = matches.Count,
                            candidates = matches.Count == 0
                                ? SelectorCandidates(allTests, index)
                                : new List<string>(),
                        };
                        result.selectors.Add(selectorMatch);
                        if (matches.Count == 0)
                            result.unmatchedSelectors.Add(selectorMatch);
                        groupMatches.UnionWith(matches);
                    }
                    if (first || matchMode == "union") selected.UnionWith(groupMatches);
                    else selected.IntersectWith(groupMatches);
                    first = false;
                }
            }
            if (selected.GroupBy(item => NormalizeAssemblyName(item.assembly) + "\n" + item.fullName, StringComparer.Ordinal).Any(group => group.Count() > 1))
                throw new ArgumentException("Duplicate full names within one assembly cannot be isolated by this Runner.");
            result.selectedTests = selected.OrderBy(item => item.assembly, StringComparer.Ordinal).ThenBy(item => item.fullName, StringComparer.Ordinal).ToList();
            result.tests = result.selectedTests.Select(item => item.fullName).ToList();
            result.matchedCount = result.selectedTests.Count;
            result.selectionValid = result.unmatchedSelectors.Count == 0;
            result.discoveryStatus = result.discoveredCount == 0 ? "no_tests" : (result.matchedCount == 0 ? "filter_no_match" : "tests_discovered");
            result.selectionSnapshotId = CreateSelectionSnapshotId(result);
            return result;
        }

        private static string NormalizeSelector(string kind, string value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            return kind == "assembly" ? NormalizeAssemblyName(normalized) : normalized;
        }

        private static List<string> SelectorCandidates(
            IEnumerable<TestLeafIdentityPayload> tests, int selectorKindIndex)
        {
            var testList = tests?.ToList() ?? new List<TestLeafIdentityPayload>();
            if (selectorKindIndex == 0)
            {
                // A bare full name remains compatible for a single assembly.  When
                // the same name is discovered from more than one assembly, include
                // the normalized assembly so a diagnostic never hides the second
                // exact, assembly-isolated leaf.
                var duplicatedAcrossAssemblies = new HashSet<string>(
                    testList.Where(item => !string.IsNullOrWhiteSpace(item.fullName))
                        .GroupBy(item => item.fullName, StringComparer.Ordinal)
                        .Where(group => group.Select(item => NormalizeAssemblyName(item.assembly))
                            .Distinct(StringComparer.Ordinal).Skip(1).Any())
                        .Select(group => group.Key),
                    StringComparer.Ordinal);
                return testList
                    .Where(item => !string.IsNullOrWhiteSpace(item.fullName))
                    .Select(item => duplicatedAcrossAssemblies.Contains(item.fullName)
                        ? NormalizeAssemblyName(item.assembly) + "::" + item.fullName
                        : item.fullName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .Take(10)
                    .ToList();
            }
            IEnumerable<string> values = selectorKindIndex switch
            {
                1 => testList.Select(item => item.fixture),
                2 => testList.Select(item => NormalizeAssemblyName(item.assembly)),
                3 => testList.SelectMany(item => item.categories ?? Array.Empty<string>()),
                _ => Array.Empty<string>(),
            };
            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(10)
                .ToList();
        }

        private static string NormalizeAssemblyName(string name) =>
            name != null && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name ?? "";

        internal static Array CreateSelectionFilters(TestListResultPayload selection, Type filterType, Type testModeType)
        {
            if (selection.selectedTests.Count == 0)
                throw new InvalidOperationException("Empty selection must not reach TestRunnerApi.Execute.");
            var namesField = filterType.GetField("testNames");
            var assembliesField = filterType.GetField("assemblyNames");
            var modeField = filterType.GetField("testMode");
            if (namesField == null || assembliesField == null || modeField == null || testModeType == null)
                throw new InvalidOperationException("Runner cannot represent exact assembly-isolated selectors.");
            var groups = selection.selectedTests.GroupBy(item => NormalizeAssemblyName(item.assembly), StringComparer.Ordinal).ToArray();
            var filters = Array.CreateInstance(filterType, groups.Length);
            for (int index = 0; index < groups.Length; index++)
            {
                if (string.IsNullOrEmpty(groups[index].Key))
                    throw new InvalidOperationException("Selected test has no verified assembly identity.");
                var filter = Activator.CreateInstance(filterType);
                modeField.SetValue(filter, Enum.Parse(testModeType, selection.testMode));
                namesField.SetValue(filter, groups[index].Select(item => item.fullName).ToArray());
                assembliesField.SetValue(filter, new[] { groups[index].Key });
                filters.SetValue(filter, index);
            }
            return filters;
        }

        private static List<string> FilterDiscoveredTests(List<string> tests, string testFilter)
        {
            if (string.IsNullOrWhiteSpace(testFilter))
                return new List<string>(tests);
            const string regexPrefix = "regex:";
            if (testFilter.StartsWith(regexPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var regex = new Regex(testFilter.Substring(regexPrefix.Length), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                return tests.Where(name => regex.IsMatch(name)).ToList();
            }
            return tests.Where(name => string.Equals(name, testFilter, StringComparison.Ordinal)).ToList();
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
            ((TestCallbackProxy)proxy).Initialize(this, _activeRunGuid, CallbackDomain);
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
            if (!_isRunning || _cleanupScheduled || _lastResults?.resultAuthoritative == true) return;
            if (_lastResults != null)
            {
                MarkFirstProgressObserved();
                _lastResults.status = "running";
                _lastResults.phase = "running";
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
            }
        }

        private void OnTestStarted(object test)
        {
            if (!_isRunning || _cleanupScheduled || _lastResults == null || _lastResults.resultAuthoritative)
                return;

            if (Convert.ToBoolean(GetProperty(test, "IsSuite") ?? false))
                return;

            MarkFirstProgressObserved();
            _lastResults.currentTest = Convert.ToString(GetProperty(test, "FullName"))
                ?? Convert.ToString(GetProperty(test, "Name"))
                ?? string.Empty;
            _lastResults.phase = "test";
            _lastResults.lastProgressAt = NowMs();
            AppendEvent(_lastResults, "test_started", _lastResults.currentTest, "");
            PersistSnapshot();
        }

        private void OnTestFinished(object testResult)
        {
            if (!_isRunning || _cleanupScheduled || _lastResults?.resultAuthoritative == true) return;
            var test = GetProperty(testResult, "Test");
            if (test == null)
            {
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
                return;
            }
            if (Convert.ToBoolean(GetProperty(test, "IsSuite") ?? false)) return;
            string name = Convert.ToString(GetProperty(test, "FullName"))
                ?? Convert.ToString(GetProperty(test, "Name")) ?? string.Empty;
            string leafKey = Convert.ToString(GetProperty(test, "UniqueName"));
            // A name is display text, not a stable parameterized-leaf identity.
            // Keep a diagnostic/progress timestamp for an unmappable callback,
            // but never let it inflate live passed/failed/skipped counts.
            if (string.IsNullOrWhiteSpace(leafKey))
            {
                _lastResults.lastProgressAt = NowMs();
                _lastResults.recoveryDiagnostic = "Ignored TestFinished callback without a stable UniqueName; live leaf counters were not changed.";
                PersistSnapshot();
                return;
            }
            string status = Convert.ToString(GetProperty(testResult, "TestStatus")) ?? "Inconclusive";
            if (RecordLeafCompletion(_lastResults, leafKey, name, status))
            {
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
            }
        }

        private void OnRunFinished(object rootResult)
        {
            if (!_isRunning || _lastResults == null || _cleanupScheduled) return;
            // UTF's PlayMode RunFinished callback precedes its ExitPlayModeTask.
            // Do not invent a new exit intent if an exit is already in progress.
            if (ShouldRecordFrameworkExit(_lastResults,
                    EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode))
                UPilotPlayModeTransitions.RegisterIntent("testFramework", "edit",
                    _lastResults.originatingRequestId, _lastResults.originatingCommandId,
                    _lastResults.operationId, "testFramework.RunFinished", _lastResults.runGuid);
            try
            {
                var results = new List<TestResultItemPayload>();
                CollectLeafResults(rootResult, results);
                foreach (var item in results)
                    RecordFinalLeafResult(_lastResults, item);
                _pendingTerminalStatus = ApplyRunResults(_lastResults, results, _lastResults.cancelRequested);
                _lastResults.completedLeafCount = _lastResults.total;
                _lastResults.passedSoFar = _lastResults.passed;
                _lastResults.failedSoFar = _lastResults.failed;
                _lastResults.skippedSoFar = _lastResults.skipped;
                _lastResults.lastCompletedTest = results.LastOrDefault()?.testName ?? string.Empty;
                _lastResults.firstFailure = results.FirstOrDefault(item => item.testStatus == "Failed")?.testName ?? string.Empty;
                _lastResults.intermediate = false;
                _lastResults.outcomeStatus = _pendingTerminalStatus;
                _lastResults.resultAuthoritative = true;
            }
            catch (Exception ex)
            {
                _pendingTerminalStatus = null;
                _lastResults.outcomeStatus = "unknown";
                _lastResults.resultAuthoritative = false;
                _lastResults.recoveryDiagnostic = "RunFinished result decode failed: " + ex;
            }
            finally
            {
                _lastResults.status = "cleanup";
                _lastResults.phase = "cleanup";
                _lastResults.cleanupStatus = "running";
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

        internal static bool ShouldRecordFrameworkExit(TestRunResultPayload result, bool stablePlayMode)
        {
            return stablePlayMode && result != null && !string.IsNullOrEmpty(result.runGuid)
                && string.Equals(result.testMode, "PlayMode", StringComparison.OrdinalIgnoreCase);
        }

        internal void ObservePlayModeTransition(PlayModeTransitionRecord transition)
        {
            if (_lastResults == null || transition == null || string.IsNullOrEmpty(transition.runGuid)
                || transition.runGuid != _lastResults.runGuid || transition.at < _lastResults.startedAt) return;
            _lastResults.playModeTransition = transition;
            PersistSnapshot(clearActivePointer: !_isRunning);
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

        internal static bool RecordLeafCompletion(TestRunResultPayload target, string leafKey, string testName, string testStatus)
        {
            if (target == null) return false;
            leafKey = string.IsNullOrWhiteSpace(leafKey) ? testName ?? string.Empty : leafKey;
            target.events ??= new List<TestRunEventPayload>();
            if (target.events.Any(item => item.kind == "leaf_completed"
                && string.Equals(item.leafKey, leafKey, StringComparison.Ordinal)))
                return false;
            AppendEvent(target, "leaf_completed", testName, testStatus, leafKey);
            UpsertLeafResult(target, new TestResultItemPayload
            {
                leafKey = leafKey,
                testName = testName ?? string.Empty,
                testStatus = testStatus ?? string.Empty,
                duration = 0f,
                message = string.Empty,
                stackTrace = string.Empty,
            });
            target.completedLeafCount++;
            target.lastCompletedTest = testName ?? string.Empty;
            target.intermediate = true;
            switch (testStatus)
            {
                case "Passed": target.passedSoFar++; break;
                case "Failed":
                    target.failedSoFar++;
                    if (string.IsNullOrWhiteSpace(target.firstFailure)) target.firstFailure = testName ?? string.Empty;
                    break;
                case "Skipped":
                case "Inconclusive": target.skippedSoFar++; break;
            }
            return true;
        }

        internal static void RecordFinalLeafResult(TestRunResultPayload target, TestResultItemPayload item)
        {
            if (target == null || item == null) return;
            item.leafKey = string.IsNullOrWhiteSpace(item.leafKey) ? item.testName ?? string.Empty : item.leafKey;
            UpsertLeafResult(target, item);
            // UTF can deliver the final result callback more than once while a
            // run is being recovered.  Compare with this leaf's latest event,
            // including an earlier correction, so the same correction is not
            // appended repeatedly to the persisted cursor stream.
            var previous = target.events?.LastOrDefault(eventItem =>
                string.Equals(eventItem.leafKey, item.leafKey, StringComparison.Ordinal));
            if (previous == null)
            {
                AppendEvent(target, "leaf_completed", item.testName, item.testStatus, item.leafKey);
                return;
            }
            if (!string.Equals(previous.testStatus, item.testStatus, StringComparison.Ordinal))
                AppendEvent(target, "leaf_corrected", item.testName, item.testStatus, previous.leafKey);
        }

        private static void UpsertLeafResult(TestRunResultPayload target, TestResultItemPayload item)
        {
            if (target == null || item == null) return;
            target.results ??= new List<TestResultItemPayload>();
            string leafKey = string.IsNullOrWhiteSpace(item.leafKey) ? item.testName ?? string.Empty : item.leafKey;
            item.leafKey = leafKey;
            int index = target.results.FindIndex(existing => existing != null
                && string.Equals(existing.leafKey, leafKey, StringComparison.Ordinal));
            if (index >= 0) target.results[index] = item;
            else target.results.Add(item);
        }

        private static void AppendEvent(TestRunResultPayload target, string kind, string testName, string testStatus,
            string leafKey = "")
        {
            if (target == null) return;
            target.events ??= new List<TestRunEventPayload>();
            target.nextEventSequence++;
            target.events.Add(new TestRunEventPayload
            {
                sequence = target.nextEventSequence,
                kind = kind ?? string.Empty,
                leafKey = leafKey ?? string.Empty,
                testName = testName ?? string.Empty,
                testStatus = testStatus ?? string.Empty,
                observedAt = NowMs(),
            });
            const int maxRetainedEvents = 10000;
            if (target.events.Count > maxRetainedEvents)
            {
                target.events.RemoveRange(0, target.events.Count - maxRetainedEvents);
                target.eventsTruncated = true;
            }
            target.earliestEventSequence = GetEarliestEventSequence(target);
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
                leafKey = Convert.ToString(GetProperty(test, "UniqueName"))
                    ?? Convert.ToString(GetProperty(test, "FullName"))
                    ?? Convert.ToString(GetProperty(test, "Name"))
                    ?? "(unknown)",
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
            if (_lastResults == null) return;
            if (NowMs() < _nextCleanupProbeAt) return;
            _nextCleanupProbeAt = NowMs() + 100;
            // A failed result write must be repaired before releasing its callback/API.
            if (!string.IsNullOrWhiteSpace(_lastResults.persistenceError))
            {
                PersistSnapshot();
                if (!string.IsNullOrWhiteSpace(_lastResults.persistenceError)) return;
            }
            if (!string.IsNullOrWhiteSpace(_activeRunGuid))
            {
                var runnerState = ProbeFrameworkRun(out _);
                bool editorClean = !string.Equals(_lastResults.testMode, "PlayMode", StringComparison.OrdinalIgnoreCase)
                    || (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode
                        && !EditorApplication.isCompiling && !EditorApplication.isUpdating);
                _editorCleanupPending = !editorClean;
                if (runnerState != "inactive" || !editorClean)
                {
                    _lastResults.cleanupPending = true;
                    _lastResults.cleanupSucceeded = false;
                    RefreshUnresolvedResources();
                    PersistSnapshot();
                    return;
                }
            }
            EditorApplication.update -= ForceStopTick;
            EditorApplication.update -= CleanupActiveRunFromUpdate;
            EditorApplication.update -= ReattachPersistedRun;
            EditorApplication.update -= RecoveredRunWatchdog;
            EditorApplication.update -= TestProgressWatchdog;
            s_recoveryCallbackAttached = false;
            EditorApplication.delayCall -= CleanupActiveRun;
            _cleanupScheduled = false;
            try { ReleaseOwnedRunnerResources(); }
            finally
            {
                if (_lastResults != null)
                {
                    if (_activeCallback != null || _activeApi != null)
                    {
                        _lastResults.cleanupPending = true;
                        _lastResults.cleanupSucceeded = false;
                        _lastResults.cleanupStatus = "failed";
                        _lastResults.nextAction = "Retry cleanup for the same runGuid after resolving the reported callback/API release error.";
                        RefreshUnresolvedResources();
                        PersistSnapshot();
                        ScheduleCleanup();
                    }
                    else if (!_lastResults.resultAuthoritative && string.IsNullOrWhiteSpace(_pendingTerminalStatus))
                    {
                        _lastResults.cleanupPending = false;
                        _lastResults.cleanupStatus = "completed";
                        _lastResults.cleanupSucceeded = _lastResults.cleanupErrors.Count == 0;
                        MarkRecoveredRunOrphaned();
                    }
                    else
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
                    _lastResults.cleanupSucceeded = _lastResults.cleanupErrors.Count == 0;
                    _lastResults.cleanupStatus = _lastResults.cleanupSucceeded ? "completed" : "failed";
                    PersistSnapshot(clearActivePointer: true);
                    if (string.IsNullOrWhiteSpace(_lastResults.persistenceError))
                    {
                        _isRunning = false;
                        _activeRunGuid = null;
                        _pendingTerminalStatus = null;
                        _forceStopRequested = false;
                    }
                    else
                    {
                        _lastResults.status = "cleanup";
                        _lastResults.cleanupPending = true;
                        _lastResults.cleanupSucceeded = false;
                        ScheduleCleanup();
                    }
                    }
                }
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

        internal bool ReleaseOwnedRunnerResources()
        {
            var api = _activeApi;
            var callback = _activeCallback;
            if (callback != null)
            {
                try
                {
                    var apiType = api != null ? api.GetType() : FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                    var unregister = ResolveRunnerAdapter(apiType).Unregister;
                    var callbacksType = callback.GetType().GetInterfaces()
                        .FirstOrDefault(type => type.FullName == "UnityEditor.TestTools.TestRunner.Api.ICallbacks");
                    if (unregister == null || callbacksType == null)
                        throw new MissingMethodException("UnregisterTestCallback<T>(T) is unavailable.");
                    var invoker = CallbackUnregisterInvokerForTests;
                    if (invoker != null) invoker(unregister, callbacksType, callback);
                    else unregister.MakeGenericMethod(callbacksType).Invoke(null, new[] { callback });
                    _activeCallback = null;
                }
                catch (Exception ex)
                {
                    AddCleanupError("callback-unregister", RootException(ex));
                }
            }

            if (api != null && _activeCallback == null)
            {
                try
                {
                    var destroyer = ApiDestroyerForTests;
                    if (destroyer != null) destroyer(api);
                    else UnityEngine.Object.DestroyImmediate(api);
                    _activeApi = null;
                }
                catch (Exception ex)
                {
                    AddCleanupError("api-release", RootException(ex));
                }
            }
            RefreshUnresolvedResources();
            return _activeCallback == null && _activeApi == null;
        }

        private void AddCleanupError(string stage, Exception error)
        {
            if (_lastResults == null) return;
            string message = stage + ": " + error.GetType().Name + ": " + error.Message;
            if (!_lastResults.cleanupErrors.Contains(message)) _lastResults.cleanupErrors.Add(message);
        }

        private UPilotTestRunnerAdapter ResolveRunnerAdapter(Type apiType)
        {
            return (RunnerAdapterResolverForTests ?? UPilotTestRunnerAdapter.Get)(apiType);
        }

        private static Exception RootException(Exception error)
        {
            while (error is TargetInvocationException invocation && invocation.InnerException != null)
                error = invocation.InnerException;
            return error;
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
                string runnerState = ProbeFrameworkRun(out object runner);
                if (runnerState == "unknown") return;

                // CancelTestRun can unregister the runner without invoking ICallbacks.RunFinished.
                // Once the framework no longer owns the job, it is safe to finish UPilot cleanup.
                if (runnerState == "inactive")
                {
                    if (_forceStopRequested)
                    {
                        _lastResults.forceStopAttempted = true;
                        _lastResults.forceStopSucceeded = true;
                    }
                    FinalizeCancelledRun();
                    return;
                }

                if (!_forceStopRequested || NowMs() < _forceStopDeadline || _lastResults.forceStopAttempted)
                    return;

                _lastResults.forceStopAttempted = true;
                PersistSnapshot();
                if (!string.IsNullOrWhiteSpace(_lastResults.persistenceError)) return;
                MethodInfo stopRun = runner?.GetType().GetMethod("StopRun", BindingFlags.NonPublic | BindingFlags.Instance);
                if (stopRun == null)
                    throw new Exception("Test Framework active runner cleanup entry was not found.");

                stopRun.Invoke(runner, null);
                PersistSnapshot();
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
            _lastResults.outcomeStatus = "aborted";
            _lastResults.resultAuthoritative = true;
            PersistSnapshot();
            ScheduleCleanup();
        }

        private void RefreshUnresolvedResources()
        {
            if (_lastResults == null)
                return;
            _lastResults.unresolvedResources.Clear();
            if (_isRunning && _lastResults.runnerState != "inactive")
                _lastResults.unresolvedResources.Add("test-runner-job");
            if (!string.IsNullOrWhiteSpace(_activeRunGuid))
                _lastResults.unresolvedResources.Add($"run-guid:{_activeRunGuid}");
            if (_activeApi != null)
                _lastResults.unresolvedResources.Add("test-runner-api");
            if (_activeCallback != null)
                _lastResults.unresolvedResources.Add("test-callback");
            if (_editorCleanupPending)
                _lastResults.unresolvedResources.Add("editor-playmode");
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
                _lastResults.callbackDomain = CallbackDomain;
                if (_lastResults.resultAuthoritative && !string.IsNullOrWhiteSpace(_lastResults.outcomeStatus)
                    && _lastResults.outcomeStatus != "pending" && _lastResults.outcomeStatus != "unknown")
                    _pendingTerminalStatus = _lastResults.outcomeStatus;
                _lastResults.terminalReason = string.Empty;
                _recoveryDeadline = NowMs() + 30000;
                _recoveryInactiveObservations = 0;
                _nextRecoveryProbeAt = _recoveryDeadline;
                if (!string.IsNullOrWhiteSpace(_pendingTerminalStatus))
                {
                    _lastResults.status = "cleanup";
                    _lastResults.phase = "cleanup";
                    ScheduleCleanup();
                }
            }
        }

        private void ReattachPersistedRun()
        {
            if (!_isRunning || string.IsNullOrWhiteSpace(_activeRunGuid) || _activeCallback != null
                || !string.IsNullOrWhiteSpace(_pendingTerminalStatus))
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
                var api = _activeApi ?? ScriptableObject.CreateInstance(apiType);
                _activeApi = api;
                var callback = CreateCallbackProxy(callbacksType);
                RegisterCallbacks(apiType, api, callbacksType, callback);
                _activeApi = api;
                _activeCallback = callback;
                _lastResults.phase = "running_recovered";
                _lastResults.lastProgressAt = NowMs();
                PersistSnapshot();
                _recoveryDeadline = NowMs() + 30000;
                _recoveryInactiveObservations = 0;
                _nextRecoveryProbeAt = NowMs();
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
            _lastResults.status = "running";
            _lastResults.phase = "recovery_required";
            if (!_lastResults.resultAuthoritative) _lastResults.outcomeStatus = "unknown";
            _lastResults.terminalReason = "Test Runner callback was not recovered after Domain Reload; assertion outcome is unknown.";
            _lastResults.nextAction = "Inspect this runGuid and Runner diagnostics; do not replay test start.";
            _lastResults.isRunning = true;
            _lastResults.endedAt = 0;
            PersistSnapshot();
        }

        private void RecoveredRunWatchdog()
        {
            if (!_isRunning)
            {
                EditorApplication.update -= RecoveredRunWatchdog;
                return;
            }

            long now = NowMs();
            if (now < _nextRecoveryProbeAt)
                return;
            var runnerState = ProbeFrameworkRun(out _);
            if (runnerState == "active")
            {
                _recoveryInactiveObservations = 0;
                _nextRecoveryProbeAt = now + 1000;
                return;
            }
            if (runnerState == "inactive") _recoveryInactiveObservations++;
            _nextRecoveryProbeAt = now + 1000;
            if (_lastResults.cancelRequested && runnerState == "inactive")
            {
                FinalizeCancelledRun();
                return;
            }
            if (now >= _recoveryDeadline && (_recoveryInactiveObservations >= 3 || runnerState == "unknown"))
                MarkRecoveredRunOrphaned();
        }

        private void MarkFirstProgressObserved()
        {
            EditorApplication.update -= TestProgressWatchdog;
            if (_lastResults == null)
                return;
            _lastResults.firstProgressObserved = true;
            _lastResults.watchdogState = "progress_observed";
            _lastResults.suspectedStuck = false;
            _lastResults.failureSignature = string.Empty;
            _lastResults.nextAction = string.Empty;
        }

        private void TestProgressWatchdog()
        {
            if (!_isRunning || _lastResults == null || _lastResults.firstProgressObserved)
            {
                EditorApplication.update -= TestProgressWatchdog;
                return;
            }
            long now = NowMs();
            if (!ApplyFirstProgressTimeout(_lastResults, now))
                return;

            EditorApplication.update -= TestProgressWatchdog;
            PersistSnapshot();
            try
            {
                RequestCancel(force: true);
            }
            catch (Exception ex)
            {
                _lastResults.cleanupErrors.Add($"first-progress-watchdog: {ex.GetType().Name}: {ex.Message}");
                _pendingTerminalStatus = "aborted";
                FinalizeCancelledRun();
            }
        }

        internal static bool ApplyFirstProgressTimeout(TestRunResultPayload payload, long now)
        {
            if (payload == null || payload.firstProgressObserved || now < payload.firstProgressDeadlineAt)
                return false;
            payload.suspectedStuck = true;
            payload.watchdogState = "first_progress_timeout";
            payload.failureSignature = "TestRunner.FirstProgressTimeout";
            payload.nextAction = "Inspect unity_hang_status; UPilot requested bounded Test Runner cancellation and cleanup.";
            payload.lastProgressAt = now;
            return true;
        }

        private string ProbeFrameworkRun(out object runner)
        {
            runner = null;
            try
            {
                Type apiType = _activeApi != null ? _activeApi.GetType() : FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                _lastResults.runnerState = ResolveRunnerAdapter(apiType).Probe(_activeRunGuid, out runner, out var diagnostic);
                _lastResults.recoveryDiagnostic = diagnostic;
            }
            catch (Exception ex)
            {
                _lastResults.runnerState = "unknown";
                _lastResults.recoveryDiagnostic = ex.GetType().Name + ": " + ex.Message;
            }
            return _lastResults.runnerState;
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
                UPilotTestRunStore.Save(PersistenceDirectory, _lastResults,
                    _isRunning || IsNonTerminal(_lastResults.status), clearActivePointer);
            }
            catch (Exception ex)
            {
                _lastResults.persistenceError = $"{ex.GetType().Name}: {ex.Message}";
                if (!_lastResults.cleanupErrors.Any(item => item.StartsWith("persistence:", StringComparison.Ordinal)))
                    _lastResults.cleanupErrors.Add($"persistence: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static TestRunResultPayload LoadPersistedSnapshot(string runGuid)
        {
            try
            {
                string path = GetRunPath(runGuid);
                return UPilotTestRunStore.Read(path);
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
            private string _runGuid;
            private string _domain;

            public void Initialize(UPilotTestService service, string runGuid, string domain)
            {
                _service = service;
                _runGuid = runGuid;
                _domain = domain;
            }

            internal void BindRun(string runGuid) { _runGuid = runGuid; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (_service != Instance || _domain != CallbackDomain || string.IsNullOrEmpty(_runGuid)
                    || _runGuid != _service._activeRunGuid) return null;
                if ((targetMethod?.Name == "TestStarted" || targetMethod?.Name == "TestFinished")
                    && args != null && args.Length > 0)
                {
                    var test = targetMethod.Name == "TestFinished" ? GetProperty(args[0], "Test") : args[0];
                    if (!Convert.ToBoolean(GetProperty(test, "IsSuite") ?? false))
                    {
                        var selected = _service._lastResults?.selectedLeafIdentities;
                        var unique = Convert.ToString(GetProperty(test, "UniqueName"));
                        if (selected != null && selected.Length > 0 && !selected.Contains(unique)) return null;
                    }
                }
                switch (targetMethod?.Name)
                {
                    case "RunStarted":
                        _service.OnRunStarted();
                        break;
                    case "RunFinished":
                        if (!MatchesSelection(args != null && args.Length > 0 ? args[0] : null,
                                _service._lastResults?.selectedLeafIdentities))
                        {
                            _service._lastResults.recoveryDiagnostic = "Ignored RunFinished with a different test selection.";
                            return null;
                        }
                        _service.OnRunFinished(args != null && args.Length > 0 ? args[0] : null);
                        break;
                    case "TestStarted":
                        _service.OnTestStarted(args != null && args.Length > 0 ? args[0] : null);
                        break;
                    case "TestFinished":
                        _service.OnTestFinished(args != null && args.Length > 0 ? args[0] : null);
                        break;
                }
                return null;
            }

            private static bool MatchesSelection(object result, string[] selected)
            {
                if (result == null) return false;
                if (selected == null || selected.Length == 0) return true;
                var children = GetProperty(result, "Children") as IEnumerable;
                if (children != null)
                    foreach (var child in children)
                        if (!MatchesSelection(child, selected)) return false;
                var test = GetProperty(result, "Test");
                return Convert.ToBoolean(GetProperty(test, "IsSuite") ?? false)
                    || selected.Contains(Convert.ToString(GetProperty(test, "UniqueName")));
            }
        }

    }
}
