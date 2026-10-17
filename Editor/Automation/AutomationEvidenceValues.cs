using System;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Finite numeric evidence only; never traverses Unity computed properties.</summary>
    public static class AutomationEvidenceValues
    {
        private static float Part(float value, string type, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentException(type + "." + name + " must be finite.");
            return value;
        }
        public static float[] Components(Vector2 value) => new[] { Part(value.x, "Vector2", "x"), Part(value.y, "Vector2", "y") };
        public static float[] Components(Vector3 value) => new[] { Part(value.x, "Vector3", "x"), Part(value.y, "Vector3", "y"), Part(value.z, "Vector3", "z") };
        public static float[] Components(Vector4 value) => new[] { Part(value.x, "Vector4", "x"), Part(value.y, "Vector4", "y"), Part(value.z, "Vector4", "z"), Part(value.w, "Vector4", "w") };
        public static float[] Components(Quaternion value) => new[] { Part(value.x, "Quaternion", "x"), Part(value.y, "Quaternion", "y"), Part(value.z, "Quaternion", "z"), Part(value.w, "Quaternion", "w") };
        public static float[] Components(Color value) => new[] { Part(value.r, "Color", "r"), Part(value.g, "Color", "g"), Part(value.b, "Color", "b"), Part(value.a, "Color", "a") };
        public static float[] Components(Rect value) => new[] { Part(value.x, "Rect", "x"), Part(value.y, "Rect", "y"), Part(value.width, "Rect", "width"), Part(value.height, "Rect", "height") };
    }
}
