using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Flow
{
    [FilePath("ProjectSettings/UPilotFlowSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class UPilotFlowProjectSettings : ScriptableSingleton<UPilotFlowProjectSettings>
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
            get => Mathf.Clamp(_preStepDelayMs, 0, UPilotFlowProjectSettingsUtility.MaxPreStepDelayMs);
            set => _preStepDelayMs = Mathf.Clamp(value, 0, UPilotFlowProjectSettingsUtility.MaxPreStepDelayMs);
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

    public static class UPilotFlowProjectSettingsUtility
    {
        public const string SettingsPath = "Project/UPilot Flow";
        public const int MaxPreStepDelayMs = 60000;

        public static bool IsVerboseLoggingEnabled(bool runtimeEnabled)
        {
            return UPilotFlowProjectSettings.instance.AlwaysEnableVerboseLog || runtimeEnabled;
        }

        public static TestOptions ApplyOverrides(TestOptions options)
        {
            TestOptions resolved = options?.Clone() ?? new TestOptions();
            UPilotFlowProjectSettings settings = UPilotFlowProjectSettings.instance;

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

    [Serializable]
    public sealed class UPilotFlowExecutionSettings
    {
        public bool Headed = true;
        public bool DebugOnFailure = true;
        public bool StopOnFirstFailure;
        public bool ContinueOnStepFailure;
        public bool ScreenshotOnFailure = true;
        public int DefaultTimeoutMs = UPilotFlowSchema.DefaultStepTimeoutMs;
        public bool EnableVerboseLog;
        public int PreStepDelayMs;
        public bool RequireOfficialHost;
        public bool RequireOfficialPointerDriver;
        public bool RequireInputSystemKeyboardDriver;
        public string ReportOutputPath = "Reports/UPilot/Flow";
        public string ScreenshotPath;
        public bool GenerateSingleReport = true;
    }

    public static class UPilotFlowConfigurationService
    {
        public static TestOptions Resolve(UPilotFlowExecutionSettings settings)
        {
            settings ??= new UPilotFlowExecutionSettings();
            string reportPath = string.IsNullOrWhiteSpace(settings.ReportOutputPath)
                ? "Reports/UPilot/Flow"
                : settings.ReportOutputPath;
            string screenshotPath = string.IsNullOrWhiteSpace(settings.ScreenshotPath)
                ? Path.Combine(reportPath, "Screenshots")
                : settings.ScreenshotPath;

            var options = new TestOptions
            {
                Headed = settings.Headed,
                DebugOnFailure = settings.DebugOnFailure,
                StopOnFirstFailure = settings.StopOnFirstFailure,
                ContinueOnStepFailure = settings.ContinueOnStepFailure,
                ScreenshotOnFailure = settings.ScreenshotOnFailure,
                DefaultTimeoutMs = settings.DefaultTimeoutMs,
                EnableVerboseLog = settings.EnableVerboseLog,
                PreStepDelayMs = settings.PreStepDelayMs,
                RequireOfficialHost = settings.RequireOfficialHost,
                RequireOfficialPointerDriver = settings.RequireOfficialPointerDriver,
                RequireInputSystemKeyboardDriver = settings.RequireInputSystemKeyboardDriver,
                ReportOutputPath = reportPath,
                ScreenshotPath = screenshotPath,
                GenerateSingleReport = settings.GenerateSingleReport,
            };

            options = UPilotFlowProjectSettingsUtility.ApplyOverrides(options);
            options.Validate();
            return options;
        }

        public static TestOptions Resolve(TestOptions options)
        {
            TestOptions resolved = UPilotFlowProjectSettingsUtility.ApplyOverrides(options ?? new TestOptions());
            resolved.Validate();
            return resolved;
        }
    }

    public static class UPilotFlowPathResolver
    {
        public const int DefaultMaxYamlPaths = 1000;

        public static List<string> Resolve(
            IEnumerable<string> yamlPaths,
            string yamlDirectory,
            int maxYamlPaths = DefaultMaxYamlPaths)
        {
            string[] requestedPaths = yamlPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray() ?? Array.Empty<string>();
            bool hasPaths = requestedPaths.Length > 0;
            bool hasDirectory = !string.IsNullOrWhiteSpace(yamlDirectory);
            if (hasPaths == hasDirectory)
            {
                throw new UPilotFlowException(
                    ErrorCodes.TestCasePathInvalid,
                    "Specify either yamlPaths or yamlDirectory.");
            }

            var resolved = new List<string>();
            if (hasPaths)
            {
                foreach (string rawPath in requestedPaths)
                {
                    string fullPath = Path.GetFullPath(rawPath);
                    if (!File.Exists(fullPath) || !fullPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UPilotFlowException(ErrorCodes.TestCasePathInvalid, $"YAML file not found or invalid: {rawPath}");
                    }

                    resolved.Add(fullPath);
                }
            }
            else
            {
                string directory = Path.GetFullPath(yamlDirectory);
                if (!Directory.Exists(directory))
                {
                    throw new UPilotFlowException(ErrorCodes.TestSuiteDirectoryNotFound, $"YAML directory not found: {yamlDirectory}");
                }

                resolved.AddRange(Directory.GetFiles(directory, "*.yaml", SearchOption.AllDirectories));
            }

            resolved = resolved
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (resolved.Count == 0)
            {
                throw new UPilotFlowException(ErrorCodes.TestCasePathInvalid, "No YAML files were resolved.");
            }

            if (resolved.Count > maxYamlPaths)
            {
                throw new UPilotFlowException(
                    ErrorCodes.TestOptionsInvalid,
                    $"Too many YAML files. Maximum supported count is {maxYamlPaths}.");
            }

            return resolved;
        }
    }

    public static class ExitCodeResolver
    {
        public static int Resolve(TestSuiteResult result)
        {
            if (result == null)
            {
                return 2;
            }

            return result.Errors > 0 ? 2 : result.Failed > 0 ? 1 : 0;
        }
    }
}
