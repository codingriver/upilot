using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CodingRiver.UPilot.Acceptance
{
    public static class UPilotAcceptanceMaintenance
    {
        private sealed class Result
        {
            public bool ok;
            public bool wasDirty;
            public bool isDirty;
            public string scenePath;
            public string assetPath;
            public string guid;
            public string error;
        }

        private const string PrefabPhysicsAuditFixturePath =
            "Assets/UPilotAcceptance/Temp/PrefabPhysicsAuditE2E.prefab";
        private static bool _prefabPhysicsAuditSceneWasDirty;
        private static string _prefabPhysicsAuditScenePath = string.Empty;

        public static string CreatePrefabPhysicsAuditFixture()
        {
            var result = new Result { assetPath = PrefabPhysicsAuditFixturePath };
            GameObject root = null;
            try
            {
                var activeScene = SceneManager.GetActiveScene();
                _prefabPhysicsAuditSceneWasDirty = activeScene.isDirty;
                _prefabPhysicsAuditScenePath = activeScene.path;
                if (!AssetDatabase.IsValidFolder("Assets/UPilotAcceptance/Temp"))
                {
                    result.error = "Expected acceptance Temp folder does not exist.";
                    return JsonUtility.ToJson(result);
                }
                if (AssetDatabase.LoadMainAssetAtPath(PrefabPhysicsAuditFixturePath) != null)
                {
                    result.error = "Temporary Prefab physics fixture already exists.";
                    return JsonUtility.ToJson(result);
                }

                root = new GameObject("UPilot_PrefabPhysicsAudit_E2E");
                root.AddComponent<Rigidbody>().isKinematic = true;
                root.AddComponent<BoxCollider>().isTrigger = true;

                var disabledChild = new GameObject("DisabledChild3D");
                disabledChild.layer = 2;
                disabledChild.transform.SetParent(root.transform, false);
                disabledChild.AddComponent<CapsuleCollider>().enabled = false;

                var child2D = new GameObject("Child2D");
                child2D.transform.SetParent(root.transform, false);
                child2D.AddComponent<Rigidbody2D>().bodyType = RigidbodyType2D.Kinematic;
                child2D.AddComponent<BoxCollider2D>().isTrigger = true;

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPhysicsAuditFixturePath);
                if (prefab == null)
                {
                    result.error = "PrefabUtility.SaveAsPrefabAsset returned null.";
                    return JsonUtility.ToJson(result);
                }

                AssetDatabase.SaveAssets();
                result.guid = AssetDatabase.AssetPathToGUID(PrefabPhysicsAuditFixturePath);
                result.ok = !string.IsNullOrEmpty(result.guid);
                if (!result.ok)
                    result.error = "Saved fixture has no GUID.";
                return JsonUtility.ToJson(result);
            }
            catch (System.Exception ex)
            {
                result.error = ex.ToString();
                return JsonUtility.ToJson(result);
            }
            finally
            {
                if (root != null)
                    Object.DestroyImmediate(root);
            }
        }

        public static string DeletePrefabPhysicsAuditFixture()
        {
            var result = new Result { assetPath = PrefabPhysicsAuditFixturePath };
            try
            {
                foreach (var temporaryName in new[]
                         {
                             "UPilot_PrefabPhysicsAudit_E2E",
                             "DisabledChild3D",
                             "Child2D",
                         })
                {
                    var temporaryObject = GameObject.Find(temporaryName);
                    if (temporaryObject != null)
                        Object.DestroyImmediate(temporaryObject);
                }
                var existing = AssetDatabase.LoadMainAssetAtPath(PrefabPhysicsAuditFixturePath);
                result.ok = existing == null || AssetDatabase.DeleteAsset(PrefabPhysicsAuditFixturePath);
                if (!result.ok)
                    result.error = "AssetDatabase.DeleteAsset returned false.";
                var activeScene = SceneManager.GetActiveScene();
                result.scenePath = activeScene.path;
                result.wasDirty = _prefabPhysicsAuditSceneWasDirty;
                if (
                    result.ok
                    && !_prefabPhysicsAuditSceneWasDirty
                    && activeScene.isDirty
                    && string.Equals(activeScene.path, _prefabPhysicsAuditScenePath, System.StringComparison.Ordinal)
                )
                {
                    var markSceneClean = FindMarkSceneCleanMethod();
                    if (markSceneClean == null)
                    {
                        result.ok = false;
                        result.error = "No supported EditorSceneManager scene-dirtiness clear method is available.";
                    }
                    else
                    {
                        markSceneClean.Invoke(null, new object[] { activeScene });
                    }
                }
                result.isDirty = activeScene.isDirty;
                return JsonUtility.ToJson(result);
            }
            catch (System.Exception ex)
            {
                result.error = ex.ToString();
                return JsonUtility.ToJson(result);
            }
        }

        public static string MarkActiveSceneCleanAfterTemporaryComponentAcceptance()
        {
            var scene = SceneManager.GetActiveScene();
            var result = new Result
            {
                scenePath = scene.path,
                wasDirty = scene.isDirty,
            };
            if (GameObject.Find("UPilot_ComponentModify_E2E") != null)
            {
                result.error = "Temporary component acceptance GameObject still exists.";
                return JsonUtility.ToJson(result);
            }

            var markSceneClean = FindMarkSceneCleanMethod();
            if (markSceneClean == null)
            {
                result.error = "No supported EditorSceneManager scene-dirtiness clear method is available.";
                return JsonUtility.ToJson(result);
            }
            markSceneClean.Invoke(null, new object[] { scene });
            result.ok = true;
            result.isDirty = scene.isDirty;
            return JsonUtility.ToJson(result);
        }

        private static MethodInfo FindMarkSceneCleanMethod()
        {
            foreach (var candidate in new[] { "ClearSceneDirtiness", "MarkSceneClean", "ClearSceneDirty" })
            {
                var method = typeof(EditorSceneManager).GetMethod(
                    candidate,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(Scene) },
                    null);
                if (method != null)
                    return method;
            }
            return null;
        }
    }
}
