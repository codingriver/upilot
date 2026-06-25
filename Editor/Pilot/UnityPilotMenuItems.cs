// -----------------------------------------------------------------------
// upilot Editor — Menu items for bridge control.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

namespace codingriver.unity.pilot
{
    public static class UnityPilotMenuItems
    {
        [MenuItem("upilot/Force Restart Bridge")]
        public static void RestartBridge()
        {
            Logger.Log("[Menu] Force Restart UnityPilotBridge triggered.");
            UnityPilotBridge.Instance.Restart();
        }

        [MenuItem("upilot/Force Restart Bridge", true)]
        public static bool ValidateRestartBridge()
        {
            return !Application.isPlaying;
        }

        [MenuItem("UnityUIFlow/Force Restart UnityPilotBridge")]
        public static void RestartBridgeLegacy()
        {
            RestartBridge();
        }

        [MenuItem("UnityUIFlow/Force Restart UnityPilotBridge", true)]
        public static bool ValidateRestartBridgeLegacy()
        {
            return ValidateRestartBridge();
        }
    }
}
