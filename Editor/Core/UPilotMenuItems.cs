// -----------------------------------------------------------------------
// upilot Editor — Menu items for bridge control.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;
using UnityEngine;
using System;

namespace CodingRiver.UPilot
{
    public static class UPilotMenuItems
    {
        public static void RestartBridge()
        {
            try
            {
                if (UPilotUpdateService.Instance.IsServiceStartBlocked)
                {
                    Logger.LogWarning("SYSTEM", UPilotUpdateService.ServiceStartBlockedMessage);
                    return;
                }

                Logger.Log("[Menu] Force Restart UPilotBridge triggered.");
                UPilotBridge.Instance.Restart();
            }
            catch (Exception ex)
            {
                Debug.LogError("[UPilot] 菜单重启 Unity 桥接器失败：" + ex.Message + "\n" + ex);
            }
        }

        public static bool ValidateRestartBridge()
        {
            return !Application.isPlaying;
        }

    }
}
