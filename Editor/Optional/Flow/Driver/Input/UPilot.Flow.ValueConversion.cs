using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodingRiver.UPilot.Flow
{
    public static partial class AdvancedActionHelpers
    {
        private static bool TryParseAnimationCurve(string value, out AnimationCurve curve)
        {
            curve = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string[] keyTokens = value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var keys = new List<Keyframe>();
            foreach (string token in keyTokens)
            {
                string[] parts = token.Split(new[] { ':' }, StringSplitOptions.None);
                if (parts.Length != 2 && parts.Length != 4)
                {
                    return false;
                }

                if (!TryParseFloat(parts[0].Trim(), out float time)
                    || !TryParseFloat(parts[1].Trim(), out float keyValue))
                {
                    return false;
                }

                if (parts.Length == 4)
                {
                    if (!TryParseFloat(parts[2].Trim(), out float inTangent)
                        || !TryParseFloat(parts[3].Trim(), out float outTangent))
                    {
                        return false;
                    }

                    keys.Add(new Keyframe(time, keyValue, inTangent, outTangent));
                }
                else
                {
                    keys.Add(new Keyframe(time, keyValue));
                }
            }

            if (keys.Count == 0)
            {
                return false;
            }

            curve = new AnimationCurve(keys.ToArray());
            return true;
        }

        private static bool GradientsEqual(Gradient left, Gradient right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            GradientColorKey[] leftColorKeys = left.colorKeys;
            GradientColorKey[] rightColorKeys = right.colorKeys;
            GradientAlphaKey[] leftAlphaKeys = left.alphaKeys;
            GradientAlphaKey[] rightAlphaKeys = right.alphaKeys;
            if (leftColorKeys.Length != rightColorKeys.Length || leftAlphaKeys.Length != rightAlphaKeys.Length)
            {
                return false;
            }

            for (int i = 0; i < leftColorKeys.Length; i++)
            {
                if (Mathf.Abs(leftColorKeys[i].time - rightColorKeys[i].time) > 0.0001f
                    || !ValuesEqual(leftColorKeys[i].color, rightColorKeys[i].color, typeof(Color)))
                {
                    return false;
                }
            }

            for (int i = 0; i < leftAlphaKeys.Length; i++)
            {
                if (Mathf.Abs(leftAlphaKeys[i].time - rightAlphaKeys[i].time) > 0.0001f
                    || Mathf.Abs(leftAlphaKeys[i].alpha - rightAlphaKeys[i].alpha) > 0.0001f)
                {
                    return false;
                }
            }

            return left.mode == right.mode;
        }

        private static bool TryLoadUnityObject(string value, Type objectType, out UnityEngine.Object asset)
        {
            asset = null;
            if (string.IsNullOrWhiteSpace(value) || objectType == null || !typeof(UnityEngine.Object).IsAssignableFrom(objectType))
            {
                return false;
            }

            string path = value.Trim();
            if (path.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                path = AssetDatabase.GUIDToAssetPath(path.Substring(5).Trim());
            }
            else if (path.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(5).Trim();
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (path.StartsWith("name:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("asset-name:", StringComparison.OrdinalIgnoreCase))
            {
                string assetName = path.Substring(path.IndexOf(':') + 1).Trim();
                return TryLoadUnityObjectByName(assetName, objectType, out asset);
            }

            if (path.StartsWith("search:", StringComparison.OrdinalIgnoreCase))
            {
                string searchQuery = path.Substring("search:".Length).Trim();
                return TryLoadUnityObjectBySearch(searchQuery, objectType, out asset);
            }

            asset = AssetDatabase.LoadAssetAtPath(path, objectType);
            return asset != null;
        }

        private static bool TryParseIndexList(string value, out List<int> indices)
        {
            indices = new List<int>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (string part in value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseInt(part.Trim(), out int index))
                {
                    indices = null;
                    return false;
                }

                indices.Add(index);
            }

            return indices.Count > 0;
        }

        private static bool TryParseFloat(string value, out float parsed)
        {
            return float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, InvariantCulture, out parsed);
        }

        private static bool TryResolveIndex(string valueLiteral, string indexLiteral, out int value)
        {
            value = 0;
            if (!string.IsNullOrWhiteSpace(indexLiteral))
            {
                return TryParseInt(indexLiteral, out value);
            }

            if (!string.IsNullOrWhiteSpace(valueLiteral))
            {
                return TryParseInt(valueLiteral, out value);
            }

            return false;
        }

        private static bool TryLoadUnityObjectBySearch(string searchQuery, Type objectType, out UnityEngine.Object asset)
        {
            asset = null;
            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                return false;
            }

            string typeName = objectType.Name;
            string needle = searchQuery;
            int separator = searchQuery.IndexOf(':');
            if (separator > 0)
            {
                string explicitType = searchQuery.Substring(0, separator).Trim();
                if (!string.IsNullOrWhiteSpace(explicitType))
                {
                    typeName = explicitType;
                }

                needle = searchQuery.Substring(separator + 1).Trim();
            }

            string filter = string.IsNullOrWhiteSpace(needle)
                ? $"t:{typeName}"
                : $"{needle} t:{typeName}";

            foreach (string guid in AssetDatabase.FindAssets(filter))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                UnityEngine.Object candidate = AssetDatabase.LoadAssetAtPath(assetPath, objectType);
                if (candidate == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(needle)
                    || candidate.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || assetPath.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || Path.GetFileNameWithoutExtension(assetPath).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    asset = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryLoadUnityObjectByName(string assetName, Type objectType, out UnityEngine.Object asset)
        {
            asset = null;
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return false;
            }

            string filter = $"{assetName} t:{objectType.Name}";
            foreach (string guid in AssetDatabase.FindAssets(filter))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                UnityEngine.Object candidate = AssetDatabase.LoadAssetAtPath(assetPath, objectType);
                if (candidate == null)
                {
                    continue;
                }

                string fileName = Path.GetFileNameWithoutExtension(assetPath);
                if (string.Equals(candidate.name, assetName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase))
                {
                    asset = candidate;
                    return true;
                }
            }

            return false;
        }

        private static string FormatGradient(Gradient gradient)
        {
            if (gradient == null)
            {
                return string.Empty;
            }

            var colorParts = new string[gradient.colorKeys.Length];
            for (int i = 0; i < gradient.colorKeys.Length; i++)
            {
                GradientColorKey key = gradient.colorKeys[i];
                colorParts[i] = $"{key.time.ToString("0.###", InvariantCulture)}:#{ColorUtility.ToHtmlStringRGBA(key.color)}";
            }

            var alphaParts = new string[gradient.alphaKeys.Length];
            for (int i = 0; i < gradient.alphaKeys.Length; i++)
            {
                GradientAlphaKey key = gradient.alphaKeys[i];
                alphaParts[i] = $"{key.time.ToString("0.###", InvariantCulture)}:{key.alpha.ToString("0.###", InvariantCulture)}";
            }

            return $"{string.Join(";", colorParts)}|{string.Join(";", alphaParts)}";
        }

        private static bool TryParseBool(string value, out bool parsed)
        {
            return bool.TryParse(value, out parsed);
        }

        public static bool TryConvertStringValue(string value, Type targetType, out object converted)
        {
            converted = null;
            if (targetType == null)
            {
                return false;
            }

            Type underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (Nullable.GetUnderlyingType(targetType) != null && string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (underlyingType == typeof(string))
            {
                converted = value;
                return true;
            }

            if (underlyingType == typeof(bool) && TryParseBool(value, out bool boolValue))
            {
                converted = boolValue;
                return true;
            }

            if (underlyingType == typeof(int) && TryParseInt(value, out int intValue))
            {
                converted = intValue;
                return true;
            }

            if (underlyingType == typeof(long) && long.TryParse(value, NumberStyles.Integer, InvariantCulture, out long longValue))
            {
                converted = longValue;
                return true;
            }

            if (underlyingType == typeof(uint) && uint.TryParse(value, NumberStyles.Integer, InvariantCulture, out uint uintValue))
            {
                converted = uintValue;
                return true;
            }

            if (underlyingType == typeof(ulong) && ulong.TryParse(value, NumberStyles.Integer, InvariantCulture, out ulong ulongValue))
            {
                converted = ulongValue;
                return true;
            }

            if (underlyingType == typeof(float) && TryParseFloat(value, out float floatValue))
            {
                converted = floatValue;
                return true;
            }

            if (underlyingType == typeof(double) && double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, InvariantCulture, out double doubleValue))
            {
                converted = doubleValue;
                return true;
            }

            if (underlyingType == typeof(Vector2) && TryParseFloatArray(value, 2, out float[] vector2Values))
            {
                converted = new Vector2(vector2Values[0], vector2Values[1]);
                return true;
            }

            if (underlyingType == typeof(Vector3) && TryParseFloatArray(value, 3, out float[] vector3Values))
            {
                converted = new Vector3(vector3Values[0], vector3Values[1], vector3Values[2]);
                return true;
            }

            if (underlyingType == typeof(Vector4) && TryParseFloatArray(value, 4, out float[] vector4Values))
            {
                converted = new Vector4(vector4Values[0], vector4Values[1], vector4Values[2], vector4Values[3]);
                return true;
            }

            if (underlyingType == typeof(Vector2Int) && TryParseIntArray(value, 2, out int[] vector2IntValues))
            {
                converted = new Vector2Int(vector2IntValues[0], vector2IntValues[1]);
                return true;
            }

            if (underlyingType == typeof(Vector3Int) && TryParseIntArray(value, 3, out int[] vector3IntValues))
            {
                converted = new Vector3Int(vector3IntValues[0], vector3IntValues[1], vector3IntValues[2]);
                return true;
            }

            if (underlyingType == typeof(Rect) && TryParseFloatArray(value, 4, out float[] rectValues))
            {
                converted = new Rect(rectValues[0], rectValues[1], rectValues[2], rectValues[3]);
                return true;
            }

            if (underlyingType == typeof(RectInt) && TryParseIntArray(value, 4, out int[] rectIntValues))
            {
                converted = new RectInt(rectIntValues[0], rectIntValues[1], rectIntValues[2], rectIntValues[3]);
                return true;
            }

            if (underlyingType == typeof(Bounds) && TryParseFloatArray(value, 6, out float[] boundsValues))
            {
                converted = new Bounds(
                    new Vector3(boundsValues[0], boundsValues[1], boundsValues[2]),
                    new Vector3(boundsValues[3] * 2f, boundsValues[4] * 2f, boundsValues[5] * 2f));
                return true;
            }

            if (underlyingType == typeof(BoundsInt) && TryParseIntArray(value, 6, out int[] boundsIntValues))
            {
                converted = new BoundsInt(
                    new Vector3Int(boundsIntValues[0], boundsIntValues[1], boundsIntValues[2]),
                    new Vector3Int(boundsIntValues[3], boundsIntValues[4], boundsIntValues[5]));
                return true;
            }

            if (underlyingType == typeof(Color))
            {
                if (ColorUtility.TryParseHtmlString(value, out Color colorValue))
                {
                    converted = colorValue;
                    return true;
                }

                if (TryParseFloatArray(value, 4, out float[] rgbaValues))
                {
                    converted = new Color(rgbaValues[0], rgbaValues[1], rgbaValues[2], rgbaValues[3]);
                    return true;
                }
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(underlyingType))
            {
                if (TryLoadUnityObject(value, underlyingType, out UnityEngine.Object asset))
                {
                    converted = asset;
                    return true;
                }

                return false;
            }

            if (underlyingType == typeof(AnimationCurve) && TryParseAnimationCurve(value, out AnimationCurve curve))
            {
                converted = curve;
                return true;
            }

            if (underlyingType == typeof(Gradient) && TryParseGradient(value, out Gradient gradient))
            {
                converted = gradient;
                return true;
            }

            if (underlyingType.IsEnum)
            {
                try
                {
                    converted = Enum.Parse(underlyingType, value, true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            MethodInfo parseMethod = underlyingType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (parseMethod != null)
            {
                try
                {
                    converted = parseMethod.Invoke(null, new object[] { value });
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                converted = Convert.ChangeType(value, underlyingType, InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseGradient(string value, out Gradient gradient)
        {
            gradient = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string[] sections = value.Split('|');
            if (sections.Length != 2)
            {
                return false;
            }

            if (!TryParseGradientColorKeys(sections[0], out GradientColorKey[] colorKeys)
                || !TryParseGradientAlphaKeys(sections[1], out GradientAlphaKey[] alphaKeys))
            {
                return false;
            }

            gradient = new Gradient();
            gradient.SetKeys(colorKeys, alphaKeys);
            return true;
        }

        private static bool TryParseFloatPair(string value, out float left, out float right)
        {
            left = 0f;
            right = 0f;
            if (!TryParseFloatArray(value, 2, out float[] values))
            {
                return false;
            }

            left = values[0];
            right = values[1];
            return true;
        }

        private static bool TryParseGradientColorKeys(string literal, out GradientColorKey[] colorKeys)
        {
            colorKeys = null;
            string[] keyTokens = literal.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (keyTokens.Length == 0)
            {
                return false;
            }

            var keys = new List<GradientColorKey>();
            foreach (string token in keyTokens)
            {
                string[] parts = token.Split(new[] { ':' }, StringSplitOptions.None);
                if (parts.Length != 2
                    || !TryParseFloat(parts[0].Trim(), out float time)
                    || !ColorUtility.TryParseHtmlString(parts[1].Trim(), out Color color))
                {
                    return false;
                }

                keys.Add(new GradientColorKey(color, time));
            }

            colorKeys = keys.ToArray();
            return true;
        }

        private static string FormatAnimationCurve(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0)
            {
                return string.Empty;
            }

            var parts = new string[curve.length];
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe key = curve.keys[i];
                parts[i] = string.Join(":",
                    key.time.ToString("0.###", InvariantCulture),
                    key.value.ToString("0.###", InvariantCulture),
                    key.inTangent.ToString("0.###", InvariantCulture),
                    key.outTangent.ToString("0.###", InvariantCulture));
            }

            return string.Join(";", parts);
        }

        private static bool TryParseIntArray(string value, int expectedCount, out int[] values)
        {
            values = null;
            string[] parts = value.Split(',');
            if (parts.Length != expectedCount)
            {
                return false;
            }

            values = new int[expectedCount];
            for (int i = 0; i < expectedCount; i++)
            {
                if (!TryParseInt(parts[i].Trim(), out values[i]))
                {
                    values = null;
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseGradientAlphaKeys(string literal, out GradientAlphaKey[] alphaKeys)
        {
            alphaKeys = null;
            string[] keyTokens = literal.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (keyTokens.Length == 0)
            {
                return false;
            }

            var keys = new List<GradientAlphaKey>();
            foreach (string token in keyTokens)
            {
                string[] parts = token.Split(new[] { ':' }, StringSplitOptions.None);
                if (parts.Length != 2
                    || !TryParseFloat(parts[0].Trim(), out float time)
                    || !TryParseFloat(parts[1].Trim(), out float alpha))
                {
                    return false;
                }

                keys.Add(new GradientAlphaKey(alpha, time));
            }

            alphaKeys = keys.ToArray();
            return true;
        }

        private static string FormatValue(object value, Type valueType)
        {
            if (value == null)
            {
                return string.Empty;
            }

            Type underlyingType = Nullable.GetUnderlyingType(valueType) ?? valueType;
            if (underlyingType == typeof(string))
            {
                return (string)value;
            }

            if (underlyingType == typeof(bool))
            {
                return ((bool)value) ? "true" : "false";
            }

            if (underlyingType == typeof(float))
            {
                return ((float)value).ToString("0.###", InvariantCulture);
            }

            if (underlyingType == typeof(double))
            {
                return ((double)value).ToString("0.###", InvariantCulture);
            }

            if (underlyingType == typeof(Vector2))
            {
                Vector2 vector = (Vector2)value;
                return $"{vector.x.ToString("0.###", InvariantCulture)},{vector.y.ToString("0.###", InvariantCulture)}";
            }

            if (underlyingType == typeof(Vector3))
            {
                Vector3 vector = (Vector3)value;
                return $"{vector.x.ToString("0.###", InvariantCulture)},{vector.y.ToString("0.###", InvariantCulture)},{vector.z.ToString("0.###", InvariantCulture)}";
            }

            if (underlyingType == typeof(Vector4))
            {
                Vector4 vector = (Vector4)value;
                return $"{vector.x.ToString("0.###", InvariantCulture)},{vector.y.ToString("0.###", InvariantCulture)},{vector.z.ToString("0.###", InvariantCulture)},{vector.w.ToString("0.###", InvariantCulture)}";
            }

            if (underlyingType == typeof(Vector2Int))
            {
                Vector2Int vector = (Vector2Int)value;
                return $"{vector.x},{vector.y}";
            }

            if (underlyingType == typeof(Vector3Int))
            {
                Vector3Int vector = (Vector3Int)value;
                return $"{vector.x},{vector.y},{vector.z}";
            }

            if (underlyingType == typeof(Rect))
            {
                Rect rect = (Rect)value;
                return $"{rect.x.ToString("0.###", InvariantCulture)},{rect.y.ToString("0.###", InvariantCulture)},{rect.width.ToString("0.###", InvariantCulture)},{rect.height.ToString("0.###", InvariantCulture)}";
            }

            if (underlyingType == typeof(RectInt))
            {
                RectInt rect = (RectInt)value;
                return $"{rect.x},{rect.y},{rect.width},{rect.height}";
            }

            if (underlyingType == typeof(Bounds))
            {
                Bounds bounds = (Bounds)value;
                Vector3 center = bounds.center;
                Vector3 extents = bounds.extents;
                return $"{center.x.ToString("0.###", InvariantCulture)},{center.y.ToString("0.###", InvariantCulture)},{center.z.ToString("0.###", InvariantCulture)},{extents.x.ToString("0.###", InvariantCulture)},{extents.y.ToString("0.###", InvariantCulture)},{extents.z.ToString("0.###", InvariantCulture)}";
            }

            if (underlyingType == typeof(BoundsInt))
            {
                BoundsInt bounds = (BoundsInt)value;
                Vector3Int position = bounds.position;
                Vector3Int size = bounds.size;
                return $"{position.x},{position.y},{position.z},{size.x},{size.y},{size.z}";
            }

            if (underlyingType == typeof(Color))
            {
                Color color = (Color)value;
                return "#" + ColorUtility.ToHtmlStringRGBA(color);
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(underlyingType))
            {
                UnityEngine.Object asset = (UnityEngine.Object)value;
                string assetPath = AssetDatabase.GetAssetPath(asset);
                return string.IsNullOrWhiteSpace(assetPath) ? asset.name : assetPath;
            }

            if (underlyingType == typeof(AnimationCurve))
            {
                return FormatAnimationCurve((AnimationCurve)value);
            }

            if (underlyingType == typeof(Gradient))
            {
                return FormatGradient((Gradient)value);
            }

            return Convert.ToString(value, InvariantCulture) ?? string.Empty;
        }

        private static bool TryParseInt(string value, out int parsed)
        {
            return int.TryParse(value, NumberStyles.Integer, InvariantCulture, out parsed);
        }

        private static bool TryParseFloatArray(string value, int expectedCount, out float[] values)
        {
            values = null;
            string[] parts = value.Split(',');
            if (parts.Length != expectedCount)
            {
                return false;
            }

            values = new float[expectedCount];
            for (int i = 0; i < expectedCount; i++)
            {
                if (!TryParseFloat(parts[i].Trim(), out values[i]))
                {
                    values = null;
                    return false;
                }
            }

            return true;
        }

        private static bool CurvesEqual(AnimationCurve left, AnimationCurve right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            if (left.length != right.length)
            {
                return false;
            }

            for (int i = 0; i < left.length; i++)
            {
                Keyframe l = left.keys[i];
                Keyframe r = right.keys[i];
                if (Mathf.Abs(l.time - r.time) > 0.0001f
                    || Mathf.Abs(l.value - r.value) > 0.0001f
                    || Mathf.Abs(l.inTangent - r.inTangent) > 0.0001f
                    || Mathf.Abs(l.outTangent - r.outTangent) > 0.0001f)
                {
                    return false;
                }
            }

            return left.preWrapMode == right.preWrapMode && left.postWrapMode == right.postWrapMode;
        }
    }
}
