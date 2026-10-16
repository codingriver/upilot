// -----------------------------------------------------------------------
// upilot Editor - https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

#if UPILOT_ENABLE_FLOW
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Flow.Tests
{
    public sealed class UPilotFlowLoggingTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void WarningsErrorsAndExceptionsBypassConsoleToggle(bool mirrorToConsole)
        {
            var previousConsole = CodingRiver.UPilot.Logger.LogToUnityConsole;
            var previousLevel = CodingRiver.UPilot.Logger.MinLevel;
            var marker = "flow-console-toggle-" + Guid.NewGuid().ToString("N");
            var exception = new InvalidOperationException(marker + "-exception");
            try
            {
                CodingRiver.UPilot.Logger.LogToUnityConsole = mirrorToConsole;
                CodingRiver.UPilot.Logger.MinLevel = CodingRiver.UPilot.Logger.LogLevel.Warning;
                LogAssert.Expect(LogType.Warning, "[WARN ] [UPilot.Flow] " + marker + " [console-test]");
                LogAssert.Expect(LogType.Error, "[ERROR] [UPilot.Flow] " + marker + " [console-test]");
                LogAssert.Expect(LogType.Error, "[ERROR] [UPilot.Flow] Exception: " + exception + " [console-test]");

                Codingriver.Logger.LogWarning(marker, "console-test");
                Codingriver.Logger.LogError(marker, "console-test");
                Codingriver.Logger.LogException(exception, "console-test");

                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                CodingRiver.UPilot.Logger.LogToUnityConsole = previousConsole;
                CodingRiver.UPilot.Logger.MinLevel = previousLevel;
            }
        }

        [TestCase(TestStatus.Passed, LogType.Log)]
        [TestCase(TestStatus.Skipped, LogType.Log)]
        [TestCase(TestStatus.Failed, LogType.Error)]
        [TestCase(TestStatus.Error, LogType.Error)]
        public void CompletionSummaryUsesStatusSpecificLogLevel(TestStatus status, LogType expectedLogType)
        {
            MethodInfo method = typeof(CodingRiver.UPilot.Flow.TestRunner).GetMethod(
                "LogCompletionSummary",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            Codingriver.Logger.LogLevel previousMinLevel = Codingriver.Logger.MinLevel;
            bool previousConsoleSetting = Codingriver.Logger.LogToUnityConsole;
            Type coreLoggerType = typeof(CodingRiver.UPilot.Logger);
            FieldInfo debugField = coreLoggerType.GetField("_debugWireLogsEnabled", BindingFlags.Static | BindingFlags.NonPublic);
            FieldInfo verboseField = coreLoggerType.GetField("_verboseLogsEnabled", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo setRuntimeOptions = coreLoggerType.GetMethod("SetRuntimeLogOptions", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(debugField, Is.Not.Null);
            Assert.That(verboseField, Is.Not.Null);
            Assert.That(setRuntimeOptions, Is.Not.Null);
            bool previousDebug = (bool)debugField.GetValue(null);
            bool previousVerbose = (bool)verboseField.GetValue(null);
            try
            {
                Codingriver.Logger.MinLevel = Codingriver.Logger.LogLevel.Debug;
                Codingriver.Logger.LogToUnityConsole = true;
                setRuntimeOptions.Invoke(null, new object[] { previousDebug, true });
                LogAssert.Expect(expectedLogType, new System.Text.RegularExpressions.Regex(
                    $"UPilot Flow.*状态={status}"));

                method.Invoke(null, new object[]
                {
                    new TestCaseDefinition { Name = "logging-contract" },
                    new TestOptions { CaseIndex = 1, TotalCases = 1 },
                    new TestResult { Status = status, DurationMs = 1 },
                });
            }
            finally
            {
                setRuntimeOptions.Invoke(null, new object[] { previousDebug, previousVerbose });
                Codingriver.Logger.MinLevel = previousMinLevel;
                Codingriver.Logger.LogToUnityConsole = previousConsoleSetting;
            }
        }
    }
}
#endif
