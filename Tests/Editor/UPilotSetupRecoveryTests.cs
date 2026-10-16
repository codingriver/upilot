using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotSetupRecoveryTests
    {
        private const string ExpectedFailurePrefix = "[预期故障测试] ";
        private readonly Dictionary<string, (bool exists, string value)> _strings = new();
        private readonly Dictionary<string, (bool exists, bool value)> _bools = new();
        private bool _lastRepairSucceeded;

        private static string RepairKey(string suffix) =>
            UPilotPreferences.ProjectKey("UPilot.DirectRepair." + suffix);

        [SetUp]
        public void SetUp()
        {
            foreach (var key in new[] { RepairKey("Failure"), RepairKey("Dialog") })
                _strings[key] = (EditorPrefs.HasKey(key), EditorPrefs.GetString(key, ""));
            foreach (var key in new[] { RepairKey("Attempted"), UPilotPreferences.SetupCompletedKey })
                _bools[key] = (EditorPrefs.HasKey(key), EditorPrefs.GetBool(key, false));
            _lastRepairSucceeded = UPilotQuickStart.LastRepairSucceeded;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var entry in _strings)
            {
                if (entry.Value.exists) EditorPrefs.SetString(entry.Key, entry.Value.value);
                else EditorPrefs.DeleteKey(entry.Key);
            }
            foreach (var entry in _bools)
            {
                if (entry.Value.exists) EditorPrefs.SetBool(entry.Key, entry.Value.value);
                else EditorPrefs.DeleteKey(entry.Key);
            }
            SetLastRepairSucceeded(_lastRepairSucceeded);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepairFailureReopensWizardOnceWithoutCompletingOrReplaying(bool previouslyConfigured)
        {
            EditorPrefs.SetBool(UPilotPreferences.SetupCompletedKey, previouslyConfigured);
            SetLastRepairSucceeded(true);
            var attempt = "setup-recovery-test-" + Guid.NewGuid().ToString("N");
            const string failure = "identity_probe\n无法安全停止 Server：端口进程不属于当前项目或身份未知。";
            var calls = new List<string>();
            var record = UPilotServerRestartDiagnostics.Current;
            var repairing = UPilotQuickStart.IsRepairing;
            Action<string> openSetup = message =>
            {
                Assert.That(message, Is.EqualTo(failure));
                Assert.That(UPilotQuickStart.LastRepairSucceeded, Is.False);
                calls.Add("setup");
            };
            Action<string, string> showDialog = (title, message) =>
            {
                Assert.That(title, Does.Contain("失败"));
                Assert.That(message, Does.Contain(failure));
                Assert.That(message, Does.Contain("已重新打开安装向导"));
                Assert.That(message, Does.Contain("实际只读调用验证通过后才算完成"));
                Assert.That(message, Does.Contain("不会自动重放"));
                calls.Add("dialog");
            };

            using (ExpectRepairError(attempt, failure))
            {
                UPilotQuickStart.ShowRepairFailureOnce(attempt, failure, openSetup, showDialog);
                UPilotQuickStart.ShowRepairFailureOnce(attempt, failure, openSetup, showDialog);
            }
            LogAssert.NoUnexpectedReceived();
            Assert.That(calls, Is.EqualTo(new[] { "setup", "dialog" }));
            Assert.That(EditorPrefs.GetString(RepairKey("Failure")), Is.EqualTo(failure));
            Assert.That(EditorPrefs.GetBool(RepairKey("Attempted")), Is.True);
            Assert.That(UPilotSetupState.IsCompleted, Is.EqualTo(previouslyConfigured));
            Assert.That(UPilotQuickStart.IsRepairing, Is.EqualTo(repairing));
            Assert.That(UPilotServerRestartDiagnostics.Current, Is.SameAs(record));

            using (ExpectRepairError(attempt + "-new", failure))
                UPilotQuickStart.ShowRepairFailureOnce(attempt + "-new", failure, openSetup, showDialog);
            Assert.That(calls, Is.EqualTo(new[] { "setup", "dialog", "setup", "dialog" }));
        }

        [Test]
        public void RecoveryFromFinalStepReturnsToPortsWithStartupEnabledAndHistoricalFailure()
        {
            var window = ScriptableObject.CreateInstance<UPilotMainWindow>();
            try
            {
                var bridge = UPilotBridge.Instance;
                var beforeWs = bridge.WsPort;
                var beforeHttp = bridge.HttpPort;
                var record = UPilotServerRestartDiagnostics.Current;
                EditorPrefs.SetBool(UPilotPreferences.SetupCompletedKey, false);
                SetField(window, "_setupInitialized", true);
                SetField(window, "_setupStep", 2);
                SetField(window, "_setupStartAfterSetup", false);
                SetField(window, "_setupCompletionMessage", "设置完成");
                SetField(window, "_setupScroll", new Vector2(0, 200));
                var dialogShown = false;
                const string failure = "identity_probe: occupied port";

                var attempt = "setup-recovery-test-" + Guid.NewGuid().ToString("N");
                using (ExpectRepairError(attempt, failure))
                    UPilotQuickStart.ShowRepairFailureOnce(attempt,
                        failure, window.EnterSetupRecovery, (title, message) =>
                        {
                            Assert.That(GetField(window, "_mainView").ToString(), Is.EqualTo("Setup"));
                            Assert.That(GetField(window, "_setupStep"), Is.EqualTo(0));
                            dialogShown = true;
                        });

                Assert.That(dialogShown, Is.True);
                Assert.That(GetField(window, "_setupStartAfterSetup"), Is.True);
                Assert.That(GetField(window, "_setupRecoveryFailure"), Is.EqualTo(failure));
                Assert.That(GetField(window, "_showSetupRecoveryFailure"), Is.False);
                Assert.That(GetField(window, "_setupCompletionMessage").ToString(), Does.Not.Contain(failure));
                Assert.That(GetField(window, "_setupCompletionMessageType"), Is.EqualTo(MessageType.Info));
                Assert.That(GetField(window, "_setupScroll"), Is.EqualTo(Vector2.zero));
                Assert.That(GetField(window, "_setupWsPort"), Is.EqualTo(beforeWs));
                Assert.That(GetField(window, "_setupHttpPort"), Is.EqualTo(beforeHttp));
                Assert.That(bridge.WsPort, Is.EqualTo(beforeWs));
                Assert.That(bridge.HttpPort, Is.EqualTo(beforeHttp));
                Assert.That(UPilotSetupState.IsCompleted, Is.False);
                Assert.That(UPilotServerRestartDiagnostics.Current, Is.SameAs(record));
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompletionRechecksOccupiedPortsBeforeWritingOrStarting(bool startAfterSetup)
        {
            var window = ScriptableObject.CreateInstance<UPilotMainWindow>();
            var ws = new TcpListener(IPAddress.Loopback, 0);
            var http = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                window.EnterSetupRecovery("previous failure");
                EditorPrefs.SetBool(UPilotPreferences.SetupCompletedKey, false);
                var beforeConfig = File.Exists(UPilotProjectConfig.ConfigPath)
                    ? File.ReadAllBytes(UPilotProjectConfig.ConfigPath) : null;
                var record = UPilotServerRestartDiagnostics.Current;
                var beforeWs = UPilotBridge.Instance.WsPort;
                var beforeHttp = UPilotBridge.Instance.HttpPort;
                ws.Start();
                http.Start();
                SetField(window, "_setupWsPort", ((IPEndPoint)ws.LocalEndpoint).Port);
                SetField(window, "_setupHttpPort", ((IPEndPoint)http.LocalEndpoint).Port);
                SetField(window, "_setupStep", 2);
                SetField(window, "_setupPortsReady", true);
                SetField(window, "_setupStartAfterSetup", startAfterSetup);

                typeof(UPilotMainWindow).GetMethod("CompleteSetupAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);

                Assert.That(GetField(window, "_mainView").ToString(), Is.EqualTo("Setup"));
                Assert.That(GetField(window, "_setupStep"), Is.EqualTo(0));
                Assert.That(GetField(window, "_setupPortsReady"), Is.False);
                Assert.That(GetField(window, "_setupCompletionRunning"), Is.False);
                Assert.That(GetField(window, "_setupCompletionMessageType"), Is.EqualTo(MessageType.Error));
                Assert.That(GetField(window, "_setupCompletionMessage").ToString(), Does.Contain("未启动服务"));
                Assert.That(UPilotSetupState.IsCompleted, Is.False);
                Assert.That(UPilotBridge.Instance.WsPort, Is.EqualTo(beforeWs));
                Assert.That(UPilotBridge.Instance.HttpPort, Is.EqualTo(beforeHttp));
                Assert.That(UPilotServerRestartDiagnostics.Current, Is.SameAs(record));
                Assert.That(File.Exists(UPilotProjectConfig.ConfigPath), Is.EqualTo(beforeConfig != null));
                if (beforeConfig != null)
                    Assert.That(File.ReadAllBytes(UPilotProjectConfig.ConfigPath), Is.EqualTo(beforeConfig));
            }
            finally
            {
                ws.Stop();
                http.Stop();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConfirmedPortsClearOnlyCurrentMessageAndPreserveRepairHistory(bool saveFails)
        {
            var window = ScriptableObject.CreateInstance<UPilotMainWindow>();
            var originalError = UPilotPortRegistration.LastError;
            try
            {
                const string history = "old restart: WS8770 port_release_timeout";
                window.EnterSetupRecovery(history);
                var record = UPilotServerRestartDiagnostics.Current;
                var config = File.ReadAllBytes(UPilotProjectConfig.ConfigPath);
                SetField(window, "_setupWsPort", 8869);
                SetField(window, "_setupHttpPort", 8017);
                SetField(window, "_setupCompletionMessage", "current save error");
                SetField(window, "_setupCompletionMessageType", MessageType.Error);
                UPilotPortRegistration.LastError = "current port error";
                var calls = 0;
                Action<string, int, int> save = (host, ws, http) =>
                {
                    calls++;
                    Assert.That(ws, Is.EqualTo(8869));
                    Assert.That(http, Is.EqualTo(8017));
                    if (saveFails) throw new IOException("injected save failure");
                };
                if (saveFails)
                    Assert.Throws<IOException>(() => window.SaveSetupPorts(save));
                else
                    window.SaveSetupPorts(save);
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(GetField(window, "_setupRecoveryFailure"), Is.EqualTo(history));
                Assert.That(GetField(window, "_setupCompletionMessage"),
                    Is.EqualTo(saveFails ? "current save error" : ""));
                Assert.That(GetField(window, "_setupCompletionMessageType"),
                    Is.EqualTo(saveFails ? MessageType.Error : MessageType.None));
                Assert.That(UPilotPortRegistration.LastError,
                    Is.EqualTo(saveFails ? "current port error" : ""));
                Assert.That(UPilotServerRestartDiagnostics.Current, Is.SameAs(record));
                Assert.That(File.ReadAllBytes(UPilotProjectConfig.ConfigPath), Is.EqualTo(config));
            }
            finally
            {
                UPilotPortRegistration.LastError = originalError;
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PortChecksOnlyObserveActualAvailabilityAndDoNotChangeReference(bool occupied)
        {
            var key = "upilot.setup-port-tests." + Guid.NewGuid().ToString("N");
            EditorPrefs.SetString(key, "0123456789abcdef:18001:19001");
            var window = ScriptableObject.CreateInstance<UPilotMainWindow>();
            var originalError = UPilotPortRegistration.LastError;
            var originalWarning = UPilotPortRegistration.LastWarning;
            UPilotPortRegistration.LastError = "";
            UPilotPortRegistration.LastWarning = "unchanged reference diagnostic";
            var config = File.ReadAllBytes(UPilotProjectConfig.ConfigPath);
            var manager = UPilotMcpServerManager.Instance;
            var beforeWs = manager.WsPort;
            var beforeHttp = manager.HttpPort;
            var wsListener = new TcpListener(IPAddress.Loopback, 0);
            var httpListener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                wsListener.Start();
                httpListener.Start();
                var ws = ((IPEndPoint)wsListener.LocalEndpoint).Port;
                var http = ((IPEndPoint)httpListener.LocalEndpoint).Port;
                httpListener.Stop();
                if (!occupied) wsListener.Stop();
                SetField(window, "_setupWsPort", ws);
                SetField(window, "_setupHttpPort", http);
                SetField(window, "_recommendedWsPort", 18003);
                SetField(window, "_recommendedHttpPort", 19003);
                window.EvaluateSetupPorts();
                Assert.That(GetField(window, "_setupPortsReady"), Is.EqualTo(!occupied));
                Assert.That(GetField(window, "_setupPortMessageType"),
                    Is.EqualTo(occupied ? MessageType.Error : MessageType.Info));
                Assert.That(GetField(window, "_setupPortMessage").ToString(),
                    Does.Contain(occupied ? "端口不可监听" : "当前端口可用"));
                if (!occupied)
                {
                    Assert.That(GetField(window, "_recommendedWsPort"), Is.Zero);
                    Assert.That(GetField(window, "_recommendedHttpPort"), Is.Zero);
                }
                var next = (double)GetField(window, "_nextSetupPortCheckAt");
                typeof(UPilotMainWindow).GetMethod("RefreshSetupPortsForRunningService",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                Assert.That(GetField(window, "_nextSetupPortCheckAt"), Is.EqualTo(next));
                window.EvaluateSetupPorts();
                Assert.That(GetField(window, "_setupPortsReady"), Is.EqualTo(!occupied));
                Assert.That(EditorPrefs.GetString(key), Is.EqualTo("0123456789abcdef:18001:19001"));
                Assert.That(UPilotPortRegistration.LastError, Is.Empty);
                Assert.That(UPilotPortRegistration.LastWarning, Is.EqualTo("unchanged reference diagnostic"));
                Assert.That(File.ReadAllBytes(UPilotProjectConfig.ConfigPath), Is.EqualTo(config));
                Assert.That(manager.WsPort, Is.EqualTo(beforeWs));
                Assert.That(manager.HttpPort, Is.EqualTo(beforeHttp));
            }
            finally
            {
                wsListener.Stop();
                httpListener.Stop();
                EditorPrefs.DeleteKey(key);
                UPilotPortRegistration.LastError = originalError;
                UPilotPortRegistration.LastWarning = originalWarning;
                UnityEngine.Object.DestroyImmediate(window);
            }
        }


        [Test]
        public void ExistingPythonSelectionPersistsPythonMode()
        {
            WithRuntimeConfig((path, original) =>
            {
                original.runtime.pythonPath = path;
                var window = ScriptableObject.CreateInstance<UPilotMainWindow>();
                try
                {
                    var mainView = typeof(UPilotMainWindow).GetField("_mainView", BindingFlags.Instance | BindingFlags.NonPublic);
                    mainView.SetValue(window, Enum.Parse(mainView.FieldType, "Setup"));
                    SetField(window, "_setupStep", 1);
                    window.SelectSetupPythonEnvironment(path);
                    var calls = new List<string>();
                    window.SelectExistingSetupPythonAsync(path, false, saved =>
                    {
                        Assert.That(calls, Is.EqualTo(new[] { "validate" }));
                        calls.Add("save");
                        typeof(UPilotProjectConfig).GetField("_cached", BindingFlags.Static | BindingFlags.NonPublic)
                            .SetValue(null, saved);
                    }, selected =>
                    {
                        Assert.That(selected, Is.EqualTo(path));
                        calls.Add("validate");
                        return Task.CompletedTask;
                    }).GetAwaiter().GetResult();
                    Assert.That(calls, Is.EqualTo(new[] { "validate", "save" }));
                    Assert.That(UPilotProjectConfig.Current.runtime.mode, Is.EqualTo("python"));
                    Assert.That(UPilotProjectConfig.Current.runtime.pythonPath, Is.EqualTo(path));
                    Assert.That(UPilotServerRuntimeService.Instance.GetConfiguredMode(), Is.EqualTo(UPilotServerRuntimeMode.Python));
                    Assert.That(GetField(window, "_setupRuntimeChoice").ToString(), Is.EqualTo("Python"));
                    Assert.That(GetField(window, "_setupStep"), Is.EqualTo(1));
                    Assert.That(original.runtime.mode, Is.EqualTo("exe"));
                }
                finally { UnityEngine.Object.DestroyImmediate(window); }
            });
        }

        [Test]
        public void ExplicitPythonSelectionValidatesExactPathBeforeSaving()
        {
            WithRuntimeConfig((path, original) =>
            {
                var calls = new List<string>();
                UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(UPilotServerRuntimeMode.Python,
                    "  " + path + "  ", saved =>
                    {
                        Assert.That(calls, Is.EqualTo(new[] { "validate" }));
                        calls.Add("save");
                        Assert.That(saved, Is.Not.SameAs(original));
                        Assert.That(saved.runtime.mode, Is.EqualTo("python"));
                        Assert.That(saved.runtime.pythonPath, Is.EqualTo(path));
                        Assert.That(saved.mcp.httpPort, Is.EqualTo(original.mcp.httpPort));
                    }, selected =>
                    {
                        Assert.That(selected, Is.EqualTo(path));
                        Assert.That(original.runtime.mode, Is.EqualTo("exe"));
                        calls.Add("validate");
                        return Task.CompletedTask;
                    }).GetAwaiter().GetResult();
                Assert.That(calls, Is.EqualTo(new[] { "validate", "save" }));
                Assert.That(original.runtime.mode, Is.EqualTo("exe"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PythonValidationFailureOrCancellationDoesNotSave(bool cancelled)
        {
            WithRuntimeConfig((path, original) =>
            {
                var before = JsonUtility.ToJson(original);
                Exception failure = cancelled ? new OperationCanceledException("cancelled") : new InvalidOperationException("dependencies missing");
                var task = UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(UPilotServerRuntimeMode.Python,
                    path, _ => Assert.Fail("Validation failure must not save."), _ => Task.FromException(failure));
                Assert.That(Assert.Catch(() => task.GetAwaiter().GetResult()), Is.SameAs(failure));
                Assert.That(JsonUtility.ToJson(UPilotProjectConfig.Current), Is.EqualTo(before));
            });
        }

        [Test]
        public void RuntimeSaveFailurePreservesCurrentConfiguration()
        {
            WithRuntimeConfig((path, original) =>
            {
                var before = JsonUtility.ToJson(original);
                var task = UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(UPilotServerRuntimeMode.Python,
                    path, saved =>
                    {
                        Assert.That(saved, Is.Not.SameAs(original));
                        Assert.That(saved.runtime, Is.Not.SameAs(original.runtime));
                        throw new IOException("injected save failure");
                    }, _ => Task.CompletedTask);
                Assert.Throws<IOException>(() => task.GetAwaiter().GetResult());
                Assert.That(UPilotProjectConfig.Current, Is.SameAs(original));
                Assert.That(JsonUtility.ToJson(original), Is.EqualTo(before));
            });
        }

        [TestCase(UPilotServerRuntimeMode.Python)]
        [TestCase(UPilotServerRuntimeMode.StandaloneExe)]
        public void RuntimeSelectionRejectsMissingAndRelativeFiles(UPilotServerRuntimeMode mode)
        {
            WithRuntimeConfig((path, original) =>
            {
                foreach (var selected in new[] { "python.exe", path + ".missing" })
                {
                    var task = UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(mode, selected,
                        _ => Assert.Fail("Invalid path must not save."), _ => throw new Exception("Must not probe another Python."));
                    Assert.Throws<FileNotFoundException>(() => task.GetAwaiter().GetResult());
                }
                Assert.That(UPilotProjectConfig.Current, Is.SameAs(original));
            });
        }

        [Test]
        public void ExplicitExeSelectionUsesRequestedFileWithoutPythonValidation()
        {
            WithRuntimeConfig((path, original) =>
            {
                var saves = 0;
                original.runtime.pythonPath = "preserved-python";
                UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(UPilotServerRuntimeMode.StandaloneExe,
                    path, saved =>
                    {
                        saves++;
                        Assert.That(saved.runtime.mode, Is.EqualTo("exe"));
                        Assert.That(saved.runtime.serverExePath, Is.EqualTo(path));
                        Assert.That(saved.runtime.serverVersion, Is.Empty);
                        Assert.That(saved.runtime.pythonPath, Is.EqualTo("preserved-python"));
                    }, _ => throw new Exception("EXE selection must not validate Python.")).GetAwaiter().GetResult();
                Assert.That(saves, Is.EqualTo(1));
                Assert.That(original.runtime.serverVersion, Is.EqualTo("old-version"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepairPreparationUsesOnlySelectedRoute(bool explicitSelection)
        {
            var calls = new List<string>();
            Func<Task> selected = () => { calls.Add("selected"); return Task.CompletedTask; };
            UPilotQuickStart.PrepareRepairRuntimeAsync(explicitSelection ? selected : null,
                () => { calls.Add("matching"); return Task.CompletedTask; }).GetAwaiter().GetResult();
            Assert.That(calls, Is.EqualTo(new[] { explicitSelection ? "selected" : "matching" }));
        }

        [Test]
        public void ExplicitPreparationFailureDoesNotFallBackToManagedDownload()
        {
            Assert.Throws<IOException>(() => UPilotQuickStart.PrepareRepairRuntimeAsync(
                () => Task.FromException(new IOException("selected runtime failed")),
                () => throw new Exception("Must not download a replacement.")).GetAwaiter().GetResult());
        }

        [Test]
        public void BusyRuntimeSwitchIsRejectedBeforePreparingOrSaving()
        {
            var field = typeof(UPilotQuickStart).GetField("_repairTask", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = field.GetValue(null);
            var pending = new TaskCompletionSource<string>();
            WithRuntimeConfig((path, original) =>
            {
                try
                {
                    field.SetValue(null, pending.Task);
                    Assert.That(UPilotQuickStart.CanSwitchRuntime, Is.False);
                    Assert.Throws<InvalidOperationException>(() =>
                        UPilotQuickStart.ApplyRuntimeAndRestartAsync(UPilotServerRuntimeMode.Python, path));
                    Assert.That(UPilotProjectConfig.Current, Is.SameAs(original));
                    Assert.That(field.GetValue(null), Is.SameAs(pending.Task));
                }
                finally { field.SetValue(null, previous); }
            });
        }

        [Test]
        public void StoppedSwitchDoesNotPersistValidatedRuntime()
        {
            var field = typeof(UPilotQuickStart).GetField("_explicitlyStopped", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = field.GetValue(null);
            WithRuntimeConfig((path, original) =>
            {
                try
                {
                    field.SetValue(null, true);
                    var task = UPilotServerRuntimeService.Instance.ApplyRuntimeSelectionAsync(UPilotServerRuntimeMode.Python,
                        path, UPilotQuickStart.SaveSelectedRuntime, _ => Task.CompletedTask);
                    Assert.Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult());
                    Assert.That(UPilotProjectConfig.Current, Is.SameAs(original));
                }
                finally { field.SetValue(null, previous); }
            });
        }

        [TestCase(UPilotServerRuntimeMode.Python, "本机 Python")]
        [TestCase(UPilotServerRuntimeMode.StandaloneExe, "本地 EXE")]
        public void RuntimeSelectionLabelReflectsConfiguredMode(UPilotServerRuntimeMode mode, string expected)
        {
            Assert.That(UPilotMainWindow.GetRuntimeSelectionModeLabel(mode), Is.EqualTo(expected));
        }

        // 同步作用域内替换缓存，保存用委托观察；不触碰真实运行配置或端口登记。
        private static void WithRuntimeConfig(Action<string, UPilotProjectConfigData> action)
        {
            var field = typeof(UPilotProjectConfig).GetField("_cached", BindingFlags.Static | BindingFlags.NonPublic);
            var previous = UPilotProjectConfig.Current;
            var disk = File.ReadAllBytes(UPilotProjectConfig.ConfigPath);
            var config = JsonUtility.FromJson<UPilotProjectConfigData>(JsonUtility.ToJson(previous));
            config.runtime = new UPilotRuntimeConfig { mode = "exe", serverExePath = "old-server", serverVersion = "old-version" };
            var path = Path.GetTempFileName();
            try
            {
                field.SetValue(null, config);
                action(path, config);
                Assert.That(File.ReadAllBytes(UPilotProjectConfig.ConfigPath), Is.EqualTo(disk));
            }
            finally
            {
                field.SetValue(null, previous);
                File.Delete(path);
            }
        }

        private static object GetField(UPilotMainWindow window, string name) =>
            typeof(UPilotMainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);

        private static void SetField(UPilotMainWindow window, string name, object value) =>
            typeof(UPilotMainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, value);

        private static void SetLastRepairSucceeded(bool value) =>
            typeof(UPilotQuickStart).GetProperty(nameof(UPilotQuickStart.LastRepairSucceeded),
                BindingFlags.Static | BindingFlags.NonPublic).GetSetMethod(true).Invoke(null, new object[] { value });
        [Test]
        public void HealthDeadlineShowsDiagnosticOnceWithoutSetupOrPortAdvice()
        {
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests("test", 41, "old");
            record.operationId = "health-recovery-test-" + Guid.NewGuid().ToString("N");
            record.status = "failed";
            record.errorCode = "restart_verification_timeout";
            record.gateDiagnostics = new[] { new UPilotRestartGate { key = "health", state = "failed" } };
            // Explicit evidence that all preceding gates passed.
            record.newProcessId = 42;
            record.newProcessStartedAtUtcMs = 2;
            record.portsReleasedAtUtcMs = 2;
            record.hasIdentityProbe = true;
            record.verifiedCount = 1;
            record.hasBridgeStopConfirmation = record.bridgeStopConfirmed = true;
            record.hasOldServerExitConfirmation = record.oldServerExitConfirmed = true;
            var gates = new List<UPilotRestartGate>();
            foreach (var key in UPilotServerRestartDiagnostics.GateKeys)
                gates.Add(new UPilotRestartGate { key = key, state = key == "health" ? "failed" :
                    Array.IndexOf(UPilotServerRestartDiagnostics.GateKeys, key) < 5 ? "passed" : "pending" });
            record.gateDiagnostics = gates.ToArray();
            Assert.That(UPilotServerRestartDiagnostics.IsHealthFailure(record), Is.True);
            var dialogs = 0;
            Action<string> setup = _ => Assert.Fail("Health failure must not open setup.");
            Action<string, string> dialog = (_, message) =>
            {
                dialogs++;
                Assert.That(message, Does.Contain("服务健康检查异常"));
                Assert.That(message, Does.Not.Contain("端口占用"));
                Assert.That(message, Does.Not.Contain("已重新打开安装向导"));
            };
            using (ExpectRepairError(record.operationId, UPilotServerRestartDiagnostics.HealthTimeoutMessage))
                for (var i = 0; i < 2; i++)
                    UPilotQuickStart.ShowRepairFailureOnce(record.operationId, UPilotServerRestartDiagnostics.HealthTimeoutMessage,
                        setup, dialog, record);
            LogAssert.NoUnexpectedReceived();
            Assert.That(dialogs, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepairFailureLogsOriginalDiagnosisBeforeUiFailure(bool matchingRecord)
        {
            var attempt = "repair-log-test-" + Guid.NewGuid().ToString("N");
            const string failure = "process_started\n原始修复错误";
            var record = UPilotServerRestartDiagnostics.CreateRecordForTests("test", 41, "old");
            record.operationId = matchingRecord ? attempt : "unrelated-history";
            record.status = "failed";
            record.failurePhase = "process_started";
            record.errorCode = "process_identity_mismatch";
            record.error = "diagnostic-original-error";
            record.launcherProcessId = record.newProcessId = 101;
            record.healthProcessId = 202;
            record.gateDiagnostics = new[] { new UPilotRestartGate { key = "identity", state = "failed" } };
            string logged = null;
            Application.LogCallback capture = (message, stack, type) =>
            {
                if (type == LogType.Error && message.StartsWith(
                    ExpectedFailurePrefix + "[UPilot] 自动修复失败", StringComparison.Ordinal))
                    logged = message;
            };
            Application.logMessageReceived += capture;
            try
            {
                using (ExpectRepairError(attempt, failure))
                {
                    var exception = Assert.Throws<InvalidOperationException>(() =>
                        UPilotQuickStart.ShowRepairFailureOnce(attempt, failure, _ =>
                        {
                            // UI 失败前已经输出；不会把另一操作的历史错误归到本次尝试。
                            Assert.That(logged, Does.Contain(failure).And.Contain(attempt));
                            if (matchingRecord)
                            {
                                Assert.That(logged, Does.Contain("process_identity_mismatch"));
                                Assert.That(logged, Does.Contain("diagnostic-original-error"));
                                Assert.That(logged, Does.Contain("101").And.Contain("202"));
                                Assert.That(logged, Does.Contain("PID 与项目身份验证"));
                            }
                            else
                            {
                                Assert.That(logged, Does.Contain("尚无对应重启 operation ID"));
                                Assert.That(logged, Does.Not.Contain("unrelated-history"));
                                Assert.That(logged, Does.Not.Contain("diagnostic-original-error"));
                            }
                            throw new InvalidOperationException("injected recovery UI failure");
                        }, (_, __) => Assert.Fail("Failed setup must not reach dialog."), record));
                    Assert.That(exception.Message, Is.EqualTo("injected recovery UI failure"));
                    UPilotQuickStart.ShowRepairFailureOnce(attempt, failure,
                        _ => Assert.Fail("Duplicate setup."), (_, __) => Assert.Fail("Duplicate dialog."), record);
                }
                LogAssert.NoUnexpectedReceived();
            }
            finally { Application.logMessageReceived -= capture; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExpectedRepairErrorLabelIsScopedAndLeavesOtherLogsUntouched(bool throwInScope)
        {
            var original = Debug.unityLogger.logHandler;
            var sink = new RecordingLogHandler();
            const string attempt = "label-test-attempt";
            const string failure = "injected failure";
            var message = "[UPilot] 自动修复失败\n" + failure + "\n\n尝试 ID：" + attempt + "（测试）";
            var exception = new InvalidOperationException("injected scope exit");
            try
            {
                // 只用内存接收器验证非预期错误原样转发，不制造新的真实 Console 故障。
                Debug.unityLogger.logHandler = sink;
                ExpectedRepairErrorLogScope scope = null;
                Action exercise = () =>
                {
                    using (scope = new ExpectedRepairErrorLogScope(RepairErrorPattern(attempt, failure)))
                    {
                        scope.LogFormat(LogType.Error, null, "{0}", message);
                        scope.LogFormat(LogType.Warning, null, "{0}", message);
                        scope.LogFormat(LogType.Error, null, "{0}", message.Replace(attempt, attempt + "-other"));
                        scope.LogFormat(LogType.Error, null, "{0}", message.Replace(failure, "unexpected failure"));
                        scope.LogException(exception, null);
                        if (throwInScope) throw exception;
                    }
                };
                if (throwInScope)
                    Assert.That(Assert.Throws<InvalidOperationException>(() => exercise()), Is.SameAs(exception));
                else
                    exercise();
                Assert.That(Debug.unityLogger.logHandler, Is.SameAs(sink));
                scope.Dispose();
                scope.LogFormat(LogType.Error, null, "{0}", message);
                Assert.That(sink.Entries, Is.EqualTo(new[]
                {
                    (LogType.Error, ExpectedFailurePrefix + message),
                    (LogType.Warning, message),
                    (LogType.Error, message.Replace(attempt, attempt + "-other")),
                    (LogType.Error, message.Replace(failure, "unexpected failure")),
                    (LogType.Error, message)
                }));
                Assert.That(sink.Exception, Is.SameAs(exception));
                Assert.That(Debug.unityLogger.logHandler, Is.SameAs(sink));
            }
            finally { Debug.unityLogger.logHandler = original; }
        }

        [Test]
        public void SetupRecommendationChangesBothPortsWithoutReusingHistoryOrSaving()
        {
            WithSetupPortRecommendation((window, registry, key) =>
            {
                var used = new HashSet<int> { 18000, 19000 };
                for (var i = 0; i < 20; i++)
                {
                    // A stale cached suggestion must never determine the next pair.
                    SetField(window, "_recommendedWsPort", 18000);
                    SetField(window, "_recommendedHttpPort", 19000);
                    Assert.That(window.ApplyRecommendedSetupPorts(registry), Is.True);
                    var ws = (int)GetField(window, "_setupWsPort");
                    var http = (int)GetField(window, "_setupHttpPort");
                    Assert.That(used.Add(ws), Is.True, "WS reused an earlier port.");
                    Assert.That(used.Add(http), Is.True, "HTTP reused an earlier port.");
                    Assert.That(UPilotPortAllocator.IsPortAvailable(ws), Is.True);
                    Assert.That(UPilotPortAllocator.IsPortAvailable(http), Is.True);
                    window.EvaluateSetupPorts();
                    Assert.That(GetField(window, "_setupWsPort"), Is.EqualTo(ws));
                    Assert.That(GetField(window, "_setupHttpPort"), Is.EqualTo(http));
                    SetField(window, "_setupStep", 1);
                    SetField(window, "_setupStep", 0);
                    if (i == 10)
                    {
                        EditorPrefs.SetString(key, "other:" + ws + ":" + http);
                        LogAssert.Expect(LogType.Log, "[UPilotPorts] 已清除参考端口登记：" + key + "；工程配置和运行中的服务不变。");
                        registry.Clear();
                    }
                    Assert.That(EditorPrefs.HasKey(key), Is.False, "Recommendation must not register draft ports.");
                }
            });
        }

        [TestCase(65535, 65535)]
        [TestCase(0, 0)]
        [TestCase(-1, 65536)]
        [TestCase(18000, 18001)]
        public void SetupRecommendationHandlesBoundaryInvalidAndOverlappingInputs(int initialWs, int initialHttp)
        {
            WithSetupPortRecommendation((window, registry, key) =>
            {
                SetField(window, "_setupWsPort", initialWs);
                SetField(window, "_setupHttpPort", initialHttp);
                Assert.That(window.ApplyRecommendedSetupPorts(registry), Is.True);
                var ws = (int)GetField(window, "_setupWsPort");
                var http = (int)GetField(window, "_setupHttpPort");
                Assert.That(ws, Is.InRange(1, 65535).And.Not.EqualTo(initialWs).And.Not.EqualTo(initialHttp));
                Assert.That(http, Is.InRange(1, 65535).And.Not.EqualTo(initialWs).And.Not.EqualTo(initialHttp).And.Not.EqualTo(ws));
            });
        }

        [Test]
        public void SetupRecommendationFailureRetainsInputsAndCanRetry()
        {
            WithSetupPortRecommendation((window, registry, key) =>
            {
                var reject = true;
                var unavailable = new UPilotPortRegistry(key, port => !reject && UPilotPortAllocator.IsPortAvailable(port));
                SetField(window, "_setupPortsReady", true);
                LogAssert.Expect(LogType.Error, new Regex(@"\[UPilotPorts\] 推荐可绑定端口失败：[\s\S]*本次搜索范围未找到可用端口"));
                Assert.That(window.ApplyRecommendedSetupPorts(unavailable), Is.False);
                Assert.That(GetField(window, "_setupWsPort"), Is.EqualTo(18000));
                Assert.That(GetField(window, "_setupHttpPort"), Is.EqualTo(19000));
                Assert.That(GetField(window, "_setupPortsReady"), Is.True);
                Assert.That(GetField(window, "_recommendedWsPort"), Is.Zero);
                Assert.That(GetField(window, "_setupStep"), Is.Zero);
                reject = false;
                Assert.That(window.ApplyRecommendedSetupPorts(unavailable), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SetupRecommendationRegistrationFailureIsVisibleAndRetryable(bool corrupt)
        {
            WithSetupPortRecommendation((window, registry, key) =>
            {
                var failRead = !corrupt;
                var observed = new UPilotPortRegistry(key, readRegistration: pref => failRead
                    ? throw new IOException("injected registration read failure") : EditorPrefs.GetString(pref, ""));
                if (corrupt) EditorPrefs.SetString(key, "broken");
                LogAssert.Expect(LogType.Error, new Regex(@"\[UPilotPorts\] 推荐可绑定端口失败：[\s\S]*" +
                    (corrupt ? "端口登记格式损坏" : "injected registration read failure")));
                Assert.That(window.ApplyRecommendedSetupPorts(observed), Is.False);
                Assert.That(GetField(window, "_setupWsPort"), Is.EqualTo(18000));
                Assert.That(GetField(window, "_setupHttpPort"), Is.EqualTo(19000));
                Assert.That(GetField(window, "_setupPortMessageType"), Is.EqualTo(MessageType.Error));
                failRead = false;
                EditorPrefs.DeleteKey(key);
                Assert.That(window.ApplyRecommendedSetupPorts(observed), Is.True);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SetupRecommendationRechecksRegistrationAndActualBindingBeforeSaving(bool registered)
        {
            WithSetupPortRecommendation((window, registry, key) =>
            {
                Assert.That(window.ApplyRecommendedSetupPorts(registry), Is.True);
                var ws = (int)GetField(window, "_setupWsPort");
                var http = (int)GetField(window, "_setupHttpPort");
                var listener = new TcpListener(IPAddress.Loopback, ws);
                try
                {
                    if (registered) EditorPrefs.SetString(key, "other:" + http + ":" + ws);
                    else listener.Start();
                    var saves = 0;
                    Action<string, int, int> save = (host, savedWs, savedHttp) =>
                    {
                        saves++;
                        Assert.That((savedWs, savedHttp), Is.EqualTo((ws, http)));
                    };
                    LogAssert.Expect(LogType.Error, new Regex(@"\[UPilotPorts\] 保存前校验推荐端口失败：[\s\S]*" +
                        (registered ? "已被登记" : "当前不可绑定")));
                    Assert.That(window.TrySaveSetupPorts(registry, save), Is.False);
                    Assert.That(saves, Is.Zero);
                    Assert.That(GetField(window, "_setupWsPort"), Is.EqualTo(ws));
                    Assert.That(GetField(window, "_setupHttpPort"), Is.EqualTo(http));
                    Assert.That(GetField(window, "_setupStep"), Is.Zero);
                    listener.Stop();
                    EditorPrefs.DeleteKey(key);
                    Assert.That(window.TrySaveSetupPorts(registry, save), Is.True);
                    Assert.That(saves, Is.EqualTo(1));
                }
                finally { listener.Stop(); }
            });
        }

        [Test]
        public void SetupManualPortEditPreservesExistingSavePolicyWithBrokenRegistration()
        {
            WithSetupPortRecommendation((window, registry, key) =>
            {
                Assert.That(window.ApplyRecommendedSetupPorts(registry), Is.True);
                var ws = (int)GetField(window, "_setupWsPort");
                var http = (int)GetField(window, "_setupHttpPort");
                SetField(window, "_setupWsPort", http);
                SetField(window, "_setupHttpPort", ws);
                EditorPrefs.SetString(key, "broken");
                var saves = 0;
                Assert.That(window.TrySaveSetupPorts(registry, (host, savedWs, savedHttp) =>
                {
                    saves++;
                    Assert.That((savedWs, savedHttp), Is.EqualTo((http, ws)));
                }), Is.True);
                Assert.That(saves, Is.EqualTo(1));
                Assert.That(EditorPrefs.GetString(key), Is.EqualTo("broken"));
            });
        }

        [Test]
        public void SetupRecommendationHistoryResetsOnlyWhenSetupIsReinitialized()
        {
            WithSetupPortRecommendation((window, registry, key) =>
            {
                Assert.That(window.ApplyRecommendedSetupPorts(registry), Is.True);
                var first = ((int)GetField(window, "_setupWsPort"), (int)GetField(window, "_setupHttpPort"));
                window.EnterSetupRecovery("test reinitialization");
                SetField(window, "_setupWsPort", 18000);
                SetField(window, "_setupHttpPort", 19000);
                Assert.That(window.ApplyRecommendedSetupPorts(registry), Is.True);
                Assert.That(((int)GetField(window, "_setupWsPort"), (int)GetField(window, "_setupHttpPort")), Is.EqualTo(first));
            });
        }

        private static void WithSetupPortRecommendation(Action<UPilotMainWindow, UPilotPortRegistry, string> test)
        {
            var key = "upilot.setup-recommendation-tests." + Guid.NewGuid().ToString("N");
            var config = File.ReadAllBytes(UPilotProjectConfig.ConfigPath);
            var manager = UPilotMcpServerManager.Instance;
            var endpoints = (manager.WsPort, manager.HttpPort);
            var originalError = UPilotPortRegistration.LastError;
            var originalWarning = UPilotPortRegistration.LastWarning;
            var window = ScriptableObject.CreateInstance<UPilotMainWindow>();
            try
            {
                SetField(window, "_setupWsPort", 18000);
                SetField(window, "_setupHttpPort", 19000);
                test(window, new UPilotPortRegistry(key), key);
                Assert.That(File.ReadAllBytes(UPilotProjectConfig.ConfigPath), Is.EqualTo(config));
                Assert.That((manager.WsPort, manager.HttpPort), Is.EqualTo(endpoints));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                EditorPrefs.DeleteKey(key);
                UPilotPortRegistration.LastError = originalError;
                UPilotPortRegistration.LastWarning = originalWarning;
            }
        }

        private static string RepairErrorPattern(string attempt, string failure) =>
            "^\\[UPilot\\] 自动修复失败\\n" + Regex.Escape(failure) +
            "\\n\\n[\\s\\S]*" + Regex.Escape(attempt) + "(?=[\\s（]|$)";

        private static IDisposable ExpectRepairError(string attempt, string failure)
        {
            var pattern = RepairErrorPattern(attempt, failure);
            LogAssert.Expect(LogType.Error, new Regex("^" + Regex.Escape(ExpectedFailurePrefix) + pattern.Substring(1)));
            return new ExpectedRepairErrorLogScope(pattern);
        }

        // 标识仅在同步测试调用期间生效；不吞日志、不降级、不放宽 LogAssert 的次数校验。
        private sealed class ExpectedRepairErrorLogScope : ILogHandler, IDisposable
        {
            private readonly ILogHandler _previous;
            private readonly Regex _expected;
            private bool _disposed;

            public ExpectedRepairErrorLogScope(string pattern)
            {
                _expected = new Regex(pattern);
                _previous = Debug.unityLogger.logHandler;
                Debug.unityLogger.logHandler = this;
            }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                if (!_disposed && logType == LogType.Error && _expected.IsMatch(string.Format(format, args)))
                    format = ExpectedFailurePrefix + format;
                _previous.LogFormat(logType, context, format, args);
            }

            public void LogException(Exception exception, UnityEngine.Object context) =>
                _previous.LogException(exception, context);

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (ReferenceEquals(Debug.unityLogger.logHandler, this))
                    Debug.unityLogger.logHandler = _previous;
            }
        }

        private sealed class RecordingLogHandler : ILogHandler
        {
            public readonly List<(LogType type, string message)> Entries = new();
            public Exception Exception;

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) =>
                Entries.Add((logType, string.Format(format, args)));

            public void LogException(Exception exception, UnityEngine.Object context) => Exception = exception;
        }

    }
}
