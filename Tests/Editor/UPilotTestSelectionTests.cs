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
        [Test]
        public void ConsolePhaseClosureIsFrozenAndCopyDoesNotCloseSource()
        {
            var create = typeof(UPilotTestService).GetMethod("NewConsolePhase", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var phase = (TestConsolePhasePayload)create.Invoke(null, new object[] { "cleanup" });
            var source = new TestRunResultPayload();
            source.consolePhases.Add(phase);
            var copy = source.ShallowCopyForPersistence();
            UPilotTestService.EndConsolePhase(copy.consolePhases[0]);
            Assert.That(phase.endedAt, Is.Zero);
            var closed = copy.consolePhases[0];
            Assert.That(closed.endedAt, Is.GreaterThanOrEqualTo(closed.startedAt));
            var frozen = UnityEngine.JsonUtility.ToJson(closed);
            UPilotTestService.EndConsolePhase(closed);
            Assert.That(UnityEngine.JsonUtility.ToJson(closed), Is.EqualTo(frozen));
            Assert.That(closed.logs.Count, Is.LessThanOrEqualTo(500));
        }

        [Test]
        public void ConsolePhaseChangedDomainHasGapAndNoBorrowedLogs()
        {
            var phase = new TestConsolePhasePayload { phase = "testExecution", domain = "old-domain", source = "persistentCapture", captureSessionId = "other" };
            UPilotTestService.EndConsolePhase(phase);
            Assert.That(phase.coverage, Is.EqualTo("partial"));
            Assert.That(phase.gapReason, Is.EqualTo("capture_switch_reload_or_cursor_reset"));
            Assert.That(phase.logs, Is.Empty);
        }

        public class TypeNode { public string FullName { get; set; } }
        public class Node
        {
            public string FullName { get; set; }
            public string Name { get; set; }
            public bool IsSuite { get; set; }
            public bool IsTestAssembly { get; set; }
            public bool HasChildren => Children.Any();
            public string RunState { get; set; }
            public object Method { get; set; }
            public int TestCaseCount { get; set; }
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
        public void ParameterizedCandidatesPrecedeUnrelatedNamesWithoutExpandingSelection()
        {
            var root = new Node
            {
                IsSuite = true,
                Children = Enumerable.Range(0, 20).Select(index => new Node { FullName = "A.Unrelated." + index })
                    .Concat(new[] { new Node { FullName = "Z.Fixture.Cases(1)" }, new Node { FullName = "Z.Fixture.Cases(2)" } }).ToArray(),
            };
            var result = UPilotTestService.ResolveTestSelection(root, "EditMode", "", new[] { "Z.Fixture.Cases" });
            Assert.That(result.matchedCount, Is.Zero);
            Assert.That(result.tests, Is.Empty);
            Assert.That(result.runnerStartAttempted, Is.False);
            Assert.That(result.selectionValid, Is.False);
            var candidates = result.unmatchedSelectors.Single().candidates;
            Assert.That(candidates, Has.Count.EqualTo(10));
            Assert.That(candidates.Take(2), Is.EqualTo(new[] { "Z.Fixture.Cases(1)", "Z.Fixture.Cases(2)" }));
        }

        [Test]
        public void ParameterizedCandidateHintsKeepCrossAssemblyIdentity()
        {
            var root = new Node
            {
                IsSuite = true,
                Children = new[] { "A.dll", "B.dll" }.Select(assembly => new Node
                {
                    IsSuite = true, IsTestAssembly = true, Name = assembly,
                    Children = new[] { new Node { FullName = "Z.Fixture.Cases(1)" } },
                }).ToArray(),
            };
            var result = UPilotTestService.ResolveTestSelection(root, "EditMode", "", new[] { "Z.Fixture.Cases" });
            Assert.That(result.tests, Is.Empty);
            Assert.That(result.runnerStartAttempted, Is.False);
            Assert.That(result.unmatchedSelectors.Single().candidates,
                Is.EqualTo(new[] { "A::Z.Fixture.Cases(1)", "B::Z.Fixture.Cases(1)" }));
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

        [TestCase("Demo.First.One")]
        [TestCase(@"regex:^Demo\.First\.One$")]
        public void LegacyFilterDiscoveryProducesTheExpectedRunSnapshot(string filter)
        {
            var listed = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", filter);
            var current = UPilotTestService.ResolveTestSelection(Tree(), "EditMode", filter);
            Assert.That(listed.matchedCount, Is.EqualTo(1));
            Assert.That(UPilotTestService.RunSelectionMatches(current,
                listed.selectionDomain, listed.selectionSnapshotId), Is.True);
            Assert.That(UPilotTestService.RunSelectionMatches(current,
                "old-domain", listed.selectionSnapshotId), Is.False);
            Assert.That(UPilotTestService.RunSelectionMatches(current,
                listed.selectionDomain, "old-snapshot"), Is.False);
            Assert.That(UPilotTestService.ResolveTestSelection(Tree(), "EditMode", "regex:^Missing$").matchedCount,
                Is.Zero, "A legacy zero-match filter must not become an unfiltered run.");
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
        private static Node MethodTree(string assembly = "Fixture.dll", string state = "Explicit") => new Node
        {
            IsSuite = true, IsTestAssembly = true, Name = assembly, FullName = assembly,
            Children = new[] { new Node
            {
                IsSuite = true, FullName = "Demo.Parameterized", RunState = "Runnable",
                TypeInfo = new TypeNode { FullName = "Demo.Parameterized" },
                Children = new[] { new Node
                {
                    IsSuite = true, FullName = "Demo.Parameterized.Cases", Method = new object(),
                    TestCaseCount = 2, RunState = state,
                    Children = new[]
                    {
                        new Node { FullName = "Demo.Parameterized.First", RunState = "Runnable" },
                        new Node { FullName = "Demo.Parameterized.Second", RunState = "Runnable" }
                    }
                } }
            } }
        };

        private static string[] RunnerNames(TestListResultPayload selection) =>
            ((FakeFilter)UPilotTestService.CreateSelectionFilters(selection, typeof(FakeFilter), typeof(FakeMode)).GetValue(0)).testNames;

        [TestCase("Runnable")]
        [TestCase("Explicit")]
        public void CompleteMethodUsesOneSelectorButKeepsExactPublicLeaves(string state)
        {
            var selection = UPilotTestService.ResolveTestSelection(MethodTree(state: state), "EditMode", "",
                fixtures: new[] { "Demo.Parameterized" });
            Assert.That(selection.matchedCount, Is.EqualTo(2));
            Assert.That(RunnerNames(selection), Is.EqualTo(new[] { "Demo.Parameterized.Cases" }));
            Assert.That(selection.tests, Is.EqualTo(new[] { "Demo.Parameterized.First", "Demo.Parameterized.Second" }));
            Assert.That(UPilotTestService.SelectionSnapshotMatches(selection, selection.selectionDomain, selection.selectionSnapshotId), Is.True);
            string json = UnityEngine.JsonUtility.ToJson(selection);
            Assert.That(json, Does.Not.Contain("runnerTestNames").And.Not.Contain("runnerSelectionSnapshotId"));
            var restored = UnityEngine.JsonUtility.FromJson<TestListResultPayload>(json);
            Assert.That(RunnerNames(restored), Is.EqualTo(selection.tests), "No tree evidence survives serialization.");
        }

        [TestCase("explicit_leaf")]
        [TestCase("unknown_leaf")]
        [TestCase("ignored_leaf")]
        [TestCase("missing_method")]
        [TestCase("unknown_method_state")]
        [TestCase("case_count_mismatch")]
        [TestCase("nested_children")]
        [TestCase("method_name_collision")]
        public void UnsafeMethodCompressionRetainsExactLeaves(string reason)
        {
            var root = MethodTree();
            var fixture = root.Children.Single();
            var method = fixture.Children.Single();
            var first = method.Children.First();
            switch (reason)
            {
                case "explicit_leaf": first.RunState = "Explicit"; break;
                case "unknown_leaf": first.RunState = null; break;
                case "ignored_leaf": first.RunState = "Ignored"; break;
                case "missing_method": method.Method = null; break;
                case "unknown_method_state": method.RunState = null; break;
                case "case_count_mismatch": method.TestCaseCount = 3; break;
                case "nested_children":
                    first.Children = new[] { new Node { FullName = "Demo.Parameterized.Nested", RunState = "Runnable" } }; break;
                case "method_name_collision":
                    root.Children = root.Children.Concat(new[] { new Node { IsSuite = true, FullName = method.FullName } }); break;
            }
            var selection = UPilotTestService.ResolveTestSelection(root, "EditMode", "", fixtures: new[] { "Demo.Parameterized" });
            Assert.That(selection.matchedCount, Is.EqualTo(2));
            Assert.That(RunnerNames(selection), Is.EqualTo(selection.tests));
        }

        [TestCase("partial")]
        [TestCase("union")]
        [TestCase("intersection")]
        public void CompleteMethodOptimizationPreservesSelectorSetSemantics(string mode)
        {
            var selection = mode == "partial"
                ? UPilotTestService.ResolveTestSelection(MethodTree(), "EditMode", "", new[] { "Demo.Parameterized.First" })
                : UPilotTestService.ResolveTestSelection(MethodTree(), "EditMode", "", new[] { "Demo.Parameterized.First" },
                    fixtures: new[] { "Demo.Parameterized" }, matchMode: mode);
            Assert.That(selection.matchedCount, Is.EqualTo(mode == "union" ? 2 : 1));
            Assert.That(RunnerNames(selection), Is.EqualTo(new[] { mode == "union" ? "Demo.Parameterized.Cases" : "Demo.Parameterized.First" }));
        }

        [TestCase("domain")]
        [TestCase("snapshot")]
        [TestCase("leaf")]
        public void ChangedSelectionCannotReuseDiscoveryMethodPlan(string change)
        {
            var selection = UPilotTestService.ResolveTestSelection(MethodTree(), "EditMode", "");
            Assert.That(RunnerNames(selection).Length, Is.EqualTo(1));
            if (change == "domain") selection.selectionDomain = "old-domain";
            if (change == "snapshot") selection.selectionSnapshotId = "old-snapshot";
            if (change == "leaf") selection.selectedTests.RemoveAt(1);
            Assert.That(UPilotTestService.SelectionSnapshotMatches(selection, "", ""), Is.False);
            Assert.That(RunnerNames(selection), Is.EqualTo(selection.selectedTests.Select(test => test.fullName)));
        }

        [Test]
        public void CompleteMethodsStayAssemblyIsolatedAndKeepUncoveredLeaf()
        {
            var first = MethodTree("A.dll");
            var method = first.Children.Single().Children.Single();
            first.Children.Single().Children = new[] { method, new Node { FullName = "Demo.Parameterized.Other", RunState = "Explicit" } };
            var root = new Node { IsSuite = true, Children = new[] { first, MethodTree("B.dll") } };
            var selection = UPilotTestService.ResolveTestSelection(root, "EditMode", "", fixtures: new[] { "Demo.Parameterized" });
            var filters = UPilotTestService.CreateSelectionFilters(selection, typeof(FakeFilter), typeof(FakeMode)).Cast<FakeFilter>().ToArray();
            Assert.That(selection.matchedCount, Is.EqualTo(5));
            Assert.That(filters.Length, Is.EqualTo(2));
            Assert.That(filters[0].assemblyNames, Is.EqualTo(new[] { "A" }));
            Assert.That(filters[0].testNames, Is.EqualTo(new[] { "Demo.Parameterized.Cases", "Demo.Parameterized.Other" }));
            Assert.That(filters[1].assemblyNames, Is.EqualTo(new[] { "B" }));
            Assert.That(filters[1].testNames, Is.EqualTo(new[] { "Demo.Parameterized.Cases" }));
        }

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

        [TestCase("completed", true)]
        [TestCase("failed", true)]
        [TestCase("cancelled", true)]
        [TestCase("cleaning", false)]
        public void IncrementalProjectionIsBoundedAndPreservesTerminal(string status, bool terminal)
        {
            var source = new TestRunResultPayload
            {
                status = status, terminal = terminal,
                events = new List<TestRunEventPayload> { new TestRunEventPayload { sequence = 1 } },
            };
            string small = UnityEngine.JsonUtility.ToJson(UPilotTestService.CreateIncrementalResult(source, 0, 1));
            for (int i = 0; i < 1000; i++)
                source.results.Add(new TestResultItemPayload { leafKey = "leaf-" + i, testStatus = "Passed" });
            var page = UPilotTestService.CreateIncrementalResult(source, 0, 1);
            Assert.That(UnityEngine.JsonUtility.ToJson(page), Is.EqualTo(small));
            Assert.That(page.results, Is.Empty);
            Assert.That(page.terminal, Is.EqualTo(terminal));
            Assert.That(UPilotTestService.CreateStatusSummary(source).terminal, Is.EqualTo(terminal));
            Assert.That(source.results.Count, Is.EqualTo(1000));
            var empty = UPilotTestService.CreateIncrementalResult(source, 1, 1);
            Assert.That(empty.events, Is.Empty);
            Assert.That(empty.lastDeliveredEventSequence, Is.EqualTo(1));
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

        [TestCase(1)]
        [TestCase(5001)]
        public void LargeFullResultTransferRoundTripsEveryFieldWithoutMutatingSnapshot(int leafCount)
        {
            string message = leafCount == 1 ? new string('文', 1500000) : new string('文', 250);
            var source = new TestRunResultPayload
            {
                runGuid = "transport-run", status = "completed", terminal = true, total = leafCount,
                results = Enumerable.Range(0, leafCount).Select(index => new TestResultItemPayload
                {
                    leafKey = "leaf-" + index, testName = "Fixture.Case(" + index + ")",
                    testStatus = "Failed", message = message + "😀", stackTrace = "stack\nline",
                }).ToList(),
                events = Enumerable.Range(0, leafCount).Select(index => new TestRunEventPayload
                {
                    sequence = index + 1, kind = "leaf_completed", leafKey = "leaf-" + index,
                }).ToList(),
                selectedLeafIdentities = Enumerable.Range(0, leafCount).Select(index => "leaf-" + index).ToArray(),
            };
            string before = UnityEngine.JsonUtility.ToJson(source);
            var transfer = UPilotTestService.CreateCompressedResult(source, true);
            Assert.That(transfer, Is.Not.Null);
            Assert.That(transfer.resultEncoding, Is.EqualTo("gzip+base64"));
            Assert.That(transfer.uncompressedBytes, Is.EqualTo(System.Text.Encoding.UTF8.GetByteCount(before)));
            Assert.That(transfer.uncompressedBytes, Is.GreaterThan(4 * 1024 * 1024));
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(UnityEngine.JsonUtility.ToJson(transfer)), Is.LessThan(4 * 1024 * 1024));
            using (var input = new MemoryStream(Convert.FromBase64String(transfer.compressedResult)))
            using (var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, System.Text.Encoding.UTF8))
                Assert.That(reader.ReadToEnd(), Is.EqualTo(before));
            Assert.That(UnityEngine.JsonUtility.ToJson(source), Is.EqualTo(before));
            Assert.That(UPilotTestService.CreateCompressedResult(source, false), Is.Null);
        }

        [Test]
        public void SmallResultDoesNotNeedNegotiatedCompression()
        {
            Assert.That(UPilotTestService.CreateCompressedResult(new TestRunResultPayload(), true), Is.Null);
            Assert.That(UPilotTestService.CreateCompressedResult(null, true), Is.Null);
        }

        [Test]
        public void EvictedCompletionDoesNotAcceptDuplicateLiveCallbackAfterSerialization()
        {
            var source = CreateSnapshotWithEvictedCompletion();
            Assert.That(UPilotTestService.RecordLeafCompletion(source, "leaf-a", "Fixture.A", "Passed"), Is.False);
            Assert.That(source.completedLeafCount, Is.EqualTo(1));
            Assert.That(source.failedSoFar, Is.EqualTo(1));
            Assert.That(source.passedSoFar, Is.Zero);
            Assert.That(source.firstFailure, Is.EqualTo("Fixture.A"));
            Assert.That(source.results.Single().testStatus, Is.EqualTo("Failed"));
            Assert.That(source.nextEventSequence, Is.EqualTo(10001));
        }

        [Test]
        public void EvictedCompletionFinalReconciliationOnlyUpdatesResultMetadata()
        {
            var source = CreateSnapshotWithEvictedCompletion();
            UPilotTestService.RecordFinalLeafResult(source, new TestResultItemPayload
            {
                leafKey = "leaf-a", testName = "Fixture.A", testStatus = "Failed",
                duration = 2f, message = "final failure", stackTrace = "final stack",
            });
            Assert.That(source.nextEventSequence, Is.EqualTo(10001));
            Assert.That(source.earliestEventSequence, Is.EqualTo(2));
            Assert.That(source.events, Has.Count.EqualTo(10000));
            Assert.That(source.results, Has.Count.EqualTo(1));
            Assert.That(source.results[0].message, Is.EqualTo("final failure"));
            Assert.That(source.results[0].stackTrace, Is.EqualTo("final stack"));
            Assert.That(source.results[0].duration, Is.EqualTo(2f));
        }

        [Test]
        public void EvictedCompletionCorrectionIsNotRepeatedAfterCorrectionAlsoEvicts()
        {
            var source = CreateSnapshotWithEvictedCompletion();
            var final = new TestResultItemPayload
            {
                leafKey = "leaf-a", testName = "Fixture.A", testStatus = "Passed", message = "corrected",
            };
            UPilotTestService.RecordFinalLeafResult(source, final);
            Assert.That(source.events.Last().kind, Is.EqualTo("leaf_corrected"));
            Assert.That(source.nextEventSequence, Is.EqualTo(10002));
            source = EvictEventsAndRoundTrip(source);
            long previousSequence = source.nextEventSequence;
            UPilotTestService.RecordFinalLeafResult(source, new TestResultItemPayload
            {
                leafKey = "leaf-a", testName = "Fixture.A", testStatus = "Passed", message = "latest metadata",
            });
            Assert.That(source.nextEventSequence, Is.EqualTo(previousSequence));
            Assert.That(source.results, Has.Count.EqualTo(1));
            Assert.That(source.results[0].testStatus, Is.EqualTo("Passed"));
            Assert.That(source.results[0].message, Is.EqualTo("latest metadata"));
        }

        [Test]
        public void FinalResultWithoutLiveCompletionDoesNotTreatStartedEventAsCompletion()
        {
            var source = new TestRunResultPayload
            {
                nextEventSequence = 1,
                events = new List<TestRunEventPayload>
                {
                    new TestRunEventPayload { sequence = 1, kind = "test_started", leafKey = "leaf-a", testName = "Fixture.A" },
                },
            };
            UPilotTestService.RecordFinalLeafResult(source, new TestResultItemPayload
            {
                leafKey = "leaf-a", testName = "Fixture.A", testStatus = "Passed",
            });
            Assert.That(source.events.Last().kind, Is.EqualTo("leaf_completed"));
            Assert.That(source.results, Has.Count.EqualTo(1));
        }

        private static TestRunResultPayload CreateSnapshotWithEvictedCompletion()
        {
            var source = new TestRunResultPayload();
            Assert.That(UPilotTestService.RecordLeafCompletion(source, "leaf-a", "Fixture.A", "Failed"), Is.True);
            return EvictEventsAndRoundTrip(source);
        }

        private static TestRunResultPayload EvictEventsAndRoundTrip(TestRunResultPayload source)
        {
            // Model a full retained window of starts for other leaves; completed
            // results survive both event eviction and persistence/Domain Reload.
            long after = source.nextEventSequence;
            source.events = Enumerable.Range(1, 10000).Select(index => new TestRunEventPayload
            {
                sequence = after + index, kind = "test_started", leafKey = "other-" + index,
            }).ToList();
            source.nextEventSequence = after + 10000;
            source.earliestEventSequence = after + 1;
            source.eventsTruncated = true;
            return UnityEngine.JsonUtility.FromJson<TestRunResultPayload>(UnityEngine.JsonUtility.ToJson(source));
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
