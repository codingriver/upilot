// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    public sealed class SerializedPropertyChangePayload
    {
        public string propertyPath;
        public string propertyType;
        public string oldValue;
        public string newValue;
        public bool modified;
    }

    internal sealed class SerializedPropertyApplyResult
    {
        public int requestedCount;
        public int modifiedCount;
        public readonly List<SerializedPropertyChangePayload> changes = new();
    }

    internal static class UPilotSerializedPropertyUtility
    {
        public static SerializedPropertyApplyResult Preview(SerializedObject serializedObject, IList<SerializedPropertyWrite> writes)
        {
            if (writes == null || writes.Count == 0)
                throw new InvalidOperationException("properties must contain at least one property write.");
            serializedObject.Update();
            ValidateAll(serializedObject, writes);
            var result = new SerializedPropertyApplyResult { requestedCount = writes.Count };
            try
            {
                foreach (var write in writes)
                {
                    var property = serializedObject.FindProperty(write.propertyPath);
                    string before = GetDisplayValue(property);
                    SetValue(property, write.value ?? "");
                    string after = GetDisplayValue(property);
                    bool modified = before != after;
                    if (modified) result.modifiedCount++;
                    result.changes.Add(new SerializedPropertyChangePayload
                    {
                        propertyPath = property.propertyPath, propertyType = property.propertyType.ToString(),
                        oldValue = before, newValue = after, modified = modified,
                    });
                }
                return result;
            }
            finally
            {
                // Discard the SerializedObject buffer; preview must not apply or add Undo.
                serializedObject.Update();
            }
        }

        public static SerializedPropertyApplyResult Apply(
            SerializedObject serializedObject,
            UnityEngine.Object undoTarget,
            IList<SerializedPropertyWrite> writes,
            string undoName)
        {
            if (serializedObject == null)
                throw new ArgumentNullException(nameof(serializedObject));
            if (undoTarget == null)
                throw new ArgumentNullException(nameof(undoTarget));
            if (writes == null || writes.Count == 0)
                throw new InvalidOperationException("properties must contain at least one property write.");

            serializedObject.Update();
            ValidateAll(serializedObject, writes);

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            Undo.RecordObject(undoTarget, undoName);

            var result = new SerializedPropertyApplyResult { requestedCount = writes.Count };
            try
            {
                foreach (var write in writes)
                {
                    var property = serializedObject.FindProperty(write.propertyPath);
                    var before = GetDisplayValue(property);
                    SetValue(property, write.value ?? string.Empty);
                    var after = GetDisplayValue(property);
                    var modified = !string.Equals(before, after, StringComparison.Ordinal);
                    if (modified)
                        result.modifiedCount++;
                    result.changes.Add(new SerializedPropertyChangePayload
                    {
                        propertyPath = property.propertyPath,
                        propertyType = property.propertyType.ToString(),
                        oldValue = before,
                        newValue = after,
                        modified = modified,
                    });
                }

                serializedObject.ApplyModifiedProperties();
                if (result.modifiedCount == 0)
                    throw new InvalidOperationException("No properties were modified; requested values already match the target.");

                Undo.CollapseUndoOperations(undoGroup);
                return result;
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
        }

        public static string GetDisplayValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.LayerMask:
                    return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return property.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumNames.Length
                        ? property.enumNames[property.enumValueIndex]
                        : property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Color:
                    var color = property.colorValue;
                    RequireFinite(color.r, property.propertyPath, "r");
                    RequireFinite(color.g, property.propertyPath, "g");
                    RequireFinite(color.b, property.propertyPath, "b");
                    RequireFinite(color.a, property.propertyPath, "a");
                    return JsonUtility.ToJson(color);
                case SerializedPropertyType.Vector2:
                    var vector2 = property.vector2Value;
                    RequireFinite(vector2.x, property.propertyPath, "x");
                    RequireFinite(vector2.y, property.propertyPath, "y");
                    return JsonUtility.ToJson(vector2);
                case SerializedPropertyType.Vector3:
                    var vector3 = property.vector3Value;
                    RequireFinite(vector3.x, property.propertyPath, "x");
                    RequireFinite(vector3.y, property.propertyPath, "y");
                    RequireFinite(vector3.z, property.propertyPath, "z");
                    return JsonUtility.ToJson(vector3);
                case SerializedPropertyType.Vector4:
                    var vector4 = property.vector4Value;
                    RequireFinite(vector4.x, property.propertyPath, "x");
                    RequireFinite(vector4.y, property.propertyPath, "y");
                    RequireFinite(vector4.z, property.propertyPath, "z");
                    RequireFinite(vector4.w, property.propertyPath, "w");
                    return JsonUtility.ToJson(vector4);
                case SerializedPropertyType.Quaternion:
                    var quaternion = property.quaternionValue;
                    RequireFinite(quaternion.x, property.propertyPath, "x");
                    RequireFinite(quaternion.y, property.propertyPath, "y");
                    RequireFinite(quaternion.z, property.propertyPath, "z");
                    RequireFinite(quaternion.w, property.propertyPath, "w");
                    return JsonUtility.ToJson(quaternion);
                case SerializedPropertyType.Rect:
                    var rect = property.rectValue;
                    RequireFinite(rect.x, property.propertyPath, "x");
                    RequireFinite(rect.y, property.propertyPath, "y");
                    RequireFinite(rect.width, property.propertyPath, "width");
                    RequireFinite(rect.height, property.propertyPath, "height");
                    return JsonUtility.ToJson(rect);
                case SerializedPropertyType.ObjectReference:
                    if (property.objectReferenceValue == null)
                        return string.Empty;
                    var path = AssetDatabase.GetAssetPath(property.objectReferenceValue);
                    return string.IsNullOrEmpty(path)
                        ? $"instanceId:{UPilotEntityIds.ToWireId(property.objectReferenceValue)}"
                        : path;
                default:
                    return property.hasChildren ? "<children>" : $"<{property.propertyType}>";
            }
        }

        private static void ValidateAll(SerializedObject serializedObject, IList<SerializedPropertyWrite> writes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var write in writes)
            {
                if (write == null || string.IsNullOrWhiteSpace(write.propertyPath))
                    throw new InvalidOperationException("propertyPath is required for every property write.");
                if (!seen.Add(write.propertyPath))
                    throw new InvalidOperationException($"Duplicate propertyPath: {write.propertyPath}");

                var property = serializedObject.FindProperty(write.propertyPath);
                if (property == null)
                    throw new InvalidOperationException($"Property not found: {write.propertyPath}");
                ValidateValue(property, write.value ?? string.Empty);
            }
        }

        private static void ValidateValue(SerializedProperty property, string value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.LayerMask:
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        throw InvalidValue(property, value, "an integer");
                    return;
                case SerializedPropertyType.Boolean:
                    if (!bool.TryParse(value, out _) && value != "0" && value != "1")
                        throw InvalidValue(property, value, "true, false, 0, or 1");
                    return;
                case SerializedPropertyType.Float:
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        throw InvalidValue(property, value, "a floating-point number");
                    return;
                case SerializedPropertyType.String:
                    return;
                case SerializedPropertyType.Enum:
                    if (Array.IndexOf(property.enumNames, value) >= 0)
                        return;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumIndex)
                        || enumIndex < 0 || enumIndex >= property.enumNames.Length)
                        throw InvalidValue(property, value, "a valid enum name or index");
                    return;
                case SerializedPropertyType.Color:
                    ParseColor(value, property.propertyPath);
                    return;
                case SerializedPropertyType.Vector2:
                    ParseVector(value, property.propertyPath, "x", "y");
                    return;
                case SerializedPropertyType.Vector3:
                    ParseVector(value, property.propertyPath, "x", "y", "z");
                    return;
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Quaternion:
                    ParseVector(value, property.propertyPath, "x", "y", "z", "w");
                    return;
                case SerializedPropertyType.ObjectReference:
                    if (!string.IsNullOrEmpty(value) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value) == null)
                        throw new InvalidOperationException($"{property.propertyPath}: object reference asset not found: {value}");
                    return;
                default:
                    throw new InvalidOperationException(
                        $"{property.propertyPath}: unsupported property type {property.propertyType}.");
            }
        }

        private static void SetValue(SerializedProperty property, string value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    property.longValue = long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.ArraySize:
                    property.arraySize = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = value == "1" || (value != "0" && bool.Parse(value));
                    break;
                case SerializedPropertyType.Float:
                    property.doubleValue = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.String:
                    property.stringValue = value;
                    break;
                case SerializedPropertyType.Enum:
                    var enumIndex = Array.IndexOf(property.enumNames, value);
                    property.enumValueIndex = enumIndex >= 0
                        ? enumIndex
                        : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Color:
                    property.colorValue = ParseColor(value, property.propertyPath);
                    break;
                case SerializedPropertyType.Vector2:
                    var v2 = ParseVector(value, property.propertyPath, "x", "y");
                    property.vector2Value = new Vector2(v2[0], v2[1]);
                    break;
                case SerializedPropertyType.Vector3:
                    var v3 = ParseVector(value, property.propertyPath, "x", "y", "z");
                    property.vector3Value = new Vector3(v3[0], v3[1], v3[2]);
                    break;
                case SerializedPropertyType.Vector4:
                    var v4 = ParseVector(value, property.propertyPath, "x", "y", "z", "w");
                    property.vector4Value = new Vector4(v4[0], v4[1], v4[2], v4[3]);
                    break;
                case SerializedPropertyType.Quaternion:
                    var q = ParseVector(value, property.propertyPath, "x", "y", "z", "w");
                    property.quaternionValue = new Quaternion(q[0], q[1], q[2], q[3]);
                    break;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = string.IsNullOrEmpty(value)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"{property.propertyPath}: unsupported property type {property.propertyType}.");
            }
        }

        private static InvalidOperationException InvalidValue(
            SerializedProperty property,
            string value,
            string expected)
        {
            return new InvalidOperationException(
                $"{property.propertyPath}: value '{value}' is invalid for {property.propertyType}; expected {expected}.");
        }

        private static Color ParseColor(string value, string propertyPath)
        {
            var parts = ParseVector(value, propertyPath, "r", "g", "b", "a");
            return new Color(parts[0], parts[1], parts[2], parts[3]);
        }

        private static float[] ParseVector(string value, string propertyPath, params string[] fields)
        {
            var parsed = UPilotComponentService.ParseSimpleJson(value);
            var result = new float[fields.Length];
            for (var index = 0; index < fields.Length; index++)
            {
                if (!parsed.TryGetValue(fields[index], out var text)
                    || !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out result[index])
                    || float.IsNaN(result[index])
                    || float.IsInfinity(result[index]))
                {
                    throw new InvalidOperationException(
                        $"{propertyPath}.{fields[index]}: expected a finite JSON number; " +
                        $"all required fields are {string.Join(", ", fields)}.");
                }
            }
            return result;
        }

        private static void RequireFinite(float value, string propertyPath, string component)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidOperationException(
                    $"{propertyPath}.{component}: cannot serialize non-finite floating-point value.");
            }
        }
    }
}
