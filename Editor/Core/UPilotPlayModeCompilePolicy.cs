using System;
using System.IO;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace CodingRiver.UPilot
{
    internal sealed class UPilotCompilePolicyException : InvalidOperationException
    {
        internal readonly string Code;
        internal readonly ErrorDetailPayload Detail;
        internal UPilotCompilePolicyException(string code, string message, string mode) : base(message)
        {
            Code = code;
            Detail = new ErrorDetailPayload { stage = "before_mutation", blockedReason = mode,
                nextAction = "Wait for stable EditMode; do not retry mutations automatically." };
        }
    }

    /// <summary>Use only on the main thread at the side-effect boundary, independent of compiler errors.</summary>
    internal static class UPilotPlayModeCompilePolicy
    {
        internal static string CurrentMode => ClassifyMode(EditorApplication.isPlaying, EditorApplication.isPaused,
            EditorApplication.isPlayingOrWillChangePlaymode, UPilotPlayModeTransitions.Current?.toState);

        internal static string ClassifyMode(bool playing, bool paused, bool playingOrChanging, string transition)
        {
            if (transition == "enteringPlay" || transition == "exitingPlay") return transition;
            if (playingOrChanging != playing) return playing ? "exitingPlay" : "enteringPlay";
            return paused ? "pause" : playing ? "play" : "edit";
        }

        internal static void RequireEditMode(string operation)
        {
            RequireEditMode(operation, CurrentMode);
        }

        internal static void RequireEditMode(string operation, string mode)
        {
            if (mode != "edit") throw new UPilotCompilePolicyException("EDITOR_IN_PLAY_MODE",
                operation + " requires stable EditMode; actual=" + mode, mode);
        }

        internal static bool RequireScriptWrite() => RequireScriptWrite(CurrentMode,
            () => EditorPrefs.GetInt(UPilotUnityConfiguration.CompilationPreference, -1));

        internal static bool RequireScriptWrite(string mode, Func<int> readPreference)
        {
            if (mode == "edit") return false;
            if (mode != "play" && mode != "pause") RequireEditMode("script write", mode);
            int preference;
            try { preference = readPreference(); }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                throw new UPilotCompilePolicyException("PLAYMODE_COMPILATION_POLICY_UNVERIFIED",
                    "Cannot read ScriptCompilationDuringPlay before disk write: " + ex.Message, mode);
            }
            if (preference != 1) throw new UPilotCompilePolicyException("PLAYMODE_COMPILATION_POLICY_UNSAFE",
                "ScriptCompilationDuringPlay must be 1 before PlayMode disk writes; actual=" + preference, mode);
            return true;
        }

        internal static void RequireAssetMutation(params string[] paths)
        {
            RequireAssetMutation(CurrentMode, ResolveAssetPath, paths);
        }

        internal static void RequireAssetMutation(string mode, Func<string, string> resolve, params string[] paths)
        {
            if (mode == "edit") return;
            foreach (string path in paths)
            {
                bool code;
                try { code = ContainsCodeAsset(resolve(path)); }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    throw new UPilotCompilePolicyException("EDITOR_IN_PLAY_MODE", "Cannot safely classify asset: " + path, mode);
                }
                if (code) RequireEditMode("code asset operation: " + path, mode);
            }
        }

        private static string ResolveAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Asset path is required");
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = PackageInfo.FindForAssetPath(normalized);
                if (package == null) throw new IOException("Package location is unknown");
                return Path.GetFullPath(Path.Combine(package.resolvedPath, normalized.Substring(package.assetPath.Length).TrimStart('/')));
            }
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) && normalized != "Assets")
                throw new ArgumentException("Expected Assets/ or Packages/ path");
            if (Array.Exists(normalized.Split('/'), part => part == ".." || part == "." || part.Length == 0))
                throw new ArgumentException("Non-canonical asset path");
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Application.dataPath), normalized));
        }

        internal static bool IsCodePath(string path)
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 5);
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".cs": case ".asmdef": case ".asmref": case ".rsp": case ".dll": return true;
                default: return false;
            }
        }

        internal static bool ContainsCodeAsset(string absolutePath)
        {
            RejectLinks(absolutePath);
            if (IsCodePath(absolutePath)) return true;
            var attributes = ReadAttributesIfPresent(absolutePath);
            if (!attributes.HasValue || (attributes.Value & FileAttributes.Directory) == 0) return false;
            foreach (string child in Directory.EnumerateFileSystemEntries(absolutePath))
                if (ContainsCodeAsset(child)) return true;
            return false;
        }

        internal static void RejectLinks(string absolutePath)
        {
            for (string current = absolutePath; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                var attributes = ReadAttributesIfPresent(current);
                if (attributes.HasValue && (attributes.Value & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked/reparse paths are not allowed: " + current);
            }
        }

        private static FileAttributes? ReadAttributesIfPresent(string path)
        {
            // Exists() also returns false on access errors. Only real absence may be treated as a new target.
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        internal static string ValidateScriptPath(string projectRoot, string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                !normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                Array.Exists(normalized.Split('/'), part => part.Length == 0 || part == "." || part == ".." || part.Contains(":")))
                throw new ArgumentException("Expected a canonical project-local Assets/*.cs path");
            string full = Path.GetFullPath(Path.Combine(projectRoot, normalized));
            string assets = Path.GetFullPath(Path.Combine(projectRoot, "Assets")) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Script escapes Assets");
            RejectLinks(full); RejectLinks(full + ".meta");
            return full;
        }
    }
}
