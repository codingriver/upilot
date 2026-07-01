using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using codingriver.upilot.UIFlow;

namespace codingriver.upilot.UIFlow.EditorAutomation
{
    [InitializeOnLoad]
    public static class UIFlowHeadedSuiteAutoRun
    {
        private const string SessionKey = "UIFlow.HeadedSuiteAutoRun.Active";
        private static bool _scheduled;

        static UIFlowHeadedSuiteAutoRun()
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

        private static async void RunRequestedSuite()
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

                Codingriver.Logger.Log("[UIFlow][HeadedAutoRun] Starting headed suite for Assets/Examples/Yaml");

                var runner = new TestRunner();
                TestSuiteResult suite = await runner.RunSuiteAsync(
                    Path.Combine(GetProjectRoot(), "Assets", "Examples", "Yaml"),
                    new TestOptions
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

                File.WriteAllText(resultPath, BuildSummary(suite), Encoding.UTF8);
                if (File.Exists(errorPath))
                {
                    File.Delete(errorPath);
                }

                Codingriver.Logger.Log($"[UIFlow][HeadedAutoRun] Completed headed suite. total={suite.Total} passed={suite.Passed} failed={suite.Failed} errors={suite.Errors} skipped={suite.Skipped}");
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
            return Path.Combine(GetProjectRoot(), "Temp", "UIFlow.HeadedSuite.request");
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
