// -----------------------------------------------------------------------
// upilot Editor — Menu items for bridge control.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;
using UnityEngine;

namespace codingriver.upilot
{
    public static class UpilotMenuItems
    {
        [MenuItem("upilot/Force Restart Bridge")]
        public static void RestartBridge()
        {
            Logger.Log("[Menu] Force Restart UpilotBridge triggered.");
            UpilotBridge.Instance.Restart();
        }

        [MenuItem("upilot/Force Restart Bridge", true)]
        public static bool ValidateRestartBridge()
        {
            return !Application.isPlaying;
        }

    }
}
