// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CodingRiver.UPilot
{
    // ── DTOs ────────────────────────────────────────────────────────────────────

    [Serializable] public class AssetFindMessage        { public AssetFindPayload payload; }
    [Serializable] public class AssetFindPayload        { public string query = ""; public string assetType = ""; }

    [Serializable] public class AssetCreateFolderMessage  { public AssetCreateFolderPayload payload; }
    [Serializable] public class AssetCreateFolderPayload  { public string parentFolder = ""; public string newFolderName = ""; }

    [Serializable] public class AssetPathPairMessage      { public AssetPathPairPayload payload; }
    [Serializable] public class AssetPathPairPayload      { public string sourcePath = ""; public string destinationPath = ""; }

    [Serializable] public class AssetSinglePathMessage    { public AssetSinglePathPayload payload; }
    [Serializable] public class AssetSinglePathPayload    { public string assetPath = ""; }

    [Serializable]
    public class AssetInfoPayload
    {
        public string assetPath;
        public string assetType;
        public string guid;
        public long   fileSize;
        public long   lastModified;
        public string name;
    }

    [Serializable] public class AssetFindResultPayload { public List<AssetInfoPayload> assets = new List<AssetInfoPayload>(); }
    [Serializable] public class AssetFolderResultPayload { public string folderPath; }

    [Serializable]
    public class AssetMutationResultPayload
    {
        public bool ok = true;
        public string status = "ok";
        public string operation;
        public string sourcePath;
        public string destinationPath;
        public string sourceGuid;
        public string destinationGuid;
        public bool guidPreserved;
        public string assetType;
        public string sha256;
        public bool saved;
        public bool verified;
    }

    [Serializable] public class AssetGetDataMessage { public AssetGetDataPayload payload; }
    [Serializable]
    public class AssetGetDataPayload
    {
        public string assetPath = "";
        public ulong gameObjectId;
        public string componentType = "";
        public int componentIndex;
        public int maxDepth = 10;
        public int maxNodes = 500;
        public string continuationToken = "";
    }

    [Serializable] public class AssetModifyDataMessage { public AssetModifyDataPayload payload; }
    [Serializable] public class AssetModifyDataPayload { public string assetPath = ""; public ulong gameObjectId; public string componentType = ""; public int componentIndex; public List<SerializedPropertyWrite> properties = new List<SerializedPropertyWrite>(); }

    [Serializable] public class SerializedPropertyWrite { public string propertyPath = ""; public string value = ""; }

    [Serializable]
    public class SerializedPropertyInfo
    {
        public string propertyPath;
        public string type;
        public string value;
        public int depth;
        public bool hasChildren;
        public bool isArray;
        public int arraySize;
        public bool truncated;
        public string truncateReason;
        public string managedReferenceType;
        public string objectReferencePath;
        public string objectReferenceGuid;
    }

    [Serializable]
    public class AssetGetDataResultPayload
    {
        public string targetType;
        public int maxDepth;
        public int maxNodes;
        public int returnedCount;
        public int scannedCount;
        public bool truncated;
        public bool depthTruncated;
        public string continuationToken;
        public string nextContinuationToken;
        public List<SerializedPropertyInfo> properties = new List<SerializedPropertyInfo>();
    }
    [Serializable]
    public class AssetModifyDataResultPayload
    {
        public bool ok;
        public int modifiedCount;
        public bool assetTarget;
        public bool dirtyApplied;
        public bool saved;
        public bool reimported;
        public bool persistenceVerified;
        public string sha256Before = "";
        public string sha256After = "";
        public List<SerializedPropertyChangePayload> changes = new List<SerializedPropertyChangePayload>();
        public List<string> errors = new List<string>();
    }

    [Serializable] public class AssetFindBuiltInMessage  { public AssetFindBuiltInPayload payload; }
    [Serializable] public class AssetFindBuiltInPayload  { public string query = ""; public string assetType = ""; }

    [Serializable]
    public class BuiltInAssetInfoPayload
    {
        public string name;
        public string assetType;
        public string source;
    }

    [Serializable]
    public class AssetFindBuiltInResultPayload
    {
        public List<BuiltInAssetInfoPayload> assets = new List<BuiltInAssetInfoPayload>();
    }

    [Serializable] public class PrefabQueryComponentsMessage { public PrefabQueryComponentsPayload payload; }
    [Serializable]
    public class PrefabQueryComponentsPayload
    {
        public string prefabPath = "";
        public string componentType = "";
        public bool includeSerializedFields = true;
        public int maxDepth = 6;
        public int maxResults = 50;
    }

    [Serializable]
    public class PrefabComponentMatchPayload
    {
        public string gameObjectPath;
        public string gameObjectName;
        public string componentType;
        public string fullComponentType;
        public int componentIndex;
        public List<SerializedPropertyInfo> serializedFields = new List<SerializedPropertyInfo>();
    }

    [Serializable]
    public class PrefabQueryComponentsResultPayload
    {
        public string prefabPath;
        public string componentType;
        public bool found;
        public int count;
        public bool readOnly = true;
        public bool changedEditorState = false;
        public List<PrefabComponentMatchPayload> matches = new List<PrefabComponentMatchPayload>();
    }

    [Serializable] public class PrefabPhysicsAuditMessage { public PrefabPhysicsAuditPayload payload; }
    [Serializable]
    public class PrefabPhysicsAuditPayload
    {
        public List<string> prefabPaths = new List<string>();
        public int maxResultsPerPrefab = 1000;
        public string sortBy = "colliderCount";
        public bool descending = true;
    }
    [Serializable] public class PhysicsCountPayload { public string key; public int count; }
    [Serializable]
    public class PrefabPhysicsComponentPayload
    {
        public string gameObjectPath;
        public string componentType;
        public int layer;
        public string layerName;
        public bool gameObjectActiveSelf;
        public bool gameObjectActiveInHierarchy;
        public bool componentEnabled;
        public bool isTrigger;
        public string attachedRigidbodyPath;
        public string attachedRigidbodyType;
    }
    [Serializable]
    public class PrefabPhysicsAssetAuditPayload
    {
        public bool ok = true;
        public string prefabPath;
        public string error;
        public int colliderCount;
        public int triggerCount;
        public int rigidbodyCount;
        public int enabledColliderCount;
        public int activeColliderCount;
        public bool truncated;
        public List<PhysicsCountPayload> typeCounts = new List<PhysicsCountPayload>();
        public List<PhysicsCountPayload> layerCounts = new List<PhysicsCountPayload>();
        public List<PrefabPhysicsComponentPayload> components = new List<PrefabPhysicsComponentPayload>();
    }
    [Serializable]
    public class PrefabPhysicsAuditResultPayload
    {
        public bool ok = true;
        public bool readOnly = true;
        public bool changedEditorState = false;
        public int prefabCount;
        public int failedPrefabCount;
        public string sortBy;
        public bool descending;
        public List<PrefabPhysicsAssetAuditPayload> prefabs = new List<PrefabPhysicsAssetAuditPayload>();
    }

    [Serializable] public class AssetSubresourcesMessage { public AssetSubresourcesPayload payload; }
    [Serializable] public class AssetSubresourcesPayload { public string assetPath = ""; public string typeFilter = ""; public bool includePreview; }
    [Serializable] public class AssetSubresourceInfoPayload { public string name; public string type; public string assetPath; public long localId; public bool preview; }
    [Serializable] public class AssetSubresourcesResultPayload { public string assetPath; public int count; public List<AssetSubresourceInfoPayload> assets = new List<AssetSubresourceInfoPayload>(); }
    [Serializable] public class AssetDependenciesMessage { public AssetDependenciesPayload payload; }
    [Serializable] public class AssetDependenciesPayload { public string assetPath = ""; public bool recursive = true; }
    [Serializable] public class AssetDependencyInfoPayload { public string assetPath; public string assetType; public string guid; public bool direct; }
    [Serializable] public class AssetDependenciesResultPayload { public string assetPath; public bool recursive; public int count; public List<AssetDependencyInfoPayload> dependencies = new List<AssetDependencyInfoPayload>(); }

    [Serializable] public class AnimationAuditMessage { public AssetSinglePathPayload payload; }
    [Serializable] public class AnimatorStateAuditPayload { public string layer; public string statePath; public string name; public string motionName; public string motionPath; public string motionType; public bool isDefault; public float speed; public int transitionCount; }
    [Serializable] public class AnimatorLayerAuditPayload { public string name; public float defaultWeight; public string blendingMode; public string avatarMaskPath; public int stateCount; }
    [Serializable] public class AnimatorControllerAuditResultPayload { public string assetPath; public List<AnimatorLayerAuditPayload> layers = new List<AnimatorLayerAuditPayload>(); public List<AnimatorStateAuditPayload> states = new List<AnimatorStateAuditPayload>(); public List<string> unreferencedClips = new List<string>(); }
    [Serializable] public class AvatarMaskTransformAuditPayload { public string path; public bool active; }
    [Serializable] public class AvatarMaskAuditResultPayload { public string assetPath; public int transformCount; public List<AvatarMaskTransformAuditPayload> transforms = new List<AvatarMaskTransformAuditPayload>(); }
    [Serializable] public class AnimationClipAuditPayload { public string name; public string assetPath; public float length; public float frameRate; public bool loopTime; public bool loopPose; public int curveCount; public int positionCurveCount; public int rotationCurveCount; public int scaleCurveCount; }
    [Serializable] public class ModelImporterAuditResultPayload { public string assetPath; public string animationType; public string avatarSetup; public string sourceAvatarPath; public bool importAnimation; public bool importBlendShapes; public float globalScale; public List<AnimationClipAuditPayload> clips = new List<AnimationClipAuditPayload>(); }
    [Serializable] public class TextureImporterSettingsMessage { public TextureImporterSettingsPayload payload; }
    [Serializable] public class TextureImporterSettingsPayload
    {
        public string assetPath = "";
        public bool applyMipmapEnabled; public bool mipmapEnabled;
        public bool applyAlphaSource; public string alphaSource = "";
        public bool applyAlphaIsTransparency; public bool alphaIsTransparency;
        public bool applySRGBTexture; public bool sRGBTexture;
        public bool applyWrapMode; public string wrapMode = "";
        public bool applyFilterMode; public string filterMode = "";
        public bool applyAnisoLevel; public int anisoLevel;
        public bool applyIsReadable; public bool isReadable;
        public bool applyTextureCompression; public string textureCompression = "";
        public bool applyMaxTextureSize; public int maxTextureSize;
        public bool reimport = true;
    }
    [Serializable] public class TexturePlatformSettingsPayload { public string name; public bool overridden; public int maxTextureSize; public string format; public int compressionQuality; }
    [Serializable] public class TextureImporterSettingsResultPayload
    {
        public bool ok = true;
        public string assetPath;
        public string textureType;
        public bool mipmapEnabled;
        public string alphaSource;
        public bool alphaIsTransparency;
        public bool sRGBTexture;
        public string wrapMode;
        public string filterMode;
        public int anisoLevel;
        public bool isReadable;
        public string textureCompression;
        public int maxTextureSize;
        public int sourceWidth;
        public int sourceHeight;
        public bool hasAlpha;
        public List<TexturePlatformSettingsPayload> platforms = new List<TexturePlatformSettingsPayload>();
        public bool applied;
        public bool reimported;
    }

    // ── Service ─────────────────────────────────────────────────────────────────

    public class UPilotAssetService
    {
        private readonly UPilotBridge _bridge;

        public UPilotAssetService(UPilotBridge bridge) { _bridge = bridge; }

        public void RegisterCommands()
        {
            _bridge.Router.Register("asset.find",         HandleFindAsync);
            _bridge.Router.Register("asset.createFolder",  HandleCreateFolderAsync);
            _bridge.Router.Register("asset.copy",          HandleCopyAsync);
            _bridge.Router.Register("asset.move",          HandleMoveAsync);
            _bridge.Router.Register("asset.delete",        HandleDeleteAsync);
            _bridge.Router.Register("asset.refresh",       HandleRefreshAsync);
            _bridge.Router.Register("asset.getInfo",       HandleGetInfoAsync);
            _bridge.Router.Register("asset.getData",       HandleGetDataAsync);
            _bridge.Router.Register("asset.modifyData",    HandleModifyDataAsync);
            _bridge.Router.Register("asset.findBuiltIn",   HandleFindBuiltInAsync);
            _bridge.Router.Register("prefab.queryComponents", HandlePrefabQueryComponentsAsync);
            _bridge.Router.Register("prefab.physicsAudit", HandlePrefabPhysicsAuditAsync);
            _bridge.Router.Register("asset.subresourcesList", HandleSubresourcesListAsync);
            _bridge.Router.Register("asset.dependencies", HandleAssetDependenciesAsync);
            _bridge.Router.Register("animator.controllerInspect", HandleAnimatorControllerInspectAsync);
            _bridge.Router.Register("animator.avatarMaskInspect", HandleAvatarMaskInspectAsync);
            _bridge.Router.Register("model.importerInspect", HandleModelImporterInspectAsync);
            _bridge.Router.Register("texture.importerGet", HandleTextureImporterGetAsync);
            _bridge.Router.Register("texture.importerPatch", HandleTextureImporterPatchAsync);
            _bridge.Router.Register("asset.reimport", HandleAssetReimportAsync);
        }

        private async Task HandleTextureImporterGetAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<TextureImporterSettingsMessage>(json);
            var path = message?.payload?.assetPath ?? "";
            await RunAssetCommand(id, "texture.importerGet", token, () => ReadTextureImporterSettings(path));
        }

        private async Task HandleTextureImporterPatchAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<TextureImporterSettingsMessage>(json);
            var payload = message?.payload ?? new TextureImporterSettingsPayload();
            await RunAssetCommand(id, "texture.importerPatch", token, () => PatchTextureImporterSettings(payload));
        }

        private async Task HandleAssetReimportAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<AssetSinglePathMessage>(json);
            var path = message?.payload?.assetPath ?? "";
            await RunAssetCommand(id, "asset.reimport", token, () =>
            {
                RequireAssetPath(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                return new GenericOkPayload { ok = true, state = path };
            });
        }

        private async Task RunAssetCommand<T>(string id, string command, CancellationToken token, Func<T> action)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try { tcs.TrySetResult(action()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, command, await tcs.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "ASSET_IMPORTER_FAILED", ex.Message, token, command); }
        }

        private static TextureImporterSettingsResultPayload ReadTextureImporterSettings(string assetPath)
        {
            RequireAssetPath(assetPath);
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Asset is not handled by TextureImporter: " + assetPath);
            importer.GetSourceTextureWidthAndHeight(out var width, out var height);
            var result = new TextureImporterSettingsResultPayload
            {
                assetPath = assetPath,
                textureType = importer.textureType.ToString(),
                mipmapEnabled = importer.mipmapEnabled,
                alphaSource = importer.alphaSource.ToString(),
                alphaIsTransparency = importer.alphaIsTransparency,
                sRGBTexture = importer.sRGBTexture,
                wrapMode = importer.wrapMode.ToString(),
                filterMode = importer.filterMode.ToString(),
                anisoLevel = importer.anisoLevel,
                isReadable = importer.isReadable,
                textureCompression = importer.textureCompression.ToString(),
                maxTextureSize = importer.maxTextureSize,
                sourceWidth = width,
                sourceHeight = height,
                hasAlpha = importer.DoesSourceTextureHaveAlpha(),
            };
            foreach (var platform in new[] { "DefaultTexturePlatform", "Standalone", "Android", "iPhone", "WebGL" })
            {
                var settings = importer.GetPlatformTextureSettings(platform);
                result.platforms.Add(new TexturePlatformSettingsPayload
                {
                    name = platform,
                    overridden = settings.overridden,
                    maxTextureSize = settings.maxTextureSize,
                    format = settings.format.ToString(),
                    compressionQuality = settings.compressionQuality,
                });
            }
            return result;
        }

        private static TextureImporterSettingsResultPayload PatchTextureImporterSettings(TextureImporterSettingsPayload payload)
        {
            RequireAssetPath(payload.assetPath);
            var importer = AssetImporter.GetAtPath(payload.assetPath) as TextureImporter;
            if (importer == null) throw new InvalidOperationException("Asset is not handled by TextureImporter: " + payload.assetPath);
            if (payload.applyMipmapEnabled) importer.mipmapEnabled = payload.mipmapEnabled;
            if (payload.applyAlphaSource) importer.alphaSource = ParseEnum(payload.alphaSource, importer.alphaSource);
            if (payload.applyAlphaIsTransparency) importer.alphaIsTransparency = payload.alphaIsTransparency;
            if (payload.applySRGBTexture) importer.sRGBTexture = payload.sRGBTexture;
            if (payload.applyWrapMode) importer.wrapMode = ParseEnum(payload.wrapMode, importer.wrapMode);
            if (payload.applyFilterMode) importer.filterMode = ParseEnum(payload.filterMode, importer.filterMode);
            if (payload.applyAnisoLevel) importer.anisoLevel = Mathf.Clamp(payload.anisoLevel, 0, 16);
            if (payload.applyIsReadable) importer.isReadable = payload.isReadable;
            if (payload.applyTextureCompression) importer.textureCompression = ParseEnum(payload.textureCompression, importer.textureCompression);
            if (payload.applyMaxTextureSize) importer.maxTextureSize = Mathf.Clamp(payload.maxTextureSize, 32, 16384);
            if (payload.reimport) importer.SaveAndReimport();
            else AssetDatabase.WriteImportSettingsIfDirty(payload.assetPath);
            var result = ReadTextureImporterSettings(payload.assetPath);
            result.applied = true;
            result.reimported = payload.reimport;
            return result;
        }

        private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct
        {
            return Enum.TryParse(value, true, out TEnum parsed) ? parsed : throw new ArgumentException($"Invalid {typeof(TEnum).Name}: {value}");
        }

        private static void RequireAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new ArgumentException("assetPath must be a project-relative Assets/... path.");
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null)
                throw new FileNotFoundException("Asset not found", assetPath);
        }

        // ── asset.find ──────────────────────────────────────────────────────────

        private async Task HandleFindAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetFindMessage>(json);
            var p   = msg?.payload ?? new AssetFindPayload();

            if (string.IsNullOrEmpty(p.query))
            {
                await _bridge.SendErrorAsync(id, "INVALID_QUERY", "Query string is empty.", token, "asset.find");
                return;
            }

            var tcs = new TaskCompletionSource<AssetFindResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    // Build search filter
                    string filter = p.query;
                    if (!string.IsNullOrEmpty(p.assetType))
                        filter += " t:" + p.assetType;

                    string[] guids = AssetDatabase.FindAssets(filter);
                    var result = new AssetFindResultPayload();

                    foreach (var guid in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        result.assets.Add(BuildAssetInfo(path, guid));
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.find", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_FIND_FAILED", ex.Message, token, "asset.find");
            }
        }

        // ── asset.createFolder ──────────────────────────────────────────────────

        private async Task HandleCreateFolderAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetCreateFolderMessage>(json);
            var p   = msg?.payload ?? new AssetCreateFolderPayload();

            if (string.IsNullOrEmpty(p.parentFolder) || string.IsNullOrEmpty(p.newFolderName))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "parentFolder and newFolderName are required.", token, "asset.createFolder");
                return;
            }

            var tcs = new TaskCompletionSource<string>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    string targetPath = p.parentFolder.TrimEnd('/') + "/" + p.newFolderName;
                    if (AssetDatabase.IsValidFolder(targetPath))
                    {
                        tcs.SetResult(targetPath); // already exists, return it
                        return;
                    }

                    string guid = AssetDatabase.CreateFolder(p.parentFolder, p.newFolderName);
                    if (string.IsNullOrEmpty(guid))
                    {
                        tcs.SetException(new Exception($"Failed to create folder: {targetPath}"));
                        return;
                    }

                    tcs.SetResult(AssetDatabase.GUIDToAssetPath(guid));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var folderPath = await tcs.Task;
                var payload = new AssetFolderResultPayload { folderPath = folderPath };
                await _bridge.SendResultAsync(id, "asset.createFolder", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "CREATE_FOLDER_FAILED", ex.Message, token, "asset.createFolder");
            }
        }

        // ── asset.copy ──────────────────────────────────────────────────────────

        private async Task HandleCopyAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetPathPairMessage>(json);
            var p   = msg?.payload ?? new AssetPathPairPayload();

            if (string.IsNullOrEmpty(p.sourcePath) || string.IsNullOrEmpty(p.destinationPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "sourcePath and destinationPath are required.", token, "asset.copy");
                return;
            }

            var tcs = new TaskCompletionSource<AssetMutationResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    tcs.SetResult(CopyAsset(p.sourcePath, p.destinationPath));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await _bridge.SendResultAsync(id, "asset.copy", await tcs.Task, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_COPY_FAILED", ex.Message, token, "asset.copy");
            }
        }

        // ── asset.move ──────────────────────────────────────────────────────────

        private async Task HandleMoveAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetPathPairMessage>(json);
            var p   = msg?.payload ?? new AssetPathPairPayload();

            if (string.IsNullOrEmpty(p.sourcePath) || string.IsNullOrEmpty(p.destinationPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "sourcePath and destinationPath are required.", token, "asset.move");
                return;
            }

            var tcs = new TaskCompletionSource<AssetMutationResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    tcs.SetResult(MoveAsset(p.sourcePath, p.destinationPath));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await _bridge.SendResultAsync(id, "asset.move", await tcs.Task, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_MOVE_FAILED", ex.Message, token, "asset.move");
            }
        }

        // ── asset.delete ────────────────────────────────────────────────────────

        private async Task HandleDeleteAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetSinglePathMessage>(json);
            var p   = msg?.payload ?? new AssetSinglePathPayload();

            if (string.IsNullOrEmpty(p.assetPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "assetPath is required.", token, "asset.delete");
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    bool ok = AssetDatabase.DeleteAsset(p.assetPath);
                    if (!ok)
                    {
                        tcs.SetException(new Exception($"DeleteAsset failed: {p.assetPath}"));
                        return;
                    }

                    AssetDatabase.SaveAssets();
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.delete", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_DELETE_FAILED", ex.Message, token, "asset.delete");
            }
        }

        // ── asset.refresh ───────────────────────────────────────────────────────

        private async Task HandleRefreshAsync(string id, string json, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<bool>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    AssetDatabase.Refresh();
                    tcs.SetResult(true);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.refresh", new GenericOkPayload { status = "ok" }, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_REFRESH_FAILED", ex.Message, token, "asset.refresh");
            }
        }

        // ── asset.getInfo ───────────────────────────────────────────────────────

        private async Task HandleGetInfoAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetSinglePathMessage>(json);
            var p   = msg?.payload ?? new AssetSinglePathPayload();

            if (string.IsNullOrEmpty(p.assetPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PARAMS", "assetPath is required.", token, "asset.getInfo");
                return;
            }

            var tcs = new TaskCompletionSource<AssetInfoPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    string guid = AssetDatabase.AssetPathToGUID(p.assetPath);
                    if (string.IsNullOrEmpty(guid))
                    {
                        tcs.SetException(new Exception($"Asset not found: {p.assetPath}"));
                        return;
                    }

                    tcs.SetResult(BuildAssetInfo(p.assetPath, guid));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.getInfo", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_INFO_FAILED", ex.Message, token, "asset.getInfo");
            }
        }

        // ── asset.getData ───────────────────────────────────────────────────────

        private async Task HandleGetDataAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetGetDataMessage>(json);
            var p   = msg?.payload ?? new AssetGetDataPayload();

            var tcs = new TaskCompletionSource<AssetGetDataResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    SerializedObject so;
                    if (p.gameObjectId != 0)
                    {
                        var go = UPilotEntityIds.GameObjectFromWireId(p.gameObjectId);
                        if (go == null)
                            throw new Exception($"GameObject not found: {p.gameObjectId}");

                        if (string.IsNullOrEmpty(p.componentType))
                            throw new Exception("componentType is required when gameObjectId is provided.");

                        var comp = UPilotComponentService.FindComponentByTypeAndIndex(go, p.componentType, p.componentIndex);
                        if (comp == null)
                            throw new Exception($"Component not found: {p.componentType}[{p.componentIndex}]");

                        so = new SerializedObject(comp);
                    }
                    else if (!string.IsNullOrEmpty(p.assetPath))
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(p.assetPath);
                        if (asset == null)
                            throw new Exception($"Asset not found: {p.assetPath}");

                        so = new SerializedObject(asset);
                    }
                    else
                    {
                        throw new Exception("Either assetPath or gameObjectId+componentType must be provided.");
                    }

                    tcs.TrySetResult(ReadSerializedProperties(
                        so,
                        p.maxDepth,
                        p.maxNodes,
                        p.continuationToken));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.getData", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_GET_DATA_FAILED", ex.Message, token, "asset.getData");
            }
        }

        // ── prefab.queryComponents ───────────────────────────────────────────────

        private async Task HandlePrefabQueryComponentsAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PrefabQueryComponentsMessage>(json);
            var p = msg?.payload ?? new PrefabQueryComponentsPayload();

            if (string.IsNullOrWhiteSpace(p.prefabPath))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PREFAB_PATH", "prefabPath is required.", token, "prefab.queryComponents");
                return;
            }

            if (!p.prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                await _bridge.SendErrorAsync(id, "INVALID_PREFAB_PATH", "prefabPath must point to a .prefab asset.", token, "prefab.queryComponents");
                return;
            }

            if (string.IsNullOrWhiteSpace(p.componentType))
            {
                await _bridge.SendErrorAsync(id, "INVALID_COMPONENT_TYPE", "componentType is required.", token, "prefab.queryComponents");
                return;
            }

            var tcs = new TaskCompletionSource<PrefabQueryComponentsResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                GameObject root = null;
                try
                {
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(p.prefabPath) == null)
                        throw new Exception($"Prefab not found: {p.prefabPath}");

                    root = PrefabUtility.LoadPrefabContents(p.prefabPath);
                    if (root == null)
                        throw new Exception($"Failed to load prefab contents: {p.prefabPath}");

                    var result = new PrefabQueryComponentsResultPayload
                    {
                        prefabPath = p.prefabPath,
                        componentType = p.componentType,
                    };

                    int maxResults = Mathf.Clamp(p.maxResults <= 0 ? 50 : p.maxResults, 1, 500);
                    int serializedMaxDepth = Mathf.Clamp(p.maxDepth <= 0 ? 6 : p.maxDepth, 0, 25);
                    var requestedType = UPilotComponentService.ResolveComponentType(p.componentType);
                    WalkPrefabComponents(
                        root.transform,
                        root.name,
                        p.componentType,
                        requestedType,
                        p.includeSerializedFields,
                        serializedMaxDepth,
                        maxResults,
                        result);

                    result.count = result.matches.Count;
                    result.found = result.count > 0;
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    if (root != null)
                    {
                        try { PrefabUtility.UnloadPrefabContents(root); }
                        catch (Exception unloadEx) { Debug.LogWarning($"[UPilot] Failed to unload prefab contents: {unloadEx.Message}"); }
                    }
                }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "prefab.queryComponents", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "PREFAB_QUERY_COMPONENTS_FAILED", ex.Message, token, "prefab.queryComponents");
            }
        }

        private async Task HandlePrefabPhysicsAuditAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<PrefabPhysicsAuditMessage>(json);
            var p = msg?.payload ?? new PrefabPhysicsAuditPayload();
            if (p.prefabPaths == null || p.prefabPaths.Count == 0)
            {
                await _bridge.SendErrorAsync(
                    id,
                    "INVALID_PREFAB_PATHS",
                    "prefabPaths must contain at least one prefab path.",
                    token,
                    "prefab.physicsAudit");
                return;
            }

            var tcs = new TaskCompletionSource<PrefabPhysicsAuditResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    tcs.TrySetResult(AuditPrefabPhysics(
                        p.prefabPaths,
                        p.maxResultsPerPrefab,
                        p.sortBy,
                        p.descending));
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            try
            {
                await _bridge.SendResultAsync(id, "prefab.physicsAudit", await tcs.Task, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(
                    id,
                    "PREFAB_PHYSICS_AUDIT_FAILED",
                    ex.Message,
                    token,
                    "prefab.physicsAudit");
            }
        }

        // ── asset.modifyData ───────────────────────────────────────────────────

        private async Task HandleModifyDataAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetModifyDataMessage>(json);
            var p   = msg?.payload ?? new AssetModifyDataPayload();

            var tcs = new TaskCompletionSource<AssetModifyDataResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    SerializedObject so;
                    UnityEngine.Object target;
                    var assetTarget = false;
                    if (p.gameObjectId != 0)
                    {
                        var go = UPilotEntityIds.GameObjectFromWireId(p.gameObjectId);
                        if (go == null)
                            throw new Exception($"GameObject not found: {p.gameObjectId}");

                        if (string.IsNullOrEmpty(p.componentType))
                            throw new Exception("componentType is required when gameObjectId is provided.");

                        var comp = UPilotComponentService.FindComponentByTypeAndIndex(go, p.componentType, p.componentIndex);
                        if (comp == null)
                            throw new Exception($"Component not found: {p.componentType}[{p.componentIndex}]");

                        target = comp;
                        so = new SerializedObject(target);
                    }
                    else if (!string.IsNullOrEmpty(p.assetPath))
                    {
                        var asset = AssetDatabase.LoadMainAssetAtPath(p.assetPath);
                        if (asset == null)
                            throw new Exception($"Asset not found: {p.assetPath}");

                        target = asset;
                        assetTarget = true;
                        so = new SerializedObject(target);
                    }
                    else
                    {
                        throw new Exception("Either assetPath or gameObjectId+componentType must be provided.");
                    }

                    tcs.TrySetResult(ApplyModifyData(so, target, assetTarget ? p.assetPath : "", p.properties));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.modifyData", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_MODIFY_DATA_FAILED", ex.Message, token, "asset.modifyData");
            }
        }

        private async Task HandleSubresourcesListAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetSubresourcesMessage>(json);
            var p = msg?.payload ?? new AssetSubresourcesPayload();
            var tcs = new TaskCompletionSource<AssetSubresourcesResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = new AssetSubresourcesResultPayload { assetPath = p.assetPath };
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(p.assetPath))
                    {
                        if (asset == null) continue;
                        var preview = asset.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase) ||
                                      (asset.hideFlags & HideFlags.HideInHierarchy) != 0;
                        if (!p.includePreview && preview) continue;
                        var typeName = asset.GetType().Name;
                        if (!string.IsNullOrEmpty(p.typeFilter) &&
                            !string.Equals(typeName, p.typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long localId);
                        result.assets.Add(new AssetSubresourceInfoPayload
                        {
                            name = asset.name,
                            type = typeName,
                            assetPath = AssetDatabase.GetAssetPath(asset),
                            localId = localId,
                            preview = preview,
                        });
                    }
                    result.count = result.assets.Count;
                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, "asset.subresourcesList", await tcs.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "ASSET_SUBRESOURCES_FAILED", ex.Message, token, "asset.subresourcesList"); }
        }

        private async Task HandleAssetDependenciesAsync(string id, string json, CancellationToken token)
        {
            var message = JsonUtility.FromJson<AssetDependenciesMessage>(json);
            var payload = message?.payload ?? new AssetDependenciesPayload();
            await RunAssetCommand(id, "asset.dependencies", token, () =>
            {
                RequireAssetPath(payload.assetPath);
                var result = new AssetDependenciesResultPayload { assetPath = payload.assetPath, recursive = payload.recursive };
                var direct = new HashSet<string>(AssetDatabase.GetDependencies(payload.assetPath, false), StringComparer.OrdinalIgnoreCase);
                foreach (var path in AssetDatabase.GetDependencies(payload.assetPath, payload.recursive))
                {
                    if (string.Equals(path, payload.assetPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                    result.dependencies.Add(new AssetDependencyInfoPayload
                    {
                        assetPath = path,
                        assetType = type != null ? type.Name : "Unknown",
                        guid = AssetDatabase.AssetPathToGUID(path),
                        direct = direct.Contains(path),
                    });
                }
                result.dependencies = result.dependencies.OrderBy(item => item.assetPath, StringComparer.Ordinal).ToList();
                result.count = result.dependencies.Count;
                return result;
            });
        }

        private async Task HandleAnimatorControllerInspectAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AnimationAuditMessage>(json);
            var path = msg?.payload?.assetPath ?? string.Empty;
            var tcs = new TaskCompletionSource<AnimatorControllerAuditResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                    if (controller == null) throw new InvalidOperationException($"AnimatorController not found: {path}");
                    var result = new AnimatorControllerAuditResultPayload { assetPath = path };
                    var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var layer in controller.layers)
                    {
                        var layerPayload = new AnimatorLayerAuditPayload
                        {
                            name = layer.name,
                            defaultWeight = layer.defaultWeight,
                            blendingMode = layer.blendingMode.ToString(),
                            avatarMaskPath = layer.avatarMask == null ? string.Empty : AssetDatabase.GetAssetPath(layer.avatarMask),
                        };
                        AppendAnimatorStates(result, referenced, layer.name, layer.stateMachine, layer.stateMachine, string.Empty);
                        foreach (var state in result.states)
                            if (state.layer == layer.name) layerPayload.stateCount++;
                        result.layers.Add(layerPayload);
                    }
                    var folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "Assets";
                    foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { folder }))
                    {
                        var clipPath = AssetDatabase.GUIDToAssetPath(guid);
                        foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(clipPath))
                        {
                            if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase))
                            {
                                var key = $"{clipPath}::{clip.name}";
                                if (!referenced.Contains(key)) result.unreferencedClips.Add(key);
                            }
                        }
                    }
                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, "animator.controllerInspect", await tcs.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "ANIMATOR_INSPECT_FAILED", ex.Message, token, "animator.controllerInspect"); }
        }

        private static void AppendAnimatorStates(
            AnimatorControllerAuditResultPayload result,
            HashSet<string> referenced,
            string layerName,
            AnimatorStateMachine root,
            AnimatorStateMachine stateMachine,
            string parentPath)
        {
            foreach (var child in stateMachine.states)
            {
                var state = child.state;
                var motionPath = state.motion == null ? string.Empty : AssetDatabase.GetAssetPath(state.motion);
                var statePath = string.IsNullOrEmpty(parentPath) ? state.name : parentPath + "/" + state.name;
                result.states.Add(new AnimatorStateAuditPayload
                {
                    layer = layerName,
                    statePath = statePath,
                    name = state.name,
                    motionName = state.motion == null ? string.Empty : state.motion.name,
                    motionPath = motionPath,
                    motionType = state.motion == null ? string.Empty : state.motion.GetType().Name,
                    isDefault = root.defaultState == state,
                    speed = state.speed,
                    transitionCount = state.transitions == null ? 0 : state.transitions.Length,
                });
                if (state.motion is AnimationClip clip)
                    referenced.Add($"{motionPath}::{clip.name}");
            }
            foreach (var childMachine in stateMachine.stateMachines)
            {
                var nextPath = string.IsNullOrEmpty(parentPath) ? childMachine.stateMachine.name : parentPath + "/" + childMachine.stateMachine.name;
                AppendAnimatorStates(result, referenced, layerName, root, childMachine.stateMachine, nextPath);
            }
        }

        private async Task HandleAvatarMaskInspectAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AnimationAuditMessage>(json);
            var path = msg?.payload?.assetPath ?? string.Empty;
            var tcs = new TaskCompletionSource<AvatarMaskAuditResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
                    if (mask == null) throw new InvalidOperationException($"AvatarMask not found: {path}");
                    var result = new AvatarMaskAuditResultPayload { assetPath = path, transformCount = mask.transformCount };
                    for (var i = 0; i < mask.transformCount; i++)
                        result.transforms.Add(new AvatarMaskTransformAuditPayload { path = mask.GetTransformPath(i), active = mask.GetTransformActive(i) });
                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, "animator.avatarMaskInspect", await tcs.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "AVATAR_MASK_INSPECT_FAILED", ex.Message, token, "animator.avatarMaskInspect"); }
        }

        private async Task HandleModelImporterInspectAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AnimationAuditMessage>(json);
            var path = msg?.payload?.assetPath ?? string.Empty;
            var tcs = new TaskCompletionSource<ModelImporterAuditResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (importer == null) throw new InvalidOperationException($"ModelImporter not found: {path}");
                    var result = new ModelImporterAuditResultPayload
                    {
                        assetPath = path,
                        animationType = importer.animationType.ToString(),
                        avatarSetup = importer.avatarSetup.ToString(),
                        sourceAvatarPath = importer.sourceAvatar == null ? string.Empty : AssetDatabase.GetAssetPath(importer.sourceAvatar),
                        importAnimation = importer.importAnimation,
                        importBlendShapes = importer.importBlendShapes,
                        globalScale = importer.globalScale,
                    };
                    var settingsByName = new Dictionary<string, ModelImporterClipAnimation>(StringComparer.OrdinalIgnoreCase);
                    var importerClips = importer.clipAnimations;
                    if (importerClips == null || importerClips.Length == 0) importerClips = importer.defaultClipAnimations;
                    foreach (var settings in importerClips) settingsByName[settings.name] = settings;
                    foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    {
                        if (!(asset is AnimationClip clip) || clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) continue;
                        settingsByName.TryGetValue(clip.name, out var importerSettings);
                        var bindings = AnimationUtility.GetCurveBindings(clip);
                        var clipPayload = new AnimationClipAuditPayload
                        {
                            name = clip.name,
                            assetPath = path,
                            length = clip.length,
                            frameRate = clip.frameRate,
                            curveCount = bindings.Length,
                            loopTime = importerSettings != null && importerSettings.loopTime,
                            loopPose = importerSettings != null && importerSettings.loopPose,
                        };
                        foreach (var binding in bindings)
                        {
                            var property = binding.propertyName ?? string.Empty;
                            if (property.IndexOf("m_LocalPosition", StringComparison.OrdinalIgnoreCase) >= 0) clipPayload.positionCurveCount++;
                            else if (property.IndexOf("m_LocalRotation", StringComparison.OrdinalIgnoreCase) >= 0 || property.IndexOf("localEulerAngles", StringComparison.OrdinalIgnoreCase) >= 0) clipPayload.rotationCurveCount++;
                            else if (property.IndexOf("m_LocalScale", StringComparison.OrdinalIgnoreCase) >= 0) clipPayload.scaleCurveCount++;
                        }
                        result.clips.Add(clipPayload);
                    }
                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            try { await _bridge.SendResultAsync(id, "model.importerInspect", await tcs.Task, token); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "MODEL_IMPORTER_INSPECT_FAILED", ex.Message, token, "model.importerInspect"); }
        }

        // ── asset.findBuiltIn ───────────────────────────────────────────────────

        private async Task HandleFindBuiltInAsync(string id, string json, CancellationToken token)
        {
            var msg = JsonUtility.FromJson<AssetFindBuiltInMessage>(json);
            var p   = msg?.payload ?? new AssetFindBuiltInPayload();

            var tcs = new TaskCompletionSource<AssetFindBuiltInResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    var result = new AssetFindBuiltInResultPayload();
                    var sources = new[] { "Resources/unity_builtin_extra", "Library/unity default resources" };

                    foreach (var source in sources)
                    {
                        var allAssets = AssetDatabase.LoadAllAssetsAtPath(source);
                        if (allAssets == null) continue;

                        foreach (var asset in allAssets)
                        {
                            if (asset == null) continue;
                            string assetName = asset.name;
                            string typeName  = asset.GetType().Name;

                            // Filter by name (contains, case-insensitive)
                            if (!string.IsNullOrEmpty(p.query))
                            {
                                if (assetName.IndexOf(p.query, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                            }

                            // Filter by type (exact, case-insensitive)
                            if (!string.IsNullOrEmpty(p.assetType))
                            {
                                if (!string.Equals(typeName, p.assetType, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }

                            result.assets.Add(new BuiltInAssetInfoPayload
                            {
                                name      = assetName,
                                assetType = typeName,
                                source    = source,
                            });
                        }
                    }

                    tcs.TrySetResult(result);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });

            try
            {
                var payload = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.findBuiltIn", payload, token);
            }
            catch (Exception ex)
            {
                await _bridge.SendErrorAsync(id, "ASSET_FIND_BUILTIN_FAILED", ex.Message, token, "asset.findBuiltIn");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static AssetInfoPayload BuildAssetInfo(string assetPath, string guid)
        {
            var info = new AssetInfoPayload
            {
                assetPath = assetPath,
                guid      = guid,
                name      = Path.GetFileName(assetPath),
            };

            var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            info.assetType = assetType != null ? assetType.Name : "Unknown";

            string fullPath = Path.GetFullPath(assetPath);
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                info.fileSize     = fi.Length;
                info.lastModified = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
            }
            else if (Directory.Exists(fullPath))
            {
                info.assetType = "Folder";
            }

            return info;
        }

        private static string GetSerializedPropertyDisplayValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue.ToString();
                case SerializedPropertyType.Boolean:
                    return prop.boolValue.ToString();
                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return $"{c.r},{c.g},{c.b},{c.a}";
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return $"{v2.x},{v2.y}";
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return $"{v3.x},{v3.y},{v3.z}";
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return $"{v4.x},{v4.y},{v4.z},{v4.w}";
                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return $"{q.x},{q.y},{q.z},{q.w}";
                case SerializedPropertyType.Enum:
                    return prop.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    if (prop.objectReferenceValue == null) return "null";
                    var referencePath = AssetDatabase.GetAssetPath(prop.objectReferenceValue);
                    return string.IsNullOrEmpty(referencePath)
                        ? $"{prop.objectReferenceValue.name} ({prop.objectReferenceValue.GetType().Name}) instanceId={UPilotEntityIds.ToWireId(prop.objectReferenceValue)}"
                        : $"{prop.objectReferenceValue.name} ({prop.objectReferenceValue.GetType().Name}) path={referencePath}";
                case SerializedPropertyType.ArraySize:
                    return prop.intValue.ToString();
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return $"{r.x},{r.y},{r.width},{r.height}";
                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return $"center:{b.center.x},{b.center.y},{b.center.z};size:{b.size.x},{b.size.y},{b.size.z}";
                case SerializedPropertyType.LayerMask:
                    return prop.intValue.ToString();
                default:
                    return "(unsupported)";
            }
        }

        private static string ComputeAssetSha256(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !File.Exists(assetPath))
                return "";
            using (var stream = File.OpenRead(assetPath))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static AssetMutationResultPayload CopyAsset(string sourcePath, string destinationPath)
        {
            EnsureAssetExists(sourcePath, "Source");
            if (AssetExists(destinationPath))
                throw new InvalidOperationException($"Destination already exists: {destinationPath}");

            var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            if (!AssetDatabase.CopyAsset(sourcePath, destinationPath))
                throw new InvalidOperationException($"CopyAsset failed: {sourcePath} -> {destinationPath}");

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(
                destinationPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            try
            {
                var result = BuildAssetMutationResult("asset.copy", sourcePath, destinationPath, sourceGuid);
                if (!result.verified)
                    throw new InvalidOperationException(
                        $"Copied asset could not be verified: {destinationPath}; "
                        + $"destinationGuid={result.destinationGuid}; assetType={result.assetType}; "
                        + $"fileExists={File.Exists(destinationPath)}; metaExists={File.Exists(destinationPath + ".meta")}");
                if (string.Equals(result.sourceGuid, result.destinationGuid, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Copied asset unexpectedly reused source GUID: {destinationPath}");
                return result;
            }
            catch
            {
                AssetDatabase.DeleteAsset(destinationPath);
                AssetDatabase.SaveAssets();
                throw;
            }
        }

        internal static AssetMutationResultPayload MoveAsset(string sourcePath, string destinationPath)
        {
            EnsureAssetExists(sourcePath, "Source");
            if (AssetExists(destinationPath))
                throw new InvalidOperationException($"Destination already exists: {destinationPath}");

            var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            var error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"MoveAsset failed: {error}");

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(
                destinationPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            try
            {
                var result = BuildAssetMutationResult("asset.move", sourcePath, destinationPath, sourceGuid);
                if (!result.verified || AssetExists(sourcePath))
                    throw new InvalidOperationException($"Moved asset could not be verified: {destinationPath}");
                if (!result.guidPreserved)
                    throw new InvalidOperationException($"Moved asset did not preserve GUID: {destinationPath}");
                return result;
            }
            catch
            {
                if (AssetExists(destinationPath) && !AssetExists(sourcePath))
                    AssetDatabase.MoveAsset(destinationPath, sourcePath);
                AssetDatabase.SaveAssets();
                throw;
            }
        }

        internal static AssetMutationResultPayload BuildAssetMutationResult(
            string operation,
            string sourcePath,
            string destinationPath,
            string sourceGuid = "")
        {
            var assetType = AssetDatabase.GetMainAssetTypeAtPath(destinationPath);
            var destinationGuid = AssetDatabase.AssetPathToGUID(destinationPath);
            return new AssetMutationResultPayload
            {
                operation = operation,
                sourcePath = sourcePath,
                destinationPath = destinationPath,
                sourceGuid = string.IsNullOrEmpty(sourceGuid)
                    ? AssetDatabase.AssetPathToGUID(sourcePath)
                    : sourceGuid,
                destinationGuid = destinationGuid,
                guidPreserved = !string.IsNullOrEmpty(sourceGuid)
                    && string.Equals(sourceGuid, destinationGuid, StringComparison.Ordinal),
                assetType = assetType != null ? assetType.FullName : "Unknown",
                sha256 = ComputeAssetSha256(destinationPath),
                saved = true,
                verified = AssetExists(destinationPath) && !string.IsNullOrEmpty(destinationGuid),
            };
        }

        private static bool AssetExists(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath))
                return true;
            return !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath))
                && File.Exists(assetPath);
        }

        private static void EnsureAssetExists(string assetPath, string label)
        {
            if (!AssetExists(assetPath))
                throw new InvalidOperationException($"{label} does not exist: {assetPath}");
        }

        internal static AssetGetDataResultPayload ReadSerializedProperties(
            SerializedObject serializedObject,
            int requestedMaxDepth,
            int requestedMaxNodes,
            string continuationToken)
        {
            if (serializedObject == null)
                throw new ArgumentNullException(nameof(serializedObject));

            var maxDepth = Mathf.Clamp(requestedMaxDepth, 0, 64);
            var maxNodes = Mathf.Clamp(requestedMaxNodes <= 0 ? 500 : requestedMaxNodes, 1, 5000);
            var offset = ParseSerializedPropertyContinuationToken(continuationToken);
            var result = new AssetGetDataResultPayload
            {
                targetType = serializedObject.targetObject != null
                    ? serializedObject.targetObject.GetType().Name
                    : "Unknown",
                maxDepth = maxDepth,
                maxNodes = maxNodes,
                continuationToken = continuationToken ?? "",
            };

            serializedObject.Update();
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            var visibleIndex = 0;
            while (iterator.NextVisible(enterChildren))
            {
                var property = iterator.Copy();
                var canEnterChildren = property.hasVisibleChildren && property.depth < maxDepth;
                enterChildren = canEnterChildren;

                if (visibleIndex < offset)
                {
                    visibleIndex++;
                    continue;
                }

                if (result.properties.Count >= maxNodes)
                {
                    result.truncated = true;
                    result.nextContinuationToken = CreateSerializedPropertyContinuationToken(visibleIndex);
                    break;
                }

                var depthTruncated = property.hasVisibleChildren && property.depth >= maxDepth;
                var info = new SerializedPropertyInfo
                {
                    propertyPath = property.propertyPath,
                    type = property.propertyType.ToString(),
                    value = UPilotSerializedPropertyUtility.GetDisplayValue(property),
                    depth = property.depth,
                    hasChildren = property.hasVisibleChildren,
                    isArray = property.isArray,
                    arraySize = property.isArray ? property.arraySize : 0,
                    truncated = depthTruncated,
                    truncateReason = depthTruncated ? "maxDepth" : "",
                };
                if (property.propertyType == SerializedPropertyType.ManagedReference)
                    info.managedReferenceType = property.managedReferenceFullTypename ?? "";
                if (property.propertyType == SerializedPropertyType.ObjectReference
                    && property.objectReferenceValue != null)
                {
                    info.objectReferencePath = AssetDatabase.GetAssetPath(property.objectReferenceValue) ?? "";
                    info.objectReferenceGuid = string.IsNullOrEmpty(info.objectReferencePath)
                        ? ""
                        : AssetDatabase.AssetPathToGUID(info.objectReferencePath);
                }

                result.properties.Add(info);
                result.depthTruncated |= depthTruncated;
                visibleIndex++;
            }

            result.returnedCount = result.properties.Count;
            result.scannedCount = visibleIndex;
            return result;
        }

        private static int ParseSerializedPropertyContinuationToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                return 0;
            const string prefix = "v1:";
            if (!token.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(
                    token.Substring(prefix.Length),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var offset)
                || offset < 0)
                throw new InvalidOperationException("Invalid asset.getData continuationToken.");
            return offset;
        }

        private static string CreateSerializedPropertyContinuationToken(int offset)
        {
            return "v1:" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static AssetModifyDataResultPayload ApplyModifyData(
            SerializedObject serializedObject,
            UnityEngine.Object target,
            string assetPath,
            IList<SerializedPropertyWrite> properties)
        {
            var assetTarget = !string.IsNullOrEmpty(assetPath);
            var result = new AssetModifyDataResultPayload
            {
                ok = true,
                assetTarget = assetTarget,
                sha256Before = assetTarget ? ComputeAssetSha256(assetPath) : "",
            };

            var applied = UPilotSerializedPropertyUtility.Apply(
                serializedObject,
                target,
                properties,
                assetTarget ? "Modify Asset Data" : "Modify Component Data");
            result.modifiedCount = applied.modifiedCount;
            result.changes = applied.changes;

            EditorUtility.SetDirty(target);
            result.dirtyApplied = EditorUtility.IsDirty(target);
            if (assetTarget)
            {
                AssetDatabase.SaveAssetIfDirty(target);
                result.saved = true;
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                result.reimported = true;
                result.sha256After = ComputeAssetSha256(assetPath);
                result.persistenceVerified = VerifyPersistedChanges(assetPath, applied.changes);
                if (!result.persistenceVerified)
                    throw new Exception($"Saved asset verification failed after reimport: {assetPath}");
            }
            else
            {
                var component = target as Component;
                if (component != null && component.gameObject.scene.IsValid())
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                result.persistenceVerified = true;
            }

            return result;
        }

        internal static PrefabPhysicsAuditResultPayload AuditPrefabPhysics(
            IList<string> prefabPaths,
            int requestedMaxResultsPerPrefab,
            string sortBy,
            bool descending)
        {
            if (prefabPaths == null || prefabPaths.Count == 0)
                throw new InvalidOperationException("prefabPaths must contain at least one prefab path.");

            var maxResults = Mathf.Clamp(
                requestedMaxResultsPerPrefab <= 0 ? 1000 : requestedMaxResultsPerPrefab,
                1,
                10000);
            var result = new PrefabPhysicsAuditResultPayload
            {
                prefabCount = prefabPaths.Count,
                sortBy = NormalizePhysicsSort(sortBy),
                descending = descending,
            };

            foreach (var prefabPath in prefabPaths)
                result.prefabs.Add(AuditSinglePrefabPhysics(prefabPath, maxResults));

            result.failedPrefabCount = result.prefabs.Count(item => !item.ok);
            result.prefabs.Sort((left, right) =>
            {
                var comparison = PhysicsSortValue(left, result.sortBy)
                    .CompareTo(PhysicsSortValue(right, result.sortBy));
                if (comparison == 0)
                    comparison = string.Compare(left.prefabPath, right.prefabPath, StringComparison.Ordinal);
                return descending ? -comparison : comparison;
            });
            return result;
        }

        private static PrefabPhysicsAssetAuditPayload AuditSinglePrefabPhysics(
            string prefabPath,
            int maxResults)
        {
            var item = new PrefabPhysicsAssetAuditPayload { prefabPath = prefabPath ?? "" };
            GameObject root = null;
            try
            {
                if (string.IsNullOrWhiteSpace(prefabPath)
                    || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    || AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                    throw new InvalidOperationException($"Prefab not found: {prefabPath}");

                root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null)
                    throw new InvalidOperationException($"Failed to load prefab contents: {prefabPath}");

                var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                var layerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null)
                        continue;
                    if (component is Rigidbody || component is Rigidbody2D)
                    {
                        item.rigidbodyCount++;
                        IncrementCount(typeCounts, component.GetType().Name);
                        continue;
                    }

                    var collider3D = component as Collider;
                    var collider2D = component as Collider2D;
                    if (collider3D == null && collider2D == null)
                        continue;

                    item.colliderCount++;
                    var enabled = collider3D != null ? collider3D.enabled : collider2D.enabled;
                    var trigger = collider3D != null ? collider3D.isTrigger : collider2D.isTrigger;
                    if (enabled)
                        item.enabledColliderCount++;
                    if (component.gameObject.activeInHierarchy)
                        item.activeColliderCount++;
                    if (trigger)
                        item.triggerCount++;
                    IncrementCount(typeCounts, component.GetType().Name);
                    IncrementCount(
                        layerCounts,
                        component.gameObject.layer.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":"
                        + LayerMask.LayerToName(component.gameObject.layer));

                    if (item.components.Count >= maxResults)
                    {
                        item.truncated = true;
                        continue;
                    }

                    Component attachedRigidbody;
                    if (collider3D != null)
                        attachedRigidbody = collider3D.attachedRigidbody
                            ?? collider3D.GetComponentInParent<Rigidbody>();
                    else
                        attachedRigidbody = collider2D.attachedRigidbody
                            ?? collider2D.GetComponentInParent<Rigidbody2D>();
                    item.components.Add(new PrefabPhysicsComponentPayload
                    {
                        gameObjectPath = BuildRelativeGameObjectPath(root.transform, component.transform),
                        componentType = component.GetType().FullName ?? component.GetType().Name,
                        layer = component.gameObject.layer,
                        layerName = LayerMask.LayerToName(component.gameObject.layer),
                        gameObjectActiveSelf = component.gameObject.activeSelf,
                        gameObjectActiveInHierarchy = component.gameObject.activeInHierarchy,
                        componentEnabled = enabled,
                        isTrigger = trigger,
                        attachedRigidbodyPath = attachedRigidbody != null
                            ? BuildRelativeGameObjectPath(root.transform, attachedRigidbody.transform)
                            : "",
                        attachedRigidbodyType = attachedRigidbody != null
                            ? attachedRigidbody.GetType().FullName
                            : "",
                    });
                }

                item.typeCounts = ToCountPayloads(typeCounts);
                item.layerCounts = ToCountPayloads(layerCounts);
            }
            catch (Exception ex)
            {
                item.ok = false;
                item.error = ex.Message;
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
            return item;
        }

        private static string NormalizePhysicsSort(string sortBy)
        {
            switch (sortBy)
            {
                case "triggerCount":
                case "rigidbodyCount":
                case "prefabPath":
                    return sortBy;
                default:
                    return "colliderCount";
            }
        }

        private static int PhysicsSortValue(PrefabPhysicsAssetAuditPayload item, string sortBy)
        {
            switch (sortBy)
            {
                case "triggerCount": return item.triggerCount;
                case "rigidbodyCount": return item.rigidbodyCount;
                default: return item.colliderCount;
            }
        }

        private static void IncrementCount(IDictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key ?? "", out var count);
            counts[key ?? ""] = count + 1;
        }

        private static List<PhysicsCountPayload> ToCountPayloads(IDictionary<string, int> counts)
        {
            return counts
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new PhysicsCountPayload { key = pair.Key, count = pair.Value })
                .ToList();
        }

        private static string BuildRelativeGameObjectPath(Transform root, Transform target)
        {
            if (root == null || target == null)
                return "";
            var names = new Stack<string>();
            var current = target;
            while (current != null)
            {
                names.Push(current.gameObject.name);
                if (current == root)
                    break;
                current = current.parent;
            }
            return string.Join("/", names);
        }

        private static bool VerifyPersistedChanges(
            string assetPath,
            IList<SerializedPropertyChangePayload> changes)
        {
            var reloaded = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (reloaded == null)
                return false;
            var serializedObject = new SerializedObject(reloaded);
            serializedObject.Update();
            foreach (var change in changes)
            {
                if (!change.modified)
                    continue;
                var property = serializedObject.FindProperty(change.propertyPath);
                if (property == null
                    || !string.Equals(
                        UPilotSerializedPropertyUtility.GetDisplayValue(property),
                        change.newValue,
                        StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static void WalkPrefabComponents(
            Transform transform,
            string path,
            string requestedComponentType,
            Type requestedType,
            bool includeSerializedFields,
            int maxDepth,
            int maxResults,
            PrefabQueryComponentsResultPayload result)
        {
            if (transform == null || result.matches.Count >= maxResults)
                return;

            var components = transform.GetComponents<Component>();
            var sameTypeIndexes = new Dictionary<Type, int>();
            foreach (var component in components)
            {
                if (component == null)
                    continue;

                var componentType = component.GetType();
                sameTypeIndexes.TryGetValue(componentType, out var componentIndex);
                sameTypeIndexes[componentType] = componentIndex + 1;

                if (!ComponentTypeMatches(componentType, requestedComponentType, requestedType))
                    continue;

                var match = new PrefabComponentMatchPayload
                {
                    gameObjectPath = path,
                    gameObjectName = transform.gameObject.name,
                    componentType = componentType.Name,
                    fullComponentType = componentType.FullName ?? componentType.Name,
                    componentIndex = componentIndex,
                };

                if (includeSerializedFields)
                    AddSerializedFields(component, maxDepth, match.serializedFields);

                result.matches.Add(match);
                if (result.matches.Count >= maxResults)
                    return;
            }

            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i);
                WalkPrefabComponents(
                    child,
                    path + "/" + child.gameObject.name,
                    requestedComponentType,
                    requestedType,
                    includeSerializedFields,
                    maxDepth,
                    maxResults,
                    result);
                if (result.matches.Count >= maxResults)
                    return;
            }
        }

        private static bool ComponentTypeMatches(Type actualType, string requestedComponentType, Type requestedType)
        {
            if (actualType == null)
                return false;
            if (requestedType != null && requestedType.IsAssignableFrom(actualType))
                return true;
            return actualType.Name.Equals(requestedComponentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(actualType.FullName, requestedComponentType, StringComparison.Ordinal)
                || (actualType.AssemblyQualifiedName != null && string.Equals(actualType.AssemblyQualifiedName, requestedComponentType, StringComparison.Ordinal));
        }

        private static void AddSerializedFields(Component component, int maxDepth, List<SerializedPropertyInfo> fields)
        {
            var so = new SerializedObject(component);
            var iterator = so.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script")
                    continue;
                var prop = iterator.Copy();
                if (prop.depth > maxDepth)
                    continue;
                fields.Add(new SerializedPropertyInfo
                {
                    propertyPath = prop.propertyPath,
                    type = prop.propertyType.ToString(),
                    value = GetSerializedPropertyDisplayValue(prop),
                    depth = prop.depth,
                    hasChildren = prop.hasChildren,
                    isArray = prop.isArray,
                    arraySize = prop.isArray ? prop.arraySize : 0,
                });
            }
        }

        private static void SetSerializedPropertyValue(SerializedProperty prop, string value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = bool.Parse(value);
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value;
                    break;
                case SerializedPropertyType.Color:
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 4) throw new Exception("Color must be 'r,g,b,a'.");
                        prop.colorValue = new Color(
                            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                    }
                case SerializedPropertyType.Vector2:
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 2) throw new Exception("Vector2 must be 'x,y'.");
                        prop.vector2Value = new Vector2(
                            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                    }
                case SerializedPropertyType.Vector3:
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 3) throw new Exception("Vector3 must be 'x,y,z'.");
                        prop.vector3Value = new Vector3(
                            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                    }
                case SerializedPropertyType.Vector4:
                    {
                        var parts = value.Split(',');
                        if (parts.Length != 4) throw new Exception("Vector4 must be 'x,y,z,w'.");
                        prop.vector4Value = new Vector4(
                            float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                            float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)
                        );
                        break;
                    }
                case SerializedPropertyType.Enum:
                    prop.enumValueIndex = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.ObjectReference:
                    prop.objectReferenceValue = string.IsNullOrEmpty(value)
                        ? null
                        : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value);
                    break;
                case SerializedPropertyType.ArraySize:
                    prop.arraySize = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new Exception($"Unsupported property type: {prop.propertyType}");
            }
        }
    }
}
