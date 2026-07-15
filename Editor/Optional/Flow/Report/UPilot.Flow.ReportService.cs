using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodingRiver.UPilot.Flow
{
    /// <summary>
    /// Shared report pipeline used by CLI, Headed, and MCP adapters.
    /// JSON is written first and is the authoritative report artifact.
    /// </summary>
    public sealed class UPilotFlowReportService
    {
        private readonly ReportPathBuilder _paths = new ReportPathBuilder();
        private readonly JsonResultWriter _jsonWriter = new JsonResultWriter();

        public void WriteSuite(
            TestSuiteResult suite,
            TestOptions options,
            string suiteName = null,
            bool overwriteUnified = true,
            bool writeArtifactManifest = true)
        {
            if (suite == null)
            {
                throw new ArgumentNullException(nameof(suite));
            }

            TestOptions resolved = UPilotFlowConfigurationService.Resolve(options);
            var reporterOptions = new ReporterOptions
            {
                ReportRootPath = resolved.ReportOutputPath,
                ScreenshotRootPath = resolved.ScreenshotPath,
                SuiteName = suiteName,
            };

            _paths.EnsureDirectory(reporterOptions.ReportRootPath);
            _paths.EnsureDirectory(reporterOptions.ScreenshotRootPath);
            _jsonWriter.WriteSuiteJson(
                suite,
                _paths.BuildSuiteJsonPath(reporterOptions.ReportRootPath, reporterOptions.SuiteName));

            new MarkdownReporter(reporterOptions).WriteSuiteReport(suite);
            MarkdownReporter.WriteUnifiedSuiteReport(suite, overwriteUnified);

            if (writeArtifactManifest)
            {
                WriteArtifactManifest(reporterOptions.ReportRootPath);
            }
        }

        public void WriteArtifactManifest(string reportRootPath)
        {
            string fullRoot = Path.GetFullPath(reportRootPath);
            if (!Directory.Exists(fullRoot))
            {
                throw new UPilotFlowException(ErrorCodes.CliReportPathInvalid, $"报告目录不可写：{reportRootPath}");
            }

            List<string> artifacts = Directory
                .GetFiles(fullRoot, "*.*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    string extension = Path.GetExtension(path);
                    return extension == ".md" || extension == ".json" || extension == ".png";
                })
                .Select(path => UPilotFlowUtility.EnsureRelativeTo(fullRoot, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _paths.EnsureDirectory(Path.Combine(reportRootPath, "Artifacts"));
            _jsonWriter.WriteArtifactManifest(artifacts, _paths.BuildArtifactsPath(reportRootPath));
        }
    }
}
