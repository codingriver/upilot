using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotSetupRuntimeTests
    {
        private static readonly FieldInfo ConfigCache = typeof(UPilotProjectConfig)
            .GetField("_cached", BindingFlags.Static | BindingFlags.NonPublic);
        private UPilotMainWindow _window;
        private UPilotProjectConfigData _previousConfig;
        private bool _configExisted;
        private byte[] _configBytes;
        private string _temporaryRoot;
        private string _serverPath;
        private string _pythonPath;

        [SetUp]
        public void SetUp()
        {
            _previousConfig = UPilotProjectConfig.Current;
            _configExisted = File.Exists(UPilotProjectConfig.ConfigPath);
            _configBytes = _configExisted ? File.ReadAllBytes(UPilotProjectConfig.ConfigPath) : null;
            _temporaryRoot = Path.Combine(Path.GetTempPath(), "upilot-setup-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryRoot);
            _serverPath = Path.Combine(_temporaryRoot, "upilot-mcp-server.exe");
            _pythonPath = Path.Combine(_temporaryRoot, "python.exe");
            File.WriteAllText(_serverPath, "test service; must never execute");
            File.WriteAllText(_pythonPath, "test interpreter; must never execute");
            _window = ScriptableObject.CreateInstance<UPilotMainWindow>();
            var config = JsonUtility.FromJson<UPilotProjectConfigData>(JsonUtility.ToJson(_previousConfig));
            config.runtime = new UPilotRuntimeConfig();
            ConfigCache.SetValue(null, config);
            SetEnumField("_mainView", "Setup");
            SetEnumField("_setupRuntimeChoice", "Managed");
            SetField("_setupInitialized", true);
            SetField("_setupStep", 1);
            SetField("_setupRuntimeSourceChannel", false);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Assert.That(File.Exists(UPilotProjectConfig.ConfigPath), Is.EqualTo(_configExisted),
                    "Tests must not create or delete project settings.");
                if (_configExisted)
                    Assert.That(File.ReadAllBytes(UPilotProjectConfig.ConfigPath), Is.EqualTo(_configBytes),
                        "Tests must observe saves through the injected delegate, without changing project settings.");
            }
            finally
            {
                ConfigCache.SetValue(null, _previousConfig);
                if (_window != null) UnityEngine.Object.DestroyImmediate(_window);
                if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, true);
            }
        }

        [Test]
        public void SourceChannelPreservesExplicitExeModeWhenServicePathIsEmpty()
        {
            var config = UPilotProjectConfig.Current;
            config.updates ??= new UPilotUpdateConfig();
            config.updates.channel = "source";
            config.runtime.mode = "exe";
            config.runtime.serverExePath = "";

            InvokePrivate("InitializeSetupRuntimeChoice");

            Assert.That(GetField("_setupRuntimeSourceChannel"), Is.True);
            Assert.That(GetField("_setupRuntimeChoice").ToString(), Is.EqualTo("Managed"));
            Assert.That(_window.CanContinueFromRuntimeStep(out _), Is.False);
            Assert.That(config.runtime.mode, Is.EqualTo("exe"));
            Assert.That(config.runtime.serverExePath, Is.Empty);
        }

        [Test]
        public void ManagedInspectionSkipsPythonAndMakesUninstalledServiceActionable()
        {
            var manifest = CreateManifest();
            var fetches = 0;
            var probes = 0;
            Func<Task<UPilotReleaseManifest>> fetch = () =>
            {
                fetches++;
                return Task.FromResult(manifest);
            };
            Func<UPilotPythonProbeResult> probe = () =>
            {
                probes++;
                return new UPilotPythonProbeResult();
            };

            _window.InspectSetupRuntimeAsync(fetch, probe).GetAwaiter().GetResult();
            _window.InspectSetupRuntimeAsync(fetch, probe).GetAwaiter().GetResult();

            Assert.That(fetches, Is.EqualTo(1));
            Assert.That(probes, Is.Zero, "The EXE route must never inspect Python.");
            Assert.That(GetField("_setupPythonProbe"), Is.Null);
            Assert.That(UPilotServerRuntimeService.Instance.IsStandaloneExeConfigured(out _), Is.False);
            Assert.That(_window.CanContinueFromRuntimeStep(out _), Is.True,
                "A service awaiting installation is an actionable first-run state.");
        }

        [UnityTest]
        public IEnumerator ManagedManifestPendingDisablesContinueUntilInformationArrives()
        {
            var response = new TaskCompletionSource<UPilotReleaseManifest>();
            var inspect = _window.InspectSetupRuntimeAsync(() => response.Task,
                () => throw new AssertionException("Must not inspect Python."));
            Assert.That(_window.CanContinueFromRuntimeStep(out _), Is.False);
            response.SetResult(CreateManifest());
            while (!inspect.IsCompleted) yield return null;
            inspect.GetAwaiter().GetResult();
            Assert.That(_window.CanContinueFromRuntimeStep(out _), Is.True);
        }

        [Test]
        public void ManagedDownloadStartsOnceAndOnlyItsVerifiedSuccessAdvancesOnce()
        {
            SetField("_setupManifest", CreateManifest());
            var state = new UPilotDownloadState { StartedAt = 10, IsComplete = true };
            var starts = 0;
            Action start = () =>
            {
                starts++;
                state = new UPilotDownloadState { StartedAt = 20, IsRunning = true };
            };
            _window.ContinueFromRuntimeStep(start, () => state);
            _window.ContinueFromRuntimeStep(start, () => state);
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));

            ConfigureManagedService();
            state.IsRunning = false;
            state.IsComplete = true;
            _window.ObserveSetupRuntimeOperation(state, new UPilotPythonEnvironmentState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(2));

            SetField("_setupStep", 1);
            _window.ObserveSetupRuntimeOperation(state, new UPilotPythonEnvironmentState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(1), "The successful operation must be consumed once.");
        }

        [Test]
        public void InstalledMatchingServiceContinuesWithoutStartingDownload()
        {
            SetField("_setupManifest", CreateManifest());
            ConfigureManagedService();
            _window.ContinueFromRuntimeStep(
                () => Assert.Fail("The matching installed service must be reused."),
                () => new UPilotDownloadState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(2));
        }

        [Test]
        public void DownloadCompletionWithoutSavedMatchingServiceDoesNotAdvance()
        {
            var state = BeginManagedDownload();
            state.IsRunning = false;
            state.IsComplete = true;
            LogAssert.Expect(LogType.Error, new Regex("First setup runtime completion could not be verified"));
            _window.ObserveSetupRuntimeOperation(state, new UPilotPythonEnvironmentState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));
        }

        [Test]
        public void HistoricalManagedCompletionNeverAdvancesWithoutCurrentRequest()
        {
            SetField("_setupManifest", CreateManifest());
            ConfigureManagedService();
            _window.ObserveSetupRuntimeOperation(new UPilotDownloadState { StartedAt = 10, IsComplete = true },
                new UPilotPythonEnvironmentState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));
        }

        [TestCase("failure")]
        [TestCase("cancelled")]
        [TestCase("incomplete")]
        public void FailedCancelledOrUnknownManagedResultClearsContinuation(string outcome)
        {
            var state = BeginManagedDownload();
            ConfigureManagedService();
            state.IsRunning = false;
            state.IsComplete = outcome == "failure";
            state.IsCancelled = outcome == "cancelled";
            state.ErrorMessage = outcome == "failure" ? "injected transfer failure" : "";
            if (outcome == "incomplete")
                LogAssert.Expect(LogType.Error, new Regex("First setup runtime outcome is unconfirmed"));
            _window.ObserveSetupRuntimeOperation(state, new UPilotPythonEnvironmentState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));

            state.IsComplete = true;
            state.IsCancelled = false;
            state.ErrorMessage = "";
            _window.ObserveSetupRuntimeOperation(state, new UPilotPythonEnvironmentState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(1), "A later stale success must not revive a terminal request.");
        }

        [Test]
        public void OtherManagedOperationCannotSatisfyCurrentContinuation()
        {
            var state = BeginManagedDownload();
            ConfigureManagedService();
            _window.ObserveSetupRuntimeOperation(new UPilotDownloadState
                { StartedAt = state.StartedAt + 1, IsComplete = true }, new UPilotPythonEnvironmentState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LeavingOrClosingManagedPagePreventsLaterAutoAdvance(bool closeWindow)
        {
            var state = BeginManagedDownload();
            if (closeWindow) InvokePrivate("OnDisable");
            else InvokePrivate("SetSetupStep", 0);
            ConfigureManagedService();
            state.IsRunning = false;
            state.IsComplete = true;
            SetField("_setupStep", 1);
            _window.ObserveSetupRuntimeOperation(state, new UPilotPythonEnvironmentState());
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AutomaticPythonNeedsUsableBaseInterpreterButNotPreinstalledDependencies()
        {
            foreach (var interpreterUsable in new[] { false, true })
            {
                _window.SelectSetupPythonEnvironment("");
                var probes = 0;
                var inspection = _window.InspectSetupRuntimeAsync(
                    () => throw new AssertionException("The Python route must not fetch EXE metadata."),
                    () =>
                    {
                        probes++;
                        return new UPilotPythonProbeResult
                        {
                            InterpreterUsable = interpreterUsable,
                            IsUsable = false,
                            DependenciesComplete = false,
                            PythonPath = interpreterUsable ? _pythonPath : ""
                        };
                    });
                while (!inspection.IsCompleted) yield return null;
                inspection.GetAwaiter().GetResult();
                SetField("_setupPythonEnvironmentPath", Path.Combine(_temporaryRoot, "not-created-environment"));
                Assert.That(probes, Is.EqualTo(1));
                Assert.That(_window.CanContinueFromRuntimeStep(out _), Is.EqualTo(interpreterUsable));
            }
        }

        [TestCase("success")]
        [TestCase("unsaved")]
        [TestCase("mismatched-path")]
        [TestCase("failure")]
        [TestCase("cancelled")]
        [TestCase("other-operation")]
        public void AutomaticPythonCompletionRequiresCurrentSuccessfulOperationAndMatchingSavedInterpreter(string outcome)
        {
            _window.SelectSetupPythonEnvironment("");
            SetEnumField("_setupRuntimeContinuation", "PythonEnvironment");
            SetField("_setupRuntimeOperationStartedAt", 20d);
            UPilotProjectConfig.Current.runtime.mode = "python";
            UPilotProjectConfig.Current.runtime.pythonPath = outcome == "unsaved" ? ""
                : outcome == "mismatched-path" ? _serverPath : _pythonPath;
            var state = new UPilotPythonEnvironmentState
            {
                StartedAt = outcome == "other-operation" ? 21 : 20,
                InterpreterPath = _pythonPath,
                IsComplete = outcome != "cancelled",
                IsCancelled = outcome == "cancelled",
                ErrorMessage = outcome == "failure" ? "injected environment failure" : ""
            };
            if (outcome == "unsaved" || outcome == "mismatched-path")
                LogAssert.Expect(LogType.Error, new Regex("First setup runtime completion could not be verified"));
            _window.ObserveSetupRuntimeOperation(new UPilotDownloadState(), state);
            Assert.That(GetField("_setupStep"), Is.EqualTo(outcome == "success" ? 2 : 1));

            SetField("_setupStep", 1);
            UPilotProjectConfig.Current.runtime.pythonPath = _pythonPath;
            state.StartedAt = 20;
            state.IsComplete = true;
            state.IsCancelled = false;
            state.ErrorMessage = "";
            _window.ObserveSetupRuntimeOperation(new UPilotDownloadState(), state);
            Assert.That(GetField("_setupStep"), Is.EqualTo(1),
                "A completed or rejected operation must not advance a second time from a later snapshot.");
        }

        [UnityTest]
        public IEnumerator ExistingPythonSavesOnlyAfterExactSelectedInterpreterPassesValidation()
        {
            SetEnumField("_setupRuntimeChoice", "Python");
            _window.SelectSetupPythonEnvironment(_pythonPath);
            var validation = new TaskCompletionSource<bool>();
            var calls = new List<string>();
            var select = _window.SelectExistingSetupPythonAsync(_pythonPath, true,
                saved =>
                {
                    calls.Add("save");
                    ConfigCache.SetValue(null, saved);
                }, selected =>
                {
                    Assert.That(selected, Is.EqualTo(_pythonPath));
                    calls.Add("validate");
                    return validation.Task;
                });
            _window.SelectExistingSetupPythonAsync(_pythonPath, true,
                _ => Assert.Fail("A duplicate request must not save."),
                _ => throw new AssertionException("A duplicate request must not validate again."))
                .GetAwaiter().GetResult();
            Assert.That(calls, Is.EqualTo(new[] { "validate" }));
            Assert.That(_window.CanContinueFromRuntimeStep(out _), Is.False);
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));
            validation.SetResult(true);
            while (!select.IsCompleted) yield return null;
            select.GetAwaiter().GetResult();
            Assert.That(calls, Is.EqualTo(new[] { "validate", "save" }));
            Assert.That(UPilotProjectConfig.Current.runtime.mode, Is.EqualTo("python"));
            Assert.That(UPilotProjectConfig.Current.runtime.pythonPath, Is.EqualTo(_pythonPath));
            Assert.That(GetField("_setupStep"), Is.EqualTo(2));
        }

        [Test]
        public void BrokenSelectedPythonCannotBorrowAnotherUsableInterpreterOrSave()
        {
            SetEnumField("_setupRuntimeChoice", "Python");
            _window.SelectSetupPythonEnvironment(_pythonPath);
            SetField("_setupPythonProbe", new UPilotPythonProbeResult
            {
                IsUsable = true, InterpreterUsable = true, DependenciesComplete = true,
                PythonPath = Path.Combine(_temporaryRoot, "another-python.exe")
            });
            var validations = 0;
            var saves = 0;
            LogAssert.Expect(LogType.Error, new Regex("First setup Python validation failed"));
            _window.SelectExistingSetupPythonAsync(_pythonPath, true,
                _ => saves++, selected =>
                {
                    validations++;
                    Assert.That(selected, Is.EqualTo(_pythonPath));
                    return Task.FromException(new InvalidOperationException("selected environment is broken"));
                }).GetAwaiter().GetResult();
            Assert.That(validations, Is.EqualTo(1));
            Assert.That(saves, Is.Zero);
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));
            Assert.That(GetField("_setupRuntimeError"), Is.EqualTo("selected environment is broken"));
            Assert.That(File.ReadAllText(_pythonPath), Is.EqualTo("test interpreter; must never execute"));
            Assert.That(UPilotProjectConfig.Current.runtime.pythonPath, Is.Empty);
        }

        [UnityTest]
        public IEnumerator LeavingPythonPageWhileValidationIsPendingPreventsSavingOrAdvancing()
        {
            SetEnumField("_setupRuntimeChoice", "Python");
            _window.SelectSetupPythonEnvironment(_pythonPath);
            var validation = new TaskCompletionSource<bool>();
            var saves = 0;
            var select = _window.SelectExistingSetupPythonAsync(_pythonPath, true,
                _ => saves++, _ => validation.Task);
            InvokePrivate("SetSetupStep", 0);
            validation.SetResult(true);
            while (!select.IsCompleted) yield return null;
            select.GetAwaiter().GetResult();
            Assert.That(saves, Is.Zero);
            Assert.That(GetField("_setupStep"), Is.EqualTo(0));
            Assert.That(UPilotProjectConfig.Current.runtime.pythonPath, Is.Empty);
        }

        [UnityTest]
        public IEnumerator LocalReleaseServiceSavesOnlyAfterMatchingManifestHash()
        {
            var manifest = CreateManifest();
            SetField("_setupManifest", manifest);
            var calls = new List<string>();
            var select = _window.SelectLocalSetupExeAsync(_serverPath, (path, version) =>
            {
                calls.Add("save");
                Assert.That(path, Is.EqualTo(_serverPath));
                Assert.That(version, Is.EqualTo(manifest.ServerVersion));
            }, path =>
            {
                calls.Add("hash");
                Assert.That(path, Is.EqualTo(_serverPath));
                return manifest.Downloads[0].Sha256;
            });
            while (!select.IsCompleted) yield return null;
            select.GetAwaiter().GetResult();
            Assert.That(calls, Is.EqualTo(new[] { "hash", "save" }));
        }

        [UnityTest]
        public IEnumerator LocalReleaseServiceHashMismatchDoesNotSaveOrClaimReady()
        {
            SetField("_setupManifest", CreateManifest());
            var saves = 0;
            LogAssert.Expect(LogType.Error, new Regex("First setup local service validation failed"));
            var select = _window.SelectLocalSetupExeAsync(_serverPath, (path, version) => saves++, _ => new string('b', 64));
            while (!select.IsCompleted) yield return null;
            select.GetAwaiter().GetResult();
            Assert.That(saves, Is.Zero);
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));
            Assert.That(UPilotProjectConfig.Current.runtime.serverExePath, Is.Empty);
            Assert.That(GetField("_setupRuntimeError"), Is.Not.Empty);
        }
        private UPilotDownloadState BeginManagedDownload()
        {
            SetField("_setupManifest", CreateManifest());
            var state = new UPilotDownloadState { StartedAt = 10, IsComplete = true };
            _window.ContinueFromRuntimeStep(
                () => state = new UPilotDownloadState { StartedAt = 20, IsRunning = true }, () => state);
            Assert.That(GetField("_setupStep"), Is.EqualTo(1));
            return state;
        }

        private void ConfigureManagedService()
        {
            UPilotProjectConfig.Current.runtime.mode = "exe";
            UPilotProjectConfig.Current.runtime.serverExePath = _serverPath;
            UPilotProjectConfig.Current.runtime.serverVersion = CreateManifest().ServerVersion;
        }

        private static UPilotReleaseManifest CreateManifest()
        {
            var manifest = new UPilotReleaseManifest
            {
                UpmVersion = UPilotServerRuntimeService.UpmVersion,
                ServerVersion = "0.3.48",
                MinCompatibleUpm = "0.0.0",
                Channel = "release"
            };
            manifest.Downloads.Add(new UPilotServerDownloadInfo
            {
                Platform = "windows", Architecture = "x64", FileName = "upilot-mcp-server.exe",
                Url = "https://example.invalid/upilot-mcp-server.exe", Sha256 = new string('a', 64), SizeBytes = 1
            });
            return manifest;
        }

        private object GetField(string name) =>
            typeof(UPilotMainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_window);

        private void SetField(string name, object value) =>
            typeof(UPilotMainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_window, value);

        private void SetEnumField(string name, string value)
        {
            var field = typeof(UPilotMainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(_window, Enum.Parse(field.FieldType, value));
        }

        private void InvokePrivate(string name, params object[] arguments) =>
            typeof(UPilotMainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_window, arguments);
    }
}