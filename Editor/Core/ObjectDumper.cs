using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CodingRiver.UPilot
{
    internal static class ObjectDumper
    {
        internal static readonly HashSet<string> DefaultSkipTypes = new()
        {
            "System.IntPtr",
            "System.UIntPtr",
            "System.RuntimeType",
            "System.RuntimeMethodHandle",
            "System.RuntimeFieldHandle",
            "System.Threading.Thread",
        };

        private static readonly HashSet<string> LeafTypeNames = new()
        {
            "System.Boolean", "System.Char", "System.String",
            "System.SByte", "System.Byte", "System.Int16", "System.UInt16",
            "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
            "System.Single", "System.Double", "System.Decimal",
            "System.Guid", "System.DateTime", "System.DateTimeOffset",
            "System.TimeSpan", "System.Uri", "System.Version",
        };

        public static ObjectDumpNodeJson Dump(
            object value,
            int maxDepth,
            int maxFieldsPerNode,
            int maxTotalNodes,
            bool includeStatic,
            HashSet<string> ignoreTypes,
            ref int totalNodes)
        {
            var visited = new HashSet<object>(new ReferenceEqualityComparer());
            return Walk(value, "$", 0, maxDepth, maxFieldsPerNode,
                        maxTotalNodes, includeStatic, ignoreTypes, visited, ref totalNodes);
        }

        private static ObjectDumpNodeJson Walk(
            object value, string name, int depth, int maxDepth,
            int maxFieldsPerNode, int maxTotalNodes,
            bool includeStatic, HashSet<string> ignoreTypes,
            HashSet<object> visited, ref int totalNodes)
        {
            if (++totalNodes > maxTotalNodes)
                return new ObjectDumpNodeJson
                {
                    name = name,
                    value = "(truncated: max total nodes)",
                    depth = depth,
                };

            if (value == null)
                return new ObjectDumpNodeJson
                {
                    name = name,
                    value = "null",
                    declaredType = "(null)",
                    depth = depth,
                };

            Type type = value.GetType();
            string typeFullName = type.FullName ?? type.Name;
            var node = new ObjectDumpNodeJson
            {
                name = name,
                declaredType = typeFullName,
                runtimeType = typeFullName,
                depth = depth,
            };

            if (ignoreTypes != null && ignoreTypes.Contains(typeFullName))
            {
                node.value = ToStringBounded(value, 256);
                return node;
            }

            if (!type.IsValueType && !visited.Add(value))
            {
                node.value = "(circular ref: " + typeFullName + ")";
                return node;
            }

            if (IsLeafType(type) || depth >= maxDepth)
            {
                node.value = FormatLeafValue(value, type);
                if (depth >= maxDepth)
                    node.value = node.value + " [max depth]";
                return node;
            }

            if (type.IsArray)
            {
                var array = (Array)value;
                var items = new List<ObjectDumpNodeJson>();
                for (int i = 0; i < array.Length && i < maxFieldsPerNode; i++)
                {
                    if (totalNodes >= maxTotalNodes) break;
                    var child = Walk(array.GetValue(i), "[" + i + "]", depth + 1,
                                     maxDepth, maxFieldsPerNode, maxTotalNodes,
                                     includeStatic, ignoreTypes, visited, ref totalNodes);
                    child.declaredType = type.GetElementType()?.FullName;
                    items.Add(child);
                }
                if (array.Length > maxFieldsPerNode)
                    items.Add(new ObjectDumpNodeJson
                    {
                        name = "...",
                        value = "(" + (array.Length - maxFieldsPerNode) + " more items, total " + array.Length + ")",
                        depth = depth + 1,
                    });
                node.children = items.ToArray();
                return node;
            }

            if (value is string)
            {
                node.value = "\"" + EscapeString((string)value) + "\"";
                return node;
            }

            if (value is IEnumerable enumerable)
            {
                var items = new List<ObjectDumpNodeJson>();
                int index = 0;
                foreach (var item in enumerable)
                {
                    if (index >= maxFieldsPerNode || totalNodes >= maxTotalNodes) break;
                    var child = Walk(item, "[" + index + "]", depth + 1,
                                     maxDepth, maxFieldsPerNode, maxTotalNodes,
                                     includeStatic, ignoreTypes, visited, ref totalNodes);
                    items.Add(child);
                    index++;
                }
                if (index >= maxFieldsPerNode)
                    items.Add(new ObjectDumpNodeJson
                    {
                        name = "...",
                        value = "(truncated: ≥" + maxFieldsPerNode + " items)",
                        depth = depth + 1,
                    });
                node.children = items.ToArray();
                return node;
            }

            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            if (includeStatic)
                flags |= BindingFlags.Static | BindingFlags.FlattenHierarchy;

            var children = new List<ObjectDumpNodeJson>();

            foreach (var f in type.GetFields(flags | BindingFlags.NonPublic))
            {
                if (totalNodes >= maxTotalNodes) break;
                if (ShouldSkipField(f)) continue;

                string fieldTypeName = f.FieldType.FullName ?? f.FieldType.Name;

                if (ignoreTypes != null && ignoreTypes.Contains(fieldTypeName))
                {
                    children.Add(new ObjectDumpNodeJson
                    {
                        name = f.Name,
                        value = "(ignored: " + fieldTypeName + ")",
                        declaredType = fieldTypeName,
                        isStatic = f.IsStatic,
                        depth = depth + 1,
                    });
                    continue;
                }

                try
                {
                    object childValue = null;
                    try { childValue = f.GetValue(value); }
                    catch { }

                    var child = Walk(childValue, f.Name, depth + 1, maxDepth,
                                     maxFieldsPerNode, maxTotalNodes, includeStatic,
                                     ignoreTypes, visited, ref totalNodes);
                    child.isStatic = f.IsStatic;
                    child.declaredType = fieldTypeName;
                    if (childValue != null)
                    {
                        Type childType = childValue.GetType();
                        if (childType != f.FieldType)
                            child.runtimeType = childType.FullName ?? childType.Name;
                    }
                    children.Add(child);
                }
                catch (Exception ex)
                {
                    children.Add(new ObjectDumpNodeJson
                    {
                        name = f.Name,
                        value = "(error: " + ex.GetType().Name + " - " + ex.Message + ")",
                        declaredType = fieldTypeName,
                        isStatic = f.IsStatic,
                        depth = depth + 1,
                    });
                }
            }

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (totalNodes >= maxTotalNodes) break;
                if (p.GetIndexParameters().Length > 0) continue;
                if (!p.CanRead) continue;

                string propTypeName = p.PropertyType.FullName ?? p.PropertyType.Name;

                if (ignoreTypes != null && ignoreTypes.Contains(propTypeName))
                {
                    children.Add(new ObjectDumpNodeJson
                    {
                        name = "." + p.Name,
                        value = "(ignored: " + propTypeName + ")",
                        declaredType = propTypeName,
                        depth = depth + 1,
                    });
                    continue;
                }

                try
                {
                    object childValue = null;
                    try { childValue = p.GetValue(value, null); }
                    catch { }

                    var child = Walk(childValue, "." + p.Name, depth + 1, maxDepth,
                                     maxFieldsPerNode, maxTotalNodes, includeStatic,
                                     ignoreTypes, visited, ref totalNodes);
                    child.declaredType = propTypeName;
                    if (childValue != null)
                    {
                        Type childType = childValue.GetType();
                        if (childType != p.PropertyType)
                            child.runtimeType = childType.FullName ?? childType.Name;
                    }
                    children.Add(child);
                }
                catch (Exception ex)
                {
                    children.Add(new ObjectDumpNodeJson
                    {
                        name = "." + p.Name,
                        value = "(error: " + ex.GetType().Name + " - " + ex.Message + ")",
                        declaredType = propTypeName,
                        depth = depth + 1,
                    });
                }
            }

            if (includeStatic)
            {
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                {
                    if (totalNodes >= maxTotalNodes) break;
                    if (p.GetIndexParameters().Length > 0) continue;
                    if (!p.CanRead) continue;
                    if (children.Any(c => c.name == "." + p.Name)) continue;

                    string propTypeName = p.PropertyType.FullName ?? p.PropertyType.Name;

                    if (ignoreTypes != null && ignoreTypes.Contains(propTypeName))
                        continue;

                    try
                    {
                        object childValue = null;
                        try { childValue = p.GetValue(null, null); }
                        catch { }

                        var child = Walk(childValue, "static " + p.Name, depth + 1, maxDepth,
                                         maxFieldsPerNode, maxTotalNodes, includeStatic,
                                         ignoreTypes, visited, ref totalNodes);
                        child.isStatic = true;
                        child.declaredType = propTypeName;
                        if (childValue != null)
                        {
                            Type childType = childValue.GetType();
                            if (childType != p.PropertyType)
                                child.runtimeType = childType.FullName ?? childType.Name;
                        }
                        children.Add(child);
                    }
                    catch { }
                }
            }

            node.children = children.ToArray();
            return node;
        }

        private static bool ShouldSkipField(FieldInfo f)
        {
            if (f.IsSpecialName) return true;
            if (f.IsLiteral) return true;
            if (f.FieldType == typeof(IntPtr) || f.FieldType == typeof(UIntPtr)) return true;
            return false;
        }

        private static bool IsLeafType(Type type)
        {
            if (type.IsPrimitive) return true;
            if (type.IsEnum) return true;
            if (type == typeof(string)) return true;
            if (type == typeof(decimal)) return true;
            if (typeof(Type).IsAssignableFrom(type)) return true;

            string fullName = type.FullName;
            if (fullName != null && LeafTypeNames.Contains(fullName)) return true;

            return false;
        }

        private static string FormatLeafValue(object value, Type type)
        {
            if (value == null) return "null";
            if (type == typeof(string))
                return "\"" + EscapeString((string)value) + "\"";
            if (type.IsEnum)
                return type.Name + "." + value;
            if (typeof(Type).IsAssignableFrom(type))
                return ((Type)value).FullName ?? ((Type)value).Name;
            if (value is IFormattable fmt)
                return fmt.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        private static string ToStringBounded(object value, int maxLen)
        {
            if (value == null) return "null";
            string s = value.ToString();
            return s.Length <= maxLen ? s : s.Substring(0, maxLen) + "…";
        }

        private static string EscapeString(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string FormatAsText(ObjectDumpNodeJson root, string indent = "  ")
        {
            var sb = new StringBuilder();
            FormatNodeText(sb, root, "", indent ?? "  ");
            return sb.ToString();
        }

        private static void FormatNodeText(StringBuilder sb,
            ObjectDumpNodeJson node, string prefix, string indent)
        {
            sb.Append(prefix);
            if (node.isStatic) sb.Append("static ");
            sb.Append(node.name);

            if (node.runtimeType != null && node.declaredType != null &&
                node.runtimeType != node.declaredType)
                sb.Append(" (").Append(node.declaredType).Append(" -> ").Append(node.runtimeType).Append(')');
            else if (node.declaredType != null && node.name != "$")
                sb.Append(" (").Append(node.declaredType).Append(')');

            if (node.children == null || node.children.Length == 0)
            {
                sb.Append(": ").Append(node.value ?? "null").AppendLine();
            }
            else
            {
                sb.AppendLine();
                foreach (var child in node.children)
                    FormatNodeText(sb, child, prefix + indent, indent);
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            int IEqualityComparer<object>.GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }
    }

    [Serializable]
    public sealed class ObjectDumpMessage
    {
        public ObjectDumpPayload payload;
    }

    [Serializable]
    public sealed class ObjectDumpPayload
    {
        public string sessionId = "";
        public string handle = "";
        public int maxDepth = 3;
        public int maxFieldsPerNode = 100;
        public int maxTotalNodes = 5000;
        public bool includeStatic = false;
        public string[] ignoreTypes = Array.Empty<string>();
        public string outputFormat = "json";
        public string indentation = "  ";
    }

    [Serializable]
    public sealed class ObjectDumpResultPayload
    {
        public string typeName = "";
        public string text = "";
        public ObjectDumpNodeJson root;
        public int totalNodes;
        public bool truncated;
        public string truncateReason = "";
    }

    [Serializable]
    public sealed class ObjectDumpNodeJson
    {
        public string name = "";
        public string declaredType = "";
        public string runtimeType = "";
        public bool isStatic;
        public string value = "";
        public ObjectDumpNodeJson[] children = Array.Empty<ObjectDumpNodeJson>();
        public int depth;
    }
}