using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace codingriver.upilot.UIFlow
{
    [FilePath("ProjectSettings/UIFlowSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class UIFlowProjectSettings : ScriptableSingleton<UIFlowProjectSettings>
    {
        [SerializeField] private bool _alwaysEnableVerboseLog;
        [SerializeField] private int _preStepDelayMs;
        [SerializeField] private bool _requireOfficialHostByDefault;
        [SerializeField] private bool _requireOfficialPointerDriverByDefault;
        [SerializeField] private bool _requireInputSystemKeyboardDriverByDefault;

        public bool AlwaysEnableVerboseLog
        {
            get => _alwaysEnableVerboseLog;
            set => _alwaysEnableVerboseLog = value;
        }

        public int PreStepDelayMs
        {
            get => Mathf.Clamp(_preStepDelayMs, 0, UIFlowProjectSettingsUtility.MaxPreStepDelayMs);
            set => _preStepDelayMs = Mathf.Clamp(value, 0, UIFlowProjectSettingsUtility.MaxPreStepDelayMs);
        }

        public bool RequireOfficialHostByDefault
        {
            get => _requireOfficialHostByDefault;
            set => _requireOfficialHostByDefault = value;
        }

        public bool RequireOfficialPointerDriverByDefault
        {
            get => _requireOfficialPointerDriverByDefault;
            set => _requireOfficialPointerDriverByDefault = value;
        }

        public bool RequireInputSystemKeyboardDriverByDefault
        {
            get => _requireInputSystemKeyboardDriverByDefault;
            set => _requireInputSystemKeyboardDriverByDefault = value;
        }

        public void SaveSettings()
        {
            Save(true);
        }
    }

    public static class UIFlowProjectSettingsUtility
    {
        public const string SettingsPath = "Project/UIFlow";
        public const int MaxPreStepDelayMs = 60000;

        public static bool IsVerboseLoggingEnabled(bool runtimeEnabled)
        {
            return UIFlowProjectSettings.instance.AlwaysEnableVerboseLog || runtimeEnabled;
        }

        public static TestOptions ApplyOverrides(TestOptions options)
        {
            TestOptions resolved = options?.Clone() ?? new TestOptions();
            UIFlowProjectSettings settings = UIFlowProjectSettings.instance;

            resolved.EnableVerboseLog = settings.AlwaysEnableVerboseLog || resolved.EnableVerboseLog;
            if (settings.PreStepDelayMs > 0 && resolved.Headed)
            {
                resolved.PreStepDelayMs = settings.PreStepDelayMs;
            }

            resolved.RequireOfficialHost = settings.RequireOfficialHostByDefault || resolved.RequireOfficialHost;
            resolved.RequireOfficialPointerDriver = settings.RequireOfficialPointerDriverByDefault || resolved.RequireOfficialPointerDriver;
            resolved.RequireInputSystemKeyboardDriver = settings.RequireInputSystemKeyboardDriverByDefault || resolved.RequireInputSystemKeyboardDriver;

            return resolved;
        }
    }

    public static class UIFlowProjectSettingsProvider
    {
        [SettingsProvider]
        private static SettingsProvider CreateProvider()
        {
            return new SettingsProvider(UIFlowProjectSettingsUtility.SettingsPath, SettingsScope.Project)
            {
                label = "UIFlow",
                guiHandler = _ => DrawGui(),
                keywords = new HashSet<string>
                {
                    "UIFlow",
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
            UIFlowProjectSettings settings = UIFlowProjectSettings.instance;

            EditorGUI.BeginChangeCheck();

            bool forceLog = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Always Enable Verbose Log",
                    "When enabled, UIFlow verbose logging is always on and runtime log flags are ignored."),
                settings.AlwaysEnableVerboseLog);

            EditorGUILayout.HelpBox(
                "This project setting has higher priority than CLI, window, or temporary verbose-log switches.",
                MessageType.Info);

            int delayMs = EditorGUILayout.IntField(
                new GUIContent(
                    "Pre-Step Delay (ms)",
                    "Adds a delay before each step action starts. Set 1000 for a 1 second debug pause."),
                settings.PreStepDelayMs);
            delayMs = Mathf.Clamp(delayMs, 0, UIFlowProjectSettingsUtility.MaxPreStepDelayMs);

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
                "The delay happens after the target element is highlighted and before simulated input or assertions run. It only applies to headed/debug runs and is ignored by non-headed automated tests.",
                MessageType.None);

            EditorGUILayout.HelpBox(
                "Strict defaults are applied as project-wide floors: if a strict toggle is enabled here, CLI, Headed, Batch Runner, and fixture runs cannot silently relax that requirement.",
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
