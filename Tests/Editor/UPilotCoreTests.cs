using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

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
        public void AgentTemplateUsesCapabilityCompileAndWorkflowRules()
        {
            var method = typeof(UPilotAgentSetup).GetMethod("BuildAgentsMd", BindingFlags.NonPublic | BindingFlags.Static);
            var text = (string)method.Invoke(null, null);

            Assert.That(text, Does.Contain("unity_capabilities_get"));
            Assert.That(text, Does.Contain("prefer an available UPilot semantic tool"));
            Assert.That(text, Does.Contain("Use `unity_tools_find` for targeted discovery"));
            Assert.That(text, Does.Contain("Do not compile again when no code changed"));
            Assert.That(text, Does.Contain("authoritative compiled orchestration entry point"));
            Assert.That(text, Does.Contain("unity_console_capture_start"));
            Assert.That(text, Does.Contain("always call `unity_console_capture_stop`"));
            Assert.That(text, Does.Contain("separate from domain-specific reports"));
            Assert.That(text, Does.Contain("incremental status, log, and report APIs"));
            Assert.That(text, Does.Contain("artifact or screenshot save tools"));
            Assert.That(text, Does.Not.Contain("## UPilot Flow"));
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
            Assert.That(Enum.IsDefined(typeof(UPilotMainState), UPilotMainState.Restarting), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotMainState), UPilotMainState.Stopping), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotMainState), UPilotMainState.Updating), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotServiceOperation), UPilotServiceOperation.Restarting), Is.True);
            Assert.That(Enum.IsDefined(typeof(UPilotServiceOperation), UPilotServiceOperation.Stopping), Is.True);
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
        public void FirstSetupEntryIsHostedByMainWindow()
        {
            Assert.That(typeof(EditorWindow).IsAssignableFrom(typeof(UPilotFirstSetupWindow)), Is.False);
            Assert.That(
                typeof(UPilotMainWindow).GetMethod("OpenSetup", BindingFlags.Public | BindingFlags.Static),
                Is.Not.Null);
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
                Assert.That(readArgs[1], Is.EqualTo(1));
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
    }
}
