// -----------------------------------------------------------------------
// upilot Editor — first-run setup state.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;

namespace CodingRiver.UPilot
{
    public static class UPilotSetupState
    {
        private static string SetupCompletedKey => $"upilot.SetupCompleted.{UPilotBridge.WsEndpointEditorPrefsKeySuffix}";

        public static bool IsCompleted => EditorPrefs.GetBool(SetupCompletedKey, false);

        public static void MarkCompleted()
        {
            EditorPrefs.SetBool(SetupCompletedKey, true);
        }

        public static void OpenFirstSetup()
        {
            UPilotFirstSetupWindow.Open();
        }

        [MenuItem("UPilot/Advanced/Reset Setup State", false, 251)]
        public static void ResetSetupState()
        {
            if (!EditorUtility.DisplayDialog(
                    "Reset UPilot setup?",
                    "This only resets the first-run setup marker for the current Unity project. Existing ports and config files are not deleted.",
                    "Reset",
                    "Cancel"))
                return;

            EditorPrefs.DeleteKey(SetupCompletedKey);
            UPilotFirstSetupWindow.Open();
        }
    }
}
