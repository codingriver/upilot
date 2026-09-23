using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotQueueCleanupTests
    {
        [TestCase("{}", true)]
        [TestCase("{\"aiQueueCleanupAllowed\":true}", true)]
        [TestCase("{\"aiQueueCleanupAllowed\":false}", false)]
        public void IndependentGrantPreservesLegacyAndExplicitValue(string json, bool expected)
        {
            var config = UPilotProjectConfig.Parse(json);
            Assert.That(config.aiQueueCleanupAllowed, Is.EqualTo(expected));
            UPilotAutomationAuthorizationCatalog.SetAll(config.safety, true);
            Assert.That(config.aiQueueCleanupAllowed, Is.EqualTo(expected));
            UPilotAutomationAuthorizationCatalog.SetAll(config.safety, false);
            Assert.That(config.aiQueueCleanupAllowed, Is.EqualTo(expected));
        }

        [Test]
        public void MissingDisconnectedOrIncompleteSnapshotIsNotEmpty()
        {
            Assert.That(UPilotQueueWindow.IsVerifiedEmpty(null), Is.False);
            var state = new UPilotQueueWindow.Snapshot { items = new UPilotQueueWindow.Row[0] };
            Assert.That(UPilotQueueWindow.IsVerifiedEmpty(state), Is.False);
            state.connected = true;
            Assert.That(UPilotQueueWindow.IsVerifiedEmpty(state), Is.False);
            state.complete = true;
            Assert.That(UPilotQueueWindow.IsVerifiedEmpty(state), Is.True);
            state.isStale = true;
            Assert.That(UPilotQueueWindow.IsVerifiedEmpty(state), Is.False);
        }

        [TestCase("failed")]
        [TestCase("unconfirmed")]
        public void CriticalFailuresUseRealUnityErrors(string phase)
        {
            LogAssert.Expect(LogType.Error, new Regex(@"\[UPilot\]\[QueueCleanup\].*phase=" + phase));
            UPilotQueueCleanupLog.Write(new UPilotQueueCleanupLog.Entry {
                requestId = "test-request", targetType = "Task", targetId = "test-only",
                action = "cancel", phase = phase, result = "预期测试错误", errorCode = "TEST_EXPECTED"
            });
        }

        [TestCase(EventType.Layout, true, true)]
        [TestCase(EventType.Repaint, true, false)]
        [TestCase(EventType.MouseDown, true, false)]
        [TestCase(EventType.Repaint, false, true)]
        public void QueueUsesStableGuiSnapshotBoundary(EventType eventType, bool initialized, bool expected)
        {
            Assert.That(UPilotStatusWindow.ShouldRefreshGuiSnapshot(eventType, initialized), Is.EqualTo(expected));
        }
    }
}
