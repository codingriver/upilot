// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Manager = CodingRiver.UPilot.UPilotMcpServerManager;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotExistingServiceRecoveryChainTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
        private const string LaunchKey = "UPilot.ServerLaunchIdentity.v1";
        private const string StopKey = "UPilot.ServiceStopObservation.v1";
        private const string RecoveryKey = "UPilot.ServerRecovery.Existing.v1";
        private readonly Dictionary<string, string> _savedSession = new Dictionary<string, string>();
        private readonly List<Process> _launchers = new List<Process>();
        private readonly List<Manager.ServerStopTarget> _children = new List<Manager.ServerStopTarget>();
        private Manager _manager;
        private Manager.ServerStopScope _scope;
        private Manager.ExistingServiceRecoveryRecord _record;
        private ServerLaunchSpec _spec;
        private CancellationTokenSource _probeCancellation;
        private Task _recoveryWork, _probeWork;
        private Application.LogCallback _stopTransition;
        private object _startupRecord, _startupPath;
        private string _missing, _directory, _witness;
        private BridgeStatus _bridgeBefore;
        private int _actualHttpPort, _actualWsPort;
        private Process _actualServer;
        private Exception _replacementError;
        private int _replacementCalls;
        private bool _stateSaved;

        [SetUp]
        public void SetUp()
        {
            _stateSaved = false; _manager = null; _scope = null; _directory = null;
            _actualServer = null; _stopTransition = null;
            Assert.That(Application.platform, Is.EqualTo(RuntimePlatform.WindowsEditor));
            Assert.That(UPilotDeploymentDiagnostics.SamePath(UPilotProjectConfig.ProjectRoot,
                Path.Combine(UPilotDeploymentDiagnostics.PackageRoot, "Tests~", "UPilotTest")), Is.True);
            Assert.That(UPilotQuickStart.IsExplicitlyStopped, Is.False);
            _missing = "missing-" + Guid.NewGuid().ToString("N");
            _savedSession.Clear();
            foreach (var key in new[] { LaunchKey, StopKey, RecoveryKey })
                _savedSession.Add(key, SessionState.GetString(key, _missing));
            _startupRecord = StartupField("s_record").GetValue(null);
            _startupPath = StartupField("s_path").GetValue(null);
            _stateSaved = true;
            StartupField("s_record").SetValue(null, new UPilotStartupRecord { failureSummaryLogged = true });
            StartupField("s_path").SetValue(null, null);
            _bridgeBefore = UPilotBridge.Instance.GetStatus();
            _actualHttpPort = UPilotBridge.Instance.HttpPort;
            _actualWsPort = UPilotBridge.Instance.WsPort;
            var actualPid = Manager.Instance.GetStatus().ProcessId.GetValueOrDefault();
            if (actualPid > 0) { _actualServer = Process.GetProcessById(actualPid); var handle = _actualServer.Handle; }
            _directory = Path.Combine(UPilotProjectConfig.ProjectRoot, "Library", "UPilot", "RecoveryChainTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            var script = Path.Combine(_directory, "owned_residual.py");
            _witness = Path.Combine(_directory, "starts.jsonl");
            File.WriteAllText(script,
                "import argparse,json,os,socket,time\n" +
                "p=argparse.ArgumentParser();p.add_argument('--http-port',type=int);p.add_argument('--port',type=int);p.add_argument('--witness');a,_=p.parse_known_args()\n" +
                "listeners=[]\n" +
                "for port in (a.http_port,a.port):\n" +
                " s=socket.socket();s.bind(('127.0.0.1',port));s.listen(64);listeners.append(s)\n" +
                "with open(a.witness,'a',encoding='utf-8') as f:f.write(json.dumps({'pid':os.getpid(),'at':int(time.time()*1000)})+'\\n');f.flush()\n" +
                "while True:time.sleep(0.1)\n", new UTF8Encoding(false));
            var http = FreePort();
            var ws = FreePort();
            while (ws == http || http == UPilotBridge.Instance.HttpPort || ws == UPilotBridge.Instance.WsPort)
            { http = FreePort(); ws = FreePort(); }
            _scope = new Manager.ServerStopScope(UPilotProjectConfig.ProjectRoot, script, "", http, ws);
            _manager = (Manager)FormatterServices.GetUninitializedObject(typeof(Manager));
            Set("_statusLock", new object());
            Set("_stoppingProcesses", new List<Process>());
            Set("_initialServiceRecoveryCompleted", true);
            Set("_restartOperationId", "");
            Set("_pythonEntryPath", script);
            Set("_autoStart", true);
            Set("_startAttemptGeneration", 13);
            Set("_restartGeneration", 17L);
            _manager.EndpointOverride = _scope;
            _manager.ExistingServiceShutdownOverride = _ => { };
            _replacementCalls = 0; _replacementError = null;
            _recoveryWork = _probeWork = null;
            _probeCancellation = new CancellationTokenSource();
            var python = UPilotProjectConfig.Current.runtime?.pythonPath;
            if (string.IsNullOrEmpty(python) || !Path.IsPathRooted(python) || !File.Exists(python))
                python = Path.Combine(UPilotDeploymentDiagnostics.PackageRoot, "upilotserver~", ".venv", "Scripts", "python.exe");
            Assert.That(File.Exists(python), Is.True, "A current Python runtime is required; the fixture never changes runtime configuration.");
            _spec = ServerLaunchSpec.Create(UPilotServerRuntimeMode.Python, python, script, "", _scope.ProjectPath,
                _directory, http, ws, "INFO", UPilotDeploymentDiagnostics.Channel, UPilotDeploymentDiagnostics.InstallSource, Now + 10000);
            _spec.arguments = Quote(script) + " --http-port " + http + " --port " + ws +
                " --log-file " + Quote(Path.Combine(_directory, "unused.log")) + " --witness " + Quote(_witness);
        }

        [TearDown]
        public async Task TearDown()
        {
            if (!_stateSaved) return;
            try
            {
                if (_manager == null) return;
                if (_stopTransition != null) Application.logMessageReceived -= _stopTransition;
                _stopTransition = null;
                Get<CancellationTokenSource>("_existingServiceRecoveryCancellation")?.Cancel();
                _probeCancellation?.Cancel();
                try { if (_recoveryWork != null) await _recoveryWork; }
                finally
                {
                    try { if (_probeWork != null) await _probeWork; }
                    finally
                    {
                        Call("StopLaunchObservation");
                        if (_manager.HasActiveStopObservation) _manager.ResetStopObservation("fixture_finished");
                        var preparation = Get<Task>("_stopPreparation");
                        if (preparation != null) await preparation;
                        var ownedLauncher = Get<Process>("_launcherProcess");
                        if (ownedLauncher != null && !_launchers.Contains(ownedLauncher)) _launchers.Add(ownedLauncher);
                        foreach (var child in _children)
                        {
                            if (child.HasExited) continue;
                            try { child.Terminate(); }
                            catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { }
                        }
                        foreach (var process in _launchers)
                        {
                            if (process.HasExited) continue;
                            try { process.Kill(); }
                            catch (InvalidOperationException) when (process.HasExited) { }
                            catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { }
                        }
                        var cleanupDeadline = Now + 5000;
                        while (Now < cleanupDeadline && (_children.Exists(child => !child.HasExited) ||
                            _launchers.Exists(process => !process.HasExited))) await Task.Delay(10);
                        Assert.That(_children.TrueForAll(child => child.HasExited), Is.True, "Every owned Python worker must exit during fixture cleanup.");
                        Assert.That(_launchers.TrueForAll(process => process.HasExited), Is.True, "Every owned launcher must exit during fixture cleanup.");
                        Assert.That(UPilotPortAllocator.IsPortAvailable(_scope.HttpPort) && UPilotPortAllocator.IsPortAvailable(_scope.WsPort), Is.True);
                    }
                }
                Assert.That(UPilotBridge.Instance.HttpPort, Is.EqualTo(_actualHttpPort));
                Assert.That(UPilotBridge.Instance.WsPort, Is.EqualTo(_actualWsPort));
                Assert.That(UPilotBridge.Instance.GetStatus().SessionId, Is.EqualTo(_bridgeBefore.SessionId));
                if (_actualServer != null) Assert.That(_actualServer.HasExited, Is.False, "The actual MCP Server must remain alive.");
            }
            finally
            {
                if (_manager != null)
                {
                    Call("StopLaunchObservation");
                    Call("ReleaseRecoveredServiceAttachment", false);
                    Get<Process>("_launcherProcess")?.Dispose();
                    Call("DisposeTrackedProcess");
                    var lifecycle = Get<CancellationTokenSource>("_lifecycleQueryCancellation");
                    lifecycle?.Cancel(); lifecycle?.Dispose();
                }
                foreach (var child in _children) child.Dispose();
                foreach (var process in _launchers) process.Dispose();
                _children.Clear(); _launchers.Clear();
                _actualServer?.Dispose(); _actualServer = null;
                _probeCancellation?.Dispose();
                StartupField("s_record").SetValue(null, _startupRecord);
                StartupField("s_path").SetValue(null, _startupPath);
                foreach (var item in _savedSession)
                {
                    if (item.Value == _missing) SessionState.EraseString(item.Key);
                    else SessionState.SetString(item.Key, item.Value);
                }
                if (_directory != null && Directory.Exists(_directory)) Directory.Delete(_directory, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NormalFallbackReclaimsOwnedResidualAndStartsAtMostOnce(bool disableAutoStartAfterStop)
        {
            var oldLauncher = Process.Start(_spec.StartInfo());
            Assert.That(oldLauncher, Is.Not.Null);
            var oldHandle = oldLauncher.Handle;
            _launchers.Add(oldLauncher);
            await WaitForWitness(1, Now + 5000);
            var oldChild = _children[0];
            var started = Now;
            _record = Record("verifying", started + 20000);
            _record.automatic = true;
            var parentDeadline = _record.deadline;
            var reuseDeadline = _record.reuseDeadline;
            var disabledAfterConfirmedStop = false;
            _stopTransition = (message, stack, type) => {
                var stop = _manager.StopObservation;
                if (!disableAutoStartAfterStop || type != LogType.Log || stop?.status != "succeeded" ||
                    !stop.processesExited || !stop.portsAvailable || !message.Contains(stop.id)) return;
                Set("_autoStart", false);
                disabledAfterConfirmedStop = true;
            };
            Application.logMessageReceived += _stopTransition;
            _manager.ExistingServiceReplacementStartOverride = record => {
                try
                {
                    ++_replacementCalls;
                    var stop = _manager.StopObservation;
                    Assert.That(stop.status, Is.EqualTo("succeeded"));
                    Assert.That(stop.processesExited && stop.portsAvailable, Is.True);
                    Assert.That(oldChild.HasExited && oldLauncher.HasExited, Is.True);
                    Assert.That(UPilotPortAllocator.IsPortAvailable(_scope.HttpPort) && UPilotPortAllocator.IsPortAvailable(_scope.WsPort), Is.True);
                    var persisted = JsonUtility.FromJson<Manager.ExistingServiceRecoveryRecord>(SessionState.GetString(RecoveryKey, ""));
                    Assert.That(persisted.id, Is.EqualTo(record.id));
                    Assert.That(persisted.status, Is.EqualTo("replacement_starting"));
                    Assert.That(persisted.replacementStarted, Is.True);
                    Assert.That(persisted.replacementPid, Is.Zero);
                    Assert.That(persisted.deadline, Is.EqualTo(parentDeadline));
                    Call("LaunchServer", _spec.Snapshot(), record.deadline);
                    // Drive this isolated manager's actual probe methods sequentially so every Task is awaited.
                    // No ready/terminal result or clock is injected; ObserveLaunch performs the real deadline closeout.
                    Call("StopLaunchObservation");
                    var process = Get<Process>("_launcherProcess");
                    var handle = process.Handle;
                    _launchers.Add(process);
                }
                catch (Exception ex) { _replacementError = ex; throw; }
            };
            Set("_existingServiceRecovery", _record);
            var cancellation = new CancellationTokenSource();
            Set("_existingServiceRecoveryCancellation", cancellation);
            Set("_startInProgress", true);
            _recoveryWork = (Task)Call("RecoverExistingServiceAsync", _record, _scope, 13, 17L, cancellation, false);
            await _recoveryWork;
            Assert.That(_replacementError, Is.Null);
            Assert.That(_record.reuseDeadline, Is.EqualTo(reuseDeadline));
            Assert.That(_record.deadline, Is.EqualTo(parentDeadline));
            Assert.That(Now, Is.GreaterThanOrEqualTo(reuseDeadline), "The real eight-second reuse observation must expire before cleanup.");
            Assert.That(_manager.StopObservation.status, Is.EqualTo("succeeded"));
            CollectionAssert.Contains(_manager.StopObservation.processIds, oldChild.ProcessId);
            Assert.That(oldChild.HasExited && oldLauncher.HasExited, Is.True);
            if (disableAutoStartAfterStop)
            {
                Assert.That(disabledAfterConfirmedStop, Is.True);
                Assert.That(_record.status, Is.EqualTo("stopped"));
                Assert.That(_record.replacementStarted, Is.False);
                Assert.That(_replacementCalls, Is.Zero);
                Assert.That(File.ReadAllLines(_witness).Length, Is.EqualTo(1));
                return;
            }
            Assert.That(_record.status, Is.EqualTo("replacement_started"));
            Assert.That(_replacementCalls, Is.EqualTo(1));
            Assert.That(_record.replacementPid, Is.GreaterThan(0));
            await WaitForWitness(2, Math.Min(parentDeadline, Now + 5000));
            var state = Get<ServerLaunchState>("_launchState");
            Assert.That(state.launcherPid, Is.EqualTo(_record.replacementPid));
            Assert.That(state.deadlineUtcMs, Is.EqualTo(parentDeadline));
            LogAssert.Expect(LogType.Error, new Regex("生命周期终态超时 source=Unity客户端.*launch_identity_timeout"));
            while (!state.terminal && Now < parentDeadline)
            {
                _probeWork = (Task)Call("ProbeLaunchAsync", state, _probeCancellation.Token);
                await _probeWork;
                var remaining = parentDeadline - Now;
                if (remaining > 0) await Task.Delay((int)Math.Min(250, remaining));
            }
            Call("ObserveLaunch");
            Assert.That(Now, Is.LessThanOrEqualTo(parentDeadline + 3000), "Terminal observation must remain bounded after the fixed deadline.");
            Assert.That(state.terminal, Is.True);
            Assert.That(state.verified || state.readyVerified, Is.False);
            Assert.That(_record.status, Is.EqualTo("failed"));
            StringAssert.Contains("launch_identity_timeout", _record.error);
            Assert.That(_record.deadline, Is.EqualTo(parentDeadline));
            Assert.That(Get<UPilotServerLifecyclePhase>("_lifecyclePhase"), Is.EqualTo(UPilotServerLifecyclePhase.None));
            Call("ObserveLaunch");
            Call("ObserveExistingServiceReplacementOutcome");
            Assert.That(_replacementCalls, Is.EqualTo(1));
            Assert.That(File.ReadAllLines(_witness).Length, Is.EqualTo(2));
            Assert.That(Get<CancellationTokenSource>("_existingServiceRecoveryCancellation"), Is.Null);
            var terminal = JsonUtility.FromJson<Manager.ExistingServiceRecoveryRecord>(SessionState.GetString(RecoveryKey, ""));
            Assert.That(terminal.status, Is.EqualTo("failed"));
            Assert.That(terminal.replacementPid, Is.EqualTo(_record.replacementPid));
        }

        [TestCase("replacement_starting")]
        [TestCase("replacement_started")]
        public void ReloadWithMissingLaunchRecordFailsWithoutReplayingReplacement(string status)
        {
            var record = Record(status, Now + 20000);
            record.replacementStarted = true;
            SessionState.SetString(RecoveryKey, JsonUtility.ToJson(record));
            SessionState.EraseString(LaunchKey);
            _manager.ExistingServiceReplacementStartOverride = ignored => ++_replacementCalls;
            LogAssert.Expect(LogType.Error, new Regex("遗留服务恢复结束；recoveryId=" + record.id));
            Call("RestoreExistingServiceRecovery");
            var restored = Get<Manager.ExistingServiceRecoveryRecord>("_existingServiceRecovery");
            Assert.That(restored.status, Is.EqualTo("failed"));
            Assert.That(restored.replacementStarted, Is.True);
            Assert.That(restored.replacementPid, Is.Zero);
            Assert.That(_replacementCalls, Is.Zero);
            Assert.That(_manager.StopObservation, Is.Null);
            Assert.That(Get<Process>("_launcherProcess"), Is.Null);
            Assert.That(SessionState.GetString(LaunchKey, _missing), Is.EqualTo(_missing));
        }

        private Manager.ExistingServiceRecoveryRecord Record(string status, long deadline) => new Manager.ExistingServiceRecoveryRecord {
            id = "owned-recovery-chain-" + Guid.NewGuid().ToString("N"), projectPath = _scope.ProjectPath,
            pythonEntry = _scope.PythonEntry, serverExe = "", spec = _spec.Snapshot(), status = status,
            deadline = deadline, reuseDeadline = Now + Manager.ExistingServiceReuseBudgetMs,
        };

        [Serializable] private sealed class Witness { public int pid; public long at; }
        private async Task WaitForWitness(int count, long deadline)
        {
            while (Now < deadline)
            {
                string text;
                try { text = File.Exists(_witness) ? File.ReadAllText(_witness) : ""; }
                catch (IOException) { await Task.Delay(10); continue; }
                var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (text.EndsWith("\n", StringComparison.Ordinal) && lines.Length >= count)
                {
                    for (var i = _children.Count; i < count; i++)
                    {
                        var witness = JsonUtility.FromJson<Witness>(lines[i]);
                        var child = Manager.ServerStopTarget.Open(witness.pid);
                        Assert.That(child, Is.Not.Null);
                        Assert.That(child.HasExited, Is.False);
                        _children.Add(child);
                    }
                    return;
                }
                await Task.Delay(10);
            }
            Assert.Fail("The owned Python process did not bind both test ports before the original deadline.");
        }
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static string Quote(string value) => "\"" + value + "\"";
        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
        }
        private static FieldInfo StartupField(string name) => typeof(UPilotStartupDiagnostics).GetField(name, Static);
        private void Set(string name, object value) => typeof(Manager).GetField(name, Private).SetValue(_manager, value);
        private T Get<T>(string name) => (T)typeof(Manager).GetField(name, Private).GetValue(_manager);
        private object Call(string name, params object[] arguments) => typeof(Manager).GetMethod(name, Private).Invoke(_manager, arguments);
    }
}
