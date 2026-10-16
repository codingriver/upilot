using System.IO;

namespace CodingRiver.UPilot
{
    /// <summary>Default visible outputs, relative to the owning Unity project. No legacy lookup.</summary>
    public static class UPilotOutputPaths
    {
        public const string Root = "Logs/UPilot";
        public const string UnityLog = Root + "/upilot.log";
        public const string StepLog = Root + "/step.log";
        public const string ServerLog = Root + "/mcp-server.log";
        public const string McpCalls = Root + "/McpCalls";
        public const string StepReports = Root + "/Reports/Steps";
        public const string AcceptanceReports = Root + "/Reports/Acceptance";
        public const string FlowReports = Root + "/Reports/Flow";
        public const string FlowDefaultReports = FlowReports + "/Default";
        public const string FlowTestRunnerReports = FlowReports + "/TestRunner";
        public const string FlowHeadedReports = FlowReports + "/HeadedAll";
        public const string FlowHistory = FlowReports + "/History";
        public const string E2EReports = Root + "/Reports/E2E";
        public const string ConsoleCaptures = Root + "/Console";
        public const string QuickDebugChecks = Root + "/Diagnostics/QuickDebug/ToolChecks";
        public const string QuickDebugObjectDump = Root + "/Diagnostics/QuickDebug/csharp_object_dump.log";
        public const string Profiler = Root + "/Diagnostics/Profiler";
        public const string Snapshots = Root + "/Diagnostics/Snapshots";
        public const string SnapshotComparisons = Root + "/Diagnostics/SnapshotComparisons";
        public const string Screenshots = Root + "/Diagnostics/Screenshots";
        public const string Dumps = Root + "/Diagnostics/Dumps";

        public static string Resolve(string relativePath, string projectRoot = null) =>
            Path.GetFullPath(Path.Combine(projectRoot ?? UPilotProjectConfig.ProjectRoot, relativePath));
    }
}
