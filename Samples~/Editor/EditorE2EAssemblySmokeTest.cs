// -----------------------------------------------------------------------
// Upilot — M26 Phase B: EditMode test assembly smoke (UTF).
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using NUnit.Framework;

namespace codingriver.upilot.tests
{
    /// <summary>
    /// Verifies the Editor Tests assembly loads; extend with VisualElement-driven checks as needed.
    /// </summary>
    [Category("Upilot")]
    public sealed class EditorE2EAssemblySmokeTest
    {
        [Test]
        [Category("Upilot")]
        public void EditorTests_Assembly_Resolves()
        {
            Assert.Pass("Upilot.Editor.Tests loaded.");
        }
    }
}
