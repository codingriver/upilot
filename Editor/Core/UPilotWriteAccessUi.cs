// -----------------------------------------------------------------------
// UPilot Editor - shared project write-access controls.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal enum UPilotWriteAccessChange
    {
        None,
        Approved,
        Revoked,
    }

    internal static class UPilotWriteAccessUi
    {
        internal static string GetStatusLabel(bool approved)
        {
            return approved ? "已允许" : "Safe";
        }

        internal static string GetActionLabel(bool approved)
        {
            return approved ? "撤销授权" : "允许写入";
        }

        internal static string GetCompactDescription(UPilotSafetyConfig safety)
        {
            if (safety?.writeAccessApproved != true)
                return "当前项目为 Safe 模式，写入工具会被拒绝。";

            return "授权时间：" + FormatApprovalTime(safety.writeAccessApprovedAtUtc);
        }

        internal static string FormatApprovalTime(string approvedAtUtc)
        {
            if (!DateTimeOffset.TryParse(
                    approvedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var approvedAt))
            {
                return "未记录";
            }

            return approvedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        internal static string BuildApprovalDialogMessage(string projectRoot)
        {
            return
                "允许后，MCP Agent 可以修改当前 Unity 项目中的脚本、资源和项目设置。\n\n" +
                "授权仅作用于当前项目：\n" + NormalizePath(projectRoot) + "\n\n" +
                "配置会写入 .upilot/config.json，并由 MCP 服务热加载。工具注册列表不会改变。";
        }

        internal static string BuildRevokeDialogMessage(string projectRoot)
        {
            return
                "撤销后，MCP 将回到 Safe 模式并拒绝修改项目的工具。\n\n" +
                "当前项目：\n" + NormalizePath(projectRoot) + "\n\n" +
                "此操作会更新 .upilot/config.json。";
        }

        internal static bool TrySetProjectWriteAccess(
            bool approve,
            Func<string, string, string, string, bool> confirmDialog = null,
            Action approveAction = null,
            Action revokeAction = null)
        {
            confirmDialog ??= (title, message, ok, cancel) =>
                EditorUtility.DisplayDialog(title, message, ok, cancel);
            approveAction ??= UPilotProjectConfig.ApproveProjectWriteAccess;
            revokeAction ??= UPilotProjectConfig.RevokeProjectWriteAccess;

            var confirmed = approve
                ? confirmDialog(
                    "允许 Agent 写入当前项目？",
                    BuildApprovalDialogMessage(UPilotProjectConfig.ProjectRoot),
                    "允许写入",
                    "取消")
                : confirmDialog(
                    "撤销写入授权？",
                    BuildRevokeDialogMessage(UPilotProjectConfig.ProjectRoot),
                    "撤销",
                    "取消");

            if (!confirmed)
                return false;

            if (approve)
                approveAction();
            else
                revokeAction();
            return true;
        }

        internal static UPilotWriteAccessChange DrawActionButton(
            bool approved,
            float width,
            float height,
            GUIStyle style = null)
        {
            var clicked = style == null
                ? GUILayout.Button(GetActionLabel(approved), GUILayout.Width(width), GUILayout.Height(height))
                : GUILayout.Button(GetActionLabel(approved), style, GUILayout.Width(width), GUILayout.Height(height));
            if (!clicked)
                return UPilotWriteAccessChange.None;

            var approve = !approved;
            if (!TrySetProjectWriteAccess(approve))
                return UPilotWriteAccessChange.None;

            return approve ? UPilotWriteAccessChange.Approved : UPilotWriteAccessChange.Revoked;
        }

        internal static UPilotWriteAccessChange DrawDetailedControl()
        {
            var safety = UPilotProjectConfig.Current.safety ?? new UPilotSafetyConfig();
            var approved = safety.writeAccessApproved;
            var change = UPilotWriteAccessChange.None;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField("项目写入权限", EditorStyles.miniBoldLabel);
                        EditorGUILayout.LabelField(
                            approved ? "已允许 Agent 修改当前项目" : "Safe 模式：写入工具会被拒绝",
                            EditorStyles.wordWrappedMiniLabel);
                    }

                    GUILayout.FlexibleSpace();
                    change = DrawActionButton(approved, 76f, 22f, EditorStyles.miniButton);
                }

                if (approved)
                    EditorGUILayout.LabelField("授权时间：" + FormatApprovalTime(safety.writeAccessApprovedAtUtc), EditorStyles.miniLabel);

                EditorGUILayout.Space(2f);
                EditorGUILayout.LabelField("当前项目", EditorStyles.miniLabel);
                EditorGUILayout.SelectableLabel(
                    UPilotProjectConfig.ProjectRoot,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField("配置文件", EditorStyles.miniLabel);
                EditorGUILayout.SelectableLabel(
                    UPilotProjectConfig.ConfigPath,
                    EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField(
                    "权限修改由 MCP 服务热加载，不需要刷新工具注册列表。",
                    EditorStyles.wordWrappedMiniLabel);
            }

            return change;
        }

        internal static string GetSuccessMessage(UPilotWriteAccessChange change)
        {
            return change == UPilotWriteAccessChange.Approved
                ? "已允许 Agent 写入当前项目"
                : change == UPilotWriteAccessChange.Revoked
                    ? "已撤销写入授权，当前项目已回到 Safe 模式"
                    : string.Empty;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "（未知项目）";

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
