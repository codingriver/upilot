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
        private const int CompactCollectionFirstItemMaxLength = 180;

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

        private static readonly HashSet<string> UnityValueLeafTypeNames = new()
        {
            "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.Vector4",
            "UnityEngine.Vector2Int", "UnityEngine.Vector3Int",
            "UnityEngine.Quaternion", "UnityEngine.Color", "UnityEngine.Color32",
            "UnityEngine.Rect", "UnityEngine.RectInt",
            "UnityEngine.Bounds", "UnityEngine.BoundsInt",
            "UnityEngine.Matrix4x4", "UnityEngine.Ray", "UnityEngine.Ray2D", "UnityEngine.Plane",
        };

        public static ObjectDumpNodeJson Dump(
            object value,
            int maxDepth,
            int maxFieldsPerNode,
            int maxTotalNodes,
            bool includeStatic,
            HashSet<string> ignoreTypes,
            ref int totalNodes,
            bool expandUnityValueTypes = false,
            bool expandReflectionTypes = false)
        {
            var visited = new HashSet<object>(new ReferenceEqualityComparer());
            return Walk(value, "$", 0, maxDepth, maxFieldsPerNode,
                        maxTotalNodes, includeStatic, ignoreTypes, visited, ref totalNodes,
                        expandUnityValueTypes, expandReflectionTypes);
        }

        private static ObjectDumpNodeJson Walk(
            object value, string name, int depth, int maxDepth,
            int maxFieldsPerNode, int maxTotalNodes,
            bool includeStatic, HashSet<string> ignoreTypes,
            HashSet<object> visited, ref int totalNodes,
            bool expandUnityValueTypes, bool expandReflectionTypes)
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

            if (!expandReflectionTypes && IsReflectionInfrastructureType(type))
            {
                node.value = "(reflection summary: " + ToStringBounded(value, 256) + ")";
                return node;
            }

            if (IsLeafType(type, expandUnityValueTypes))
            {
                node.value = FormatLeafValue(value, type);
                if (depth >= maxDepth)
                    node.value = node.value + " [max depth]";
                return node;
            }

            if (!type.IsValueType && !visited.Add(value))
            {
                node.value = "(circular ref: " + typeFullName + ")";
                return node;
            }

            if (depth >= maxDepth)
            {
                node.value = FormatLeafValue(value, type) + " [max depth]";
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
                                     includeStatic, ignoreTypes, visited, ref totalNodes,
                                     expandUnityValueTypes, expandReflectionTypes);
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
                                     includeStatic, ignoreTypes, visited, ref totalNodes,
                                     expandUnityValueTypes, expandReflectionTypes);
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

            var children = new List<ObjectDumpNodeJson>();
            bool expandUnityValueTypeFieldsOnly = expandUnityValueTypes &&
                                                  UnityValueLeafTypeNames.Contains(typeFullName);

            foreach (var f in EnumerateFields(type, includeStatic))
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

                int nodesBeforeRead = totalNodes;
                try
                {
                    object childValue = f.GetValue(value);

                    var child = Walk(childValue, f.Name, depth + 1, maxDepth,
                                     maxFieldsPerNode, maxTotalNodes, includeStatic,
                                     ignoreTypes, visited, ref totalNodes, expandUnityValueTypes,
                                     expandReflectionTypes);
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
                    if (totalNodes == nodesBeforeRead) totalNodes++;
                    children.Add(new ObjectDumpNodeJson
                    {
                        name = f.Name,
                        value = FormatError(ex),
                        declaredType = fieldTypeName,
                        isStatic = f.IsStatic,
                        depth = depth + 1,
                    });
                }
            }

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (expandUnityValueTypeFieldsOnly) break;
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

                int nodesBeforeRead = totalNodes;
                try
                {
                    object childValue = p.GetValue(value, null);

                    var child = Walk(childValue, "." + p.Name, depth + 1, maxDepth,
                                     maxFieldsPerNode, maxTotalNodes, includeStatic,
                                     ignoreTypes, visited, ref totalNodes, expandUnityValueTypes,
                                     expandReflectionTypes);
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
                    if (totalNodes == nodesBeforeRead) totalNodes++;
                    children.Add(new ObjectDumpNodeJson
                    {
                        name = "." + p.Name,
                        value = FormatError(ex),
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

                    int nodesBeforeRead = totalNodes;
                    try
                    {
                        object childValue = p.GetValue(null, null);

                        var child = Walk(childValue, "static " + p.Name, depth + 1, maxDepth,
                                         maxFieldsPerNode, maxTotalNodes, includeStatic,
                                         ignoreTypes, visited, ref totalNodes, expandUnityValueTypes,
                                         expandReflectionTypes);
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
                    catch (Exception ex)
                    {
                        if (totalNodes == nodesBeforeRead) totalNodes++;
                        children.Add(new ObjectDumpNodeJson
                        {
                            name = "static " + p.Name,
                            value = FormatError(ex),
                            declaredType = propTypeName,
                            isStatic = true,
                            depth = depth + 1,
                        });
                    }
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

        private static IEnumerable<FieldInfo> EnumerateFields(Type type, bool includeStatic)
        {
            var hierarchy = new Stack<Type>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                hierarchy.Push(current);

            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly;
            if (includeStatic)
                flags |= BindingFlags.Static;

            while (hierarchy.Count > 0)
            {
                foreach (var field in hierarchy.Pop().GetFields(flags))
                    yield return field;
            }
        }

        private static bool IsLeafType(Type type, bool expandUnityValueTypes)
        {
            if (type.IsPrimitive) return true;
            if (type.IsEnum) return true;
            if (type == typeof(string)) return true;
            if (type == typeof(decimal)) return true;
            if (typeof(Type).IsAssignableFrom(type)) return true;

            string fullName = type.FullName;
            if (fullName != null && LeafTypeNames.Contains(fullName)) return true;
            if (!expandUnityValueTypes && fullName != null && UnityValueLeafTypeNames.Contains(fullName)) return true;

            return false;
        }

        private static bool IsReflectionInfrastructureType(Type type)
        {
            return typeof(Delegate).IsAssignableFrom(type) ||
                   typeof(Assembly).IsAssignableFrom(type) ||
                   typeof(Module).IsAssignableFrom(type) ||
                   (typeof(MemberInfo).IsAssignableFrom(type) &&
                    !typeof(Type).IsAssignableFrom(type));
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

        private static string FormatError(Exception error)
        {
            // Reflection wraps getter failures; report the original cause without its stack.
            while (error is TargetInvocationException && error.InnerException != null)
                error = error.InnerException;
            return "(error: " + error.GetType().Name + " - " +
                   ToStringBounded(EscapeString(error.Message), 256) + ")";
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

        public static string FormatAsText(ObjectDumpNodeJson root, string indent = "  ",
            bool includeTypeNames = true)
        {
            var sb = new StringBuilder();
            FormatNodeText(sb, root, "", indent ?? "  ", includeTypeNames);
            return sb.ToString();
        }

        private static string FormatTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || fullName.IndexOf('`') < 0)
                return fullName;
            try
            {
                var type = Type.GetType(fullName, false);
                if (type == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        type = assembly.GetType(fullName, false);
                        if (type != null) break;
                    }
                }
                if (type != null) return FormatTypeName(type);
            }
            catch
            {
                // Display formatting must not prevent inspecting an unresolved type.
            }
            return fullName;
        }

        private static string FormatTypeName(Type type)
        {
            if (type.IsArray)
                return FormatTypeName(type.GetElementType()) +
                       "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (!type.IsGenericType) return type.FullName ?? type.Name;

            var parts = (type.GetGenericTypeDefinition().FullName ?? type.Name).Split('+');
            var arguments = type.GetGenericArguments();
            int argumentIndex = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                int tick = parts[i].IndexOf('`');
                if (tick < 0) continue;
                int count = int.Parse(parts[i].Substring(tick + 1), CultureInfo.InvariantCulture);
                var names = new string[count];
                for (int j = 0; j < count; j++)
                    names[j] = FormatTypeName(arguments[argumentIndex++]);
                parts[i] = parts[i].Substring(0, tick) + "<" + string.Join(", ", names) + ">";
            }
            return string.Join("+", parts);
        }

        private static void FormatNodeText(StringBuilder sb,
            ObjectDumpNodeJson node, string prefix, string indent, bool includeTypeNames,
            string displayName = null)
        {
            sb.Append(prefix);
            if (node.isStatic) sb.Append("static ");
            sb.Append(displayName ?? node.name);

            if (includeTypeNames)
            {
                if (!string.IsNullOrEmpty(node.runtimeType) && !string.IsNullOrEmpty(node.declaredType) &&
                    node.runtimeType != node.declaredType)
                    sb.Append(" (").Append(FormatTypeName(node.declaredType)).Append(" -> ")
                        .Append(FormatTypeName(node.runtimeType)).Append(')');
                else if (!string.IsNullOrEmpty(node.declaredType) && node.name != "$")
                    sb.Append(" (").Append(FormatTypeName(node.declaredType)).Append(')');
            }

            if (TryFormatCompactSequence(node, out string compactSequence))
            {
                sb.Append(": ").Append(compactSequence).AppendLine();
            }
            else if (node.children != null && IsStandardDictionary(node))
            {
                FormatDictionaryText(sb, node, prefix, indent, includeTypeNames);
            }
            else if (node.children == null || node.children.Length == 0)
            {
                sb.Append(": ").Append(node.value ?? "null").AppendLine();
            }
            else
            {
                bool objectBlock = UsesObjectBlock(node);
                if (objectBlock) sb.Append(" {");
                sb.AppendLine();
                foreach (var child in node.children)
                    FormatNodeText(sb, child, prefix + indent, indent, includeTypeNames);
                if (objectBlock) sb.Append(prefix).Append('}').AppendLine();
            }
        }

        private static bool UsesObjectBlock(ObjectDumpNodeJson node)
        {
            return node?.name != "$" && !IsOneDimensionalArray(node) &&
                   !IsStandardList(node) && !IsStandardDictionary(node);
        }

        private static bool TryFormatCompactSequence(ObjectDumpNodeJson node, out string text)
        {
            text = null;
            if (!IsOneDimensionalArray(node) && !IsStandardList(node)) return false;
            if (node.children == null) return false;

            var children = node.children;
            if (children.Length == 0)
            {
                if (!string.IsNullOrEmpty(node.value)) return false;
                text = "[]";
                return true;
            }

            if (!TryGetSequenceElementType(node, out string elementType)) return false;

            var values = new string[children.Length];
            for (int i = 0; i < children.Length; i++)
            {
                if (!IsSimpleLeaf(children[i], elementType)) return false;
                values[i] = children[i].value;
            }

            if (values[0].Length > CompactCollectionFirstItemMaxLength) return false;
            text = "[" + string.Join(", ", values) + "]";
            return true;
        }

        private static void FormatDictionaryText(StringBuilder sb,
            ObjectDumpNodeJson node, string prefix, string indent, bool includeTypeNames)
        {
            var entries = node.children ?? Array.Empty<ObjectDumpNodeJson>();
            if (entries.Length == 0)
            {
                if (!string.IsNullOrEmpty(node.value))
                {
                    sb.Append(": ").Append(node.value).AppendLine();
                    return;
                }
                sb.Append(": {}").AppendLine();
                return;
            }

            var simpleEntries = new string[entries.Length];
            bool allSimple = true;
            for (int i = 0; i < entries.Length; i++)
            {
                if (!TryGetDictionaryEntryNodes(entries[i], out var key, out var value) ||
                    !IsSimpleLeaf(key, null) || !IsSimpleLeaf(value, null))
                {
                    allSimple = false;
                    break;
                }
                simpleEntries[i] = key.value + ": " + value.value;
            }

            if (allSimple)
            {
                string firstEntry = "{ " + simpleEntries[0] + " }";
                if (firstEntry.Length <= CompactCollectionFirstItemMaxLength)
                {
                    string inline = "{ " + string.Join(", ", simpleEntries) + " }";
                    sb.Append(": ").Append(inline).AppendLine();
                    return;
                }
            }

            sb.AppendLine(":");
            for (int i = 0; i < entries.Length; i++)
            {
                if (TryGetDictionaryEntryNodes(entries[i], out var key, out var value))
                {
                    if (IsSimpleLeaf(key, null) && IsSimpleLeaf(value, null))
                    {
                        sb.Append(prefix).Append(indent).Append("{ ")
                            .Append(key.value).Append(": ").Append(value.value)
                            .Append(" }").AppendLine();
                    }
                    else
                    {
                        sb.Append(prefix).Append(indent).Append('{').AppendLine();
                        FormatNodeText(sb, key, prefix + indent + indent, indent, includeTypeNames, "Key");
                        FormatNodeText(sb, value, prefix + indent + indent, indent, includeTypeNames, "Value");
                        sb.Append(prefix).Append(indent).Append('}').AppendLine();
                    }
                }
                else
                {
                    // Keep incomplete or diagnostic entries inspectable without their collection index.
                    sb.Append(prefix).Append(indent).Append('{').AppendLine();
                    var fallbackChildren = entries[i].children ?? Array.Empty<ObjectDumpNodeJson>();
                    if (fallbackChildren.Length == 0)
                    {
                        string fallbackName = entries[i].name == "..." ? "..." : "entry";
                        FormatNodeText(sb, entries[i], prefix + indent + indent, indent, includeTypeNames, fallbackName);
                    }
                    else
                    {
                        foreach (var child in fallbackChildren)
                            FormatNodeText(sb, child, prefix + indent + indent, indent, includeTypeNames);
                    }
                    sb.Append(prefix).Append(indent).Append('}').AppendLine();
                }
            }
        }

        private static bool TryGetDictionaryEntryNodes(ObjectDumpNodeJson entry,
            out ObjectDumpNodeJson key, out ObjectDumpNodeJson value)
        {
            key = null;
            value = null;
            if (!IsKeyValuePair(entry)) return false;

            var children = entry.children ?? Array.Empty<ObjectDumpNodeJson>();
            key = children.FirstOrDefault(child => child.name == "key");
            value = children.FirstOrDefault(child => child.name == "value");
            if (key != null && value != null) return true;

            key = children.FirstOrDefault(child => child.name == ".Key");
            value = children.FirstOrDefault(child => child.name == ".Value");
            return key != null && value != null;
        }

        private static bool IsSimpleLeaf(ObjectDumpNodeJson node, string expectedType)
        {
            if (node == null || node.children != null && node.children.Length > 0 ||
                string.IsNullOrEmpty(node.value) || IsDiagnosticValue(node.value))
                return false;
            if (!string.IsNullOrEmpty(expectedType) && node.value != "null" &&
                node.declaredType != expectedType)
                return false;
            return string.IsNullOrEmpty(node.runtimeType) || node.runtimeType == node.declaredType;
        }

        private static bool IsDiagnosticValue(string value)
        {
            return value.StartsWith("(error:", StringComparison.Ordinal) ||
                   value.StartsWith("(circular ref:", StringComparison.Ordinal) ||
                   value.StartsWith("(ignored:", StringComparison.Ordinal) ||
                   value.StartsWith("(truncated:", StringComparison.Ordinal) ||
                   value.StartsWith("(reflection summary:", StringComparison.Ordinal) ||
                   value.IndexOf("[max depth]", StringComparison.Ordinal) >= 0;
        }

        private static bool IsOneDimensionalArray(ObjectDumpNodeJson node)
        {
            return TryResolveNodeType(node, out var type) && type.IsArray && type.GetArrayRank() == 1;
        }

        private static bool IsStandardList(ObjectDumpNodeJson node)
        {
            return TryResolveNodeType(node, out var type) && type.IsGenericType &&
                   type.GetGenericTypeDefinition() == typeof(List<>);
        }

        private static bool IsStandardDictionary(ObjectDumpNodeJson node)
        {
            return TryResolveNodeType(node, out var type) && type.IsGenericType &&
                   type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
        }

        private static bool IsKeyValuePair(ObjectDumpNodeJson node)
        {
            return TryResolveNodeType(node, out var type) && type.IsGenericType &&
                   type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);
        }

        private static bool TryGetSequenceElementType(ObjectDumpNodeJson node, out string elementType)
        {
            elementType = null;
            if (!TryResolveNodeType(node, out var type)) return false;
            Type element = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
            elementType = element?.FullName ?? element?.Name;
            return !string.IsNullOrEmpty(elementType);
        }

        private static bool TryResolveNodeType(ObjectDumpNodeJson node, out Type type)
        {
            type = null;
            string typeName = node.runtimeType ?? node.declaredType;
            if (string.IsNullOrEmpty(typeName)) return false;
            try
            {
                type = Type.GetType(typeName, false);
                if (type != null) return true;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(typeName, false);
                    if (type != null) return true;
                }
            }
            catch
            {
                // Text formatting remains available when a type cannot be resolved.
            }
            return false;
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
        public bool includeTypeNames = false;
        public bool expandUnityValueTypes = false;
        public bool expandReflectionTypes = false;
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
        public bool unityValueTypesExpanded;
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
