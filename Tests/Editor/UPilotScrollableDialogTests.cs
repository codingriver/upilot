// -----------------------------------------------------------------------
// UPilot Editor tests - shared scrollable dialog.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Linq;
using System.Reflection;
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

        [Test]
        public void ConfigurableDialogUsesCustomCopyLabelsAndCompactSize()
        {
            const string title = "UPilot Configurable Dialog Test";
            var expectedSize = new Vector2(560f, 340f);
            UPilotScrollableDialog.ShowDialog(
                title,
                "A compact help message.",
                false,
                "copy source",
                "复制验证指令",
                "已复制",
                "关闭",
                expectedSize);
            var window = Resources.FindObjectsOfTypeAll<UPilotScrollableDialog>()
                .FirstOrDefault(item => item.titleContent.text == title);

            try
            {
                Assert.That(window, Is.Not.Null);
                Assert.That(window.minSize, Is.EqualTo(expectedSize));
                Assert.That(GetPrivateField<string>(window, "_copyText"), Is.EqualTo("copy source"));
                Assert.That(GetPrivateField<string>(window, "_copyButtonText"), Is.EqualTo("复制验证指令"));
                Assert.That(GetPrivateField<string>(window, "_copiedButtonText"), Is.EqualTo("已复制"));
                Assert.That(GetPrivateField<float>(window, "_copyButtonWidth"), Is.EqualTo(110f));
                Assert.That(GetPrivateField<string>(window, "_confirmButtonText"), Is.EqualTo("关闭"));
            }
            finally
            {
                window?.Close();
            }
        }

        private static T GetPrivateField<T>(UPilotScrollableDialog window, string fieldName)
        {
            var field = typeof(UPilotScrollableDialog).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (T)field.GetValue(window);
        }
    }
}
