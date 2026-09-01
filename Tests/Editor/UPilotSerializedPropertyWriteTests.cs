// -----------------------------------------------------------------------
// UPilot Editor tests
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    internal enum SerializedWriteProbeMode
    {
        First,
        Second,
    }

    [Serializable]
    internal sealed class SerializedWriteNestedValue
    {
        public int amount;
        public string label;
    }

    internal sealed class SerializedWriteComponentProbe : MonoBehaviour
    {
        public string text;
        public bool flag;
        public int count;
        public SerializedWriteProbeMode mode;
        public SerializedWriteNestedValue nested = new();
    }

    internal sealed class SerializedWriteAssetProbe : ScriptableObject
    {
        public string text;
        public bool flag;
        public int count;
        public SerializedWriteProbeMode mode;
        public SerializedWriteNestedValue nested = new();
        public int[] values = { 1, 2, 3 };
    }

    public sealed class UPilotSerializedPropertyWriteTests
    {
        private const string TempFolder = "Assets/UPilotSerializedWriteTests";
        private const string AssetPath = TempFolder + "/WriteProbe.asset";

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TempFolder);
        }

        [Test]
        public void ComponentWritesAreStrictAtomicAndReportOldAndNewValues()
        {
            var gameObject = new GameObject("UPilotSerializedWriteProbe");
            try
            {
                var probe = gameObject.AddComponent<SerializedWriteComponentProbe>();
                probe.text = "before";
                probe.flag = false;
                probe.count = 2;
                probe.mode = SerializedWriteProbeMode.First;
                probe.nested.amount = 3;

                var result = UPilotSerializedPropertyUtility.Apply(
                    new SerializedObject(probe),
                    probe,
                    new List<SerializedPropertyWrite>
                    {
                        new() { propertyPath = "text", value = "after" },
                        new() { propertyPath = "flag", value = "true" },
                        new() { propertyPath = "count", value = "7" },
                        new() { propertyPath = "mode", value = "Second" },
                        new() { propertyPath = "nested.amount", value = "11" },
                    },
                    "Test Component Writes");

                Assert.That(result.requestedCount, Is.EqualTo(5));
                Assert.That(result.modifiedCount, Is.EqualTo(5));
                Assert.That(result.changes, Has.Count.EqualTo(5));
                Assert.That(result.changes[0].oldValue, Is.EqualTo("before"));
                Assert.That(result.changes[0].newValue, Is.EqualTo("after"));
                Assert.That(probe.text, Is.EqualTo("after"));
                Assert.That(probe.flag, Is.True);
                Assert.That(probe.count, Is.EqualTo(7));
                Assert.That(probe.mode, Is.EqualTo(SerializedWriteProbeMode.Second));
                Assert.That(probe.nested.amount, Is.EqualTo(11));

                var beforeInvalidBatch = probe.text;
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    UPilotSerializedPropertyUtility.Apply(
                        new SerializedObject(probe),
                        probe,
                        new List<SerializedPropertyWrite>
                        {
                            new() { propertyPath = "text", value = "must-not-apply" },
                            new() { propertyPath = "missing.path", value = "1" },
                        },
                        "Test Invalid Component Writes"));
                Assert.That(exception.Message, Does.Contain("missing.path"));
                Assert.That(probe.text, Is.EqualTo(beforeInvalidBatch));

                Assert.Throws<InvalidOperationException>(() =>
                    UPilotSerializedPropertyUtility.Apply(
                        new SerializedObject(probe),
                        probe,
                        new List<SerializedPropertyWrite>
                        {
                            new() { propertyPath = "count", value = "7" },
                        },
                        "Test Component No-op"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ScriptableObjectWritesAreSavedReimportedAndPersisted()
        {
            AssetDatabase.CreateFolder("Assets", "UPilotSerializedWriteTests");
            var asset = ScriptableObject.CreateInstance<SerializedWriteAssetProbe>();
            asset.text = "before";
            asset.count = 1;
            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();

            var result = UPilotAssetService.ApplyModifyData(
                new SerializedObject(asset),
                asset,
                AssetPath,
                new List<SerializedPropertyWrite>
                {
                    new() { propertyPath = "text", value = "persisted" },
                    new() { propertyPath = "flag", value = "true" },
                    new() { propertyPath = "count", value = "9" },
                    new() { propertyPath = "mode", value = "Second" },
                    new() { propertyPath = "nested.amount", value = "13" },
                    new() { propertyPath = "values.Array.data[1]", value = "22" },
                });

            Assert.That(result.ok, Is.True);
            Assert.That(result.modifiedCount, Is.EqualTo(6));
            Assert.That(result.assetTarget, Is.True);
            Assert.That(result.dirtyApplied, Is.True);
            Assert.That(result.saved, Is.True);
            Assert.That(result.reimported, Is.True);
            Assert.That(result.persistenceVerified, Is.True);
            Assert.That(result.sha256Before, Is.Not.Empty);
            Assert.That(result.sha256After, Is.Not.Empty);
            Assert.That(result.sha256After, Is.Not.EqualTo(result.sha256Before));

            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
            var reloaded = AssetDatabase.LoadAssetAtPath<SerializedWriteAssetProbe>(AssetPath);
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded.text, Is.EqualTo("persisted"));
            Assert.That(reloaded.flag, Is.True);
            Assert.That(reloaded.count, Is.EqualTo(9));
            Assert.That(reloaded.mode, Is.EqualTo(SerializedWriteProbeMode.Second));
            Assert.That(reloaded.nested.amount, Is.EqualTo(13));
            Assert.That(reloaded.values[1], Is.EqualTo(22));
        }

        [Test]
        public void AssetDataDepthAndContinuationExpandNestedAndArrayPropertiesPredictably()
        {
            var probe = ScriptableObject.CreateInstance<SerializedWriteAssetProbe>();
            try
            {
                probe.nested.amount = 17;
                probe.nested.label = "nested";
                probe.values = new[] { 4, 5, 6 };
                var serializedObject = new SerializedObject(probe);

                var shallow = UPilotAssetService.ReadSerializedProperties(serializedObject, 0, 500, "");
                Assert.That(shallow.properties.Exists(item => item.propertyPath == "nested"), Is.True);
                Assert.That(shallow.properties.Exists(item => item.propertyPath == "nested.amount"), Is.False);
                Assert.That(shallow.properties.Find(item => item.propertyPath == "nested").truncated, Is.True);
                Assert.That(shallow.depthTruncated, Is.True);

                var deep = UPilotAssetService.ReadSerializedProperties(serializedObject, 3, 500, "");
                Assert.That(deep.properties.Exists(item => item.propertyPath == "nested.amount"), Is.True);
                Assert.That(deep.properties.Exists(item => item.propertyPath == "nested.label"), Is.True);
                Assert.That(deep.properties.Exists(item => item.propertyPath == "values.Array.data[0]"), Is.True);
                Assert.That(deep.properties.Exists(item => item.propertyPath == "values.Array.data[2]"), Is.True);

                var firstPage = UPilotAssetService.ReadSerializedProperties(serializedObject, 3, 3, "");
                Assert.That(firstPage.returnedCount, Is.EqualTo(3));
                Assert.That(firstPage.truncated, Is.True);
                Assert.That(firstPage.nextContinuationToken, Does.StartWith("v1:"));

                var secondPage = UPilotAssetService.ReadSerializedProperties(
                    serializedObject,
                    3,
                    3,
                    firstPage.nextContinuationToken);
                Assert.That(secondPage.returnedCount, Is.GreaterThan(0));
                foreach (var first in firstPage.properties)
                    Assert.That(secondPage.properties.Exists(item => item.propertyPath == first.propertyPath), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
            }
        }
    }

    public sealed class UPilotAssetMutationContractTests
    {
        private const string TempFolder = "Assets/UPilotAssetMutationTests";

        [TearDown]
        public void TearDown()
        {
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                StageUtility.GoBackToPreviousStage();
            AssetDatabase.DeleteAsset(TempFolder);
        }

        [Test]
        public void CopyAndMoveReturnVerifiedConsistentResultsAcrossTenIterations()
        {
            AssetDatabase.CreateFolder("Assets", "UPilotAssetMutationTests");
            var sourcePath = TempFolder + "/Source.mat";
            var shader = Shader.Find("Standard") ?? Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var source = new Material(shader);
            AssetDatabase.CreateAsset(source, sourcePath);
            AssetDatabase.SaveAssets();
            var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);

            for (var index = 0; index < 10; index++)
            {
                var copyPath = $"{TempFolder}/Copy_{index}.mat";
                var movedPath = $"{TempFolder}/Moved_{index}.mat";
                var copied = UPilotAssetService.CopyAsset(sourcePath, copyPath);
                Assert.That(copied.ok, Is.True);
                Assert.That(copied.verified, Is.True);
                Assert.That(copied.operation, Is.EqualTo("asset.copy"));
                Assert.That(copied.sourceGuid, Is.EqualTo(sourceGuid));
                Assert.That(copied.destinationGuid, Is.Not.Empty);
                Assert.That(copied.destinationGuid, Is.Not.EqualTo(sourceGuid));
                Assert.That(copied.sha256, Is.Not.Empty);

                var copiedGuid = copied.destinationGuid;
                var moved = UPilotAssetService.MoveAsset(copyPath, movedPath);
                Assert.That(moved.ok, Is.True);
                Assert.That(moved.verified, Is.True);
                Assert.That(moved.operation, Is.EqualTo("asset.move"));
                Assert.That(moved.guidPreserved, Is.True);
                Assert.That(moved.sourceGuid, Is.EqualTo(copiedGuid));
                Assert.That(moved.destinationGuid, Is.EqualTo(copiedGuid));
                Assert.That(AssetDatabase.LoadAssetAtPath<Material>(copyPath), Is.Null);
                Assert.That(AssetDatabase.LoadAssetAtPath<Material>(movedPath), Is.Not.Null);
            }
        }

        [Test]
        public void PrefabSaveReturnsVerifiedResultAndPreservesGuid()
        {
            AssetDatabase.CreateFolder("Assets", "UPilotAssetMutationTests");
            var prefabPath = TempFolder + "/Probe.prefab";
            var root = new GameObject("Probe");
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            var stage = PrefabStageUtility.OpenPrefab(prefabPath);
            Assert.That(stage, Is.Not.Null);
            stage.prefabContentsRoot.transform.localScale = new Vector3(2f, 3f, 4f);

            var saved = UPilotPrefabService.SavePrefabStage(stage);
            Assert.That(saved.ok, Is.True);
            Assert.That(saved.verified, Is.True);
            Assert.That(saved.operation, Is.EqualTo("prefab.save"));
            Assert.That(saved.guidPreserved, Is.True);
            Assert.That(saved.destinationGuid, Is.EqualTo(guid));
            Assert.That(saved.sha256, Is.Not.Empty);

            StageUtility.GoBackToPreviousStage();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.transform.localScale, Is.EqualTo(new Vector3(2f, 3f, 4f)));
        }
    }

    public sealed class UPilotPrefabPhysicsAuditTests
    {
        private const string TempFolder = "Assets/UPilotPrefabPhysicsAuditTests";

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(TempFolder);
        }

        [Test]
        public void BatchAuditReturnsColliderTriggerRigidbodyAndObjectStateWithoutChangingEditorContext()
        {
            AssetDatabase.CreateFolder("Assets", "UPilotPrefabPhysicsAuditTests");
            var richPath = TempFolder + "/Rich.prefab";
            var simplePath = TempFolder + "/Simple.prefab";
            var emptyPath = TempFolder + "/Empty.prefab";

            var rich = new GameObject("Rich");
            rich.AddComponent<Rigidbody>().isKinematic = true;
            rich.AddComponent<BoxCollider>().isTrigger = true;
            var child = new GameObject("DisabledChild");
            child.layer = 2;
            child.transform.SetParent(rich.transform, false);
            child.AddComponent<CapsuleCollider>().enabled = false;
            SavePrefabAndDestroy(rich, richPath);

            var simple = new GameObject("Simple");
            simple.AddComponent<SphereCollider>();
            SavePrefabAndDestroy(simple, simplePath);

            SavePrefabAndDestroy(new GameObject("Empty"), emptyPath);

            var stageBefore = PrefabStageUtility.GetCurrentPrefabStage();
            var sceneBefore = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            var result = UPilotAssetService.AuditPrefabPhysics(
                new[] { emptyPath, simplePath, richPath },
                100,
                "colliderCount",
                true);

            Assert.That(result.ok, Is.True);
            Assert.That(result.readOnly, Is.True);
            Assert.That(result.changedEditorState, Is.False);
            Assert.That(result.prefabCount, Is.EqualTo(3));
            Assert.That(result.failedPrefabCount, Is.Zero);
            Assert.That(result.prefabs[0].prefabPath, Is.EqualTo(richPath));
            Assert.That(result.prefabs[0].colliderCount, Is.EqualTo(2));
            Assert.That(result.prefabs[0].triggerCount, Is.EqualTo(1));
            Assert.That(result.prefabs[0].rigidbodyCount, Is.EqualTo(1));
            Assert.That(result.prefabs[0].enabledColliderCount, Is.EqualTo(1));
            Assert.That(result.prefabs[0].components, Has.Count.EqualTo(2));
            var childCollider = result.prefabs[0].components.Find(
                item => item.gameObjectPath == "Rich/DisabledChild");
            Assert.That(childCollider, Is.Not.Null);
            Assert.That(childCollider.layer, Is.EqualTo(2));
            Assert.That(childCollider.componentEnabled, Is.False);
            Assert.That(childCollider.attachedRigidbodyPath, Is.EqualTo("Rich"));
            Assert.That(result.prefabs[1].colliderCount, Is.EqualTo(1));
            Assert.That(result.prefabs[2].colliderCount, Is.Zero);
            Assert.That(PrefabStageUtility.GetCurrentPrefabStage(), Is.SameAs(stageBefore));
            Assert.That(UnityEngine.SceneManagement.SceneManager.GetActiveScene().path, Is.EqualTo(sceneBefore));
        }

        private static void SavePrefabAndDestroy(GameObject root, string path)
        {
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
