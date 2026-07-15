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
        public static bool TryReadValueAsString(VisualElement element, out string value)
        {
            value = null;
            if (!TryReadValue(element, out object rawValue, out Type valueType))
            {
                return false;
            }

            value = FormatValue(rawValue, valueType);
            return true;
        }

        public static bool TryReadValue(VisualElement element, out object value, out Type valueType)
        {
            value = null;
            valueType = null;

            PropertyInfo valueProperty = element?.GetType().GetProperty("value", PublicInstance);
            if (valueProperty == null || !valueProperty.CanRead)
            {
                return false;
            }

            value = valueProperty.GetValue(element);
            valueType = valueProperty.PropertyType;
            if (valueType == typeof(Enum) && value != null)
            {
                valueType = value.GetType();
            }
            return true;
        }

        public static bool ValuesEqual(object actual, object expected, Type valueType)
        {
            Type underlyingType = Nullable.GetUnderlyingType(valueType) ?? valueType;
            if (actual == null || expected == null)
            {
                return Equals(actual, expected);
            }

            if (underlyingType == typeof(float))
            {
                return Mathf.Approximately((float)actual, (float)expected);
            }

            if (underlyingType == typeof(double))
            {
                return Math.Abs((double)actual - (double)expected) <= 0.000001d;
            }

            if (underlyingType == typeof(Vector2))
            {
                return Vector2.Distance((Vector2)actual, (Vector2)expected) <= 0.0001f;
            }

            if (underlyingType == typeof(Vector3))
            {
                return Vector3.Distance((Vector3)actual, (Vector3)expected) <= 0.0001f;
            }

            if (underlyingType == typeof(Vector4))
            {
                Vector4 left = (Vector4)actual;
                Vector4 right = (Vector4)expected;
                return Mathf.Abs(left.x - right.x) <= 0.0001f
                    && Mathf.Abs(left.y - right.y) <= 0.0001f
                    && Mathf.Abs(left.z - right.z) <= 0.0001f
                    && Mathf.Abs(left.w - right.w) <= 0.0001f;
            }

            if (underlyingType == typeof(Color))
            {
                Color left = (Color)actual;
                Color right = (Color)expected;
                return Mathf.Abs(left.r - right.r) <= 0.0001f
                    && Mathf.Abs(left.g - right.g) <= 0.0001f
                    && Mathf.Abs(left.b - right.b) <= 0.0001f
                    && Mathf.Abs(left.a - right.a) <= 0.0001f;
            }

            if (underlyingType == typeof(AnimationCurve))
            {
                return CurvesEqual((AnimationCurve)actual, (AnimationCurve)expected);
            }

            if (underlyingType == typeof(Gradient))
            {
                return GradientsEqual((Gradient)actual, (Gradient)expected);
            }

            return Equals(actual, expected);
        }

        public static bool TryAssignFieldValue(VisualElement element, string value)
        {
            switch (element)
            {
                case Toggle toggle when TryParseBool(value, out bool boolValue):
                    toggle.value = boolValue;
                    return true;
                case Foldout foldout when TryParseBool(value, out bool expanded):
                    foldout.value = expanded;
                    return true;
                case Slider slider when TryParseFloat(value, out float sliderValue):
                    slider.value = Mathf.Clamp(sliderValue, slider.lowValue, slider.highValue);
                    return true;
                case SliderInt sliderInt when TryParseInt(value, out int sliderIntValue):
                    sliderInt.value = Mathf.Clamp(sliderIntValue, sliderInt.lowValue, sliderInt.highValue);
                    return true;
                case MinMaxSlider minMaxSlider when TryParseFloatPair(value, out float minValue, out float maxValue):
                    minValue = Mathf.Clamp(minValue, minMaxSlider.lowLimit, minMaxSlider.highLimit);
                    maxValue = Mathf.Clamp(maxValue, minMaxSlider.lowLimit, minMaxSlider.highLimit);
                    if (maxValue < minValue)
                    {
                        maxValue = minValue;
                    }

                    minMaxSlider.value = new Vector2(minValue, maxValue);
                    return true;
                case DropdownField dropdown:
                    if (TryResolveChoice(dropdown.choices, value, null, out int _, out object selectedChoice))
                    {
                        dropdown.value = selectedChoice?.ToString() ?? string.Empty;
                        return true;
                    }

                    return false;
                case EnumField enumField when enumField.value != null && TryConvertStringValue(value, enumField.value.GetType(), out object enumValue):
                    enumField.value = (Enum)enumValue;
                    return true;
                case EnumFlagsField enumFlagsField when enumFlagsField.value != null && TryConvertStringValue(value, enumFlagsField.value.GetType(), out object enumFlagsValue):
                    enumFlagsField.value = (Enum)enumFlagsValue;
                    return true;
                case MaskField maskField when TryResolveMaskValue(maskField.choices, value, null, null, out int maskValue):
                    maskField.value = maskValue;
                    return true;
                case LayerMaskField layerMaskField when TryResolveMaskValue(layerMaskField.choices, value, null, null, out int layerMaskValue):
                    layerMaskField.value = layerMaskValue;
                    return true;
                case RadioButtonGroup radioButtonGroup when TryResolveIndex(value, null, out int radioIndex):
                    radioButtonGroup.value = radioIndex;
                    return true;
                case ObjectField objectField:
                    if (TryLoadUnityObject(value, objectField.objectType, out UnityEngine.Object objValue))
                    {
                        objectField.value = objValue;
                        return true;
                    }
                    return false;
            }

            PropertyInfo valueProperty = element.GetType().GetProperty("value", PublicInstance);
            if (valueProperty == null || !valueProperty.CanWrite)
            {
                return false;
            }

            if (!TryConvertStringValue(value, valueProperty.PropertyType, out object converted))
            {
                return false;
            }

            valueProperty.SetValue(element, converted);
            return true;
        }
    }
}
