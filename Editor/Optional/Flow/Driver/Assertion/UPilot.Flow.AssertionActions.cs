using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static UnityEditor.TypeCache;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using InputSystemKey = UnityEngine.InputSystem.Key;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow
{
    public sealed class AssertVisibleAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string selector = parameters.TryGetValue("selector", out string s) ? s : string.Empty;
            context.Log($"assert_visible: {selector}");
            await new WaitForElementAction().ExecuteAsync(root, context, parameters);
            context.Log("assert_visible: passed");
        }
    }

    public sealed class AssertNotVisibleAction : IAction
    {
        public Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            string selector = ActionHelpers.Require(parameters, "assert_not_visible", "selector");
            int timeout = parameters.TryGetValue("timeout", out string timeoutLiteral)
                ? DurationParser.ParseToMilliseconds(timeoutLiteral, "assert_not_visible")
                : context.Options.DefaultTimeoutMs;

            context.Log($"assert_not_visible: {selector}, timeout={timeout}ms");
            return AssertAsync(selector, timeout);

            async Task AssertAsync(string currentSelector, int currentTimeout)
            {
                DateTimeOffset startedAt = DateTimeOffset.UtcNow;
                SelectorExpression compiled = new SelectorCompiler().Compile(currentSelector);
                while (true)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (!context.Finder.Exists(compiled, context.Root, true))
                    {
                        context.Log("assert_not_visible: passed");
                        return;
                    }

                    if (UPilotFlowUtility.DurationMs(startedAt, DateTimeOffset.UtcNow) >= currentTimeout)
                    {
                        throw new UPilotFlowException(ErrorCodes.ElementNotVisible, $"Element is still visible: {currentSelector}");
                    }

                    await EditorAsyncUtility.NextFrameAsync(context.CancellationToken);
                }
            }
        }
    }

    public sealed class AssertTextAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_text");
            string expected = ActionHelpers.Require(parameters, "assert_text", "expected");
            string actual = ActionHelpers.GetText(element);
            context.Log($"assert_text: expected={expected}, actual={actual}");
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"assert_text failed: expected '{expected}', actual '{actual}'");
            }

            context.Log("assert_text: passed");
        }
    }

    public sealed class AssertTextContainsAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_text_contains");
            string expected = ActionHelpers.Require(parameters, "assert_text_contains", "expected");
            string actual = ActionHelpers.GetText(element);
            context.Log($"assert_text_contains: expected token={expected}, actual={actual}");
            if (actual == null || actual.IndexOf(expected, StringComparison.Ordinal) < 0)
            {
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"assert_text_contains failed: missing '{expected}'");
            }

            context.Log("assert_text_contains: passed");
        }
    }

    public sealed class AssertValueAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_value");
            string expected = ActionHelpers.Require(parameters, "assert_value", "expected");

            if (AdvancedActionHelpers.TryReadValue(element, out object actualValue, out Type valueType)
                && AdvancedActionHelpers.TryConvertStringValue(expected, valueType, out object expectedValue))
            {
                string actualText = ActionHelpers.GetValueText(element);
                context.Log($"assert_value: expected={expected}, actual={actualText}");
                if (!AdvancedActionHelpers.ValuesEqual(actualValue, expectedValue, valueType))
                {
                    throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"assert_value failed: expected '{expected}', actual '{actualText}'");
                }

                return;
            }

            string actual = ActionHelpers.GetValueText(element);
            context.Log($"assert_value: expected={expected}, actual={actual}");
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"assert_value failed: expected '{expected}', actual '{actual}'");
            }
        }
    }

    public sealed class AssertEnabledAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_enabled");
            context.Log($"assert_enabled: {ActionContext.ElementInfo(element)} => {element.enabledInHierarchy}");
            if (!element.enabledInHierarchy)
            {
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"assert_enabled failed: {ActionContext.ElementInfo(element)} is disabled");
            }
        }
    }

    public sealed class AssertDisabledAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_disabled");
            context.Log($"assert_disabled: {ActionContext.ElementInfo(element)} => {element.enabledInHierarchy}");
            if (element.enabledInHierarchy)
            {
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"assert_disabled failed: {ActionContext.ElementInfo(element)} is enabled");
            }
        }
    }

    public sealed class AssertPropertyAction : IAction
    {
        public async Task ExecuteAsync(VisualElement root, ActionContext context, Dictionary<string, string> parameters)
        {
            VisualElement element = await ActionHelpers.RequireElementAsync(context, parameters, "assert_property");
            string propertyName = ActionHelpers.Require(parameters, "assert_property", "property");
            string expected = ActionHelpers.Require(parameters, "assert_property", "expected");

            PropertyInfo property = element.GetType().GetProperty(propertyName);
            if (property == null)
            {
                throw new UPilotFlowException(ErrorCodes.ActionParameterInvalid, $"assert_property property is invalid: {propertyName}");
            }

            object actual = property.GetValue(element);
            context.Log($"assert_property: {propertyName} expected={expected}, actual={actual}");
            if (!string.Equals(actual?.ToString(), expected, StringComparison.Ordinal))
            {
                throw new UPilotFlowException(ErrorCodes.ActionExecutionFailed, $"assert_property failed: expected '{expected}', actual '{actual}'");
            }

            context.Log("assert_property: passed");
        }
    }
}
