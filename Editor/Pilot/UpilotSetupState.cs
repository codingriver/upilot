// -----------------------------------------------------------------------
// upilot Editor — first-run setup state.
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using UnityEditor;

namespace codingriver.upilot
{
    public static class UpilotSetupState
    {
        private static string SetupCompletedKey => $"upilot.SetupCompleted.{UpilotBridge.WsEndpointEditorPrefsKeySuffix}";

        public static bool IsCompleted => EditorPrefs.GetBool(SetupCompletedKey, false);

        public static void MarkCompleted()
        {
            EditorPrefs.SetBool(SetupCompletedKey, true);
        }

        [MenuItem("upilot/First Setup/Open", false, 250)]
        public static void OpenFirstSetup()
        {
            UpilotFirstSetupWindow.Open();
        }

        [MenuItem("upilot/First Setup/Reset Setup State", false, 251)]
        public static void ResetSetupState()
        {
            if (!EditorUtility.DisplayDialog(
                    "Reset upilot setup?",
                    "This only resets the first-run setup marker for the current Unity project. Existing ports and config files are not deleted.",
                    "Reset",
                    "Cancel"))
                return;

            EditorPrefs.DeleteKey(SetupCompletedKey);
            UpilotFirstSetupWindow.Open();
        }
    }
}
