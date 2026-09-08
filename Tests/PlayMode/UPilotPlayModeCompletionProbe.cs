using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotPlayModeCompletionProbe
    {
        [UnityTest]
        public IEnumerator CompletesNormallyAndLetsRunnerExit()
        {
            Assert.That(Application.isPlaying, Is.True);
            yield return null;
            yield return null;
            Assert.That(Application.isPlaying, Is.True);
        }
    }
}
