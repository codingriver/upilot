using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotPrefabPatchTests
    {
        private const string Folder = "Assets/UPilotPrefabPatchTests";
        private const string Path = Folder + "/Probe.prefab";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "UPilotPrefabPatchTests");
            var root = new GameObject("Probe");
            try
            {
                var child = new GameObject("Camera");
                child.transform.SetParent(root.transform);
                var camera = child.AddComponent<Camera>();
                camera.fieldOfView = 60;
                camera.nearClipPlane = 0.3f;
                var probe = child.AddComponent<UPilotPrefabPatchProbe>();
                Assert.That(probe, Is.Not.Null, "The prefab probe component could not be attached.");
                probe.nested = new UPilotPrefabPatchProbe.Values { mode = UPilotPrefabPatchProbe.Mode.First };
                PrefabUtility.SaveAsPrefabAsset(root, Path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [TearDown] public void TearDown() { AssetDatabase.DeleteAsset(Folder); }

        private static PrefabPatchPayload Request() => new()
        {
            assetPath = Path, hierarchyPath = "Camera", componentType = typeof(Camera).FullName,
            properties = new List<SerializedPropertyWrite> { new() { propertyPath = "field of view", value = "75" } },
        };

        [Test]
        public void DryRunDoesNotWriteAndApplyReloadsVerifiedFields()
        {
            var request = Request();
            var preview = UPilotPrefabPatchService.Patch(request);
            Assert.That(preview.error, Is.Empty);
            Assert.That(preview.sha256Before, Is.EqualTo(preview.sha256After));
            Assert.That(preview.changes[0].oldValue, Is.EqualTo("60"));
            request.dryRun = false;
            request.confirmToken = preview.confirmToken;
            var applied = UPilotPrefabPatchService.Patch(request);
            Assert.That(applied.error, Is.Empty);
            Assert.That(applied.applied && applied.persistenceVerified, Is.True);
            Assert.That(applied.metaSha256Before, Is.EqualTo(applied.metaSha256After));
            var root = PrefabUtility.LoadPrefabContents(Path);
            try
            {
                var camera = root.GetComponentInChildren<Camera>();
                Assert.That(camera.fieldOfView, Is.EqualTo(75));
                Assert.That(camera.nearClipPlane, Is.EqualTo(0.3f).Within(0.00001f));
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [Test]
        public void StaleConfirmationOrInvalidFieldDoesNotModifyPrefab()
        {
            var request = Request();
            var preview = UPilotPrefabPatchService.Patch(request);
            request.dryRun = false;
            request.confirmToken = preview.confirmToken;
            request.properties[0].value = "80";
            var stale = UPilotPrefabPatchService.Patch(request);
            Assert.That(stale.applied, Is.False);
            Assert.That(stale.error, Does.Contain("confirmToken"));
            Assert.That(stale.sha256Before, Is.EqualTo(stale.sha256After));
            request.dryRun = true;
            request.properties.Add(new SerializedPropertyWrite { propertyPath = "not.a.field", value = "1" });
            var invalid = UPilotPrefabPatchService.Patch(request);
            Assert.That(invalid.error, Does.Contain("not.a.field"));
            Assert.That(invalid.sha256Before, Is.EqualTo(invalid.sha256After));
        }

        [Test]
        public void NamedEnumsPersistWithoutChangingAdjacentFieldsAndNumericEnumsAreRejected()
        {
            var request = Request();
            request.componentType = typeof(UPilotPrefabPatchProbe).FullName;
            request.properties = new() { new() { propertyPath = "nested.mode", value = "Second" }, new() { propertyPath = "nested.amount", value = "7" } };
            var preview = UPilotPrefabPatchService.Patch(request);
            Assert.That(preview.error, Is.Empty);
            request.dryRun = false;
            request.confirmToken = preview.confirmToken;
            var applied = UPilotPrefabPatchService.Patch(request);
            Assert.That(applied.error, Is.Empty);
            Assert.That(applied.persistenceVerified, Is.True);
            var root = PrefabUtility.LoadPrefabContents(Path);
            try
            {
                var probe = root.GetComponentInChildren<UPilotPrefabPatchProbe>();
                Assert.That(probe.nested.mode, Is.EqualTo(UPilotPrefabPatchProbe.Mode.Second));
                Assert.That(probe.nested.adjacent, Is.EqualTo(UPilotPrefabPatchProbe.Mode.Second));
                Assert.That(probe.nested.amount, Is.EqualTo(7));
                Assert.That(probe.values, Is.EqualTo(new[] { 1, 2, 3 }));
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            request.dryRun = true;
            request.properties[0].value = "17";
            var numeric = UPilotPrefabPatchService.Patch(request);
            Assert.That(numeric.error, Does.Contain("exact enum name"));
            Assert.That(numeric.sha256Before, Is.EqualTo(numeric.sha256After));
        }

        [Test]
        public void CallbackChangedValueFailsVerificationAndConditionallyRestoresBackup()
        {
            var request = Request();
            request.componentType = typeof(UPilotPrefabPatchProbe).FullName;
            request.properties = new() { new() { propertyPath = "nested.amount", value = "99" } };
            var preview = UPilotPrefabPatchService.Patch(request);
            Assert.That(preview.error, Is.Empty);
            request.dryRun = false;
            request.confirmToken = preview.confirmToken;
            var failed = UPilotPrefabPatchService.Patch(request);
            Assert.That(failed.persistenceVerified, Is.False);
            Assert.That(failed.error, Does.Contain("verification failed"));
            Assert.That(failed.recoveryStatus, Is.EqualTo("restored"));
            Assert.That(failed.sha256Before, Is.EqualTo(failed.sha256After));
        }
    }
}
