// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] public class SelectionSetMessage  { public SelectionSetPayload payload; }
    [Serializable]
    public class SelectionSetPayload
    {
        public List<ulong>  gameObjectIds = new();
        public List<string> assetPaths    = new();
    }

    [Serializable]
    public class SelectionResultPayload
    {
        public List<ulong>  selectedGameObjectIds = new();
        public List<string> selectedAssetPaths    = new();
        public ulong        activeGameObjectId;
        public int          selectionCount;
    }

    [Serializable]
    public class SelectionObjectIdentityPayload
    {
        public ulong instanceId;
        public string name;
        public string typeName;
        public string assetPath;
    }

    [Serializable]
    public class SelectionClearResultPayload
    {
        public bool ok;
        public bool businessEffectVerified;
        public bool changed;
        public string status;
        public int beforeSelectionCount;
        public int afterSelectionCount;
        public SelectionObjectIdentityPayload beforeActiveObject;
        public SelectionObjectIdentityPayload afterActiveObject;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotSelectionService
    {
        private readonly UPilotBridge _bridge;

        public UPilotSelectionService(UPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("selection.get",   HandleGetAsync);
            _bridge.Router.Register("selection.set",   HandleSetAsync);
            _bridge.Router.Register("selection.clear",  HandleClearAsync);
        }

        // ── selection.get ───────────────────────────────────────────────────────

        private async Task HandleGetAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<SelectionResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.SetResult(BuildSelectionResult()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "selection.get", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SELECTION_GET_FAILED", ex.Message, token, "selection.get");
            }
        }

        // ── selection.set ───────────────────────────────────────────────────────

        private async Task HandleSetAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<SelectionSetMessage>(json);
            var p   = msg?.payload ?? new SelectionSetPayload();

            var tcs = new TaskCompletionSource<SelectionResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var objects = new List<UnityEngine.Object>();

                    // Resolve game object IDs
                    if (p.gameObjectIds != null)
                    {
                        foreach (ulong instanceId in p.gameObjectIds)
                        {
                            var obj = UPilotEntityIds.GameObjectFromWireId(instanceId);
                            if (obj != null) objects.Add(obj);
                        }
                    }

                    // Resolve asset paths
                    if (p.assetPaths != null)
                    {
                        foreach (string path in p.assetPaths)
                        {
                            if (string.IsNullOrEmpty(path)) continue;
                            var asset = AssetDatabase.LoadMainAssetAtPath(path);
                            if (asset != null) objects.Add(asset);
                        }
                    }

                    Selection.objects = objects.ToArray();
                    tcs.SetResult(BuildSelectionResult());
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "selection.set", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SELECTION_SET_FAILED", ex.Message, token, "selection.set");
            }
        }

        // ── selection.clear ─────────────────────────────────────────────────────

        private async Task HandleClearAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<SelectionClearResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var beforeCount = Selection.objects?.Length ?? 0;
                    var beforeActive = BuildSelectionIdentity(Selection.activeObject);
                    Selection.objects = new UnityEngine.Object[0];
                    var afterCount = Selection.objects?.Length ?? 0;
                    var afterActive = BuildSelectionIdentity(Selection.activeObject);
                    var verified = afterCount == 0 && Selection.activeObject == null;
                    tcs.TrySetResult(new SelectionClearResultPayload
                    {
                        ok = verified,
                        businessEffectVerified = verified,
                        changed = beforeCount > 0 || beforeActive != null,
                        status = beforeCount > 0 || beforeActive != null ? "cleared" : "already_empty",
                        beforeSelectionCount = beforeCount,
                        afterSelectionCount = afterCount,
                        beforeActiveObject = beforeActive,
                        afterActiveObject = afterActive,
                    });
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                await _bridge.SendResultAsync(id, "selection.clear", await tcs.Task, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "SELECTION_CLEAR_FAILED", ex.Message, token, "selection.clear",
                    new ErrorDetailPayload { commandSubmitted = true, sideEffectsMayHaveOccurred = true });
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static SelectionResultPayload BuildSelectionResult()
        {
            var result = new SelectionResultPayload();

            // Game objects in scene
            foreach (var go in Selection.gameObjects)
            {
                result.selectedGameObjectIds.Add(UPilotEntityIds.ToWireId(go));
            }

            // All selected objects — check for assets
            foreach (var obj in Selection.objects)
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    result.selectedAssetPaths.Add(assetPath);
                }
            }

            result.activeGameObjectId = Selection.activeGameObject != null
                ? UPilotEntityIds.ToWireId(Selection.activeGameObject)
                : 0;
            result.selectionCount = Selection.objects.Length;

            return result;
        }

        private static SelectionObjectIdentityPayload BuildSelectionIdentity(UnityEngine.Object value)
        {
            if (value == null) return null;
            return new SelectionObjectIdentityPayload
            {
                instanceId = UPilotEntityIds.ToWireId(value),
                name = value.name,
                typeName = value.GetType().FullName,
                assetPath = AssetDatabase.GetAssetPath(value) ?? string.Empty,
            };
        }
    }
}
