using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotModalObserverTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void MenuReceiptRetainsConcreteWireFields(bool executed)
        {
            var receipt = new MenuExecuteResultPayload { menuPath = "Fixture/Menu", executed = executed };
            var json = UnityEngine.JsonUtility.ToJson(new ResultMessage<MenuExecuteResultPayload> { payload = receipt });
            var restored = UnityEngine.JsonUtility.FromJson<ResultMessage<MenuExecuteResultPayload>>(json);
            Assert.That(restored.payload.menuPath, Is.EqualTo(receipt.menuPath));
            Assert.That(restored.payload.executed, Is.EqualTo(executed));
            Assert.That(json, Does.Contain("\"executed\":"));
        }

        [Test]
        public void PendingMenuObservationRetainsIdentityAndWaitState()
        {
            var json = UnityEngine.JsonUtility.ToJson(new ResultMessage<ModalObservationPayload>
            {
                payload = new ModalObservationPayload
                { commandId = "menu-pending", waitingForModalUi = true, terminal = false },
            });
            var restored = UnityEngine.JsonUtility.FromJson<ResultMessage<ModalObservationPayload>>(json);
            Assert.That(restored.payload.commandId, Is.EqualTo("menu-pending"));
            Assert.That(restored.payload.waitingForModalUi, Is.True);
            Assert.That(restored.payload.terminal, Is.False);
        }

        [Test]
        public void OnlyExactDeclaredTitleAndCompleteButtonSetCanMatch()
        {
            var declaration = new ExpectedModalPayload
            {
                title = "Fixture confirmation", buttons = new[] { "Proceed", "Cancel" }, clickButton = "Proceed",
            };
            Assert.That(UPilotModalObserver.MatchesExpectedModal(declaration, declaration.title, new[] { "Cancel", "Proceed" }), Is.True);
            Assert.That(UPilotModalObserver.MatchesExpectedModal(null, declaration.title, declaration.buttons), Is.False);
            Assert.That(UPilotModalObserver.MatchesExpectedModal(declaration, "Other", declaration.buttons), Is.False);
            Assert.That(UPilotModalObserver.MatchesExpectedModal(declaration, declaration.title, new[] { "Proceed", "Cancel", "Discard" }), Is.False);
            Assert.That(UPilotModalObserver.MatchesExpectedModal(declaration, declaration.title, Array.Empty<string>()), Is.False);
            declaration.clickButton = "Discard";
            Assert.That(UPilotModalObserver.MatchesExpectedModal(declaration, declaration.title, declaration.buttons), Is.False);
        }

        [Test]
        public void EmptyDeserializedDeclarationIsTreatedAsAbsent()
        {
            Assert.That(UPilotModalObserver.IsEmptyExpectedModal(new ExpectedModalPayload()), Is.True);
            Assert.That(UPilotModalObserver.IsEmptyExpectedModal(null), Is.False);
            Assert.That(UPilotModalObserver.IsEmptyExpectedModal(new ExpectedModalPayload { title = "partial" }), Is.False);
        }

        [Test]
        public void OwnerlessGenericMenuRequiresExactTargetAndOverlap()
        {
            Assert.That(UPilotModalObserver.IsOwnerlessGenericMenuCandidate(
                "#32768", "Target", "Target", true, true), Is.True);
            Assert.That(UPilotModalObserver.IsOwnerlessGenericMenuCandidate(
                "#32768", "Target", "Other", true, true), Is.False);
            Assert.That(UPilotModalObserver.IsOwnerlessGenericMenuCandidate(
                "#32768", "Target", "Target", false, true), Is.False);
            Assert.That(UPilotModalObserver.IsOwnerlessGenericMenuCandidate(
                "#32768", "Target", "Target", true, false), Is.False);
            Assert.That(UPilotModalObserver.IsOwnerlessGenericMenuCandidate(
                "#32770", "Target", "Target", true, true), Is.False);
        }

        [Test]
        public void NativeButtonMnemonicsNormalizeToVisibleLabels()
        {
            Assert.That(UPilotModalObserver.NormalizeNativeButtonLabel("&Proceed"), Is.EqualTo("Proceed"));
            Assert.That(UPilotModalObserver.NormalizeNativeButtonLabel("Save && Close"), Is.EqualTo("Save & Close"));
            Assert.That(UPilotModalObserver.NormalizeNativeButtonLabel("Cancel"), Is.EqualTo("Cancel"));
        }

        [Test]
        public void CommandObservationRetainsIdentityAndResultAfterDisposal()
        {
            string id = "modal-fixture-" + Guid.NewGuid().ToString("N");
            using (var watch = UPilotModalObserver.Arm(id))
            {
                Assert.That(watch.Snapshot().terminal, Is.False);
                watch.Complete(new GenericOkPayload { ok = true });
            }
            var result = UPilotModalObserver.Get(id);
            Assert.That(result.commandId, Is.EqualTo(id));
            Assert.That(result.commandReturned && result.terminal, Is.True);
            Assert.That(result.actionAttempted, Is.False);
            Assert.That(result.resultJson, Does.Contain("\"ok\":true"));
        }

        [Test]
        public async Task ModalTimerEndsAtItsFrozenBudgetWithoutClaimingCommandReturn()
        {
            using (var watch = new UPilotModalObserver.Watch("timed-modal", IntPtr.Zero,
                "", "", null, false, 500, 1000))
            {
                var frozen = watch.Snapshot();
                Assert.That(frozen.observationTimeoutMs, Is.EqualTo(500));
                Assert.That(frozen.checkIntervalMs, Is.EqualTo(250));
                Assert.That(await Task.WhenAny(watch.ObservationEnded, Task.Delay(3000)),
                    Is.SameAs(watch.ObservationEnded));
                var timedOut = watch.Snapshot();
                Assert.That(timedOut.terminal && timedOut.budgetElapsed, Is.True);
                Assert.That(timedOut.commandReturned, Is.False);
                Assert.That(timedOut.state, Is.EqualTo("observation_timed_out"));
                Assert.That(timedOut.observationDeadlineUtcMs, Is.EqualTo(frozen.observationDeadlineUtcMs));
                Assert.That(typeof(UPilotModalObserver.Watch).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(watch), Is.Null);
                watch.Complete(new GenericOkPayload { ok = true });
                Assert.That(watch.Snapshot().commandReturned, Is.False, "Late completion cannot overwrite the observation terminal.");
                Assert.That(watch.Snapshot().state, Is.EqualTo("observation_timed_out"));
            }
        }

        [Test]
        public async Task DisposingModalWatchSettlesGenericMenuCompletionWithoutAnotherPoll()
        {
            using (var watch = new UPilotModalObserver.Watch("stopped-menu", IntPtr.Zero,
                "", "", null, true, 30000, 1000))
            {
                var completion = new TaskCompletionSource<GenericOkPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                var receipt = new GenericOkPayload { ok = true };
                var method = typeof(UPilotModalObserver).GetMethod("CompleteAfterGenericMenuRecoveryAsync",
                    BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(typeof(GenericOkPayload));
                var recovery = (Task)method.Invoke(null, new object[] { watch, receipt, completion });
                Assert.That(recovery.IsCompleted, Is.False);
                watch.Dispose();
                Assert.That(await Task.WhenAny(recovery, Task.Delay(3000)), Is.SameAs(recovery));
                await recovery;
                Assert.That(await completion.Task, Is.SameAs(receipt));
                Assert.That(watch.Snapshot().terminal, Is.True);
                Assert.That(watch.Snapshot().commandReturned, Is.False);
                Assert.That(watch.Snapshot().state, Is.EqualTo("observation_stopped"));
            }
        }

        [Test]
        public void WindowCloseObservationEndsWithPendingModalAndDetachesItsUpdate()
        {
            var window = ScriptableObject.CreateInstance<UPilotSafeWindowProbe>();
            var watch = new UPilotModalObserver.Watch("close-pending", IntPtr.Zero,
                "test-window", "", null, false, 30000, 1000);
            var state = (ModalObservationPayload)typeof(UPilotModalObserver.Watch)
                .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(watch);
            state.waitingForModalUi = true;
            var response = new EditorWindowCloseResultPayload { commandId = "close-pending" };
            var completion = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = new UPilotWindowService.WindowCloseObservation(window, watch, response, completion, CancellationToken.None);
            try
            {
                Assert.That(EditorApplication.update.GetInvocationList().Any(d => ReferenceEquals(d.Target, observer)), Is.True);
                watch.EndObservation("observation_timed_out");
                typeof(UPilotWindowService.WindowCloseObservation).GetMethod("Observe",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(observer, null);
                Assert.That(completion.Task.IsCompleted, Is.True);
                var pending = completion.Task.Result;
                Assert.That(pending.terminal && pending.waitingForModalUi, Is.True);
                Assert.That(pending.closeVerified || pending.ok, Is.False);
                Assert.That(pending.state, Is.EqualTo("close_pending"));
                Assert.That(pending.observationStatus, Is.EqualTo("observation_timed_out"));
                Assert.That(EditorApplication.update.GetInvocationList().Any(d => ReferenceEquals(d.Target, observer)), Is.False);
                observer.Dispose();
                Assert.That(completion.Task.Result, Is.SameAs(pending));
            }
            finally
            {
                observer.Dispose();
                watch.Dispose();
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void WindowCloseCancellationStopsTimerBeforeNextEditorFrameDetachesObserver()
        {
            var window = ScriptableObject.CreateInstance<UPilotSafeWindowProbe>();
            var watch = new UPilotModalObserver.Watch("cancel-close", IntPtr.Zero,
                "test-window", "", null, false, 30000, 1000);
            using (var cancellation = new CancellationTokenSource())
            {
                var completion = new TaskCompletionSource<EditorWindowCloseResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
                var observer = new UPilotWindowService.WindowCloseObservation(window, watch,
                    new EditorWindowCloseResultPayload { commandId = "cancel-close" }, completion, cancellation.Token);
                try
                {
                    cancellation.Cancel();
                    Assert.That(watch.Snapshot().terminal, Is.True);
                    Assert.That(watch.Snapshot().state, Is.EqualTo("observation_stopped"));
                    Assert.That(typeof(UPilotModalObserver.Watch).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)
                        .GetValue(watch), Is.Null);
                    typeof(UPilotWindowService.WindowCloseObservation).GetMethod("Observe",
                        BindingFlags.Instance | BindingFlags.NonPublic).Invoke(observer, null);
                    Assert.That(completion.Task.Result.terminal, Is.True);
                    Assert.That(completion.Task.Result.closeVerified || completion.Task.Result.ok, Is.False);
                    Assert.That(EditorApplication.update.GetInvocationList().Any(d => ReferenceEquals(d.Target, observer)), Is.False);
                }
                finally
                {
                    observer.Dispose();
                    watch.Dispose();
                    UnityEngine.Object.DestroyImmediate(window);
                }
            }
        }

        [Test]
        public void PendingObservationsAreBoundedAndNeverSilentlyReplaced()
        {
            var watches = new List<UPilotModalObserver.Watch>();
            string prefix = Guid.NewGuid().ToString("N");
            try
            {
                for (int index = 0; index < UPilotModalObserver.Capacity; index++)
                    watches.Add(UPilotModalObserver.Arm(prefix + index, budgetMs: 30000));
                Assert.That(Assert.Throws<InvalidOperationException>(() => UPilotModalObserver.Arm(prefix + "extra", budgetMs: 30000)).Message,
                    Does.Contain("MODAL_OBSERVER_CAPACITY"));
                Assert.That(Assert.Throws<InvalidOperationException>(() => UPilotModalObserver.Arm(prefix + "0", budgetMs: 30000)).Message,
                    Does.Contain("MODAL_COMMAND_ALREADY_OBSERVED"));
                Assert.That(UPilotModalObserver.Get(prefix + "0").terminal, Is.False);
            }
            finally
            {
                foreach (var watch in watches) watch.Complete(null);
            }
        }
    }
}
