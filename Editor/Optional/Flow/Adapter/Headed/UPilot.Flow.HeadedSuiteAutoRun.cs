using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using CodingRiver.UPilot.Flow;

namespace CodingRiver.UPilot.Flow.EditorAutomation
{
    [InitializeOnLoad]
    public static class UPilotFlowHeadedSuiteAutoRun
    {
        private const string SessionKey = "CodingRiver.UPilot.Flow.HeadedSuiteAutoRun.Active";
        private static bool _scheduled;

        static UPilotFlowHeadedSuiteAutoRun()
        {
            EditorApplication.delayCall += ScheduleIfRequested;
        }

        private static void ScheduleIfRequested()
        {
            if (_scheduled || SessionState.GetBool(SessionKey, false))
            {
                return;
            }

            string requestPath = GetRequestPath();
            if (!File.Exists(requestPath))
            {
                return;
            }

            _scheduled = true;
            SessionState.SetBool(SessionKey, true);
            EditorApplication.delayCall += RunRequestedSuite;
        }

        private static void RunRequestedSuite()
        {
            _ = RunRequestedSuiteAsync();
        }

        private static async Task RunRequestedSuiteAsync()
        {
            string requestPath = GetRequestPath();
            string resultPath = GetResultPath();
            string errorPath = GetErrorPath();
            string reportRoot = Path.Combine(GetProjectRoot(), "Reports", "HeadedAll");
            string screenshotRoot = Path.Combine(reportRoot, "Screenshots");

            try
            {
                if (File.Exists(requestPath))
                {
                    File.Delete(requestPath);
                }

                Directory.CreateDirectory(reportRoot);
                Directory.CreateDirectory(screenshotRoot);

                Codingriver.Logger.Log("[UPilot Flow][HeadedAutoRun] Starting headed suite for Assets/Examples/Yaml");

                string yamlDirectory = Path.Combine(GetProjectRoot(), "Assets", "Examples", "Yaml");
                TestOptions options = UPilotFlowConfigurationService.Resolve(
                    new UPilotFlowExecutionSettings
                    {
                        Headed = true,
                        DebugOnFailure = false,
                        ReportOutputPath = reportRoot,
                        ScreenshotPath = screenshotRoot,
                        ScreenshotOnFailure = true,
                        StopOnFirstFailure = false,
                        ContinueOnStepFailure = false,
                        EnableVerboseLog = true,
                    });
                UPilotFlowExecutionBatchResult execution = await new UPilotFlowExecutionService().RunAsync(
                    new UPilotFlowExecutionRequest
                    {
                        YamlPaths = UPilotFlowPathResolver.Resolve(null, yamlDirectory),
                        Options = options,
                        SuiteName = "headed-all",
                    });
                TestSuiteResult suite = execution.Suite;

                File.WriteAllText(resultPath, BuildSummary(suite), Encoding.UTF8);
                if (File.Exists(errorPath))
                {
                    File.Delete(errorPath);
                }

                Codingriver.Logger.Log($"[UPilot Flow][HeadedAutoRun] Completed headed suite. total={suite.Total} passed={suite.Passed} failed={suite.Failed} errors={suite.Errors} skipped={suite.Skipped}");
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(reportRoot);
                File.WriteAllText(errorPath, ex.ToString(), Encoding.UTF8);
                Codingriver.Logger.LogException(ex);
            }
            finally
            {
                SessionState.EraseBool(SessionKey);
                _scheduled = false;
                AssetDatabase.Refresh();
            }
        }

        private static string BuildSummary(TestSuiteResult suite)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"total={suite.Total}");
            sb.AppendLine($"passed={suite.Passed}");
            sb.AppendLine($"failed={suite.Failed}");
            sb.AppendLine($"errors={suite.Errors}");
            sb.AppendLine($"skipped={suite.Skipped}");
            sb.AppendLine($"exitCode={suite.ExitCode}");
            sb.AppendLine($"startedAtUtc={suite.StartedAtUtc}");
            sb.AppendLine($"endedAtUtc={suite.EndedAtUtc}");
            sb.AppendLine();

            foreach (TestResult result in suite.CaseResults)
            {
                string errorCode = string.IsNullOrWhiteSpace(result.ErrorCode) ? string.Empty : result.ErrorCode;
                string errorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage) ? string.Empty : result.ErrorMessage.Replace("\r", " ").Replace("\n", " ");
                sb.AppendLine($"{result.CaseName}|{result.Status}|{result.DurationMs}|{errorCode}|{errorMessage}");
            }

            return sb.ToString();
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string GetRequestPath()
        {
            return Path.Combine(GetProjectRoot(), "Temp", "CodingRiver.UPilot.Flow.HeadedSuite.request");
        }

        private static string GetResultPath()
        {
            return Path.Combine(GetProjectRoot(), "Reports", "HeadedAll", "headed-suite-summary.txt");
        }

        private static string GetErrorPath()
        {
            return Path.Combine(GetProjectRoot(), "Reports", "HeadedAll", "headed-suite-error.txt");
        }
    }
}
