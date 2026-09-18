// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    /// <summary>
    /// Deliberately mixed leaves for P2-WP-08's real Runner observation
    /// matrix.  All leaves are Explicit, so they never enter a normal package
    /// run; callers must select these exact fully-qualified test names.
    /// </summary>
    public sealed class UPilotRunnerLeafAcceptanceFixtures
    {
        [Test, Explicit("P2-WP-08 targeted real-Runner pass leaf."), Order(1)]
        public void PassLeaf()
        {
            Assert.That(2 + 2, Is.EqualTo(4));
        }

        [Test, Explicit("P2-WP-08 targeted real-Runner expected failure leaf."), Order(2)]
        public void FailLeaf()
        {
            Assert.Fail("P2_WP08_EXPECTED_LEAF_FAILURE");
        }

        [UnityTest, Explicit("P2-WP-08 targeted real-Runner delayed leaf."), Order(3), Timeout(45000)]
        public IEnumerator DelayedLeaf()
        {
            // Leave enough time for a fresh Streamable HTTP client to attach
            // and observe the earlier failed leaf while this leaf is pending.
            double deadline = EditorApplication.timeSinceStartup + 30.0;
            while (EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.That(EditorApplication.timeSinceStartup, Is.GreaterThanOrEqualTo(deadline));
        }
    }
}
