using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using YamlDotNet.Serialization;

namespace UnityUIFlow
{
    /// <summary>
    /// Builds standardized artifact paths.
    /// </summary>
    public sealed class ReportPathBuilder
    {
        public string EnsureDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new UnityUIFlowException(ErrorCodes.ReportOutputUnavailable, $"Report directory is unavailable: {path}");
            }

            Directory.CreateDirectory(path);
            return Path.GetFullPath(path);
        }

        public string BuildScreenshotPath(string rootPath, string caseName, int stepIndex, string tag)
        {
            string safeCaseName = UnityUIFlowUtility.SanitizeFileName(caseName);
            string safeTag = UnityUIFlowUtility.SanitizeFileName(tag);
            string fileName = $"{safeCaseName}-{stepIndex:D3}-{safeTag}-{DateTime.UtcNow:yyyyMMddHHmmss}.png";
            return Path.Combine(rootPath, fileName);
        }

        public string BuildCaseMarkdownPath(string rootPath, string caseName)
        {
            return Path.Combine(rootPath, "Cases", $"{UnityUIFlowUtility.SanitizeFileName(caseName)}.md");
        }

        public string BuildCaseJsonPath(string rootPath, string caseName)
        {
            return Path.Combine(rootPath, "Cases", $"{UnityUIFlowUtility.SanitizeFileName(caseName)}.json");
        }

        public string BuildSuiteMarkdownPath(string rootPath, string suiteName)
        {
            return Path.Combine(rootPath, "full_reports.md");
        }

        public string BuildSuiteJsonPath(string rootPath, string suiteName)
        {
            string fileName = string.IsNullOrWhiteSpace(suiteName)
                ? "suite-report.json"
                : $"suite-{UnityUIFlowUtility.SanitizeFileName(suiteName)}.json";
            return Path.Combine(rootPath, "Cases", fileName);
        }

        public string BuildSingleReportMarkdownPath(string rootPath)
        {
            return Path.Combine(rootPath, "single_reports.md");
        }

        public string BuildSingleReportJsonPath(string rootPath)
        {
            return Path.Combine(rootPath, "Cases", "single-report.json");
        }

        public string BuildArtifactsPath(string rootPath)
        {
            return Path.Combine(rootPath, "Artifacts", "artifacts.json");
        }
    }

    /// <summary>
    /// Manages screenshot capture and persistence.
    /// </summary>
    public class ScreenshotManager
    {
        public const string SourceWindowReadScreenPixel = "window-readscreenpixel";
        public const string SourceFocusedWindowReadScreenPixel = "focused-window-readscreenpixel";
        public const string SourceDisplayCaptureCrop = "display-capture-crop";
        public const string SourceFallbackTexture = "fallback-texture";

        private readonly TestOptions _options;
        private readonly ReportPathBuilder _pathBuilder = new ReportPathBuilder();
        private readonly Func<EditorWindow> _windowProvider;

        public ScreenshotManager(TestOptions options, Func<EditorWindow> windowProvider = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _windowProvider = windowProvider;
            _pathBuilder.EnsureDirectory(_options.ScreenshotPath);
        }

        public string LastCaptureSource { get; private set; }

        /// <summary>
        /// Captures a screenshot asynchronously.
        /// </summary>
        public virtual async Task<string> CaptureAsync(string caseName, int stepIndex, string tag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 64)
            {
                throw new UnityUIFlowException(ErrorCodes.ScreenshotArgumentInvalid, $"Screenshot tag is invalid: {tag}");
            }

            EditorWindow preferred = ResolveCaptureWindow();
            if (preferred == null || preferred != EditorWindow.focusedWindow)
            {
                LastCaptureSource = "skipped-unfocused";
                Codingriver.Logger.LogWarning($"[UnityUIFlow] 截图跳过：窗口未聚焦 (preferred={preferred?.GetType().Name}, focused={EditorWindow.focusedWindow?.GetType().Name})");
                return null;
            }

            string path = _pathBuilder.BuildScreenshotPath(_options.ScreenshotPath, caseName, stepIndex, tag);
            await EditorAsyncUtility.NextFrameAsync(cancellationToken);
            CaptureSync(path);
            Codingriver.Logger.Log($"[UnityUIFlow] 截图已保存 {path}");
            return path;
        }

        /// <summary>
        /// Captures a screenshot immediately.
        /// </summary>
        public string CaptureSync(string filePath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? _options.ScreenshotPath);

                LastCaptureSource = null;

                if (TryCaptureRealScreenshot(out byte[] pngBytes, out string captureSource) && pngBytes != null && pngBytes.Length > 0)
                {
                    LastCaptureSource = captureSource;
                    File.WriteAllBytes(filePath, pngBytes);
                }
                else
                {
                    LastCaptureSource = SourceFallbackTexture;
                    Codingriver.Logger.LogWarning($"[UnityUIFlow] 截图回退到纹理模式: {Path.GetFileName(filePath)}");
                    Texture2D fallbackTexture = CreateFallbackTexture();
                    try
                    {
                        File.WriteAllBytes(filePath, fallbackTexture.EncodeToPNG());
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(fallbackTexture);
                    }
                }

                return filePath;
            }
            catch (Exception ex)
            {
                throw new UnityUIFlowException(ErrorCodes.ScreenshotSaveFailed, $"Failed to save screenshot: {Path.GetFileName(filePath)}", ex);
            }
        }

        private bool TryCaptureRealScreenshot(out byte[] pngBytes, out string captureSource)
        {
            pngBytes = null;
            captureSource = null;

            EditorWindow preferredWindow = ResolveCaptureWindow();
            if (TryCaptureWindow(preferredWindow, out Texture2D windowTexture))
            {
                try
                {
                    pngBytes = windowTexture.EncodeToPNG();
                    captureSource = SourceWindowReadScreenPixel;
                    return pngBytes != null && pngBytes.Length > 0;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(windowTexture);
                }
            }

            if (TryCaptureWindow(EditorWindow.focusedWindow, out Texture2D focusedWindowTexture))
            {
                try
                {
                    pngBytes = focusedWindowTexture.EncodeToPNG();
                    captureSource = SourceFocusedWindowReadScreenPixel;
                    return pngBytes != null && pngBytes.Length > 0;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(focusedWindowTexture);
                }
            }

            if (TryCaptureFullDisplay(preferredWindow, out Texture2D displayTexture))
            {
                try
                {
                    pngBytes = displayTexture.EncodeToPNG();
                    captureSource = SourceDisplayCaptureCrop;
                    return pngBytes != null && pngBytes.Length > 0;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(displayTexture);
                }
            }

            return false;
        }

        private EditorWindow ResolveCaptureWindow()
        {
            try
            {
                EditorWindow provided = _windowProvider?.Invoke();
                if (provided != null)
                {
                    return provided;
                }
            }
            catch (Exception ex)
            {
                Codingriver.Logger.LogWarning($"[UnityUIFlow] Failed to resolve screenshot host window: {ex.Message}");
            }

            return EditorWindow.focusedWindow;
        }

        private static bool TryCaptureWindow(EditorWindow window, out Texture2D texture)
        {
            texture = null;
            if (window == null)
            {
                return false;
            }

            Rect windowRect = window.position;
            if (windowRect.width < 2f || windowRect.height < 2f)
            {
                return false;
            }

            int screenHeight = GetScreenHeight();
            if (screenHeight <= 0)
            {
                return false;
            }

            float pixelsPerPoint = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            int width = Mathf.Max(2, Mathf.RoundToInt(windowRect.width * pixelsPerPoint));
            int height = Mathf.Max(2, Mathf.RoundToInt(windowRect.height * pixelsPerPoint));
            Vector2 readPosition = new Vector2(
                Mathf.Max(0f, windowRect.xMin * pixelsPerPoint),
                Mathf.Max(0f, screenHeight - (windowRect.yMax * pixelsPerPoint)));

            Color[] pixels = ReadScreenPixels(readPosition, width, height);
            if (pixels == null || pixels.Length != width * height)
            {
                return false;
            }

            texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.SetPixels(pixels);
            texture.Apply();
            return true;
        }

        private static bool TryCaptureFullDisplay(EditorWindow window, out Texture2D capturedTexture)
        {
            capturedTexture = null;
            Texture2D fullTexture = null;
            try
            {
                fullTexture = ScreenCapture.CaptureScreenshotAsTexture();
                if (fullTexture == null)
                {
                    return false;
                }

                RectInt cropRect = BuildCropRect(window, fullTexture.width, fullTexture.height);
                if (cropRect.width < 2 || cropRect.height < 2)
                {
                    capturedTexture = fullTexture;
                    fullTexture = null;
                    return true;
                }

                int sourceY = Mathf.Clamp(fullTexture.height - cropRect.y - cropRect.height, 0, Mathf.Max(0, fullTexture.height - cropRect.height));
                Color[] pixels = fullTexture.GetPixels(cropRect.x, sourceY, cropRect.width, cropRect.height);
                var cropped = new Texture2D(cropRect.width, cropRect.height, TextureFormat.RGBA32, false);
                cropped.SetPixels(pixels);
                cropped.Apply();
                capturedTexture = cropped;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (fullTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(fullTexture);
                }
            }
        }

        private static RectInt BuildCropRect(EditorWindow window, int textureWidth, int textureHeight)
        {
            if (window == null)
            {
                return new RectInt(0, 0, textureWidth, textureHeight);
            }

            float pixelsPerPoint = Mathf.Max(1f, EditorGUIUtility.pixelsPerPoint);
            Rect rect = window.position;
            int x = Mathf.Clamp(Mathf.RoundToInt(rect.xMin * pixelsPerPoint), 0, Mathf.Max(0, textureWidth - 2));
            int y = Mathf.Clamp(Mathf.RoundToInt(rect.yMin * pixelsPerPoint), 0, Mathf.Max(0, textureHeight - 2));
            int width = Mathf.Clamp(Mathf.RoundToInt(rect.width * pixelsPerPoint), 2, Mathf.Max(2, textureWidth - x));
            int height = Mathf.Clamp(Mathf.RoundToInt(rect.height * pixelsPerPoint), 2, Mathf.Max(2, textureHeight - y));
            return new RectInt(x, y, width, height);
        }

        private static int GetScreenHeight()
        {
            if (Display.displays != null && Display.displays.Length > 0 && Display.displays[0] != null && Display.displays[0].systemHeight > 0)
            {
                return Display.displays[0].systemHeight;
            }

            return Screen.currentResolution.height;
        }

        private static Color[] ReadScreenPixels(Vector2 startPosition, int width, int height)
        {
            Type utilityType = Type.GetType("UnityEditorInternal.InternalEditorUtility, UnityEditor");
            if (utilityType == null)
            {
                return null;
            }

            var method = utilityType.GetMethod(
                "ReadScreenPixel",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(Vector2), typeof(int), typeof(int) },
                null);

            if (method == null)
            {
                return null;
            }

            return method.Invoke(null, new object[] { startPosition, width, height }) as Color[];
        }

        private static Texture2D CreateFallbackTexture()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 1f));
            texture.SetPixel(1, 0, new Color(0.3f, 0.3f, 0.3f, 1f));
            texture.SetPixel(0, 1, new Color(0.85f, 0.2f, 0.2f, 1f));
            texture.SetPixel(1, 1, new Color(0.9f, 0.9f, 0.9f, 1f));
            texture.Apply();
            return texture;
        }
    }

    /// <summary>
    /// Writes JSON result artifacts.
    /// </summary>
    public sealed class JsonResultWriter
    {
        private readonly ISerializer _serializer = new SerializerBuilder().JsonCompatible().Build();

        public void WriteCaseJson(TestResult result, string path)
        {
            File.WriteAllText(path, _serializer.Serialize(result), Encoding.UTF8);
        }

        public void WriteSuiteJson(TestSuiteResult result, string path)
        {
            File.WriteAllText(path, _serializer.Serialize(result), Encoding.UTF8);
        }

        public void WriteArtifactManifest(IEnumerable<string> paths, string path)
        {
            File.WriteAllText(path, _serializer.Serialize(paths), Encoding.UTF8);
        }
    }

    /// <summary>
    /// Produces Markdown case and suite reports.
    /// </summary>
    public sealed class MarkdownReporter : IExecutionReporter
    {
        private readonly ReporterOptions _options;
        private readonly ReportPathBuilder _pathBuilder = new ReportPathBuilder();
        private readonly JsonResultWriter _jsonWriter = new JsonResultWriter();
        private readonly Dictionary<string, List<StepReportEntry>> _buffer = new Dictionary<string, List<StepReportEntry>>(StringComparer.Ordinal);

        public MarkdownReporter(ReporterOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _pathBuilder.EnsureDirectory(_options.ReportRootPath);
            _pathBuilder.EnsureDirectory(_options.ScreenshotRootPath);
            _pathBuilder.EnsureDirectory(Path.Combine(_options.ReportRootPath, "Cases"));
            _pathBuilder.EnsureDirectory(Path.Combine(_options.ReportRootPath, "Artifacts"));
        }

        public void RecordAction(string stepId, string actionName, string message)
        {
            Codingriver.Logger.Log($"[UnityUIFlow] [{stepId}] {actionName}: {message}");
        }

        /// <summary>
        /// Records a step result to the current buffer.
        /// </summary>
        public void RecordStepResult(string caseName, StepResult result, IReadOnlyList<string> attachments)
        {
            if (!_buffer.TryGetValue(caseName, out List<StepReportEntry> entries))
            {
                entries = new List<StepReportEntry>();
                _buffer[caseName] = entries;
            }

            var entry = new StepReportEntry
            {
                CaseName = caseName,
                StepName = result.DisplayName,
                Status = result.Status,
                StartedAtUtc = result.StartedAtUtc,
                EndedAtUtc = result.EndedAtUtc,
                DurationMs = result.DurationMs,
                ScreenshotPath = result.ScreenshotPath,
                ScreenshotSource = result.ScreenshotSource,
                ErrorCode = result.ErrorCode,
                ErrorMessage = result.ErrorMessage,
                HostDriver = result.HostDriver,
                PointerDriver = result.PointerDriver,
                KeyboardDriver = result.KeyboardDriver,
                DriverDetails = result.DriverDetails,
            };

            if (!string.IsNullOrWhiteSpace(result.HostDriver))
            {
                entry.Attachments.Add($"host-driver:{result.HostDriver}");
            }

            if (!string.IsNullOrWhiteSpace(result.PointerDriver))
            {
                entry.Attachments.Add($"pointer-driver:{result.PointerDriver}");
            }

            if (!string.IsNullOrWhiteSpace(result.KeyboardDriver))
            {
                entry.Attachments.Add($"keyboard-driver:{result.KeyboardDriver}");
            }

            if (!string.IsNullOrWhiteSpace(result.DriverDetails))
            {
                entry.Attachments.Add($"driver-details:{result.DriverDetails}");
            }

            if (attachments != null)
            {
                entry.Attachments.AddRange(attachments);
            }

            if (result.Attachments != null && result.Attachments.Count > 0)
            {
                entry.Attachments.AddRange(result.Attachments);
            }

            entries.Add(entry);
        }

        /// <summary>
        /// Writes all per-case artifacts.
        /// </summary>
        public void WriteCaseReport(TestResult result)
        {
            ValidateCaseName(result.CaseName);
            string markdownPath = _pathBuilder.BuildCaseMarkdownPath(_options.ReportRootPath, result.CaseName);
            string jsonPath = _pathBuilder.BuildCaseJsonPath(_options.ReportRootPath, result.CaseName);
            var markdown = new StringBuilder();
            markdown.AppendLine($"# Test Report: {result.CaseName}");
            markdown.AppendLine();
            markdown.AppendLine($"**Status**: {result.Status}");
            markdown.AppendLine($"**Started At**: {FormatLocalTime(result.StartedAtUtc)}");
            markdown.AppendLine($"**Ended At**: {FormatLocalTime(result.EndedAtUtc)}");
            markdown.AppendLine($"**Duration**: {result.DurationMs}ms");
            markdown.AppendLine();
            markdown.AppendLine("## Step Details");
            markdown.AppendLine();
            markdown.AppendLine("| Step | Status | Duration(ms) | Driver | Driver Details | Error Code | Screenshot Source | Screenshot |");
            markdown.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

            if (_buffer.TryGetValue(result.CaseName, out List<StepReportEntry> entries))
            {
                foreach (StepReportEntry entry in entries)
                {
                    string screenshot = string.IsNullOrWhiteSpace(entry.ScreenshotPath)
                        ? string.Empty
                        : $"[View]({entry.ScreenshotPath.Replace('\\', '/')})";
                    string details = string.IsNullOrWhiteSpace(entry.DriverDetails) ? string.Empty : entry.DriverDetails.Replace("|", "/");
                    markdown.AppendLine($"| {entry.StepName} | {entry.Status} | {entry.DurationMs} | {BuildDriverSummary(entry.Attachments)} | {details} | {entry.ErrorCode ?? string.Empty} | {entry.ScreenshotSource ?? string.Empty} | {screenshot} |");
                }
            }

            File.WriteAllText(markdownPath, markdown.ToString(), Encoding.UTF8);
            _jsonWriter.WriteCaseJson(result, jsonPath);
        }

        /// <summary>
        /// Writes suite summary artifacts (full_reports.md).
        /// </summary>
        public void WriteSuiteReport(TestSuiteResult result)
        {
            string markdownPath = _pathBuilder.BuildSuiteMarkdownPath(_options.ReportRootPath, _options.SuiteName);
            string jsonPath = _pathBuilder.BuildSuiteJsonPath(_options.ReportRootPath, _options.SuiteName);
            var markdown = new StringBuilder();
            markdown.AppendLine("# Full Suite Report");
            markdown.AppendLine();
            markdown.AppendLine($"**Started At**: {FormatLocalTime(result.StartedAtUtc)}");
            markdown.AppendLine($"**Ended At**: {FormatLocalTime(result.EndedAtUtc)}");
            markdown.AppendLine();
            markdown.AppendLine($"**Total**: {result.Total} | **Passed**: {result.Passed} | **Failed**: {result.Failed} | **Errors**: {result.Errors} | **Skipped**: {result.Skipped}");
            markdown.AppendLine();
            markdown.AppendLine("## Cases");
            markdown.AppendLine();
            markdown.AppendLine("| Case | Status | Started At | Duration(ms) | Report |");
            markdown.AppendLine("| --- | --- | --- | --- | --- |");

            foreach (TestResult caseResult in result.CaseResults)
            {
                markdown.AppendLine($"| {caseResult.CaseName} | {caseResult.Status} | {FormatLocalTime(caseResult.StartedAtUtc)} | {caseResult.DurationMs} | {BuildCaseReportLink(caseResult)} |");
            }

            File.WriteAllText(markdownPath, markdown.ToString(), Encoding.UTF8);
            _jsonWriter.WriteSuiteJson(result, jsonPath);
        }

        /// <summary>
        /// Writes or appends to the unified suite report at Reports/full_reports.md.
        /// When overwrite is true, clears history and writes only the provided cases.
        /// When overwrite is false, appends cases to existing history (deduplicated by CaseName).
        /// </summary>
        public static void WriteUnifiedSuiteReport(TestSuiteResult result, bool overwrite)
        {
            string unifiedJsonPath = Path.Combine("Reports", "Cases", "unified-history.json");
            var pathBuilder = new ReportPathBuilder();
            pathBuilder.EnsureDirectory(Path.GetDirectoryName(unifiedJsonPath) ?? Path.Combine("Reports", "Cases"));

            TestSuiteResult targetSuite;
            var jsonWriter = new JsonResultWriter();
            var deserializer = new DeserializerBuilder().Build();

            if (!overwrite && File.Exists(unifiedJsonPath))
            {
                try
                {
                    string json = File.ReadAllText(unifiedJsonPath, Encoding.UTF8);
                    targetSuite = deserializer.Deserialize<TestSuiteResult>(json) ?? new TestSuiteResult();
                    targetSuite.CaseResults = targetSuite.CaseResults ?? new List<TestResult>();
                }
                catch
                {
                    targetSuite = new TestSuiteResult();
                }
            }
            else
            {
                targetSuite = new TestSuiteResult();
                targetSuite.StartedAtUtc = result.StartedAtUtc;
            }

            // Merge cases: remove duplicates by CaseName, then add new ones
            foreach (TestResult caseResult in result.CaseResults)
            {
                targetSuite.CaseResults.RemoveAll(c => c.CaseName == caseResult.CaseName);
                targetSuite.CaseResults.Add(caseResult);
            }

            targetSuite.EndedAtUtc = result.EndedAtUtc;
            targetSuite.Total = targetSuite.CaseResults.Count;
            targetSuite.Passed = targetSuite.CaseResults.Count(c => c.Status == TestStatus.Passed);
            targetSuite.Failed = targetSuite.CaseResults.Count(c => c.Status == TestStatus.Failed);
            targetSuite.Errors = targetSuite.CaseResults.Count(c => c.Status == TestStatus.Error);
            targetSuite.Skipped = targetSuite.CaseResults.Count(c => c.Status == TestStatus.Skipped);

            jsonWriter.WriteSuiteJson(targetSuite, unifiedJsonPath);

            var reporter = new MarkdownReporter(new ReporterOptions
            {
                ReportRootPath = "Reports",
                ScreenshotRootPath = Path.Combine("Reports", "Screenshots"),
                SuiteName = "unified",
            });
            reporter.WriteSuiteReport(targetSuite);
        }

        private static string FormatLocalTime(string utcIsoString)
        {
            if (string.IsNullOrWhiteSpace(utcIsoString))
            {
                return string.Empty;
            }
            if (DateTimeOffset.TryParse(utcIsoString, out DateTimeOffset dto))
            {
                return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            }
            return utcIsoString;
        }

        private string BuildCaseReportLink(TestResult caseResult)
        {
            if (!string.IsNullOrWhiteSpace(caseResult.ReportMarkdownPath))
            {
                string linkPath = caseResult.ReportMarkdownPath.Replace('\\', '/');
                string reportRoot = _options.ReportRootPath?.Replace('\\', '/').TrimEnd('/') ?? string.Empty;
                if (!string.IsNullOrEmpty(reportRoot) && linkPath.StartsWith(reportRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    linkPath = linkPath.Substring(reportRoot.Length + 1);
                }
                string linkText = Path.GetFileName(caseResult.ReportMarkdownPath);
                return $"[{linkText}]({linkPath})";
            }
            string caseFileName = Path.GetFileName(_pathBuilder.BuildCaseMarkdownPath(_options.ReportRootPath, caseResult.CaseName));
            return $"[Cases/{caseFileName}](Cases/{caseFileName})";
        }

        /// <summary>
        /// Writes single test report (single_reports.md).
        /// </summary>
        public void WriteSingleReport(TestResult result)
        {
            string markdownPath = _pathBuilder.BuildSingleReportMarkdownPath(_options.ReportRootPath);
            string jsonPath = _pathBuilder.BuildSingleReportJsonPath(_options.ReportRootPath);
            var markdown = new StringBuilder();
            markdown.AppendLine($"# Single Test Report: {result.CaseName}");
            markdown.AppendLine();
            markdown.AppendLine($"**Status**: {result.Status}");
            markdown.AppendLine($"**Started At**: {FormatLocalTime(result.StartedAtUtc)}");
            markdown.AppendLine($"**Ended At**: {FormatLocalTime(result.EndedAtUtc)}");
            markdown.AppendLine($"**Duration**: {result.DurationMs}ms");
            markdown.AppendLine();
            markdown.AppendLine("## Step Details");
            markdown.AppendLine();
            markdown.AppendLine("| Step | Status | Duration(ms) | Driver | Driver Details | Error Code | Screenshot Source | Screenshot |");
            markdown.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

            if (_buffer.TryGetValue(result.CaseName, out List<StepReportEntry> entries))
            {
                foreach (StepReportEntry entry in entries)
                {
                    string screenshot = string.IsNullOrWhiteSpace(entry.ScreenshotPath)
                        ? string.Empty
                        : $"[View]({entry.ScreenshotPath.Replace('\\', '/')})";
                    string details = string.IsNullOrWhiteSpace(entry.DriverDetails) ? string.Empty : entry.DriverDetails.Replace("|", "/");
                    markdown.AppendLine($"| {entry.StepName} | {entry.Status} | {entry.DurationMs} | {BuildDriverSummary(entry.Attachments)} | {details} | {entry.ErrorCode ?? string.Empty} | {entry.ScreenshotSource ?? string.Empty} | {screenshot} |");
                }
            }

            File.WriteAllText(markdownPath, markdown.ToString(), Encoding.UTF8);
            _jsonWriter.WriteCaseJson(result, jsonPath);
        }

        private static void ValidateCaseName(string caseName)
        {
            if (string.IsNullOrWhiteSpace(caseName))
            {
                throw new UnityUIFlowException(ErrorCodes.ReportWriteFailed, "Test case name cannot be empty.");
            }

            if (caseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new UnityUIFlowException(ErrorCodes.ReportWriteFailed, $"Test case name contains invalid file-name characters: {caseName}");
            }
        }

        private static string BuildDriverSummary(List<string> attachments)
        {
            if (attachments == null || attachments.Count == 0)
            {
                return string.Empty;
            }

            string pointer = null;
            string keyboard = null;
            string host = null;
            foreach (string attachment in attachments)
            {
                if (attachment != null && attachment.StartsWith("host-driver:", StringComparison.Ordinal))
                {
                    host = attachment.Substring("host-driver:".Length);
                    continue;
                }

                if (attachment != null && attachment.StartsWith("pointer-driver:", StringComparison.Ordinal))
                {
                    pointer = attachment.Substring("pointer-driver:".Length);
                    continue;
                }

                if (attachment != null && attachment.StartsWith("keyboard-driver:", StringComparison.Ordinal))
                {
                    keyboard = attachment.Substring("keyboard-driver:".Length);
                }
            }

            if (host == null && pointer == null && keyboard == null)
            {
                return string.Empty;
            }

            if (host == null && pointer == null)
            {
                return $"K={keyboard}";
            }

            if (host == null && keyboard == null)
            {
                return $"P={pointer}";
            }

            if (pointer == null && keyboard == null)
            {
                return $"H={host}";
            }

            if (pointer == null)
            {
                return $"H={host}; K={keyboard}";
            }

            if (keyboard == null)
            {
                return $"H={host}; P={pointer}";
            }

            return $"H={host}; P={pointer}; K={keyboard}";
        }
    }
}
