using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable] public sealed class PrefabPatchMessage { public PrefabPatchPayload payload; }
    [Serializable]
    public sealed class PrefabPatchPayload
    {
        public string assetPath;
        public string hierarchyPath = ".";
        public string componentType;
        public List<SerializedPropertyWrite> properties = new();
        public bool dryRun = true;
        public string confirmToken = "";
    }

    [Serializable]
    public sealed class PrefabPatchResultPayload
    {
        public bool dryRun;
        public bool applied;
        public bool persistenceVerified;
        public string assetPath;
        public string hierarchyPath;
        public string componentType;
        public string confirmToken;
        public string sha256Before;
        public string sha256After;
        public string metaSha256Before;
        public string metaSha256After;
        public int modifiedCount;
        public List<SerializedPropertyChangePayload> changes = new();
        public string backupPath;
        public string recoveryStatus = "not_needed";
        public string error = "";
    }

    public static class UPilotPrefabPatchService
    {
        private static readonly HashSet<string> ActiveAssets = new(StringComparer.OrdinalIgnoreCase);

        public static PrefabPatchResultPayload Patch(PrefabPatchPayload request)
        {
            if (!request.dryRun && UPilotProjectConfig.Load().safety?.writeAccessApproved != true)
                throw new InvalidOperationException("Project write access is not approved.");
            if (!request.dryRun && (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling))
                throw new InvalidOperationException("Prefab commits require ready EditMode.");
            string path = request.assetPath ?? "";
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                || path.Contains("\\") || path.Split('/').Any(part => part == ".." || part == "." || part == ""))
                throw new InvalidOperationException("assetPath must be a canonical Assets/.../*.prefab path.");
            string absolute = Path.GetFullPath(path);
            string assetsRoot = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prefab path is outside Assets.");
            for (var parent = new FileInfo(absolute).Directory; parent != null && parent.FullName.Length >= assetsRoot.Length - 1; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Prefab paths through reparse points are unsupported.");
            if (!File.Exists(absolute) || !File.Exists(absolute + ".meta"))
                throw new InvalidOperationException("Prefab and meta must both exist.");
            if (((File.GetAttributes(absolute) | File.GetAttributes(absolute + ".meta")) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Prefab reparse points are unsupported.");
            if (PrefabStageUtility.GetCurrentPrefabStage()?.assetPath == path)
                throw new InvalidOperationException("Close the target Prefab Mode before patching; no stage is closed automatically.");
            if (PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(path)) != PrefabAssetType.Regular)
                throw new InvalidOperationException("Only ordinary prefabs are supported; model and variant prefabs are excluded.");
            if (request.properties == null || request.properties.Count == 0 || request.properties.Count > 128)
                throw new InvalidOperationException("Provide 1 to 128 property writes.");
            var receipt = new PrefabPatchResultPayload
            {
                assetPath = path, hierarchyPath = request.hierarchyPath, componentType = request.componentType, dryRun = request.dryRun,
                sha256Before = Hash(File.ReadAllBytes(absolute)), metaSha256Before = Hash(File.ReadAllBytes(absolute + ".meta")),
            };
            if (!ActiveAssets.Add(absolute))
                throw new InvalidOperationException("A patch for this asset is already committing.");
            GameObject root = null;
            string candidate = "Assets/__UPilotPrefabPatch_" + Guid.NewGuid().ToString("N") + ".prefab";
            string committedHash = "";
            bool commitStarted = false;
            try
            {
                root = PrefabUtility.LoadPrefabContents(path);
                if (root.GetComponentsInChildren<Transform>(true).Any(item => item.gameObject != root && PrefabUtility.IsAnyPrefabInstanceRoot(item.gameObject)))
                    throw new InvalidOperationException("Nested prefab instances are not supported by this patch tool.");
                var component = ResolveComponent(root, request);
                var serialized = new SerializedObject(component);
                foreach (var write in request.properties)
                {
                    if (write == null || string.IsNullOrWhiteSpace(write.propertyPath))
                        throw new InvalidOperationException("Every property needs a propertyPath.");
                    var property = serialized.FindProperty(write.propertyPath);
                    if (property == null) throw new InvalidOperationException("Property not found: " + write.propertyPath);
                    if (property.propertyPath.StartsWith("m_", StringComparison.Ordinal)
                        && property.propertyPath != "m_Enabled")
                        throw new InvalidOperationException("Structural/native fields are excluded: " + write.propertyPath);
                    if (property.propertyType == SerializedPropertyType.ArraySize || (property.isArray && property.propertyType != SerializedPropertyType.String))
                        throw new InvalidOperationException("Array structure changes are excluded: " + write.propertyPath);
                    if (property.propertyType == SerializedPropertyType.ObjectReference)
                        throw new InvalidOperationException("Object reference writes are excluded in prefab patch v1: " + write.propertyPath);
                    if (property.propertyType == SerializedPropertyType.Enum && Array.IndexOf(property.enumNames, write.value) < 0)
                        throw new InvalidOperationException("Use an exact enum name, never a numeric index: " + write.propertyPath);
                }
                var preview = UPilotSerializedPropertyUtility.Preview(serialized, request.properties);
                receipt.modifiedCount = preview.modifiedCount;
                receipt.changes = preview.changes;
                receipt.confirmToken = Hash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(new PrefabPatchPayload
                {
                    assetPath = path, hierarchyPath = request.hierarchyPath, componentType = request.componentType,
                    properties = request.properties, confirmToken = receipt.sha256Before + ":" + receipt.metaSha256Before,
                })));
                if (request.dryRun) return receipt;
                if (!string.Equals(request.confirmToken, receipt.confirmToken, StringComparison.Ordinal))
                    throw new InvalidOperationException("confirmToken does not match the current asset/meta, exact target and property writes.");
                if (preview.modifiedCount == 0)
                {
                    receipt.persistenceVerified = true;
                    return receipt;
                }

                UPilotSerializedPropertyUtility.Apply(serialized, component, request.properties, "UPilot Prefab Patch");
                PrefabUtility.SaveAsPrefabAsset(root, candidate, out bool saved);
                if (!saved) throw new IOException("Could not serialize the candidate prefab.");
                byte[] candidateBytes = File.ReadAllBytes(candidate);
                committedHash = Hash(candidateBytes);
                PrefabUtility.UnloadPrefabContents(root);
                root = null;

                receipt.backupPath = "Library/UPilot/PrefabPatches/" + Guid.NewGuid().ToString("N") + "/before.prefab";
                Directory.CreateDirectory(Path.GetDirectoryName(receipt.backupPath));
                // Block cooperative filesystem writers through the final hash check and write.
                // This is a single-file guarded commit, not a transaction over user callbacks.
                using (var meta = new FileStream(absolute + ".meta", FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var file = new FileStream(absolute, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    if (Hash(file) != receipt.sha256Before || Hash(meta) != receipt.metaSha256Before)
                        throw new IOException("Prefab or meta changed after preview; no commit was performed.");
                    file.Position = 0;
                    using (var backup = File.Create(receipt.backupPath)) file.CopyTo(backup);
                    meta.Position = 0;
                    using (var backupMeta = File.Create(receipt.backupPath + ".meta")) meta.CopyTo(backupMeta);
                    file.Position = 0;
                    commitStarted = true;
                    file.Write(candidateBytes, 0, candidateBytes.Length);
                    file.SetLength(candidateBytes.Length);
                    file.Flush(true);
                }
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                root = PrefabUtility.LoadPrefabContents(path);
                var reloaded = new SerializedObject(ResolveComponent(root, request));
                foreach (var change in receipt.changes)
                {
                    var property = reloaded.FindProperty(change.propertyPath);
                    if (property == null || UPilotSerializedPropertyUtility.GetDisplayValue(property) != change.newValue)
                        throw new InvalidOperationException("Persisted verification failed: " + change.propertyPath);
                }
                if (Hash(File.ReadAllBytes(absolute)) != committedHash || Hash(File.ReadAllBytes(absolute + ".meta")) != receipt.metaSha256Before)
                    throw new IOException("Prefab changed during import or verification.");
                receipt.applied = true;
                receipt.persistenceVerified = true;
            }
            catch (Exception ex)
            {
                receipt.error = ex.Message;
                if (root != null) { PrefabUtility.UnloadPrefabContents(root); root = null; }
                if (commitStarted)
                {
                    receipt.recoveryStatus = "recovery_required";
                    try
                    {
                        using (var meta = new FileStream(absolute + ".meta", FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var file = new FileStream(absolute, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        {
                            if (Hash(file) == committedHash && Hash(meta) == receipt.metaSha256Before)
                            {
                                byte[] backup = File.ReadAllBytes(receipt.backupPath);
                                file.Position = 0;
                                file.Write(backup, 0, backup.Length);
                                file.SetLength(backup.Length);
                                file.Flush(true);
                                receipt.recoveryStatus = "restored";
                            }
                            else receipt.recoveryStatus = "external_change_preserved";
                        }
                        if (receipt.recoveryStatus == "restored")
                            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    }
                    catch (Exception recoveryError) { receipt.error += " Recovery: " + recoveryError.Message; }
                }
            }
            finally
            {
                try
                {
                    if (root != null) PrefabUtility.UnloadPrefabContents(root);
                    if (File.Exists(candidate) && !AssetDatabase.DeleteAsset(candidate))
                        throw new IOException("Could not remove the candidate prefab: " + candidate);
                    receipt.sha256After = File.Exists(absolute) ? Hash(File.ReadAllBytes(absolute)) : "";
                    receipt.metaSha256After = File.Exists(absolute + ".meta") ? Hash(File.ReadAllBytes(absolute + ".meta")) : "";
                }
                catch (Exception cleanupError)
                {
                    receipt.error += " Cleanup: " + cleanupError.Message;
                    receipt.persistenceVerified = false;
                }
                finally { ActiveAssets.Remove(absolute); }
            }
            return receipt;
        }

        private static Component ResolveComponent(GameObject root, PrefabPatchPayload request)
        {
            Transform target = root.transform;
            if (request.hierarchyPath != ".")
            {
                foreach (string part in (request.hierarchyPath ?? "").Split('/'))
                {
                    var matches = new List<Transform>();
                    for (int index = 0; index < target.childCount; index++)
                        if (target.GetChild(index).name == part) matches.Add(target.GetChild(index));
                    if (matches.Count != 1) throw new InvalidOperationException("Missing or ambiguous hierarchy segment: " + part);
                    target = matches[0];
                }
            }
            var components = target.GetComponents<Component>().Where(item => item != null && item.GetType().FullName == request.componentType).ToArray();
            if (components.Length != 1) throw new InvalidOperationException("Expected one component with the exact full type name; found " + components.Length);
            return components[0];
        }

        private static string Hash(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string Hash(Stream stream)
        {
            stream.Position = 0;
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
