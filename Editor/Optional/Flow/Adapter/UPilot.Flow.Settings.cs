using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Flow
{
    public static class UPilotFlowProjectSettingsProvider
    {
        [SettingsProvider]
        private static SettingsProvider CreateProvider()
        {
            return new SettingsProvider(UPilotFlowProjectSettingsUtility.SettingsPath, SettingsScope.Project)
            {
                label = "UPilot Flow",
                guiHandler = _ => DrawGui(),
                keywords = new HashSet<string>
                {
                    "UPilot Flow",
                    "verbose",
                    "log",
                    "delay",
                    "highlight",
                    "debug",
                    "input",
                    "strict",
                    "official",
                    "pointer",
                    "inputsystem",
                },
            };
        }

        private static void DrawGui()
        {
            UPilotFlowProjectSettings settings = UPilotFlowProjectSettings.instance;
            EditorGUI.BeginChangeCheck();

            bool forceLog = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Always Enable Verbose Log",
                    "When enabled, UPilot Flow verbose logging is always on and runtime log flags are ignored."),
                settings.AlwaysEnableVerboseLog);

            EditorGUILayout.HelpBox(
                "This project setting has higher priority than CLI, window, or temporary verbose-log switches.",
                MessageType.Info);

            int delayMs = EditorGUILayout.IntField(
                new GUIContent(
                    "Pre-Step Delay (ms)",
                    "Adds a delay before each step action starts. Set 1000 for a 1 second debug pause."),
                settings.PreStepDelayMs);
            delayMs = Mathf.Clamp(delayMs, 0, UPilotFlowProjectSettingsUtility.MaxPreStepDelayMs);

            bool requireOfficialHost = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Default Require Official Host",
                    "When enabled, all runs default to requiring the official com.unity.test-framework EditorWindow host."),
                settings.RequireOfficialHostByDefault);
            bool requireOfficialPointerDriver = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Default Require Official Pointer Driver",
                    "When enabled, click/drag/hover/scroll actions fail fast unless the official UI pointer driver is executable."),
                settings.RequireOfficialPointerDriverByDefault);
            bool requireInputSystemKeyboardDriver = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Default Require InputSystem Keyboard Driver",
                    "When enabled, press_key/type_text fail fast unless the InputSystem keyboard path is available."),
                settings.RequireInputSystemKeyboardDriverByDefault);

            EditorGUILayout.HelpBox(
                "Strict defaults are project-wide floors and cannot be relaxed by an individual adapter.",
                MessageType.Warning);

            if (EditorGUI.EndChangeCheck())
            {
                settings.AlwaysEnableVerboseLog = forceLog;
                settings.PreStepDelayMs = delayMs;
                settings.RequireOfficialHostByDefault = requireOfficialHost;
                settings.RequireOfficialPointerDriverByDefault = requireOfficialPointerDriver;
                settings.RequireInputSystemKeyboardDriverByDefault = requireInputSystemKeyboardDriver;
                settings.SaveSettings();
            }
        }
    }
}
