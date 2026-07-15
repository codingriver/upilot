using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace CodingRiver.UPilot.Flow
{
    /// <summary>
    /// Loads CLI defaults from a JSON config file.
    /// </summary>
    public sealed class ConfigFileLoader
    {
        private readonly IDeserializer _deserializer = new DeserializerBuilder().Build();

        /// <summary>
        /// Loads config file values.
        /// </summary>
        public Dictionary<string, object> Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                object raw = _deserializer.Deserialize<object>(File.ReadAllText(path));
                return YamlObjectReader.AsMap(raw, path);
            }
            catch (Exception ex)
            {
                throw new UPilotFlowException(ErrorCodes.CliConfigFileInvalid, $"配置文件解析失败：{path}，{ex.Message}", ex);
            }
        }
    }

    public static class UPilotFlowConfigResolver
    {
        private static readonly string[] DefaultCustomActionAssemblies =
        {
            "Assembly-CSharp",
            "Assembly-CSharp-Editor",
            "Assembly-CSharp-firstpass",
        };

        public static string GetDefaultConfigFilePath()
        {
            return Path.Combine(Directory.GetCurrentDirectory(), ".upilot-flow.json");
        }

        public static HashSet<string> GetCustomActionAssemblyWhitelist()
        {
            var result = new HashSet<string>(DefaultCustomActionAssemblies, StringComparer.Ordinal);
            Dictionary<string, object> config = TryLoadConfig(GetDefaultConfigFilePath());
            if (config == null || !config.TryGetValue("customActionAssemblies", out object value) || value == null)
            {
                return result;
            }

            List<object> assemblies;
            try
            {
                assemblies = YamlObjectReader.AsSequence(value, "customActionAssemblies");
            }
            catch
            {
                return result;
            }

            foreach (object assembly in assemblies)
            {
                string assemblyName = assembly?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(assemblyName))
                {
                    result.Add(assemblyName);
                }
            }

            return result;
        }

        private static Dictionary<string, object> TryLoadConfig(string path)
        {
            try
            {
                return new ConfigFileLoader().Load(path);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Parses UPilot Flow command-line parameters.
    /// </summary>
    public sealed class CommandLineOptionsParser
    {
        private readonly ConfigFileLoader _configFileLoader = new ConfigFileLoader();

        /// <summary>
        /// Parses current process args.
        /// </summary>
        public CliOptions Parse(string[] args = null, IDictionary<string, string> environmentVariables = null)
        {
            args ??= Environment.GetCommandLineArgs();
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> environment = CreateEnvironmentSnapshot(environmentVariables);

            for (int index = 0; index < args.Length; index++)
            {
                string current = args[index];
                if (!current.StartsWith("-upilotFlow.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (index == args.Length - 1)
                {
                    throw new UPilotFlowException(ErrorCodes.CliArgumentInvalid, $"命令行参数非法：{current}");
                }

                string key = current.Substring(1);
                if (raw.ContainsKey(key))
                {
                    throw new UPilotFlowException(ErrorCodes.CliArgumentInvalid, $"命令行参数非法：重复参数 {key}");
                }

                raw[key] = args[index + 1];
                index++;
            }

            string configFile = ReadString(
                raw,
                environment,
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
                "upilotFlow.configFile",
                "configFile",
                UPilotFlowConfigResolver.GetDefaultConfigFilePath());

            Dictionary<string, object> config = _configFileLoader.Load(configFile);
            var options = new CliOptions
            {
                YamlPath = ReadString(raw, environment, config, "upilotFlow.yamlPath", "yamlPath", null),
                YamlDirectory = ReadString(raw, environment, config, "upilotFlow.yamlDirectory", "yamlDirectory", null),
                Headed = ReadBool(raw, environment, config, "upilotFlow.headed", "headed", true),
                ReportPath = ReadString(raw, environment, config, "upilotFlow.reportPath", "reportPath", "Reports"),
                ScreenshotOnFailure = ReadBool(raw, environment, config, "upilotFlow.screenshotOnFailure", "screenshotOnFailure", true),
                ScreenshotPath = ReadString(raw, environment, config, "upilotFlow.screenshotPath", "screenshotPath", null),
                TestFilter = ReadString(raw, environment, config, "upilotFlow.testFilter", "testFilter", null),
                StopOnFirstFailure = ReadBool(raw, environment, config, "upilotFlow.stopOnFirstFailure", "stopOnFirstFailure", false),
                ContinueOnStepFailure = ReadBool(raw, environment, config, "upilotFlow.continueOnStepFailure", "continueOnStepFailure", false),
                DefaultTimeoutMs = ReadInt(raw, environment, config, "upilotFlow.defaultTimeoutMs", "defaultTimeoutMs", UPilotFlowSchema.DefaultStepTimeoutMs),
                PreStepDelayMs = ReadInt(raw, environment, config, "upilotFlow.preStepDelayMs", "preStepDelayMs", 0),
                RequireOfficialHost = ReadBool(raw, environment, config, "upilotFlow.requireOfficialHost", "requireOfficialHost", false),
                RequireOfficialPointerDriver = ReadBool(raw, environment, config, "upilotFlow.requireOfficialPointerDriver", "requireOfficialPointerDriver", false),
                RequireInputSystemKeyboardDriver = ReadBool(raw, environment, config, "upilotFlow.requireInputSystemKeyboardDriver", "requireInputSystemKeyboardDriver", false),
                EnableVerboseLog = ReadBool(raw, environment, config, "upilotFlow.verbose", "verbose", false),
                Nographics = HasFlag(args, "-nographics"),
                ConfigFile = configFile,
                ParsedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            };

            if (HasFlag(args, "-batchmode"))
            {
                throw new UPilotFlowException(ErrorCodes.CliArgumentInvalid, "-batchmode 模式已被禁用，请使用带窗口的编辑器模式执行测试。");
            }

            if (string.IsNullOrWhiteSpace(options.ScreenshotPath))
            {
                options.ScreenshotPath = Path.Combine(options.ReportPath, "Screenshots");
            }

            if (!string.IsNullOrWhiteSpace(options.TestFilter) && options.TestFilter.Length > 256)
            {
                throw new UPilotFlowException(ErrorCodes.CliFilterInvalid, "测试过滤表达式过长");
            }

            if (!string.IsNullOrWhiteSpace(options.YamlPath) && !string.IsNullOrWhiteSpace(options.YamlDirectory))
            {
                throw new UPilotFlowException(ErrorCodes.CliArgumentInvalid, "yamlPath 与 yamlDirectory 不能同时指定");
            }

            if (options.DefaultTimeoutMs < 100 || options.DefaultTimeoutMs > 600000)
            {
                throw new UPilotFlowException(ErrorCodes.CliArgumentInvalid, "defaultTimeoutMs 超出允许范围");
            }

            return options;
        }

        /// <summary>
        /// Converts CLI options into runtime options.
        /// </summary>
        public TestOptions ToTestOptions(CliOptions cliOptions)
        {
            return UPilotFlowConfigurationService.Resolve(new UPilotFlowExecutionSettings
            {
                Headed = cliOptions.Headed,
                DebugOnFailure = true,
                ScreenshotOnFailure = cliOptions.ScreenshotOnFailure,
                StopOnFirstFailure = cliOptions.StopOnFirstFailure,
                ContinueOnStepFailure = cliOptions.ContinueOnStepFailure,
                DefaultTimeoutMs = cliOptions.DefaultTimeoutMs,
                EnableVerboseLog = cliOptions.EnableVerboseLog,
                PreStepDelayMs = cliOptions.PreStepDelayMs,
                RequireOfficialHost = cliOptions.RequireOfficialHost,
                RequireOfficialPointerDriver = cliOptions.RequireOfficialPointerDriver,
                RequireInputSystemKeyboardDriver = cliOptions.RequireInputSystemKeyboardDriver,
                ReportOutputPath = cliOptions.ReportPath,
                ScreenshotPath = cliOptions.ScreenshotPath,
            });
        }

        private static Dictionary<string, string> CreateEnvironmentSnapshot(IDictionary<string, string> source)
        {
            if (source != null)
            {
                return new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                result[key] = entry.Value?.ToString();
            }

            return result;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ReadString(
            Dictionary<string, string> cli,
            Dictionary<string, string> environment,
            Dictionary<string, object> config,
            string cliKey,
            string configKey,
            string defaultValue)
        {
            if (cli.TryGetValue(cliKey, out string cliValue))
            {
                return cliValue;
            }

            if (TryReadEnvironmentValue(environment, configKey, out string environmentValue))
            {
                return environmentValue;
            }

            if (config.TryGetValue(configKey, out object configValue))
            {
                return configValue?.ToString();
            }

            return defaultValue;
        }

        private static bool ReadBool(
            Dictionary<string, string> cli,
            Dictionary<string, string> environment,
            Dictionary<string, object> config,
            string cliKey,
            string configKey,
            bool defaultValue)
        {
            if (cli.TryGetValue(cliKey, out string cliValue))
            {
                return ParseBool(cliKey, cliValue);
            }

            if (TryReadEnvironmentValue(environment, configKey, out string environmentValue))
            {
                return ParseBool(ToEnvironmentKey(configKey), environmentValue);
            }

            if (config.TryGetValue(configKey, out object configValue))
            {
                return ParseBool(configKey, configValue?.ToString());
            }

            return defaultValue;
        }

        private static int ReadInt(
            Dictionary<string, string> cli,
            Dictionary<string, string> environment,
            Dictionary<string, object> config,
            string cliKey,
            string configKey,
            int defaultValue)
        {
            if (cli.TryGetValue(cliKey, out string cliValue))
            {
                return ParseInt(cliKey, cliValue);
            }

            if (TryReadEnvironmentValue(environment, configKey, out string environmentValue))
            {
                return ParseInt(ToEnvironmentKey(configKey), environmentValue);
            }

            if (config.TryGetValue(configKey, out object configValue))
            {
                return ParseInt(configKey, configValue?.ToString());
            }

            return defaultValue;
        }

        private static bool TryReadEnvironmentValue(Dictionary<string, string> environment, string configKey, out string value)
        {
            if (environment != null && environment.TryGetValue(ToEnvironmentKey(configKey), out value))
            {
                return !string.IsNullOrWhiteSpace(value);
            }

            value = null;
            return false;
        }

        private static string ToEnvironmentKey(string configKey)
        {
            return "UNITY_UI_FLOW_" + Regex.Replace(configKey ?? string.Empty, "(?<!^)([A-Z])", "_$1").ToUpperInvariant();
        }

        private static bool ParseBool(string name, string value)
        {
            if (!bool.TryParse(value, out bool parsed))
            {
                throw new UPilotFlowException(ErrorCodes.CliArgumentInvalid, $"参数 {name} 的布尔值非法：{value}");
            }

            return parsed;
        }

        private static int ParseInt(string name, string value)
        {
            if (!int.TryParse(value, out int parsed))
            {
                throw new UPilotFlowException(ErrorCodes.CliArgumentInvalid, $"参数 {name} 的数值非法：{value}");
            }

            return parsed;
        }
    }

    /// <summary>
    /// Applies wildcard-based YAML filtering.
    /// </summary>
    public static class YamlTestCaseFilter
    {
        /// <summary>
        /// Returns true when the file or case name matches the filter.
        /// </summary>
        public static bool Match(string filter, string yamlPath, string caseName = null)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            string regexPattern = "^" + Regex.Escape(filter).Replace("\\*", ".*") + "$";
            string fileName = Path.GetFileNameWithoutExtension(yamlPath) ?? string.Empty;
            return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase)
                || (!string.IsNullOrWhiteSpace(caseName) && Regex.IsMatch(caseName, regexPattern, RegexOptions.IgnoreCase));
        }
    }

    /// <summary>
    /// Resolves suite exit codes.
    /// </summary>
    /// <summary>
    /// Unity executeMethod entry point for batch runs.
    /// </summary>
    public static class UPilotFlowCliEntry
    {
        /// <summary>
        /// Runs all YAML files under Assets using command-line options.
        /// </summary>
        public static async void RunAllFromCommandLine()
        {
            int exitCode = await RunAllAsync();
            UnityEditor.EditorApplication.Exit(exitCode);
        }

        public static async Task<int> RunAllAsync(string[] args = null)
        {
            int exitCode = 2;
            string configSource = args != null ? "命令行参数" : "默认配置";
            Codingriver.Logger.Log($"[UPilot Flow] CLI 批量执行开始 配置来源={configSource}");
            try
            {
                var parser = new CommandLineOptionsParser();
                CliOptions cliOptions = parser.Parse(args);
                TestOptions testOptions = parser.ToTestOptions(cliOptions);
                string suiteDirectory = string.IsNullOrWhiteSpace(cliOptions.YamlDirectory)
                    ? "Assets"
                    : cliOptions.YamlDirectory;
                List<string> yamlPaths = UPilotFlowPathResolver.Resolve(
                    string.IsNullOrWhiteSpace(cliOptions.YamlPath) ? null : new[] { cliOptions.YamlPath },
                    string.IsNullOrWhiteSpace(cliOptions.YamlPath) ? suiteDirectory : null);
                UPilotFlowExecutionBatchResult result = await new UPilotFlowExecutionService().RunAsync(
                    new UPilotFlowExecutionRequest
                    {
                        YamlPaths = yamlPaths,
                        Options = testOptions,
                        Filter = (path, caseName) => YamlTestCaseFilter.Match(cliOptions.TestFilter, path, caseName),
                        SuiteName = "cli",
                    });
                exitCode = result.Suite.ExitCode;
                Codingriver.Logger.Log($"[UPilot Flow] CLI 执行完成 退出码={exitCode}");
            }
            catch (UPilotFlowException ex)
            {
                exitCode = ex.ErrorCode == ErrorCodes.CliTestsFailed ? 1 : 2;
                Codingriver.Logger.LogError($"[UPilot Flow] CLI 执行异常 退出码={exitCode} 错误码={ex.ErrorCode}: {ex.Message}");
            }
            catch (Exception ex)
            {
                exitCode = 2;
                Codingriver.Logger.LogError($"[UPilot Flow] CLI 执行异常 退出码={exitCode}: {ex.Message}");
                Codingriver.Logger.LogException(ex);
            }
            return exitCode;
        }
    }
}
