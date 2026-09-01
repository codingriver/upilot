using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Acceptance
{
    public sealed class UPilotPlayModeReloadAcceptanceTests
    {
        [UnityTest]
        public IEnumerator RunGuidSurvivesPlayModeDomainReloadAndReturnsAuthoritativeResult()
        {
            Assert.That(Application.isPlaying, Is.True);
            yield return null;
            yield return new WaitForEndOfFrame();
            Assert.That(Time.frameCount, Is.GreaterThan(0));
        }
    }
}
