// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] public class ScriptReadMessage    { public ScriptReadPayload payload; }
    [Serializable] public class ScriptReadPayload    { public string scriptPath = ""; }

    [Serializable] public class ScriptCreateMessage  { public ScriptCreatePayload payload; }
    [Serializable] public class ScriptCreatePayload  { public string scriptPath = ""; public string content = ""; }

    [Serializable] public class ScriptUpdateMessage  { public ScriptUpdatePayload payload; }
    [Serializable] public class ScriptUpdatePayload  { public string scriptPath = ""; public string content = ""; }

    [Serializable] public class ScriptDeleteMessage  { public ScriptDeletePayload payload; }
    [Serializable] public class ScriptDeletePayload  { public string scriptPath = ""; }

    [Serializable]
    public class ScriptContentPayload
    {
        public string scriptPath;
        public string content;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotScriptService
    {
        private readonly UPilotBridge _bridge;

        public UPilotScriptService(UPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("script.read",   HandleReadAsync);
            _bridge.Router.Register("script.create",  HandleCreateAsync);
            _bridge.Router.Register("script.update",  HandleUpdateAsync);
            _bridge.Router.Register("script.delete",  HandleDeleteAsync);
        }

        // ── Validation helpers ──────────────────────────────────────────────────

        private static bool ValidateScriptPath(string path, out string error)
        {
            try
            {
                UPilotPlayModeCompilePolicy.ValidateScriptPath(Path.GetDirectoryName(Application.dataPath), path);
                error = null;
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string ToAbsolutePath(string assetPath)
        {
            // Unity project root is parent of Assets/
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot, assetPath);
        }

        // ── script.read ─────────────────────────────────────────────────────────

        private async Task HandleReadAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<ScriptReadMessage>(json);
            var p   = msg?.payload ?? new ScriptReadPayload();

            if (!ValidateScriptPath(p.scriptPath, out string error))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", error, token, "script.read");
                return;
            }

            try
            {
                string absPath = ToAbsolutePath(p.scriptPath);
                if (!File.Exists(absPath))
                {
                    await _bridge.SendErrorAsync(id, "FILE_NOT_FOUND", $"Script not found: {p.scriptPath}", token, "script.read");
                    return;
                }

                string content = File.ReadAllText(absPath, System.Text.Encoding.UTF8);
                var payload = new ScriptContentPayload
                {
                    scriptPath = p.scriptPath,
                    content    = content,
                };
                await _bridge.SendResultAsync(id, "script.read", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCRIPT_READ_FAILED", ex.Message, token, "script.read", errorDetail: null, exception: ex);
            }
        }

        private Task HandleCreateAsync(string id, string json, CancellationToken token)
        {
            var p = JsonUtility.FromJson<ScriptCreateMessage>(json)?.payload ?? new ScriptCreatePayload();
            return HandleMutationAsync(id, "script.create", p.scriptPath,
                string.IsNullOrEmpty(p.content) ? "// Auto-generated script placeholder\n" : p.content, token);
        }

        private Task HandleUpdateAsync(string id, string json, CancellationToken token)
        {
            var p = JsonUtility.FromJson<ScriptUpdateMessage>(json)?.payload ?? new ScriptUpdatePayload();
            return HandleMutationAsync(id, "script.update", p.scriptPath, p.content ?? "", token);
        }

        private Task HandleDeleteAsync(string id, string json, CancellationToken token)
        {
            var p = JsonUtility.FromJson<ScriptDeleteMessage>(json)?.payload ?? new ScriptDeletePayload();
            return HandleMutationAsync(id, "script.delete", p.scriptPath, null, token);
        }

        private async Task HandleMutationAsync(string id, string command, string path, string content, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<ScriptMutationPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    tcs.TrySetResult(MutateScript(Path.GetDirectoryName(Application.dataPath), path, content, command));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, command, await tcs.Task, token); }
            catch (UPilotCompilePolicyException ex)
            {
                await _bridge.SendErrorAsync(id, ex.Code, ex.Message, token, command, ex.Detail, exception: ex);
            }
            catch (ScriptMutationException ex)
            {
                await _bridge.SendErrorAsync(id, "SCRIPT_MUTATION_FAILED", ex.Message, token, command,
                    new ErrorDetailPayload { changed = ex.Result.saved, sideEffectsMayHaveOccurred = true,
                        importDeferred = ex.Result.importDeferred, changedPaths = ex.Result.changedPaths,
                        deletedPaths = ex.Result.deletedPaths, stage = ex.Stage,
                        exceptionType = ex.InnerException?.GetType().FullName, exceptionMessage = ex.InnerException?.Message,
                        stackTrace = ex.InnerException?.StackTrace, nextAction = "Inspect actual disk state; do not retry automatically." }, exception: ex);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SCRIPT_MUTATION_FAILED", ex.Message, token, command, errorDetail: null, exception: ex);
            }
        }

        // Production calls this only within the queued main-thread action. Injected side effects keep tests isolated.
        internal static ScriptMutationPayload MutateScript(string projectRoot, string path, string content, string command,
            Func<bool> deferImport = null, Action<string> import = null, Func<string, bool> deleteAsset = null,
            Func<bool> syncProjectFiles = null, Action<string> deleteFile = null)
        {
            string absolute = UPilotPlayModeCompilePolicy.ValidateScriptPath(projectRoot, path);
            path = path.Replace('\\', '/');
            bool deferred = (deferImport ?? UPilotPlayModeCompilePolicy.RequireScriptWrite)();
            bool deleting = command == "script.delete";
            if (command == "script.create" && File.Exists(absolute)) throw new IOException("Script already exists: " + path);
            if (command != "script.create" && !File.Exists(absolute)) throw new FileNotFoundException("Script not found", path);
            var deletedPaths = new System.Collections.Generic.List<string>();
            bool metaExisted = deleting && File.Exists(absolute + ".meta");
            var result = new ScriptMutationPayload { scriptPath = path, content = content, importDeferred = deferred };
            string stage = "disk_write";
            try
            {
                if (deleting)
                {
                    if (deferred)
                    {
                        (deleteFile ?? File.Delete)(absolute);
                        if (File.Exists(absolute)) throw new IOException("Script deletion could not be verified");
                        deletedPaths.Add(path); result.saved = true;
                        if (File.Exists(absolute + ".meta"))
                        {
                            (deleteFile ?? File.Delete)(absolute + ".meta");
                            if (File.Exists(absolute + ".meta")) throw new IOException("Meta deletion could not be verified");
                            deletedPaths.Add(path + ".meta");
                        }
                    }
                    else
                    {
                        stage = "asset_delete";
                        bool hadMeta = File.Exists(absolute + ".meta");
                        bool reported = (deleteAsset ?? AssetDatabase.DeleteAsset)(path);
                        if (!File.Exists(absolute)) deletedPaths.Add(path);
                        if (hadMeta && !File.Exists(absolute + ".meta")) deletedPaths.Add(path + ".meta");
                        result.saved = deletedPaths.Count > 0;
                        if (!reported || File.Exists(absolute) || File.Exists(absolute + ".meta"))
                            throw new IOException("AssetDatabase deletion postconditions failed");
                        stage = "project_file_sync";
                        result.projectFilesSynchronized = (syncProjectFiles ?? RefreshGeneratedProjectFiles)();
                    }
                    result.deleted = !File.Exists(absolute);
                    result.deletedPaths = deletedPaths.ToArray();
                    result.changedPaths = result.deletedPaths;
                    result.state = deferred ? "deleted_import_deferred" : "deleted";
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(absolute));
                    // Once an OS write is attempted, a failure can still leave partial disk effects.
                    File.WriteAllText(absolute, content ?? "", System.Text.Encoding.UTF8);
                    result.saved = true; result.changedPaths = new[] { path };
                    if (!deferred)
                    {
                        stage = "asset_import";
                        (import ?? (asset => AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceUpdate)))(path);
                        result.imported = true;
                    }
                    result.state = deferred ? "saved_import_deferred" : "saved_and_imported";
                }
                result.ok = true;
                return result;
            }
            catch (Exception ex)
            {
                if (deleting)
                {
                    // An IO/callback may throw after deleting a file. Report observed state, not just completed calls.
                    deletedPaths.Clear();
                    if (!File.Exists(absolute)) deletedPaths.Add(path);
                    if (metaExisted && !File.Exists(absolute + ".meta")) deletedPaths.Add(path + ".meta");
                }
                result.deleted = deleting && !File.Exists(absolute);
                result.deletedPaths = deletedPaths.ToArray();
                if (deleting) { result.changedPaths = result.deletedPaths; result.saved = deletedPaths.Count > 0; }
                throw new ScriptMutationException(stage, result, ex);
            }
        }

        [Serializable]
        internal sealed class ScriptMutationPayload
        {
            public bool ok, saved, imported, importDeferred, deleted, projectFilesSynchronized;
            public string scriptPath, content, state;
            public string[] changedPaths = Array.Empty<string>();
            public string[] deletedPaths = Array.Empty<string>();
        }

        internal sealed class ScriptMutationException : IOException
        {
            internal readonly ScriptMutationPayload Result;
            internal readonly string Stage;
            internal ScriptMutationException(string stage, ScriptMutationPayload result, Exception inner)
                : base("Script mutation failed at " + stage + ": " + inner.Message, inner) { Result = result; Stage = stage; }
        }

        internal static bool RefreshGeneratedProjectFiles()
        {
            Type syncType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("UnityEditor.SyncVS.SyncSolution"))
                .FirstOrDefault(type => type != null);
            MethodInfo sync = syncType?.GetMethod(
                "Sync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            if (sync == null)
            {
                Debug.LogWarning("[UPilot] Unity project-file generator was not found after script deletion.");
                return false;
            }
            sync.Invoke(null, null);
            return true;
        }
    }
}
