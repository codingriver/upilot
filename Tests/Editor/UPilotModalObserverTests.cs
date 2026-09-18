using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotModalObserverTests
    {
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
        public void PendingObservationsAreBoundedAndNeverSilentlyReplaced()
        {
            var watches = new List<UPilotModalObserver.Watch>();
            string prefix = Guid.NewGuid().ToString("N");
            try
            {
                for (int index = 0; index < UPilotModalObserver.Capacity; index++)
                    watches.Add(UPilotModalObserver.Arm(prefix + index));
                Assert.That(Assert.Throws<InvalidOperationException>(() => UPilotModalObserver.Arm(prefix + "extra")).Message,
                    Does.Contain("MODAL_OBSERVER_CAPACITY"));
                Assert.That(Assert.Throws<InvalidOperationException>(() => UPilotModalObserver.Arm(prefix + "0")).Message,
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
