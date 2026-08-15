using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        public void ReflectionBinderConvertsPrimitiveParameter()
        {
            var method = typeof(UPilotReflectionService).GetMethod("TryConvertParameter", BindingFlags.NonPublic | BindingFlags.Static);
            var args = new object[] { "42", typeof(int), null, null, 0 };
            var ok = (bool)method.Invoke(null, args);

            Assert.That(ok, Is.True);
            Assert.That(args[2], Is.EqualTo(42));
        }

        [Test]
        public void ScreenshotPathRejectsOutsideProjectByDefault()
        {
            var method = typeof(UPilotScreenshotService).GetMethod("ResolveSavePath", BindingFlags.NonPublic | BindingFlags.Static);
            var outside = Path.Combine(Path.GetPathRoot(Application.dataPath), "upilot-outside.png");
            var args = new object[] { outside, false, null };
            var resolved = (string)method.Invoke(null, args);

            Assert.That(resolved, Is.Empty);
            Assert.That(args[2], Does.Contain("current Unity project"));
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
                    source = "bridge-response",
                    sessionId = "session-1",
                    playModeState = "edit",
                    updatedAt = 123,
                    lastMainThreadPumpAt = 123,
                    mainThreadQueueDepth = 2,
                    processId = 42,
                },
            };

            var json = JsonUtility.ToJson(message);

            Assert.That(json, Does.Contain("\"authoritative\":true"));
            Assert.That(json, Does.Contain("\"source\":\"bridge-response\""));
            Assert.That(json, Does.Contain("\"mainThreadQueueDepth\":2"));
            Assert.That(json, Does.Contain("\"processId\":42"));
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
            File.WriteAllText(jsonlPath, "{\"sequence\":0}\n");
            var manifest = new ConsoleCaptureManifest
            {
                sessionId = sessionId,
                title = "historical-stop",
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

                var result = (ConsoleCaptureResult)stop.Invoke(null, new object[] { sessionId });
                var persisted = JsonUtility.FromJson<ConsoleCaptureManifest>(
                    File.ReadAllText(manifestPath));

                Assert.That(result.ok, Is.True);
                Assert.That(result.session.active, Is.False);
                Assert.That(persisted.active, Is.False);
                Assert.That(persisted.finishedAtUtcMs, Is.GreaterThan(0));
                Assert.That(persisted.sha256, Is.Not.Empty);
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
            Assert.That(text, Does.Contain("prefer one `unity_safe_compile_and_wait` call"));
            Assert.That(text, Does.Contain("Do not compile again when no code changed"));
            Assert.That(text, Does.Contain("project-provided bridge entry points"));
            Assert.That(text, Does.Contain("waitWindowElapsed=true/terminal=false"));
            Assert.That(text, Does.Contain("unity_console_capture_start"));
            Assert.That(text, Does.Contain("always call `unity_console_capture_stop`"));
            Assert.That(text, Does.Contain("`nextSequence` as the next call's `afterSequence`"));
            Assert.That(text, Does.Contain("recovered or historical sessions still marked active"));
            Assert.That(text, Does.Contain("separate from domain-specific reports"));
            Assert.That(text, Does.Contain("unity_config_csv_get"));
            Assert.That(text, Does.Contain("unity_config_csv_patch"));
            Assert.That(text, Does.Contain("explicit write approval"));
            Assert.That(text, Does.Contain("unity_hang_status"));
            Assert.That(text, Does.Contain("unity_hang_capture"));
            Assert.That(text, Does.Contain("fallbackSources"));
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
        public void AgentRulesAutoSetupKeyTracksRulesAndSkillTemplateVersions()
        {
            var rulesVersion = GetAgentRulesTemplateVersion();
            var skillVersion = GetSkillInstallTemplateVersion();

            var keys = UPilotAgentSetup.GetAgentRulesPreferenceKeysForCurrentProject();

            Assert.That(keys, Has.Some.EndsWith($".rules.v{rulesVersion}.skill.v{skillVersion}"));
            Assert.That(keys, Has.Some.EndsWith($".v{rulesVersion}"));
        }

        [Test]
        public void AgentSetupExposesSupportedMcpAndRuleStatusesInSameOrder()
        {
            var mcpStatuses = UPilotAgentSetup.GetMcpConfigStatuses();
            var ruleStatuses = UPilotAgentSetup.GetRuleConfigStatuses();

            Assert.That(mcpStatuses.Length, Is.EqualTo(3));
            Assert.That(ruleStatuses.Length, Is.EqualTo(3));
            Assert.That(mcpStatuses[0].ClientName, Is.EqualTo("Codex"));
            Assert.That(mcpStatuses[1].ClientName, Is.EqualTo("Claude Code"));
            Assert.That(mcpStatuses[2].ClientName, Is.EqualTo("Cursor"));
            Assert.That(ruleStatuses[0].ClientName, Is.EqualTo("Codex"));
            Assert.That(ruleStatuses[1].ClientName, Is.EqualTo("Claude Code"));
            Assert.That(ruleStatuses[2].ClientName, Is.EqualTo("Cursor"));
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
            var method = typeof(UPilotMcpServerManager).GetMethod(
                "IsCurrentProjectMcpCommandLine",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);
            Assert.That(
                method.Invoke(null, new object[]
                {
                    "upilot-mcp-server.exe --transport http --http-port 8012 --port 8766",
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
                    "upilot-mcp-server.exe --transport http --http-port=8012 --port=8766",
                    8012,
                    8766,
                }),
                Is.True);
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
                PlatformDisplayName = "Windows x64",
            };

            Assert.That(state.SegmentCount, Is.EqualTo(4));
            Assert.That(state.CompletedSegments, Is.EqualTo(2));
            Assert.That(state.PlatformDisplayName, Is.EqualTo("Windows x64"));
        }

        [Test]
        public void UpdateDownloadProgressLabelShowsThreadCount()
        {
            var multiThread = new UPilotDownloadState
            {
                Phase = "正在下载安装",
                SegmentCount = 4,
                CompletedSegments = 2,
                BytesReceived = 10 * 1024 * 1024,
                TotalBytes = 20 * 1024 * 1024,
            };
            var singleThread = new UPilotDownloadState
            {
                Phase = "正在下载安装",
                SegmentCount = 1,
                BytesReceived = 512 * 1024,
                TotalBytes = 1024 * 1024,
            };

            Assert.That(
                UPilotUpdateService.FormatDownloadProgressLabel(multiThread),
                Is.EqualTo("正在下载安装（4 线程，已完成 2/4）"));
            Assert.That(
                UPilotUpdateService.FormatDownloadProgressDetail(multiThread),
                Does.Contain("4 线程下载"));
            Assert.That(
                UPilotUpdateService.FormatDownloadProgressLabel(singleThread),
                Is.EqualTo("正在下载安装（单线程）"));
            Assert.That(
                UPilotUpdateService.FormatDownloadProgressDetail(singleThread),
                Does.Contain("单线程下载"));

            var verifying = new UPilotDownloadState
            {
                Phase = "正在验证文件",
                SegmentCount = 4,
                CompletedSegments = 4,
            };
            Assert.That(
                UPilotUpdateService.FormatDownloadProgressLabel(verifying),
                Is.EqualTo("正在验证文件"));
        }

        [Test]
        public void ManagedServerPackageUpdateSupportsPreparedServerContinuation()
        {
            Assert.That(typeof(UPilotPreparedServerDownload).GetField("TargetPath"), Is.Not.Null);
            Assert.That(typeof(UPilotPreparedServerDownload).GetField("Version"), Is.Not.Null);
            Assert.That(
                typeof(UPilotServerRuntimeService).GetMethod(
                    "PrepareLatestServerExeAsync",
                    new[] { typeof(UPilotReleaseManifest) }),
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
            Assert.That(keys, Does.Contain(UPilotPreferences.SetupCompletedKey));
            Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Count));
            Assert.That(keys, Does.Not.Contain(UPilotBootstrap.EnabledPrefKey));
            Assert.That(keys, Does.Not.Contain(Logger.LogToUnityConsolePrefsKey));
            Assert.That(keys.Any(key => key.Contains("config.json")), Is.False);
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
                    BindingFlags.NonPublic | BindingFlags.Static);
                writeMethod.Invoke(null, new object[] { directory });

                var readMethod = typeof(UPilotAgentSetup).GetMethod(
                    "TryReadSkillInstallMetadata",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var readArgs = new object[] { directory, 0, null };
                var readOk = (bool)readMethod.Invoke(null, readArgs);

                var hashMethod = typeof(UPilotAgentSetup).GetMethod(
                    "ComputeSkillInstallHash",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var originalHash = (string)hashMethod.Invoke(null, new object[] { directory });

                Assert.That(readOk, Is.True);
                Assert.That(readArgs[1], Is.EqualTo(GetSkillInstallTemplateVersion()));
                Assert.That(readArgs[2], Is.EqualTo(originalHash));

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
            var field = typeof(UPilotAgentSetup).GetField(
                "AgentRulesTemplateVersion",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (int)field.GetRawConstantValue();
        }

        private static int GetSkillInstallTemplateVersion()
        {
            var field = typeof(UPilotAgentSetup).GetField(
                "SkillInstallTemplateVersion",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (int)field.GetRawConstantValue();
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
