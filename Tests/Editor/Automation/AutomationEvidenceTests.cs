using System;
using System.Collections;
using System.Collections.Generic;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationEvidenceTests
    {
        [Test]
        public void EmptyIntervalsAreOmittedAndInvalidRangesRejected()
        {
            var intervals = new List<AutomationLogInterval>();
            AutomationEvidenceSession.AddInterval(intervals, "Finally", "exit", 3, 3);
            Assert.That(intervals, Is.Empty);
            Assert.Throws<ArgumentException>(() => AutomationEvidenceSession.AddInterval(intervals, "", "", 3, 2));
            AutomationEvidenceSession.AddInterval(intervals, "Finally", "exit", 3, 4);
            Assert.That(intervals[0].toSequenceExclusive, Is.EqualTo(4));
        }
        [UnityTest]
        public IEnumerator BorrowedCaptureCollectsFixedPagesAndCannotStopOwner()
        {
            string token = Guid.NewGuid().ToString("N");
            var owned = AutomationEvidenceSession.StartOwned(new ConsoleCaptureStartPayload
            { title = "step-evidence-test", ownerId = "step-evidence-test", ownerToken = token,
                requestKey = Guid.NewGuid().ToString("N"), flushIntervalMs = 50 });
            try
            {
                var borrowed = AutomationEvidenceSession.Borrow(owned.SessionId);
                Assert.Throws<InvalidOperationException>(() => borrowed.StopOwned());
                long start = borrowed.Boundary();
                for (int index = 0; index < 1100; index++) Debug.Log("Step evidence page " + index);
                long end = borrowed.Boundary();
                using var collector = new AutomationConsoleCollector(owned.SessionId, start, end);
                while (!collector.Complete) { collector.Poll(); yield return null; }
                Assert.That(collector.Evidence.pagesComplete, Is.True, collector.Evidence.detail);
                Assert.That(collector.Records.Count, Is.EqualTo(end - start));
                Assert.That(UPilotConsoleCaptureApi.Status(owned.SessionId).session.active, Is.True);
            }
            finally { Assert.That(owned.StopOwned().ok, Is.True); }
        }
    }
}
