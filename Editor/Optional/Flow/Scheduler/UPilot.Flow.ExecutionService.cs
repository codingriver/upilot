using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow
{
    public sealed class UPilotFlowExecutionRequest
    {
        public IReadOnlyList<string> YamlPaths = Array.Empty<string>();
        public TestOptions Options = new TestOptions();
        public CancellationToken CancellationToken;
        public VisualElement RootOverride;
        public Func<string, string, bool> Filter;
        public Action<int, int, string> CaseStarted;
        public Action<int, int, string, TestResult> CaseCompleted;
        public Action<string, ExecutionContext> ContextReady;
        public string SuiteName;
        public bool WriteReports = true;
        public bool OverwriteUnifiedReport = true;
        public int CaseIndexOffset;
        public int TotalCases;
    }

    public sealed class UPilotFlowExecutedCase
    {
        public string YamlPath;
        public TestResult Result;
    }

    public sealed class UPilotFlowExecutionBatchResult
    {
        public TestSuiteResult Suite = new TestSuiteResult();
        public List<UPilotFlowExecutedCase> Cases = new List<UPilotFlowExecutedCase>();
        public bool Cancelled;
    }

    /// <summary>
    /// Shared execution loop for every UPilot Flow adapter.
    /// </summary>
    public sealed class UPilotFlowExecutionService
    {
        private readonly TestRunner _runner;
        private readonly YamlTestCaseParser _parser;
        private readonly UPilotFlowReportService _reports;

        public UPilotFlowExecutionService()
            : this(new TestRunner(), new YamlTestCaseParser(), new UPilotFlowReportService())
        {
        }

        internal UPilotFlowExecutionService(
            TestRunner runner,
            YamlTestCaseParser parser,
            UPilotFlowReportService reports)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        }

        public async Task<UPilotFlowExecutionBatchResult> RunAsync(UPilotFlowExecutionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            List<string> yamlPaths = request.YamlPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            if (yamlPaths.Count == 0)
            {
                throw new UPilotFlowException(ErrorCodes.TestCasePathInvalid, "No YAML files were provided.");
            }

            TestOptions options = UPilotFlowConfigurationService.Resolve(request.Options);
            var batch = new UPilotFlowExecutionBatchResult
            {
                Suite = new TestSuiteResult
                {
                    StartedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                },
            };
            int totalCases = request.TotalCases > 0 ? request.TotalCases : yamlPaths.Count;

            for (int index = 0; index < yamlPaths.Count; index++)
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    batch.Cancelled = true;
                    break;
                }

                string yamlPath = yamlPaths[index];
                string caseName = ResolveCaseName(yamlPath);
                if (request.Filter != null && !request.Filter(yamlPath, caseName))
                {
                    continue;
                }

                int caseIndex = request.CaseIndexOffset + index + 1;
                request.CaseStarted?.Invoke(caseIndex, totalCases, yamlPath);
                TestOptions caseOptions = options.Clone();
                caseOptions.GenerateSingleReport = yamlPaths.Count == 1;
                caseOptions.CaseIndex = caseIndex;
                caseOptions.TotalCases = totalCases;

                TestResult result;
                CancellationTokenRegistration cancellationRegistration = default;
                try
                {
                    result = await _runner.RunFileAsync(
                        yamlPath,
                        caseOptions,
                        request.RootOverride,
                        context =>
                        {
                            cancellationRegistration = request.CancellationToken.Register(
                                () => context.RuntimeController?.Stop());
                            request.ContextReady?.Invoke(yamlPath, context);
                        });
                }
                catch (OperationCanceledException)
                {
                    batch.Cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    string now = DateTimeOffset.UtcNow.ToString("O");
                    result = new TestResult
                    {
                        CaseName = caseName,
                        Status = TestStatus.Error,
                        StartedAtUtc = now,
                        EndedAtUtc = now,
                        ErrorCode = ex is UPilotFlowException flowException
                            ? flowException.ErrorCode
                            : ErrorCodes.CliExecutionError,
                        ErrorMessage = ex.Message,
                    };
                }
                finally
                {
                    cancellationRegistration.Dispose();
                }

                result.ReportMarkdownPath = new ReportPathBuilder().BuildCaseMarkdownPath(
                    options.ReportOutputPath,
                    result.CaseName);
                batch.Cases.Add(new UPilotFlowExecutedCase { YamlPath = yamlPath, Result = result });
                batch.Suite.CaseResults.Add(result);
                ApplyCounter(batch.Suite, result.Status);
                request.CaseCompleted?.Invoke(caseIndex, totalCases, yamlPath, result);

                if (options.StopOnFirstFailure
                    && (result.Status == TestStatus.Failed || result.Status == TestStatus.Error))
                {
                    break;
                }
            }

            batch.Suite.Total = batch.Suite.CaseResults.Count;
            batch.Suite.EndedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            batch.Suite.ExitCode = batch.Suite.Errors > 0 ? 2 : batch.Suite.Failed > 0 ? 1 : 0;
            if (request.WriteReports && batch.Suite.Total > 0)
            {
                _reports.WriteSuite(
                    batch.Suite,
                    options,
                    request.SuiteName,
                    request.OverwriteUnifiedReport,
                    writeArtifactManifest: true);
            }

            return batch;
        }

        private string ResolveCaseName(string yamlPath)
        {
            try
            {
                TestCaseDefinition definition = _parser.ParseFile(yamlPath);
                return string.IsNullOrWhiteSpace(definition.Name)
                    ? Path.GetFileNameWithoutExtension(yamlPath)
                    : definition.Name;
            }
            catch
            {
                return Path.GetFileNameWithoutExtension(yamlPath);
            }
        }

        private static void ApplyCounter(TestSuiteResult suite, TestStatus status)
        {
            switch (status)
            {
                case TestStatus.Passed:
                    suite.Passed++;
                    break;
                case TestStatus.Failed:
                    suite.Failed++;
                    break;
                case TestStatus.Error:
                    suite.Errors++;
                    break;
                case TestStatus.Skipped:
                    suite.Skipped++;
                    break;
            }
        }
    }
}
