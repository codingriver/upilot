using System;
using System.Text.RegularExpressions;

namespace UnityUIFlow
{
    /// <summary>
    /// Compiles IMGUI selector strings into <see cref="ImguiSelector"/> instances.
    /// </summary>
    public static class ImguiSelectorCompiler
    {
        // Supported selector patterns:
        //   gui(button)
        //   gui(button, text="OK")
        //   gui(button, index=3)
        //   gui(textfield, control_name="username")
        //   gui(group="Settings")
        //   gui(group="Settings" > button, text="Apply")
        //   gui(focused)

        private static readonly Regex SelectorRegex = new Regex(
            @"^\s*gui\s*\(\s*(?<inner>[^)]*)\)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static ImguiSelector Compile(string selectorText)
        {
            if (string.IsNullOrWhiteSpace(selectorText))
                throw new UnityUIFlowException(ErrorCodes.SelectorInvalid, "IMGUI selector is empty.");

            string trimmed = selectorText.Trim();

            // Handle group path syntax: gui(group="X" > button, text="Y")
            string groupPrefix = null;
            int pathSeparator = trimmed.IndexOf('>');
            if (pathSeparator > 0)
            {
                string groupPart = trimmed.Substring(0, pathSeparator).Trim();
                trimmed = trimmed.Substring(pathSeparator + 1).Trim();
                var groupSel = CompileSingle(groupPart);
                if (!string.IsNullOrEmpty(groupSel.Group) && string.IsNullOrEmpty(groupSel.Type))
                {
                    groupPrefix = groupSel.Group;
                }
            }

            var result = CompileSingle(trimmed);
            if (groupPrefix != null)
            {
                result.Group = groupPrefix;
            }

            return result;
        }

        private static ImguiSelector CompileSingle(string selectorText)
        {
            var match = SelectorRegex.Match(selectorText);
            if (!match.Success)
            {
                throw new UnityUIFlowException(ErrorCodes.SelectorInvalid,
                    $"IMGUI selector '{selectorText}' does not match expected pattern 'gui(type, args...)'.");
            }

            string inner = match.Groups["inner"].Value.Trim();
            var selector = new ImguiSelector();

            // Special case: gui(focused)
            if (string.Equals(inner, "focused", StringComparison.OrdinalIgnoreCase))
            {
                selector.Focused = true;
                return selector;
            }

            // Split by commas, respecting quoted strings
            var parts = SplitArguments(inner);
            if (parts.Count == 0)
            {
                throw new UnityUIFlowException(ErrorCodes.SelectorInvalid,
                    $"IMGUI selector '{selectorText}' has no arguments.");
            }

            // First part is the type or group definition
            string first = parts[0].Trim();
            if (first.StartsWith("group=", StringComparison.OrdinalIgnoreCase) ||
                first.StartsWith("group=\"", StringComparison.OrdinalIgnoreCase))
            {
                selector.Group = ExtractValue(first);
                selector.Type = "group";
            }
            else if (first.StartsWith("control_name=", StringComparison.OrdinalIgnoreCase) ||
                     first.StartsWith("control_name=\"", StringComparison.OrdinalIgnoreCase))
            {
                selector.ControlName = ExtractValue(first);
            }
            else
            {
                selector.Type = first.ToLowerInvariant();
            }

            // Remaining parts are key=value filters
            for (int i = 1; i < parts.Count; i++)
            {
                string part = parts[i].Trim();
                int eq = part.IndexOf('=');
                if (eq < 0) continue;

                string key = part.Substring(0, eq).Trim().ToLowerInvariant();
                string value = ExtractValue(part.Substring(eq + 1).Trim());

                switch (key)
                {
                    case "text":
                        selector.Text = value;
                        break;
                    case "index":
                        if (int.TryParse(value, out int idx))
                            selector.Index = idx;
                        break;
                    case "control_name":
                        selector.ControlName = value;
                        break;
                    case "group":
                        selector.Group = value;
                        break;
                }
            }

            return selector;
        }

        private static System.Collections.Generic.List<string> SplitArguments(string input)
        {
            var result = new System.Collections.Generic.List<string>();
            int depth = 0;
            bool inQuotes = false;
            int start = 0;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '\"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }
                if (inQuotes)
                    continue;

                if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    result.Add(input.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }

            if (start < input.Length)
                result.Add(input.Substring(start).Trim());

            return result;
        }

        private static string ExtractValue(string raw)
        {
            raw = raw.Trim();
            if (raw.StartsWith("\"") && raw.EndsWith("\""))
                return raw.Substring(1, raw.Length - 2);
            return raw;
        }
    }
}
