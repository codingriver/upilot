using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace CodingRiver.UPilot.Automation
{
    internal static class EditorStepOperations
    {
        internal static string ValidateScene(string path, bool optional)
        {
            if (optional && path == "") return "{\"ok\":true}";
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)
                || !path.EndsWith(".unity", StringComparison.Ordinal) || path.Contains("..") || path.Contains("\\")
                || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                return AutomationStepJsonCodec.ValidationErrorJson("STEP_SCENE_INVALID", "Expected an existing Assets/.../*.unity scene.");
            return "{\"ok\":true}";
        }
        internal static void Prepare()
        {
            var result = UPilotSceneService.PrepareForAutomation("block");
            if (!result.prepared) throw new AutomationStepException("STEP_SCENE_UNSAVED", "Save scene changes before running this plan.");
        }
        internal static bool Stable => !EditorApplication.isCompiling && !EditorApplication.isUpdating
            && EditorApplication.isPlaying == EditorApplication.isPlayingOrWillChangePlaymode;
        internal static void Open(string path)
        {
            CheckOpen(path);
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        }
        internal static void CheckOpen(string path)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new AutomationStepException("STEP_EDIT_MODE_REQUIRED", "Opening a scene requires EditMode.");
            var validation = AutomationStepJsonCodec.Validation(ValidateScene(path, false));
            if (!validation.ok) throw new AutomationStepException("STEP_SCENE_INVALID", "Invalid scene path.");
            Prepare();
        }
        internal static bool IsOpen(string path) => SceneManager.GetActiveScene().path == path
            && SceneManager.GetActiveScene().isLoaded;
        internal static bool Requested(string contextJson)
        {
            var root = AutomationStepJsonCodec.ParseObject(AutomationStepContextJson.Checkpoint(contextJson));
            var requested = root.Element("requested");
            return (string)requested?.Attribute("type") == "boolean" && requested.Value == "true";
        }
        internal static void Mode(bool play)
        {
            UPilotPlayModeTransitions.RegisterIntent("upilot", play ? "play" : "edit",
                operationId: UPilotAutomationStepService.CallbackOperationId, toolName: "automation.step");
            EditorApplication.isPlaying = play;
        }
    }
}
