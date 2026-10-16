// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using Manager = CodingRiver.UPilot.UPilotMcpServerManager;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotServiceShutdownProtectionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private static Manager CreateManager() => (Manager)FormatterServices.GetUninitializedObject(typeof(Manager));
        private static void Set(Manager manager, string name, object value) =>
            typeof(Manager).GetField(name, PrivateInstance).SetValue(manager, value);
        private static void ReplacementStarted(Manager manager) =>
            typeof(Manager).GetMethod("AllowServiceReplacement", PrivateInstance).Invoke(manager, new object[] { "owner" });

        [Test]
        public void ShutdownRejectsAlreadyQueuedAndNewHelloWithoutReleasingOtherOwners()
        {
            var manager = CreateManager();
            var queued = manager.CaptureServiceShutdown();
            var closing = manager.BeginServiceShutdown("owner", "stop_preparing", 123456);
            Assert.That(manager.CanApplyHello(queued, out var queuedReason), Is.False);
            Assert.That(queuedReason, Is.EqualTo("shutdown_generation_changed"));
            Assert.That(manager.CanApplyHello(closing, out var closingReason), Is.False);
            Assert.That(closingReason, Is.EqualTo("shutdown_in_progress"));
            manager.FinishServiceShutdown("other-owner", "late_callback");
            Assert.That(manager.CaptureServiceShutdown().OwnerId, Is.EqualTo("owner"));
        }

        [Test]
        public void ChildStopPhasesPreserveAcceptedDeadlineAndOriginalResetReceipt()
        {
            var manager = CreateManager();
            var attempts = 0;
            manager.ServiceWorkResetOverride = _ => { ++attempts; return "capture_cleanup_failed"; };
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000;
            var original = manager.BeginServiceShutdown("owner", "maintenance_preparing", deadline);
            var first = manager.ResetActiveWorkForServiceOwner("owner", "maintenance");
            var child = manager.BeginServiceShutdown("owner", "stop_preparing", deadline - 90000);
            var repeated = manager.ResetActiveWorkForServiceOwner("owner", "restart");
            Assert.That(child.Generation, Is.EqualTo(original.Generation));
            Assert.That(child.DeadlineUtcMs, Is.EqualTo(deadline));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(first.Performed && repeated.Performed, Is.True);
            Assert.That(repeated.Error, Is.EqualTo("capture_cleanup_failed"));
        }

        [Test]
        public void ReloadReceiptRestoresPartialResultAndOriginalParentBudgetWithoutRepeatingCleanup()
        {
            var manager = CreateManager();
            var attempts = 0;
            manager.ServiceWorkResetOverride = _ => { ++attempts; return "unexpected_second_reset"; };
            var parentDeadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000;
            var childDeadline = parentDeadline - 90000;
            var restored = manager.BeginServiceShutdown("owner", "stop_restored", childDeadline);
            Assert.That(manager.RestoreServiceWorkResetReceipt(restored, true, "original_partial_error", parentDeadline,
                out var reason), Is.True, reason);
            var receipt = manager.ResetActiveWorkForServiceOwner("owner", "restored_restart");
            Assert.That(receipt.Performed, Is.True);
            Assert.That(receipt.Error, Is.EqualTo("original_partial_error"));
            Assert.That(attempts, Is.Zero);
            Assert.That(manager.CaptureServiceShutdown().DeadlineUtcMs, Is.EqualTo(parentDeadline));
            Set(manager, "_launchState", new ServerLaunchState { deadlineUtcMs = parentDeadline });
            ReplacementStarted(manager);
            Assert.That(manager.IsOwnedResetVerificationPending(manager.CaptureServiceShutdown()), Is.True);
        }

        [TestCase("unknown")]
        [TestCase("generation")]
        [TestCase("owner")]
        [TestCase("expired")]
        [TestCase("shorter_parent")]
        [TestCase("result_changed")]
        public void ReloadReceiptRejectsMissingOrChangedEvidence(string changed)
        {
            var manager = CreateManager();
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000;
            var captured = manager.BeginServiceShutdown("owner", "stop_restored", deadline);
            if (changed == "generation")
            {
                manager.FinishServiceShutdown("owner", "superseded");
                manager.BeginServiceShutdown("owner", "new_operation", deadline);
            }
            if (changed == "owner") manager.BeginServiceShutdown("another-owner", "stop_restored", deadline);
            if (changed == "result_changed")
                Assert.That(manager.RestoreServiceWorkResetReceipt(captured, true, "original_error", deadline, out _), Is.True);
            var restoreDeadline = changed == "expired" ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1 :
                changed == "shorter_parent" ? deadline - 1 : deadline;
            Assert.That(manager.RestoreServiceWorkResetReceipt(captured, changed != "unknown", "", restoreDeadline,
                out var reason), Is.False);
            Assert.That(reason, Is.Not.Empty);
            if (changed == "result_changed")
                Assert.That(manager.ResetActiveWorkForServiceOwner("owner", "verify_receipt").Error,
                    Is.EqualTo("original_error"));
        }

        [Test]
        public void BridgeOnlyStopIsFencedUntilAnExplicitNewStart()
        {
            var manager = CreateManager();
            var beforeStop = manager.CaptureServiceShutdown();
            manager.MarkBridgeClosing();
            var duringStop = manager.CaptureServiceShutdown();
            Assert.That(duringStop.Stopping, Is.True);
            manager.ReleaseBridgeOnlyShutdown();
            Assert.That(manager.CanApplyHello(beforeStop, out _), Is.False);
            Assert.That(manager.CanApplyHello(duringStop, out _), Is.False);
            Assert.That(manager.CanApplyHello(manager.CaptureServiceShutdown(), out _), Is.True);
            manager.BeginServiceShutdown("owner", "server_stopping");
            manager.MarkBridgeClosing();
            manager.ReleaseBridgeOnlyShutdown();
            Assert.That(manager.CaptureServiceShutdown().OwnerId, Is.EqualTo("owner"));
            Assert.That(manager.CaptureServiceShutdown().Stopping, Is.True);
        }

        [Test]
        public void NewOwnerCannotInheritAnotherOperationsCleanup()
        {
            var manager = CreateManager();
            var attempts = 0;
            manager.ServiceWorkResetOverride = _ => { ++attempts; return ""; };
            manager.BeginServiceShutdown("first", "stopping");
            manager.ResetActiveWorkForServiceOwner("first", "maintenance");
            manager.BeginServiceShutdown("second", "stopping");
            Assert.Throws<InvalidOperationException>(() => manager.ResetActiveWorkForServiceOwner("first", "late"));
            manager.ResetActiveWorkForServiceOwner("second", "maintenance");
            Assert.That(attempts, Is.EqualTo(2));
        }

        [Test]
        public void ReplacementRequiresVerifiedActualProcessAndPreservesPartialCleanupError()
        {
            var manager = CreateManager();
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000;
            manager.ServiceWorkResetOverride = _ => "partial_cleanup_failure";
            manager.BeginServiceShutdown("owner", "stopping", deadline);
            var receipt = manager.ResetActiveWorkForServiceOwner("owner", "maintenance");
            var beforeLaunch = manager.CaptureServiceShutdown();
            var launch = new ServerLaunchState { launcherPid = 1, deadlineUtcMs = deadline };
            Set(manager, "_launchState", launch);
            ReplacementStarted(manager);
            var current = manager.CaptureServiceShutdown();
            Assert.That(manager.CanApplyHello(beforeLaunch, out _), Is.False);
            Assert.That(manager.CanApplyHello(current, out _), Is.True);
            Assert.That(manager.IsOwnedResetVerificationPending(current), Is.True);
            Assert.That(manager.CanReuseOwnedReset(current, "server-lifetime", "verified-current-instance", out _), Is.False);
            using var process = Process.GetCurrentProcess();
            launch.servicePid = process.Id;
            launch.serviceCreatedAt = process.StartTime.ToUniversalTime().Ticks;
            launch.instanceId = "verified-current-instance";
            launch.verified = true;
            Set(manager, "_trackedProcess", process);
            Assert.That(manager.IsOwnedResetVerificationPending(current), Is.False);
            Assert.That(manager.CanReuseOwnedReset(current, "server-lifetime", "other-instance", out _), Is.False);
            Assert.That(manager.CanReuseOwnedReset(current, "server-lifetime", "", out _), Is.False);
            Assert.That(manager.CanReuseOwnedReset(current, "server-lifetime", "verified-current-instance", out var owner), Is.True);
            Assert.That(owner, Is.EqualTo("owner"));
            Assert.That(receipt.Error, Is.EqualTo("partial_cleanup_failure"));
            Set(manager, "_launchNeedsRevalidation", true);
            Assert.That(manager.IsOwnedResetVerificationPending(current), Is.True);
            Assert.That(manager.CanReuseOwnedReset(current, "server-lifetime", "verified-current-instance", out _), Is.False);
            Set(manager, "_launchNeedsRevalidation", false);
            Assert.That(manager.IsOwnedResetVerificationPending(current), Is.False);
            Assert.That(manager.CanReuseOwnedReset(current, "server-lifetime", "verified-current-instance", out _), Is.True);
            launch.terminal = true;
            Assert.That(manager.CanReuseOwnedReset(current, "server-lifetime", "verified-current-instance", out _), Is.False);
        }

        [Test]
        public void ReplacedLaunchReferenceAndStartGenerationCannotUseTheReceipt()
        {
            var manager = CreateManager();
            manager.ServiceWorkResetOverride = _ => "";
            manager.BeginServiceShutdown("owner", "stopping", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000);
            manager.ResetActiveWorkForServiceOwner("owner", "maintenance");
            Set(manager, "_launchState", new ServerLaunchState());
            ReplacementStarted(manager);
            var captured = manager.CaptureServiceShutdown();
            Assert.That(manager.IsOwnedResetVerificationPending(captured), Is.True);
            Set(manager, "_startAttemptGeneration", 1);
            Assert.That(manager.IsOwnedResetVerificationPending(captured), Is.False);
            Set(manager, "_startAttemptGeneration", 0);
            Set(manager, "_launchState", new ServerLaunchState());
            Assert.That(manager.IsOwnedResetVerificationPending(captured), Is.False);
        }
    }
}
