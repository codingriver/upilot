// -----------------------------------------------------------------------
// upilot Editor — Menu items for bridge control.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    public static class UPilotMenuItems
    {
        [MenuItem("UPilot/Advanced/Force Restart Unity Bridge")]
        public static void RestartBridge()
        {
            Logger.Log("[Menu] Force Restart UPilotBridge triggered.");
            UPilotBridge.Instance.Restart();
        }

        [MenuItem("UPilot/Advanced/Force Restart Unity Bridge", true)]
        public static bool ValidateRestartBridge()
        {
            return !Application.isPlaying;
        }

    }
}
