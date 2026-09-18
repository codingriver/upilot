// -----------------------------------------------------------------------
// UPilot Editor tests
// -----------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotPrefabReferenceQueryTests
    {
        private const string TempFolder = "Assets/UPilotPrefabReferenceQueryTests";
        private const string RootPrefabPath = TempFolder + "/Root.prefab";
        private const string ExternalPrefabPath = TempFolder + "/External.prefab";
        private const string NestedPrefabPath = TempFolder + "/Nested.prefab";

        private static readonly MethodInfo AddSerializedFields = typeof(UPilotAssetService).GetMethod(
            "AddSerializedFields",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo WalkPrefabComponents = typeof(UPilotAssetService).GetMethod(
            "WalkPrefabComponents",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo TraversePrefabObjectReferences = typeof(UPilotAssetService).GetMethod(
            "TraversePrefabObjectReferences",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo BuildObjectAssetDependencies = typeof(UPilotAssetService).GetMethod(
            "BuildObjectAssetDependencies",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo BuildReverseObjectDependencies = typeof(UPilotAssetService).GetMethod(
            "BuildReverseObjectDependencies",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo CreateReversePartialResult = typeof(UPilotAssetService).GetMethod(
            "CreateReversePartialResult",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo MarkTraversalCancelled = typeof(UPilotAssetService).GetMethod(
            "MarkTraversalCancelled",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(PrefabQueryComponentsResultPayload), typeof(int) },
            null);
        private static readonly MethodInfo MarkPrefabCleanupFailure = typeof(UPilotAssetService).GetMethod(
            "MarkCleanupFailure",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(PrefabQueryComponentsResultPayload), typeof(string), typeof(Exception) },
            null);
        private static readonly MethodInfo TryValidatePrefabReferenceQuery = typeof(UPilotAssetService).GetMethod(
            "TryValidatePrefabReferenceQuery",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly Type PrefabReferenceWorkItemType = typeof(UPilotAssetService).GetNestedType(
            "PrefabReferenceWorkItem",
            BindingFlags.NonPublic);
        private static readonly FieldInfo DependencyCursors = typeof(UPilotAssetService).GetField(
            "DependencyCursors",
            BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly Type AssetDependencyCursorStateType = typeof(UPilotAssetService).GetNestedType(
            "AssetDependencyCursorState",
            BindingFlags.NonPublic);
        private static readonly FieldInfo CursorCreatedAtUtc = AssetDependencyCursorStateType?.GetField(
            "createdAtUtc",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        [Test]
        public void ReferenceOptionsRejectDepthWhenTraversalIsDisabled()
        {
            Assert.That(TryValidatePrefabReferenceQuery, Is.Not.Null);
            var arguments = new object[]
            {
                new PrefabQueryComponentsPayload { followObjectReferences = false, referenceDepth = 2 }, null, null,
            };
            var valid = (bool)TryValidatePrefabReferenceQuery.Invoke(null, arguments);

            Assert.That(valid, Is.False);
            Assert.That(arguments[1], Is.EqualTo("REFERENCE_QUERY_INVALID"));
            Assert.That(arguments[2], Does.Contain("followObjectReferences"));
        }

        [Test]
        public void FollowObjectReferencesScansWhenSerializedFieldsAreOmitted()
        {
            var root = new GameObject("UPilotPrefabReferenceRoot");
            var child = new GameObject("Target");
            child.transform.SetParent(root.transform);
            try
            {
                var probe = root.AddComponent<PrefabReferenceQueryProbe>();
                probe.target = child;
                var fields = new List<SerializedPropertyInfo>();
                var observedReferences = new List<UnityEngine.Object>();

                InvokeAddSerializedFields(probe, fields, root.transform, includeFields: false, traversalRequested: true,
                    (reference, _) => observedReferences.Add(reference));

                Assert.That(fields, Is.Empty);
                Assert.That(observedReferences, Is.EqualTo(new[] { child }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DefaultObjectReferenceExplanationKeepsLoadedPrefabBoundary()
        {
            var root = new GameObject("UPilotPrefabReferenceRoot");
            var child = new GameObject("Target");
            child.transform.SetParent(root.transform);
            try
            {
                var probe = root.AddComponent<PrefabReferenceQueryProbe>();
                probe.target = child;
                var fields = new List<SerializedPropertyInfo>();

                InvokeAddSerializedFields(probe, fields, root.transform, includeFields: true, traversalRequested: false, null);

                Assert.That(fields, Has.Count.EqualTo(1));
                Assert.That(fields[0].propertyPath, Is.EqualTo("target"));
                Assert.That(fields[0].referenceScope, Is.EqualTo("child"));
                Assert.That(fields[0].notSearchedReason, Is.EqualTo("withinLoadedPrefab"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void NestedPrefabReferenceIsExplainedAsItsOwnSourceBeforeTheLoadedRootChildBoundary()
        {
            GameObject root = null;
            try
            {
                CreateNestedReferencePrefabs();
                root = PrefabUtility.LoadPrefabContents(RootPrefabPath);
                var probe = root.GetComponent<PrefabReferenceQueryProbe>();
                Assert.That(probe, Is.Not.Null);
                var fields = new List<SerializedPropertyInfo>();

                InvokeAddSerializedFields(probe, fields, root.transform, includeFields: true, traversalRequested: false, null);

                Assert.That(fields, Has.Count.EqualTo(1));
                Assert.That(fields[0].referenceScope, Is.EqualTo("nestedSource"));
                Assert.That(fields[0].notSearchedReason, Is.EqualTo("nestedPrefabContentsNotExpanded"));
                Assert.That(fields[0].objectReferencePath, Is.EqualTo(NestedPrefabPath));
                Assert.That(fields[0].objectReferenceGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(NestedPrefabPath)));
                Assert.That(fields[0].objectReferenceLocalFileId, Is.Not.Null.And.Not.Empty);
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void NestedPrefabReferenceBecomesBoundedTraversalEvidenceWhenOptedIn()
        {
            try
            {
                CreateNestedReferencePrefabs();
                var result = RunReferenceQuery(includeNestedPrefabContents: false);
                var nested = result.objectReferences.Find(reference =>
                    string.Equals(reference.assetPath, NestedPrefabPath, StringComparison.Ordinal));

                Assert.That(nested, Is.Not.Null);
                Assert.That(nested.referenceScope, Is.EqualTo("externalAsset"));
                Assert.That(nested.notSearchedReason, Is.EqualTo("nestedPrefabContentsNotExpanded"));
                Assert.That(nested.guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(NestedPrefabPath)));
                Assert.That(nested.localFileId, Is.Not.Null.And.Not.Empty);
                Assert.That(result.notSearchedReasons, Does.Contain("nestedPrefabContentsNotExpanded"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ReferenceTraversalKeepsExternalPrefabExplicitlyUnsearchedWhenNestedContentsAreDisabled()
        {
            try
            {
                CreateReferencePrefabs();
                var result = RunReferenceQuery(includeNestedPrefabContents: false);

                var external = result.objectReferences.Find(reference =>
                    string.Equals(reference.assetPath, ExternalPrefabPath, StringComparison.Ordinal));
                Assert.That(external, Is.Not.Null);
                Assert.That(external.notSearchedReason, Is.EqualTo("nestedPrefabContentsNotExpanded"));
                Assert.That(result.coverageComplete, Is.False);
                Assert.That(result.notSearchedReasons, Does.Contain("nestedPrefabContentsNotExpanded"));
                Assert.That(result.readOnly && !result.changedEditorState, Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ReferenceTraversalExpandsExplicitExternalPrefabWithoutChangingAssets()
        {
            try
            {
                CreateReferencePrefabs();
                var result = RunReferenceQuery(includeNestedPrefabContents: true);

                var external = result.objectReferences.Find(reference =>
                    string.Equals(reference.assetPath, ExternalPrefabPath, StringComparison.Ordinal));
                Assert.That(external, Is.Not.Null);
                Assert.That(external.notSearchedReason, Is.Null.Or.Empty);
                Assert.That(
                    result.coverageComplete,
                    Is.True,
                    "truncation=" + result.truncationReason + "; changed=" + result.inspectionStateChangeAssetPath);
                Assert.That(result.inspectionStateChanged, Is.False);
                Assert.That(result.objectReferences.Count, Is.EqualTo(1));
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ObjectDependencyEvidenceExcludesLoadedPrefabChildrenAndRetainsExternalReference()
        {
            try
            {
                CreateReferencePrefabs();
                Assert.That(BuildObjectAssetDependencies, Is.Not.Null);
                var result = (AssetDependenciesResultPayload)BuildObjectAssetDependencies.Invoke(null, new object[]
                {
                    new AssetDependenciesPayload
                    {
                        assetPath = RootPrefabPath,
                        recursive = true,
                        evidenceMode = "object",
                    },
                });

                Assert.That(result.coverageComplete, Is.True);
                Assert.That(result.dependencies, Has.Count.EqualTo(1));
                Assert.That(result.dependencies[0].assetPath, Is.EqualTo(ExternalPrefabPath));
                Assert.That(result.dependencies[0].evidenceKind, Is.EqualTo("objectReference"));
                Assert.That(result.dependencies[0].direct, Is.True);
                Assert.That(result.dependencies[0].propertyPath, Is.EqualTo("target"));
                Assert.That(result.readOnly && !result.changedEditorState, Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ObjectDependencyTraversalExpandsPersistedObjectCyclesOncePerStableIdentity()
        {
            try
            {
                CreateObjectReferenceCycleAssets();
                Assert.That(BuildObjectAssetDependencies, Is.Not.Null);
                var result = (AssetDependenciesResultPayload)BuildObjectAssetDependencies.Invoke(null, new object[]
                {
                    new AssetDependenciesPayload
                    {
                        assetPath = TempFolder + "/CycleA.asset",
                        recursive = true,
                        evidenceMode = "object",
                    },
                });

                Assert.That(result.coverageComplete, Is.True);
                Assert.That(result.dependencies.Count(item => item.assetPath == TempFolder + "/CycleA.asset"), Is.EqualTo(1));
                Assert.That(result.dependencies.Count(item => item.assetPath == TempFolder + "/CycleB.asset"), Is.EqualTo(1));
                Assert.That(result.dependencies.All(item => item.evidenceKind == "objectReference"), Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ReverseObjectDependenciesPageFromStableCandidatesAndRejectChangedCandidates()
        {
            try
            {
                CreateReferencePrefabs();
                Assert.That(BuildReverseObjectDependencies, Is.Not.Null);
                var externalGuid = AssetDatabase.AssetPathToGUID(ExternalPrefabPath);
                var initialPayload = CreateReversePayload(externalGuid, string.Empty);

                var firstPage = InvokeReverseQuery(initialPayload);
                Assert.That(firstPage.candidateCount, Is.GreaterThanOrEqualTo(2));
                Assert.That(firstPage.nextContinuationToken, Is.Not.Empty);
                Assert.That(firstPage.partial, Is.True);
                Assert.That(firstPage.readOnly && !firstPage.changedEditorState, Is.True);

                var replay = InvokeReverseQuery(CreateReversePayload(externalGuid, firstPage.continuationToken));
                Assert.That(ReferenceEquals(firstPage, replay), Is.True, "A repeated page must return the cached page.");

                var dependencies = new List<AssetDependencyInfoPayload>(firstPage.dependencies);
                var continuation = firstPage.nextContinuationToken;
                while (!string.IsNullOrEmpty(continuation))
                {
                    var page = InvokeReverseQuery(CreateReversePayload(externalGuid, continuation));
                    Assert.That(page.readOnly && !page.changedEditorState, Is.True);
                    dependencies.AddRange(page.dependencies);
                    continuation = page.nextContinuationToken;
                }
                Assert.That(dependencies.Exists(item => string.Equals(item.assetPath, ExternalPrefabPath, StringComparison.Ordinal)), Is.True);

                var changedCursor = InvokeReverseQuery(CreateReversePayload(externalGuid, string.Empty));
                Assert.That(changedCursor.nextContinuationToken, Is.Not.Empty);
                CreateUnrelatedPrefab(TempFolder + "/Changed.prefab");

                var cachedAfterSourceChange = InvokeReverseQuery(CreateReversePayload(externalGuid, changedCursor.continuationToken));
                Assert.That(ReferenceEquals(changedCursor, cachedAfterSourceChange), Is.True,
                    "A cached page remains valid local evidence after later source changes.");

                var exception = Assert.Throws<TargetInvocationException>(() =>
                    BuildReverseObjectDependencies.Invoke(null, new object[]
                    {
                        CreateReversePayload(externalGuid, changedCursor.nextContinuationToken), "test-bridge-session",
                    }));
                Assert.That(exception.InnerException, Is.Not.Null);
                Assert.That(exception.InnerException.GetType().Name, Is.EqualTo("AssetDependencyQueryException"));
                Assert.That(exception.InnerException.Message, Does.Contain("Candidate assets changed"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ReverseDependencyCursorExpiresAfterItsFixedTenMinuteLifetime()
        {
            string cursorId = null;
            try
            {
                ClearDependencyCursors();
                CreateReferencePrefabs();
                Assert.That(DependencyCursors, Is.Not.Null);
                Assert.That(CursorCreatedAtUtc, Is.Not.Null);
                var externalGuid = AssetDatabase.AssetPathToGUID(ExternalPrefabPath);
                var firstPage = InvokeReverseQuery(CreateReversePayload(externalGuid, string.Empty));
                Assert.That(firstPage.nextContinuationToken, Is.Not.Empty);
                cursorId = CursorId(firstPage.continuationToken);
                var cursor = ((IDictionary)DependencyCursors.GetValue(null))[cursorId];
                Assert.That(cursor, Is.Not.Null);
                CursorCreatedAtUtc.SetValue(cursor, DateTime.UtcNow.AddMinutes(-10).AddSeconds(-1));

                var exception = Assert.Throws<TargetInvocationException>(() =>
                    BuildReverseObjectDependencies.Invoke(null, new object[]
                    {
                        CreateReversePayload(externalGuid, firstPage.nextContinuationToken), "test-bridge-session",
                    }));

                Assert.That(exception.InnerException, Is.Not.Null);
                Assert.That(exception.InnerException.Message, Does.Contain("cursor expired"));
            }
            finally
            {
                RemoveDependencyCursor(cursorId);
                ClearDependencyCursors();
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ReverseDependencyCursorRejectsTheNinthActiveSnapshotWithoutEvictingTheFirstEight()
        {
            var cursorIds = new List<string>();
            try
            {
                ClearDependencyCursors();
                CreateReferencePrefabs();
                Assert.That(DependencyCursors, Is.Not.Null);
                var externalGuid = AssetDatabase.AssetPathToGUID(ExternalPrefabPath);
                for (var index = 0; index < 8; index++)
                {
                    var page = InvokeReverseQuery(CreateReversePayload(externalGuid, string.Empty));
                    Assert.That(page.nextContinuationToken, Is.Not.Empty);
                    cursorIds.Add(CursorId(page.continuationToken));
                }

                var exception = Assert.Throws<TargetInvocationException>(() =>
                    BuildReverseObjectDependencies.Invoke(null, new object[]
                    {
                        CreateReversePayload(externalGuid, string.Empty), "test-bridge-session",
                    }));

                Assert.That(exception.InnerException, Is.Not.Null);
                Assert.That(exception.InnerException.Message, Does.Contain("eight active dependency cursors"));
                var cursors = (IDictionary)DependencyCursors.GetValue(null);
                foreach (var cursorId in cursorIds)
                    Assert.That(cursors.Contains(cursorId), Is.True, "Capacity rejection must not evict an active cursor.");
            }
            finally
            {
                foreach (var cursorId in cursorIds)
                    RemoveDependencyCursor(cursorId);
                ClearDependencyCursors();
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ReverseLiteralQueryOnlyReturnsDeclaredSerializedPropertyMatches()
        {
            try
            {
                CreateReferencePrefabs();
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(RootPrefabPath);
                Assert.That(root, Is.Not.Null);
                var literalProbe = root.GetComponent<PrefabReferenceLiteralProbe>();
                Assert.That(literalProbe, Is.Not.Null, "The fixture must persist a real component field, not infer a string from asset text.");
                var literalProperty = new SerializedObject(literalProbe).FindProperty("literal");
                Assert.That(literalProperty, Is.Not.Null);
                Assert.That(literalProperty.propertyType, Is.EqualTo(SerializedPropertyType.String));
                Assert.That(literalProperty.stringValue, Is.EqualTo("fixture-literal"));
                var payload = new AssetDependenciesPayload
                {
                    evidenceMode = "object",
                    direction = "reverse",
                    scope = new List<string> { TempFolder },
                    maxNodes = 50,
                    timeBudgetMs = 5000,
                    referenceQuery = new AssetReferenceQueryPayload
                    {
                        kind = "stringLiteral",
                        value = "fixture-literal",
                        propertyPaths = new List<string> { "literal" },
                    },
                };
                var result = InvokeReverseQuery(payload);

                Assert.That(result.dependencies, Has.Count.EqualTo(1));
                Assert.That(result.dependencies[0].evidenceKind, Is.EqualTo("literalMatch"));
                Assert.That(result.dependencies[0].propertyPath, Is.EqualTo("literal"));
                Assert.That(result.readOnly && !result.changedEditorState, Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ReverseDependencyCursorCanonicalizesScopeAndLiteralPropertyPaths()
        {
            var signature = typeof(UPilotAssetService).GetMethod(
                "BuildDependencyQuerySignature", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(signature, Is.Not.Null);

            var first = new AssetDependenciesPayload
            {
                evidenceMode = "object",
                direction = "reverse",
                scope = new List<string> { "Assets/Z/", "Assets/A", "Assets/A" },
                referenceQuery = new AssetReferenceQueryPayload
                {
                    kind = "stringLiteral",
                    value = "fixture-key",
                    propertyPaths = new List<string> { "m_second", "m_first", "m_first" },
                },
                maxNodes = 1,
                timeBudgetMs = 1,
            };
            var equivalent = new AssetDependenciesPayload
            {
                evidenceMode = "object",
                direction = "reverse",
                scope = new List<string> { "Assets/A", "Assets/Z" },
                referenceQuery = new AssetReferenceQueryPayload
                {
                    kind = "stringLiteral",
                    value = "fixture-key",
                    propertyPaths = new List<string> { "m_first", "m_second" },
                },
                maxNodes = 5000,
                timeBudgetMs = 30000,
            };

            Assert.That(signature.Invoke(null, new object[] { first }), Is.EqualTo(signature.Invoke(null, new object[] { equivalent })));
        }

        [Test]
        public void ReverseBudgetResultExplainsThatTheRemainingScopeWasNotSearched()
        {
            Assert.That(CreateReversePartialResult, Is.Not.Null);
            var result = (AssetDependenciesResultPayload)CreateReversePartialResult.Invoke(null, new object[]
            {
                null, null, 1200, "timeBudget", true, false,
            });

            Assert.That(result.coverageComplete, Is.False);
            Assert.That(result.truncationReason, Is.EqualTo("timeBudget"));
            Assert.That(result.notSearchedReason, Is.EqualTo("budget"));
        }

        [Test]
        public void PrefabTraversalCancellationAndCleanupExposeUnsearchedReasons()
        {
            Assert.That(MarkTraversalCancelled, Is.Not.Null);
            Assert.That(MarkPrefabCleanupFailure, Is.Not.Null);

            var cancelled = new PrefabQueryComponentsResultPayload();
            MarkTraversalCancelled.Invoke(null, new object[] { cancelled, 7 });
            Assert.That(cancelled.notSearchedReasons, Does.Contain("cancelled"));

            var cleanupFailed = new PrefabQueryComponentsResultPayload();
            MarkPrefabCleanupFailure.Invoke(null, new object[]
            {
                cleanupFailed, "Assets/Test.prefab", new InvalidOperationException("fixture unload failure"),
            });
            Assert.That(cleanupFailed.notSearchedReasons, Does.Contain("prefabUnloadFailed"));
        }

        [Test]
        public void ModelVariantSubassetAndMissingGuidFixturesKeepIdentityClaimsBounded()
        {
            var modelPath = TempFolder + "/Model.obj";
            var basePath = TempFolder + "/Base.prefab";
            var variantPath = TempFolder + "/Variant.prefab";
            var assetPath = TempFolder + "/Subassets.asset";
            GameObject missingGuidOwner = null;
            GameObject missingGuidTarget = null;
            try
            {
                AssetDatabase.DeleteAsset(TempFolder);
                AssetDatabase.CreateFolder("Assets", "UPilotPrefabReferenceQueryTests");
                File.WriteAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, modelPath),
                    "o Model\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
                AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceSynchronousImport);
                Assert.That(PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath)), Is.EqualTo(PrefabAssetType.Model));
                var model = UPilotAssetService.BuildObjectAssetDependenciesWithCancellation(
                    new AssetDependenciesPayload { assetPath = modelPath, evidenceMode = "object" }, CancellationToken.None);
                Assert.That(model.truncationReason, Is.EqualTo("modelPrefabContentsUnsupported"));

                var baseObject = new GameObject("Base");
                PrefabUtility.SaveAsPrefabAsset(baseObject, basePath);
                UnityEngine.Object.DestroyImmediate(baseObject);
                var variantInstance = PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(basePath)) as GameObject;
                PrefabUtility.SaveAsPrefabAsset(variantInstance, variantPath);
                UnityEngine.Object.DestroyImmediate(variantInstance);
                Assert.That(PrefabUtility.GetPrefabAssetType(AssetDatabase.LoadAssetAtPath<GameObject>(variantPath)), Is.EqualTo(PrefabAssetType.Variant));

                var main = ScriptableObject.CreateInstance<PrefabReferenceQueryAsset>();
                var subasset = ScriptableObject.CreateInstance<PrefabReferenceQueryAsset>();
                AssetDatabase.CreateAsset(main, assetPath);
                AssetDatabase.AddObjectToAsset(subasset, assetPath);
                main.objectTarget = subasset;
                EditorUtility.SetDirty(main);
                AssetDatabase.SaveAssets();
                var subassetResult = UPilotAssetService.BuildObjectAssetDependenciesWithCancellation(
                    new AssetDependenciesPayload { assetPath = assetPath, evidenceMode = "object" }, CancellationToken.None);
                Assert.That(subassetResult.dependencies.Any(item => item.guid == AssetDatabase.AssetPathToGUID(assetPath)
                    && !string.IsNullOrEmpty(item.localFileId)), Is.True);

                missingGuidOwner = new GameObject("MissingGuidOwner");
                missingGuidTarget = new GameObject("MissingGuidTarget");
                missingGuidOwner.AddComponent<PrefabReferenceQueryAssetProbe>().target = missingGuidTarget;
                var fields = new List<SerializedPropertyInfo>();
                UPilotAssetService.AddSerializedFields(
                    missingGuidOwner.GetComponent<PrefabReferenceQueryAssetProbe>(), 6, fields,
                    missingGuidOwner.transform, RootPrefabPath, true, false, null);
                Assert.That(fields.Single(field => field.propertyPath == "target").referenceScope, Is.EqualTo("unknown"));
                Assert.That(fields.Single(field => field.propertyPath == "target").notSearchedReason, Is.EqualTo("stableAssetIdentityUnavailable"));
            }
            finally
            {
                if (missingGuidOwner != null) UnityEngine.Object.DestroyImmediate(missingGuidOwner);
                if (missingGuidTarget != null) UnityEngine.Object.DestroyImmediate(missingGuidTarget);
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void IsolatedLoadDirtyCancellationAndUnloadFailureRemainObservableWithoutSaving()
        {
            GameObject unresolvedRoot = null;
            try
            {
                CreateDirtyLoadPrefab();
                UPilotAssetService.PrefabContentsChangedStateForTests = root => true;
                var dirty = UPilotAssetService.BuildObjectAssetDependenciesWithCancellation(
                    new AssetDependenciesPayload { assetPath = RootPrefabPath, evidenceMode = "object" }, CancellationToken.None);
                Assert.That(dirty.changedEditorState, Is.True);
                Assert.That(dirty.sideEffectsMayHaveOccurred, Is.True);
                Assert.That(dirty.truncationReason, Is.EqualTo("prefabLoadCallbackChangedState"));

                UPilotAssetService.PrefabContentsChangedStateForTests = null;
                var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var cancelled = UPilotAssetService.BuildObjectAssetDependenciesWithCancellation(
                    new AssetDependenciesPayload { assetPath = RootPrefabPath, evidenceMode = "object" }, cancellation.Token);
                Assert.That(cancelled.truncationReason, Is.EqualTo("cancelled"));

                UPilotAssetService.PrefabContentsUnloadFaultForTests = root =>
                {
                    unresolvedRoot = root;
                    return new IOException("fixture unload failure");
                };
                var unloadFailed = UPilotAssetService.BuildObjectAssetDependenciesWithCancellation(
                    new AssetDependenciesPayload { assetPath = RootPrefabPath, evidenceMode = "object" }, CancellationToken.None);
                Assert.That(unloadFailed.cleanupVerified, Is.False);
                Assert.That(unloadFailed.unresolvedResources, Does.Contain("prefabContents:" + RootPrefabPath));
                Assert.That(unloadFailed.sideEffectsMayHaveOccurred, Is.True);
            }
            finally
            {
                UPilotAssetService.PrefabContentsChangedStateForTests = null;
                UPilotAssetService.PrefabContentsUnloadFaultForTests = null;
                if (unresolvedRoot != null)
                    PrefabUtility.UnloadPrefabContents(unresolvedRoot);
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test, Explicit("P2-WP-11-T09: manual U6 performance fixture; do not run in routine EditMode.")]
        public void ThousandCandidateReverseQueryRemainsBoundedBySnapshotCapacityAndNodeBudget()
        {
            try
            {
                CreateReferencePrefabs();
                for (var index = 0; index < 1000; index++)
                    CreateUnrelatedPrefab(TempFolder + "/Candidate" + index.ToString("D4") + ".prefab");
                var first = InvokeReverseQuery(CreateReversePayload(AssetDatabase.AssetPathToGUID(ExternalPrefabPath), string.Empty));
                Assert.That(first.candidateCount, Is.GreaterThanOrEqualTo(1002));
                Assert.That(first.coverageComplete, Is.False);
                Assert.That(first.truncationReason, Is.EqualTo("nodeBudget"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        private static void InvokeAddSerializedFields(
            Component component,
            List<SerializedPropertyInfo> fields,
            Transform prefabRoot,
            bool includeFields,
            bool traversalRequested,
            Action<UnityEngine.Object, SerializedPropertyInfo> onObjectReference)
        {
            Assert.That(AddSerializedFields, Is.Not.Null);
            AddSerializedFields.Invoke(null, new object[]
            {
                component,
                6,
                fields,
                prefabRoot,
                "Assets/UPilotPrefabReferenceQueryTests/Probe.prefab",
                includeFields,
                traversalRequested,
                onObjectReference,
            });
        }

        private static void CreateReferencePrefabs()
        {
            AssetDatabase.DeleteAsset(TempFolder);
            AssetDatabase.CreateFolder("Assets", "UPilotPrefabReferenceQueryTests");

            var external = new GameObject("External");
            try
            {
                external.AddComponent<PrefabReferenceQueryProbe>();
                PrefabUtility.SaveAsPrefabAsset(external, ExternalPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(external);
            }

            var root = new GameObject("Root");
            try
            {
                var child = new GameObject("Child");
                child.transform.SetParent(root.transform);
                root.AddComponent<PrefabReferenceQueryProbe>().target = child;
                var externalProbe = root.AddComponent<PrefabReferenceQueryProbe>();
                externalProbe.target = AssetDatabase.LoadAssetAtPath<GameObject>(ExternalPrefabPath);
                root.AddComponent<PrefabReferenceLiteralProbe>().literal = "fixture-literal";
                PrefabUtility.SaveAsPrefabAsset(root, RootPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void CreateNestedReferencePrefabs()
        {
            AssetDatabase.DeleteAsset(TempFolder);
            AssetDatabase.CreateFolder("Assets", "UPilotPrefabReferenceQueryTests");

            var nestedSource = new GameObject("NestedSource");
            try
            {
                var nestedChild = new GameObject("NestedChild");
                nestedChild.transform.SetParent(nestedSource.transform);
                PrefabUtility.SaveAsPrefabAsset(nestedSource, NestedPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(nestedSource);
            }

            var outerRoot = new GameObject("Root");
            try
            {
                var nestedAsset = AssetDatabase.LoadAssetAtPath<GameObject>(NestedPrefabPath);
                var nestedInstance = PrefabUtility.InstantiatePrefab(nestedAsset) as GameObject;
                Assert.That(nestedInstance, Is.Not.Null);
                nestedInstance.transform.SetParent(outerRoot.transform);
                outerRoot.AddComponent<PrefabReferenceQueryProbe>().target = nestedInstance.transform.GetChild(0).gameObject;
                PrefabUtility.SaveAsPrefabAsset(outerRoot, RootPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(outerRoot);
            }
        }

        private static void CreateObjectReferenceCycleAssets()
        {
            AssetDatabase.DeleteAsset(TempFolder);
            AssetDatabase.CreateFolder("Assets", "UPilotPrefabReferenceQueryTests");

            var first = ScriptableObject.CreateInstance<PrefabReferenceQueryAsset>();
            var second = ScriptableObject.CreateInstance<PrefabReferenceQueryAsset>();
            AssetDatabase.CreateAsset(first, TempFolder + "/CycleA.asset");
            AssetDatabase.CreateAsset(second, TempFolder + "/CycleB.asset");
            first = AssetDatabase.LoadAssetAtPath<PrefabReferenceQueryAsset>(TempFolder + "/CycleA.asset");
            second = AssetDatabase.LoadAssetAtPath<PrefabReferenceQueryAsset>(TempFolder + "/CycleB.asset");
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            first.objectTarget = second;
            second.objectTarget = first;
            EditorUtility.SetDirty(first);
            EditorUtility.SetDirty(second);
            AssetDatabase.SaveAssets();
        }

        private static void CreateDirtyLoadPrefab()
        {
            AssetDatabase.DeleteAsset(TempFolder);
            AssetDatabase.CreateFolder("Assets", "UPilotPrefabReferenceQueryTests");
            var root = new GameObject("DirtyLoadRoot");
            try
            {
                root.AddComponent<UPilotPrefabReferenceDirtyLoadProbe>();
                PrefabUtility.SaveAsPrefabAsset(root, RootPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static PrefabQueryComponentsResultPayload RunReferenceQuery(bool includeNestedPrefabContents)
        {
            Assert.That(WalkPrefabComponents, Is.Not.Null);
            Assert.That(TraversePrefabObjectReferences, Is.Not.Null);
            Assert.That(PrefabReferenceWorkItemType, Is.Not.Null);

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(RootPrefabPath);
                var result = new PrefabQueryComponentsResultPayload
                {
                    prefabPath = RootPrefabPath,
                    componentType = typeof(PrefabReferenceQueryProbe).FullName,
                    followObjectReferences = true,
                };
                var seeds = Activator.CreateInstance(typeof(List<>).MakeGenericType(PrefabReferenceWorkItemType));
                WalkPrefabComponents.Invoke(null, new object[]
                {
                    root.transform,
                    root.name,
                    root.transform,
                    RootPrefabPath,
                    typeof(PrefabReferenceQueryProbe).FullName,
                    typeof(PrefabReferenceQueryProbe),
                    false,
                    6,
                    50,
                    true,
                    seeds,
                    result,
                });
                TraversePrefabObjectReferences.Invoke(null, new object[]
                {
                    seeds,
                    RootPrefabPath,
                    includeNestedPrefabContents,
                    2,
                    500,
                    5000,
                    result,
                    CancellationToken.None,
                });
                return result;
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static AssetDependenciesResultPayload InvokeReverseQuery(AssetDependenciesPayload payload)
        {
            return (AssetDependenciesResultPayload)BuildReverseObjectDependencies.Invoke(null, new object[]
            {
                payload, "test-bridge-session",
            });
        }

        private static AssetDependenciesPayload CreateReversePayload(string externalGuid, string continuationToken)
        {
            return new AssetDependenciesPayload
            {
                evidenceMode = "object",
                direction = "reverse",
                recursive = false,
                scope = new List<string> { TempFolder },
                maxNodes = 1,
                timeBudgetMs = 5000,
                continuationToken = continuationToken,
                referenceQuery = new AssetReferenceQueryPayload
                {
                    kind = "guid",
                    value = externalGuid,
                },
            };
        }

        private static string CursorId(string continuationToken)
        {
            var separator = (continuationToken ?? string.Empty).IndexOf(':');
            Assert.That(separator, Is.GreaterThan(0), "Expected a dependency continuation token.");
            return continuationToken.Substring(0, separator);
        }

        private static void RemoveDependencyCursor(string cursorId)
        {
            if (string.IsNullOrEmpty(cursorId) || DependencyCursors == null)
                return;
            ((IDictionary)DependencyCursors.GetValue(null)).Remove(cursorId);
        }

        private static void ClearDependencyCursors()
        {
            if (DependencyCursors != null)
                ((IDictionary)DependencyCursors.GetValue(null)).Clear();
        }

        private static void CreateUnrelatedPrefab(string path)
        {
            var gameObject = new GameObject("Changed");
            try
            {
                gameObject.AddComponent<PrefabReferenceQueryProbe>();
                PrefabUtility.SaveAsPrefabAsset(gameObject, path);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }
    }
}
