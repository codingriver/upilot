using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotTestSelectionTests
    {
        public class TypeNode { public string FullName { get; set; } }
        public class Node
        {
            public string FullName { get; set; }
            public string Name { get; set; }
            public bool IsSuite { get; set; }
            public bool IsTestAssembly { get; set; }
            public TypeNode TypeInfo { get; set; }
            public string[] Categories { get; set; } = Array.Empty<string>();
            public IEnumerable<Node> Children { get; set; } = Array.Empty<Node>();
        }

        private static Node Tree() => new Node
        {
            IsSuite = true, IsTestAssembly = true, Name = "Fixture.dll",
            Children = new[]
            {
                new Node { IsSuite = true, TypeInfo = new TypeNode { FullName = "Demo.First" }, Children = new[]
                {
                    new Node { FullName = "Demo.First.One" },
                    new Node { FullName = "Demo.First.Cases(\"a.b\")" },
                } },
                new Node { IsSuite = true, TypeInfo = new TypeNode { FullName = "Demo.Second" }, Children = new[]
                {
                    new Node { FullName = "Demo.Second.Two" },
                } },
            },
        };

        [Test]
        public void ExactSelectorsUnionDeduplicatesAndReportsEachMatch()
        {
            var result = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "",
                new[] { "Demo.First.One", "Demo.Second.Two", "Missing" },
                new[] { "Demo.First", "Demo.First" });
            Assert.That(result.tests, Has.Count.EqualTo(3));
            Assert.That(result.selectors.ConvertAll(item => item.matchedCount), Is.EqualTo(new[] { 1, 1, 0, 2 }));
            Assert.That(result.unmatchedSelectors.ConvertAll(item => item.selector), Is.EqualTo(new[] { "Missing" }));
            Assert.That(result.duplicateSelectors.ConvertAll(item => item.selector), Is.EqualTo(new[] { "Demo.First" }));
            Assert.That(result.selectionValid, Is.False);
            Assert.That(result.assemblies, Is.EqualTo(new[] { "Fixture.dll" }));
        }

        [Test]
        public void NoMatchesNeverSelectsWholeTree()
        {
            var result = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "", new[] { "Missing" });
            Assert.That(result.matchedCount, Is.Zero);
            Assert.That(result.discoveryStatus, Is.EqualTo("filter_no_match"));
            Assert.That(result.selectionValid, Is.False);
            Assert.That(result.unmatchedSelectors.Single().candidates, Is.EqualTo(new[]
            {
                "Demo.First.Cases(\"a.b\")", "Demo.First.One", "Demo.Second.Two",
            }));
        }

        [Test]
        public void UnmatchedSelectorCandidatesAreBoundedAndNeverBecomeSelected()
        {
            var root = new Node
            {
                IsSuite = true,
                Children = Enumerable.Range(0, 12)
                    .Select(index => new Node { FullName = "Demo.Candidate." + index.ToString("00") })
                    .ToArray(),
            };

            var result = UPilotTestService.ResolveTestSelection(root, "EditMode", "", new[] { "Missing" });

            Assert.That(result.matchedCount, Is.Zero);
            Assert.That(result.tests, Is.Empty);
            Assert.That(result.unmatchedSelectors.Single().candidates, Has.Count.EqualTo(10));
            Assert.That(result.unmatchedSelectors.Single().candidates[0], Is.EqualTo("Demo.Candidate.00"));
            Assert.That(result.unmatchedSelectors.Single().candidates[9], Is.EqualTo("Demo.Candidate.09"));
        }

        [Test]
        public void SelectionSnapshotChangesWhenUnmatchedCandidateSetChanges()
        {
            var firstRoot = new Node { IsSuite = true, Children = new[] { new Node { FullName = "Demo.First" } } };
            var secondRoot = new Node { IsSuite = true, Children = new[] { new Node { FullName = "Demo.Second" } } };

            var first = UPilotTestService.ResolveTestSelection(firstRoot, "EditMode", "", new[] { "Missing" });
            var second = UPilotTestService.ResolveTestSelection(secondRoot, "EditMode", "", new[] { "Missing" });

            Assert.That(first.discoveredCount, Is.EqualTo(second.discoveredCount));
            Assert.That(first.matchedCount, Is.Zero);
            Assert.That(second.matchedCount, Is.Zero);
            Assert.That(second.selectionSnapshotId, Is.Not.EqualTo(first.selectionSnapshotId));
        }

        [Test]
        public void CompatibilityModeKeepsMatchedSubsetButReportsMissingSelectors()
        {
            var result = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "",
                new[] { "Demo.First.One", "Missing" }, requireAllSelectorsMatch: false);
            Assert.That(result.requireAllSelectorsMatch, Is.False);
            Assert.That(result.selectionValid, Is.False);
            Assert.That(result.matchedCount, Is.EqualTo(1));
            Assert.That(result.unmatchedSelectors, Has.Count.EqualTo(1));
        }

        [Test]
        public void NullFilterIsTheSameAsAnOmittedFilter()
        {
            var omitted = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "");
            var nullFilter = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", null);
            Assert.That(nullFilter.requestedFilter, Is.Empty);
            Assert.That(nullFilter.tests, Is.EqualTo(omitted.tests));
            Assert.That(nullFilter.selectionValid, Is.True);
        }

        [Test]
        public void SelectionSnapshotIsStableAndRejectsStaleExpectedIdentity()
        {
            var first = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "",
                new[] { "Demo.First.One" });
            var second = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "",
                new[] { "Demo.First.One" });

            Assert.That(first.selectionDomain, Is.Not.Empty);
            Assert.That(first.selectionSnapshotId, Is.Not.Empty);
            Assert.That(second.selectionDomain, Is.EqualTo(first.selectionDomain));
            Assert.That(second.selectionSnapshotId, Is.EqualTo(first.selectionSnapshotId));
            Assert.That(UPilotTestService.SelectionSnapshotMatches(
                first, first.selectionDomain, first.selectionSnapshotId), Is.True);
            Assert.That(UPilotTestService.SelectionSnapshotMatches(
                first, first.selectionDomain, "stale-snapshot"), Is.False);
            Assert.That(UPilotTestService.SelectionSnapshotMatches(
                first, "stale-domain", first.selectionSnapshotId), Is.False);
        }

        [Test]
        public void MissingExpectedIdentityStillRejectsOldDomainOrForgedSnapshotBeforeRunnerStart()
        {
            var valid = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "",
                new[] { "Demo.First.One" });
            Assert.That(UPilotTestService.SelectionSnapshotMatches(valid, "", ""), Is.True);

            var oldDomain = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "",
                new[] { "Demo.First.One" });
            oldDomain.selectionDomain = "old-callback-domain";
            Assert.That(UPilotTestService.SelectionSnapshotMatches(oldDomain, "", ""), Is.False);
            Assert.That(oldDomain.runnerStartAttempted, Is.False);

            var forgedSnapshot = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "",
                new[] { "Demo.First.One" });
            forgedSnapshot.selectionSnapshotId = "forged-snapshot";
            Assert.That(UPilotTestService.SelectionSnapshotMatches(forgedSnapshot, "", ""), Is.False);
            Assert.That(forgedSnapshot.runnerStartAttempted, Is.False);
            Assert.That(UPilotTestService.SelectionSnapshotMatches(null, "", ""), Is.False);
        }

        [Test]
        public void UnfilteredRunDoesNotRequireASelectionSnapshot()
        {
            Assert.That(UPilotTestService.RunSelectionMatches(null, "", ""), Is.True);
            Assert.That(UPilotTestService.RunSelectionMatches(
                null, "expected-domain", "expected-snapshot"), Is.False);
        }

        [Test]
        public void SameFullNameAcrossAssembliesProducesDistinctTestNameCandidates()
        {
            const string sharedName = "CodingRiver.UPilot.SelectionEvidence.SharedFixture.Leaf";
            var root = new Node
            {
                IsSuite = true,
                Children = new[]
                {
                    new Node
                    {
                        IsSuite = true, IsTestAssembly = true, Name = "UPilot.SelectionA.Tests.dll",
                        TypeInfo = new TypeNode { FullName = "CodingRiver.UPilot.SelectionEvidence.SharedFixture" },
                        Children = new[] { new Node { FullName = sharedName } },
                    },
                    new Node
                    {
                        IsSuite = true, IsTestAssembly = true, Name = "UPilot.SelectionB.Tests.dll",
                        TypeInfo = new TypeNode { FullName = "CodingRiver.UPilot.SelectionEvidence.SharedFixture" },
                        Children = new[] { new Node { FullName = sharedName } },
                    },
                },
            };

            var result = UPilotTestService.ResolveTestSelection(root, "EditMode", "", new[] { "Missing" });

            Assert.That(result.matchedCount, Is.Zero);
            Assert.That(result.unmatchedSelectors.Single().candidates, Is.EqualTo(new[]
            {
                "UPilot.SelectionA.Tests::" + sharedName,
                "UPilot.SelectionB.Tests::" + sharedName,
            }));
        }

        [Test]
        public void PartialExpectedSelectionIdentityIsRejected()
        {
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateExpectedSelectionIdentity(
                "domain-only", ""));
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateExpectedSelectionIdentity(
                "", "snapshot-only"));
        }

        [Test]
        public void DuplicateFullNamesCannotSilentlyRunMoreTestsThanDiscoveredSelection()
        {
            var root = new Node { IsSuite = true, Children = new[]
            {
                new Node { FullName = "Same.Fixture.Test" },
                new Node { FullName = "Same.Fixture.Test" },
            } };
            Assert.Throws<ArgumentException>(() => UPilotTestService.ResolveTestSelection(
                root, "EditMode", "", new[] { "Same.Fixture.Test" }));
        }

        [Test]
        public void EmptyAndMixedSelectorsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateSelectors("", Array.Empty<string>(), null));
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateSelectors("regex:.*", new[] { "Demo.First.One" }, null));
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateSelectors("", null, new[] { "" }));
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateSelectors("", new[] { "Test" }, null, Array.Empty<string>()));
            Assert.Throws<ArgumentException>(() => UPilotTestService.ValidateSelectors("", null, null, matchMode: "invalid"));
        }

        public enum FakeMode { EditMode, PlayMode }
        public class FakeFilter
        {
            public FakeMode testMode;
            public string[] testNames;
            public string[] assemblyNames;
        }

        [Test]
        public void IntersectionUsesAssemblyIdentityAndInheritedCategories()
        {
            var first = Tree();
            first.Categories = new[] { "Inherited" };
            var second = Tree();
            second.Name = "Other.dll";
            var root = new Node { IsSuite = true, Children = new[] { first, second } };
            var intersection = UPilotTestService.ResolveTestSelection(root, "EditMode", "",
                new[] { "Demo.First.One" }, assemblies: new[] { "Fixture" },
                categories: new[] { "Inherited" }, matchMode: "intersection");
            Assert.That(intersection.matchedCount, Is.EqualTo(1));
            Assert.That(intersection.selectedTests[0].assembly, Is.EqualTo("Fixture.dll"));
            var union = UPilotTestService.ResolveTestSelection(root, "EditMode", "", new[] { "Demo.First.One" });
            Assert.That(union.matchedCount, Is.EqualTo(2));
            var filters = UPilotTestService.CreateSelectionFilters(union, typeof(FakeFilter), typeof(FakeMode));
            Assert.That(filters.Length, Is.EqualTo(2));
            Assert.That(((FakeFilter)filters.GetValue(0)).assemblyNames, Is.EqualTo(new[] { "Fixture" }));
            Assert.That(((FakeFilter)filters.GetValue(1)).assemblyNames, Is.EqualTo(new[] { "Other" }));
            Assert.That(((FakeFilter)filters.GetValue(1)).testNames, Is.EqualTo(new[] { "Demo.First.One" }));
            var none = UPilotTestService.ResolveTestSelection(root, "EditMode", "",
                new[] { "Demo.First.One" }, categories: new[] { "Missing" }, matchMode: "intersection");
            Assert.That(none.matchedCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(() => UPilotTestService.CreateSelectionFilters(none, typeof(FakeFilter), typeof(FakeMode)));
        }

        [Test]
        public void LegacyFixtureListUsesTreeTypeNotParameterizedNameSplitting()
        {
            var result = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "Demo.First");
            Assert.That(result.matchedCount, Is.EqualTo(2));
            Assert.That(result.tests, Does.Contain("Demo.First.Cases(\"a.b\")"));
        }

        [Test]
        public void IncrementalResultsFilterWithoutMutatingPersistedEventHistory()
        {
            var source = new TestRunResultPayload
            {
                runGuid = "incremental-run", resultStreamVersion = 1, nextEventSequence = 3,
                events = new List<TestRunEventPayload>
                {
                    new TestRunEventPayload { sequence = 1, kind = "leaf_completed" },
                    new TestRunEventPayload { sequence = 2, kind = "leaf_completed" },
                    new TestRunEventPayload { sequence = 3, kind = "leaf_completed" },
                },
            };

            var incremental = UPilotTestService.CreateIncrementalResult(source, 1, 1);

            Assert.That(incremental.events.Select(item => item.sequence), Is.EqualTo(new[] { 2L }));
            Assert.That(incremental.lastDeliveredEventSequence, Is.EqualTo(2));
            Assert.That(incremental.cursorAccepted, Is.True);
            Assert.That(source.events.Select(item => item.sequence), Is.EqualTo(new[] { 1L, 2L, 3L }));
        }

        [Test]
        public void StatusSummaryRetainsLeafProgressButNotLeafHistory()
        {
            var source = new TestRunResultPayload
            {
                completedLeafCount = 2,
                passedSoFar = 1,
                failedSoFar = 1,
                resultStreamVersion = 1,
                nextEventSequence = 2,
                results = new List<TestResultItemPayload>
                {
                    new TestResultItemPayload { leafKey = "one", testStatus = "Passed" },
                },
                events = new List<TestRunEventPayload>
                {
                    new TestRunEventPayload { sequence = 2, kind = "leaf_completed", leafKey = "two" },
                },
            };

            var summary = UPilotTestService.CreateStatusSummary(source);

            Assert.That(summary.completedLeafCount, Is.EqualTo(2));
            Assert.That(summary.passedSoFar, Is.EqualTo(1));
            Assert.That(summary.failedSoFar, Is.EqualTo(1));
            Assert.That(summary.resultStreamVersion, Is.EqualTo(1));
            Assert.That(summary.nextEventSequence, Is.EqualTo(2));
            Assert.That(summary.results, Is.Empty);
            Assert.That(summary.events, Is.Empty);
            Assert.That(source.results, Has.Count.EqualTo(1));
            Assert.That(source.events, Has.Count.EqualTo(1));
        }

        [Test]
        public void IncrementalResultReportsTheFirstRetainedSequence()
        {
            var source = new TestRunResultPayload
            {
                nextEventSequence = 12,
                events = new List<TestRunEventPayload>
                {
                    new TestRunEventPayload { sequence = 10 },
                    new TestRunEventPayload { sequence = 11 },
                    new TestRunEventPayload { sequence = 12 },
                },
            };

            Assert.That(UPilotTestService.GetEarliestEventSequence(source), Is.EqualTo(10));
            Assert.That(UPilotTestService.GetEarliestEventSequence(new TestRunResultPayload { nextEventSequence = 12 }), Is.EqualTo(13));
        }

        [Test]
        public void LeafCompletionIsDeduplicatedAndKeepsTheFirstFailure()
        {
            var source = new TestRunResultPayload();

            Assert.That(UPilotTestService.RecordLeafCompletion(source, "leaf-a", "Fixture.A", "Passed"), Is.True);
            Assert.That(UPilotTestService.RecordLeafCompletion(source, "leaf-a", "Fixture.A", "Passed"), Is.False);
            Assert.That(UPilotTestService.RecordLeafCompletion(source, "leaf-b", "Fixture.B", "Failed"), Is.True);

            Assert.That(source.completedLeafCount, Is.EqualTo(2));
            Assert.That(source.passedSoFar, Is.EqualTo(1));
            Assert.That(source.failedSoFar, Is.EqualTo(1));
            Assert.That(source.firstFailure, Is.EqualTo("Fixture.B"));
            Assert.That(source.events.Select(item => item.kind), Is.EqualTo(new[] { "leaf_completed", "leaf_completed" }));
        }

        [Test]
        public void LiveLeafTableUsesUniqueIdentityAndFinalCorrectionUpdatesTheSameLeaf()
        {
            var source = new TestRunResultPayload();

            Assert.That(UPilotTestService.RecordLeafCompletion(source, "case:one", "Fixture.Case", "Passed"), Is.True);
            Assert.That(UPilotTestService.RecordLeafCompletion(source, "case:two", "Fixture.Case", "Failed"), Is.True);
            UPilotTestService.RecordFinalLeafResult(source, new TestResultItemPayload
            {
                leafKey = "case:one", testName = "Fixture.Case", testStatus = "Failed",
                duration = 1.5f, message = "final", stackTrace = "stack",
            });
            // A duplicate final callback is possible after a Reload/recovery
            // boundary.  It must update the leaf table at most once and cannot
            // create a second correction event for the same observed result.
            UPilotTestService.RecordFinalLeafResult(source, new TestResultItemPayload
            {
                leafKey = "case:one", testName = "Fixture.Case", testStatus = "Failed",
                duration = 1.5f, message = "duplicate", stackTrace = "stack",
            });

            Assert.That(source.results, Has.Count.EqualTo(2));
            Assert.That(source.results.Single(item => item.leafKey == "case:one").testStatus, Is.EqualTo("Failed"));
            Assert.That(source.results.Single(item => item.leafKey == "case:one").message, Is.EqualTo("duplicate"));
            Assert.That(source.results.Single(item => item.leafKey == "case:one").duration, Is.EqualTo(1.5f));
            Assert.That(source.events.Select(item => item.kind), Is.EqualTo(new[] {
                "leaf_completed", "leaf_completed", "leaf_corrected",
            }));
        }

        [Test]
        public void MoreThanTenThousandLeafEventsExposeCursorGapWithoutRenumbering()
        {
            var source = new TestRunResultPayload();
            for (int index = 0; index < 10001; index++)
                Assert.That(UPilotTestService.RecordLeafCompletion(source, "leaf-" + index, "Fixture." + index, "Passed"), Is.True);

            Assert.That(source.nextEventSequence, Is.EqualTo(10001));
            Assert.That(source.eventsTruncated, Is.True);
            Assert.That(source.earliestEventSequence, Is.EqualTo(2));
            Assert.That(source.events, Has.Count.EqualTo(10000));
            Assert.That(source.events[0].sequence, Is.EqualTo(2));
            Assert.That(UPilotTestService.CreateIncrementalResult(source, 1, 1).events[0].sequence, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator NetworkObservationsStayOnDiskDuringActiveRun()
        {
            if (UPilotTestService.Instance?.IsRunning != true)
                Assert.Ignore("Requires an UPilot-started run to exercise active observation isolation.");
            bool previous = Logger.LogToUnityConsole;
            bool previousWireLogs = UPilotBridge.Instance.DebugWireLogsEnabled;
            var level = Logger.MinLevel;
            string marker = "upilot-network-isolation-" + Guid.NewGuid().ToString("N");
            var consoleMessages = new List<string>();
            UnityEngine.Application.LogCallback callback = (message, stack, type) => consoleMessages.Add(message);
            try
            {
                Logger.LogToUnityConsole = false;
                UPilotBridge.Instance.DebugWireLogsEnabled = true;
                Logger.MinLevel = Logger.LogLevel.Info;
                Logger.LogToUnityConsole = true;
                UnityEngine.Application.logMessageReceived += callback;
                for (int index = 0; index < 20; index++)
                {
                    Logger.LogNetwork("NETWORK", marker, false);
                    Logger.LogNetwork(marker, true);
                    Logger.Log("COMMAND", marker);
                    Logger.Log("NETWORK", marker);
                    yield return null;
                }
                Assert.That(consoleMessages, Has.None.Contains(marker));
                Assert.That(File.ReadAllText(Logger.LogFilePath), Does.Contain(marker));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= callback;
                Logger.LogToUnityConsole = false;
                UPilotBridge.Instance.DebugWireLogsEnabled = previousWireLogs;
                Logger.LogToUnityConsole = previous;
                Logger.MinLevel = level;
            }
        }
    }
}
