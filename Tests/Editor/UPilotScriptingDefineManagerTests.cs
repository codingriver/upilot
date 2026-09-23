using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotScriptingDefineManagerTests
    {
        [Test]
        public void AddDefineAppendsOnceAndPreservesExistingOrder()
        {
            Assert.That(
                UPilotScriptingDefineManager.AddDefine("GAME_DEBUG;TA_URP_MODIFY"),
                Is.EqualTo("GAME_DEBUG;TA_URP_MODIFY;UPILOT"));
            Assert.That(
                UPilotScriptingDefineManager.AddDefine("GAME_DEBUG;UPILOT;TA_URP_MODIFY"),
                Is.EqualTo("GAME_DEBUG;UPILOT;TA_URP_MODIFY"));
        }

        [Test]
        public void RemoveDefineRemovesOnlyExactUPilotSymbols()
        {
            Assert.That(
                UPilotScriptingDefineManager.RemoveDefine("GAME_DEBUG;UPILOT;UPILOT_EXTRA;TA_URP_MODIFY;UPILOT"),
                Is.EqualTo("GAME_DEBUG;UPILOT_EXTRA;TA_URP_MODIFY"));
        }

        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(false, false, false)]
        public void RemovalDecisionKeepsDefineForPackageReplacement(
            bool packageRemoved,
            bool replacementPresent,
            bool expected)
        {
            Assert.That(
                UPilotScriptingDefineManager.ShouldRemoveForPackageRegistration(
                    packageRemoved,
                    replacementPresent),
                Is.EqualTo(expected));
        }
    }
}
