// -----------------------------------------------------------------------
// UPilot Editor - lifecycle MonoBehaviour target scope filtering.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodingRiver.UPilot
{
    internal static class UPilotMonoHookLifecycleFilter
    {
        internal static bool Includes(Type type, UPilotMonoHookSettings settings, out string reason)
        {
            if (type == null)
            {
                reason = "类型为空";
                return false;
            }

            settings = settings ?? UPilotMonoHookSettings.instance;
            string assemblyName = type.Assembly.GetName().Name ?? string.Empty;
            string namespaceName = type.Namespace ?? string.Empty;
            string typeName = type.FullName ?? type.Name ?? string.Empty;

            if (!MatchesInclude(assemblyName, settings.lifecycleAssemblyIncludes))
            {
                reason = "程序集未命中包含范围：" + assemblyName;
                return false;
            }
            if (MatchesAny(assemblyName, settings.lifecycleAssemblyExcludes))
            {
                reason = "程序集命中排除范围：" + assemblyName;
                return false;
            }
            if (!MatchesInclude(namespaceName, settings.lifecycleNamespaceIncludes))
            {
                reason = "命名空间未命中包含范围：" + namespaceName;
                return false;
            }
            if (MatchesAny(namespaceName, settings.lifecycleNamespaceExcludes))
            {
                reason = "命名空间命中排除范围：" + namespaceName;
                return false;
            }
            if (!MatchesInclude(typeName, settings.lifecycleTypeIncludes))
            {
                reason = "类型未命中包含范围：" + typeName;
                return false;
            }
            if (MatchesAny(typeName, settings.lifecycleTypeExcludes))
            {
                reason = "类型命中排除范围：" + typeName;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static bool MatchesInclude(string value, string patterns)
        {
            var parsed = ParsePatterns(patterns);
            return parsed.Length == 0 || parsed.Any(pattern => Matches(value, pattern));
        }

        private static bool MatchesAny(string value, string patterns)
        {
            return ParsePatterns(patterns).Any(pattern => Matches(value, pattern));
        }

        private static string[] ParsePatterns(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
            return value.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool Matches(string value, string pattern)
        {
            string expression = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(value ?? string.Empty, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
    }
}
