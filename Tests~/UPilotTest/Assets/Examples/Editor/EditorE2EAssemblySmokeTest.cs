// -----------------------------------------------------------------------
// UPilot — M26 Phase B: EditMode test assembly smoke (UTF).
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using NUnit.Framework;

namespace CodingRiver.UPilot.tests
{
    /// <summary>
    /// Verifies the Editor Tests assembly loads; extend with VisualElement-driven checks as needed.
    /// </summary>
    [Category("UPilot")]
    public sealed class EditorE2EAssemblySmokeTest
    {
        [Test]
        [Category("UPilot")]
        public void EditorTests_Assembly_Resolves()
        {
            Assert.Pass("UPilot.Editor.Tests loaded.");
        }
    }
}
