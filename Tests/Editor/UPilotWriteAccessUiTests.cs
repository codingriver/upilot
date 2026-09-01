using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotWriteAccessUiTests
    {
        [Test]
        public void WriteAccessControlOffersApproveAndRevokeActions()
        {
            Assert.That(UPilotWriteAccessUi.GetStatusLabel(false), Is.EqualTo("Safe"));
            Assert.That(UPilotWriteAccessUi.GetStatusLabel(true), Is.EqualTo("已允许"));
            Assert.That(UPilotWriteAccessUi.GetActionLabel(false), Is.EqualTo("允许写入"));
            Assert.That(UPilotWriteAccessUi.GetActionLabel(true), Is.EqualTo("撤销授权"));
        }

        [Test]
        public void ApprovalConfirmationIdentifiesProjectAndWriteScope()
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "upilot-write-access-test");
            var message = UPilotWriteAccessUi.BuildApprovalDialogMessage(projectRoot);

            Assert.That(message, Does.Contain(Path.GetFullPath(projectRoot)));
            Assert.That(message, Does.Contain("当前项目"));
            Assert.That(message, Does.Contain("脚本"));
            Assert.That(message, Does.Contain("资源"));
            Assert.That(message, Does.Contain("项目设置"));
            Assert.That(message, Does.Contain("热加载"));
        }

        [Test]
        public void ConfirmationRoutesOnlyTheRequestedMutation()
        {
            var approveCount = 0;
            var revokeCount = 0;
            Func<string, string, string, string, bool> confirm = (_, _, _, _) => true;

            var approved = UPilotWriteAccessUi.TrySetProjectWriteAccess(
                true,
                confirm,
                () => approveCount++,
                () => revokeCount++);
            var revoked = UPilotWriteAccessUi.TrySetProjectWriteAccess(
                false,
                confirm,
                () => approveCount++,
                () => revokeCount++);

            Assert.That(approved, Is.True);
            Assert.That(revoked, Is.True);
            Assert.That(approveCount, Is.EqualTo(1));
            Assert.That(revokeCount, Is.EqualTo(1));
        }

        [Test]
        public void CancelledConfirmationDoesNotChangeWriteAccess()
        {
            var mutationCount = 0;

            var changed = UPilotWriteAccessUi.TrySetProjectWriteAccess(
                true,
                (_, _, _, _) => false,
                () => mutationCount++,
                () => mutationCount++);

            Assert.That(changed, Is.False);
            Assert.That(mutationCount, Is.Zero);
        }

        [Test]
        public void ApprovalTimeHasStableFallbackForMissingOrInvalidValues()
        {
            Assert.That(UPilotWriteAccessUi.FormatApprovalTime(""), Is.EqualTo("未记录"));
            Assert.That(UPilotWriteAccessUi.FormatApprovalTime("invalid"), Is.EqualTo("未记录"));
            Assert.That(
                UPilotWriteAccessUi.FormatApprovalTime("2026-08-31T08:30:00.0000000+00:00"),
                Is.Not.EqualTo("未记录"));
        }

        [Test]
        public void MainAndAdvancedWindowsExposeWriteAccessControls()
        {
            Assert.That(
                typeof(UPilotMainWindow).GetMethod(
                    "DrawWriteAccessControls",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                typeof(UPilotStatusWindow).GetMethod(
                    "DrawProjectWriteAccessSection",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
        }
    }
}
