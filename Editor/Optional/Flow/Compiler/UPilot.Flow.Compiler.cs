using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace CodingRiver.UPilot.Flow
{
    public sealed class SelectorCompiler
    {
        /// <summary>
        /// Compiles a selector string.
        /// </summary>
        public SelectorExpression Compile(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                throw new UPilotFlowException(ErrorCodes.SelectorEmpty, "Selector cannot be empty.");
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
                        throw new UPilotFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細{selector}");
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
                throw new UPilotFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細{selector}");
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
                throw new UPilotFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細{text.Substring(start)}");
            }

            string content = text.Substring(index, end - index);
            string[] parts = content.Split(new[] { '=' }, 2);
            if (parts.Length != 2)
            {
                throw new UPilotFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細[{content}]");
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
                throw new UPilotFlowException(ErrorCodes.SelectorSyntaxInvalid, $"閫夋嫨鍣ㄨ娉曢敊璇細{text}");
            }

            return value;
        }
    }

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
                throw new UPilotFlowException(ErrorCodes.ExecutionPlanEmpty, $"Test case {definition.Name} produced no executable steps.");
            }

            if (plan.Steps.Count > 5000)
            {
                throw new UPilotFlowException(ErrorCodes.TestCaseSchemaInvalid, $"Test case {definition.Name} exceeds the compiled step limit.");
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
                throw new UPilotFlowException(ErrorCodes.ActionNotRegistered, $"Step {displayName} action {step.Action} is not registered.");
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
}
