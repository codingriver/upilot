using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotCoreTests
    {
        [Test]
        public void BridgeAdmissionDistinguishesReconnectFromHardStop()
        {
            // Isolated instance: never close admission or reset the running test project's Bridge.
            var type = typeof(UPilotBridge);
            var bridge = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var resetting = type.GetField("_resettingWork", flags);
            var closed = type.GetField("_admissionClosed", flags);
            var guard = type.GetMethod("GetAdmissionErrorCode", flags);
            Assert.That(guard, Is.Not.Null);
            foreach (var isResetting in new[] { false, true })
            foreach (var isClosed in new[] { false, true })
            {
                resetting.SetValue(bridge, isResetting);
                closed.SetValue(bridge, isClosed);
                var expected = isResetting ? "UPILOT_RESET_IN_PROGRESS"
                    : isClosed ? "UPILOT_RECONNECT_PENDING" : null;
                foreach (var command in new[] { "resource.editorState", "compile.errors.get", "compile.request" })
                    Assert.That(guard.Invoke(bridge, new object[] { command }), Is.EqualTo(expected), command);
                foreach (var control in new[] { "service.restart", "queue.snapshot", "editor.state" })
                    Assert.That(guard.Invoke(bridge, new object[] { control }), Is.Null, control);
            }
        }

        [Test]
        public void CommandDescriptorCarriesExecutionMetadata()
        {
            var descriptor = new CommandDescriptor(
                "test.command",
                "test",
                idempotent: false,
                destructive: true,
                playModePolicy: "blocked",
                feature: "core",
                timeoutMs: 1234,
                capabilityRequirements: new[] { "editor" });

            Assert.That(descriptor.TimeoutMs, Is.EqualTo(1234));
            Assert.That(descriptor.Destructive, Is.True);
            Assert.That(descriptor.CapabilityRequirements, Is.EqualTo(new[] { "editor" }));
        }

        [Test]
        public void OperationTrackerExposesLayeredTiming()
        {
            var id = Guid.NewGuid().ToString("N");
            var context = UPilotOperationTracker.Instance.BeginOperation(id, "test.timing");
            context.Step("主线程执行中");
            context.Step("主线程执行完毕");
            context.Complete();

            var timing = UPilotOperationTracker.Instance.GetTimingSnapshot(id);
            Assert.That(timing.bridgeMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(timing.queueMs, Is.GreaterThanOrEqualTo(0));
            Assert.That(timing.unityExecutionMs, Is.GreaterThanOrEqualTo(0));
            UPilotOperationTracker.Instance.EndOperation(id);
        }

        [Test]
        public void OperationTerminalIgnoresLateProgressAndSecondCompletion()
        {
            var entry = new OperationLogEntry { ReceivedAt = DateTime.Now };
            var context = new OperationContext(entry, new UPilotOperationTracker());
            context.Complete("original");
            var finished = entry.CompletedAt;
            var step = entry.CurrentStep;
            var count = entry.Steps.Count;
            context.Progress(90, "late");
            context.Step("late");
            context.Warn("late");
            context.Fail("LATE", "late");
            context.Complete("late");
            Assert.That(entry.CompletedAt, Is.EqualTo(finished));
            Assert.That(entry.Phase, Is.EqualTo("completed"));
            Assert.That(entry.CurrentStep, Is.EqualTo(step));
            Assert.That(entry.Steps.Count, Is.EqualTo(count));
            Assert.That(entry.Progress, Is.EqualTo(-1));
        }

        [Test]
        public void FlowHardResetFencesLateLeaseAndAllowsFreshRun()
        {
            var registry = Type.GetType("CodingRiver.UPilot.Flow.UPilotFlowExecutionRegistry, UPilot.Flow");
            if (registry == null) Assert.Ignore("Flow is not installed.");
            var list = (IEnumerable)registry.GetMethod("List").Invoke(null, null);
            foreach (var item in list)
                if ((long)item.GetType().GetField("endedAt").GetValue(item) == 0)
                    Assert.Ignore("An unrelated Flow execution is active.");
            var register = registry.GetMethod("Register");
            var get = registry.GetMethod("Get");
            var reset = registry.GetMethod("ResetActive");
            var oldId = Guid.NewGuid().ToString("N");
            var nextId = Guid.NewGuid().ToString("N");
            var lease = (IDisposable)register.Invoke(null, new object[] { oldId, "reset-test", null });
            reset.Invoke(null, null);
            Assert.That(get.Invoke(null, new object[] { oldId }), Is.Null);
            var rejected = Assert.Throws<TargetInvocationException>(() =>
                register.Invoke(null, new object[] { oldId, "late", null }));
            Assert.That(rejected.InnerException, Is.TypeOf<OperationCanceledException>());
            using (var fresh = (IDisposable)register.Invoke(null, new object[] { nextId, "reset-test", null }))
            {
                lease.Dispose();
                registry.GetMethod("MarkTerminal").Invoke(null, new object[] { oldId, "completed" });
                Assert.That(get.Invoke(null, new object[] { oldId }), Is.Null);
                Assert.That(get.Invoke(null, new object[] { nextId }), Is.Not.Null);
            }
        }

        [Test]
        public void FlowLateContextCannotReplaceOrClearFreshExecutionContext()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("CodingRiver.UPilot.UPilotFlowService")).FirstOrDefault(t => t != null);
            if (type == null) Assert.Ignore("Flow adapter is not installed.");
            // Isolated service shell: do not register UI callbacks or reset the live registry.
            var service = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            type.GetField("_stateLock", flags).SetValue(service, new object());
            var executionsField = type.GetField("_executions", flags);
            var executions = Activator.CreateInstance(executionsField.FieldType);
            executionsField.SetValue(service, executions);
            var payloadType = executionsField.FieldType.GetGenericArguments()[1];
            executions.GetType().GetMethod("TryAdd").Invoke(executions,
                new object[] { "fresh", Activator.CreateInstance(payloadType) });
            type.GetField("_activeExecutionId", flags).SetValue(service, "fresh");
            var contextField = type.GetField("_activeContext", flags);
            var fresh = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(contextField.FieldType);
            var retired = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(contextField.FieldType);
            var update = type.GetMethod("TrySetActiveContext", flags);
            Assert.That(update.Invoke(service, new object[] { "fresh", fresh }), Is.True);
            Assert.That(update.Invoke(service, new object[] { "retired", retired }), Is.False);
            Assert.That(update.Invoke(service, new object[] { "retired", null }), Is.False);
            Assert.That(contextField.GetValue(service), Is.SameAs(fresh));
            Assert.That(update.Invoke(service, new object[] { "fresh", null }), Is.True);
            Assert.That(contextField.GetValue(service), Is.Null);
        }

        [Test]
        public void LoggerInfoConsolePolicyKeepsRoutineTransportQuietByDefault()
        {
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("COMMAND", false, false), Is.False);
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("COMPILE", false, false), Is.False);
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("NETWORK", false, false), Is.False);
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("UPilot.Flow", false, false), Is.False);
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("SYSTEM", false, false), Is.True);
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("EntityIds", false, false), Is.True);
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("COMMAND", true, false), Is.True);
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("UPilot.Flow", true, false), Is.True);
            Assert.That(Logger.ShouldMirrorInfoToUnityConsole("NETWORK", true, true), Is.False);
        }

        [Test]
        public void EntityIdDebugLogsAreOptInAndPreserveObjectResolution()
        {
            var key = UPilotPreferences.EntityIdLogsKey;
            var hadPreference = EditorPrefs.HasKey(key);
            var previousPreference = EditorPrefs.GetBool(key, false);
            var previousEnabled = UPilotEntityIds.DebugLogsEnabled;
            var previousConsole = Logger.LogToUnityConsole;
            var previousLevel = Logger.MinLevel;
            var messages = new List<string>();
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (message.StartsWith("[INFO ] [EntityIds]", StringComparison.Ordinal))
                    messages.Add(message);
            };
            var go = new GameObject("UPilotEntityIdLogProbe");
            Application.logMessageReceived += capture;
            try
            {
                Logger.LogToUnityConsole = true;
                Logger.MinLevel = Logger.LogLevel.Info;
                UPilotEntityIds.DebugLogsEnabled = false;
                Assert.That(EditorPrefs.GetBool(key, true), Is.False);
                Assert.That(UPilotEntityIds.ToWireId(null), Is.Zero);
                var id = UPilotEntityIds.ToWireId(go);
                Assert.That(id, Is.Not.Zero);
                Assert.That(UPilotEntityIds.ObjectFromWireId(id), Is.SameAs(go));
                Assert.That(UPilotEntityIds.GameObjectFromWireId(id), Is.SameAs(go));
                Assert.That(messages, Is.Empty);

                UPilotEntityIds.DebugLogsEnabled = true;
                Assert.That(EditorPrefs.GetBool(key, false), Is.True);
                Assert.That(UPilotEntityIds.ToWireId(go), Is.EqualTo(id));
                Assert.That(UPilotEntityIds.GameObjectFromWireId(id), Is.SameAs(go));
                Assert.That(messages.Count, Is.EqualTo(2));
                Assert.That(messages[0], Does.Contain("ToWireId: UPilotEntityIdLogProbe -> " + id));
                Assert.That(messages[1], Does.Contain("GameObjectFromWireId: " + id + " -> UPilotEntityIdLogProbe"));
                Assert.That(UPilotPreferences.CurrentProjectKeys, Does.Contain(key));

                UPilotEntityIds.DebugLogsEnabled = false;
                UPilotEntityIds.ToWireId(go);
                UPilotEntityIds.GameObjectFromWireId(id);
                Assert.That(messages.Count, Is.EqualTo(2));
            }
            finally
            {
                Application.logMessageReceived -= capture;
                UPilotEntityIds.DebugLogsEnabled = previousEnabled;
                if (hadPreference) EditorPrefs.SetBool(key, previousPreference);
                else EditorPrefs.DeleteKey(key);
                Logger.LogToUnityConsole = previousConsole;
                Logger.MinLevel = previousLevel;
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void FlowLoggerCompatibilityFacadeUsesCoreLoggerSettingsAndPath()
        {
            var flowLogger = Type.GetType("Codingriver.Logger, UPilot.Flow");
            if (flowLogger == null)
                Assert.Ignore("UPilot Flow is not enabled in this project.");

            Assert.That(flowLogger.GetProperty("LogFilePath")?.GetValue(null), Is.EqualTo(Logger.LogFilePath));
            Assert.That(flowLogger.GetProperty("LogToUnityConsole"), Is.Not.Null);

            var previous = Logger.LogToUnityConsole;
            try
            {
                flowLogger.GetProperty("LogToUnityConsole")?.SetValue(null, !previous);
                Assert.That(Logger.LogToUnityConsole, Is.EqualTo(!previous));
            }
            finally
            {
                Logger.SetLogToUnityConsole(previous);
            }
        }

        [Test]
        public void LoggerRotationAccountsForIncomingEntryAndRetainsTwoBackups()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-logger-" + Guid.NewGuid().ToString("N"));
            var logPath = Path.Combine(directory, "upilot.log");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllBytes(logPath, new byte[8]);
                File.WriteAllText(logPath + ".1", "previous");
                File.WriteAllText(logPath + ".2", "oldest");

                Logger.RotateLogIfNeeded(logPath, incomingByteCount: 3, maxLogSizeBytes: 10, maxBackupFiles: 2);

                Assert.That(File.Exists(logPath), Is.False);
                Assert.That(new FileInfo(logPath + ".1").Length, Is.EqualTo(8));
                Assert.That(File.ReadAllText(logPath + ".2"), Is.EqualTo("previous"));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void LoggerNewSessionRotatesCurrentLogInsteadOfOverwritingIt()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-logger-session-" + Guid.NewGuid().ToString("N"));
            var logPath = Path.Combine(directory, "upilot.log");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(logPath, "previous session");

                Logger.RotateLogForNewSession(logPath, maxBackupFiles: 2);

                Assert.That(File.Exists(logPath), Is.False);
                Assert.That(File.ReadAllText(logPath + ".1"), Is.EqualTo("previous session"));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void LoggerTruncatesOversizedEntriesWithOriginalLength()
        {
            var value = new string('x', 20);
            Assert.That(Logger.TruncateLogEntry(value, 10), Is.EqualTo("xxxxxxxxxx ... [truncated, total=20]"));
        }

        [Test]
        public void ReflectionBinderConvertsPrimitiveParameter()
        {
            var method = typeof(UPilotReflectionService).GetMethod("TryConvertParameter", BindingFlags.NonPublic | BindingFlags.Static);
            var args = new object[] { "42", typeof(int), null, null, 0 };
            var ok = (bool)method.Invoke(null, args);

            Assert.That(ok, Is.True);
            Assert.That(args[2], Is.EqualTo(42));
        }

        [Test]
        public void ReflectionTypeQueryResolvesExactTypeWithoutMemberEnumeration()
        {
            var exact = Execution.ReflectionCache.FindTypes(typeof(GameObject).FullName);
            var shortName = Execution.ReflectionCache.FindTypes(nameof(GameObject));

            Assert.That(exact, Has.Count.EqualTo(1));
            Assert.That(exact[0], Is.EqualTo(typeof(GameObject)));
            Assert.That(shortName, Does.Contain(typeof(GameObject)));
        }

        [Test]
        public void GameObjectInfoIncludesHierarchyAndComponentTypes()
        {
            var root = new GameObject("UPilotFindRoot");
            var child = new GameObject("UPilotFindChild");
            try
            {
                child.transform.SetParent(root.transform, false);
                child.AddComponent<BoxCollider>();
                var buildInfo = typeof(UPilotGameObjectService).GetMethod(
                    "BuildInfo",
                    BindingFlags.Public | BindingFlags.Static);

                var info = (GameObjectInfoPayload)buildInfo.Invoke(null, new object[] { child });

                Assert.That(info.hierarchyPath, Is.EqualTo("UPilotFindRoot/UPilotFindChild"));
                Assert.That(info.componentTypes, Does.Contain(typeof(Transform).FullName));
                Assert.That(info.componentTypes, Does.Contain(typeof(BoxCollider).FullName));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SelectionClearReceiptPreservesBusinessOutcomeAndIdentity()
        {
            var payload = new SelectionClearResultPayload
            {
                ok = true,
                businessEffectVerified = true,
                changed = true,
                status = "cleared",
                beforeSelectionCount = 2,
                afterSelectionCount = 0,
                beforeActiveObject = new SelectionObjectIdentityPayload
                {
                    instanceId = 42,
                    name = "Selected",
                    typeName = typeof(GameObject).FullName,
                },
            };

            var restored = JsonUtility.FromJson<SelectionClearResultPayload>(JsonUtility.ToJson(payload));

            Assert.That(restored.ok, Is.True);
            Assert.That(restored.businessEffectVerified, Is.True);
            Assert.That(restored.changed, Is.True);
            Assert.That(restored.status, Is.EqualTo("cleared"));
            Assert.That(restored.beforeSelectionCount, Is.EqualTo(2));
            Assert.That(restored.afterSelectionCount, Is.Zero);
            Assert.That(restored.beforeActiveObject.instanceId, Is.EqualTo(42));
            Assert.That(restored.afterActiveObject.instanceId, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DeleteSceneFinalizationMarksOnlyPersistentEditModeScenes(bool isPlaying)
        {
            // Preview scenes isolate this branch test from the Test Runner's untitled scene.
            // No saved scene or asset is needed to verify the valid-Scene/PlayMode guard.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                Assert.That(scene.IsValid(), Is.True);
                Assert.That(scene.isDirty, Is.False);
                var saveRequired = UPilotGameObjectService.MarkSceneDirtyAfterDelete(scene, isPlaying);
                Assert.That(saveRequired, Is.EqualTo(!isPlaying));
                Assert.That(scene.isDirty, Is.EqualTo(!isPlaying));
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene); }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DeleteSceneFinalizationDoesNotMarkInvalidScene(bool isPlaying)
        {
            Assert.That(UPilotGameObjectService.MarkSceneDirtyAfterDelete(default(UnityEngine.SceneManagement.Scene), isPlaying), Is.False);
        }

        [Test]
        public void GameObjectDeleteReceiptPreservesTargetPersistenceAndSideEffectEvidence()
        {
            var payload = new GameObjectDeleteResultPayload
            {
                ok = true,
                operation = "delete",
                target = new GameObjectDeleteTargetPayload
                {
                    instanceId = 77,
                    name = "Deleted",
                    hierarchyPath = "Root/Deleted",
                    sceneName = "Scene",
                    scenePath = "Assets/Scene.unity",
                },
                beforeExists = true,
                afterExists = false,
                businessEffectVerified = true,
                sceneDirty = true,
                saveRequired = true,
                sideEffectsMayHaveOccurred = true,
            };

            var restored = JsonUtility.FromJson<GameObjectDeleteResultPayload>(JsonUtility.ToJson(payload));

            Assert.That(restored.ok, Is.True);
            Assert.That(restored.operation, Is.EqualTo("delete"));
            Assert.That(restored.target.instanceId, Is.EqualTo(77));
            Assert.That(restored.target.hierarchyPath, Is.EqualTo("Root/Deleted"));
            Assert.That(restored.beforeExists, Is.True);
            Assert.That(restored.afterExists, Is.False);
            Assert.That(restored.businessEffectVerified, Is.True);
            Assert.That(restored.sceneDirty, Is.True);
            Assert.That(restored.saveRequired, Is.True);
            Assert.That(restored.sideEffectsMayHaveOccurred, Is.True);
        }

        [Test]
        public void ObjectDumpSummarizesUnityValueTypesUnlessExpansionIsExplicit()
        {
            var ignoredTypes = new HashSet<string>(ObjectDumper.DefaultSkipTypes, StringComparer.Ordinal);
            var summarizedCount = 0;
            var summarized = ObjectDumper.Dump(
                new Vector3(1f, 2f, 3f), 6, 100, 5000, false, ignoredTypes,
                ref summarizedCount);
            var expandedCount = 0;
            var expanded = ObjectDumper.Dump(
                new Vector3(1f, 2f, 3f), 6, 100, 5000, false, ignoredTypes,
                ref expandedCount, expandUnityValueTypes: true);

            Assert.That(summarized.children, Is.Empty);
            Assert.That(summarized.value, Does.Contain("1").And.Contain("2").And.Contain("3"));
            Assert.That(summarizedCount, Is.EqualTo(1));
            Assert.That(expanded.children, Is.Not.Empty);
            Assert.That(expandedCount, Is.GreaterThan(summarizedCount));
            var expandedNames = expanded.children.Select(child => child.name).ToArray();
            Assert.That(expandedNames, Is.EquivalentTo(new[] { "x", "y", "z" }));
            Assert.That(expandedNames, Does.Not.Contain(".normalized"));
            Assert.That(expandedNames, Does.Not.Contain(".magnitude"));
            Assert.That(expandedNames, Does.Not.Contain(".sqrMagnitude"));
        }

        [Test]
        public void ScreenshotPathRejectsOutsideProjectByDefault()
        {
            var method = typeof(UPilotSnapshotService).GetMethod(
                "ResolveOutputDirectory", BindingFlags.NonPublic | BindingFlags.Static);
            var outside = Path.Combine(Path.GetPathRoot(Application.dataPath), "upilot-outside.png");
            var error = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { outside, "outside-project" }));

            Assert.That(error.InnerException?.Message, Does.Contain("current Unity project"));
        }

        [Test]
        public void BridgeResultSerializesAuthoritativeEditorContext()
        {
            var message = new ResultMessage<GenericOkPayload>
            {
                id = "cmd-1",
                name = "test.result",
                payload = new GenericOkPayload { ok = true },
                context = new EditorContextPayload
                {
                    connected = true,
                    authoritative = true,
                    isStale = false,
                    ready = false,
                    blocked = true,
                    blockedReason = "CompilationInProgress",
                    nextAction = "Continue with unity_compile_wait.",
                    source = "bridge-response",
                    sessionId = "session-1",
                    playModeState = "edit",
                    isCompiling = true,
                    compileStatus = "compiling",
                    compilePhase = "domain_reload",
                    compileRequestId = "compile-1",
                    updatedAt = 123,
                    lastMainThreadPumpAt = 123,
                    mainThreadQueueDepth = 2,
                    processId = 42,
                },
            };

            var json = JsonUtility.ToJson(message);

            Assert.That(json, Does.Contain("\"authoritative\":true"));
            Assert.That(json, Does.Contain("\"ready\":false"));
            Assert.That(json, Does.Contain("\"blocked\":true"));
            Assert.That(json, Does.Contain("\"blockedReason\":\"CompilationInProgress\""));
            Assert.That(json, Does.Contain("\"nextAction\":\"Continue with unity_compile_wait.\""));
            Assert.That(json, Does.Contain("\"compilePhase\":\"domain_reload\""));
            Assert.That(json, Does.Contain("\"compileRequestId\":\"compile-1\""));
            Assert.That(json, Does.Contain("\"source\":\"bridge-response\""));
            Assert.That(json, Does.Contain("\"mainThreadQueueDepth\":2"));
            Assert.That(json, Does.Contain("\"processId\":42"));
        }

        [Test]
        public void GenericOkPayloadPreservesPlayModeIdempotencyEvidence()
        {
            var payload = new GenericOkPayload
            {
                ok = true,
                state = "pause",
                changed = false,
                writeCount = 0,
                commandId = "cmd-pause-1",
                stateObserved = true,
                requestedState = "pause",
                observedState = "pause",
                commandSubmitted = true,
                confirmed = true,
                authoritative = true,
                isStale = false,
            };

            var restored = JsonUtility.FromJson<GenericOkPayload>(JsonUtility.ToJson(payload));

            Assert.That(restored.changed, Is.False);
            Assert.That(restored.writeCount, Is.EqualTo(0));
            Assert.That(restored.commandId, Is.EqualTo("cmd-pause-1"));
            Assert.That(restored.stateObserved, Is.True);
            Assert.That(restored.requestedState, Is.EqualTo("pause"));
            Assert.That(restored.observedState, Is.EqualTo("pause"));
            Assert.That(restored.commandSubmitted, Is.True);
            Assert.That(restored.confirmed, Is.True);
            Assert.That(restored.authoritative, Is.True);
            Assert.That(restored.isStale, Is.False);
        }

        [Test]
        public void SceneViewMaximizePayloadPreservesIdentityEvidenceAndZeroWriteRejection()
        {
            var payload = new EditorWindowCloseResultPayload
            {
                commandId = "cmd-sceneview-1",
                instanceId = "42",
                domainGeneration = "domain-3",
                ok = false,
                deniedReason = "SCENEVIEW_STATE_CHANGED",
                changed = false,
                writeCount = 0,
                commandSubmitted = false,
                stateObserved = true,
                sideEffectsMayHaveOccurred = false,
                originalRect = new WindowRectPayload { x = 1, y = 2, width = 300, height = 200 },
                observedRect = new WindowRectPayload { x = 3, y = 4, width = 500, height = 400 },
            };

            var restored = JsonUtility.FromJson<EditorWindowCloseResultPayload>(JsonUtility.ToJson(payload));

            Assert.That(restored.instanceId, Is.EqualTo("42"));
            Assert.That(restored.domainGeneration, Is.EqualTo("domain-3"));
            Assert.That(restored.deniedReason, Is.EqualTo("SCENEVIEW_STATE_CHANGED"));
            Assert.That(restored.changed, Is.False);
            Assert.That(restored.writeCount, Is.EqualTo(0));
            Assert.That(restored.commandSubmitted, Is.False);
            Assert.That(restored.stateObserved, Is.True);
            Assert.That(restored.sideEffectsMayHaveOccurred, Is.False);
            Assert.That(restored.originalRect.width, Is.EqualTo(300));
            Assert.That(restored.observedRect.height, Is.EqualTo(400));
        }

        [Test]
        public void SceneViewMaximizeAcceptsOnlyCanonicalNonNegativeIntegerIdentity()
        {
            Assert.That(UPilotWindowService.TryNormalizeSceneViewInstanceId("00042", out var canonical), Is.True);
            Assert.That(canonical, Is.EqualTo("42"));
            Assert.That(UPilotWindowService.TryNormalizeSceneViewInstanceId("-1", out _), Is.False);
            Assert.That(UPilotWindowService.TryNormalizeSceneViewInstanceId("42.0", out _), Is.False);
            Assert.That(UPilotWindowService.TryNormalizeSceneViewInstanceId("Scene", out _), Is.False);
        }

        [Test]
        public void SceneViewCommandObservationRetainsTimeoutAndPostSetterStatesWithoutReplay()
        {
            const string commandId = "sceneview-observation-contract";
            UPilotWindowService.ResetSceneViewCommandObservationsForTests();
            try
            {
                // This is the write-ahead state retained before the setter. A
                // timed-out caller can safely query it rather than submit a
                // second mutation.
                UPilotWindowService.RecordSceneViewCommandObservationForTests(new EditorWindowCloseResultPayload
                {
                    commandId = commandId,
                    observationStatus = "submitted",
                    terminal = false,
                    commandSubmitted = true,
                    stateObserved = false,
                    changed = false,
                    writeCount = 0,
                    sideEffectsMayHaveOccurred = true,
                });
                Assert.That(UPilotWindowService.TryGetSceneViewCommandObservationForTests(commandId, out var submitted), Is.True);
                Assert.That(submitted.observationStatus, Is.EqualTo("submitted"));
                Assert.That(submitted.terminal, Is.False);
                Assert.That(submitted.writeCount, Is.EqualTo(0));

                // Simulate a completed setter whose original response was
                // lost. Reading the saved result must not re-run that setter.
                UPilotWindowService.RecordSceneViewCommandObservationForTests(new EditorWindowCloseResultPayload
                {
                    commandId = commandId,
                    observationStatus = "observed",
                    terminal = false,
                    commandSubmitted = true,
                    stateObserved = true,
                    changed = true,
                    writeCount = 1,
                    maximized = true,
                    sideEffectsMayHaveOccurred = true,
                });
                Assert.That(UPilotWindowService.TryGetSceneViewCommandObservationForTests(commandId, out var observed), Is.True);
                observed.writeCount = 99;
                Assert.That(UPilotWindowService.TryGetSceneViewCommandObservationForTests(commandId, out var reread), Is.True);
                Assert.That(reread.observationStatus, Is.EqualTo("observed"));
                Assert.That(reread.writeCount, Is.EqualTo(1));

                // Domain reload clears this in-process store. The command is
                // deliberately not recreated or inferred from a reused ID.
                UPilotWindowService.ResetSceneViewCommandObservationsForTests();
                Assert.That(UPilotWindowService.TryGetSceneViewCommandObservationForTests(commandId, out _), Is.False);
                var unknown = UPilotWindowService.CreateUnknownSceneViewCommandObservation(commandId);
                Assert.That(unknown.observationStatus, Is.EqualTo("unknown"));
                Assert.That(unknown.recoveryRequired, Is.True);
                Assert.That(unknown.terminal, Is.False);
                Assert.That(unknown.commandSubmitted, Is.True);
                Assert.That(unknown.sideEffectsMayHaveOccurred, Is.True);
                Assert.That(unknown.writeCount, Is.Zero);
            }
            finally
            {
                UPilotWindowService.ResetSceneViewCommandObservationsForTests();
            }
        }

        [Test]
        public void SceneViewPreSetterWriteGatePayloadStatesZeroSideEffects()
        {
            // P2-WP-13-T02/T06 contract: a pre-setter readiness rejection is
            // not an unknown mutation and must never make a later retry infer
            // that a native SceneView write may have occurred.
            var rejected = new EditorWindowCloseResultPayload
            {
                commandId = "sceneview-preflight-blocked",
                instanceId = "42",
                state = "denied",
                deniedReason = "CompilationInProgress",
                commandSubmitted = false,
                stateObserved = false,
                changed = false,
                writeCount = 0,
                sideEffectsMayHaveOccurred = false,
            };

            var restored = JsonUtility.FromJson<EditorWindowCloseResultPayload>(JsonUtility.ToJson(rejected));
            Assert.That(restored.state, Is.EqualTo("denied"));
            Assert.That(restored.deniedReason, Is.EqualTo("CompilationInProgress"));
            Assert.That(restored.commandSubmitted, Is.False);
            Assert.That(restored.stateObserved, Is.False);
            Assert.That(restored.changed, Is.False);
            Assert.That(restored.writeCount, Is.Zero);
            Assert.That(restored.sideEffectsMayHaveOccurred, Is.False);
        }

        [Test]
        public void SceneViewRuntimeFailureOnlyClaimsUnknownSideEffectsAfterSetterBoundary()
        {
            var beforeSetter = UPilotWindowService.CreateSceneViewRuntimeFailureObservation(
                "sceneview-before-setter", "42", "domain-7", setterMayHaveRun: false);
            var afterSetter = UPilotWindowService.CreateSceneViewRuntimeFailureObservation(
                "sceneview-after-setter", "42", "domain-7", setterMayHaveRun: true);

            Assert.That(beforeSetter.observationStatus, Is.EqualTo("not_submitted"));
            Assert.That(beforeSetter.terminal, Is.True);
            Assert.That(beforeSetter.commandSubmitted, Is.False);
            Assert.That(beforeSetter.sideEffectsMayHaveOccurred, Is.False);
            Assert.That(beforeSetter.recoveryRequired, Is.False);

            Assert.That(afterSetter.observationStatus, Is.EqualTo("submitted"));
            Assert.That(afterSetter.terminal, Is.False);
            Assert.That(afterSetter.commandSubmitted, Is.True);
            Assert.That(afterSetter.sideEffectsMayHaveOccurred, Is.True);
            Assert.That(afterSetter.recoveryRequired, Is.True);
        }

        [Test]
        public void EditorWindowSetRectPayloadPreservesFullTypeNameAndTypeMismatchZeroWriteEvidence()
        {
            var request = new EditorWindowSetRectPayload
            {
                instanceId = "42",
                domainGeneration = "domain-3",
                fullTypeName = "UnityEditor.SceneView",
                windowTitle = "Scene",
                matchMode = "exact",
            };
            var denied = new EditorWindowCloseResultPayload
            {
                commandId = "cmd-window-type-mismatch",
                instanceId = "42",
                domainGeneration = "domain-3",
                ok = false,
                state = "denied",
                deniedReason = "WINDOW_TYPE_MISMATCH",
                matchedFullTypeName = "UnityEditor.InspectorWindow",
                changed = false,
                writeCount = 0,
                commandSubmitted = false,
                stateObserved = true,
                sideEffectsMayHaveOccurred = false,
            };

            var restoredRequest = JsonUtility.FromJson<EditorWindowSetRectPayload>(JsonUtility.ToJson(request));
            var restoredDenied = JsonUtility.FromJson<EditorWindowCloseResultPayload>(JsonUtility.ToJson(denied));

            Assert.That(restoredRequest.fullTypeName, Is.EqualTo("UnityEditor.SceneView"));
            Assert.That(restoredDenied.deniedReason, Is.EqualTo("WINDOW_TYPE_MISMATCH"));
            Assert.That(restoredDenied.changed, Is.False);
            Assert.That(restoredDenied.writeCount, Is.EqualTo(0));
            Assert.That(restoredDenied.commandSubmitted, Is.False);
            Assert.That(restoredDenied.stateObserved, Is.True);
            Assert.That(restoredDenied.sideEffectsMayHaveOccurred, Is.False);
        }

        [Test]
        public void EditorWindowSetRectExplicitTitleRemainsAnIdentityConstraint()
        {
            // P2-WP-04-T02 direct regression: with an exact instanceId the
            // title remains an explicit consistency check, rather than a
            // fallback selector that could be silently ignored before a write.
            Assert.That(UPilotWindowService.WindowTitleMatches("Probe 42", "Probe 42", "exact"), Is.True);
            Assert.That(UPilotWindowService.WindowTitleMatches("Probe 42", "Probe", "contains"), Is.True);
            Assert.That(UPilotWindowService.WindowTitleMatches("Probe 42", "Other", "exact"), Is.False);
        }

        [Test]
        public void PlayModeCancellationErrorPreservesZeroSetterEvidence()
        {
            var detail = new ErrorDetailPayload
            {
                commandId = "cmd-pause-cancelled",
                commandName = "playmode.set",
                commandSubmitted = false,
                stateObserved = false,
                changed = false,
                writeCount = 0,
                stage = "cancelled",
                sideEffectsMayHaveOccurred = false,
            };

            var restored = JsonUtility.FromJson<ErrorDetailPayload>(JsonUtility.ToJson(detail));

            Assert.That(restored.commandId, Is.EqualTo("cmd-pause-cancelled"));
            Assert.That(restored.commandName, Is.EqualTo("playmode.set"));
            Assert.That(restored.commandSubmitted, Is.False);
            Assert.That(restored.stateObserved, Is.False);
            Assert.That(restored.changed, Is.False);
            Assert.That(restored.writeCount, Is.EqualTo(0));
            Assert.That(restored.sideEffectsMayHaveOccurred, Is.False);
        }

        [Test]
        public void CompileLifecyclePayloadRoundTripPreservesReloadVerificationState()
        {
            var payload = new CompileErrorsPayload
            {
                requestId = "compile-reload",
                compileOperationId = "operation-1",
                writeBatchId = "batch-1",
                writeBatchCreatedAt = 90,
                compileOrigin = "mcp",
                status = "verifying",
                phase = "verifying",
                terminal = false,
                verificationPending = true,
                errorsVerified = false,
                total = 0,
                warningCount = 2,
                warningDetailsAvailable = true,
                warningsTruncated = false,
                startedAt = 100,
                finishedAt = 200,
                lastProgressAt = 250,
                lastCompileRequestedAt = 95,
                lastCompileStartedAt = 100,
                lastCompilerFinishedAt = 200,
                reloadId = "reload-1",
                domainReloadObserved = true,
                errors = new List<CompileErrorItemPayload>(),
                warnings = new List<CompileErrorItemPayload>
                {
                    new CompileErrorItemPayload { assemblyName = "Assembly-CSharp", severity = "warning", message = "example" },
                },
            };

            var restored = JsonUtility.FromJson<CompileErrorsPayload>(
                JsonUtility.ToJson(payload));

            Assert.That(restored.requestId, Is.EqualTo("compile-reload"));
            Assert.That(restored.compileOperationId, Is.EqualTo("operation-1"));
            Assert.That(restored.writeBatchId, Is.EqualTo("batch-1"));
            Assert.That(restored.writeBatchCreatedAt, Is.EqualTo(90));
            Assert.That(restored.compileOrigin, Is.EqualTo("mcp"));
            Assert.That(restored.status, Is.EqualTo("verifying"));
            Assert.That(restored.phase, Is.EqualTo("verifying"));
            Assert.That(restored.warningCount, Is.EqualTo(2));
            Assert.That(restored.warningDetailsAvailable, Is.True);
            Assert.That(restored.warnings, Has.Count.EqualTo(1));
            Assert.That(restored.warnings[0].assemblyName, Is.EqualTo("Assembly-CSharp"));
            Assert.That(restored.startedAt, Is.EqualTo(100));
            Assert.That(restored.finishedAt, Is.EqualTo(200));
            Assert.That(restored.lastProgressAt, Is.EqualTo(250));
            Assert.That(restored.lastCompilerFinishedAt, Is.EqualTo(200));
            Assert.That(restored.verificationPending, Is.True);
            Assert.That(restored.terminal, Is.False);
            Assert.That(restored.errorsVerified, Is.False);
            Assert.That(restored.reloadId, Is.EqualTo("reload-1"));
            Assert.That(restored.domainReloadObserved, Is.True);
            Assert.That(restored.errors, Is.Empty);
        }

        [Test]
        public void HeartbeatSerializesCompleteExecutionStateResultCounts()
        {
            var message = new HeartbeatMessage
            {
                id = "heartbeat-1",
                type = "heartbeat",
                name = "session.heartbeat",
                payload = new EditorExecutionStatePayload
                {
                    stateContractVersion = 2,
                    snapshotId = "epoch:1:2",
                    compilePhase = "failed",
                    terminal = true,
                    errorsVerified = true,
                    errorCount = 2,
                    warningCount = 3,
                    preReloadPublishFailed = true,
                },
            };

            var json = JsonUtility.ToJson(message);

            Assert.That(json, Does.Contain("\"errorCount\":2"));
            Assert.That(json, Does.Contain("\"warningCount\":3"));
            Assert.That(json, Does.Contain("\"preReloadPublishFailed\":true"));
        }

        [Test]
        public void ScreenshotSaveDefaultsRemainBackwardCompatible()
        {
            var payload = new ScreenshotSavePayload();

            Assert.That(payload.degrade, Is.EqualTo("none"));
            Assert.That(payload.fallbackSources, Is.Null);
            Assert.That(new ScreenshotSaveResultPayload().degraded, Is.False);
        }

        [Test]
        public void EditorWindowResolutionIgnoresSameNamedNativeWindow()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Native-window isolation is Windows-specific.");

            var title = "UPilotNativeWindow-" + Guid.NewGuid().ToString("N");
            var handle = CreateWindowEx(
                0,
                "STATIC",
                title,
                unchecked((int)0x80000000),
                0,
                0,
                64,
                64,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            Assert.That(handle, Is.Not.EqualTo(IntPtr.Zero),
                $"CreateWindowEx failed with Win32 error {Marshal.GetLastWin32Error()}");

            try
            {
                var resolved = UPilotWindowService.ResolveWindow(title, "exact");
                Assert.That(resolved.window, Is.Null);
                Assert.That(resolved.info, Is.Null);
            }
            finally
            {
                DestroyWindow(handle);
            }
        }

        [Test]
        public void EditorWindowResolutionByInstanceIdDoesNotFallBackToTitle()
        {
            var resolved = UPilotWindowService.ResolveWindowByInstanceId("not-a-window-id");
            Assert.That(resolved.window, Is.Null);
            Assert.That(resolved.info, Is.Null);
            Assert.That(resolved.multipleMatches, Is.False);
        }

        [Test]
        public void SafeWindowProbeResolvesCurrentIdentityWithoutHistory()
        {
            var window = ScriptableObject.CreateInstance<UPilotSafeWindowProbe>();
            try
            {
                window.titleContent = new GUIContent(UPilotSafeWindowProbe.Title);
                var instanceId = UPilotEntityIds.ToWireId(window).ToString();
                var resolved = UPilotWindowService.ResolveWindowByInstanceId(instanceId);

                Assert.That(resolved.window, Is.SameAs(window));
                Assert.That(resolved.multipleMatches, Is.False);
                Assert.That(resolved.info.instanceId.ToString(), Is.EqualTo(instanceId));
                Assert.That(resolved.info.fullTypeName, Is.EqualTo(UPilotSafeWindowProbe.FullTypeName));
                Assert.That(resolved.info.title, Is.EqualTo(UPilotSafeWindowProbe.Title));
                Assert.That(window.minSize, Is.EqualTo(new Vector2(240, 120)));
                Assert.That(UPilotSafeWindowProbe.IsAllowedTypeName(resolved.info.fullTypeName), Is.True);
                Assert.That(UPilotSafeWindowProbe.IsAllowedTypeName(typeof(SceneView).FullName), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int extendedStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr window);

        [TestCase(false)]
        [TestCase(true)]
        public void ConsoleCaptureActiveObservationIgnoresHistoricalArtifacts(bool hasActive)
        {
            var type = typeof(UPilotConsoleCaptureService);
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            var activeField = type.GetField("s_active", flags);
            Assert.That(activeField.GetValue(null), Is.Null, "Requires no live capture.");
            var key = (string)type.GetMethod("ProjectSessionKey", flags).Invoke(null,
                new object[] { "UPilot.ConsoleCapture.ActiveDirectory" });
            var previousDirectory = SessionState.GetString(key, string.Empty);
            var root = (string)type.GetMethod("GetDefaultCaptureRoot", flags).Invoke(null, null);
            var directory = Path.Combine(root, "test-observe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "session.json");
            const string corruptHistory = "not a capture manifest";
            File.WriteAllText(path, corruptHistory);
            try
            {
                SessionState.EraseString(key);
                var list = type.GetMethod("ListCaptures", flags);
                var manifest = new ConsoleCaptureManifest { sessionId = "live-fixture", active = true };
                if (hasActive)
                {
                    var active = Activator.CreateInstance(activeField.FieldType, true);
                    activeField.FieldType.GetField("Manifest").SetValue(active, manifest);
                    activeField.SetValue(null, active);
                }
                var result = (ConsoleCaptureListResult)list.Invoke(null,
                    new object[] { new ConsoleCaptureListPayload { activeOnly = true }, false });
                Assert.That(result.ok, Is.True);
                Assert.That(result.activeCount, Is.EqualTo(hasActive ? 1 : 0));
                Assert.That(result.returnedCount, Is.EqualTo(result.activeCount));
                if (hasActive)
                {
                    Assert.That(result.sessions[0].sessionId, Is.EqualTo("live-fixture"));
                    Assert.That(result.sessions[0], Is.Not.SameAs(manifest));
                }
                Assert.That(File.ReadAllText(path), Is.EqualTo(corruptHistory));
                Assert.That(SessionState.GetString(key, string.Empty), Is.Empty);
                // Historical audit remains strict; active inventory must not hide
                // unreadable artifacts when a caller actually requests history.
                Assert.Throws<TargetInvocationException>(() => list.Invoke(null,
                    new object[] { new ConsoleCaptureListPayload { activeOnly = false }, false }));
                activeField.SetValue(null, null);
                SessionState.SetString(key, directory);
                var unresolved = Assert.Throws<TargetInvocationException>(() => list.Invoke(null,
                    new object[] { new ConsoleCaptureListPayload { activeOnly = true }, false }));
                Assert.That(unresolved.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(SessionState.GetString(key, string.Empty), Is.EqualTo(directory));
            }
            finally
            {
                activeField.SetValue(null, null);
                if (string.IsNullOrEmpty(previousDirectory)) SessionState.EraseString(key);
                else SessionState.SetString(key, previousDirectory);
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ConsoleCaptureStopFinalizesHistoricalActiveManifest()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var directory = Path.Combine(
                root,
                "Log",
                "UPilotConsole",
                "test-historical-stop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var sessionId = "console_test_historical_" + Guid.NewGuid().ToString("N");
            var manifestPath = Path.Combine(directory, "session.json");
            var summaryPath = Path.Combine(directory, "summary.json");
            var jsonlPath = Path.Combine(directory, "console.jsonl");
            const string ownerToken = "historical-owner-token";
            string ownerTokenSha256;
            using (var sha256 = SHA256.Create())
                ownerTokenSha256 = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(ownerToken)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            File.WriteAllText(jsonlPath, "{\"sequence\":0}\n");
            var manifest = new ConsoleCaptureManifest
            {
                sessionId = sessionId,
                title = "historical-stop",
                ownerTokenSha256 = ownerTokenSha256,
                directory = directory,
                jsonlPath = jsonlPath,
                manifestPath = manifestPath,
                summaryPath = summaryPath,
                active = true,
                startedAtUtcMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(),
            };
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));

            string activeDirectoryKey = null;
            string previousActiveDirectory = null;
            try
            {
                var activeField = typeof(UPilotConsoleCaptureService).GetField(
                    "s_active",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(activeField.GetValue(null), Is.Null,
                    "The historical-stop test requires no live capture.");
                var projectSessionKey = typeof(UPilotConsoleCaptureService).GetMethod(
                    "ProjectSessionKey",
                    BindingFlags.NonPublic | BindingFlags.Static);
                activeDirectoryKey = (string)projectSessionKey.Invoke(
                    null,
                    new object[] { "UPilot.ConsoleCapture.ActiveDirectory" });
                previousActiveDirectory = SessionState.GetString(activeDirectoryKey, string.Empty);
                SessionState.EraseString(activeDirectoryKey);
                activeField.SetValue(null, null);
                var stop = typeof(UPilotConsoleCaptureService).GetMethod(
                    "StopCapture",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var rejected = (ConsoleCaptureResult)stop.Invoke(null, new object[] { sessionId, string.Empty, false });
                var result = (ConsoleCaptureResult)stop.Invoke(null, new object[] { sessionId, ownerToken, false });
                var repeated = (ConsoleCaptureResult)stop.Invoke(null, new object[] { sessionId, ownerToken, false });
                var persisted = JsonUtility.FromJson<ConsoleCaptureManifest>(
                    File.ReadAllText(manifestPath));

                Assert.That(rejected.ok, Is.False);
                Assert.That(result.ok, Is.True);
                Assert.That(result.session.active, Is.False);
                Assert.That(persisted.active, Is.False);
                Assert.That(persisted.finishedAtUtcMs, Is.GreaterThan(0));
                Assert.That(persisted.sha256, Is.Not.Empty);
                Assert.That(repeated.ok, Is.True);
                Assert.That(repeated.session.active, Is.False);
                Assert.That(repeated.session.sha256, Is.EqualTo(persisted.sha256));
                Assert.That(File.Exists(summaryPath), Is.True);
            }
            finally
            {
                if (!string.IsNullOrEmpty(previousActiveDirectory))
                    SessionState.SetString(activeDirectoryKey, previousActiveDirectory);
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Serializable]
        private sealed class CaptureWriteFailureEvidence
        {
            public string sessionId, lockedArtifact, failureMessage, firstCaptureError;
            public int nativeError;
            public bool stopThrew, ownershipRetainedAfterFailure, ownershipReleased, repeatedStopUnchanged;
            public long droppedCount, finishedAtUtcMs;
            public string jsonlSha256, manifestSha256, summarySha256;
        }

        [TestCase("jsonl")]
        [TestCase("manifest")]
        [TestCase("summary")]
        public void ConsoleCaptureOwnedWriteSharingFailureIsObservableAndFinalizesAfterUnlock(string artifact)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Real Windows sharing violations require Windows.");
            var type = typeof(UPilotConsoleCaptureService);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var activeField = type.GetField("s_active", flags);
            var key = (string)type.GetMethod("ProjectSessionKey", flags).Invoke(null,
                new object[] { "UPilot.ConsoleCapture.ActiveDirectory" });
            Assert.That(activeField.GetValue(null), Is.Null, "Never replace another Capture.");
            Assert.That(SessionState.GetString(key, string.Empty), Is.Empty, "Never clear a recovery pointer.");
            var root = (string)type.GetMethod("GetDefaultCaptureRoot", flags).Invoke(null, null);
            var directory = Path.GetFullPath(Path.Combine(root, "test-write-sharing-" + artifact + "-" + Guid.NewGuid().ToString("N")));
            string ownerToken = Guid.NewGuid().ToString("N"); // Never written into evidence.
            ConsoleCaptureManifest owned = null;
            FileStream held = null;
            try
            {
                var started = UPilotConsoleCaptureService.StartCapture(new ConsoleCaptureStartPayload
                {
                    path = directory, title = "owned-write-sharing", ownerToken = ownerToken,
                    excludeUPilot = true, flushIntervalMs = 60000,
                });
                Assert.That(started.ok, Is.True, started.error);
                owned = started.session;
                var lockedPath = artifact == "jsonl" ? owned.jsonlPath :
                    artifact == "manifest" ? owned.manifestPath : owned.summaryPath;
                if (artifact == "summary") File.WriteAllText(lockedPath, "owned-summary-before-stop");
                var beforeBytes = File.ReadAllBytes(lockedPath);
                var evidence = new CaptureWriteFailureEvidence { sessionId = owned.sessionId, lockedArtifact = artifact };
                held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var nativeFailure = Assert.Throws<IOException>(() =>
                {
                    using var denied = new FileStream(lockedPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });
                evidence.nativeError = nativeFailure.HResult & 0xffff;
                Assert.That(evidence.nativeError, Is.EqualTo(32), "Must hit a real sharing violation, not a simulated failure.");
                evidence.failureMessage = nativeFailure.Message;
                if (artifact == "jsonl")
                {
                    type.GetMethod("OnLogMessageReceived", flags).Invoke(null,
                        new object[] { "owned-record-denied-by-sharing", string.Empty, LogType.Log });
                    type.GetMethod("FlushActiveCapture", flags).Invoke(null, new object[] { true });
                    var failed = UPilotConsoleCaptureService.GetStatus(owned.sessionId).session;
                    Assert.That(failed.active, Is.True);
                    Assert.That(failed.droppedCount, Is.EqualTo(1));
                    Assert.That(failed.lastError, Is.Not.Empty);
                    evidence.firstCaptureError = failed.lastError;
                }
                else
                {
                    var failed = Assert.Throws<IOException>(() =>
                        UPilotConsoleCaptureService.StopCapture(owned.sessionId, ownerToken, false));
                    Assert.That(failed.HResult & 0xffff, Is.EqualTo(32));
                    evidence.stopThrew = true;
                    evidence.failureMessage = failed.Message;
                    // No success receipt was returned and ownership is not yet released.
                    Assert.That(UPilotConsoleCaptureService.GetStatus(owned.sessionId).session.active, Is.False);
                }
                Assert.That(File.ReadAllBytes(lockedPath), Is.EqualTo(beforeBytes));
                evidence.ownershipRetainedAfterFailure = activeField.GetValue(null) != null &&
                    SessionState.GetString(key, string.Empty) == directory;
                Assert.That(evidence.ownershipRetainedAfterFailure, Is.True);
                held.Dispose();
                held = null;
                // A known, synchronous failure is followed by one deliberate stop of
                // this owned session after unlocking, never an uncertain public replay.
                var stopped = UPilotConsoleCaptureService.StopCapture(owned.sessionId, ownerToken, false);
                Assert.That(stopped.ok, Is.True, stopped.error);
                Assert.That(stopped.session.active, Is.False);
                Assert.That(stopped.session.finishedAtUtcMs, Is.GreaterThan(0));
                evidence.droppedCount = stopped.session.droppedCount;
                evidence.finishedAtUtcMs = stopped.session.finishedAtUtcMs;
                if (artifact == "jsonl")
                {
                    Assert.That(stopped.session.lastError, Is.EqualTo(evidence.firstCaptureError));
                    Assert.That(evidence.droppedCount, Is.EqualTo(1));
                    Assert.That(new FileInfo(owned.jsonlPath).Length, Is.Zero);
                }
                else Assert.That(evidence.droppedCount, Is.Zero);
                evidence.ownershipReleased = activeField.GetValue(null) == null &&
                    SessionState.GetString(key, string.Empty) == string.Empty;
                Assert.That(evidence.ownershipReleased, Is.True);
                var manifestBytes = File.ReadAllBytes(owned.manifestPath);
                var summaryBytes = File.ReadAllBytes(owned.summaryPath);
                Assert.That(summaryBytes, Is.EqualTo(manifestBytes));
                var repeated = UPilotConsoleCaptureService.StopCapture(owned.sessionId, ownerToken, false);
                evidence.repeatedStopUnchanged = repeated.ok &&
                    repeated.session.finishedAtUtcMs == evidence.finishedAtUtcMs &&
                    File.ReadAllBytes(owned.manifestPath).SequenceEqual(manifestBytes) &&
                    File.ReadAllBytes(owned.summaryPath).SequenceEqual(summaryBytes);
                Assert.That(evidence.repeatedStopUnchanged, Is.True);
                using var sha = SHA256.Create();
                evidence.jsonlSha256 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(owned.jsonlPath))).Replace("-", "").ToLowerInvariant();
                evidence.manifestSha256 = BitConverter.ToString(sha.ComputeHash(manifestBytes)).Replace("-", "").ToLowerInvariant();
                evidence.summarySha256 = BitConverter.ToString(sha.ComputeHash(summaryBytes)).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(Path.Combine(directory, "write-failure-evidence.json"),
                    JsonUtility.ToJson(evidence, true), new UTF8Encoding(false));
            }
            finally
            {
                held?.Dispose();
                // Never reset the global Capture or stop a session with unknown ownership.
                if (owned != null && activeField.GetValue(null) != null)
                {
                    var current = UPilotConsoleCaptureService.GetStatus(owned.sessionId).session;
                    Assert.That(current.sessionId, Is.EqualTo(owned.sessionId));
                    Assert.That(UPilotConsoleCaptureService.StopCapture(owned.sessionId, ownerToken, false).ok, Is.True);
                }
                Assert.That(activeField.GetValue(null), Is.Null);
                Assert.That(SessionState.GetString(key, string.Empty), Is.Empty);
            }
        }

        [Test]
        public void ConsoleSummaryUsesSharedClassificationAndBoundedSamples()
        {
            var summary = new ConsoleSummaryPayload();
            int[] modes = { 0, 1 << 9, 1 << 0, 1 << 1, 1 << 3 };
            foreach (int mode in modes)
            {
                UPilotConsoleService.AddToSummary(summary, new ConsoleLogEntry
                {
                    logType = UPilotConsoleService.ModeToLogType(mode),
                    message = "sample-" + mode,
                }, samplesPerType: 1);
            }
            UPilotConsoleService.AddToSummary(summary, new ConsoleLogEntry
            {
                logType = "Error",
                message = "second-error",
            }, samplesPerType: 1);

            Assert.That(summary.total, Is.EqualTo(6));
            Assert.That(summary.logCount, Is.EqualTo(1));
            Assert.That(summary.warningCount, Is.EqualTo(1));
            Assert.That(summary.errorCount, Is.EqualTo(2));
            Assert.That(summary.assertCount, Is.EqualTo(1));
            Assert.That(summary.exceptionCount, Is.EqualTo(1));
            Assert.That(summary.samples.Count, Is.EqualTo(5));
        }

        [Test]
        public void ConsoleUPilotFilterKeepsBusinessLogsTriggeredThroughReflection()
        {
            const string reflectionStack =
                "Business.Debug:WriteWarning()\n" +
                "CodingRiver.UPilot.UPilotReflectionService:Invoke()";
            const string loggerStack =
                "UnityEngine.Debug:Log(object)\n" +
                "CodingRiver.UPilot.Logger:WriteToUnityConsole(string,UnityEngine.LogType,string[])";

            Assert.That(
                UPilotConsoleService.IsUPilotOwnedLog("business UPilot integration warning", reflectionStack),
                Is.False);
            Assert.That(
                UPilotConsoleService.IsUPilotOwnedLog("[INFO ] [COMMAND ] request", string.Empty),
                Is.True);
            Assert.That(
                UPilotConsoleService.IsUPilotOwnedLog("ordinary message", loggerStack),
                Is.True);
            Assert.That(
                UPilotConsoleService.IsUPilotOwnedLog("[UPilot Flow] infrastructure", string.Empty),
                Is.True);
        }

        [TestCase(0, false)]
        [TestCase(1, false)]
        [TestCase(2, false)]
        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, true)]
        public void ConsoleCaptureReadRejectsMissingSegmentsWithoutChangingIndex(int missingIndex, bool continuation)
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var directory = Path.Combine(root, "Log", "UPilotConsole", "test-missing-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var paths = new[] { "console.jsonl", "console.001.jsonl", "console.002.jsonl" }
                .Select(name => Path.Combine(directory, name)).ToArray();
            var missingPath = paths[missingIndex];
            var backupPath = missingPath + ".owned-backup";
            try
            {
                for (int index = 0; index < paths.Length; index++)
                    File.WriteAllText(paths[index], JsonUtility.ToJson(new ConsoleCaptureRecord
                    {
                        sequence = index, message = "owned-marker " + index, logType = "Log",
                    }) + "\n");
                var manifest = new ConsoleCaptureManifest
                {
                    sessionId = "console_test_missing_read", directory = directory,
                    segmentCount = paths.Length, nextSequence = paths.Length,
                };
                var read = typeof(UPilotConsoleCaptureService).GetMethod(
                    "ReadCaptureFiles", BindingFlags.NonPublic | BindingFlags.Static);
                var first = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest, new ConsoleCaptureReadPayload { count = 1, regex = "owned-marker" }, CancellationToken.None,
                });
                Assert.That(first.ok, Is.True, first.error);
                Assert.That(first.continuationToken, Is.Not.Empty);
                byte[] indexBefore = File.ReadAllBytes(first.indexPath);
                File.Move(missingPath, backupPath);
                var failure = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest, new ConsoleCaptureReadPayload
                    {
                        count = 1, contains = continuation ? null : new[] { "no-matching-log" },
                        continuationToken = continuation ? first.continuationToken : string.Empty,
                    }, CancellationToken.None,
                });
                Assert.That(failure.ok, Is.False);
                Assert.That(failure.error, Is.EqualTo("CONSOLE_CAPTURE_SEGMENT_MISSING: " + Path.GetFileName(missingPath)));
                Assert.That(failure.sessionId, Is.EqualTo(manifest.sessionId));
                Assert.That(failure.scanComplete, Is.False);
                Assert.That(failure.logs, Is.Empty);
                Assert.That(failure.continuationToken, Is.Null.Or.Empty);
                Assert.That(File.ReadAllBytes(first.indexPath), Is.EqualTo(indexBefore));
                Assert.That(manifest.segmentCount, Is.EqualTo(3));
                Assert.That(manifest.nextSequence, Is.EqualTo(3));
                File.Move(backupPath, missingPath);
                var restored = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest, new ConsoleCaptureReadPayload { count = 10 }, CancellationToken.None,
                });
                Assert.That(restored.ok, Is.True, restored.error);
                Assert.That(restored.logs.Select(item => item.sequence), Is.EqualTo(new long[] { 0, 1, 2 }));
                Assert.That(restored.scanComplete, Is.True);
            }
            finally
            {
                if (File.Exists(backupPath) && !File.Exists(missingPath)) File.Move(backupPath, missingPath);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConsoleCaptureReadRejectsSegmentRemovedDuringScan(bool continuation)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("This fixture passively observes Windows file owners during a real scan.");
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), @"D:\upilot\Tests~\UPilotTest", StringComparison.OrdinalIgnoreCase))
                Assert.Ignore("Use the exact canonical acceptance project for this controlled file race.");
            var directory = Path.Combine(root, "Log", "UPilotConsole", "test-scan-gap-" + Guid.NewGuid().ToString("N"));
            var firstPath = Path.Combine(directory, "console.jsonl");
            var missingPath = Path.Combine(directory, "console.001.jsonl");
            var backupPath = missingPath + ".owned-backup";
            var stop = new ManualResetEventSlim(false);
            var ready = new ManualResetEventSlim(false);
            Thread mover = null;
            Exception moveError = null;
            bool scanObserved = false;
            bool calibrationObserved = false;
            int ownerQueries = 0;
            long queryTicks = 0, maxQueryTicks = 0;
            long readStartedTicks = -1, readFinishedTicks = -1, movedTicks = -1;
            var observationClock = System.Diagnostics.Stopwatch.StartNew();
            Func<long, double> milliseconds = ticks => ticks < 0 ? -1d :
                ticks * 1000d / System.Diagnostics.Stopwatch.Frequency;
            Func<string> observationDetails = () => string.Format(
                "Capture scan observer: continuation={0}, calibrated={1}, ownerQueries={2}, " +
                "queryTotalMs={3:F3}, queryMaxMs={4:F3}, readStartMs={5:F3}, readEndMs={6:F3}, movedMs={7:F3}",
                continuation, calibrationObserved, ownerQueries, milliseconds(queryTicks), milliseconds(maxQueryTicks),
                milliseconds(readStartedTicks), milliseconds(readFinishedTicks), milliseconds(movedTicks));
            try
            {
                Directory.CreateDirectory(directory);
                using (var writer = new StreamWriter(firstPath, false, new UTF8Encoding(false)))
                {
                    string message = "owned scan marker " + new string('x', 65536);
                    for (int index = 0; index < 128; index++)
                        writer.WriteLine(JsonUtility.ToJson(new ConsoleCaptureRecord
                        {
                            sequence = index, message = message, logType = "Log",
                        }));
                }
                File.WriteAllText(missingPath, JsonUtility.ToJson(new ConsoleCaptureRecord
                {
                    sequence = 128, message = "owned final marker", logType = "Log",
                }) + Environment.NewLine);
                byte[] expectedSegment = File.ReadAllBytes(missingPath);
                var manifest = new ConsoleCaptureManifest
                {
                    sessionId = "console_test_scan_gap", directory = directory, segmentCount = 2, nextSequence = 129,
                };
                var read = typeof(UPilotConsoleCaptureService).GetMethod(
                    "ReadCaptureFiles", BindingFlags.NonPublic | BindingFlags.Static);
                var warm = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest, new ConsoleCaptureReadPayload { count = 1, newestFirst = true }, CancellationToken.None,
                });
                Assert.That(warm.ok, Is.True, warm.error);
                Assert.That(warm.continuationToken, Is.Not.Empty);
                byte[] indexBefore = File.ReadAllBytes(warm.indexPath);
                // Reuse only the existing resource-scoped native owner query.
                // Calibrate a held read handle before claiming this observes a short scan;
                // query latency is measured rather than assumed to explain a missed race.
                const BindingFlags nativeFlags = BindingFlags.NonPublic | BindingFlags.Static;
                var observerType = typeof(UPilotProcessOccupancyService);
                var startSession = observerType.GetMethod("RmStartSession", nativeFlags);
                var register = observerType.GetMethod("RmRegisterResources", nativeFlags);
                var getList = observerType.GetMethod("RmGetList", nativeFlags);
                var endSession = observerType.GetMethod("RmEndSession", nativeFlags);
                Assert.That(startSession, Is.Not.Null);
                Assert.That(register, Is.Not.Null);
                Assert.That(getList, Is.Not.Null);
                Assert.That(endSession, Is.Not.Null);
                var entryType = getList.GetParameters()[3].ParameterType.GetElementType();
                var processField = entryType.GetField("Process");
                var pidField = processField.FieldType.GetField("dwProcessId");
                int editorPid;
                using (var editor = System.Diagnostics.Process.GetCurrentProcess()) editorPid = editor.Id;
                mover = new Thread(() =>
                {
                    uint session = 0;
                    bool sessionOpened = false;
                    try
                    {
                        var startArguments = new object[] { 0u, 0, new StringBuilder(33) };
                        int status = (int)startSession.Invoke(null, startArguments);
                        if (status != 0) throw new InvalidOperationException("RmStartSession: " + status);
                        session = (uint)startArguments[0];
                        sessionOpened = true;
                        status = (int)register.Invoke(null, new object[]
                        {
                            session, 1u, new[] { firstPath }, 0u, null, 0u, null,
                        });
                        if (status != 0) throw new InvalidOperationException("RmRegisterResources: " + status);
                        var entries = Array.CreateInstance(entryType, 16);
                        Func<bool> editorOwnsFirstSegment = () =>
                        {
                            long queryStartedTicks = observationClock.ElapsedTicks;
                            try
                            {
                                var arguments = new object[] { session, 0u, (uint)entries.Length, entries, 0u };
                                int query = (int)getList.Invoke(null, arguments);
                                if (query == 234)
                                {
                                    uint needed = (uint)arguments[1];
                                    if (needed > 256) throw new InvalidOperationException("File owner list exceeds fixture bound.");
                                    entries = Array.CreateInstance(entryType, (int)needed);
                                    arguments[2] = needed;
                                    arguments[3] = entries;
                                    query = (int)getList.Invoke(null, arguments);
                                }
                                if (query != 0) throw new InvalidOperationException("RmGetList: " + query);
                                for (int index = 0; index < (uint)arguments[2]; index++)
                                    if ((int)pidField.GetValue(processField.GetValue(entries.GetValue(index))) == editorPid)
                                        return true;
                                return false;
                            }
                            finally
                            {
                                long elapsedTicks = observationClock.ElapsedTicks - queryStartedTicks;
                                ownerQueries++;
                                queryTicks += elapsedTicks;
                                maxQueryTicks = Math.Max(maxQueryTicks, elapsedTicks);
                            }
                        };
                        if (editorOwnsFirstSegment())
                            throw new InvalidOperationException("Unexpected editor ownership before the read.");
                        // Same flags as the real scan, held only for observer calibration.
                        // The handle is disposed before readiness; it cannot block the scan.
                        using (new FileStream(firstPath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan))
                        {
                            calibrationObserved = editorOwnsFirstSegment();
                            if (!calibrationObserved)
                                throw new InvalidOperationException(
                                    "Observer calibration failed: the held owned read handle was not observed; no race coverage.");
                        }
                        // The previous acceptance observed owner retention after disposal.
                        // End calibration before starting an independent scan observation;
                        // a fresh session must not attribute that old handle to the scan.
                        status = (int)endSession.Invoke(null, new object[] { session });
                        if (status != 0) throw new InvalidOperationException("RmEndSession calibration: " + status);
                        sessionOpened = false;
                        startArguments = new object[] { 0u, 0, new StringBuilder(33) };
                        status = (int)startSession.Invoke(null, startArguments);
                        if (status != 0) throw new InvalidOperationException("RmStartSession scan: " + status);
                        session = (uint)startArguments[0];
                        sessionOpened = true;
                        status = (int)register.Invoke(null, new object[]
                        {
                            session, 1u, new[] { firstPath }, 0u, null, 0u, null,
                        });
                        if (status != 0) throw new InvalidOperationException("RmRegisterResources scan: " + status);
                        if (editorOwnsFirstSegment())
                            throw new InvalidOperationException("Fresh scan observer reports ownership before the read; no race coverage.");
                        ready.Set();
                        var budget = System.Diagnostics.Stopwatch.StartNew();
                        while (!stop.IsSet && budget.ElapsedMilliseconds < 5000)
                        {
                            if (editorOwnsFirstSegment() && !stop.IsSet)
                            {
                                // Only our stopped fixture is touched; no open probe,
                                // process termination, shared Capture or product hook.
                                File.Move(missingPath, backupPath);
                                movedTicks = observationClock.ElapsedTicks;
                                scanObserved = true;
                                return;
                            }
                            Thread.Sleep(1);
                        }
                    }
                    catch (Exception ex) { moveError = ex; }
                    finally
                    {
                        if (sessionOpened)
                        {
                            try
                            {
                                int status = (int)endSession.Invoke(null, new object[] { session });
                                if (status != 0 && moveError == null)
                                    moveError = new InvalidOperationException("RmEndSession: " + status);
                            }
                            catch (Exception ex) { if (moveError == null) moveError = ex; }
                        }
                        ready.Set();
                    }
                }) { IsBackground = true, Name = "UPilot-owned-Capture-scan-gap" };
                mover.Start();
                Assert.That(ready.Wait(5000), Is.True, "File race worker did not become ready.");
                if (moveError != null)
                {
                    Assert.That(mover.Join(5000), Is.True, "Failed observer must finish before reading diagnostics.");
                    Assert.That(moveError, Is.Null, observationDetails());
                }
                ConsoleCaptureReadResult result;
                readStartedTicks = observationClock.ElapsedTicks;
                try
                {
                    result = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                    {
                        manifest, new ConsoleCaptureReadPayload
                        {
                            count = 1, newestFirst = true,
                            continuationToken = continuation ? warm.continuationToken : string.Empty,
                        }, CancellationToken.None,
                    });
                }
                finally
                {
                    readFinishedTicks = observationClock.ElapsedTicks;
                    stop.Set();
                }
                Assert.That(mover.Join(5000), Is.True, "File race worker must finish before restoration.");
                Assert.That(moveError, Is.Null, observationDetails());
                Assert.That(scanObserved, Is.True,
                    "No concurrent removal observed: this is not race coverage. " + observationDetails());
                Assert.That(movedTicks, Is.InRange(readStartedTicks, readFinishedTicks),
                    "The owned segment must be removed during the actual read. " + observationDetails());
                Assert.That(result.ok, Is.False);
                Assert.That(result.sessionId, Is.EqualTo(manifest.sessionId));
                Assert.That(result.error, Is.EqualTo("CONSOLE_CAPTURE_SEGMENT_MISSING: console.001.jsonl"));
                Assert.That(result.scanComplete, Is.False);
                Assert.That(result.logs, Is.Empty);
                Assert.That(result.continuationToken, Is.Null.Or.Empty);
                Assert.That(File.ReadAllBytes(warm.indexPath), Is.EqualTo(indexBefore));
                File.Move(backupPath, missingPath);
                Assert.That(File.ReadAllBytes(missingPath), Is.EqualTo(expectedSegment));
                var recovered = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest, new ConsoleCaptureReadPayload
                    {
                        count = 1, newestFirst = true, continuationToken = warm.continuationToken,
                    }, CancellationToken.None,
                });
                Assert.That(recovered.ok, Is.True, recovered.error);
                Assert.That(recovered.logs.Single().sequence, Is.EqualTo(127));
            }
            finally
            {
                stop.Set();
                bool joined = mover == null || mover.Join(5000);
                if (joined)
                {
                    // Worker-owned facts are read only after join, including calibration failures.
                    TestContext.Out.WriteLine(observationDetails());
                    if (File.Exists(backupPath) && !File.Exists(missingPath)) File.Move(backupPath, missingPath);
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                    ready.Dispose();
                    stop.Dispose();
                }
                Assert.That(joined, Is.True, "Worker still running: retain the owned directory instead of racing cleanup.");
            }
        }

        [Test]
        public void ConsoleCaptureReadPreservesSharingFailureForAnExistingSegment()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("This fixture requires Windows file-sharing semantics.");
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var directory = Path.Combine(root, "Log", "UPilotConsole", "test-read-sharing-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "console.jsonl");
                File.WriteAllText(file, "owned segment");
                var manifest = new ConsoleCaptureManifest
                {
                    sessionId = "console_test_read_sharing", directory = directory, segmentCount = 1,
                };
                var read = typeof(UPilotConsoleCaptureService).GetMethod(
                    "ReadCaptureFiles", BindingFlags.NonPublic | BindingFlags.Static);
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var error = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, new object[]
                    {
                        manifest, new ConsoleCaptureReadPayload(), CancellationToken.None,
                    }));
                    Assert.That(error.InnerException, Is.TypeOf<IOException>());
                    Assert.That(error.InnerException.HResult & 0xffff, Is.EqualTo(32));
                    Assert.That(File.Exists(file), Is.True);
                }
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConsoleCaptureReadDistinguishesEmptyFileFromMissingDirectory(bool missingDirectory)
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var directory = Path.Combine(root, "Log", "UPilotConsole", "test-zero-read-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (!missingDirectory)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(Path.Combine(directory, "console.jsonl"), Array.Empty<byte>());
                }
                var manifest = new ConsoleCaptureManifest
                {
                    sessionId = "console_test_zero_read", directory = directory,
                    segmentCount = missingDirectory ? int.MaxValue : 1, nextSequence = 0,
                };
                var read = typeof(UPilotConsoleCaptureService).GetMethod(
                    "ReadCaptureFiles", BindingFlags.NonPublic | BindingFlags.Static);
                var result = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest, new ConsoleCaptureReadPayload(), CancellationToken.None,
                });
                Assert.That(result.ok, Is.EqualTo(!missingDirectory));
                Assert.That(result.scanComplete, Is.EqualTo(!missingDirectory));
                Assert.That(result.logs, Is.Empty);
                if (missingDirectory)
                    Assert.That(result.error, Is.EqualTo("CONSOLE_CAPTURE_SEGMENT_MISSING: console.jsonl"));
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var error = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, new object[]
                {
                    manifest, new ConsoleCaptureReadPayload(), cancelled.Token,
                }));
                Assert.That(error.InnerException, Is.TypeOf<OperationCanceledException>());
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ConsoleCaptureReadPaginatesFilteredSnapshotWithoutDuplicates()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var directory = Path.Combine(root, "Log", "UPilotConsole", "test-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var records = Enumerable.Range(0, 40)
                    .Select(index => new ConsoleCaptureRecord
                    {
                        sequence = index,
                        timestampUtcMs = index,
                        logType = index % 2 == 0 ? "Log" : "Warning",
                        message = index % 3 == 0 ? "RewardPending " + index : "Other " + index,
                        stackTrace = string.Empty,
                    });
                File.WriteAllLines(
                    Path.Combine(directory, "console.jsonl"),
                    records.Select(JsonUtility.ToJson));
                var manifest = new ConsoleCaptureManifest
                {
                    sessionId = "console_test_read",
                    directory = directory,
                    nextSequence = 40,
                };
                var read = typeof(UPilotConsoleCaptureService).GetMethod(
                    "ReadCaptureFiles",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var first = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest,
                    new ConsoleCaptureReadPayload
                    {
                        count = 4,
                        fromSequence = 5,
                        toSequence = 30,
                        contains = new[] { "RewardPending", "SkillTrapCast" },
                    },
                    CancellationToken.None,
                });
                var second = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest,
                    new ConsoleCaptureReadPayload
                    {
                        count = 4,
                        continuationToken = first.continuationToken,
                    },
                    CancellationToken.None,
                });

                Assert.That(first.ok, Is.True);
                Assert.That(first.totalMatchCount, Is.EqualTo(9));
                Assert.That(first.logs.Select(item => item.sequence), Is.EqualTo(new long[] { 6, 9, 12, 15 }));
                Assert.That(second.logs.Select(item => item.sequence), Is.EqualTo(new long[] { 18, 21, 24, 27 }));
                Assert.That(first.logs.Select(item => item.sequence).Intersect(second.logs.Select(item => item.sequence)), Is.Empty);
                Assert.That(first.indexUsed, Is.True);
                Assert.That(File.Exists(first.indexPath), Is.True);
                Assert.That(first.effectiveFromSequence, Is.EqualTo(5));
                Assert.That(first.effectiveToSequence, Is.EqualTo(30));
                Assert.That(first.effectiveQuery.fromSequence, Is.EqualTo(5));
                Assert.That(first.effectiveQuery.toSequence, Is.EqualTo(30));
                Assert.That(first.effectiveQuery.contains, Is.EqualTo(new[] { "RewardPending", "SkillTrapCast" }));
                Assert.That(first.matchedFields, Does.Contain("contains"));
                Assert.That(first.matchedFields, Does.Contain("fromSequence"));
                Assert.That(first.matchedFields, Does.Contain("toSequence"));
                Assert.That(first.ignoredArguments, Is.Empty);
                Assert.That(new[] { "created", "used" }, Does.Contain(first.indexStatus));
                Assert.That(first.managedMemoryBeforeBytes, Is.GreaterThan(0));
                Assert.That(first.managedMemoryAfterBytes, Is.GreaterThan(0));
                Assert.That(first.processWorkingSetBeforeBytes, Is.GreaterThan(0));
                Assert.That(first.processWorkingSetAfterBytes, Is.GreaterThan(0));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ConsoleCaptureReadAdvancesPastScannedRecordsWhenFilterHasNoMatches()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var directory = Path.Combine(root, "Log", "UPilotConsole", "test-empty-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var records = Enumerable.Range(10, 5).Select(index => new ConsoleCaptureRecord
                {
                    sequence = index,
                    timestampUtcMs = index,
                    logType = "Log",
                    message = "ordinary " + index,
                    stackTrace = string.Empty,
                });
                File.WriteAllLines(Path.Combine(directory, "console.jsonl"), records.Select(JsonUtility.ToJson));
                var manifest = new ConsoleCaptureManifest
                {
                    sessionId = "console_test_empty_read",
                    directory = directory,
                    nextSequence = 15,
                };
                var read = typeof(UPilotConsoleCaptureService).GetMethod(
                    "ReadCaptureFiles", BindingFlags.NonPublic | BindingFlags.Static);
                var result = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                {
                    manifest,
                    new ConsoleCaptureReadPayload { afterSequence = 9, contains = new[] { "missing" } },
                    CancellationToken.None,
                });

                Assert.That(result.ok, Is.True);
                Assert.That(result.logs, Is.Empty);
                Assert.That(result.scannedCount, Is.EqualTo(5));
                Assert.That(result.scannedToSequence, Is.EqualTo(14));
                Assert.That(result.nextSequence, Is.EqualTo(14));
                Assert.That(result.scanComplete, Is.True);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ConsoleCaptureLiteralRegexPaginatesMoreThanPageLimitWithoutLoss()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var directory = Path.Combine(root, "Log", "UPilotConsole", "test-regex-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var records = Enumerable.Range(0, 420)
                    .Select(index => new ConsoleCaptureRecord
                    {
                        sequence = index,
                        timestampUtcMs = index,
                        logType = "Log",
                        message = index % 2 == 0 ? "SkillTrapCast " + index : "Other " + index,
                        stackTrace = string.Empty,
                    });
                File.WriteAllLines(
                    Path.Combine(directory, "console.jsonl"),
                    records.Select(JsonUtility.ToJson));
                var manifest = new ConsoleCaptureManifest
                {
                    sessionId = "console_test_regex_read",
                    directory = directory,
                    nextSequence = 420,
                };
                var read = typeof(UPilotConsoleCaptureService).GetMethod(
                    "ReadCaptureFiles",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var all = new System.Collections.Generic.List<long>();
                string continuationToken = string.Empty;
                long totalMatches = -1;
                do
                {
                    var result = (ConsoleCaptureReadResult)read.Invoke(null, new object[]
                    {
                        manifest,
                        new ConsoleCaptureReadPayload
                        {
                            count = 150,
                            regex = string.IsNullOrEmpty(continuationToken) ? "RewardPending|SkillTrapCast" : string.Empty,
                            continuationToken = continuationToken,
                        },
                        CancellationToken.None,
                    });
                    Assert.That(result.ok, Is.True, result.error);
                    if (totalMatches < 0) totalMatches = result.totalMatchCount;
                    all.AddRange(result.logs.Select(item => item.sequence));
                    continuationToken = result.continuationToken;
                } while (!string.IsNullOrEmpty(continuationToken));

                Assert.That(totalMatches, Is.EqualTo(210));
                Assert.That(all.Count, Is.EqualTo(210));
                Assert.That(all.Distinct().Count(), Is.EqualTo(210));
                Assert.That(all, Is.Ordered.Ascending);
                Assert.That(all.First(), Is.EqualTo(0));
                Assert.That(all.Last(), Is.EqualTo(418));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void AnimationAuditDtosAreReadOnlyResultShapes()
        {
            var controller = new AnimatorControllerAuditResultPayload();
            var mask = new AvatarMaskAuditResultPayload();
            var importer = new ModelImporterAuditResultPayload();

            Assert.That(controller.layers, Is.Not.Null);
            Assert.That(controller.states, Is.Not.Null);
            Assert.That(mask.transforms, Is.Not.Null);
            Assert.That(importer.clips, Is.Not.Null);
        }

        [UnityTest]
        [Explicit("Long-running MCP operation wait-window acceptance case.")]
        public IEnumerator OperationWaitWindowContractLongRun()
        {
            var deadline = EditorApplication.timeSinceStartup + 90.0;
            while (EditorApplication.timeSinceStartup < deadline)
                yield return null;

            Assert.Pass();
        }

        public static string BusyLoopForHangDiagnostics()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long accumulator = 0;
            while (stopwatch.ElapsedMilliseconds < 15000)
            {
                accumulator ^= stopwatch.ElapsedTicks;
                Thread.SpinWait(512);
            }

            return $"completed:{accumulator}";
        }

        [Test]
        public void AgentTemplateUsesCapabilityCompileAndWorkflowRules()
        {
            var method = typeof(UPilotAgentSetup).GetMethod(
                "BuildAgentsMd",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            var text = (string)method.Invoke(null, null);

            Assert.That(text, Does.Contain("Parent Agent rules path"));
            Assert.That(text, Does.Contain("visited set"));
            Assert.That(text, Does.Contain("circular references are skipped"));
            Assert.That(text, Does.Contain("unity_capabilities_get"));
            Assert.That(text, Does.Contain("prefer an available UPilot semantic tool"));
            Assert.That(text, Does.Contain("Use `unity_tools_find` for targeted discovery"));
            Assert.That(text, Does.Contain("`unity_reflection_call` is the single public generic reflection entry point"));
            Assert.That(text, Does.Contain("pass `typeName` + `methodName`"));
            Assert.That(text, Does.Contain("pass only `expression`"));
            Assert.That(text, Does.Contain("`kind=auto` selects the engine"));
            Assert.That(text, Does.Contain("never retries through the other engine"));
            Assert.That(text, Does.Contain("No separate public reflection-expression alias is exposed"));
            Assert.That(text, Does.Contain("unity_write_batch_register(paths, compileWhenEditMode=true)"));
            Assert.That(text, Does.Contain("automatically performs one sync and one safe compile"));
            Assert.That(text, Does.Contain("do not start a second manual sync or compile"));
            Assert.That(text, Does.Contain("`compiler_finished`, `domain_reload`, and `verifying`"));
            Assert.That(text, Does.Contain("`ok=true` with `status/phase=failed`"));
            Assert.That(text, Does.Contain("`ready`, `blocked`, `blockedReason`, `authoritative`, `isStale`, and `nextAction`"));
            Assert.That(text, Does.Contain("Do not infer readiness from raw `isPlaying` or `isCompiling`"));
            Assert.That(text, Does.Contain("Do not compile again when no code changed"));
            Assert.That(text, Does.Contain("Optional UPilot Tracer"));
            Assert.That(text, Does.Contain("`追踪器`, or `the tracer` as UPilot Tracer (`UPilot 追踪器`)"));
            Assert.That(text, Does.Contain("All trace points, stack capture, and Console output default to disabled"));
            Assert.That(text, Does.Contain("saves without applying by default"));
            Assert.That(text, Does.Contain("Do not use Native, InternalCall, injected"));
            Assert.That(text, Does.Contain("restore the original configuration"));
            Assert.That(text, Does.Contain("project-provided bridge entry points"));
            Assert.That(text, Does.Contain("waitWindowElapsed=true/terminal=false"));
            Assert.That(text, Does.Contain("unity_console_capture_start"));
            Assert.That(text, Does.Contain("Call `unity_console_capture_stop` only with that matching token"));
            Assert.That(text, Does.Contain("`nextSequence` as the next call's `afterSequence`"));
            Assert.That(text, Does.Contain("unity_console_capture_list(activeOnly=true)"));
            Assert.That(text, Does.Contain("inspect `activeCount/returnedCount`"));
            Assert.That(text, Does.Contain("separate from domain-specific reports"));
            Assert.That(text, Does.Contain("unity_config_csv_get"));
            Assert.That(text, Does.Contain("unity_config_csv_patch"));
            Assert.That(text, Does.Contain("explicit write approval"));
            Assert.That(text, Does.Contain("unity_hang_status"));
            Assert.That(text, Does.Contain("unity_hang_capture"));
            Assert.That(text, Does.Contain("`acceptedAsEvidence=true`, `pixelSourceVerified=true`, and `occlusionSensitive=false`"));
            Assert.That(text, Does.Contain("unity_editor_windows_list"));
            Assert.That(text, Does.Contain("incremental status, log, and report APIs"));
            Assert.That(text, Does.Contain("project-relative artifact paths"));
            Assert.That(text, Does.Not.Contain("{{"));
            Assert.That(text, Does.Not.Contain("## UPilot Flow"));
        }

        [Test]
        public void ManagedAgentRuleComparisonIgnoresGeneratedTimestamp()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-rule-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var currentText = BuildAgentRulesText();
                var installedText = ReplaceLine(
                    currentText,
                    "generatedAt: ",
                    "generatedAt: 2000-01-01T00:00:00Z");
                var path = Path.Combine(directory, "AGENTS.md");
                File.WriteAllText(path, WrapManagedRule(installedText));

                var state = InspectManagedRuleState(path, currentText);

                Assert.That(state, Is.EqualTo(AgentRuleConfigState.Current));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void ManagedAgentRuleComparisonDetectsRuleTemplateVersionChange()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-rule-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var currentText = BuildAgentRulesText();
                var currentVersion = GetAgentRulesTemplateVersion();
                var installedText = ReplaceLine(
                    currentText,
                    "rulesVersion: ",
                    "rulesVersion: " + Math.Max(0, currentVersion - 1));
                var path = Path.Combine(directory, "AGENTS.md");
                File.WriteAllText(path, WrapManagedRule(installedText));

                var state = InspectManagedRuleState(path, currentText);

                Assert.That(state, Is.EqualTo(AgentRuleConfigState.UpdateAvailable));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void ManagedAgentRuleComparisonDetectsPackageVersionChange()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-rule-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var currentText = BuildAgentRulesText();
                var installedText = ReplaceLine(
                    currentText,
                    "upilotPackageVersion: ",
                    "upilotPackageVersion: 0.0.0-test");
                var path = Path.Combine(directory, "AGENTS.md");
                File.WriteAllText(path, WrapManagedRule(installedText));

                var state = InspectManagedRuleState(path, currentText);

                Assert.That(state, Is.EqualTo(AgentRuleConfigState.UpdateAvailable));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void CustomizedAgentManagedBlockIsBackedUpAndRepairedWithoutChangingBusinessRules()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-agent-rule-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "AGENTS.md");
            var backupRoot = Path.Combine(directory, ".upilot", "backups", "agent-integrations");
            var backupsBefore = Directory.Exists(backupRoot)
                ? new HashSet<string>(Directory.GetDirectories(backupRoot), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string createdBackup = null;
            Directory.CreateDirectory(directory);
            try
            {
                var customized = "rulesVersion: " + GetAgentRulesTemplateVersion() + "\nmanaged: customized\n";
                var original = "# Business rules\n\n" + WrapManagedRule(customized);
                File.WriteAllText(path, original);
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/io.github.codingriver.upilot/package.json").resolvedPath;
                var result = UPilotAgentSetup.SyncAgentIntegrationsAt(directory, package, 8011, true,
                    AgentIntegrationScope.SharedAgents);
                Assert.That(result.ok, Is.True, result.error);

                var updated = File.ReadAllText(path);
                Assert.That(updated, Does.StartWith("# Business rules"));
                Assert.That(updated, Does.Contain("rulesVersion: " + GetAgentRulesTemplateVersion()));
                Assert.That(updated, Does.Not.Contain("managed: customized"));
                Assert.That(result.targets[0].backupPath, Is.Not.Empty);
                createdBackup = Directory.GetDirectories(backupRoot)
                    .Single(candidate => !backupsBefore.Contains(candidate));
                var backupFile = Directory.GetFiles(createdBackup, "AGENTS.md", SearchOption.AllDirectories).Single();
                Assert.That(File.ReadAllText(backupFile), Is.EqualTo(original));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                if (!string.IsNullOrEmpty(createdBackup) && Directory.Exists(createdBackup))
                    Directory.Delete(createdBackup, recursive: true);
            }
        }

        [Test]
        public void AgentRulesAutoSetupKeyTracksRulesAndSkillTemplateVersions()
        {
            var rulesVersion = GetAgentRulesTemplateVersion();
            var skillVersion = GetSkillInstallTemplateVersion();

            var keys = UPilotAgentSetup.GetAgentRulesPreferenceKeysForCurrentProject();

            Assert.That(keys, Has.Some.EndsWith($".rules.v{rulesVersion}.skill.v{skillVersion}"));
            Assert.That(keys, Has.Some.EndsWith($".v{rulesVersion}"));
        }

        [Test]
        public void AgentSetupExposesMcpRuleAndSkillStatusesInSameOrder()
        {
            var mcpStatuses = UPilotAgentSetup.GetMcpConfigStatuses();
            var ruleStatuses = UPilotAgentSetup.GetRuleConfigStatuses();
            var skillStatuses = UPilotAgentSetup.GetSkillConfigStatuses();

            Assert.That(mcpStatuses.Length, Is.EqualTo(4));
            Assert.That(ruleStatuses.Length, Is.EqualTo(4));
            Assert.That(skillStatuses.Length, Is.EqualTo(4));
            Assert.That(mcpStatuses[0].ClientName, Is.EqualTo("Codex"));
            Assert.That(mcpStatuses[1].ClientName, Is.EqualTo("Claude Code"));
            Assert.That(mcpStatuses[2].ClientName, Is.EqualTo("Cursor"));
            Assert.That(mcpStatuses[3].ClientName, Is.EqualTo("OpenCode"));
            Assert.That(ruleStatuses[0].ClientName, Is.EqualTo("Codex"));
            Assert.That(ruleStatuses[1].ClientName, Is.EqualTo("Claude Code"));
            Assert.That(ruleStatuses[2].ClientName, Is.EqualTo("Cursor"));
            Assert.That(ruleStatuses[3].ClientName, Is.EqualTo("OpenCode"));
            Assert.That(ruleStatuses[3].ConfigPath, Is.EqualTo(ruleStatuses[0].ConfigPath));
            Assert.That(ruleStatuses[3].IssuePaths, Is.EqualTo(ruleStatuses[0].IssuePaths));
            Assert.That(skillStatuses[0].ClientName, Is.EqualTo("Codex"));
            Assert.That(skillStatuses[1].ClientName, Is.EqualTo("Claude Code"));
            Assert.That(skillStatuses[2].ClientName, Is.EqualTo("Cursor"));
            Assert.That(skillStatuses[3].ClientName, Is.EqualTo("OpenCode"));
            Assert.That(skillStatuses[0].IsApplicable, Is.True);
            Assert.That(skillStatuses[0].InstalledSkillCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(skillStatuses[0].InstalledSkillNames, Does.Contain("upilot-unity-mcp"));
            Assert.That(skillStatuses[0].UpilotSkillCount, Is.EqualTo(1));
            Assert.That(skillStatuses[0].AssociatedToolCount, Is.GreaterThan(0));
            Assert.That(skillStatuses[0].PrimaryToolNames, Does.Contain("unity_mcp_status"));
            Assert.That(skillStatuses[0].CapabilityLabels, Is.Not.Empty);
            Assert.That(skillStatuses[1].IsApplicable, Is.True);
            Assert.That(skillStatuses[2].IsApplicable, Is.True);
            Assert.That(skillStatuses[1].State, Is.EqualTo(AgentSkillConfigState.Current));
            Assert.That(skillStatuses[2].State, Is.EqualTo(AgentSkillConfigState.Current));
            Assert.That(skillStatuses[1].InstalledSkillNames, Does.Contain("upilot-unity-mcp"));
            Assert.That(skillStatuses[2].InstalledSkillNames, Does.Contain("upilot-unity-mcp"));
            Assert.That(skillStatuses[1].AssociatedToolCount, Is.GreaterThan(0));
            Assert.That(skillStatuses[2].AssociatedToolCount, Is.GreaterThan(0));
            Assert.That(skillStatuses[0].ConfigPath, Is.EqualTo(skillStatuses[2].ConfigPath));
            Assert.That(skillStatuses[2].ApplicabilityExplanation, Does.Contain("共享"));
            Assert.That(skillStatuses[2].SkillRootPaths, Is.Not.Empty);
            Assert.That(skillStatuses[1].IsSatisfied, Is.True);
            Assert.That(skillStatuses[2].IsSatisfied, Is.True);
            Assert.That(skillStatuses[3].IsApplicable, Is.True);
            Assert.That(skillStatuses[3].State, Is.EqualTo(AgentSkillConfigState.Current));
            Assert.That(skillStatuses[3].ConfigPath, Is.EqualTo(skillStatuses[0].ConfigPath));
            Assert.That(skillStatuses[3].InstalledSkillNames, Does.Contain("upilot-unity-mcp"));
            Assert.That(skillStatuses[3].UpilotSkillCount, Is.EqualTo(1));
            Assert.That(skillStatuses[3].ApplicabilityExplanation, Does.Contain("OpenCode"));
        }

        [Test]
        public void AgentSkillInstallPathsUseClaudeNativeDirectoryAndCursorSharedDirectory()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "upilot-skill-paths"));
            var codex = UPilotAgentSetup.GetAgentSkillInstallPath(projectRoot, "Codex");
            var claude = UPilotAgentSetup.GetAgentSkillInstallPath(projectRoot, "Claude Code");
            var cursor = UPilotAgentSetup.GetAgentSkillInstallPath(projectRoot, "Cursor");
            var openCode = UPilotAgentSetup.GetAgentSkillInstallPath(projectRoot, "OpenCode");

            Assert.That(codex, Does.Contain(Path.Combine(".agents", "skills", "upilot-unity-mcp")));
            Assert.That(claude, Does.Contain(Path.Combine(".claude", "skills", "upilot-unity-mcp")));
            Assert.That(cursor, Is.EqualTo(codex));
            Assert.That(openCode, Is.EqualTo(codex));
            Assert.That(
                UPilotAgentSetup.GetAgentSkillDiscoveryRoots(projectRoot, "Cursor"),
                Does.Contain(Path.Combine(projectRoot, ".cursor", "skills")));
            Assert.That(
                UPilotAgentSetup.GetAgentSkillDiscoveryRoots(projectRoot, "OpenCode"),
                Does.Contain(Path.Combine(projectRoot, ".opencode", "skills")));
            Assert.That(
                UPilotAgentSetup.GetAgentSkillDiscoveryRoots(projectRoot, "OpenCode"),
                Does.Contain(Path.Combine(projectRoot, ".claude", "skills")));
        }

        [TestCase("Codex", true)]
        [TestCase("Codex", false)]
        [TestCase("Claude Code", true)]
        [TestCase("Claude Code", false)]
        [TestCase("Cursor", true)]
        [TestCase("Cursor", false)]
        [TestCase("OpenCode", true)]
        [TestCase("OpenCode", false)]
        public void McpConfigWritesSilentlyPreserveOtherSettings(string client, bool legacyPrompt)
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-silent-mcp-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "config");
            var isToml = client == "Codex";
            var isOpenCode = client == "OpenCode";
            var method = typeof(UPilotAgentSetup).GetMethod(
                isToml ? "WriteTomlMcpConfig" : isOpenCode ? "WriteOpenCodeJsonMcpConfig" : "WriteJsonMcpConfig",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var args = isToml || isOpenCode
                ? new object[] { path, legacyPrompt }
                : new object[] { path, client == "Claude Code", legacyPrompt };
            try
            {
                // First install must also work without requiring a pre-existing directory/file.
                Assert.That((string)method.Invoke(null, args), Does.StartWith("Wrote "));
                Assert.That(File.ReadAllText(path), Does.Contain(UPilotAgentSetup.McpUrl));

                const string otherUrl = "https://example.com/other-mcp";
                var original = isToml
                    ? "model = \"keep-model\"\n\n[mcp_servers.upilot]\nurl = \"http://127.0.0.1:9999/mcp\"\n\n" +
                      "[mcp_servers.other]\nurl = \"" + otherUrl + "\"\n"
                    : "{\n  \"model\": \"keep-model\",\n  \"" + (isOpenCode ? "mcp" : "mcpServers") +
                      "\": {\n    \"upilot\": { \"url\": \"http://127.0.0.1:9999/mcp\" },\n" +
                      "    \"other\": { \"url\": \"" + otherUrl + "\" }\n  }\n}\n";
                File.WriteAllText(path, original);

                // In particular, the legacy true argument must not open a modal dialog.
                Assert.That((string)method.Invoke(null, args), Does.StartWith("Updated UPilot"));
                var updated = File.ReadAllText(path);
                Assert.That(updated, Does.Contain("keep-model"));
                Assert.That(updated, Does.Contain(otherUrl));
                Assert.That(updated, Does.Contain(UPilotAgentSetup.McpUrl));
                Assert.That(updated, Does.Not.Contain("127.0.0.1:9999"));
                method.Invoke(null, args);
                Assert.That(File.ReadAllText(path), Is.EqualTo(updated), "Repeated updates must be content-idempotent.");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void SilentOpenCodeConfigWriteRejectsInvalidShapeWithoutChangingFile()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-silent-mcp-invalid-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "opencode.jsonc");
            const string original = "{ \"model\": \"keep-model\", \"mcp\": [] }\n";
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(path, original);
                var before = File.ReadAllBytes(path);
                var method = typeof(UPilotAgentSetup).GetMethod(
                    "WriteOpenCodeJsonMcpConfig", BindingFlags.NonPublic | BindingFlags.Static);
                var exception = Assert.Throws<TargetInvocationException>(() =>
                    method.Invoke(null, new object[] { path, true }));
                Assert.That(exception.InnerException, Is.TypeOf<InvalidDataException>());
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void OpenCodeJsonUpsertPreservesJsoncCommentsAndOtherSettings()
        {
            var original = "{\n" +
                           "  // keep this comment\n" +
                           "  \"model\": \"anthropic/test\",\n" +
                           "  \"mcp\": {\n" +
                           "    \"other\": { \"type\": \"remote\", \"url\": \"https://example.com/mcp\" },\n" +
                           "    \"upilot\": { \"type\": \"local\", \"url\": \"http://127.0.0.1:9999/mcp\", \"enabled\": false }\n" +
                           "  }\n" +
                           "}\n";
            var method = typeof(UPilotAgentSetup).GetMethod(
                "TryUpsertOpenCodeMcpServer",
                BindingFlags.NonPublic | BindingFlags.Static);
            var args = new object[] { original, null, null };

            var ok = (bool)method.Invoke(null, args);
            var updated = (string)args[1];

            Assert.That(ok, Is.True, args[2] as string);
            Assert.That(updated, Does.Contain("// keep this comment"));
            Assert.That(updated, Does.Contain("\"model\": \"anthropic/test\""));
            Assert.That(updated, Does.Contain("https://example.com/mcp"));
            Assert.That(updated, Does.Contain("\"type\": \"remote\""));
            Assert.That(updated, Does.Contain(UPilotAgentSetup.McpUrl));
            Assert.That(updated, Does.Contain("\"enabled\": true"));
            Assert.That(updated, Does.Contain("\"timeout\": 30000"));
            Assert.That(updated, Does.Not.Contain("127.0.0.1:9999"));
        }

        [Test]
        public void OpenCodeJsonUpsertRejectsInvalidMcpShapeWithoutReplacingFile()
        {
            const string original = "{ \"model\": \"test\", \"mcp\": [] }\n";
            var method = typeof(UPilotAgentSetup).GetMethod(
                "TryUpsertOpenCodeMcpServer",
                BindingFlags.NonPublic | BindingFlags.Static);
            var args = new object[] { original, null, null };

            var ok = (bool)method.Invoke(null, args);

            Assert.That(ok, Is.False);
            Assert.That(args[1], Is.EqualTo(original));
            Assert.That(args[2] as string, Does.Contain("mcp 必须是 JSON 对象"));
        }

        [Test]
        public void OpenCodeJsonUpsertAddsMcpWithoutRemovingJsoncContent()
        {
            var original = "{\n" +
                           "  // retained project setting\n" +
                           "  \"model\": \"anthropic/test\"\n" +
                           "}\n";
            var method = typeof(UPilotAgentSetup).GetMethod(
                "TryUpsertOpenCodeMcpServer",
                BindingFlags.NonPublic | BindingFlags.Static);
            var args = new object[] { original, null, null };

            var ok = (bool)method.Invoke(null, args);
            var updated = (string)args[1];

            Assert.That(ok, Is.True, args[2] as string);
            Assert.That(updated, Does.Contain("// retained project setting"));
            Assert.That(updated, Does.Contain("\"model\": \"anthropic/test\""));
            Assert.That(updated, Does.Contain("\"mcp\""));
            Assert.That(updated, Does.Contain("\"upilot\""));
            Assert.That(updated, Does.Contain(UPilotAgentSetup.McpUrl));
            Assert.That(updated, Does.Contain("\"timeout\": 30000"));
        }

        [Test]
        public void OpenCodeInspectionReportsDisabledLocalAndLowTimeoutConfiguration()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-opencode-config-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "opencode.jsonc");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(
                    path,
                    "{\n" +
                    "  // valid JSONC comment\n" +
                    "  \"mcp\": {\n" +
                    "    \"upilot\": {\n" +
                    "      \"type\": \"local\",\n" +
                    "      \"url\": \"" + UPilotAgentSetup.McpUrl + "\",\n" +
                    "      \"enabled\": false,\n" +
                    "      \"timeout\": 1000\n" +
                    "    }\n" +
                    "  }\n" +
                    "}\n");
                var inspect = typeof(UPilotAgentSetup).GetMethod(
                    "InspectOpenCodeConfig",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var status = (AgentMcpConfigStatus)inspect.Invoke(null, new object[] { "OpenCode", path });

                Assert.That(status.HasUPilotEntry, Is.True);
                Assert.That(status.UsesCurrentUrl, Is.True);
                Assert.That(status.IsConfigured, Is.False);
                Assert.That(status.ConfigurationIssue, Does.Contain("remote"));
                Assert.That(status.ConfigurationIssue, Does.Contain("已禁用"));
                Assert.That(status.ConfigurationIssue, Does.Contain("30000"));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void OpenCodeConfigPathKeepsExistingJsoncWhenJsonIsAbsent()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-opencode-path-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var jsoncPath = Path.Combine(directory, "opencode.jsonc");
            File.WriteAllText(jsoncPath, "{}\n");
            try
            {
                var resolve = typeof(UPilotAgentSetup).GetMethod(
                    "ResolveOpenCodeConfigPath",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var resolved = (string)resolve.Invoke(null, new object[] { directory });

                Assert.That(resolved, Is.EqualTo(jsoncPath));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void OpenCodeSkillStatusDetectsDifferentSameNameCopies()
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "upilot-opencode-skill-conflict-" + Guid.NewGuid().ToString("N"));
            var agentsSkill = Path.Combine(projectRoot, ".agents", "skills", "upilot-unity-mcp");
            var claudeSkill = Path.Combine(projectRoot, ".claude", "skills", "upilot-unity-mcp");
            Directory.CreateDirectory(agentsSkill);
            Directory.CreateDirectory(claudeSkill);
            try
            {
                File.WriteAllText(Path.Combine(agentsSkill, "SKILL.md"), "---\nname: upilot-unity-mcp\ndescription: agents\n---\n");
                File.WriteAllText(Path.Combine(claudeSkill, "SKILL.md"), "---\nname: upilot-unity-mcp\ndescription: claude changed\n---\n");
                var writeMetadata = typeof(UPilotAgentSetup).GetMethod(
                    "WriteSkillInstallMetadata",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
                writeMetadata.Invoke(null, new object[] { agentsSkill });
                var inspect = typeof(UPilotAgentSetup).GetMethod(
                    "InspectSkillConfig",
                    BindingFlags.NonPublic | BindingFlags.Static);

                var status = (AgentSkillConfigStatus)inspect.Invoke(null, new object[] { projectRoot, "OpenCode" });

                Assert.That(status.State, Is.EqualTo(AgentSkillConfigState.Conflict));
                Assert.That(status.UpilotSkillCount, Is.EqualTo(1));
                Assert.That(status.DuplicateSkillPaths.Length, Is.EqualTo(2));
                Assert.That(status.SkillConflictSummary, Does.Contain("SHA256 不一致"));
                Assert.That(status.IsSatisfied, Is.False);
            }
            finally
            {
                if (Directory.Exists(projectRoot))
                    Directory.Delete(projectRoot, recursive: true);
            }
        }

        [Test]
        public void MainStateDistinguishesRestartingAndStopping()
        {
            Assert.That(Enum.IsDefined(typeof(UPilotMainState), UPilotMainState.CheckingStatus), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotMainState), UPilotMainState.Restarting), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotMainState), UPilotMainState.Stopping), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotMainState), UPilotMainState.Updating), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotServiceOperation), UPilotServiceOperation.Restarting), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotServiceOperation), UPilotServiceOperation.Stopping), Is.True);
        }

        [Test]
        public void MainStateKeepsReadyWhenPidIsTemporarilyMissing()
        {
            var bridge = new BridgeStatus
            {
                IsStarted = true,
                IsWsOpen = true,
                IsAuthenticated = true,
            };
            var mcp = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                ProcessId = null,
                ProcessOwnership = McpProcessOwnership.Unknown,
                DiagnosisPending = true,
            };

            var snapshot = UPilotQuickStart.EvaluateServiceState(bridge, mcp);

            Assert.That(snapshot.State, Is.EqualTo(UPilotMainState.Ready));
        }

        [Test]
        public void MainStateShowsWaitingEditorWithoutClaimingRestart()
        {
            var bridge = new BridgeStatus
            {
                IsStarted = true,
                IsWsOpen = true,
                IsAuthenticated = true,
            };
            var mcp = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                EditorObservationAvailable = true,
                EditorObservationStatus = "waiting_editor",
            };

            var snapshot = UPilotQuickStart.EvaluateServiceState(bridge, mcp);

            Assert.That(snapshot.State, Is.EqualTo(UPilotMainState.CheckingStatus));
            Assert.That(snapshot.Title, Does.Contain("主线程"));
            Assert.That(snapshot.Message, Does.Contain("每秒检查"));
            Assert.That(snapshot.Message, Does.Contain("未执行自动重启"));
        }

        [Test]
        public void MainStateShowsUnknownEditorEvidenceAsUnconfirmed()
        {
            var bridge = new BridgeStatus
            {
                IsStarted = true,
                IsWsOpen = true,
                IsAuthenticated = true,
            };
            var mcp = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                EditorObservationAvailable = true,
                EditorObservationStatus = "unknown",
            };

            var snapshot = UPilotQuickStart.EvaluateServiceState(bridge, mcp);

            Assert.That(snapshot.State, Is.EqualTo(UPilotMainState.CheckingStatus));
            Assert.That(snapshot.Title, Does.Contain("暂无法确认"));
            Assert.That(snapshot.Message, Does.Contain("未执行自动重启"));
        }

        [TestCase(false, null)]
        [TestCase(true, "responsive")]
        public void MainStateKeepsLegacyOrResponsiveHealthReady(bool observationAvailable, string observationStatus)
        {
            var bridge = new BridgeStatus
            {
                IsStarted = true,
                IsWsOpen = true,
                IsAuthenticated = true,
            };
            var mcp = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                EditorObservationAvailable = observationAvailable,
                EditorObservationStatus = observationStatus,
            };

            Assert.That(UPilotQuickStart.EvaluateServiceState(bridge, mcp).State,
                Is.EqualTo(UPilotMainState.Ready));
        }

        [Test]
        public void MainStateDoesNotLetEditorWaitingHideAuthenticationOrIdentityErrors()
        {
            var waiting = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot,
                EditorObservationAvailable = true,
                EditorObservationStatus = "waiting_editor",
                ErrorMessage = "health identity mismatch",
            };
            var authenticatedBridge = new BridgeStatus
            {
                IsStarted = true,
                IsWsOpen = true,
                IsAuthenticated = true,
            };

            Assert.That(UPilotQuickStart.EvaluateServiceState(authenticatedBridge, waiting).State,
                Is.EqualTo(UPilotMainState.NeedsRepair));

            waiting.ErrorMessage = "";
            waiting.ProcessOwnership = McpProcessOwnership.Foreign;
            Assert.That(UPilotQuickStart.EvaluateServiceState(authenticatedBridge, waiting).State,
                Is.EqualTo(UPilotMainState.NeedsRepair));

            waiting.ProcessOwnership = McpProcessOwnership.CurrentUPilot;
            var rejectedBridge = authenticatedBridge;
            rejectedBridge.AuthenticationError = "token rejected";
            Assert.That(UPilotQuickStart.EvaluateServiceState(rejectedBridge, waiting).State,
                Is.EqualTo(UPilotMainState.NeedsRepair));
        }

        [Test]
        public void MainStateTreatsUnconfirmedOwnershipAsChecking()
        {
            var bridge = new BridgeStatus { IsStarted = true };
            var mcp = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                ProcessOwnership = McpProcessOwnership.Unknown,
                DiagnosisPending = true,
            };

            var snapshot = UPilotQuickStart.EvaluateServiceState(bridge, mcp);

            Assert.That(snapshot.State, Is.EqualTo(UPilotMainState.CheckingStatus));
            Assert.That(snapshot.Message, Does.Contain("确认 MCP 服务身份"));
        }

        [Test]
        public void MainStateRequiresConfirmedForeignOwnershipForPortRepair()
        {
            var bridge = new BridgeStatus { IsStarted = true };
            var mcp = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                ProcessOwnership = McpProcessOwnership.Foreign,
                DiagnosisPending = false,
            };

            var snapshot = UPilotQuickStart.EvaluateServiceState(bridge, mcp);

            Assert.That(snapshot.State, Is.EqualTo(UPilotMainState.NeedsRepair));
            Assert.That(snapshot.Message, Does.Contain("已确认"));
        }

        [Test]
        public void RepairRoutingUsesControlledServerRestartForEveryUnhealthyState()
        {
            var disconnectedBridge = new BridgeStatus { IsStarted = true };
            var healthyCurrent = new McpServerStatus
            {
                IsRunning = true,
                HttpPortListening = true,
                WsPortListening = true,
                ProcessOwnership = McpProcessOwnership.CurrentUPilot,
            };
            var unknown = healthyCurrent;
            unknown.ProcessOwnership = McpProcessOwnership.Unknown;
            var foreign = healthyCurrent;
            foreign.ProcessOwnership = McpProcessOwnership.Foreign;
            var partial = healthyCurrent;
            partial.WsPortListening = false;

            Assert.That(
                UPilotQuickStart.DetermineRepairAction(disconnectedBridge, unknown),
                Is.EqualTo(UPilotRepairAction.RestartServer));
            Assert.That(
                UPilotQuickStart.DetermineRepairAction(disconnectedBridge, foreign),
                Is.EqualTo(UPilotRepairAction.RestartServer));
            Assert.That(
                UPilotQuickStart.DetermineRepairAction(disconnectedBridge, partial),
                Is.EqualTo(UPilotRepairAction.RestartServer));
            Assert.That(
                UPilotQuickStart.DetermineRepairAction(disconnectedBridge, healthyCurrent),
                Is.EqualTo(UPilotRepairAction.RestartServer));
            Assert.That(
                UPilotQuickStart.DetermineRepairAction(default, default),
                Is.EqualTo(UPilotRepairAction.RestartServer));
        }

        [Test]
        public void MainWindowUsesNeutralLabelWhileFetchingStatus()
        {
            var method = typeof(UPilotMainWindow).GetMethod(
                "GetStateLabel",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.Invoke(null, new object[] { UPilotMainState.CheckingStatus }),
                Is.EqualTo("获取状态中"));
            Assert.That(
                method.Invoke(null, new object[] { UPilotMainState.Starting }),
                Is.EqualTo("启动中"));
        }

        [Test]
        public void MainWindowHidesConfiguredRuntimeModeDuringTransientStates()
        {
            var method = typeof(UPilotMainWindow).GetMethod(
                "GetRuntimeModeLabel",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.Invoke(null, new object[] { UPilotMainState.Updating }),
                Is.EqualTo("正在更新"));
            Assert.That(
                method.Invoke(null, new object[] { UPilotMainState.CheckingStatus }),
                Is.EqualTo("获取状态中"));
        }

        [Test]
        public void UpdateOperationStatusTreatsOnlyActivePhasesAsRunning()
        {
            var running = new UPilotUpdateOperationStatus(
                UPilotUpdateOperationPhase.DownloadingService,
                "正在更新服务",
                "正在下载安装",
                "0.3.7",
                "0.3.7");
            var completed = new UPilotUpdateOperationStatus(
                UPilotUpdateOperationPhase.Completed,
                "更新完成",
                "已完成",
                "0.3.7",
                "0.3.7");
            var failed = new UPilotUpdateOperationStatus(
                UPilotUpdateOperationPhase.Failed,
                "更新失败",
                "失败",
                "0.3.7",
                "0.3.7");

            Assert.That(running.IsRunning, Is.True);
            Assert.That(completed.IsRunning, Is.False);
            Assert.That(failed.IsRunning, Is.False);
        }

        [Test]
        public void SourcePackageClassificationKeepsReleaseTargetsManaged()
        {
            Assert.That(
                UPilotServerRuntimeService.IsSourcePackage(PackageSource.Local, "io.github.codingriver.upilot@file:D:/upilot"),
                Is.True);
            Assert.That(
                UPilotServerRuntimeService.IsSourcePackage(PackageSource.Embedded, "io.github.codingriver.upilot@file:Packages/upilot"),
                Is.True);
            Assert.That(
                UPilotServerRuntimeService.IsSourcePackage(PackageSource.Git, "io.github.codingriver.upilot@https://github.com/codingriver/upilot.git#main"),
                Is.True);
            Assert.That(
                UPilotPackageUpdateLifecycle.ShouldManagePackageUpdate(
                    PackageSource.Git,
                    "io.github.codingriver.upilot@https://github.com/codingriver/upilot.git#v0.3.23"),
                Is.True);
            Assert.That(
                UPilotPackageUpdateLifecycle.ShouldManagePackageUpdate(
                    PackageSource.Local,
                    "io.github.codingriver.upilot@file:D:/upilot"),
                Is.False);
        }

        [Test]
        public void SourceChannelDoesNotBlockServiceStartFromReleaseOperationState()
        {
            if (!UPilotServerRuntimeService.IsSourceUpdateChannel())
                Assert.Ignore("This assertion requires a source/local package installation.");

            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.WaitingForReload,
                "stale source update state",
                "0.3.23",
                "0.3.23");

            try
            {
                Assert.That(UPilotUpdateService.Instance.GetOperationStatus().BlocksServiceStart, Is.True);
                Assert.That(UPilotUpdateService.Instance.IsServiceStartBlocked, Is.False);
                Assert.That(UPilotUpdateService.Instance.IsUpdateRunning, Is.False);
            }
            finally
            {
                UPilotUpdateService.ResetSourceChannelState();
            }
        }

        [Test]
        public void SourceChannelResetClearsOperationLifecycleAndReloadGuardState()
        {
            if (!UPilotServerRuntimeService.IsSourceUpdateChannel())
                Assert.Ignore("This assertion requires a source/local package installation.");

            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.DownloadingService,
                "stale source download state",
                "0.3.23",
                "0.3.23");
            SessionState.SetBool(
                GetPackageLifecycleSessionKey("CodingRiver.UPilot.PackageUpdate.InProgress"),
                true);

            try
            {
                Assert.That(UPilotUpdateService.GetReloadGuardStatus().IsActive, Is.True);

                UPilotUpdateService.ResetSourceChannelState();

                Assert.That(
                    UPilotUpdateService.Instance.GetOperationStatus().Phase,
                    Is.EqualTo(UPilotUpdateOperationPhase.None));
                Assert.That(UPilotUpdateService.GetReloadGuardStatus().IsActive, Is.False);
                Assert.That(
                    SessionState.GetBool(
                        GetPackageLifecycleSessionKey("CodingRiver.UPilot.PackageUpdate.InProgress"),
                        false),
                    Is.False);
            }
            finally
            {
                UPilotUpdateService.ResetSourceChannelState();
            }
        }

        [Test]
        public void UpdateWindowRefreshesWhenRestartTransitionsToCompleted()
        {
            Assert.That(
                UPilotUpdateWindow.ShouldRefreshForOperationStatus(
                    UPilotUpdateOperationPhase.RestartingService,
                    UPilotUpdateOperationPhase.Completed,
                    operationWasRunning: false,
                    operationRunning: false,
                    statusIsRunning: false,
                    downloadRunning: false),
                Is.True);
            Assert.That(
                UPilotUpdateWindow.ShouldRefreshForOperationStatus(
                    UPilotUpdateOperationPhase.Completed,
                    UPilotUpdateOperationPhase.Completed,
                    operationWasRunning: false,
                    operationRunning: false,
                    statusIsRunning: false,
                    downloadRunning: false),
                Is.False);
        }

        [Test]
        public void ReopeningUpdateCenterClearsCompletedStatusBeforeFreshCheck()
        {
            UPilotUpdateService.SetOperationCompleted("更新成功");

            try
            {
                Assert.That(UPilotUpdateWindow.ClearCompletedOperationStatusForFreshCheck(), Is.True);
                Assert.That(
                    UPilotUpdateService.Instance.GetOperationStatus().Phase,
                    Is.EqualTo(UPilotUpdateOperationPhase.None));
            }
            finally
            {
                UPilotUpdateService.ClearOperationStatus();
            }
        }

        [Test]
        public void UpdateOperationStatusUsesWaitingCopyDuringPackageReload()
        {
            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.WaitingForReload,
                "UPilot 包已更新，等待 Unity 重载后继续更新 MCP 服务…",
                "0.3.9",
                "0.3.9");

            try
            {
                var status = UPilotUpdateService.Instance.GetOperationStatus();

                Assert.That(status.IsRunning, Is.True);
                Assert.That(status.Label, Is.EqualTo("等待更新完成"));
                Assert.That(status.Message, Does.Contain("等待 Unity 重载后继续更新 MCP 服务"));
                Assert.That(status.Label, Does.Not.Contain("启动"));
                Assert.That(status.Message, Does.Not.Contain("启动中"));
            }
            finally
            {
                UPilotUpdateService.ClearOperationStatus();
            }
        }

        [Test]
        public void UpdateOperationStatusFailsStaleMemoryBoundPhaseAfterDomainReload()
        {
            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.DownloadingService,
                "正在下载并验证 MCP 服务…",
                "0.3.18",
                "0.3.18");

            try
            {
                SessionState.SetString(GetUpdateServiceProjectKey("CodingRiver.UPilot.UpdateService.RuntimeId"), "previous-runtime");
                LogAssert.Expect(LogType.Error, new Regex("更新任务在.*脚本重载中断", RegexOptions.Singleline));

                var status = UPilotUpdateService.Instance.GetOperationStatus();

                Assert.That(status.Phase, Is.EqualTo(UPilotUpdateOperationPhase.Failed));
                Assert.That(status.IsRunning, Is.False);
                Assert.That(status.Message, Does.Contain("脚本重载中断"));
                Assert.That(
                    UPilotUpdateService.ShouldShowRepairUpdateStateButton(
                        status,
                        UPilotUpdateService.GetReloadGuardStatus()),
                    Is.True);
            }
            finally
            {
                UPilotUpdateService.ClearOperationStatus();
            }
        }

        [Test]
        public void UpdateReloadGuardIsVisibleAndReleasedOnCompletion()
        {
            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.DownloadingService,
                "正在下载并验证 MCP 服务…",
                "0.3.18",
                "0.3.18");

            try
            {
                var activeGuard = UPilotUpdateService.GetReloadGuardStatus();
                Assert.That(activeGuard.IsActive, Is.True);
                Assert.That(activeGuard.IsCurrentRuntime, Is.True);
                Assert.That(
                    UPilotUpdateService.GetReloadGuardNotice(activeGuard, compact: false),
                    Does.Contain("自动刷新/脚本重载已暂时拦截"));

                UPilotUpdateService.SetOperationCompleted("更新完成");

                var clearedGuard = UPilotUpdateService.GetReloadGuardStatus();
                Assert.That(clearedGuard.IsActive, Is.False);
                Assert.That(UPilotUpdateService.GetReloadGuardNotice(clearedGuard, compact: false), Is.Empty);
            }
            finally
            {
                UPilotUpdateService.ClearOperationStatus();
            }
        }

        [Test]
        public void UpdateReloadGuardRecoveryReleasesPreviousRuntimeGuard()
        {
            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.DownloadingService,
                "正在下载并验证 MCP 服务…",
                "0.3.18",
                "0.3.18");

            try
            {
                SessionState.SetString(GetUpdateServiceProjectKey("CodingRiver.UPilot.UpdateService.RuntimeId"), "previous-runtime");
                SessionState.SetString(GetUpdateServiceProjectKey("CodingRiver.UPilot.UpdateService.ReloadGuardRuntimeId"), "previous-runtime");
                LogAssert.Expect(LogType.Error, new Regex("更新任务在.*脚本重载中断", RegexOptions.Singleline));

                var status = UPilotUpdateService.Instance.GetOperationStatus();
                var guard = UPilotUpdateService.GetReloadGuardStatus();

                Assert.That(status.Phase, Is.EqualTo(UPilotUpdateOperationPhase.Failed));
                Assert.That(status.Message, Does.Contain("脚本重载中断"));
                Assert.That(guard.IsActive, Is.False);
                Assert.That(guard.HasFailure, Is.True);
                Assert.That(UPilotUpdateService.GetReloadGuardNotice(guard, compact: true), Does.Contain("脚本重载中断"));
            }
            finally
            {
                UPilotUpdateService.ClearOperationStatus();
            }
        }

        [Test]
        public void PackageManagerConflictIsDetectedOnlyBeforeLifecycleTakesOver()
        {
            if (UPilotServerRuntimeService.IsSourceUpdateChannel())
                Assert.Ignore("External Package Manager conflict handling applies to managed release channels.");

            var method = typeof(UPilotPackageUpdateLifecycle).GetMethod(
                "ShouldHandleExternalPackageManagerConflict",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            UPilotUpdateService.SetOperationPhase(
                UPilotUpdateOperationPhase.DownloadingService,
                "正在下载并验证 MCP 服务…",
                "0.3.20",
                "0.3.20");

            try
            {
                Assert.That((bool)method.Invoke(null, null), Is.True);

                SessionState.SetBool(
                    GetPackageLifecycleSessionKey("CodingRiver.UPilot.PackageUpdate.InProgress"),
                    true);
                Assert.That((bool)method.Invoke(null, null), Is.False);
            }
            finally
            {
                UPilotPackageUpdateLifecycle.ResetUpdateStateForRepair(clearPendingPackageRetry: true);
                UPilotUpdateService.ClearOperationStatus();
            }
        }

        [Test]
        public void UpdateVersionComparisonHandlesReleaseAndMainBuilds()
        {
            Assert.That(UPilotServerRuntimeService.IsVersionNewer("0.3.2", "0.3.1"), Is.True);
            Assert.That(UPilotServerRuntimeService.IsVersionNewer("0.3.2-main.12+abc", "0.3.2-main.7+def"), Is.True);
            Assert.That(UPilotServerRuntimeService.IsVersionNewer("0.3.2-main.7+abc", "0.3.2-main.12+def"), Is.False);
            Assert.That(UPilotServerRuntimeService.IsVersionAtLeast("0.3.2", "0.3.2-main.12+abc"), Is.True);
            Assert.That(UPilotServerRuntimeService.IsVersionAtLeast("0.3.2-main.12+abc", "0.3.2"), Is.False);
        }

        [Test]
        public void McpProcessMatchingRequiresBothCurrentProjectPorts()
        {
            var logArgument = " --log-file \"" +
                              Path.Combine(UPilotProjectConfig.ProjectRoot, "log", "mcp-server.log") + "\"";
            var method = typeof(UPilotMcpServerManager).GetMethod(
                "IsCurrentProjectMcpCommandLine",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.Invoke(null, new object[]
                {
                    "upilot-mcp-server.exe --transport http --http-port 8012 --port 8766" + logArgument,
                    8012,
                    8766,
                }),
                Is.True);
            Assert.That(
                method.Invoke(null, new object[]
                {
                    "python run_upilot_mcp.py --transport http --http-port 8011 --port 8769",
                    8012,
                    8766,
                }),
                Is.False);
            Assert.That(
                method.Invoke(null, new object[]
                {
                    "upilot-mcp-server.exe --transport http --http-port=8012 --port=8766" + logArgument,
                    8012,
                    8766,
                }),
                Is.True);
            Assert.That(method.Invoke(null, new object[]
            {
                "upilot-mcp-server.exe --http-port 8012 --port 8766 --log-file \"" +
                Path.Combine(Path.GetTempPath(), "another-project", "log", "mcp-server.log") + "\"", 8012, 8766
            }), Is.False, "Another project's server must not be stopped or reused just because ports match.");
            Assert.That(method.Invoke(null, new object[]
            {
                "upilot-mcp-server.exe --http-port 8012 --port 8766", 8012, 8766
            }), Is.False, "Unknown project identity is not ownership.");
            var backgroundProbe = Task.Run(() => method.Invoke(null, new object[]
            {
                "upilot-mcp-server.exe --http-port 8012 --port 8766" + logArgument, 8012, 8766
            }));
            Assert.That(backgroundProbe.Wait(2000), Is.True);
            Assert.That(backgroundProbe.Result, Is.True, "Status ownership probes also run on worker threads.");
        }

        [Test]
        public void ManifestFileDependencyResolvesRelativeToManifestDirectory()
        {
            var method = typeof(UPilotMcpServerManager).GetMethod(
                "TryResolveManifestFileDependencyRoot",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);

            var projectRoot = Path.Combine(
                Path.GetTempPath(),
                "upilot-test-root",
                "repo",
                "Tests~",
                "UPilotTest");
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            var args = new object[] { "file:../../..", manifestPath, null, false };
            var resolved = (bool)method.Invoke(null, args);
            var expectedRoot = Path.GetFullPath(Path.Combine(projectRoot, "Packages", "../../.."));

            Assert.That(resolved, Is.True);
            Assert.That(args[2], Is.EqualTo(expectedRoot));
            Assert.That(args[3], Is.False);
        }

        [Test]
        public void FirstSetupEntryIsHostedByMainWindow()
        {
            Assert.That(typeof(EditorWindow).IsAssignableFrom(typeof(UPilotFirstSetupWindow)), Is.False);
            Assert.That(
                typeof(UPilotMainWindow).GetMethod("OpenSetup", BindingFlags.Public | BindingFlags.Static),
                Is.Not.Null);
        }

        [Test]
        public void FirstSetupApprovesProjectWritesByDefault()
        {
            Assert.That(UPilotMainWindow.SetupApprovesProjectWritesByDefault, Is.True);
        }

        [Test]
        public void FirstSetupWriteApprovalDoesNotRequireSecondaryConfirmation()
        {
            Assert.That(
                typeof(UPilotMainWindow).GetMethod(
                    "ConfirmSetupProjectWriteAccess",
                    BindingFlags.NonPublic | BindingFlags.Static),
                Is.Null);
        }

        [Test]
        public void ManagedServerDownloadSelectsMatchingPlatformAndArchitecture()
        {
            var manifest = new UPilotReleaseManifest();
            var windows = new UPilotServerDownloadInfo
            {
                Platform = "windows",
                Architecture = "x64",
                Url = "https://example.test/windows",
            };
            var mac = new UPilotServerDownloadInfo
            {
                Platform = "macos",
                Architecture = "arm64",
                Url = "https://example.test/macos",
            };
            manifest.Downloads.Add(windows);
            manifest.Downloads.Add(mac);

            var method = typeof(UPilotServerRuntimeService).GetMethod(
                "PickDownloadForPlatform",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { manifest, "windows", "x64" }), Is.SameAs(windows));
            Assert.That(method.Invoke(null, new object[] { manifest, "macos", "arm64" }), Is.SameAs(mac));
            Assert.That(method.Invoke(null, new object[] { manifest, "linux", "x64" }), Is.Null);
        }

        [Test]
        public void DownloadStateExposesSegmentProgress()
        {
            var state = new UPilotDownloadState
            {
                SegmentCount = 4,
                CompletedSegments = 2,
                ActiveSegmentRequests = 3,
                PlatformDisplayName = "Windows x64",
            };

            Assert.That(state.SegmentCount, Is.EqualTo(4));
            Assert.That(state.CompletedSegments, Is.EqualTo(2));
            Assert.That(state.ActiveSegmentRequests, Is.EqualTo(3));
            Assert.That(UPilotDownloadHelper.GetCompletedSegmentCount(state), Is.EqualTo(2));
            Assert.That(UPilotDownloadHelper.GetActiveSegmentCount(state), Is.EqualTo(3));
            Assert.That(state.PlatformDisplayName, Is.EqualTo("Windows x64"));
        }

        [Test]
        public void DownloadStateSnapshotPublishesCoherentDefensiveCopies()
        {
            var published = new UPilotDownloadState
            {
                Phase = "old",
                BytesReceived = 10,
                TotalBytes = 100,
                CompletedSegments = 1,
                ActiveSegmentRequests = 2,
            };
            var unpublished = UPilotDownloadHelper.CloneDownloadState(published);
            unpublished.Phase = "new";
            unpublished.BytesReceived = 80;
            unpublished.CompletedSegments = 8;
            unpublished.ActiveSegmentRequests = 5;

            var before = UPilotDownloadHelper.ReadDownloadStateSnapshot(ref published);
            Assert.That(before.Phase, Is.EqualTo("old"));
            Assert.That(before.BytesReceived, Is.EqualTo(10));
            Assert.That(before.CompletedSegments, Is.EqualTo(1));
            Assert.That(before.ActiveSegmentRequests, Is.EqualTo(2));

            Volatile.Write(ref published, unpublished);

            var after = UPilotDownloadHelper.ReadDownloadStateSnapshot(ref published);
            Assert.That(after.Phase, Is.EqualTo("new"));
            Assert.That(after.BytesReceived, Is.EqualTo(80));
            Assert.That(after.CompletedSegments, Is.EqualTo(8));
            Assert.That(after.ActiveSegmentRequests, Is.EqualTo(5));
            Assert.That(UPilotDownloadHelper.GetProgress(after), Is.EqualTo(0.8f));

            after.Phase = "reader mutation";
            after.CompletedSegments = 99;
            after.ActiveSegmentRequests = 99;

            var reread = UPilotDownloadHelper.ReadDownloadStateSnapshot(ref published);
            Assert.That(reread.Phase, Is.EqualTo("new"));
            Assert.That(reread.CompletedSegments, Is.EqualTo(8));
            Assert.That(reread.ActiveSegmentRequests, Is.EqualTo(5));
        }

        [Test]
        public void DownloadProgressHelperHandlesUnknownTotals()
        {
            Assert.That(UPilotDownloadHelper.GetProgress(new UPilotDownloadState()), Is.Zero);
            Assert.That(
                UPilotDownloadHelper.GetProgress(new UPilotDownloadState { IsComplete = true }),
                Is.EqualTo(1f));
            Assert.That(
                UPilotDownloadHelper.GetCompletedSegmentCount(new UPilotDownloadState { CompletedSegments = -1 }),
                Is.Zero);
            Assert.That(
                UPilotDownloadHelper.GetActiveSegmentCount(new UPilotDownloadState { ActiveSegmentRequests = -1 }),
                Is.Zero);
        }

        [Test]
        public void UpdateDownloadProgressLabelShowsSegmentConcurrency()
        {
            var multiThread = new UPilotDownloadState
            {
                Phase = "正在下载安装",
                SegmentCount = 12,
                MaxConcurrentSegmentRequests = 5,
                CompletedSegments = 2,
                ActiveSegmentRequests = 5,
                BytesReceived = 10 * 1024 * 1024,
                TotalBytes = 20 * 1024 * 1024,
            };
            var singleThread = new UPilotDownloadState
            {
                Phase = "正在下载安装",
                SegmentCount = 1,
                ActiveSegmentRequests = 1,
                BytesReceived = 512 * 1024,
                TotalBytes = 1024 * 1024,
            };

            Assert.That(
                UPilotUpdateService.FormatDownloadProgressLabel(multiThread),
                Is.EqualTo("正在下载安装（12 分片，正在下载 5，最多 5 并发，已完成 2/12）"));
            Assert.That(
                UPilotUpdateService.FormatDownloadProgressDetail(multiThread),
                Is.EqualTo("10.0 MB / 20.0 MB · 12 分片下载 · 正在下载 5 · 最多 5 并发 · 已完成 2/12"));
            Assert.That(
                UPilotUpdateService.FormatDownloadProgressLabel(singleThread),
                Is.EqualTo("正在下载安装（单流下载）"));
            Assert.That(
                UPilotUpdateService.FormatDownloadProgressDetail(singleThread),
                Does.Contain("单流下载"));

            var verifying = new UPilotDownloadState
            {
                Phase = "正在验证文件",
                SegmentCount = 4,
                CompletedSegments = 4,
            };
            Assert.That(
                UPilotUpdateService.FormatDownloadProgressLabel(verifying),
                Is.EqualTo("正在验证文件"));
            Assert.That(UPilotUpdateService.FormatDownloadProgressDetail(verifying), Is.Empty);
        }

        [Test]
        public void ManagedServerPackageUpdateSupportsPreparedServerContinuation()
        {
            Assert.That(typeof(UPilotPreparedServerDownload).GetField("TargetPath"), Is.Not.Null);
            Assert.That(typeof(UPilotPreparedServerDownload).GetField("Version"), Is.Not.Null);
            Assert.That(
                typeof(UPilotServerRuntimeService).GetMethod(
                    "PrepareLatestServerExeAsync",
                    new[] { typeof(UPilotReleaseManifest), typeof(CancellationToken) }),
                Is.Not.Null);
            Assert.That(
                typeof(UPilotServerRuntimeService).GetMethod(
                    "ActivatePreparedStandaloneExe",
                    new[] { typeof(string), typeof(string), typeof(string).MakeByRefType() }),
                Is.Not.Null);
            Assert.That(
                typeof(UPilotServerRuntimeService).GetMethod(
                    "GetConfiguredStandaloneRuntime",
                    new[] { typeof(string).MakeByRefType(), typeof(string).MakeByRefType() }),
                Is.Not.Null);
            Assert.That(
                typeof(UPilotUpdateService).GetMethod(
                    "ActivatePreparedManagedServerAfterPackageUpdateAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                typeof(UPilotMcpServerManager).GetMethod(
                    "RestartServer",
                    new[] { typeof(Action) }),
                Is.Not.Null);

            var prepareMethod = typeof(UPilotPackageUpdateLifecycle).GetMethod(
                "PrepareForPackageUpdate",
                BindingFlags.Public | BindingFlags.Static);
            Assert.That(prepareMethod, Is.Not.Null);
            Assert.That(
                prepareMethod.GetParameters().Any(parameter => parameter.Name == "preparedServerPath"),
                Is.True);
        }

        [Test]
        public void PythonEnvironmentPathsShareByReleaseOrLocalSource()
        {
            var root = Path.Combine(Path.GetTempPath(), "upilot-python-path-tests");
            string Resolve(PackageSource source, bool release, string version, string package, string legacy)
                => UPilotServerRuntimeService.ResolvePythonEnvironmentPath(
                    source, release, version, package, root, legacy, out _);

            var releaseA = Resolve(PackageSource.Git, true, "0.3.31", "package-a", "legacy-a");
            var releaseB = Resolve(PackageSource.Git, true, "0.3.31", "package-b", "legacy-b");
            Assert.That(releaseA, Is.EqualTo(Path.Combine(root, ".upilot", "python-envs", "0.3.31", "venv")));
            Assert.That(releaseB, Is.EqualTo(releaseA));
            Assert.That(Resolve(PackageSource.Git, true, "0.3.32", "package-a", "legacy-a"), Is.Not.EqualTo(releaseA));
            var local = Path.Combine(root, "source");
            Assert.That(Resolve(PackageSource.Local, false, "main", local, "legacy-a"),
                Is.EqualTo(Path.Combine(local, "upilotserver~", ".venv")));
            Assert.That(Resolve(PackageSource.Embedded, false, "main", local, "legacy-b"),
                Is.EqualTo(Resolve(PackageSource.Local, false, "main", local, "legacy-a")));
            Assert.That(Resolve(PackageSource.Local, false, "main", local + "-other", "legacy-a"),
                Is.Not.EqualTo(Resolve(PackageSource.Local, false, "main", local, "legacy-a")));
            Assert.That(Resolve(PackageSource.Git, false, "main", local, "legacy-a"), Is.EqualTo("legacy-a"));
            Assert.Throws<InvalidOperationException>(() =>
                UPilotServerRuntimeService.ResolvePythonEnvironmentPath(
                    PackageSource.Git, true, "0.3.31", local, "", "legacy", out _));
            Assert.Throws<InvalidOperationException>(() => Resolve(PackageSource.Git, true, "../bad", local, "legacy"));
        }

        [UnityTest]
        public IEnumerator PythonEnvironmentConcurrentPreparationInstallsOnceAndReuses()
        {
            var root = Path.Combine(Path.GetTempPath(), "upilot-python-test-" + Guid.NewGuid().ToString("N"));
            var venv = Path.Combine(root, "venv");
            var installs = 0;
            var releaseInstall = new TaskCompletionSource<bool>();
            Task Install(string interpreter)
            {
                installs++;
                return FinishInstall(interpreter);
            }
            async Task FinishInstall(string interpreter)
            {
                await releaseInstall.Task;
                Directory.CreateDirectory(Path.GetDirectoryName(interpreter));
                File.WriteAllText(interpreter, "test interpreter");
            }
            void Validate(string interpreter) => Assert.That(File.ReadAllText(interpreter), Is.EqualTo("test interpreter"));
            Task<string> first = null;
            Task<string> second = null;
            try
            {
                first = UPilotServerRuntimeService.PreparePythonEnvironmentAsync(venv, Install, Validate, CancellationToken.None);
                second = UPilotServerRuntimeService.PreparePythonEnvironmentAsync(venv, Install, Validate, CancellationToken.None);
                Assert.That(installs, Is.EqualTo(1));
                Assert.That(second.IsCompleted, Is.False);
                releaseInstall.SetResult(true);
                while (!first.IsCompleted || !second.IsCompleted)
                    yield return null;
                Assert.That(second.GetAwaiter().GetResult(), Is.EqualTo(first.GetAwaiter().GetResult()));
                Assert.That(installs, Is.EqualTo(1));
                var reused = UPilotServerRuntimeService.PreparePythonEnvironmentAsync(venv, Install, Validate, CancellationToken.None);
                while (!reused.IsCompleted)
                    yield return null;
                Assert.That(reused.GetAwaiter().GetResult(), Is.EqualTo(first.Result));
                Assert.That(installs, Is.EqualTo(1));
            }
            finally
            {
                releaseInstall.TrySetResult(true);
                if (first?.IsCompleted != false && second?.IsCompleted != false && Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [UnityTest]
        public IEnumerator PythonEnvironmentInvalidExistingFilesArePreserved()
        {
            var root = Path.Combine(Path.GetTempPath(), "upilot-python-test-" + Guid.NewGuid().ToString("N"));
            var venv = Path.Combine(root, "venv");
            var python = Path.Combine(venv, "Scripts", "python.exe");
            var configBefore = JsonUtility.ToJson(UPilotProjectConfig.Current);
            Directory.CreateDirectory(Path.GetDirectoryName(python));
            File.WriteAllText(python, "keep existing");
            try
            {
                var task = UPilotServerRuntimeService.PreparePythonEnvironmentAsync(
                    venv, _ => throw new AssertionException("Must not install into an existing environment"),
                    _ => throw new InvalidOperationException("missing dependency"), CancellationToken.None);
                while (!task.IsCompleted)
                    yield return null;
                var error = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
                Assert.That(error.Message, Does.Contain("人工修复"));
                Assert.That(File.ReadAllText(python), Is.EqualTo("keep existing"));
                Assert.That(JsonUtility.ToJson(UPilotProjectConfig.Current), Is.EqualTo(configBefore));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [UnityTest]
        public IEnumerator PythonEnvironmentFailedInstallAndCancellationDoNotChangeConfiguration()
        {
            var root = Path.Combine(Path.GetTempPath(), "upilot-python-test-" + Guid.NewGuid().ToString("N"));
            var venv = Path.Combine(root, "venv");
            var configBefore = JsonUtility.ToJson(UPilotProjectConfig.Current);
            try
            {
                var failed = UPilotServerRuntimeService.PreparePythonEnvironmentAsync(
                    venv, _ => throw new IOException("installation failed"), _ => Assert.Fail("Must not validate"),
                    CancellationToken.None);
                while (!failed.IsCompleted)
                    yield return null;
                Assert.Throws<IOException>(() => failed.GetAwaiter().GetResult());
                using var cts = new CancellationTokenSource();
                using (var held = new FileStream(venv + ".install.lock.tmp", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    var cancelled = UPilotServerRuntimeService.PreparePythonEnvironmentAsync(
                        venv, _ => throw new AssertionException("Must wait for lock"), _ => { }, cts.Token);
                    Assert.That(cancelled.IsCompleted, Is.False);
                    cts.Cancel();
                    while (!cancelled.IsCompleted)
                        yield return null;
                    Assert.Throws<TaskCanceledException>(() => cancelled.GetAwaiter().GetResult());
                }
                Assert.That(JsonUtility.ToJson(UPilotProjectConfig.Current), Is.EqualTo(configBefore));
                Assert.That(Directory.Exists(venv), Is.False);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void ManagedServerCacheUsesUserProfileVersionPathAndRequiresSha256()
        {
            var expectedRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".upilot",
                "servers");
            Assert.That(UPilotServerRuntimeService.Instance.RuntimeCacheRoot, Is.EqualTo(expectedRoot));

            var resolvePath = typeof(UPilotServerRuntimeService).GetMethod(
                "TryGetManagedServerPath",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(resolvePath, Is.Not.Null);
            var arguments = new object[] { "9.8.7", "upilot-mcp-server-9.8.7-win-x64.exe", null, null };
            Assert.That((bool)resolvePath.Invoke(null, arguments), Is.True);
            Assert.That((string)arguments[2], Is.EqualTo(Path.Combine(
                expectedRoot,
                "9.8.7",
                "upilot-mcp-server-9.8.7-win-x64.exe")));

            var directory = Path.Combine(Path.GetTempPath(), "upilot-managed-server-hash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "server.exe");
            File.WriteAllText(file, "verified");
            try
            {
                var isVerified = typeof(UPilotServerRuntimeService).GetMethod(
                    "IsVerifiedServerFileReady",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(isVerified, Is.Not.Null);
                var digest = UPilotServerRuntimeService.ComputeSha256(file);
                Assert.That((bool)isVerified.Invoke(null, new object[] { file, digest }), Is.True);
                Assert.That((bool)isVerified.Invoke(null, new object[] { file, "" }), Is.False);

                var validate = typeof(UPilotServerRuntimeService).GetMethod(
                    "TryValidateManagedServerDownload",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(validate, Is.Not.Null);
                var manifest = new UPilotReleaseManifest { ServerVersion = "9.8.7" };
                var download = new UPilotServerDownloadInfo
                {
                    Url = "https://example.test/upilot-mcp-server-9.8.7-win-x64.exe",
                    FileName = "upilot-mcp-server-9.8.7-win-x64.exe",
                    SizeBytes = 1,
                };
                var validationArgs = new object[] { manifest, download, null };
                Assert.That((bool)validate.Invoke(null, validationArgs), Is.False);
                Assert.That((string)validationArgs[2], Does.Contain("SHA256"));
                download.Sha256 = digest;
                validationArgs = new object[] { manifest, download, null };
                Assert.That((bool)validate.Invoke(null, validationArgs), Is.True);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [UnityTest]
        public IEnumerator VerifiedDownloadMoveRetriesTransientFileLocks()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("File sharing behavior is Windows-specific.");

            var directory = Path.Combine(Path.GetTempPath(), "upilot-file-retry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var downloadPath = Path.Combine(directory, "server.exe.download");
            var finalPath = Path.Combine(directory, "server.exe");
            File.WriteAllText(downloadPath, "verified");

            FileStream heldStream = null;
            try
            {
                var expectedSha256 = UPilotServerRuntimeService.ComputeSha256(downloadPath);
                heldStream = new FileStream(downloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var method = typeof(UPilotServerRuntimeService).GetMethod(
                    "ReplaceVerifiedDownloadAsync",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.That(method, Is.Not.Null);
                var moveTask = (Task<bool>)method.Invoke(
                    null,
                    new object[] { downloadPath, finalPath, expectedSha256, CancellationToken.None });
                var releaseAt = EditorApplication.timeSinceStartup + 0.6;
                while (EditorApplication.timeSinceStartup < releaseAt)
                    yield return null;
                heldStream.Dispose();
                heldStream = null;

                while (!moveTask.IsCompleted)
                    yield return null;
                Assert.That(moveTask.GetAwaiter().GetResult(), Is.True);
                Assert.That(File.Exists(finalPath), Is.True);
                Assert.That(File.Exists(downloadPath), Is.False);
            }
            finally
            {
                heldStream?.Dispose();
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [UnityTest]
        public IEnumerator VerifiedDownloadCopyFallbackRevalidatesTarget()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-copy-fallback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var downloadPath = Path.Combine(directory, "server.exe.download");
            var finalPath = Path.Combine(directory, "server.exe");
            File.WriteAllText(downloadPath, "verified-copy");

            try
            {
                var expectedSha256 = UPilotServerRuntimeService.ComputeSha256(downloadPath);
                var method = typeof(UPilotServerRuntimeService).GetMethod(
                    "CopyVerifiedDownloadWithRetryAsync",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.That(method, Is.Not.Null);
                var copyTask = (Task)method.Invoke(
                    null,
                    new object[] { downloadPath, finalPath, expectedSha256, CancellationToken.None });
                while (!copyTask.IsCompleted)
                    yield return null;
                copyTask.GetAwaiter().GetResult();

                Assert.That(File.Exists(finalPath), Is.True);
                Assert.That(UPilotServerRuntimeService.ComputeSha256(finalPath), Is.EqualTo(expectedSha256));
                Assert.That(File.Exists(downloadPath), Is.True);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void PendingManagedPackageUpdateNoticeSurvivesUntilUpmCompletes()
        {
            UPilotPackageUpdateLifecycle.ClearPendingManagedPackageUpdate();
            try
            {
                UPilotPackageUpdateLifecycle.MarkManagedPackageUpdatePending(
                    "99.9.1",
                    "99.9.1",
                    "new-server",
                    "old-server",
                    "99.9.0");

                Assert.That(UPilotPackageUpdateLifecycle.HasPendingManagedPackageUpdate, Is.True);
                Assert.That(
                    UPilotPackageUpdateLifecycle.TryGetPendingPackageUpdateNotice(out var message),
                    Is.True);
                Assert.That(message, Does.Contain("自动管理服务已更新到 99.9.1"));
                Assert.That(message, Does.Contain("UPilot 包还未完成更新"));
                Assert.That(message, Does.Contain("继续更新"));

                UPilotPackageUpdateLifecycle.ClearPendingManagedPackageUpdate();
                Assert.That(UPilotPackageUpdateLifecycle.HasPendingManagedPackageUpdate, Is.False);
            }
            finally
            {
                UPilotPackageUpdateLifecycle.ClearPendingManagedPackageUpdate();
            }
        }

        [Test]
        public void MainWindowCanOpenWithLongLivedUpdateNotice()
        {
            var method = typeof(UPilotMainWindow).GetMethod(
                "OpenWithNotice",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(MessageType), typeof(double) },
                null);

            Assert.That(method, Is.Not.Null);
        }

        [Test]
        public void PreferenceResetDeletesOnlyRegisteredKeys()
        {
            string prefix = "CodingRiver.UPilot.Tests." + Guid.NewGuid().ToString("N");
            string first = prefix + ".First";
            string second = prefix + ".Second";
            string unrelated = prefix + ".Unrelated";
            EditorPrefs.SetString(first, "one");
            EditorPrefs.SetInt(second, 2);
            EditorPrefs.SetBool(unrelated, true);

            try
            {
                int deleted = UPilotPreferences.DeleteKeys(new[] { first, second });

                Assert.That(deleted, Is.EqualTo(2));
                Assert.That(EditorPrefs.HasKey(first), Is.False);
                Assert.That(EditorPrefs.HasKey(second), Is.False);
                Assert.That(EditorPrefs.GetBool(unrelated), Is.True);
            }
            finally
            {
                EditorPrefs.DeleteKey(first);
                EditorPrefs.DeleteKey(second);
                EditorPrefs.DeleteKey(unrelated);
            }
        }

        [Test]
        public void CurrentProjectPreferenceListExcludesGlobalAndProjectConfigSettings()
        {
            var keys = UPilotPreferences.CurrentProjectKeys;

            Assert.That(keys, Does.Contain(UPilotPreferences.McpPythonEntryKey));
            Assert.That(keys, Does.Contain(UPilotPreferences.McpHealthCheckIntervalSecondsKey));
            Assert.That(keys, Does.Contain(UPilotPreferences.BridgeHeartbeatIntervalSecondsKey));
            Assert.That(keys, Does.Contain(UPilotPreferences.SetupCompletedKey));
            Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Count));
            Assert.That(keys, Does.Not.Contain(UPilotBootstrap.EnabledPrefKey));
            Assert.That(keys, Does.Not.Contain(Logger.LogToUnityConsolePrefsKey));
            Assert.That(keys.Any(key => key.Contains("config.json")), Is.False);
        }

        [Test]
        public async Task BridgeHeartbeatIntervalPersistsClampsWakesAndResets()
        {
            var key = UPilotPreferences.BridgeHeartbeatIntervalSecondsKey;
            var hadKey = EditorPrefs.HasKey(key);
            var original = EditorPrefs.GetInt(key);
            using var changed = new SemaphoreSlim(0, 1);
            using var cancellation = new CancellationTokenSource();
            var bridge = (UPilotBridge)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(UPilotBridge));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(UPilotBridge).GetField("_heartbeatIntervalChanged", flags).SetValue(bridge, changed);
            typeof(UPilotBridge).GetField("_heartbeatIntervalMs", flags).SetValue(bridge, 3000);
            Task<bool> pendingWait = null;
            try
            {
                EditorPrefs.DeleteKey(key);
                var reload = typeof(UPilotBridge).GetMethod("LoadHeartbeatIntervalPreference", flags);
                reload.Invoke(bridge, null);
                Assert.That(bridge.HeartbeatIntervalSeconds, Is.EqualTo(3));
                Assert.That(bridge.HeartbeatIntervalMs, Is.EqualTo(3000));
                Assert.That(bridge.HeartbeatTimeoutMs, Is.EqualTo(9000));
                bridge.HeartbeatIntervalSeconds = 3;
                Assert.That(changed.Wait(0), Is.False, "An unchanged setting must not wake the heartbeat loop.");

                bridge.HeartbeatIntervalSeconds = 12;
                Assert.That(EditorPrefs.GetInt(key), Is.EqualTo(12));
                typeof(UPilotBridge).GetField("_heartbeatIntervalMs", flags).SetValue(bridge, 0);
                reload.Invoke(bridge, null);
                Assert.That(bridge.HeartbeatIntervalMs, Is.EqualTo(12000));
                Assert.That(bridge.HeartbeatTimeoutMs, Is.EqualTo(36000));
                Assert.That(changed.Wait(0), Is.True);
                bridge.HeartbeatIntervalSeconds = int.MinValue;
                Assert.That(bridge.HeartbeatIntervalSeconds, Is.EqualTo(1));
                Assert.That(EditorPrefs.GetInt(key), Is.EqualTo(1));
                Assert.That(changed.Wait(0), Is.True);
                bridge.HeartbeatIntervalSeconds = int.MaxValue;
                Assert.That(bridge.HeartbeatIntervalSeconds, Is.EqualTo(600));
                Assert.That(EditorPrefs.GetInt(key), Is.EqualTo(600));
                Assert.That(bridge.HeartbeatTimeoutMs, Is.EqualTo(1800000));
                Assert.That(changed.Wait(0), Is.True);

                pendingWait = changed.WaitAsync(bridge.HeartbeatIntervalMs, cancellation.Token);
                Assert.That(pendingWait.IsCompleted, Is.False);
                bridge.HeartbeatIntervalSeconds = 3;
                Assert.That(await Task.WhenAny(pendingWait, Task.Delay(1000, cancellation.Token)),
                    Is.SameAs(pendingWait), "Changing a long interval must wake the existing wait immediately.");
                Assert.That(await pendingWait, Is.True);
                Assert.That(changed.Wait(0), Is.False, "One change must not queue multiple wakes.");
                bridge.HeartbeatIntervalSeconds = 3;
                Assert.That(changed.Wait(0), Is.False);

                bridge.HeartbeatIntervalSeconds = 9;
                Assert.That(changed.Wait(0), Is.True);
                UPilotPreferences.DeleteKeys(new[] { key });
                bridge.ResetHeartbeatIntervalToDefault();
                Assert.That(bridge.HeartbeatIntervalSeconds, Is.EqualTo(3));
                Assert.That(bridge.HeartbeatIntervalMs, Is.EqualTo(3000));
                Assert.That(bridge.HeartbeatTimeoutMs, Is.EqualTo(9000));
                Assert.That(EditorPrefs.HasKey(key), Is.False, "Reset must not recreate a deleted preference.");
                Assert.That(changed.Wait(0), Is.True);
            }
            finally
            {
                cancellation.Cancel();
                if (pendingWait != null)
                {
                    try { await pendingWait; }
                    catch (OperationCanceledException) { }
                }
                if (hadKey) EditorPrefs.SetInt(key, original);
                else EditorPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void SkillInstallMetadataDetectsLocalChanges()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-skill-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                File.WriteAllText(Path.Combine(directory, "SKILL.md"), "managed content");

                var writeMethod = typeof(UPilotAgentSetup).GetMethod(
                    "WriteSkillInstallMetadata",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);
                writeMethod.Invoke(null, new object[] { directory });

                var readMethod = typeof(UPilotAgentSetup).GetMethod(
                    "TryReadSkillInstallMetadata",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(int).MakeByRefType(), typeof(string).MakeByRefType() },
                    null);
                var readArgs = new object[] { directory, 0, null };
                var readOk = (bool)readMethod.Invoke(null, readArgs);

                var hashMethod = typeof(UPilotAgentSetup).GetMethod(
                    "ComputeSkillInstallHash",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var originalHash = (string)hashMethod.Invoke(null, new object[] { directory });

                Assert.That(readOk, Is.True);
                Assert.That(readArgs[1], Is.EqualTo(GetSkillInstallTemplateVersion()));
                Assert.That(readArgs[2], Is.EqualTo(originalHash));

                var cacheDirectory = Path.Combine(directory, "scripts", "__pycache__");
                Directory.CreateDirectory(cacheDirectory);
                File.WriteAllBytes(Path.Combine(cacheDirectory, "generated.pyc"), new byte[] { 1, 2, 3 });
                File.WriteAllText(Path.Combine(cacheDirectory, "cache.json"), "{}");
                File.WriteAllText(Path.Combine(directory, "SKILL.md.meta"), "ignored meta");
                var ignoredDirectory = Path.Combine(directory, "ignored.meta");
                Directory.CreateDirectory(ignoredDirectory);
                File.WriteAllText(Path.Combine(ignoredDirectory, "content.txt"), "ignored directory");
                var hashWithGeneratedCache = (string)hashMethod.Invoke(null, new object[] { directory });
                Assert.That(hashWithGeneratedCache, Is.EqualTo(originalHash));

                File.AppendAllText(Path.Combine(directory, "SKILL.md"), "\nuser change");
                var changedHash = (string)hashMethod.Invoke(null, new object[] { directory });
                Assert.That(changedHash, Is.Not.EqualTo(originalHash));
            }
            finally
            {
                if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void CustomizedManagedSkillIsBackedUpAndRewrittenWithoutForce()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-skill-customized-" + Guid.NewGuid().ToString("N"));
            var backupRoot = Path.Combine(directory, ".upilot", "backups", "agent-integrations");
            var backupsBefore = Directory.Exists(backupRoot)
                ? new HashSet<string>(Directory.GetDirectories(backupRoot), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string createdBackup = null;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/io.github.codingriver.upilot/package.json").resolvedPath;
            try
            {
                Assert.That(UPilotAgentSetup.SyncAgentIntegrationsAt(directory, package, 8011, true,
                    AgentIntegrationScope.Skills).ok, Is.True);
                var skillPath = Path.Combine(directory, ".agents/skills/upilot-unity-mcp/SKILL.md");
                File.AppendAllText(skillPath, "\nlocal customization\n");
                var before = File.ReadAllBytes(skillPath);

                var updateResult = UPilotAgentSetup.SyncAgentIntegrationsAt(directory, package, 8011, true,
                    AgentIntegrationScope.Skills);
                Assert.That(updateResult.ok, Is.True, updateResult.error);

                Assert.That(File.ReadAllText(skillPath), Does.Not.Contain("local customization"));
                Assert.That(updateResult.targets[0].backupPath, Is.Not.Empty);
                var backupsAfter = Directory.GetDirectories(backupRoot);
                createdBackup = backupsAfter.Single(path => !backupsBefore.Contains(path));
                var externalBackupSkill = Directory.GetFiles(
                        createdBackup,
                        "SKILL.md",
                        SearchOption.AllDirectories)
                    .Single();
                Assert.That(File.ReadAllBytes(externalBackupSkill), Is.EqualTo(before));
                Assert.That(File.Exists(Path.Combine(createdBackup, "manifest.json")), Is.True);
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                if (!string.IsNullOrEmpty(createdBackup) && Directory.Exists(createdBackup))
                    Directory.Delete(createdBackup, recursive: true);
            }
        }

        [Test]
        public void ManagedSkillRefreshDetectsSourceChangeWithoutTemplateVersionBump()
        {
            var currentVersion = GetSkillInstallTemplateVersion();

            Assert.That(
                UPilotAgentSetup.IsManagedSkillInstallUpdateAvailable(
                    currentVersion,
                    "installed-hash",
                    "new-source-hash"),
                Is.True);
            Assert.That(
                UPilotAgentSetup.IsManagedSkillInstallUpdateAvailable(
                    currentVersion,
                    "matching-hash",
                    "matching-hash"),
                Is.False);
            Assert.That(
                UPilotAgentSetup.IsManagedSkillInstallUpdateAvailable(
                    currentVersion,
                    "matching-hash",
                    "matching-hash",
                    "old-template-hash",
                    "new-template-hash"),
                Is.True);
        }

        [Test]
        public void LegacyV1SkillInstallMetadataRemainsReadable()
        {
            var directory = Path.Combine(Path.GetTempPath(), "upilot-skill-v1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var hash = new string('a', 64);
                File.WriteAllText(
                    Path.Combine(directory, ".upilot-install.json"),
                    "{\"templateVersion\":30,\"contentSha256\":\"" + hash + "\"}");
                var method = typeof(UPilotAgentSetup).GetMethod(
                    "TryReadSkillInstallMetadata",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[]
                    {
                        typeof(string),
                        typeof(int).MakeByRefType(),
                        typeof(string).MakeByRefType(),
                        typeof(string).MakeByRefType(),
                    },
                    null);
                var args = new object[] { directory, 0, null, null };

                Assert.That((bool)method.Invoke(null, args), Is.True);
                Assert.That(args[1], Is.EqualTo(30));
                Assert.That(args[2], Is.EqualTo(hash));
                Assert.That(args[3], Is.EqualTo(""));
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }

        [Test]
        public void ManagedSkillTemplatesRenderBothEndpointArtifacts()
        {
            var resolveMethod = typeof(UPilotAgentSetup).GetMethod(
                "ResolvePackageRoot",
                BindingFlags.NonPublic | BindingFlags.Static);
            var packageRoot = (string)resolveMethod.Invoke(null, null);
            var skillRoot = Path.Combine(packageRoot, "skills", "upilot-unity-mcp");
            var renderMethod = typeof(UPilotAgentSetup).GetMethod(
                "BuildRenderedSkillOutputs",
                BindingFlags.NonPublic | BindingFlags.Static);

            var outputs = (Dictionary<string, string>)renderMethod.Invoke(null, new object[] { skillRoot });

            Assert.That(outputs["SKILL.md"], Does.Contain(UPilotAgentSetup.McpUrl));
            Assert.That(outputs["SKILL.md"], Does.Contain(UPilotAgentSetup.HealthUrl));
            Assert.That(outputs["agents/openai.yaml"], Does.Contain(UPilotAgentSetup.McpUrl));
            Assert.That(outputs.Values.Any(value => value.Contains("{{")), Is.False);
        }

        [Test]
        public void UnsavedScenePolicyDefaultsToBlockAndRejectsUnknownValues()
        {
            var safety = new UPilotSafetyConfig();

            Assert.That(safety.unsavedScenePolicy, Is.EqualTo(UPilotSafetyConfig.UnsavedScenePolicyBlock));
            Assert.That(
                UPilotSafetyConfig.NormalizeUnsavedScenePolicy("autoSave"),
                Is.EqualTo(UPilotSafetyConfig.UnsavedScenePolicyAutoSave));
            Assert.That(
                UPilotSafetyConfig.NormalizeUnsavedScenePolicy("ignore"),
                Is.EqualTo(UPilotSafetyConfig.UnsavedScenePolicyIgnore));
            Assert.That(
                UPilotSafetyConfig.NormalizeUnsavedScenePolicy("unexpected"),
                Is.EqualTo(UPilotSafetyConfig.UnsavedScenePolicyBlock));
        }

        [Test]
        public void SkillSourceIncludesAgentsTemplateForManagedUpdates()
        {
            var resolveMethod = typeof(UPilotAgentSetup).GetMethod(
                "ResolvePackageRoot",
                BindingFlags.NonPublic | BindingFlags.Static);
            var packageRoot = (string)resolveMethod.Invoke(null, null);
            var source = Path.Combine(
                packageRoot,
                "skills",
                "upilot-unity-mcp",
                "AGENTS.md.template");

            Assert.That(File.Exists(source), Is.True);
            Assert.That(File.ReadAllText(source), Does.Contain("{{projectPath}}"));
        }

        [Test]
        public void TestRunnerNoTestsIsExplicitTerminalContractWithoutSyntheticFailure()
        {
            var payload = new TestRunResultPayload();
            string terminal = UPilotTestService.ApplyRunResults(payload, new List<TestResultItemPayload>(), false);

            Assert.That(terminal, Is.EqualTo("no_tests"));
            Assert.That(payload.noTests, Is.True);
            Assert.That(payload.discoveryStatus, Is.EqualTo("no_tests"));
            Assert.That(payload.total, Is.Zero);
            Assert.That(payload.failed, Is.Zero);
            Assert.That(payload.results, Is.Empty);
        }

        [Test]
        public void TestRunnerSnapshotRoundTripPreservesRunIdentityAndFailureEvidence()
        {
            var payload = new TestRunResultPayload
            {
                status = "failed",
                phase = "failed",
                testMode = "PlayMode",
                runGuid = "run-domain-reload-123",
                total = 2,
                passed = 1,
                failed = 1,
                endedAt = 123456,
                results = new List<TestResultItemPayload>
                {
                    new TestResultItemPayload
                    {
                        testName = "Example.PlayModeFailure",
                        testStatus = "Failed",
                        message = "expected failure",
                        stackTrace = "stack evidence",
                    },
                },
            };

            var restored = JsonUtility.FromJson<TestRunResultPayload>(JsonUtility.ToJson(payload));

            Assert.That(restored.runGuid, Is.EqualTo(payload.runGuid));
            Assert.That(restored.testMode, Is.EqualTo("PlayMode"));
            Assert.That(restored.total, Is.EqualTo(2));
            Assert.That(restored.results.Single().message, Is.EqualTo("expected failure"));
            Assert.That(restored.results.Single().stackTrace, Is.EqualTo("stack evidence"));
        }

        [Test]
        public void TestRunnerOutcomeAndCleanupEvidenceRemainSeparate()
        {
            var payload = new TestRunResultPayload
            {
                cleanupStatus = "failed",
                cleanupSucceeded = false,
                cleanupErrors = new List<string> { "callback-unregister" },
            };
            string outcome = UPilotTestService.ApplyRunResults(payload, new List<TestResultItemPayload>
            {
                new TestResultItemPayload { testName = "Fixture.Pass", testStatus = "Passed" },
            }, false);

            Assert.That(outcome, Is.EqualTo("completed"));
            Assert.That(payload.passed, Is.EqualTo(1));
            Assert.That(payload.failed, Is.Zero);
            Assert.That(payload.cleanupStatus, Is.EqualTo("failed"));
            Assert.That(payload.cleanupErrors, Has.Count.EqualTo(1));
        }

        [Test]
        public void TestRunnerRecoverySnapshotCanRepresentUnknownOutcomeWithoutFalseFailure()
        {
            var payload = new TestRunResultPayload
            {
                status = "aborted",
                phase = "callback_not_recovered",
                outcomeStatus = "unknown",
                resultAuthoritative = false,
                terminalReason = "callback was not recovered",
            };

            var restored = JsonUtility.FromJson<TestRunResultPayload>(JsonUtility.ToJson(payload));

            Assert.That(restored.status, Is.EqualTo("aborted"));
            Assert.That(restored.outcomeStatus, Is.EqualTo("unknown"));
            Assert.That(restored.resultAuthoritative, Is.False);
            Assert.That(restored.failed, Is.Zero);
        }

        [Test]
        public void TestRunnerFirstProgressWatchdogProducesBoundedDiagnosticState()
        {
            var payload = new TestRunResultPayload
            {
                firstProgressDeadlineAt = 1000,
                firstProgressObserved = false,
                watchdogState = "waiting_first_progress",
            };

            Assert.That(UPilotTestService.ApplyFirstProgressTimeout(payload, 999), Is.False);
            Assert.That(UPilotTestService.ApplyFirstProgressTimeout(payload, 1000), Is.True);
            Assert.That(payload.suspectedStuck, Is.True);
            Assert.That(payload.watchdogState, Is.EqualTo("first_progress_timeout"));
            Assert.That(payload.failureSignature, Is.EqualTo("TestRunner.FirstProgressTimeout"));
            Assert.That(payload.nextAction, Does.Contain("unity_hang_status"));
            Assert.That(payload.lastProgressAt, Is.EqualTo(1000));
        }

        [Test]
        public void NavMeshStatusReadsCurrentDataWithoutContinuousPreUpdateObserver()
        {
            var serviceType = typeof(UPilotRuntimeDiagnosticsService);
            var build = serviceType.GetMethod("BuildNavMeshStatus", BindingFlags.NonPublic | BindingFlags.Static);
            var result = (NavMeshStatusResultPayload)build.Invoke(null, new object[]
            {
                new NavMeshStatusPayload { includeSurfaces = false, includeAgents = false, includeTriangulation = true },
            });

            Assert.That(result.ok, Is.True);
            Assert.That(result.observedAt, Is.GreaterThan(0));
            Assert.That(result.observationVersion, Is.GreaterThan(0));
            Assert.That(result.observationSignature, Is.Not.Empty);
            Assert.That(result.triangulation, Is.Not.Null);
            Assert.That(result.triangulation.vertexCount, Is.GreaterThanOrEqualTo(0));
            Assert.That(result.triangulation.triangleCount, Is.GreaterThanOrEqualTo(0));
            var wire = JsonUtility.ToJson(result);
            Assert.That(wire, Does.Not.Contain("lastPreUpdateAt"));
            Assert.That(wire, Does.Not.Contain("preUpdateVersion"));

            var callbacksField = typeof(UnityEngine.AI.NavMesh).GetField(
                "onPreUpdate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(callbacksField, Is.Not.Null);
            var callbacks = callbacksField.GetValue(null) as Delegate;
            Assert.That(callbacks?.GetInvocationList().Any(callback =>
                callback.Method.DeclaringType == serviceType) ?? false, Is.False,
                "An on-demand NavMesh query must not install a continuous observer.");
        }

        [Test]
        public void NavMeshTransformVersionsChangeOnlyWhenQueriedTransformChanges()
        {
            var target = new GameObject("__UPilotNavMeshObservationTest");
            var serviceType = typeof(UPilotRuntimeDiagnosticsService);
            var observe = serviceType.GetMethod("ApplyNavMeshTransformObservation",
                BindingFlags.NonPublic | BindingFlags.Static);
            var info = new NavMeshSurfaceInfoPayload { instanceId = UPilotEntityIds.ToWireId(target) };
            try
            {
                observe.Invoke(null, new object[] { info, target.transform, null });
                var firstVersion = info.surfaceTransformVersion;
                var firstChangedAt = info.transformChangedAt;
                Assert.That(firstVersion, Is.GreaterThan(0));
                Assert.That(firstChangedAt, Is.GreaterThan(0));

                observe.Invoke(null, new object[] { info, target.transform, null });
                Assert.That(info.surfaceTransformVersion, Is.EqualTo(firstVersion));
                Assert.That(info.transformChangedAt, Is.EqualTo(firstChangedAt));

                target.transform.position = new Vector3(12f, 3f, -4f);
                observe.Invoke(null, new object[] { info, target.transform, null });
                Assert.That(info.surfaceTransformVersion, Is.EqualTo(firstVersion + 1));
                Assert.That(info.transformChangedAt, Is.GreaterThanOrEqualTo(firstChangedAt));
                var wire = JsonUtility.ToJson(info);
                Assert.That(wire, Does.Not.Contain("transformObservedPreUpdateVersion"));
                Assert.That(wire, Does.Not.Contain("transformAwaitingPreUpdate"));
            }
            finally
            {
                var observations = (IDictionary)serviceType.GetField("NavMeshTransformObservations",
                    BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                observations.Remove(info.instanceId);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void ProfilerTerminalElapsedTimeIsFrozenAcrossStatusReads()
        {
            Type serviceType = typeof(UPilotRuntimeDiagnosticsService);
            MethodInfo start = serviceType.GetMethod("StartProfiler", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo stop = serviceType.GetMethod("StopProfiler", BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo status = serviceType.GetMethod("GetProfilerStatus", BindingFlags.NonPublic | BindingFlags.Static);
            var started = (ProfilerCaptureResultPayload)start.Invoke(null, new object[]
            {
                new ProfilerCaptureStartPayload { durationSec = 1f, title = "elapsed-freeze-test" },
            });
            var terminal = (ProfilerCaptureResultPayload)stop.Invoke(null, new object[] { started.captureId, "Stopped" });
            double elapsed = terminal.elapsedSec;
            var queried = (ProfilerCaptureResultPayload)status.Invoke(null, new object[] { started.captureId });

            Assert.That(terminal.elapsedFrozen, Is.True);
            Assert.That(queried.elapsedSec, Is.EqualTo(elapsed).Within(0.000001d));
            Assert.That(queried.elapsedSource, Is.EqualTo("Stopwatch.GetTimestamp"));
        }

        [Test]
        public void ScriptProjectFileRefreshEntryPointIsAvailable()
        {
            Assert.DoesNotThrow(() => UPilotScriptService.RefreshGeneratedProjectFiles());
        }

        [Test]
        public void ShaderDiagnosticsCanReadMessagesWithoutVersionSpecificMessageType()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var result = new ShaderDiagnosticResultPayload();

            Assert.DoesNotThrow(() => UPilotMaterialService.ReadShaderMessages(shader, true, result));
            Assert.That(result.messageCount, Is.EqualTo(result.messages.Count));
            Assert.That(result.errorCount, Is.GreaterThanOrEqualTo(0));
            Assert.That(result.warningCount, Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void ScreenshotEvidencePayloadExposesPixelProvenance()
        {
            var capture = new EditorWindowPixelCapture
            {
                captureApi = "Win32.PrintWindow(PW_RENDERFULLCONTENT)",
                windowHandle = 42,
                pixelSourceVerified = true,
                occlusionSensitive = false,
                contentRectVerified = true,
                cropComplete = true,
                includesDecorations = false,
                geometrySource = "Win32.GetWindowRect+GetClientRect+ClientToScreen",
                pixelsPerPoint = 1.25f,
                windowLeft = 100,
                windowTop = 200,
                windowRight = 1100,
                windowBottom = 900,
                contentLeft = 110,
                contentTop = 230,
                contentRight = 1090,
                contentBottom = 890,
                cropLeft = 110,
                cropTop = 230,
                cropRight = 1090,
                cropBottom = 890,
            };
            Assert.That(capture.captureApi, Does.Contain("PrintWindow"));
            Assert.That(capture.windowHandle, Is.EqualTo(42));
            Assert.That(capture.pixelSourceVerified, Is.True);
            Assert.That(capture.occlusionSensitive, Is.False);
            Assert.That(capture.contentRectVerified, Is.True);
            Assert.That(capture.cropComplete, Is.True);
            Assert.That(capture.includesDecorations, Is.False);
            Assert.That(capture.geometrySource, Does.Contain("GetClientRect"));
            Assert.That(capture.pixelsPerPoint, Is.EqualTo(1.25f));
            Assert.That(capture.windowRight - capture.windowLeft, Is.EqualTo(1000));
            Assert.That(capture.contentBottom - capture.contentTop, Is.EqualTo(660));
            Assert.That(capture.cropLeft, Is.EqualTo(capture.contentLeft));
        }

        private static string BuildAgentRulesText()
        {
            var method = typeof(UPilotAgentSetup).GetMethod(
                "BuildAgentsMd",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            return (string)method.Invoke(null, null);
        }

        private static string WrapManagedRule(string content)
        {
            var method = typeof(UPilotAgentSetup).GetMethod(
                "WrapManagedBlock",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { content });
        }

        private static AgentRuleConfigState InspectManagedRuleState(string path, string expectedContent)
        {
            var method = typeof(UPilotAgentSetup).GetMethod(
                "InspectManagedRuleFile",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (AgentRuleConfigState)method.Invoke(null, new object[] { path, expectedContent });
        }

        private static int GetAgentRulesTemplateVersion()
        {
            var property = typeof(UPilotAgentSetup).GetProperty(
                "AgentRulesTemplateVersion",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (int)property.GetValue(null);
        }

        [Test]
        public void OccupancyDiagnosticsRecognizeUPilotPythonCommandLinesOnly()
        {
            Assert.That(
                UPilotProcessOccupancyService.IsUPilotServerCommandLine(
                    "python.exe D:/upilot/upilotserver~/run_upilot_mcp.py --http-port 8011"),
                Is.True);
            Assert.That(
                UPilotProcessOccupancyService.IsUPilotServerCommandLine(
                    "python -m upilot_mcp.mcp_main"),
                Is.True);
            Assert.That(
                UPilotProcessOccupancyService.IsUPilotServerCommandLine("python tools/build.py"),
                Is.False);
        }

        [TestCase("missingCreation")]
        [TestCase("changedCreation")]
        [TestCase("missingExecutable")]
        [TestCase("changedExecutable")]
        [TestCase("notTerminable")]
        [TestCase("valid")]
        public void OccupancyTerminationRevalidatesOwnedProcessIdentity(string identityCase)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("Uses one owned Windows child; never stops a shared process.");
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.That(File.Exists(executable), Is.True, "Owned-process executable is absent.");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.That(process, Is.Not.Null);
            try
            {
                var create = typeof(UPilotProcessOccupancyService).GetMethod("GetOrCreate",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(create, Is.Not.Null);
                UPilotOccupyingProcess occupant;
                var startupBudget = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                    occupant = (UPilotOccupyingProcess)create.Invoke(null, new object[]
                    {
                        new Dictionary<int, UPilotOccupyingProcess>(), process.Id,
                        Path.GetFileNameWithoutExtension(executable), "owned occupancy identity fixture",
                    });
                    if (occupant.CanTerminate || process.HasExited || startupBudget.ElapsedMilliseconds >= 5000)
                        break;
                    System.Threading.Thread.Sleep(20);
                } while (true);
                Assert.That(occupant.CanTerminate, Is.True, occupant.CannotTerminateReason);
                process.Refresh();
                Assert.That(occupant.CreatedAtTicks, Is.EqualTo(process.StartTime.ToUniversalTime().Ticks));
                Assert.That(occupant.ExecutablePath, Is.EqualTo(process.MainModule.FileName));
                if (identityCase == "missingCreation") occupant.CreatedAtTicks = 0;
                if (identityCase == "changedCreation") occupant.CreatedAtTicks++;
                if (identityCase == "missingExecutable") occupant.ExecutablePath = "";
                if (identityCase == "changedExecutable") occupant.ExecutablePath += ".not-the-owned-executable";
                if (identityCase == "notTerminable") occupant.CanTerminate = false;
                var scan = new UPilotOccupancyScanResult();
                scan.Processes.Add(occupant);
                var stopped = UPilotProcessOccupancyService.TerminateAll(scan);
                if (identityCase == "notTerminable")
                {
                    Assert.That(stopped.Succeeded, Is.Empty);
                    Assert.That(stopped.Failed, Is.Empty);
                    Assert.That(process.HasExited, Is.False);
                }
                else if (identityCase != "valid")
                {
                    Assert.That(stopped.Succeeded, Is.Empty);
                    Assert.That(stopped.Failed.Count, Is.EqualTo(1));
                    Assert.That(stopped.Failed.Single(), Does.Contain("拒绝结束"));
                    Assert.That(process.HasExited, Is.False, "Identity rejection must leave the owned child alive.");
                }
                else
                {
                    Assert.That(stopped.Failed, Is.Empty);
                    Assert.That(stopped.Succeeded.Count, Is.EqualTo(1));
                    Assert.That(process.HasExited, Is.True, "Success requires original-process exit.");
                }
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                Assert.That(process.WaitForExit(5000), Is.True, "Owned child must exit before completion.");
            }
        }

        [TestCase(0)]
        [TestCase(4)]
        [TestCase(-1)]
        public void OccupancyTerminationIdentityRejectsProtectedTargets(int processId)
        {
            using var editor = System.Diagnostics.Process.GetCurrentProcess();
            var occupant = new UPilotOccupyingProcess
            {
                ProcessId = processId < 0 ? editor.Id : processId,
                CanTerminate = true,
                CreatedAtTicks = editor.StartTime.ToUniversalTime().Ticks,
                ExecutablePath = editor.MainModule.FileName,
            };
            Assert.That(UPilotProcessOccupancyService.ValidateTerminationIdentity(occupant, editor, out var error),
                Is.False);
            Assert.That(error, Does.Contain("不能结束"));
        }

        [Test]
        public void OccupancyConfirmationListsEveryProcessWithoutIndividualActions()
        {
            var scan = new UPilotOccupancyScanResult();
            scan.Processes.Add(new UPilotOccupyingProcess
            {
                ProcessId = 101,
                ProcessName = "python",
                CommandLine = "python run_upilot_mcp.py",
                CanTerminate = true,
            });
            scan.Processes.Add(new UPilotOccupyingProcess
            {
                ProcessId = 202,
                ProcessName = "UPilotMcpServer",
                CanTerminate = false,
                CannotTerminateReason = "当前 Unity Editor 进程不能结束",
            });

            var text = UPilotProcessOccupancyService.BuildConfirmationMessage(scan);

            Assert.That(text, Does.Contain("PID 101"));
            Assert.That(text, Does.Contain("PID 202"));
            Assert.That(text, Does.Contain("其他 Unity 工程"));
            Assert.That(text, Does.Contain("无法结束"));
        }

        private static int GetSkillInstallTemplateVersion()
        {
            var property = typeof(UPilotAgentSetup).GetProperty(
                "SkillInstallTemplateVersion",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (int)property.GetValue(null);
        }

        private static string GetUpdateServiceProjectKey(string key)
        {
            var method = typeof(UPilotUpdateService).GetMethod(
                "ProjectKey",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { key });
        }

        private static string GetPackageLifecycleSessionKey(string key)
        {
            var method = typeof(UPilotPackageUpdateLifecycle).GetMethod(
                "ProjectSessionKey",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { key });
        }

        private static string ReplaceLine(string text, string prefix, string replacement)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                lines[i] = replacement;
                return string.Join("\n", lines);
            }

            throw new InvalidOperationException("Line prefix not found: " + prefix);
        }
    }
}
