// -----------------------------------------------------------------------
// UPilot Editor tests - shared scrollable dialog.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotScrollableDialogTests
    {
        [Test]
        public void ShowDialogCreatesClosableUtilityWindow()
        {
            const string title = "UPilot Scrollable Dialog Test";
            UPilotScrollableDialog.ShowDialog(title, "A scrollable message.");
            var window = Resources.FindObjectsOfTypeAll<UPilotScrollableDialog>()
                .FirstOrDefault(item => item.titleContent.text == title);

            try
            {
                Assert.That(window, Is.Not.Null);
                Assert.That(window.minSize, Is.EqualTo(new Vector2(640f, 420f)));
            }
            finally
            {
                window?.Close();
            }
        }
    }
}
