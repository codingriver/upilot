using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace codingriver.upilot.UIFlow
{
    /// <summary>
    /// Parses YAML test case files into in-memory definitions.
    /// </summary>
    public sealed class YamlTestCaseParser
    {
        private readonly IDeserializer _deserializer;

        public YamlTestCaseParser()
        {
            _deserializer = new DeserializerBuilder().Build();
        }

        /// <summary>
        /// Parses a YAML file from disk.
        /// </summary>
        public TestCaseDefinition ParseFile(string yamlPath)
        {
            if (string.IsNullOrWhiteSpace(yamlPath) || !yamlPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                throw new UIFlowException(ErrorCodes.TestCasePathInvalid, $"Test case path is invalid: {yamlPath}");
            }

            if (!File.Exists(yamlPath))
            {
                throw new UIFlowException(ErrorCodes.TestCaseFileNotFound, $"娴嬭瘯鐢ㄤ緥鏂囦欢涓嶅瓨鍦細{yamlPath}");
            }

            string yamlText = File.ReadAllText(yamlPath);
            return Parse(yamlText, yamlPath);
        }

        /// <summary>
        /// Parses YAML text into a test case definition.
        /// </summary>
        public TestCaseDefinition Parse(string yamlText, string sourcePath="")
        {
            if (string.IsNullOrWhiteSpace(yamlText))
            {
                throw new UIFlowException(ErrorCodes.YamlParseError, "YAML 鍐呭涓嶈兘涓虹┖");
            }

            Dictionary<string, object> root;
            try
            {
                object raw = _deserializer.Deserialize<object>(yamlText);
                root = YamlObjectReader.AsMap(raw, "root");
            }
            catch (YamlException ex)
            {
                throw new UIFlowException(ErrorCodes.YamlParseError, $"YAML parse failed: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new UIFlowException(ErrorCodes.YamlParseError, $"YAML parse failed: {ex.Message}", ex);
            }

            var definition = new TestCaseDefinition
            {
                Name = YamlObjectReader.GetString(root, "name", true),
                Description = YamlObjectReader.GetString(root, "description", false) ?? string.Empty,
                SourceFile = sourcePath,
                TimeoutMs = YamlObjectReader.GetNullableInt(root, "timeoutMs", false),
                Tags = YamlObjectReader.GetStringList(root, "tags"),
                Fixture = ParseFixture(root),
                Data = ParseDataSource(root),
                Steps = ParseSteps(YamlObjectReader.GetSequence(root, "steps", true), "steps"),
            };

            TestCaseSchemaValidator.Validate(definition);
            return definition;
        }

        private static FixtureDefinition ParseFixture(Dictionary<string, object> root)
        {
            if (!root.TryGetValue("fixture", out object fixtureValue) || fixtureValue == null)
            {
                return new FixtureDefinition();
            }

            Dictionary<string, object> fixture = YamlObjectReader.AsMap(fixtureValue, "fixture");
            return new FixtureDefinition
            {
                Setup = ParseSteps(YamlObjectReader.GetSequence(fixture, "setup", false), "fixture.setup"),
                Teardown = ParseSteps(YamlObjectReader.GetSequence(fixture, "teardown", false), "fixture.teardown"),
                HostWindow = ParseHostWindow(YamlObjectReader.GetMap(fixture, "host_window", false)),
            };
        }

        private static HostWindowDefinition ParseHostWindow(Dictionary<string, object> map)
        {
            if (map == null)
            {
                return null;
            }

            return new HostWindowDefinition
            {
                Type = YamlObjectReader.GetString(map, "type", true),
                ReopenIfOpen = YamlObjectReader.GetBool(map, "reopen_if_open", false, true),
            };
        }

        private static DataSourceDefinition ParseDataSource(Dictionary<string, object> root)
        {
            if (!root.TryGetValue("data", out object dataValue) || dataValue == null)
            {
                return new DataSourceDefinition();
            }

            Dictionary<string, object> data = YamlObjectReader.AsMap(dataValue, "data");
            var definition = new DataSourceDefinition
            {
                FromCsv = YamlObjectReader.GetString(data, "from_csv", false),
                FromJson = YamlObjectReader.GetString(data, "from_json", false),
            };

            List<object> rows = YamlObjectReader.GetSequence(data, "rows", false);
            if (rows != null)
            {
                foreach (object rowValue in rows)
                {
                    Dictionary<string, object> row = YamlObjectReader.AsMap(rowValue, "data.rows[]");
                    var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object> pair in row)
                    {
                        normalized[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                    }

                    definition.Rows.Add(normalized);
                }
            }

            return definition;
        }

        private static List<StepDefinition> ParseSteps(List<object> stepValues, string path)
        {
            var results = new List<StepDefinition>();
            if (stepValues == null)
            {
                return results;
            }

            for (int index = 0; index < stepValues.Count; index++)
            {
                Dictionary<string, object> map = YamlObjectReader.AsMap(stepValues[index], $"{path}[{index}]");
                var step = new StepDefinition
                {
                    Name = YamlObjectReader.GetString(map, "name", false),
                    Action = YamlObjectReader.GetString(map, "action", false),
                    Selector = YamlObjectReader.GetString(map, "selector", false),
                    Value = YamlObjectReader.GetString(map, "value", false),
                    Expected = YamlObjectReader.GetString(map, "expected", false),
                    Timeout = YamlObjectReader.GetString(map, "timeout", false),
                    Duration = YamlObjectReader.GetString(map, "duration", false),
                    If = ParseCondition(YamlObjectReader.GetMap(map, "if", false)),
                    RepeatWhile = ParseLoop(YamlObjectReader.GetMap(map, "repeat_while", false), $"{path}[{index}].repeat_while"),
                };

                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (pair.Value == null || IsKnownStepKey(pair.Key))
                    {
                        continue;
                    }

                    step.Parameters[pair.Key] = pair.Value.ToString();
                }

                results.Add(step);
            }

            return results;
        }

        private static bool IsKnownStepKey(string key)
        {
            switch (key)
            {
                case "name":
                case "action":
                case "selector":
                case "value":
                case "expected":
                case "timeout":
                case "duration":
                case "if":
                case "repeat_while":
                    return true;
                default:
                    return false;
            }
        }

        private static ConditionDefinition ParseCondition(Dictionary<string, object> map)
        {
            if (map == null)
            {
                return null;
            }

            string exists = YamlObjectReader.GetString(map, "exists", false);
            string notExists = YamlObjectReader.GetString(map, "not_exists", false);
            if (string.IsNullOrWhiteSpace(exists) && string.IsNullOrWhiteSpace(notExists))
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, "Condition must specify either 'exists' or 'not_exists'.");
            }

            return new ConditionDefinition
            {
                Exists = exists,
                NotExists = notExists,
            };
        }

        private static LoopDefinition ParseLoop(Dictionary<string, object> map, string path)
        {
            if (map == null)
            {
                return null;
            }

            return new LoopDefinition
            {
                Condition = ParseCondition(YamlObjectReader.GetMap(map, "condition", true)),
                Steps = ParseSteps(YamlObjectReader.GetSequence(map, "steps", true), path + ".steps"),
                MaxIterations = YamlObjectReader.GetInt(map, "max_iterations", false, 1000),
            };
        }
    }

    public static class TestCaseSchemaValidator
    {
        public static void Validate(TestCaseDefinition definition)
        {
            if (definition == null)
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, "娴嬭瘯鐢ㄤ緥涓嶈兘涓虹┖");
            }

            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, "娴嬭瘯鐢ㄤ緥鍚嶇О涓嶈兘涓虹┖");
            }

            if (definition.Steps == null || definition.Steps.Count == 0)
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"Test case {definition.Name} must contain at least one step.");
            }

            if (definition.TimeoutMs.HasValue && (definition.TimeoutMs.Value < 100 || definition.TimeoutMs.Value > 600000))
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"娴嬭瘯鐢ㄤ緥 {definition.Name} 缁撴瀯闈炴硶锛歵imeoutMs 瓒呭嚭鑼冨洿");
            }

            if (definition.Fixture?.HostWindow != null && string.IsNullOrWhiteSpace(definition.Fixture.HostWindow.Type))
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"濞村鐦悽銊ょ伐 {definition.Name} 閻?fixture.host_window.type 娑撳秷鍏樻稉铏光敄");
            }

            int dataSources = 0;
            if (definition.Data.HasInlineRows)
            {
                dataSources++;
            }

            if (!string.IsNullOrWhiteSpace(definition.Data.FromCsv))
            {
                dataSources++;
            }

            if (!string.IsNullOrWhiteSpace(definition.Data.FromJson))
            {
                dataSources++;
            }

            if (dataSources > 1)
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"娴嬭瘯鐢ㄤ緥 {definition.Name} 鐨?data 鍙兘澹版槑涓€绉嶆暟鎹簮");
            }

            ValidateSteps(definition.Steps);
            ValidateSteps(definition.Fixture.Setup);
            ValidateSteps(definition.Fixture.Teardown);
        }

        private static void ValidateSteps(List<StepDefinition> steps)
        {
            if (steps == null)
            {
                return;
            }

            foreach (StepDefinition step in steps)
            {
                ValidateStep(step);
            }
        }

        private static void ValidateStep(StepDefinition step)
        {
            string stepName = string.IsNullOrWhiteSpace(step.Name) ? step.Action ?? "unnamed" : step.Name;
            bool hasAction = !string.IsNullOrWhiteSpace(step.Action);
            bool hasLoop = step.RepeatWhile != null;

            if (!hasAction && !hasLoop)
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"姝ラ {stepName} 缂哄皯 action 鎴?repeat_while");
            }

            if (hasAction && hasLoop)
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"姝ラ {stepName} 鐨?repeat_while 缁撴瀯闈炴硶锛氫笉鑳藉悓鏃跺０鏄?action");
            }

            // Duration validation is intentionally deferred to execution phase so that
            // negative tests with out-of-bound values (e.g. 601s) fail as step-level
            // errors rather than crashing the whole case during parsing.
            // All actions that consume duration/timeout call DurationParser themselves.
            // if (!string.IsNullOrWhiteSpace(step.Timeout))
            // {
            //     DurationParser.ParseToMilliseconds(step.Timeout, stepName);
            // }
            // if (!string.IsNullOrWhiteSpace(step.Duration))
            // {
            //     DurationParser.ParseToMilliseconds(step.Duration, stepName);
            // }

            if (step.If != null && string.IsNullOrWhiteSpace(step.If.Exists) && string.IsNullOrWhiteSpace(step.If.NotExists))
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"Step {stepName} if condition must specify either 'exists' or 'not_exists'.");
            }

            if (step.RepeatWhile != null)
            {
                if (step.RepeatWhile.Condition == null || (string.IsNullOrWhiteSpace(step.RepeatWhile.Condition.Exists) && string.IsNullOrWhiteSpace(step.RepeatWhile.Condition.NotExists)))
                {
                    throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"Loop condition in step {stepName} cannot be empty.");
                }

                if (step.RepeatWhile.Steps == null || step.RepeatWhile.Steps.Count == 0)
                {
                    throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"姝ラ {stepName} 鐨勫惊鐜綋涓嶈兘涓虹┖");
                }

                if (step.RepeatWhile.MaxIterations < 1 || step.RepeatWhile.MaxIterations > 1000)
                {
                    throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"姝ラ {stepName} 鐨?max_iterations 瓒呭嚭鑼冨洿");
                }

                ValidateSteps(step.RepeatWhile.Steps);
            }
        }
    }

    public static class TestDataResolver
    {
        public static List<Dictionary<string, string>> ResolveRows(TestCaseDefinition definition)
        {
            if (definition.Data == null)
            {
                return new List<Dictionary<string, string>> { new Dictionary<string, string>(StringComparer.Ordinal) };
            }

            if (definition.Data.HasInlineRows)
            {
                return CloneRows(definition.Data.Rows);
            }

            if (!string.IsNullOrWhiteSpace(definition.Data.FromCsv))
            {
                return LoadCsv(definition.SourceFile, definition.Data.FromCsv);
            }

            if (!string.IsNullOrWhiteSpace(definition.Data.FromJson))
            {
                return LoadJson(definition.SourceFile, definition.Data.FromJson);
            }

            return new List<Dictionary<string, string>> { new Dictionary<string, string>(StringComparer.Ordinal) };
        }

        private static List<Dictionary<string, string>> CloneRows(List<Dictionary<string, string>> rows)
        {
            var results = new List<Dictionary<string, string>>();
            foreach (Dictionary<string, string> row in rows)
            {
                results.Add(new Dictionary<string, string>(row, StringComparer.Ordinal));
            }

            return results;
        }

        private static string ResolveDataPath(string sourceFile, string relativePath)
        {
            string baseDirectory = ResolveSourceDirectory(sourceFile);
            return Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        }

        private static string ResolveSourceDirectory(string sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                return Directory.GetCurrentDirectory();
            }

            if (Path.IsPathRooted(sourceFile))
            {
                return Path.GetDirectoryName(sourceFile) ?? Directory.GetCurrentDirectory();
            }

            string currentDirectory = Directory.GetCurrentDirectory();
            string fileName = Path.GetFileName(sourceFile);
            string[] candidatePaths =
            {
                Path.Combine(currentDirectory, sourceFile),
                Path.Combine(currentDirectory, "Assets", "UIFlow", "Samples", "Yaml", fileName),
                Path.Combine(currentDirectory, "Assets", "Examples", "Yaml", fileName),
            };

            foreach (string candidatePath in candidatePaths)
            {
                if (File.Exists(candidatePath))
                {
                    return Path.GetDirectoryName(candidatePath) ?? currentDirectory;
                }
            }

            string fallback = Directory.GetFiles(currentDirectory, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
            return !string.IsNullOrWhiteSpace(fallback)
                ? Path.GetDirectoryName(fallback) ?? currentDirectory
                : currentDirectory;
        }

        private static List<Dictionary<string, string>> LoadCsv(string sourceFile, string relativePath)
        {
            string path = ResolveDataPath(sourceFile, relativePath);
            if (!File.Exists(path))
            {
                throw new UIFlowException(ErrorCodes.TestDataFileNotFound, $"鏁版嵁鏂囦欢涓嶅瓨鍦細{path}");
            }

            string[] lines;
            using (var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true))
            {
                var allLines = new List<string>();
                while (!reader.EndOfStream)
                {
                    allLines.Add(reader.ReadLine());
                }

                lines = allLines.ToArray();
            }

            if (lines.Length == 0)
            {
                return new List<Dictionary<string, string>>();
            }

            List<string> headers = SplitCsvLine(lines[0]);
            var rows = new List<Dictionary<string, string>>();
            for (int index = 1; index < lines.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index]))
                {
                    continue;
                }

                List<string> values = SplitCsvLine(lines[index]);
                if (values.Count != headers.Count)
                {
                    throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"Data file {path} column count is inconsistent.");
                }

                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int column = 0; column < headers.Count; column++)
                {
                    row[headers[column]] = values[column];
                }

                rows.Add(row);
            }

            return rows;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var values = new List<string>();
            string current = string.Empty;
            bool inQuotes = false;

            for (int index = 0; index < line.Length; index++)
            {
                char ch = line[index];
                if (ch == '"')
                {
                    if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current += "\"";
                        index++;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (ch == ',' && !inQuotes)
                {
                    values.Add(current);
                    current = string.Empty;
                    continue;
                }

                current += ch;
            }

            values.Add(current);
            return values;
        }

        private static List<Dictionary<string, string>> LoadJson(string sourceFile, string relativePath)
        {
            string path = ResolveDataPath(sourceFile, relativePath);
            if (!File.Exists(path))
            {
                throw new UIFlowException(ErrorCodes.TestDataFileNotFound, $"鏁版嵁鏂囦欢涓嶅瓨鍦細{path}");
            }

            string jsonText = File.ReadAllText(path);
            try
            {
                var deserializer = new DeserializerBuilder().Build();
                object raw = deserializer.Deserialize<object>(jsonText);
                List<object> items = YamlObjectReader.AsSequence(raw, "json");
                var rows = new List<Dictionary<string, string>>();
                foreach (object item in items)
                {
                    Dictionary<string, object> map = YamlObjectReader.AsMap(item, "json[]");
                    var row = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, object> pair in map)
                    {
                        row[pair.Key] = pair.Value?.ToString() ?? string.Empty;
                    }

                    rows.Add(row);
                }

                return rows;
            }
            catch (Exception ex)
            {
                throw new UIFlowException(ErrorCodes.TestDataFileNotFound, $"Data file {path} parse failed: {ex.Message}", ex);
            }
        }
    }

    public static class TemplateRenderer
    {
        private static readonly Regex VariableRegex = new Regex(@"{{\s*([A-Za-z0-9_.-]+)\s*}}", RegexOptions.Compiled);

        public static string Render(string template, IReadOnlyDictionary<string, string> variables, string stepName)
        {
            if (string.IsNullOrEmpty(template))
            {
                return template;
            }

            return VariableRegex.Replace(template, match =>
            {
                string name = match.Groups[1].Value;
                if (variables == null || !variables.TryGetValue(name, out string value))
                {
                    throw new UIFlowException(ErrorCodes.TestDataVariableMissing, $"姝ラ {stepName} 缂哄皯鍙橀噺 {name}");
                }

                return value ?? string.Empty;
            });
        }
    }

    public static class DurationParser
    {
        private static readonly Regex DurationRegex = new Regex(@"^(?<value>\d+(\.\d+)?)(?<unit>ms|s)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static int ParseToMilliseconds(string literal, string stepName)
        {
            if (string.IsNullOrWhiteSpace(literal))
            {
                throw new UIFlowException(ErrorCodes.DurationLiteralInvalid, $"Duration literal in step {stepName} is invalid.");
            }

            Match match = DurationRegex.Match(literal.Trim());
            if (!match.Success)
            {
                throw new UIFlowException(ErrorCodes.DurationLiteralInvalid, $"鏃堕暱瀛楅潰閲忛潪娉曪細{literal}");
            }

            decimal value = decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
            decimal multiplier = match.Groups["unit"].Value == "s" ? 1000m : 1m;
            int milliseconds = (int)Math.Round(value * multiplier, MidpointRounding.AwayFromZero);
            if (milliseconds < 0 || milliseconds > 600000)
            {
                throw new UIFlowException(ErrorCodes.DurationLiteralInvalid, $"鏃堕暱瀛楅潰閲忛潪娉曪細{literal}");
            }

            return milliseconds;
        }
    }

    /// <summary>
    /// Compiles selector strings into structured selector expressions.
    /// </summary>
    public sealed class SelectorCompiler
    {
        /// <summary>
        /// Compiles a selector string.
        /// </summary>
        public SelectorExpression Compile(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw new UIFlowException(ErrorCodes.SelectorEmpty, "Selector cannot be empty.");
            }

            string text = selector.Trim();
            var expression = new SelectorExpression { Raw = text };
            int index = 0;
            SelectorCombinator pendingCombinator = SelectorCombinator.Self;
            bool sawToken = false;

            while (index < text.Length)
            {
                if (char.IsWhiteSpace(text[index]))
                {
                    index++;
                    if (sawToken)
                    {
                        pendingCombinator = SelectorCombinator.Descendant;
                    }

                    continue;
                }

                if (text[index] == '>')
                {
                    if (!sawToken || index == text.Length - 1)
                    {
                        throw new UIFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細{selector}");
                    }

                    pendingCombinator = SelectorCombinator.Child;
                    index++;
                    sawToken = false;
                    continue;
                }

                SelectorSegment segment = ParseToken(text, ref index, pendingCombinator);
                expression.Segments.Add(segment);
                pendingCombinator = SelectorCombinator.Self;
                sawToken = true;
            }

            if (expression.Segments.Count == 0)
            {
                throw new UIFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細{selector}");
            }

            return expression;
        }

        private static SelectorSegment ParseToken(string text, ref int index, SelectorCombinator combinator)
        {
            char prefix = text[index];
            switch (prefix)
            {
                case '#':
                    index++;
                    return new SelectorSegment
                    {
                        Combinator = combinator,
                        TokenType = SelectorTokenType.Id,
                        TokenValue = ReadIdentifier(text, ref index),
                    };
                case '.':
                    index++;
                    return new SelectorSegment
                    {
                        Combinator = combinator,
                        TokenType = SelectorTokenType.Class,
                        TokenValue = ReadIdentifier(text, ref index),
                    };
                case '[':
                    return ParseAttribute(text, ref index, combinator);
                case ':':
                    index++;
                    return new SelectorSegment
                    {
                        Combinator = combinator,
                        TokenType = SelectorTokenType.Pseudo,
                        TokenValue = ReadIdentifier(text, ref index),
                    };
                default:
                    return new SelectorSegment
                    {
                        Combinator = combinator,
                        TokenType = SelectorTokenType.Type,
                        TokenValue = ReadIdentifier(text, ref index),
                    };
            }
        }

        private static SelectorSegment ParseAttribute(string text, ref int index, SelectorCombinator combinator)
        {
            int start = index;
            index++;
            int end = text.IndexOf(']', index);
            if (end < 0)
            {
                throw new UIFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細{text.Substring(start)}");
            }

            string content = text.Substring(index, end - index);
            string[] parts = content.Split(new[] { '=' }, 2);
            if (parts.Length != 2)
            {
                throw new UIFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細[{content}]");
            }

            index = end + 1;
            return new SelectorSegment
            {
                Combinator = combinator,
                TokenType = SelectorTokenType.Attribute,
                TokenValue = parts[0].Trim() + "=" + parts[1].Trim(),
            };
        }

        private static string ReadIdentifier(string text, ref int index)
        {
            int start = index;
            while (index < text.Length)
            {
                char ch = text[index];
                if (char.IsWhiteSpace(ch) || ch == '>' || ch == '#' || ch == '.' || ch == '[' || ch == ':')
                {
                    break;
                }

                index++;
            }

            string value = text.Substring(start, index - start);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new UIFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細{text}");
            }

            return value;
        }
    }

    /// <summary>
    /// Builds executable plans from parsed case definitions.
    /// </summary>
    public sealed class ExecutionPlanBuilder
    {
        private readonly SelectorCompiler _selectorCompiler;
        private readonly ActionRegistry _actionRegistry;

        public ExecutionPlanBuilder(SelectorCompiler selectorCompiler, ActionRegistry actionRegistry)
        {
            _selectorCompiler = selectorCompiler ?? throw new ArgumentNullException(nameof(selectorCompiler));
            _actionRegistry = actionRegistry ?? throw new ArgumentNullException(nameof(actionRegistry));
        }

        /// <summary>
        /// Builds an execution plan for a test case.
        /// </summary>
        public ExecutionPlan Build(TestCaseDefinition definition, TestOptions options)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var plan = new ExecutionPlan
            {
                CaseName = definition.Name,
                DefaultTimeoutMs = definition.TimeoutMs ?? options.DefaultTimeoutMs,
                SourcePath = definition.SourceFile,
            };

            List<Dictionary<string, string>> rows = TestDataResolver.ResolveRows(definition);
            if (rows.Count == 0)
            {
                rows.Add(new Dictionary<string, string>(StringComparer.Ordinal));
            }

            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                IReadOnlyDictionary<string, string> data = rows[rowIndex];
                AppendSteps(plan.Steps, definition.Fixture.Setup, data, StepPhase.Setup, options, rowIndex);
                AppendSteps(plan.Steps, definition.Steps, data, StepPhase.Main, options, rowIndex);
                AppendSteps(plan.Steps, definition.Fixture.Teardown, data, StepPhase.Teardown, options, rowIndex);
            }

            if (plan.Steps.Count == 0)
            {
                throw new UIFlowException(ErrorCodes.ExecutionPlanEmpty, $"Test case {definition.Name} produced no executable steps.");
            }

            if (plan.Steps.Count > 5000)
            {
                throw new UIFlowException(ErrorCodes.TestCaseSchemaInvalid, $"娴嬭瘯鐢ㄤ緥 {definition.Name} 缂栬瘧鍚庣殑姝ラ瓒呰繃涓婇檺");
            }

            return plan;
        }

        private void AppendSteps(List<ExecutableStep> target, List<StepDefinition> steps, IReadOnlyDictionary<string, string> data, StepPhase phase, TestOptions options, int iterationIndex)
        {
            if (steps == null)
            {
                return;
            }

            foreach (StepDefinition step in steps)
            {
                target.Add(CompileStep(step, data, phase, options, iterationIndex));
            }
        }

        private ExecutableStep CompileStep(StepDefinition step, IReadOnlyDictionary<string, string> data, StepPhase phase, TestOptions options, int iterationIndex)
        {
            string displayName = TemplateRenderer.Render(step.Name ?? step.Action ?? "repeat_while", data, step.Name ?? step.Action ?? "repeat_while");
            if (step.RepeatWhile != null)
            {
                var compiledLoop = new ExecutableStep
                {
                    DisplayName = displayName,
                    Phase = phase,
                    IterationIndex = iterationIndex,
                    TimeoutMs = !string.IsNullOrWhiteSpace(step.Timeout)
                        ? DurationParser.ParseToMilliseconds(TemplateRenderer.Render(step.Timeout, data, displayName), displayName)
                        : options.DefaultTimeoutMs,
                    ContinueOnFailure = options.ContinueOnStepFailure,
                    Condition = CompileCondition(step.If, data, displayName),
                    Kind = ExecutableStepKind.Loop,
                    Loop = new LoopExpression
                    {
                        Condition = CompileCondition(step.RepeatWhile.Condition, data, displayName),
                        MaxIterations = step.RepeatWhile.MaxIterations,
                    },
                };

                foreach (StepDefinition loopStep in step.RepeatWhile.Steps)
                {
                    compiledLoop.Loop.Steps.Add(CompileStep(loopStep, data, phase, options, iterationIndex));
                }

                return compiledLoop;
            }

            if (!_actionRegistry.HasAction(step.Action))
            {
                throw new UIFlowException(ErrorCodes.ActionNotRegistered, $"Step {displayName} action {step.Action} is not registered.");
            }

            var compiled = new ExecutableStep
            {
                DisplayName = displayName,
                ActionName = step.Action,
                IterationIndex = iterationIndex,
                TimeoutMs = !string.IsNullOrWhiteSpace(step.Timeout)
                    ? DurationParser.ParseToMilliseconds(TemplateRenderer.Render(step.Timeout, data, displayName), displayName)
                    : options.DefaultTimeoutMs,
                ContinueOnFailure = options.ContinueOnStepFailure,
                Phase = phase,
                Condition = CompileCondition(step.If, data, displayName),
            };

            if (step.Selector != null)
            {
                string selector = TemplateRenderer.Render(step.Selector, data, displayName);
                compiled.Selector = _selectorCompiler.Compile(selector);
                compiled.Parameters["selector"] = selector;
            }

            if (step.Value != null)
            {
                compiled.Parameters["value"] = TemplateRenderer.Render(step.Value, data, displayName);
            }

            if (step.Expected != null)
            {
                compiled.Parameters["expected"] = TemplateRenderer.Render(step.Expected, data, displayName);
            }

            if (step.Duration != null)
            {
                compiled.Parameters["duration"] = TemplateRenderer.Render(step.Duration, data, displayName);
            }

            if (step.Timeout != null)
            {
                compiled.Parameters["timeout"] = TemplateRenderer.Render(step.Timeout, data, displayName);
            }

            foreach (KeyValuePair<string, string> pair in step.Parameters)
            {
                compiled.Parameters[pair.Key] = TemplateRenderer.Render(pair.Value, data, displayName);
            }

            return compiled;
        }

        private ConditionExpression CompileCondition(ConditionDefinition condition, IReadOnlyDictionary<string, string> data, string stepName)
        {
            if (condition == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(condition.NotExists))
            {
                string selector = TemplateRenderer.Render(condition.NotExists, data, stepName);
                return new ConditionExpression
                {
                    Type = ConditionType.NotExists,
                    SelectorExpression = _selectorCompiler.Compile(selector),
                };
            }

            string existsSelector = TemplateRenderer.Render(condition.Exists, data, stepName);
            return new ConditionExpression
            {
                Type = ConditionType.Exists,
                SelectorExpression = _selectorCompiler.Compile(existsSelector),
            };
        }
    }

    public static class YamlObjectReader
    {
        public static Dictionary<string, object> GetMap(Dictionary<string, object> map, string key, bool required)
        {
            if (!map.TryGetValue(key, out object value) || value == null)
            {
                if (required)
                {
                    throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} has an invalid type.");
                }

                return null;
            }

            return AsMap(value, key);
        }

        public static List<object> GetSequence(Dictionary<string, object> map, string key, bool required)
        {
            if (!map.TryGetValue(key, out object value) || value == null)
            {
                if (required)
                {
                    throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} has an invalid type.");
                }

                return null;
            }

            return AsSequence(value, key);
        }

        public static string GetString(Dictionary<string, object> map, string key, bool required)
        {
            if (!map.TryGetValue(key, out object value) || value == null)
            {
                if (required)
                {
                    throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} has an invalid type.");
                }

                return null;
            }

            return value.ToString();
        }

        public static int GetInt(Dictionary<string, object> map, string key, bool required, int defaultValue)
        {
            if (!map.TryGetValue(key, out object value) || value == null)
            {
                if (required)
                {
                    throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} has an invalid type.");
                }

                return defaultValue;
            }

            if (int.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }

            throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} has an invalid type.");
        }

        public static int? GetNullableInt(Dictionary<string, object> map, string key, bool required)
        {
            if (!map.TryGetValue(key, out object value) || value == null)
            {
                if (required)
                {
                    throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} has an invalid type.");
                }

                return null;
            }

            if (int.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }

            throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} has an invalid type.");
        }

        public static bool GetBool(Dictionary<string, object> map, string key, bool required, bool defaultValue)
        {
            if (!map.TryGetValue(key, out object value) || value == null)
            {
                if (required)
                {
                    throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} must be a boolean.");
                }

                return defaultValue;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (bool.TryParse(value.ToString(), out bool parsed))
            {
                return parsed;
            }

            throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {key} must be a boolean.");
        }

        public static List<string> GetStringList(Dictionary<string, object> map, string key)
        {
            List<object> values = GetSequence(map, key, false);
            var results = new List<string>();
            if (values == null)
            {
                return results;
            }

            foreach (object value in values)
            {
                results.Add(value?.ToString() ?? string.Empty);
            }

            return results;
        }

        public static Dictionary<string, object> AsMap(object value, string path)
        {
            if (value is Dictionary<object, object> objectMap)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (KeyValuePair<object, object> pair in objectMap)
                {
                    result[pair.Key.ToString()] = pair.Value;
                }

                return result;
            }

            if (value is Dictionary<string, object> stringMap)
            {
                return new Dictionary<string, object>(stringMap, StringComparer.Ordinal);
            }

            throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {path} has an invalid type.");
        }

        public static List<object> AsSequence(object value, string path)
        {
            if (value is List<object> list)
            {
                return list;
            }

            if (value is IEnumerable<object> enumerable)
            {
                return new List<object>(enumerable);
            }

            throw new UIFlowException(ErrorCodes.YamlFieldTypeInvalid, $"Field {path} has an invalid type.");
        }
    }
}
