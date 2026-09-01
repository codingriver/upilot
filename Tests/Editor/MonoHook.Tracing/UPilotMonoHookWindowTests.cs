// -----------------------------------------------------------------------
// UPilot Editor tests - tracer installation-detail dialog content.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMonoHookWindowTests
    {
        [Test]
        public void ShowInstallationDetailsDialogCreatesClosableScrollableDialog()
        {
            var definition = UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.GameObjectSetActive);
            var state = new UPilotMonoHookPointRuntimeState
            {
                PointId = UPilotMonoHookPointId.GameObjectSetActive,
                InstallState = UPilotMonoHookInstallState.NotInstalled,
            };

            UPilotMonoHookWindow.ShowInstallationDetailsDialog(definition, state);
            string expectedTitle = $"UPilot 追踪器 - {definition.DisplayName} 安装详情";
            var window = Resources.FindObjectsOfTypeAll<UPilotScrollableDialog>()
                .FirstOrDefault(item => item.titleContent.text == expectedTitle);

            try
            {
                Assert.That(window, Is.Not.Null);
            }
            finally
            {
                window?.Close();
            }
        }

        [Test]
        public void InstallationDetailsTextGroupsAndSimplifiesEntries()
        {
            var definition = UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.LifecycleOnEnable);
            var state = new UPilotMonoHookPointRuntimeState
            {
                PointId = UPilotMonoHookPointId.LifecycleOnEnable,
                InstallState = UPilotMonoHookInstallState.PartiallyInstalled,
                AppliedExecutionMode = UPilotMonoHookExecutionMode.PassThrough,
                Message = "部分类型已安装",
                Coverage = new UPilotMonoHookCoverage(
                    candidateCount: 4,
                    installedCount: 2,
                    skippedCount: 1,
                    failedCount: 1,
                    entries: new[]
                    {
                        new UPilotMonoHookInstallEntry(
                            "Game.EditorPreview",
                            "Game.EditorPreview",
                            "OnEnable()",
                            "preview:OnEnable",
                            "Skipped",
                            "被类型过滤器排除"),
                        new UPilotMonoHookInstallEntry(
                            "Game.PlayerController",
                            "UnityEngine.MonoBehaviour",
                            "OnEnable()",
                            "player:OnEnable",
                            "Installed",
                            trampolineKey: "trampoline:player"),
                        new UPilotMonoHookInstallEntry(
                            "Game.PlayerController",
                            "UnityEngine.MonoBehaviour",
                            "OnEnable()",
                            "player:OnEnable:duplicate",
                            "Installed",
                            trampolineKey: "trampoline:player"),
                        new UPilotMonoHookInstallEntry(
                            "Game.BrokenProbe",
                            "Game.BrokenProbe",
                            "OnEnable()",
                            "broken:OnEnable",
                            "Failed",
                            "安装器拒绝目标"),
                    }),
            };

            string text = UPilotMonoHookWindow.BuildInstallationDetailsText(definition, state);

            Assert.That(text, Does.Contain("点位 ID：" + UPilotMonoHookPointId.LifecycleOnEnable));
            Assert.That(text, Does.Contain("安装状态：PartiallyInstalled"));
            Assert.That(text, Does.Contain("执行策略：PassThrough"));
            Assert.That(text, Does.Contain("候选 4 · 已安装类型 1"));
            Assert.That(text, Does.Contain("trampoline 1"));
            Assert.That(text, Does.Contain("已安装（1）"));
            Assert.That(text, Does.Contain("Game.PlayerController（继承自 UnityEngine.MonoBehaviour）"));
            Assert.That(text, Does.Contain("跳过（1）"));
            Assert.That(text, Does.Contain("Game.EditorPreview — 被类型过滤器排除"));
            Assert.That(text, Does.Contain("失败（1）"));
            Assert.That(text, Does.Contain("Game.BrokenProbe — 安装器拒绝目标"));
            Assert.That(text, Does.Not.Contain("[Installed]"));
            Assert.That(text, Does.Not.Contain("OnEnable()"));
            Assert.That(text, Does.Not.Contain("trampoline:player"));
            Assert.That(text, Does.Not.Contain("Version="));
        }

        [Test]
        public void InstallationDetailsTextKeepsMethodSignatureForMultipleOverloads()
        {
            var definition = UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.GameObjectDestroy);
            var state = new UPilotMonoHookPointRuntimeState
            {
                PointId = UPilotMonoHookPointId.GameObjectDestroy,
                InstallState = UPilotMonoHookInstallState.Installed,
                Coverage = new UPilotMonoHookCoverage(
                    candidateCount: 2,
                    installedCount: 2,
                    skippedCount: 0,
                    failedCount: 0,
                    entries: new[]
                    {
                        new UPilotMonoHookInstallEntry(
                            "UnityEngine.Object",
                            "UnityEngine.Object",
                            "DestroyImmediate(Object)",
                            "destroy:single",
                            "Installed",
                            trampolineKey: "trampoline:single"),
                        new UPilotMonoHookInstallEntry(
                            "UnityEngine.Object",
                            "UnityEngine.Object",
                            "DestroyImmediate(Object,bool)",
                            "destroy:allowAssets",
                            "Installed",
                            trampolineKey: "trampoline:allowAssets"),
                    }),
            };

            string text = UPilotMonoHookWindow.BuildInstallationDetailsText(definition, state);

            Assert.That(text, Does.Contain("已安装（2）"));
            Assert.That(text, Does.Contain("UnityEngine.Object · DestroyImmediate(Object)"));
            Assert.That(text, Does.Contain("UnityEngine.Object · DestroyImmediate(Object,bool)"));
            Assert.That(text, Does.Not.Contain("trampoline:single"));
        }

        [Test]
        public void InstallationDetailsTextFormatsNestedAndGenericTypeNames()
        {
            var definition = UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.LifecycleStart);
            var state = new UPilotMonoHookPointRuntimeState
            {
                PointId = UPilotMonoHookPointId.LifecycleStart,
                InstallState = UPilotMonoHookInstallState.Installed,
                Coverage = new UPilotMonoHookCoverage(
                    candidateCount: 1,
                    installedCount: 1,
                    skippedCount: 0,
                    failedCount: 0,
                    entries: new[]
                    {
                        new UPilotMonoHookInstallEntry(
                            typeof(NestedProbe).FullName,
                            typeof(System.Collections.Generic.Dictionary<
                                string,
                                System.Collections.Generic.List<int>>).FullName,
                            "Start()",
                            "start:nested",
                            "Installed",
                            trampolineKey: "trampoline:nested"),
                    }),
            };

            string text = UPilotMonoHookWindow.BuildInstallationDetailsText(definition, state);

            Assert.That(text, Does.Contain(
                "CodingRiver.UPilot.Tests.UPilotMonoHookWindowTests.NestedProbe" +
                "（继承自 System.Collections.Generic.Dictionary<System.String, " +
                "System.Collections.Generic.List<System.Int32>>）"));
            Assert.That(text, Does.Not.Contain("+NestedProbe"));
            Assert.That(text, Does.Not.Contain("Version="));
            Assert.That(text, Does.Not.Contain("Culture="));
            Assert.That(text, Does.Not.Contain("PublicKeyToken="));
        }

        [Test]
        public void InstallationDetailsTextHandlesMissingSnapshot()
        {
            var definition = UPilotMonoHookCatalog.Find(UPilotMonoHookPointId.GameObjectSetActive);
            var state = new UPilotMonoHookPointRuntimeState
            {
                PointId = UPilotMonoHookPointId.GameObjectSetActive,
                InstallState = UPilotMonoHookInstallState.NotInstalled,
            };

            string text = UPilotMonoHookWindow.BuildInstallationDetailsText(definition, state);

            Assert.That(text, Does.Contain("安装状态：NotInstalled"));
            Assert.That(text, Does.Contain("当前没有安装快照"));
        }

        [Test]
        public void CompactStatusUsesShortCoverageSummary()
        {
            var state = new UPilotMonoHookPointRuntimeState
            {
                InstallState = UPilotMonoHookInstallState.PartiallyInstalled,
                Coverage = new UPilotMonoHookCoverage(19, 16, 3, 0),
            };

            Assert.That(UPilotMonoHookWindow.GetCompactStatusText(state), Is.EqualTo("部分 16/19"));
        }

        [Test]
        public void CompactStatusShowsUnappliedForEnabledPoint()
        {
            var state = new UPilotMonoHookPointRuntimeState
            {
                ConfiguredEnabled = true,
                InstallState = UPilotMonoHookInstallState.NotInstalled,
            };

            Assert.That(UPilotMonoHookWindow.GetCompactStatusText(state), Is.EqualTo("未应用"));
        }

        private sealed class NestedProbe
        {
        }
    }
}
