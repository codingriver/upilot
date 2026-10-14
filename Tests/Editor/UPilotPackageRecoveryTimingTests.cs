using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotPackageRecoveryTimingTests
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private const string RestoreKey = "CodingRiver.UPilot.PackageUpdate.RestoreWait";
        private const string RepairKey = "CodingRiver.UPilot.PackageUpdate.RepairWait";
        private const string Missing = "__UPilotTimingTestMissing__";
        private readonly Dictionary<string, string> _strings = new Dictionary<string, string>();
        private readonly Dictionary<string, bool> _bools = new Dictionary<string, bool>();
        private bool _stopped;
        private bool _registration;
        private bool _captured;

        [SetUp]
        public void SaveInactiveLifecycle()
        {
            if (Field<bool>("_restartScheduled") || Field<bool>("_deferredRepairScheduled") ||
                Field<bool>("_deferredRepairRunning") || UPilotUpdateService.Instance.GetOperationStatus().IsRunning ||
                UPilotUpdateService.GetReloadGuardStatus().IsActive)
                Assert.Ignore("An existing package update owns the lifecycle; preserve its state.");
            var boolKeys = new HashSet<string>
            {
                "UpdateInProgressKey", "RestartPendingKey", "UpdateCompletedKey", "ManagedServerPendingKey",
                "DeploymentRepairPendingKey", "DeploymentRepairAttemptedKey", "ReloadGuardActiveKey",
                "ReloadGuardAutoRefreshBlockedKey", "ReloadGuardAssemblyReloadLockedKey",
                "ExternalPackageManagerAbortKey", "OccupancyRecoveryPendingKey",
            };
            foreach (var type in new[] { typeof(UPilotPackageUpdateLifecycle), typeof(UPilotUpdateService) })
                foreach (var field in type.GetFields(StaticPrivate).Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Key")))
                {
                    string key = UPilotPreferences.ProjectKey((string)field.GetRawConstantValue());
                    if (boolKeys.Contains(field.Name)) _bools[key] = SessionState.GetBool(key, false);
                    else _strings[key] = SessionState.GetString(key, Missing);
                }
            _stopped = (bool)typeof(UPilotQuickStart).GetField("_explicitlyStopped", StaticPrivate).GetValue(null);
            _registration = Field<bool>("_registrationInProgress");
            typeof(UPilotQuickStart).GetField("_explicitlyStopped", StaticPrivate).SetValue(null, false);
            SetField("_registrationInProgress", true);
            SessionState.EraseString(UPilotPreferences.ProjectKey("CodingRiver.UPilot.PackageUpdate.SupersededRestart"));
            _captured = true;
        }

        [TearDown]
        public void RestoreLifecycle()
        {
            if (!_captured) return;
            UPilotPackageUpdateLifecycle.ResetUpdateStateForRepair(clearPendingPackageRetry: false);
            foreach (var pair in _strings)
                if (pair.Value == Missing) SessionState.EraseString(pair.Key);
                else SessionState.SetString(pair.Key, pair.Value);
            foreach (var pair in _bools) SessionState.SetBool(pair.Key, pair.Value);
            typeof(UPilotQuickStart).GetField("_explicitlyStopped", StaticPrivate).SetValue(null, _stopped);
            SetField("_registrationInProgress", _registration);
            _strings.Clear();
            _bools.Clear();
            _captured = false;
        }

        [TestCase(RestoreKey)]
        [TestCase(RepairKey)]
        public void PersistedWaitKeepsItsFrozenBudgetAcrossAnotherDomainRestore(string key)
        {
            long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 17000;
            SaveWait(key, "frozen-identity", 31, deadline);
            var first = Call("GetOrCreateRecoveryWait", key);
            var restored = Call("GetOrCreateRecoveryWait", key);
            Assert.That(WaitField<string>(first, "operationId"), Is.EqualTo("frozen-identity"));
            Assert.That(WaitField<string>(restored, "operationId"), Is.EqualTo("frozen-identity"));
            Assert.That(WaitField<int>(restored, "timeoutSeconds"), Is.EqualTo(31));
            Assert.That(WaitField<long>(restored, "deadlineUtcMs"), Is.EqualTo(deadline));
            Assert.That((bool)Call("HasRecoveryWaitExpired", restored), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExpiredWaitEndsBeforeBusyEditorCanExtendIt(bool repair)
        {
            string key = repair ? RepairKey : RestoreKey;
            SaveWait(key, "expired-identity", 30, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
            var wait = Call("GetOrCreateRecoveryWait", key);
            SetField(repair ? "_deferredRepairWait" : "_restoreWait", wait);
            SetField(repair ? "_deferredRepairScheduled" : "_restartScheduled", true);
            if (repair) SetField("_deferredRepairRunning", true); // An awaited probe must not hide the deadline.
            SessionState.SetBool(UPilotPreferences.ProjectKey(repair
                ? "CodingRiver.UPilot.PackageUpdate.DeploymentRepairPending"
                : "CodingRiver.UPilot.PackageUpdate.Completed"), true);
            var method = typeof(UPilotPackageUpdateLifecycle).GetMethod(repair ? "PollDeferredPackageRepair" : "WaitForEditorReady", StaticPrivate);
            var callback = (EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction), method);
            EditorApplication.update += callback;
            try
            {
                method.Invoke(null, null);
                Assert.That(EditorApplication.update.GetInvocationList(), Has.No.Member(callback));
                Assert.That(Field<bool>(repair ? "_deferredRepairScheduled" : "_restartScheduled"), Is.False);
                if (repair) Assert.That(Field<bool>("_deferredRepairRunning"), Is.False);
                Assert.That(SessionState.GetString(UPilotPreferences.ProjectKey(key), ""), Is.Empty);
                Assert.That(UPilotUpdateService.Instance.GetOperationStatus().Phase, Is.EqualTo(UPilotUpdateOperationPhase.Failed));
                Assert.That((bool)Call("IsCurrentRecoveryWait", key, wait), Is.False);
            }
            finally { EditorApplication.update -= callback; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task StopOrResetFencesOldDeferredContinuationAndPreservesSuccessor(bool userStop)
        {
            SaveWait(RepairKey, "old-wait", 600, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600000);
            var old = Call("GetOrCreateRecoveryWait", RepairKey);
            SetField("_deferredRepairWait", old);
            SetField("_deferredRepairRunning", true);
            if (userStop) UPilotPackageUpdateLifecycle.CancelDeferredRepairForUserStop();
            else UPilotPackageUpdateLifecycle.ResetUpdateStateForRepair(clearPendingPackageRetry: false);
            Assert.That(Field<bool>("_deferredRepairRunning"), Is.False);
            Assert.That((bool)Call("IsCurrentRecoveryWait", RepairKey, old), Is.False);
            SaveWait(RepairKey, "successor", 600, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 600000);
            var successor = Call("GetOrCreateRecoveryWait", RepairKey);
            SetField("_deferredRepairWait", successor);
            SetField("_deferredRepairRunning", true);
            await (Task)Call("CompleteDeferredPackageRepairAsync", old);
            Assert.That(WaitField<string>(Call("GetOrCreateRecoveryWait", RepairKey), "operationId"), Is.EqualTo("successor"));
            Assert.That(Field<bool>("_deferredRepairRunning"), Is.True, "The old finally must not clear its successor's running state.");
            Assert.That(Field<bool>("_deferredRepairScheduled"), Is.False);
        }

        [Test] public void SourceResetReleasesAnActiveSelfUpdateObservationBeforeClearingStatus()
        {
            var service = UPilotUpdateService.Instance;
            int failures = 0;
            var observation = UPilotUpmRequestObservation.Create(service, "fixture-update", "upilot.update",
                () => { }, () => false, () => null, () => Assert.Fail("Reset cannot deliver success."), ex => ++failures);
            typeof(UPilotUpdateService).GetField("_upmObservation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, observation);
            try
            {
                observation.Start();
                UPilotUpdateService.SetOperationPhase(UPilotUpdateOperationPhase.UpdatingPackage, "fixture");
                UPilotUpdateService.ResetSourceChannelState();
                observation.Tick();
                Assert.That(observation.IsSubscribed, Is.False);
                Assert.That(failures, Is.EqualTo(1));
                Assert.That(service.GetOperationStatus().Phase, Is.EqualTo(UPilotUpdateOperationPhase.None));
            }
            finally { observation.Cancel("fixture_end"); typeof(UPilotUpdateService).GetField("_upmObservation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, null); }
        }

        [Test] public void RegisteredCompletionReleasesObservationAndPreservesRecoveryHandoff()
        {
            var service = UPilotUpdateService.Instance;
            int successes = 0, failures = 0;
            var observation = UPilotUpmRequestObservation.Create(service, "fixture-register", "upilot.update",
                () => { }, () => false, () => null, () => ++successes, ex => ++failures);
            var field = typeof(UPilotUpdateService).GetField("_upmObservation", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(service, observation);
            try
            {
                observation.Start();
                SessionState.SetBool(UPilotPreferences.ProjectKey("CodingRiver.UPilot.PackageUpdate.InProgress"), true);
                UPilotPackageUpdateLifecycle.MarkPackageUpdateCompleted();
                observation.Cancel("domain_reload");
                Assert.That(observation.IsSubscribed, Is.False); Assert.That(successes, Is.EqualTo(1)); Assert.That(failures, Is.Zero);
                Assert.That(service.GetOperationStatus().Phase, Is.EqualTo(UPilotUpdateOperationPhase.WaitingForReload));
            }
            finally { observation.Cancel("fixture_end"); field.SetValue(service, null); }
        }

        private static void SaveWait(string key, string id, int timeout, long deadline) =>
            SessionState.SetString(UPilotPreferences.ProjectKey(key),
                "{\"operationId\":\"" + id + "\",\"timeoutSeconds\":" + timeout + ",\"deadlineUtcMs\":" + deadline + "}");
        private static T Field<T>(string name) => (T)typeof(UPilotPackageUpdateLifecycle).GetField(name, StaticPrivate).GetValue(null);
        private static void SetField(string name, object value) => typeof(UPilotPackageUpdateLifecycle).GetField(name, StaticPrivate).SetValue(null, value);
        private static object Call(string name, params object[] args) => typeof(UPilotPackageUpdateLifecycle).GetMethod(name, StaticPrivate).Invoke(null, args);
        private static T WaitField<T>(object wait, string name) => (T)wait.GetType().GetField(name).GetValue(wait);
    }
}
