using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotSetupRecoveryTests
    {
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

            UPilotQuickStart.ShowRepairFailureOnce(attempt, failure, openSetup, showDialog);
            UPilotQuickStart.ShowRepairFailureOnce(attempt, failure, openSetup, showDialog);
            Assert.That(calls, Is.EqualTo(new[] { "setup", "dialog" }));
            Assert.That(EditorPrefs.GetString(RepairKey("Failure")), Is.EqualTo(failure));
            Assert.That(EditorPrefs.GetBool(RepairKey("Attempted")), Is.True);
            Assert.That(UPilotSetupState.IsCompleted, Is.EqualTo(previouslyConfigured));
            Assert.That(UPilotQuickStart.IsRepairing, Is.EqualTo(repairing));
            Assert.That(UPilotServerRestartDiagnostics.Current, Is.SameAs(record));

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

                UPilotQuickStart.ShowRepairFailureOnce("setup-recovery-test-" + Guid.NewGuid().ToString("N"),
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
            var generationField = typeof(UPilotPortRegistration).GetField("_configurationGeneration",
                BindingFlags.NonPublic | BindingFlags.Static);
            var generation = generationField.GetValue(null);
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
                Assert.That((long)generationField.GetValue(null),
                    Is.EqualTo((long)generation + (saveFails ? 0 : 1)));
                Assert.That(UPilotServerRestartDiagnostics.Current, Is.SameAs(record));
                Assert.That(File.ReadAllBytes(UPilotProjectConfig.ConfigPath), Is.EqualTo(config));
            }
            finally
            {
                generationField.SetValue(null, generation);
                UPilotPortRegistration.LastError = originalError;
                UnityEngine.Object.DestroyImmediate(window);
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
            for (var i = 0; i < 2; i++)
                UPilotQuickStart.ShowRepairFailureOnce(record.operationId, UPilotServerRestartDiagnostics.HealthTimeoutMessage,
                    setup, dialog, record);
            Assert.That(dialogs, Is.EqualTo(1));
        }

    }
}
