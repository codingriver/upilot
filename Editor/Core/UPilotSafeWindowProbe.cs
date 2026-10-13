// -----------------------------------------------------------------------
// UPilot Editor - https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotSafeWindowProbe : EditorWindow
    {
        internal const string Title = "UPilot Safe Probe";
        internal static readonly string FullTypeName = typeof(UPilotSafeWindowProbe).FullName;
        internal static bool IsAllowedTypeName(string typeName) =>
            string.Equals(typeName, FullTypeName, StringComparison.Ordinal);

        private void OnEnable()
        {
            minSize = new Vector2(240, 120);
        }
    }
}
