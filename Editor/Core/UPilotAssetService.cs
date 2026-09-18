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
        public bool deleted;
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
        public string objectReferenceLocalFileId;
        public string evidenceKind;
        public string referenceScope;
        public string notSearchedReason;
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
        public bool followObjectReferences;
        public bool includeNestedPrefabContents;
        public int referenceDepth = 1;
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
    public class PrefabObjectReferencePayload
    {
        public string assetPath;
        public string guid;
        public string localFileId;
        public string sourceAssetPath;
        public string gameObjectPath;
        public string propertyPath;
        public string evidenceKind = "objectReference";
        public string referenceScope;
        public int depth;
        public List<string> referenceChain = new List<string>();
        public string notSearchedReason;
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
        public bool followObjectReferences;
        public bool coverageComplete = true;
        public bool truncated;
        public string truncationReason;
        public bool inspectionStateChanged;
        public string inspectionStateChangeAssetPath;
        public int visitedReferenceNodes;
        public bool partial;
        public int nextReferencePosition;
        public bool sideEffectsMayHaveOccurred;
        public bool cleanupVerified = true;
        public string cleanupError;
        public List<string> unresolvedResources = new List<string>();
        public bool budgetOverrun;
        public List<string> notSearchedReasons = new List<string>();
        public List<PrefabComponentMatchPayload> matches = new List<PrefabComponentMatchPayload>();
        public List<PrefabObjectReferencePayload> objectReferences = new List<PrefabObjectReferencePayload>();
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
    [Serializable] public class AssetReferenceQueryPayload { public string kind = ""; public string value = ""; public string guid = ""; public string localFileId = ""; public List<string> propertyPaths = new List<string>(); }
    [Serializable]
    public class AssetDependenciesPayload
    {
        public string assetPath = "";
        public bool recursive = true;
        public string evidenceMode = "file";
        public string runtimeBoundary = "none";
        public string direction = "forward";
        public AssetReferenceQueryPayload referenceQuery;
        public List<string> scope = new List<string>();
        public int maxNodes = 500;
        public int timeBudgetMs = 5000;
        public string continuationToken = "";
    }
    [Serializable]
    public class AssetDependencyInfoPayload
    {
        public string assetPath;
        public string assetType;
        public string guid;
        public bool direct;
        public string localFileId;
        public string evidenceKind = "fileDependency";
        public string sourceAssetPath;
        public string gameObjectPath;
        public string propertyPath;
        public string referenceScope;
        public List<string> referenceChain = new List<string>();
        public string notSearchedReason;
        public bool runtimeBoundaryViolation;
    }
    [Serializable]
    public class AssetDependenciesResultPayload
    {
        public string assetPath;
        public bool recursive;
        public bool readOnly = true;
        public bool changedEditorState = false;
        public string evidenceMode = "file";
        public bool coverageComplete = true;
        public bool truncated;
        public string truncationReason;
        // Unlike a missing dependency, an incomplete object query must explain
        // the unsearched portion at result level as well as on individual
        // evidence rows.  This keeps bounded/model results from being read as
        // a negative match.
        public string notSearchedReason;
        public int visitedNodes;
        public int candidateCount;
        public string continuationToken;
        public string nextContinuationToken;
        public string cursorStatus;
        public bool cleanupVerified = true;
        public string cleanupError;
        public List<string> unresolvedResources = new List<string>();
        public bool sideEffectsMayHaveOccurred;
        public bool partial;
        public int nextPosition;
        public bool budgetOverrun;
        public bool inspectionStateChanged;
        public string inspectionStateChangeAssetPath;
        public int count;
        public List<AssetDependencyInfoPayload> dependencies = new List<AssetDependencyInfoPayload>();
    }

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
        private static readonly object DependencyCursorGate = new object();
        private static readonly Dictionary<string, AssetDependencyCursorState> DependencyCursors = new Dictionary<string, AssetDependencyCursorState>(StringComparer.Ordinal);
        private const int DependencyCursorLimit = 8;
        private static readonly TimeSpan DependencyCursorTtl = TimeSpan.FromMinutes(10);
        // Deliberately opt-in fault seam for the isolated Prefab cleanup boundary.
        // Production leaves this null; it exists so the EditMode fixture can prove
        // that a real loaded Prefab is reported as unresolved when cleanup fails.
        internal static Func<GameObject, Exception> PrefabContentsUnloadFaultForTests;
        // Loading Prefab contents creates editor-owned objects which Unity can report as
        // individually dirty even when the inspected source was untouched. Keep the
        // callback-boundary probe opt-in for deterministic EditMode containment tests;
        // production still detects a genuinely dirty isolated Prefab scene below.
        internal static Func<GameObject, bool> PrefabContentsChangedStateForTests;

        private sealed class AssetDependencyCursorState
        {
            public string cursorId;
            public string bridgeSessionId;
            public string domainGeneration;
            public string querySignature;
            public string candidateFingerprint;
            public List<string> candidates;
            public int nextIndex;
            public DateTime createdAtUtc;
            public readonly Dictionary<string, AssetDependenciesResultPayload> pages = new Dictionary<string, AssetDependenciesResultPayload>(StringComparer.Ordinal);
        }

        private sealed class AssetDependencyQueryException : Exception
        {
            public readonly string errorCode;

            public AssetDependencyQueryException(string errorCode, string message)
                : base(message)
            {
                this.errorCode = errorCode;
            }
        }

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
            // A dependency traversal can observe cancellation on a Unity-access boundary and still has a
            // useful partial result. Do not discard that evidence merely because the request token fired.
            var responseToken = token.IsCancellationRequested ? CancellationToken.None : token;
            try { await _bridge.SendResultAsync(id, command, await tcs.Task, responseToken); }
            catch (AssetDependencyQueryException ex) { await _bridge.SendErrorAsync(id, ex.errorCode, ex.Message, responseToken, command); }
            catch (Exception ex) { await _bridge.SendErrorAsync(id, "ASSET_IMPORTER_FAILED", ex.Message, responseToken, command); }
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

            var tcs = new TaskCompletionSource<AssetMutationResultPayload>();
            _bridge.EnqueueTracked(id, () =>
            {
                try
                {
                    tcs.SetResult(DeleteAsset(p.assetPath));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });

            try
            {
                var result = await tcs.Task;
                await _bridge.SendResultAsync(id, "asset.delete", result, token);
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
                await _bridge.SendResultAsync(id, "asset.refresh", new GenericOkPayload { ok = true, status = "ok" }, token);
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
            if (!TryValidatePrefabReferenceQuery(p, out var prefabValidationCode, out var prefabValidationMessage))
            {
                await _bridge.SendErrorAsync(id, prefabValidationCode, prefabValidationMessage, token, "prefab.queryComponents");
                return;
            }
            var tcs = new TaskCompletionSource<PrefabQueryComponentsResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridge.EnqueueTracked(id, () =>
            {
                GameObject root = null;
                PrefabQueryComponentsResultPayload result = null;
                Exception failure = null;
                try
                {
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(p.prefabPath) == null)
                        throw new Exception($"Prefab not found: {p.prefabPath}");

                    var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(p.prefabPath);
                    if (prefabAsset != null && PrefabUtility.GetPrefabAssetType(prefabAsset) == PrefabAssetType.Model)
                    {
                        result = new PrefabQueryComponentsResultPayload
                        {
                            prefabPath = p.prefabPath,
                            componentType = p.componentType,
                            followObjectReferences = p.followObjectReferences,
                            coverageComplete = false,
                            truncated = true,
                            partial = true,
                            truncationReason = "modelPrefabContentsUnsupported",
                        };
                        AddPrefabNotSearchedReason(result, "modelPrefabContentsUnsupported");
                    }
                    else
                    {
                        root = PrefabUtility.LoadPrefabContents(p.prefabPath);
                        if (root == null)
                            throw new Exception($"Failed to load prefab contents: {p.prefabPath}");

                        result = new PrefabQueryComponentsResultPayload
                        {
                            prefabPath = p.prefabPath,
                            componentType = p.componentType,
                            followObjectReferences = p.followObjectReferences,
                        };
                        var referenceSeeds = new List<PrefabReferenceWorkItem>();

                        if (!MarkPrefabContentsDirty(root, result, p.prefabPath))
                        {
                            int maxResults = Mathf.Clamp(p.maxResults <= 0 ? 50 : p.maxResults, 1, 500);
                            int serializedMaxDepth = Mathf.Clamp(p.maxDepth <= 0 ? 6 : p.maxDepth, 0, 25);
                            var requestedType = UPilotComponentService.ResolveComponentType(p.componentType);
                            WalkPrefabComponents(
                                root.transform,
                                root.name,
                                root.transform,
                                p.prefabPath,
                                p.componentType,
                                requestedType,
                                p.includeSerializedFields,
                                serializedMaxDepth,
                                maxResults,
                                p.followObjectReferences,
                                referenceSeeds,
                                result);

                            if (p.followObjectReferences)
                                TraversePrefabObjectReferences(referenceSeeds, p.prefabPath, p.includeNestedPrefabContents, p.referenceDepth, 500, 5000, result, token);
                        }

                        result.count = result.matches.Count;
                        result.found = result.count > 0;
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    if (root != null)
                    {
                        try { UnloadPrefabContents(root); }
                        catch (Exception unloadEx)
                        {
                            // The isolated Prefab may still be loaded even though the query result was otherwise usable.
                            // Do not advertise a clean read-only result in that case.
                            Debug.LogWarning($"[UPilot] Failed to unload prefab contents: {unloadEx.Message}");
                            if (result != null)
                                MarkCleanupFailure(result, p.prefabPath, unloadEx);
                        }
                    }
                }
                if (failure != null)
                    tcs.TrySetException(failure);
                else
                    tcs.TrySetResult(result);
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

        private static bool TryValidatePrefabReferenceQuery(PrefabQueryComponentsPayload payload, out string errorCode, out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;
            if (payload.referenceDepth < 1 || payload.referenceDepth > 4)
            {
                errorCode = "REFERENCE_DEPTH_INVALID";
                errorMessage = "referenceDepth must be an integer from 1 through 4.";
                return false;
            }
            if (!payload.followObjectReferences && payload.referenceDepth != 1)
            {
                errorCode = "REFERENCE_QUERY_INVALID";
                errorMessage = "referenceDepth requires followObjectReferences=true.";
                return false;
            }
            if (payload.includeNestedPrefabContents && !payload.followObjectReferences)
            {
                errorCode = "REFERENCE_QUERY_INVALID";
                errorMessage = "includeNestedPrefabContents requires followObjectReferences=true.";
                return false;
            }
            return true;
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
            if (!TryValidateDependencyPayload(payload, out var validationCode, out var validationMessage))
            {
                await _bridge.SendErrorAsync(id, validationCode, validationMessage, token, "asset.dependencies");
                return;
            }
            bool isLegacyFileQuery = string.Equals(payload.evidenceMode, "file", StringComparison.Ordinal)
                && string.Equals(payload.direction, "forward", StringComparison.Ordinal)
                && string.Equals(payload.runtimeBoundary, "none", StringComparison.Ordinal)
                && payload.referenceQuery == null
                && (payload.scope == null || payload.scope.Count == 0)
                && payload.maxNodes == 500
                && payload.timeBudgetMs == 5000
                && string.IsNullOrEmpty(payload.continuationToken);
            bool isObjectForward = string.Equals(payload.evidenceMode, "object", StringComparison.Ordinal)
                && string.Equals(payload.direction, "forward", StringComparison.Ordinal)
                && payload.referenceQuery == null
                && (payload.scope == null || payload.scope.Count == 0)
                && string.IsNullOrEmpty(payload.continuationToken);
            bool isObjectReverse = string.Equals(payload.evidenceMode, "object", StringComparison.Ordinal)
                && string.Equals(payload.direction, "reverse", StringComparison.Ordinal)
                && string.IsNullOrEmpty(payload.assetPath)
                && payload.referenceQuery != null
                && payload.scope != null
                && payload.scope.Count > 0;
            if (!isLegacyFileQuery && !isObjectForward && !isObjectReverse)
            {
                await _bridge.SendErrorAsync(
                    id,
                    "OBJECT_DEPENDENCY_QUERY_UNSUPPORTED",
                    "The dependency query shape is unsupported; no asset query was run.",
                    token,
                    "asset.dependencies");
                return;
            }
            if (isObjectForward)
            {
                await RunAssetCommand(id, "asset.dependencies", token, () => BuildObjectAssetDependenciesWithCancellation(payload, token));
                return;
            }
            if (isObjectReverse)
            {
                var bridgeSessionId = _bridge.GetStatus().SessionId ?? string.Empty;
                var domainGeneration = UPilotWindowDiagnostics.DomainReloadEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await RunAssetCommand(id, "asset.dependencies", token, () => BuildReverseObjectDependenciesWithContext(payload, bridgeSessionId, domainGeneration, token));
                return;
            }
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

        private static bool TryValidateDependencyPayload(AssetDependenciesPayload payload, out string errorCode, out string errorMessage)
        {
            errorCode = null;
            errorMessage = null;
            if (payload == null)
            {
                errorCode = "REFERENCE_QUERY_INVALID";
                errorMessage = "payload is required.";
                return false;
            }
            if (!string.Equals(payload.evidenceMode, "file", StringComparison.Ordinal)
                && !string.Equals(payload.evidenceMode, "object", StringComparison.Ordinal))
            {
                errorCode = "DEPENDENCY_EVIDENCE_MODE_INVALID";
                errorMessage = "evidenceMode must be file or object.";
                return false;
            }
            if (!string.Equals(payload.runtimeBoundary, "none", StringComparison.Ordinal)
                && !string.Equals(payload.runtimeBoundary, "ExcludeAssetsEditor", StringComparison.Ordinal))
            {
                errorCode = "DEPENDENCY_RUNTIME_BOUNDARY_INVALID";
                errorMessage = "runtimeBoundary must be none or ExcludeAssetsEditor.";
                return false;
            }
            if (!string.Equals(payload.direction, "forward", StringComparison.Ordinal)
                && !string.Equals(payload.direction, "reverse", StringComparison.Ordinal))
            {
                errorCode = "DEPENDENCY_DIRECTION_INVALID";
                errorMessage = "direction must be forward or reverse.";
                return false;
            }
            if (payload.maxNodes < 1 || payload.maxNodes > 5000)
            {
                errorCode = "DEPENDENCY_NODE_BUDGET_INVALID";
                errorMessage = "maxNodes must be an integer from 1 through 5000.";
                return false;
            }
            if (payload.timeBudgetMs < 1 || payload.timeBudgetMs > 30000)
            {
                errorCode = "DEPENDENCY_TIME_BUDGET_INVALID";
                errorMessage = "timeBudgetMs must be an integer from 1 through 30000.";
                return false;
            }
            var scope = payload.scope ?? new List<string>();
            if (scope.Any(path => string.IsNullOrEmpty(path)
                                  || path != path.Trim()
                                  || path.IndexOf('\\') >= 0
                                  || !path.StartsWith("Assets/", StringComparison.Ordinal)
                                  || path.Split('/').Any(segment => segment == "." || segment == "..")))
            {
                errorCode = "REFERENCE_SCOPE_INVALID";
                errorMessage = "scope must contain project-relative Assets/ folders only.";
                return false;
            }
            if (string.Equals(payload.direction, "forward", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(payload.assetPath))
                {
                    errorCode = "ASSET_PATH_REQUIRED";
                    errorMessage = "assetPath is required for forward dependency queries.";
                    return false;
                }
                if (payload.referenceQuery != null || scope.Count != 0 || !string.IsNullOrEmpty(payload.continuationToken))
                {
                    errorCode = "REFERENCE_QUERY_INVALID";
                    errorMessage = "referenceQuery, scope, and continuationToken are only valid for reverse queries.";
                    return false;
                }
                if (string.Equals(payload.runtimeBoundary, "ExcludeAssetsEditor", StringComparison.Ordinal)
                    && !string.Equals(payload.evidenceMode, "object", StringComparison.Ordinal))
                {
                    errorCode = "DEPENDENCY_RUNTIME_BOUNDARY_INVALID";
                    errorMessage = "runtimeBoundary is only available for object evidence.";
                    return false;
                }
                return true;
            }

            if (!string.IsNullOrEmpty(payload.assetPath))
            {
                errorCode = "REFERENCE_QUERY_INVALID";
                errorMessage = "assetPath must be empty for reverse dependency queries.";
                return false;
            }
            if (!string.Equals(payload.evidenceMode, "object", StringComparison.Ordinal))
            {
                errorCode = "DEPENDENCY_EVIDENCE_MODE_INVALID";
                errorMessage = "Reverse queries require evidenceMode=object.";
                return false;
            }
            if (payload.referenceQuery == null || scope.Count == 0)
            {
                errorCode = "REFERENCE_QUERY_INVALID";
                errorMessage = "Reverse queries require one referenceQuery and a non-empty scope.";
                return false;
            }
            var query = payload.referenceQuery;
            if (string.Equals(query.kind, "guid", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(query.value)
                && string.IsNullOrEmpty(query.guid)
                && string.IsNullOrEmpty(query.localFileId)
                && (query.propertyPaths == null || query.propertyPaths.Count == 0))
                return true;
            if (string.Equals(query.kind, "object", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(query.guid)
                && !string.IsNullOrEmpty(query.localFileId)
                && string.IsNullOrEmpty(query.value)
                && (query.propertyPaths == null || query.propertyPaths.Count == 0))
                return true;
            if (string.Equals(query.kind, "stringLiteral", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(query.value)
                && string.IsNullOrEmpty(query.guid)
                && string.IsNullOrEmpty(query.localFileId)
                && query.propertyPaths != null
                && query.propertyPaths.Count > 0
                && query.propertyPaths.All(path => !string.IsNullOrEmpty(path)))
                return true;
            errorCode = "REFERENCE_QUERY_INVALID";
            errorMessage = "referenceQuery has fields incompatible with its kind.";
            return false;
        }

        // Kept as the narrow test/reflection entry point. The Bridge path supplies the request token below.
        private static AssetDependenciesResultPayload BuildObjectAssetDependencies(AssetDependenciesPayload payload)
        {
            return BuildObjectAssetDependenciesWithCancellation(payload, CancellationToken.None);
        }

        internal static AssetDependenciesResultPayload BuildObjectAssetDependenciesWithCancellation(AssetDependenciesPayload payload, CancellationToken cancellationToken)
        {
            RequireAssetPath(payload.assetPath);
            var sourceFingerprint = GetAssetDependencyFingerprint(payload.assetPath);
            var result = new AssetDependenciesResultPayload
            {
                assetPath = payload.assetPath,
                recursive = payload.recursive,
                evidenceMode = "object",
            };
            var traversal = new PrefabQueryComponentsResultPayload();
            var pending = new Queue<PrefabReferenceWorkItem>();
            GameObject prefabRoot = null;
            try
            {
                // Model Prefabs are identifiable by PrefabAssetType even though their
                // imported source path is typically .obj/.fbx rather than .prefab.
                // Do not attempt isolated Prefab loading for either representation.
                var sourceGameObject = AssetDatabase.LoadAssetAtPath<GameObject>(payload.assetPath);
                if (sourceGameObject != null && PrefabUtility.GetPrefabAssetType(sourceGameObject) == PrefabAssetType.Model)
                {
                    result.coverageComplete = false;
                    result.truncated = true;
                    result.partial = true;
                    result.truncationReason = "modelPrefabContentsUnsupported";
                    result.notSearchedReason = "modelPrefabContentsUnsupported";
                    return result;
                }
                if (payload.assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    prefabRoot = PrefabUtility.LoadPrefabContents(payload.assetPath);
                    if (prefabRoot == null)
                        throw new InvalidOperationException("Failed to load Prefab contents: " + payload.assetPath);
                    if (!MarkPrefabContentsDirty(prefabRoot, traversal, payload.assetPath))
                        foreach (var component in prefabRoot.GetComponentsInChildren<Component>(true))
                        {
                            if (component == null)
                                continue;
                            var parent = new PrefabReferenceWorkItem(component, payload.assetPath, component.gameObject.name, "", 0, new List<string>());
                            AppendSerializedObjectReferences(component, payload.assetPath, parent, pending, traversal, prefabRoot.transform, cancellationToken);
                            if (traversal.partial)
                                break;
                        }
                }
                else
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(payload.assetPath);
                    if (asset == null)
                        throw new InvalidOperationException("Asset not found: " + payload.assetPath);
                    foreach (var source in AssetDatabase.LoadAllAssetsAtPath(payload.assetPath))
                    {
                        if (source == null)
                            continue;
                        var parent = new PrefabReferenceWorkItem(source, payload.assetPath, "", "", 0, new List<string>());
                        AppendSerializedObjectReferences(source, payload.assetPath, parent, pending, traversal, null, cancellationToken);
                        if (traversal.partial)
                            break;
                    }
                }

                if (!MarkInspectionStateChanged(payload.assetPath, sourceFingerprint, traversal))
                {
                    TraversePrefabObjectReferences(
                        pending.ToList(),
                        payload.assetPath,
                        includeNestedPrefabContents: true,
                        referenceDepth: payload.recursive ? 4 : 1,
                        maxReferenceNodes: payload.maxNodes,
                        timeBudgetMs: payload.timeBudgetMs,
                        result: traversal,
                        cancellationToken: cancellationToken);
                }
                result.coverageComplete = traversal.coverageComplete;
                result.truncated = traversal.truncated;
                result.truncationReason = traversal.truncationReason;
                result.notSearchedReason = DependencyNotSearchedReason(traversal.truncationReason);
                result.inspectionStateChanged = traversal.inspectionStateChanged;
                result.inspectionStateChangeAssetPath = traversal.inspectionStateChangeAssetPath;
                result.changedEditorState = traversal.changedEditorState;
                result.visitedNodes = traversal.visitedReferenceNodes;
                result.partial = traversal.partial;
                result.nextPosition = traversal.nextReferencePosition;
                result.sideEffectsMayHaveOccurred = traversal.sideEffectsMayHaveOccurred;
                result.cleanupVerified = traversal.cleanupVerified;
                result.cleanupError = traversal.cleanupError;
                result.unresolvedResources.AddRange(traversal.unresolvedResources);
                result.budgetOverrun = traversal.budgetOverrun;
                foreach (var item in traversal.objectReferences)
                {
                    var dependency = new AssetDependencyInfoPayload
                    {
                        assetPath = item.assetPath,
                        assetType = string.IsNullOrEmpty(item.assetPath) ? "Unknown" : (AssetDatabase.GetMainAssetTypeAtPath(item.assetPath)?.Name ?? "Unknown"),
                        guid = item.guid,
                        direct = item.depth == 1,
                        localFileId = item.localFileId,
                        evidenceKind = item.evidenceKind,
                        sourceAssetPath = item.sourceAssetPath,
                        gameObjectPath = item.gameObjectPath,
                        propertyPath = item.propertyPath,
                        referenceScope = item.referenceScope,
                        referenceChain = item.referenceChain,
                        notSearchedReason = item.notSearchedReason,
                        runtimeBoundaryViolation = string.Equals(payload.runtimeBoundary, "ExcludeAssetsEditor", StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(item.assetPath)
                            && item.assetPath.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0,
                    };
                    result.dependencies.Add(dependency);
                }
                result.dependencies = result.dependencies
                    .OrderBy(item => item.assetPath ?? "", StringComparer.Ordinal)
                    .ThenBy(item => item.propertyPath ?? "", StringComparer.Ordinal)
                    .ToList();
                result.count = result.dependencies.Count;
                return result;
            }
            finally
            {
                if (prefabRoot != null)
                {
                    try { UnloadPrefabContents(prefabRoot); }
                    catch (Exception ex)
                    {
                        result.coverageComplete = false;
                        result.truncated = true;
                        result.truncationReason = "prefabUnloadFailed";
                        MarkCleanupFailure(result, payload.assetPath, ex);
                    }
                }
                if (MarkInspectionStateChanged(payload.assetPath, sourceFingerprint, traversal))
                {
                    result.coverageComplete = false;
                    result.truncated = true;
                    result.truncationReason = "assetStateChangedDuringInspection";
                    result.inspectionStateChanged = true;
                    result.inspectionStateChangeAssetPath = traversal.inspectionStateChangeAssetPath;
                }
            }
        }

        // Kept as the narrow test/reflection entry point. The Bridge path supplies the request token below.
        private static AssetDependenciesResultPayload BuildReverseObjectDependencies(AssetDependenciesPayload payload, string bridgeSessionId)
        {
            return BuildReverseObjectDependenciesWithContext(
                payload,
                bridgeSessionId,
                UPilotWindowDiagnostics.DomainReloadEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CancellationToken.None);
        }

        private static AssetDependenciesResultPayload BuildReverseObjectDependenciesWithContext(
            AssetDependenciesPayload payload,
            string bridgeSessionId,
            string domainGeneration,
            CancellationToken cancellationToken)
        {
            var startedAt = System.Diagnostics.Stopwatch.StartNew();
            var signature = BuildDependencyQuerySignature(payload);
            var normalizedScope = NormalizeDependencyScope(payload.scope);
            AssetDependencyCursorState cursor;
            string pageToken;
            lock (DependencyCursorGate)
            {
                if (string.IsNullOrEmpty(payload.continuationToken))
                {
                    // Only a new snapshot may prune unrelated expired cursors. A
                    // continuation must inspect its retained identity first so an
                    // expired token has a precise CURSOR_EXPIRED outcome.
                    PruneDependencyCursors();
                    if (DependencyCursors.Count >= DependencyCursorLimit)
                        throw new AssetDependencyQueryException("REFERENCE_CURSOR_CAPACITY", "At most eight active dependency cursors are allowed.");
                    var candidates = FindDependencyCandidates(normalizedScope, cancellationToken, startedAt, payload.timeBudgetMs, out var discoveryOverrun, out var cancelled);
                    if (cancelled)
                        return CreateReversePartialResult(null, null, candidates.Count, "cancelled", false, true);
                    cursor = new AssetDependencyCursorState
                    {
                        cursorId = Guid.NewGuid().ToString("N"),
                        bridgeSessionId = bridgeSessionId,
                        domainGeneration = domainGeneration,
                        querySignature = signature,
                        candidateFingerprint = BuildCandidateFingerprint(candidates, cancellationToken, startedAt, payload.timeBudgetMs, out var fingerprintOverrun, out cancelled),
                        candidates = candidates,
                        createdAtUtc = DateTime.UtcNow,
                    };
                    if (cancelled)
                        return CreateReversePartialResult(null, null, candidates.Count, "cancelled", false, true);
                    DependencyCursors.Add(cursor.cursorId, cursor);
                    pageToken = cursor.cursorId + ":0";
                    if (discoveryOverrun || fingerprintOverrun)
                    {
                        var partial = CreateReversePartialResult(cursor, pageToken, candidates.Count, "timeBudget", true, false);
                        // No candidate was processed at position zero.  Caching
                        // this partial would turn its own next token into an
                        // endless replay, rather than a retryable checkpoint.
                        return partial;
                    }
                }
                else
                {
                    var parts = payload.continuationToken.Split(':');
                    if (parts.Length != 2 || !DependencyCursors.TryGetValue(parts[0], out cursor))
                        throw new AssetDependencyQueryException("CURSOR_EXPIRED", "Dependency cursor is unavailable.");
                    if (!string.Equals(cursor.bridgeSessionId, bridgeSessionId, StringComparison.Ordinal)
                        || !string.Equals(cursor.domainGeneration, domainGeneration, StringComparison.Ordinal)
                        || cursor.createdAtUtc + DependencyCursorTtl < DateTime.UtcNow)
                    {
                        DependencyCursors.Remove(cursor.cursorId);
                        throw new AssetDependencyQueryException("CURSOR_EXPIRED", "Bridge identity changed or the cursor expired.");
                    }
                    if (!string.Equals(cursor.querySignature, signature, StringComparison.Ordinal))
                        throw new AssetDependencyQueryException("REFERENCE_QUERY_CHANGED", "Continuation parameters differ from the original query.");
                    if (cursor.pages.TryGetValue(payload.continuationToken, out var cached))
                        return cached;
                    var candidates = FindDependencyCandidates(normalizedScope, cancellationToken, startedAt, payload.timeBudgetMs, out var discoveryOverrun, out var cancelled);
                    if (cancelled)
                        return CreateReversePartialResult(cursor, payload.continuationToken, cursor.candidates.Count, "cancelled", false, true);
                    var fingerprint = BuildCandidateFingerprint(candidates, cancellationToken, startedAt, payload.timeBudgetMs, out var fingerprintOverrun, out cancelled);
                    if (cancelled)
                        return CreateReversePartialResult(cursor, payload.continuationToken, cursor.candidates.Count, "cancelled", false, true);
                    if (discoveryOverrun || fingerprintOverrun)
                        return CreateReversePartialResult(cursor, payload.continuationToken, cursor.candidates.Count, "timeBudget", true, false);
                    if (!string.Equals(cursor.candidateFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        DependencyCursors.Remove(cursor.cursorId);
                        throw new AssetDependencyQueryException("REFERENCE_SOURCE_CHANGED", "Candidate assets changed after the first page.");
                    }
                    pageToken = payload.continuationToken;
                    var expectedToken = cursor.cursorId + ":" + cursor.nextIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!string.Equals(pageToken, expectedToken, StringComparison.Ordinal))
                        throw new AssetDependencyQueryException("CURSOR_EXPIRED", "Page token is no longer active.");
                }

                var result = new AssetDependenciesResultPayload
                {
                    evidenceMode = "object",
                    recursive = false,
                    candidateCount = cursor.candidates.Count,
                    continuationToken = pageToken,
                    cursorStatus = "active",
                };
                while (cursor.nextIndex < cursor.candidates.Count)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.partial = true;
                        result.coverageComplete = false;
                        result.truncated = true;
                        result.truncationReason = "cancelled";
                        break;
                    }
                    if (result.visitedNodes >= payload.maxNodes)
                    {
                        result.coverageComplete = false;
                        result.truncated = true;
                        result.truncationReason = "nodeBudget";
                        break;
                    }
                    if (startedAt.ElapsedMilliseconds >= payload.timeBudgetMs)
                    {
                        result.coverageComplete = false;
                        result.truncated = true;
                        result.truncationReason = "timeBudget";
                        break;
                    }

                    var candidatePath = cursor.candidates[cursor.nextIndex++];
                    result.visitedNodes++;
                    var remainingTimeMs = Math.Max(1, payload.timeBudgetMs - (int)startedAt.ElapsedMilliseconds);
                    AppendReverseCandidateMatches(candidatePath, payload, result, remainingTimeMs, cancellationToken);
                    if (startedAt.ElapsedMilliseconds >= payload.timeBudgetMs)
                    {
                        result.budgetOverrun = true;
                        result.coverageComplete = false;
                        result.truncated = true;
                        result.truncationReason = "timeBudget";
                        break;
                    }
                }

                if (cursor.nextIndex < cursor.candidates.Count)
                    result.nextContinuationToken = cursor.cursorId + ":" + cursor.nextIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                else
                {
                    result.cursorStatus = "completed";
                    DependencyCursors.Remove(cursor.cursorId);
                }
                result.nextPosition = cursor.nextIndex;
                result.partial = result.truncated || string.Equals(result.truncationReason, "cancelled", StringComparison.Ordinal);
                result.notSearchedReason = DependencyNotSearchedReason(result.truncationReason);
                result.dependencies = result.dependencies
                    .OrderBy(item => item.assetPath ?? "", StringComparer.Ordinal)
                    .ThenBy(item => item.propertyPath ?? "", StringComparer.Ordinal)
                    .ToList();
                result.count = result.dependencies.Count;
                if (!string.Equals(result.cursorStatus, "completed", StringComparison.Ordinal))
                    cursor.pages[pageToken] = result;
                return result;
            }
        }

        private static void AppendReverseCandidateMatches(string candidatePath, AssetDependenciesPayload payload, AssetDependenciesResultPayload result, int remainingTimeMs, CancellationToken cancellationToken)
        {
            var query = payload.referenceQuery;
            if (query == null)
                return;
            if (string.Equals(query.kind, "stringLiteral", StringComparison.Ordinal))
            {
                AppendLiteralMatches(candidatePath, query, result, cancellationToken);
                return;
            }
            var forward = BuildObjectAssetDependenciesWithCancellation(new AssetDependenciesPayload
            {
                assetPath = candidatePath,
                recursive = false,
                evidenceMode = "object",
                runtimeBoundary = payload.runtimeBoundary,
                // Candidate discovery and the candidate's object walk consume the same page budget.
                // A Unity API call may return after the remaining allowance; report that overrun below.
                maxNodes = Math.Max(1, payload.maxNodes - result.visitedNodes),
                timeBudgetMs = remainingTimeMs,
            }, cancellationToken);
            result.visitedNodes += forward.visitedNodes;
            result.budgetOverrun |= forward.budgetOverrun || result.visitedNodes > payload.maxNodes;
            if (!forward.cleanupVerified)
            {
                result.cleanupVerified = false;
                result.cleanupError = forward.cleanupError;
                result.sideEffectsMayHaveOccurred |= forward.sideEffectsMayHaveOccurred;
                result.unresolvedResources.AddRange(forward.unresolvedResources);
            }
            if (forward.partial)
            {
                result.coverageComplete = false;
                result.truncated = true;
                result.partial = true;
                result.truncationReason = forward.truncationReason;
            }
            foreach (var item in forward.dependencies)
            {
                bool matches = string.Equals(query.kind, "guid", StringComparison.Ordinal)
                    ? string.Equals(item.guid, query.value, StringComparison.Ordinal)
                    : string.Equals(item.guid, query.guid, StringComparison.Ordinal)
                        && string.Equals(item.localFileId, query.localFileId, StringComparison.Ordinal);
                if (matches)
                    result.dependencies.Add(item);
            }
        }

        private static void AppendLiteralMatches(string assetPath, AssetReferenceQueryPayload query, AssetDependenciesResultPayload result, CancellationToken cancellationToken = default(CancellationToken))
        {
            var propertyPaths = query.propertyPaths ?? new List<string>();
            if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(assetPath);
                    if (root != null)
                        foreach (var component in root.GetComponentsInChildren<Component>(true))
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                MarkReverseCancelled(result);
                                break;
                            }
                            if (component != null) AppendLiteralMatchesForObject(component, assetPath, query.value, propertyPaths, result, cancellationToken);
                            if (result.partial)
                                break;
                        }
                }
                catch (Exception ex)
                {
                    result.coverageComplete = false;
                    result.truncated = true;
                    result.truncationReason = "serializedObjectUnavailable";
                    Debug.LogWarning("[UPilot] Literal dependency query could not inspect " + assetPath + ": " + ex.Message);
                }
                finally
                {
                    if (root != null)
                    {
                        try { PrefabUtility.UnloadPrefabContents(root); }
                        catch (Exception ex)
                        {
                            MarkCleanupFailure(result, assetPath, ex);
                        }
                    }
                }
            }
            else
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset != null)
                    AppendLiteralMatchesForObject(asset, assetPath, query.value, propertyPaths, result, cancellationToken);
            }
        }

        private static void AppendLiteralMatchesForObject(
            UnityEngine.Object source,
            string assetPath,
            string expectedValue,
            List<string> propertyPaths,
            AssetDependenciesResultPayload result,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var serialized = new SerializedObject(source);
            // Literal matching is deliberately limited to the caller's declared SerializedProperty paths.
            // FindProperty also handles direct fields consistently for Prefab contents across Unity versions;
            // iterating only visible children can omit a direct field after a hidden script property.
            foreach (var propertyPath in propertyPaths.Distinct(StringComparer.Ordinal))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    MarkReverseCancelled(result);
                    return;
                }
                var property = serialized.FindProperty(propertyPath);
                if (property == null
                    || property.propertyType != SerializedPropertyType.String
                    || !string.Equals(property.stringValue, expectedValue, StringComparison.Ordinal))
                    continue;
                result.dependencies.Add(new AssetDependencyInfoPayload
                {
                    assetPath = assetPath,
                    assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath)?.Name ?? "Unknown",
                    guid = AssetDatabase.AssetPathToGUID(assetPath),
                    direct = true,
                    evidenceKind = "literalMatch",
                    sourceAssetPath = assetPath,
                    propertyPath = property.propertyPath,
                    referenceScope = "literal",
                });
            }
        }

        private static AssetDependenciesResultPayload CreateReversePartialResult(
            AssetDependencyCursorState cursor,
            string pageToken,
            int candidateCount,
            string reason,
            bool budgetOverrun,
            bool cancelled)
        {
            var result = new AssetDependenciesResultPayload
            {
                evidenceMode = "object",
                recursive = false,
                candidateCount = candidateCount,
                continuationToken = pageToken,
                cursorStatus = cursor == null ? "cancelled" : "active",
                coverageComplete = false,
                truncated = true,
                partial = true,
                truncationReason = reason,
                notSearchedReason = DependencyNotSearchedReason(reason),
                budgetOverrun = budgetOverrun,
                sideEffectsMayHaveOccurred = false,
                nextPosition = cursor?.nextIndex ?? 0,
            };
            if (cursor != null)
                result.nextContinuationToken = cursor.cursorId + ":" + cursor.nextIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return result;
        }

        private static void MarkReverseCancelled(AssetDependenciesResultPayload result)
        {
            result.coverageComplete = false;
            result.truncated = true;
            result.partial = true;
            result.truncationReason = "cancelled";
        }

        private static string DependencyNotSearchedReason(string truncationReason)
        {
            if (string.Equals(truncationReason, "nodeBudget", StringComparison.Ordinal)
                || string.Equals(truncationReason, "timeBudget", StringComparison.Ordinal))
                return "budget";
            return truncationReason;
        }

        private static void MarkTraversalCancelled(PrefabQueryComponentsResultPayload result, int nextPosition)
        {
            result.coverageComplete = false;
            result.truncated = true;
            result.partial = true;
            result.truncationReason = "cancelled";
            result.nextReferencePosition = nextPosition;
            AddPrefabNotSearchedReason(result, "cancelled");
        }

        private static void MarkCleanupFailure(PrefabQueryComponentsResultPayload result, string prefabPath, Exception exception)
        {
            result.coverageComplete = false;
            result.truncated = true;
            result.partial = true;
            result.truncationReason = "prefabUnloadFailed";
            result.cleanupVerified = false;
            result.cleanupError = exception.Message;
            result.changedEditorState = true;
            result.sideEffectsMayHaveOccurred = true;
            result.unresolvedResources.Add("prefabContents:" + prefabPath);
            AddPrefabNotSearchedReason(result, "prefabUnloadFailed");
        }

        private static void AddPrefabNotSearchedReason(PrefabQueryComponentsResultPayload result, string reason)
        {
            if (result == null || string.IsNullOrEmpty(reason))
                return;
            if (!result.notSearchedReasons.Contains(reason))
                result.notSearchedReasons.Add(reason);
        }

        private static void MarkCleanupFailure(AssetDependenciesResultPayload result, string prefabPath, Exception exception)
        {
            result.coverageComplete = false;
            result.truncated = true;
            result.partial = true;
            result.truncationReason = "prefabUnloadFailed";
            result.cleanupVerified = false;
            result.cleanupError = exception.Message;
            result.sideEffectsMayHaveOccurred = true;
            result.unresolvedResources.Add("prefabContents:" + prefabPath);
        }

        private static List<string> FindDependencyCandidates(
            List<string> scope,
            CancellationToken cancellationToken,
            System.Diagnostics.Stopwatch startedAt,
            int timeBudgetMs,
            out bool budgetOverrun,
            out bool cancelled)
        {
            budgetOverrun = false;
            cancelled = false;
            var candidates = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, scope.ToArray()))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path))
                    candidates.Add(path);
                if (startedAt.ElapsedMilliseconds >= timeBudgetMs)
                    budgetOverrun = true;
            }
            candidates.Sort(StringComparer.Ordinal);
            return candidates;
        }

        private static string BuildCandidateFingerprint(
            List<string> candidates,
            CancellationToken cancellationToken,
            System.Diagnostics.Stopwatch startedAt,
            int timeBudgetMs,
            out bool budgetOverrun,
            out bool cancelled)
        {
            budgetOverrun = false;
            cancelled = false;
            var entries = new List<string>(candidates.Count);
            foreach (var path in candidates)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                entries.Add(path + "|" + AssetDatabase.AssetPathToGUID(path) + "|" + AssetDatabase.GetAssetDependencyHash(path));
                if (startedAt.ElapsedMilliseconds >= timeBudgetMs)
                    budgetOverrun = true;
            }
            return string.Join("\n", entries);
        }

        private static string GetAssetDependencyFingerprint(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return string.Empty;
            try { return AssetDatabase.GetAssetDependencyHash(assetPath).ToString(); }
            catch { return string.Empty; }
        }

        private static bool MarkInspectionStateChanged(
            string assetPath,
            string beforeFingerprint,
            PrefabQueryComponentsResultPayload result)
        {
            if (string.IsNullOrEmpty(beforeFingerprint)
                || string.Equals(beforeFingerprint, GetAssetDependencyFingerprint(assetPath), StringComparison.Ordinal))
                return false;
            result.coverageComplete = false;
            result.truncated = true;
            result.truncationReason = "assetStateChangedDuringInspection";
            result.inspectionStateChanged = true;
            result.inspectionStateChangeAssetPath = assetPath;
            AddPrefabNotSearchedReason(result, "assetStateChangedDuringInspection");
            return true;
        }

        private static string BuildDependencyQuerySignature(AssetDependenciesPayload payload)
        {
            var query = payload.referenceQuery;
            var propertyPaths = (query?.propertyPaths ?? new List<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal);
            return string.Join("|", new[]
            {
                payload.evidenceMode ?? string.Empty,
                payload.runtimeBoundary ?? string.Empty,
                payload.direction ?? string.Empty,
                query?.kind ?? string.Empty,
                query?.value ?? string.Empty,
                query?.guid ?? string.Empty,
                query?.localFileId ?? string.Empty,
                EncodeDependencySignatureList(propertyPaths),
                EncodeDependencySignatureList(NormalizeDependencyScope(payload.scope)),
                // Limits control a page attempt, not the semantic source/query
                // snapshot.  A caller may raise a budget to resume a genuine
                // native-call overrun without mixing candidates or query terms.
            }.Select(value => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))));
        }

        private static List<string> NormalizeDependencyScope(IEnumerable<string> scope)
        {
            return (scope ?? Enumerable.Empty<string>())
                .Select(path => path == "Assets/" ? path : (path ?? string.Empty).TrimEnd('/'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private static string EncodeDependencySignatureList(IEnumerable<string> values)
        {
            return string.Join(".", (values ?? Enumerable.Empty<string>())
                .Select(value => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty))));
        }

        private static void PruneDependencyCursors()
        {
            var expired = DependencyCursors.Values
                .Where(item => item.createdAtUtc + DependencyCursorTtl < DateTime.UtcNow)
                .Select(item => item.cursorId)
                .ToList();
            foreach (var cursorId in expired)
                DependencyCursors.Remove(cursorId);
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

        internal static AssetMutationResultPayload DeleteAsset(string assetPath, Func<string, bool> delete = null)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                || assetPath.Contains("..") || assetPath.Contains("\\"))
                throw new ArgumentException("Delete requires an exact project-relative Assets/ path.", nameof(assetPath));
            EnsureAssetExists(assetPath, "Asset");
            var sourceGuid = AssetDatabase.AssetPathToGUID(assetPath);
            try
            {
                if (!(delete ?? AssetDatabase.DeleteAsset)(assetPath))
                    throw new InvalidOperationException("AssetDatabase.DeleteAsset returned false.");
                if (File.Exists(assetPath) || Directory.Exists(assetPath) || File.Exists(assetPath + ".meta")
                    || AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                    throw new InvalidOperationException("Deletion postconditions could not be verified.");
                return new AssetMutationResultPayload
                {
                    operation = "asset.delete", sourcePath = assetPath, sourceGuid = sourceGuid,
                    ok = true, verified = true, deleted = true, saved = true,
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Delete was attempted for {assetPath} (GUID {sourceGuid}); its effects are unverified. "
                    + "Inspect actual asset and meta state; do not retry automatically. " + ex.Message, ex);
            }
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
            Transform prefabRoot,
            string prefabPath,
            string requestedComponentType,
            Type requestedType,
            bool includeSerializedFields,
            int maxDepth,
            int maxResults,
            bool followObjectReferences,
            List<PrefabReferenceWorkItem> referenceSeeds,
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

                if (includeSerializedFields || followObjectReferences)
                    AddSerializedFields(
                        component,
                        maxDepth,
                        match.serializedFields,
                        prefabRoot,
                        prefabPath,
                        includeSerializedFields,
                        followObjectReferences,
                        (reference, field) =>
                        {
                            if (reference != null && !IsReferenceWithinLoadedPrefab(field.referenceScope))
                                referenceSeeds.Add(new PrefabReferenceWorkItem(reference, prefabPath, path, field.propertyPath, 1, new List<string> { prefabPath + ":" + field.propertyPath }));
                        });

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
                    prefabRoot,
                    prefabPath,
                    requestedComponentType,
                    requestedType,
                    includeSerializedFields,
                    maxDepth,
                    maxResults,
                    followObjectReferences,
                    referenceSeeds,
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

        internal static void AddSerializedFields(
            Component component,
            int maxDepth,
            List<SerializedPropertyInfo> fields,
            Transform prefabRoot,
            string prefabPath,
            bool includeFields,
            bool traversalRequested,
            Action<UnityEngine.Object, SerializedPropertyInfo> onObjectReference)
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
                var field = new SerializedPropertyInfo
                {
                    propertyPath = prop.propertyPath,
                    type = prop.propertyType.ToString(),
                    value = GetSerializedPropertyDisplayValue(prop),
                    depth = prop.depth,
                    hasChildren = prop.hasChildren,
                    isArray = prop.isArray,
                    arraySize = prop.isArray ? prop.arraySize : 0,
                };
                if (prop.propertyType == SerializedPropertyType.ObjectReference)
                {
                    DescribePrefabObjectReference(prop.objectReferenceValue, component, prefabRoot, prefabPath, field, traversalRequested);
                    onObjectReference?.Invoke(prop.objectReferenceValue, field);
                }
                if (includeFields)
                    fields.Add(field);
            }
        }

        private static void DescribePrefabObjectReference(
            UnityEngine.Object reference,
            Component owner,
            Transform prefabRoot,
            string prefabPath,
            SerializedPropertyInfo field,
            bool traversalRequested)
        {
            field.evidenceKind = "objectReference";
            if (reference == null)
            {
                field.referenceScope = "unknown";
                field.notSearchedReason = "nullReference";
                return;
            }

            var identityObject = GetReferenceAssetIdentityObject(reference);
            field.objectReferencePath = AssetDatabase.GetAssetPath(identityObject);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(identityObject, out field.objectReferenceGuid, out long localFileId);
            field.objectReferenceLocalFileId = localFileId == 0 ? null : localFileId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var target = GetReferenceGameObject(reference);
            // A nested Prefab instance is physically below the isolated root,
            // but it remains a separate source boundary.  Classify it before
            // the generic hierarchy check so default queries can say that its
            // contents were not expanded, and opt-in traversal can enqueue it.
            var nestedSourcePath = target == null ? string.Empty : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
            if (!string.IsNullOrEmpty(nestedSourcePath)
                && !string.Equals(nestedSourcePath, prefabPath, StringComparison.OrdinalIgnoreCase)
                && PrefabUtility.IsPartOfPrefabInstance(target))
            {
                field.referenceScope = "nestedSource";
                field.notSearchedReason = "nestedPrefabContentsNotExpanded";
                return;
            }

            if (target != null && prefabRoot != null && target.transform.IsChildOf(prefabRoot))
            {
                if (ReferenceTargetsOwner(reference, owner))
                    field.referenceScope = "sameObject";
                else if (target == prefabRoot.gameObject)
                    field.referenceScope = "root";
                else
                    field.referenceScope = "child";
                field.notSearchedReason = "withinLoadedPrefab";
                return;
            }

            if (!string.IsNullOrEmpty(field.objectReferencePath))
            {
                field.referenceScope = "externalAsset";
                field.notSearchedReason = traversalRequested ? null : "objectReferenceTraversalNotRequested";
                return;
            }

            field.referenceScope = "unknown";
            field.notSearchedReason = "stableAssetIdentityUnavailable";
        }

        private static GameObject GetReferenceGameObject(UnityEngine.Object reference)
        {
            if (reference is GameObject gameObject)
                return gameObject;
            if (reference is Component component)
                return component.gameObject;
            return null;
        }

        // Objects from a nested Prefab instance have no direct AssetDatabase identity
        // while their containing Prefab is loaded in isolation. Resolve their source
        // object so both field explanations and traversal use the stable source asset.
        private static UnityEngine.Object GetReferenceAssetIdentityObject(UnityEngine.Object reference)
        {
            if (reference == null || !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(reference)))
                return reference;

            var target = GetReferenceGameObject(reference);
            if (target == null || !PrefabUtility.IsPartOfPrefabInstance(target))
                return reference;

            return PrefabUtility.GetCorrespondingObjectFromSource(target) ?? reference;
        }

        private static bool ReferenceTargetsOwner(UnityEngine.Object reference, Component owner)
        {
            if (owner == null)
                return false;
            if (ReferenceEquals(reference, owner) || ReferenceEquals(reference, owner.gameObject))
                return true;
            return reference is Component component && component.gameObject == owner.gameObject;
        }

        private static bool IsReferenceWithinLoadedPrefab(string referenceScope)
        {
            return string.Equals(referenceScope, "sameObject", StringComparison.Ordinal)
                || string.Equals(referenceScope, "root", StringComparison.Ordinal)
                || string.Equals(referenceScope, "child", StringComparison.Ordinal);
        }

        private sealed class PrefabReferenceWorkItem
        {
            public readonly UnityEngine.Object reference;
            public readonly string sourceAssetPath;
            public readonly string gameObjectPath;
            public readonly string propertyPath;
            public readonly int depth;
            public readonly List<string> chain;

            public PrefabReferenceWorkItem(
                UnityEngine.Object reference,
                string sourceAssetPath,
                string gameObjectPath,
                string propertyPath,
                int depth,
                List<string> chain)
            {
                this.reference = reference;
                this.sourceAssetPath = sourceAssetPath;
                this.gameObjectPath = gameObjectPath;
                this.propertyPath = propertyPath;
                this.depth = depth;
                this.chain = chain;
            }
        }

        private static void TraversePrefabObjectReferences(
            List<PrefabReferenceWorkItem> seeds,
            string rootPrefabPath,
            bool includeNestedPrefabContents,
            int referenceDepth,
            int maxReferenceNodes,
            int timeBudgetMs,
            PrefabQueryComponentsResultPayload result,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var pending = new Queue<PrefabReferenceWorkItem>(seeds);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var startedAt = System.Diagnostics.Stopwatch.StartNew();
            while (pending.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    MarkTraversalCancelled(result, result.visitedReferenceNodes);
                    return;
                }
                if (result.visitedReferenceNodes >= maxReferenceNodes)
                {
                    result.coverageComplete = false;
                    result.truncated = true;
                    result.partial = true;
                    result.nextReferencePosition = result.visitedReferenceNodes;
                    result.truncationReason = "nodeBudget";
                    AddPrefabNotSearchedReason(result, "budget");
                    return;
                }
                if (startedAt.ElapsedMilliseconds >= timeBudgetMs)
                {
                    result.coverageComplete = false;
                    result.truncated = true;
                    result.partial = true;
                    result.nextReferencePosition = result.visitedReferenceNodes;
                    result.truncationReason = "timeBudget";
                    AddPrefabNotSearchedReason(result, "budget");
                    return;
                }

                var item = pending.Dequeue();
                var identity = ReferenceIdentity(item.reference);
                if (!visited.Add(identity))
                    continue;

                var evidence = BuildPrefabReferenceEvidence(item, rootPrefabPath);
                result.objectReferences.Add(evidence);
                result.visitedReferenceNodes++;
                if (item.depth >= referenceDepth)
                {
                    evidence.notSearchedReason = "referenceDepth";
                    continue;
                }

                var targetPath = AssetDatabase.GetAssetPath(GetReferenceAssetIdentityObject(item.reference));
                if (string.IsNullOrEmpty(targetPath))
                {
                    evidence.notSearchedReason = "stableAssetIdentityUnavailable";
                    result.coverageComplete = false;
                    AddPrefabNotSearchedReason(result, "stableAssetIdentityUnavailable");
                    continue;
                }

                if (targetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    if (!includeNestedPrefabContents && !string.Equals(targetPath, rootPrefabPath, StringComparison.OrdinalIgnoreCase))
                    {
                        evidence.notSearchedReason = "nestedPrefabContentsNotExpanded";
                        result.coverageComplete = false;
                        AddPrefabNotSearchedReason(result, "nestedPrefabContentsNotExpanded");
                        continue;
                    }
                    AppendPrefabContentReferences(targetPath, item, pending, result, cancellationToken);
                }
                else
                {
                    var sourceFingerprint = GetAssetDependencyFingerprint(targetPath);
                    AppendSerializedObjectReferences(item.reference, targetPath, item, pending, result, null, cancellationToken);
                    if (MarkInspectionStateChanged(targetPath, sourceFingerprint, result))
                        return;
                }
                if (result.inspectionStateChanged)
                    return;
                if (startedAt.ElapsedMilliseconds >= timeBudgetMs)
                {
                    result.coverageComplete = false;
                    result.truncated = true;
                    result.partial = true;
                    result.nextReferencePosition = result.visitedReferenceNodes;
                    result.truncationReason = "timeBudget";
                    result.budgetOverrun = true;
                    AddPrefabNotSearchedReason(result, "budget");
                    return;
                }
            }
        }

        private static PrefabObjectReferencePayload BuildPrefabReferenceEvidence(PrefabReferenceWorkItem item, string rootPrefabPath)
        {
            var identityObject = GetReferenceAssetIdentityObject(item.reference);
            var targetPath = AssetDatabase.GetAssetPath(identityObject);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(identityObject, out string guid, out long localFileId);
            var evidence = new PrefabObjectReferencePayload
            {
                assetPath = targetPath,
                guid = guid,
                localFileId = localFileId == 0 ? null : localFileId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                sourceAssetPath = item.sourceAssetPath,
                gameObjectPath = item.gameObjectPath,
                propertyPath = item.propertyPath,
                referenceScope = string.Equals(targetPath, rootPrefabPath, StringComparison.OrdinalIgnoreCase) ? "root" : (string.IsNullOrEmpty(targetPath) ? "unknown" : "externalAsset"),
                depth = item.depth,
                referenceChain = new List<string>(item.chain),
            };
            if (string.IsNullOrEmpty(guid) || localFileId == 0)
                evidence.notSearchedReason = "stableAssetIdentityUnavailable";
            return evidence;
        }

        private static string ReferenceIdentity(UnityEngine.Object reference)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(GetReferenceAssetIdentityObject(reference), out string guid, out long localFileId);
            if (!string.IsNullOrEmpty(guid) && localFileId != 0)
                return guid + ":" + localFileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return "instance:" + UPilotEntityIds.ToWireId(reference);
        }

        private static void AppendPrefabContentReferences(
            string prefabPath,
            PrefabReferenceWorkItem parent,
            Queue<PrefabReferenceWorkItem> pending,
            PrefabQueryComponentsResultPayload result,
            CancellationToken cancellationToken)
        {
            GameObject root = null;
            var sourceFingerprint = GetAssetDependencyFingerprint(prefabPath);
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                if (root == null)
                    throw new InvalidOperationException("Failed to load Prefab contents: " + prefabPath);
                if (MarkPrefabContentsDirty(root, result, prefabPath))
                    return;
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        MarkTraversalCancelled(result, result.visitedReferenceNodes);
                        break;
                    }
                    if (component != null)
                        AppendSerializedObjectReferences(component, prefabPath, parent, pending, result, root.transform, cancellationToken);
                    if (result.partial)
                        break;
                }
            }
            catch (Exception ex)
            {
                result.coverageComplete = false;
                result.truncated = true;
                result.truncationReason = "prefabLoadFailed";
                AddPrefabNotSearchedReason(result, "prefabLoadFailed");
                Debug.LogWarning("[UPilot] Object reference traversal could not load " + prefabPath + ": " + ex.Message);
            }
            finally
            {
                if (root != null)
                {
                    try { UnloadPrefabContents(root); }
                    catch (Exception ex)
                    {
                        MarkCleanupFailure(result, prefabPath, ex);
                        Debug.LogWarning("[UPilot] Object reference traversal could not unload " + prefabPath + ": " + ex.Message);
                    }
                }
                MarkInspectionStateChanged(prefabPath, sourceFingerprint, result);
            }
        }

        private static void AppendSerializedObjectReferences(
            UnityEngine.Object source,
            string sourceAssetPath,
            PrefabReferenceWorkItem parent,
            Queue<PrefabReferenceWorkItem> pending,
            PrefabQueryComponentsResultPayload result,
            Transform loadedPrefabRoot = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var serialized = new SerializedObject(source);
                var iterator = serialized.GetIterator();
                bool enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        MarkTraversalCancelled(result, result.visitedReferenceNodes);
                        return;
                    }
                    enterChildren = false;
                    if (iterator.name == "m_Script" || iterator.propertyType != SerializedPropertyType.ObjectReference)
                        continue;
                    var reference = iterator.objectReferenceValue;
                    if (reference == null)
                        continue;
                    var target = GetReferenceGameObject(reference);
                    if (target != null && loadedPrefabRoot != null && target.transform.IsChildOf(loadedPrefabRoot))
                        continue;
                    var propertyPath = iterator.propertyPath;
                    var chain = new List<string>(parent.chain) { sourceAssetPath + ":" + propertyPath };
                    pending.Enqueue(new PrefabReferenceWorkItem(reference, sourceAssetPath, parent.gameObjectPath, propertyPath, parent.depth + 1, chain));
                }
            }
            catch (Exception ex)
            {
                result.coverageComplete = false;
                result.truncated = true;
                result.truncationReason = "serializedObjectUnavailable";
                AddPrefabNotSearchedReason(result, "serializedObjectUnavailable");
                Debug.LogWarning("[UPilot] Object reference traversal could not inspect " + sourceAssetPath + ": " + ex.Message);
            }
        }

        private static bool MarkPrefabContentsDirty(
            GameObject root,
            PrefabQueryComponentsResultPayload result,
            string prefabPath)
        {
            if (root == null || result == null)
                return false;
            // Component-level IsDirty is not a source-change signal for isolated
            // Prefab contents: Unity marks ordinary load-created objects dirty on
            // some supported versions. A dirty isolated scene is meaningful, and
            // the opt-in seam covers deterministic callback-boundary testing.
            var dirty = (root.scene.IsValid() && root.scene.isDirty)
                        || (PrefabContentsChangedStateForTests?.Invoke(root) ?? false);
            if (!dirty)
                return false;
            result.coverageComplete = false;
            result.truncated = true;
            result.partial = true;
            result.truncationReason = "prefabLoadCallbackChangedState";
            result.inspectionStateChanged = true;
            result.inspectionStateChangeAssetPath = prefabPath;
            result.changedEditorState = true;
            result.sideEffectsMayHaveOccurred = true;
            AddPrefabNotSearchedReason(result, "prefabLoadCallbackChangedState");
            return true;
        }

        private static void UnloadPrefabContents(GameObject root)
        {
            var fault = PrefabContentsUnloadFaultForTests?.Invoke(root);
            if (fault != null)
                throw fault;
            PrefabUtility.UnloadPrefabContents(root);
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
