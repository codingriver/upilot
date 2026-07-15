using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using YamlDotNet.Serialization;

namespace CodingRiver.UPilot.Flow
{
    public static class UPilotFlowSchema
    {
        public const int CurrentVersion = 2;
        public const int DefaultStepTimeoutMs = 10000;
    }

    [Serializable]
    public sealed class ActionDescriptor
    {
        public string Name;
        public string ActionType;
        public string Category;
        public string[] Parameters;
        public bool TargetRequired;
        public string DriverCapability;
        public int DefaultTimeoutMs;
        public bool HasSideEffects;
    }

    public static class ActionDescriptorFactory
    {
        public static ActionDescriptor Create(string actionName, Type actionType)
        {
            var category = GetCategory(actionName);
            return new ActionDescriptor
            {
                Name = actionName,
                ActionType = actionType?.FullName ?? string.Empty,
                Category = category,
                Parameters = new[] { "selector", "value", "expected", "timeout", "duration" },
                TargetRequired = RequiresTarget(actionName),
                DriverCapability = actionName.StartsWith("imgui_", StringComparison.Ordinal)
                    ? "imgui"
                    : category == "command" ? "editor" : "uitoolkit",
                DefaultTimeoutMs = UPilotFlowSchema.DefaultStepTimeoutMs,
                HasSideEffects = HasSideEffects(actionName),
            };
        }

        private static string GetCategory(string name)
        {
            if (name.StartsWith("imgui_", StringComparison.Ordinal)) return "imgui";
            if (name.StartsWith("assert_", StringComparison.Ordinal) || name.StartsWith("read_", StringComparison.Ordinal)) return "assertion";
            if (name.StartsWith("wait", StringComparison.Ordinal)) return "wait";
            if (name.Contains("menu") || name.Contains("popup")) return "menu";
            if (name.StartsWith("drag", StringComparison.Ordinal) || name is "scroll" or "hover" or "navigate_breadcrumb") return "navigation";
            if (name is "execute_command" or "validate_command") return "command";
            if (name == "screenshot") return "artifact";
            return "input";
        }

        private static bool RequiresTarget(string name)
        {
            return name is not ("wait" or "execute_command" or "validate_command" or "screenshot" or "menu_item");
        }

        private static bool HasSideEffects(string name)
        {
            return !name.StartsWith("assert_", StringComparison.Ordinal)
                   && !name.StartsWith("read_", StringComparison.Ordinal)
                   && !name.StartsWith("wait", StringComparison.Ordinal)
                   && name != "validate_command";
        }
    }

    [Serializable]
    public sealed class UPilotFlowSchemaDocument
    {
        public int schemaVersion = UPilotFlowSchema.CurrentVersion;
        public ActionDescriptor[] actions;
    }

    public static class UPilotFlowSchemaGenerator
    {
        public static string GenerateJson(IEnumerable<ActionDescriptor> descriptors)
        {
            var document = new UPilotFlowSchemaDocument
            {
                actions = descriptors.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            };
            return JsonUtility.ToJson(document, true);
        }
    }

    [Serializable]
    public sealed class UPilotFlowValidationResult
    {
        public string yamlPath;
        public bool ok;
        public int schemaVersion;
        public bool legacyFormat;
        public int actionCount;
        public List<string> errors = new List<string>();
    }

    [Serializable]
    public sealed class UPilotFlowMigrationResult
    {
        public string sourcePath;
        public string targetPath;
        public bool dryRun;
        public bool changed;
        public bool legacyFormatDetected;
        public int sourceSchemaVersion;
        public int targetSchemaVersion = UPilotFlowSchema.CurrentVersion;
        public List<string> fieldChanges = new List<string>();
        public List<string> invalidActions = new List<string>();
        public string error;
    }

    public sealed class UPilotFlowMigrationService
    {
        private readonly IDeserializer _deserializer = new DeserializerBuilder().Build();

        public UPilotFlowValidationResult Validate(string yamlPath)
        {
            var result = new UPilotFlowValidationResult { yamlPath = yamlPath };
            try
            {
                var definition = new YamlTestCaseParser().ParseFile(yamlPath);
                result.schemaVersion = definition.SchemaVersion;
                result.legacyFormat = definition.SchemaVersion < UPilotFlowSchema.CurrentVersion;
                var registry = new ActionRegistry();
                foreach (var step in EnumerateSteps(definition.Steps))
                {
                    if (string.IsNullOrWhiteSpace(step.Action)) continue;
                    result.actionCount++;
                    if (!registry.HasAction(step.Action))
                        result.errors.Add($"Unknown action: {step.Action}");
                }
                result.ok = result.errors.Count == 0;
            }
            catch (Exception ex)
            {
                result.errors.Add(ex.Message);
                result.ok = false;
            }
            return result;
        }

        public UPilotFlowMigrationResult Migrate(string sourcePath, string targetPath = null, bool dryRun = true)
        {
            var result = new UPilotFlowMigrationResult
            {
                sourcePath = sourcePath,
                targetPath = string.IsNullOrWhiteSpace(targetPath) ? sourcePath : targetPath,
                dryRun = dryRun,
            };
            try
            {
                var text = File.ReadAllText(sourcePath, Encoding.UTF8);
                var raw = _deserializer.Deserialize<object>(text);
                var root = YamlObjectReader.AsMap(raw, "root");
                result.sourceSchemaVersion = YamlObjectReader.GetNullableInt(root, "schemaVersion", false) ?? 1;
                result.legacyFormatDetected = result.sourceSchemaVersion < UPilotFlowSchema.CurrentVersion;

                var validation = Validate(sourcePath);
                result.invalidActions.AddRange(validation.errors.Where(item => item.StartsWith("Unknown action:", StringComparison.Ordinal)));
                if (result.sourceSchemaVersion > UPilotFlowSchema.CurrentVersion)
                    throw new UPilotFlowException(ErrorCodes.TestCaseSchemaInvalid, $"Unsupported schemaVersion: {result.sourceSchemaVersion}");

                if (result.sourceSchemaVersion == UPilotFlowSchema.CurrentVersion)
                    return result;

                result.changed = true;
                result.fieldChanges.Add($"schemaVersion: {result.sourceSchemaVersion} -> {UPilotFlowSchema.CurrentVersion}");
                if (!dryRun)
                {
                    var migrated = $"schemaVersion: {UPilotFlowSchema.CurrentVersion}{DetectNewLine(text)}{text}";
                    var directory = Path.GetDirectoryName(result.targetPath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllText(result.targetPath, migrated, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
            }
            return result;
        }

        private static string DetectNewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

        private static IEnumerable<StepDefinition> EnumerateSteps(IEnumerable<StepDefinition> steps)
        {
            if (steps == null) yield break;
            foreach (var step in steps)
            {
                if (step == null) continue;
                yield return step;
                if (step.RepeatWhile?.Steps == null) continue;
                foreach (var nested in EnumerateSteps(step.RepeatWhile.Steps))
                    yield return nested;
            }
        }
    }
}
