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
            Assert.That(UPilotWriteAccessUi.GetStatusLabel(false), Is.EqualTo("未授权（只读）"));
            Assert.That(UPilotWriteAccessUi.GetStatusLabel(true), Is.EqualTo("已允许"));
            Assert.That(UPilotWriteAccessUi.GetActionLabel(false), Is.EqualTo("允许授权"));
            Assert.That(UPilotWriteAccessUi.GetActionLabel(true), Is.EqualTo("撤销授权"));
            Assert.That(UPilotWriteAccessUi.DetailedTitle, Is.EqualTo("Agent 操作授权"));
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
        public void MainWindowUsesConditionalAuthorizationBannerAndAdvancedWindowOwnsDetailedControls()
        {
            Assert.That(UPilotMainWindow.ShouldShowWriteAccessBanner(null), Is.True);
            Assert.That(
                UPilotMainWindow.ShouldShowWriteAccessBanner(new UPilotSafetyConfig
                {
                    writeAccessApproved = false,
                }),
                Is.True);
            Assert.That(
                UPilotMainWindow.ShouldShowWriteAccessBanner(new UPilotSafetyConfig
                {
                    writeAccessApproved = true,
                }),
                Is.False);

            Assert.That(
                typeof(UPilotMainWindow).GetMethod(
                    "DrawWriteAccessBanner",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                typeof(UPilotMainWindow).GetMethod(
                    "DrawWriteAccessControls",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Null);
            Assert.That(
                typeof(UPilotStatusWindow).GetMethod(
                    "DrawProjectWriteAccessSection",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
        }

        [Test]
        public void MainWindowUsesConcisePortLabelsAndAgentUpdateActions()
        {
            Assert.That(UPilotMainWindow.McpPortLabel, Is.EqualTo("MCP 端口"));
            Assert.That(UPilotMainWindow.UnityBridgePortLabel, Is.EqualTo("Unity Bridge 端口"));
            Assert.That(UPilotMainWindow.GetAgentUpdateButtonLabel(0), Is.EqualTo("更新全部"));
            Assert.That(UPilotMainWindow.GetAgentUpdateButtonLabel(3), Is.EqualTo("更新 3 项"));

            Assert.That(
                typeof(UPilotMainWindow).GetMethod(
                    "DrawRuntimeDetails",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                typeof(UPilotMainWindow).GetMethod(
                    "ForceUpdateAllAgentIntegrations",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
        }

        [Test]
        public void AgentSummaryPrioritizesFinalState()
        {
            var readyMcp = new AgentMcpConfigStatus("Codex", "config", true, true, true);
            var currentRule = new AgentRuleConfigStatus("Codex", "rules", AgentRuleConfigState.Current);
            var currentSkill = new AgentSkillConfigStatus("Codex", "skill", AgentSkillConfigState.Current);
            var unavailableSkill = new AgentSkillConfigStatus(
                "Claude Code",
                "",
                AgentSkillConfigState.NotProvided,
                applicabilityExplanation: "当前未提供独立 Skill");
            var missingSkill = new AgentSkillConfigStatus(
                "Claude Code",
                "skill",
                AgentSkillConfigState.Missing);
            var staleMcp = new AgentMcpConfigStatus("Codex", "config", true, true, false);
            var missingMcp = new AgentMcpConfigStatus("Codex", "config", false, false, false);
            var failedRule = new AgentRuleConfigStatus("Codex", "rules", AgentRuleConfigState.Error, "failed");
            var failedSkill = new AgentSkillConfigStatus("Codex", "skill", AgentSkillConfigState.Error, "failed");

            Assert.That(
                UPilotMainWindow.GetAgentOverallStateText(readyMcp, currentRule, currentSkill),
                Is.EqualTo("已就绪"));
            Assert.That(
                UPilotMainWindow.GetAgentOverallStateText(readyMcp, currentRule, unavailableSkill),
                Is.EqualTo("已就绪"));
            Assert.That(
                UPilotMainWindow.GetAgentOverallStateText(readyMcp, currentRule, missingSkill),
                Is.EqualTo("需更新"));
            Assert.That(
                UPilotMainWindow.GetAgentOverallStateText(staleMcp, currentRule, currentSkill),
                Is.EqualTo("需更新"));
            Assert.That(
                UPilotMainWindow.GetAgentOverallStateText(missingMcp, currentRule, currentSkill),
                Is.EqualTo("未配置"));
            Assert.That(
                UPilotMainWindow.GetAgentOverallStateText(readyMcp, failedRule, currentSkill),
                Is.EqualTo("异常"));
            Assert.That(
                UPilotMainWindow.GetAgentOverallStateText(readyMcp, currentRule, failedSkill),
                Is.EqualTo("异常"));
        }

        [Test]
        public void AgentDetailsAlwaysExposeThreeEqualRowsWithDiagnosticTooltips()
        {
            var labels = UPilotMainWindow.GetAgentDetailLabels();
            Assert.That(labels, Is.EqualTo(new[] { "Agent 规则", "MCP 配置", "Skill 技能" }));

            var configPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "upilot", "config.toml"));
            var rulesPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "upilot", "AGENTS.md"));
            var skillPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "upilot", "skill"));
            var sourcePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "upilot", "source"));
            var mcp = new AgentMcpConfigStatus(
                "Codex",
                configPath,
                true,
                true,
                false,
                configuredUrl: "http://127.0.0.1:9000/mcp");
            var rules = new AgentRuleConfigStatus(
                "Codex",
                rulesPath,
                AgentRuleConfigState.UpdateAvailable,
                configPaths: new[] { rulesPath },
                installedVersion: 21,
                targetVersion: 22,
                sourcePath: sourcePath,
                contentHash: "current-rule-hash",
                expectedHash: "target-rule-hash");
            var skill = new AgentSkillConfigStatus(
                "Codex",
                skillPath,
                AgentSkillConfigState.Current,
                sourcePath: sourcePath,
                installedVersion: 17,
                targetVersion: 17,
                recordedHash: "recorded-skill-hash",
                contentHash: "current-skill-hash",
                skillRootPath: Path.GetDirectoryName(skillPath),
                installedSkillCount: 2,
                installedSkillNames: new[] { "upilot-unity-mcp", "project-helper" },
                upilotSkillCount: 1,
                capabilityLabels: new[] { "连接与工具发现", "脚本与编译" },
                associatedToolCount: 12,
                primaryToolNames: new[] { "unity_mcp_status", "unity_safe_compile_and_wait" },
                skillRootPaths: new[]
                {
                    Path.GetDirectoryName(skillPath),
                    Path.GetFullPath(Path.Combine(Path.GetTempPath(), "upilot", "compatible-skills")),
                });

            var serverStatus = new McpServerStatus
            {
                ToolCountsKnown = true,
                DetailedToolCountsKnown = true,
                RegisteredToolCount = 185,
                AvailableToolCount = 182,
                CallableToolCount = 150,
                ToolRegistryVersion = 4,
                ToolCategorySummary = "asset:13,editor:11,console:10,flow:8",
            };

            var mcpTooltip = UPilotMainWindow.BuildMcpTooltip(mcp, serverStatus);
            var ruleTooltip = UPilotMainWindow.BuildRuleTooltip(rules);
            var skillTooltip = UPilotMainWindow.BuildSkillTooltip(skill);

            Assert.That(mcpTooltip, Does.Contain(configPath));
            Assert.That(mcpTooltip, Does.Contain("http://127.0.0.1:9000/mcp"));
            Assert.That(mcpTooltip, Does.Contain("目标 URL"));
            Assert.That(mcpTooltip, Does.Contain("已注册 MCP 工具：185 个"));
            Assert.That(mcpTooltip, Does.Contain("当前可用：182 个"));
            Assert.That(mcpTooltip, Does.Contain("当前可调用：150 个"));
            Assert.That(mcpTooltip, Does.Contain("工具注册表版本：4"));
            Assert.That(mcpTooltip, Does.Contain("资源 13"));
            Assert.That(ruleTooltip, Does.Contain(rulesPath));
            Assert.That(ruleTooltip, Does.Contain(sourcePath));
            Assert.That(ruleTooltip, Does.Contain("当前版本：21"));
            Assert.That(ruleTooltip, Does.Contain("目标版本：22"));
            Assert.That(ruleTooltip, Does.Contain("current-rule-hash"));
            Assert.That(skillTooltip, Does.Contain(skillPath));
            Assert.That(skillTooltip, Does.Contain("Skill 发现目录 2"));
            Assert.That(skillTooltip, Does.Contain(sourcePath));
            Assert.That(skillTooltip, Does.Contain("当前版本：17"));
            Assert.That(skillTooltip, Does.Contain("current-skill-hash"));
            Assert.That(skillTooltip, Does.Contain("项目级 Skill 数量：2 个"));
            Assert.That(skillTooltip, Does.Contain("UPilot Skill 数量：1 个"));
            Assert.That(skillTooltip, Does.Contain("upilot-unity-mcp"));
            Assert.That(skillTooltip, Does.Contain("Skill 引用的 MCP 工具：12 个"));
            Assert.That(skillTooltip, Does.Contain("unity_mcp_status"));
        }
    }
}
